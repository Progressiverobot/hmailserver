// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"

#include "DKIM.h"
#include "DKIMParameters.h"

#include "../AntiSpamDiagnostics.h"
#include "../../Util/Hashing/HashCreator.h"
#include "../../Util/Encoding/Base64.h"
#include "../../BO/Message.h"
#include "../../MIME/MimeCode.h"
#include "../../MIME/Mime.h"
#include "../../TCPIP/DNSResolver.h"
#include "../../Util/TraceHeaderWriter.h"
#include "../../Util/FileUtilities.h"
#include "../../Persistence/PersistentMessage.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // The number of signatures we are prepared to verify in one message. Each
      // one costs a DNS lookup and a hash of the whole body, so it is bounded;
      // GetSignatureFields reports back when a message had more than this, and
      // the verdict is softened accordingly (see DKIM::Verify).
      const size_t MaxSignaturesToVerify = 10;

      // Ranks a single signature's outcome for the message-level verdict.
      //
      // A message is verified if any one signature verifies (RFC 6376 6.1). When
      // none does, the strongest thing we learned has to survive: a signature we
      // could not even parse must not overwrite the *failure* of one we could.
      // Before this existed, the last signature in the header simply won, so
      // appending one deliberately unparseable DKIM-Signature (v=2 is enough)
      // after a forged one turned the verdict from PermFail into Neutral and the
      // DKIM failure score - and with it the rejection - disappeared.
      //
      // PermFail outranks TempFail on purpose, and not only for tidiness: a
      // lookup for a selector that does not exist is a PermFail, but one for a
      // domain whose name servers are unreachable is a TempFail, so if TempFail
      // won the same trick would work with an unresolvable d= instead.
      int RankSignatureResult(DKIM::Result result)
      {
         switch (result)
         {
         case DKIM::Pass:
            return 4;
         case DKIM::PermFail:
            return 3;
         case DKIM::TempFail:
            return 2;
         default:
            return 1; // Neutral: this signature tells us nothing.
         }
      }

      // A bound on what an administrator can push into h=. The tag is emitted on a
      // single unfolded line and RFC 5322 2.1.1 caps a header line at 998 octets; the
      // 30 recommended fields can already account for around 350 of them.
      const int MaxOversignHeadersLength = 256;

      // RFC 5322 3.6.8 ftext: printable US-ASCII with the colon excluded. The colon is
      // also h='s separator, so a name containing one would silently become two
      // entries, and one containing ';' would end the tag early and corrupt every tag
      // after it.
      bool IsValidHeaderFieldName(const AnsiString &name)
      {
         if (name.IsEmpty())
            return false;

         for (char c : name)
         {
            if (c < 33 || c > 126 || c == ':')
               return false;
         }

         return true;
      }

      bool ContainsFieldNoCase(const std::vector<AnsiString> &fields, const AnsiString &name)
      {
         for (const AnsiString &field : fields)
         {
            if (field.CompareNoCase(name) == 0)
               return true;
         }

         return false;
      }

      // h= is a colon-separated list (RFC 6376 3.5).
      AnsiString JoinFieldList(const std::vector<AnsiString> &fields)
      {
         AnsiString result;

         for (const AnsiString &field : fields)
         {
            if (field.IsEmpty())
               continue;

            if (!result.IsEmpty())
               result += ":";

            result += field;
         }

         return result;
      }
   }

   std::vector<AnsiString> DKIM::recommendedHeaderFields_;
   std::vector<AnsiString> DKIM::oversignHeaderFields_;

   DKIM::DKIM()
   {

   }

   void 
   DKIM::Initialize()
   {
      // OpenSSL 1.1.0+ initializes itself automatically; the legacy
      // ERR_load_EVP_strings() call is deprecated and no longer needed.

      // Rebuilt from scratch rather than appended to. This is reached from
      // SpamProtection::Load, which StartServers calls - and Application::Reinitialize
      // calls StartServers again, so the list gained another 30 entries on every
      // settings reload and grew without bound for the life of the process.
      //
      // It has been survivable only by accident: both canonicalizers consume a matched
      // field from their pool and append to h= only when they found something, so the
      // repeats matched nothing and were dropped. That accident stops holding the moment
      // an entry is *meant* to repeat, which is what RFC 6376 5.4 oversigning does.
      recommendedHeaderFields_.clear();
      oversignHeaderFields_.clear();

      recommendedHeaderFields_.push_back("From");
      recommendedHeaderFields_.push_back("Sender");
      recommendedHeaderFields_.push_back("Reply-To");
      recommendedHeaderFields_.push_back("Subject");
      recommendedHeaderFields_.push_back("Date");
      recommendedHeaderFields_.push_back("Message-ID");
      recommendedHeaderFields_.push_back("To");
      recommendedHeaderFields_.push_back("CC");
      recommendedHeaderFields_.push_back("MIME-Version");

      recommendedHeaderFields_.push_back("Content-Type");
      recommendedHeaderFields_.push_back("Content-Transfer-Encoding");
      recommendedHeaderFields_.push_back("Content-ID");
      recommendedHeaderFields_.push_back("Content-Description");

      recommendedHeaderFields_.push_back("Resent-Date");
      recommendedHeaderFields_.push_back("Resent-From");
      recommendedHeaderFields_.push_back("Resent-Sender");
      recommendedHeaderFields_.push_back("Resent-To");
      recommendedHeaderFields_.push_back("Resent-Cc");
      recommendedHeaderFields_.push_back("Resent-Message-ID");

      recommendedHeaderFields_.push_back("In-Reply-To");
      recommendedHeaderFields_.push_back("References");

      recommendedHeaderFields_.push_back("List-Id");
      recommendedHeaderFields_.push_back("List-Help");
      recommendedHeaderFields_.push_back("List-Unsubscribe");
      recommendedHeaderFields_.push_back("List-Unsubscribe-Post");
      recommendedHeaderFields_.push_back("List-Subscribe");
      recommendedHeaderFields_.push_back("List-Post");
      recommendedHeaderFields_.push_back("List-Owner");
      recommendedHeaderFields_.push_back("List-Archive");

      // Addition for CSA-Compliant Mail Headers
      recommendedHeaderFields_.push_back("X-CSA-Complaints");

      InitializeOversigning_();
   }

   void
   DKIM::InitializeOversigning_()
   {
      String configured = IniFileSettings::Instance()->GetDkimOversignHeaders();

      if (configured.IsEmpty())
         return;

      if (configured.GetLength() > MaxOversignHeadersLength)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6122, "DKIM::InitializeOversigning_",
            Formatter::Format("DkimOversignHeaders is longer than {0} characters and was ignored. h= is emitted on one unfolded line, which RFC 5322 caps at 998 octets, and the fields signed by default already account for a large part of that.", MaxOversignHeadersLength));
         return;
      }

      // Colon is the h= separator; comma and semicolon are accepted because they are
      // what an administrator is likely to type.
      AnsiString flattened = configured;
      flattened.Replace(",", ":");
      flattened.Replace(";", ":");

      for (AnsiString name : StringParser::SplitString(flattened, ":"))
      {
         name = name.Trim();

         if (name.IsEmpty())
            continue;

         if (!IsValidHeaderFieldName(name))
         {
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6123, "DKIM::InitializeOversigning_",
               Formatter::Format("'{0}' is not a valid header field name and was dropped from DkimOversignHeaders. Names must be printable US-ASCII with no colon.", String(name)));
            continue;
         }

         if (ContainsFieldNoCase(oversignHeaderFields_, name))
            continue;

         oversignHeaderFields_.push_back(name);

         // Oversigning a field means "sign it, and forbid a second one". A name that is
         // not signed in the first place would appear in h= exactly once, which is an
         // ordinary signature over it and no protection at all - a silent no-op, and the
         // dangerous kind, because the administrator asked for protection and the
         // configuration looks like it was accepted.
         if (!ContainsFieldNoCase(recommendedHeaderFields_, name))
            recommendedHeaderFields_.push_back(name);
      }

      // From is the field oversigning exists for: a prepended second From: is what every
      // mail client shows while a bottom-up verifier still passes on the one underneath.
      // It is always present and always already signed, and no legitimate intermediary
      // adds a second, so including it carries no interop cost. Oversigning anything at
      // all but not From is a mistake, and one nothing would report.
      if (!oversignHeaderFields_.empty() && !ContainsFieldNoCase(oversignHeaderFields_, "From"))
         oversignHeaderFields_.push_back("From");
   }

   // helper.
   EVP_PKEY* 
   _GetPublicKey(const AnsiString &keyData)
   {
      // base64 decode the public key.
      AnsiString publicKeyData = Base64::Decode(keyData, keyData.GetLength());
      const unsigned char * publicKeyDataPointer = (const unsigned char*) publicKeyData.GetBuffer();

      EVP_PKEY *publicKey = d2i_PUBKEY(NULL, &publicKeyDataPointer, publicKeyData.GetLength());

      return publicKey;
   }

   bool
   DKIM::Sign(std::shared_ptr<Message> message,
              const AnsiString &header,
              const AnsiString &domain,
              const AnsiString &selector,
              const String &privateKey,
              HashCreator::HashType algorithm,
              Canonicalization::CanonicalizeMethod headerMethod,
              Canonicalization::CanonicalizeMethod bodyMethod)
   {

      std::shared_ptr<Canonicalization> bodyCanonicalization = CreateCanonicalization_(bodyMethod);
      std::shared_ptr<Canonicalization> headerCanonicalization = CreateCanonicalization_(headerMethod);

      if (!bodyCanonicalization || !headerCanonicalization)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5307, "DKIM::Sign", "Creation of canonicalization method failed.");
         return false;
      }

      const String fileName = PersistentMessage::GetFileName(message);

      if (FileUtilities::FileSize(fileName) > MaxFileSize)
      {
         LOG_DEBUG("Message was not signed using DKIM since the size of the message exceeded the max DKIM size of 50MB.");
         return true;
      }

      MimeHeader mimeHeader;
      mimeHeader.Load(header.c_str(), header.GetLength(), false);

      if (HasSignatureForDomain_(mimeHeader, domain))
      {
         LOG_DEBUG("Skipping DKIM signing: message already carries a DKIM-Signature for domain " + String(domain));
         return true;
      }

      String messageBody = bodyCanonicalization->CanonicalizeBody(PersistentMessage::LoadBody(fileName));

      HashCreator shaer(algorithm);
      String bodyHash = shaer.GenerateHashNoSalt(messageBody, HashCreator::base64);

      std::pair<AnsiString, AnsiString> dummySignatureField;

      AnsiString fieldList;
      AnsiString canonicalizedHeader = headerCanonicalization->CanonicalizeHeader(header, dummySignatureField, recommendedHeaderFields_, fieldList);

      if (!oversignHeaderFields_.empty())
      {
         // Oversigning has to go through the canonicalizer a second time rather than
         // just appending names to h=, and the reason is the one case where the two
         // differ: a message that GENUINELY carries two of an oversigned name.
         //
         // A verifier reads h= left to right and, for each entry, takes the bottom-most
         // instance it has not used yet. So given h="...:from", it consumes the second
         // From and hashes it. Pass one above consumed only the first, so appending the
         // name to h= without re-canonicalizing would have us hashing one From where
         // every verifier hashes two - a signature that fails everywhere and nowhere
         // locally.
         //
         // Re-running with the final list makes the signer do exactly what the verifier
         // will do. For the ordinary message - at most one of each name - the extra
         // entries match nothing, contribute the null string, and the header hash is
         // byte-identical to what pass one produced.
         //
         // fieldList carries the fields actually found, in the order they were taken,
         // and is what h= would have been; the oversigned names are appended once each,
         // which is all RFC 6376 5.4.2 needs. One more listing than the message has
         // instances already forbids adding any further one.
         std::vector<AnsiString> finalFields;

         for (AnsiString found : StringParser::SplitString(fieldList, ":"))
         {
            if (!found.IsEmpty())
               finalFields.push_back(found);
         }

         for (const AnsiString &oversigned : oversignHeaderFields_)
            finalFields.push_back(oversigned);

         AnsiString reCanonicalizedFieldList;
         canonicalizedHeader = headerCanonicalization->CanonicalizeHeader(header, dummySignatureField, finalFields, reCanonicalizedFieldList);

         fieldList = JoinFieldList(finalFields);
      }
   
      AnsiString privateKeyContent = FileUtilities::ReadCompleteTextFile(String(privateKey));

      bool isEd25519Key = IsEd25519PrivateKey_(privateKeyContent);

      String tagV = "1";
      String tagA = isEd25519Key ? "ed25519-sha256" : (algorithm == HashCreator::SHA1 ? "rsa-sha1" : "rsa-sha256");
      String tagC = headerMethod == Canonicalization::Simple ? "simple/" : "relaxed/";
      tagC.append(bodyMethod == Canonicalization::Simple ? _T("simple") : _T("relaxed"));
      String tagQ = "dns/txt";

      String tagDomain = domain;
      String tagSelector = selector;

      // RFC 6376 3.5: t= is when this signature was made and x= when it stops being one
      // a verifier should honour, both in seconds since the UNIX epoch, UTC. time()
      // gives that number directly.
      //
      // t= goes on every signature - the RFC lists it as RECOMMENDED, no verifier
      // decides pass or fail on it, and an x= with no t= beside it is much harder to
      // reason about when a receiver complains. x= appears only when an administrator
      // has asked for a validity window, because an expiry is a promise about our own
      // outbound mail that cannot be taken back once sent: a window shorter than the
      // delay a legitimate retry, greylist or mailing list introduces silently costs
      // the message its DKIM pass and its DMARC alignment at the far end.
      const __int64 signingTime = static_cast<__int64>(time(nullptr));
      const String timestampTags = BuildTimestampTags_(signingTime,
         IniFileSettings::Instance()->GetDKIMSignatureValiditySeconds());

      String headerValue = BuildSignatureHeader_(tagA, tagDomain, tagSelector, tagC, tagQ, timestampTags, fieldList, bodyHash, "");
      
      // The header name must be hashed exactly as it is written to the message:
      // "simple" header canonicalization preserves the case, so signing over
      // "dkim-signature" while emitting "DKIM-Signature" produced a signature
      // that could not verify. (Fix from upstream hMailServer, PR #530.)
      canonicalizedHeader += headerCanonicalization->CanonicalizeHeaderLine("DKIM-Signature", headerValue);

      AnsiString signatureString = SignHash_(privateKeyContent, canonicalizedHeader, algorithm);
      if (signatureString == "")
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5308, "DKIM::Sign", "Failed to create signature.");
         return false;
      }
      
      headerValue = BuildSignatureHeader_(tagA, tagDomain, tagSelector, tagC, tagQ, timestampTags, fieldList, bodyHash, signatureString);

      // output to file.
      std::vector<std::pair<AnsiString, AnsiString> > fieldsToWrite;
      fieldsToWrite.push_back(std::make_pair("DKIM-Signature", headerValue));

      TraceHeaderWriter writer;
      bool result = writer.Write(fileName, message, fieldsToWrite);

    
      //   debugging code.
      /*
         bool immediateVerification = false;
         if (immediateVerification)
         {
            if (!Verify(message))
            {
               assert(0);
            }
         }
      */

      return result;

   }

   bool
   DKIM::IsEd25519PrivateKey_(AnsiString &privateKey)
   {
      BIO *private_bio = BIO_new_mem_buf(privateKey.GetBuffer(), -1);
      if (private_bio == NULL)
         return false;

      EVP_PKEY *private_key = PEM_read_bio_PrivateKey(private_bio, NULL, NULL, NULL);
      BIO_free(private_bio);

      if (private_key == NULL)
         return false;

      bool isEd25519 = EVP_PKEY_base_id(private_key) == EVP_PKEY_ED25519;

      EVP_PKEY_free(private_key);

      return isEd25519;
   }

   AnsiString 
   DKIM::SignHash_(AnsiString &privateKey, AnsiString &canonicalizedHeader, HashCreator::HashType hashType)
   {
      // Sign the hash.
      BIO *private_bio = BIO_new_mem_buf(privateKey.GetBuffer(), -1);
      if(private_bio == NULL) 
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5309, "DKIM::SignHash_", "Unable to read the private key file into memory.");
         return "";
      }

      EVP_PKEY *private_key = PEM_read_bio_PrivateKey(private_bio, NULL, NULL, NULL);
      if(private_key == NULL) 
      {
         BIO_free(private_bio);
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5310, "DKIM::SignHash_", "Unable to parse the private key file.");
         return "";
      }
      BIO_free(private_bio);

      if (EVP_PKEY_base_id(private_key) == EVP_PKEY_ED25519)
      {
         // Ed25519 signing (RFC 8463). Ed25519 is a one-shot algorithm and
         // must be used through the EVP_DigestSign interface.
         String result;

         EVP_MD_CTX *signingContext = EVP_MD_CTX_create();

         if (EVP_DigestSignInit(signingContext, NULL, NULL, NULL, private_key) == 1)
         {
            size_t signatureLength = 0;

            if (EVP_DigestSign(signingContext, NULL, &signatureLength,
                               (const unsigned char*) canonicalizedHeader.GetBuffer(), canonicalizedHeader.GetLength()) == 1 &&
                signatureLength > 0)
            {
               std::vector<unsigned char> signature(signatureLength);

               if (EVP_DigestSign(signingContext, signature.data(), &signatureLength,
                                  (const unsigned char*) canonicalizedHeader.GetBuffer(), canonicalizedHeader.GetLength()) == 1)
               {
                  result = Base64::Encode((const char*) signature.data(), (int) signatureLength);
               }
            }
         }

         if (result.GetLength() == 0)
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5313, "DKIM::SignHash_", "Ed25519 signing operation failed.");

         EVP_MD_CTX_destroy(signingContext);
         EVP_PKEY_free(private_key);

         return result;
      }

      unsigned int siglen = EVP_PKEY_size(private_key);
      unsigned char *sig = (unsigned char*) OPENSSL_malloc(siglen);
      
	  EVP_MD_CTX* headerSigningContext = EVP_MD_CTX_create();
      EVP_SignInit( headerSigningContext, hashType == HashCreator::SHA256 ? EVP_sha256() : EVP_sha1());
      
      String result;

      if (EVP_SignUpdate( headerSigningContext, canonicalizedHeader.GetBuffer(), canonicalizedHeader.GetLength() ) == 1)
      {
         if (EVP_SignFinal( headerSigningContext, sig, &siglen, private_key) == 1)
         {
            result = Base64::Encode((const char*) sig, siglen);
         }
         else
         {
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5311, "DKIM::SignHash_", "Call to EVP_SignFinal failed.");
         }
      }
      else
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5312, "DKIM::SignHash_", "Call to EVP_SignUpdate failed.");
      }

      EVP_PKEY_free(private_key);
	  EVP_MD_CTX_destroy(headerSigningContext);
      OPENSSL_free(sig);

      return result;
   }

   /*
      Returns one of the following
      Neutral - Undecided
      Pass - Signature verified properly.
      TempFail - Failed to verify signature, potentially a local problem.
      PermFail - Failed to verify signature.

   */
   DKIM::Result
   DKIM::Verify(const String &fileName)
   {
      std::vector<AnsiString> passingDomains;
      return Verify(fileName, passingDomains);
   }

   DKIM::Result
   DKIM::Verify(const String &fileName, std::vector<AnsiString> &passingDomains)
   {
      if (FileUtilities::FileSize(fileName) > MaxFileSize)
         return Neutral;
      
      AnsiString messageHeader = PersistentMessage::LoadHeader(fileName);
      MimeHeader mimeHeader;
      mimeHeader.Load(messageHeader.GetBuffer(), messageHeader.GetLength(), false);

      bool moreSignaturesThanWeVerify = false;
      std::vector<std::pair<AnsiString, AnsiString> > signatureFields = GetSignatureFields(mimeHeader, &moreSignaturesThanWeVerify);

      if (signatureFields.size() == 0)
      {
         // No signatures in message.
         return Neutral;
      }

      Result result = Neutral;

      typedef std::pair<AnsiString, AnsiString> HeaderField;
      for (HeaderField signatureField : signatureFields)
      {
         AnsiString signingDomain;
         Result signatureResult = VerifySignature_(fileName, messageHeader, signatureField, signingDomain);

         if (signatureResult == Pass && !signingDomain.IsEmpty())
         {
            // Every passing d= is collected, not just the first: DMARC needs all
            // of them, because alignment may hold for one and not another.
            passingDomains.push_back(signingDomain);
         }

         if (RankSignatureResult(signatureResult) > RankSignatureResult(result))
            result = signatureResult;
      };

      if (moreSignaturesThanWeVerify && result == PermFail)
      {
         // We stopped before the end of the list, so "this message failed DKIM"
         // is not a claim we are entitled to make - the signature that verifies
         // may be one of the ones we never looked at. Report no assertion, which
         // scores nothing, rather than a failure. Otherwise anyone could get a
         // legitimately signed message scored as a DKIM failure by prepending a
         // handful of junk signatures to it.
         LOG_DEBUG("DKIM: The message carries more signatures than are verified. Reporting no assertion rather than a failure.");
         result = Neutral;
      }

      return result;


   }

   DKIM::Result 
   DKIM::VerifySignature_(const String &fileName, const AnsiString &messageHeader, std::pair<AnsiString, AnsiString> signatureField, AnsiString &signingDomain)
   {
      AnsiString headerValue = signatureField.second;

      // Unfold the value before trying to parse it. Otherwise it will contain
      // \t, \r\n which DKIMParameters doesn't take into account.
      MimeField::UnfoldField(headerValue);

      DKIMParameters signatureParams;
      signatureParams.Load (headerValue);

      signingDomain = signatureParams.GetValue("d");
      signingDomain.MakeLower();

      if (!ValidateHeaderContents_(signatureParams))
      {
         // Skip this header.
         return Neutral;
      }

      // RFC 6376 3.5: "Signatures MAY be considered invalid if the verification time at
      // the verifier is past the expiration date." x= was read by nothing here before,
      // so a captured signed message replayed forever and a signer who deliberately
      // short-lived its signatures got no benefit from doing so.
      //
      // PermFail rather than Neutral, because x= sits inside the bytes b= covers: an
      // expired signature is the SIGNER's own instruction that it should no longer be
      // honoured, not something a third party can inject. RFC 6376 6.1.1 names
      // PERMFAIL (signature expired) as the outcome for exactly this.
      if (IniFileSettings::Instance()->GetDKIMEnforceSignatureExpiry())
      {
         const __int64 verificationTime = static_cast<__int64>(time(nullptr));

         if (IsSignatureExpired_(signatureParams.GetValue("x"), verificationTime,
                                 IniFileSettings::Instance()->GetDKIMExpiryClockSkewSeconds()))
         {
            LOG_DEBUG("DKIM: Signature for domain " + signingDomain + " has passed its x= expiry.");
            return PermFail;
         }
      }

      std::shared_ptr<Canonicalization> headerCanonicalization;
      std::shared_ptr<Canonicalization> bodyCanonicalization;

      AnsiString method = signatureParams.GetValue("c");
      AnsiString headerMethod;
      AnsiString bodyMethod;

      if (method == "")
      {
         headerMethod = "simple";
         bodyMethod = "simple";
      }
      else
      {
         if (method.Find("/") > 0)
         {
            std::vector<AnsiString> vec = StringParser::SplitString(method, "/");    

            headerMethod = vec[0];
            bodyMethod = vec[1];
         }
         else
         {
            headerMethod = method;
            bodyMethod = "simple";
         }
      }

      if (headerMethod == "simple")
         headerCanonicalization = std::shared_ptr<SimpleCanonicalization>(new SimpleCanonicalization) ;
      else
         headerCanonicalization = std::shared_ptr<RelaxedCanonicalization>(new RelaxedCanonicalization) ;

      if (bodyMethod == "simple")
         bodyCanonicalization = std::shared_ptr<SimpleCanonicalization>(new SimpleCanonicalization) ;
      else
         bodyCanonicalization = std::shared_ptr<RelaxedCanonicalization>(new RelaxedCanonicalization) ;

      AnsiString publicKeyString;
      AnsiString flags;
      Result res = RetrievePublicKey_(signatureParams, publicKeyString, flags);
      if (res != Pass)
      {
         LOG_DEBUG("DKIM: Retrieval of public key failed.");
         return res;
      }

      bool testMode = flags.Find("y") >= 0;

      if (testMode)
      {
         LOG_DEBUG("DKIM: Domain is in test mode. Results of this signature test won't have any effect.");
      }

      if (!ValidateBodyHash_(fileName, signatureParams, bodyCanonicalization))
      {
         LOG_DEBUG("DKIM: Validation of body hash failed.");

         // RFC 6376 3.6.1: t=y means the verifier must not treat a failure as a
         // failure - it does NOT mean the signature verified. Returning Pass here
         // granted DMARC alignment to mail whose signature was invalid, so a
         // domain that left the test flag in its key record could be spoofed.
         return testMode ? Neutral : PermFail;
      }

      AnsiString tagH = signatureParams.GetValue("h");
      AnsiString tagA = signatureParams.GetValue("a");

      std::vector<AnsiString> headerFields = StringParser::SplitString(tagH,":");

      AnsiString fieldList;
      AnsiString canonicalizedHeader = headerCanonicalization->CanonicalizeHeader(messageHeader, signatureField, headerFields, fieldList);

      /*
         body-hash = hash-alg(canon_body)
         header-hash = hash-alg(canon_header || DKIM-SIG)
         signature = sig-alg(header-hash, key)
      */

      // The header hash is not computed here: VerifyHeaderHash_ digests the
      // canonicalized header itself, inside the EVP verify context. A hash was
      // being taken over the whole header and then dropped on the floor - and
      // with SHA1 whenever a= was ed25519-sha256, which shows how unused it was.
      AnsiString tagB = signatureParams.GetValue("b");


      Result result = VerifyHeaderHash_(canonicalizedHeader, tagA, tagB, publicKeyString);

      // In test mode a failure is downgraded to "no assertion" rather than being
      // reported as a successful verification (see the body-hash case above).
      if (testMode && result != Pass)
         return Neutral;

      return result;
   }

   DKIM::Result
   DKIM::VerifyHeaderHash_(AnsiString canonicalizedHeader, const AnsiString &tagA, AnsiString &tagB, const AnsiString &publicKeyString)
   {
      Result result = PermFail;

      if (tagA == "ed25519-sha256")
      {
         // Ed25519 verification (RFC 8463). The p= tag contains the raw
         // 32 byte public key (not a SubjectPublicKeyInfo structure).
         AnsiString rawPublicKey = Base64::Decode(publicKeyString, publicKeyString.GetLength());

         if (rawPublicKey.GetLength() != 32)
         {
            LOG_DEBUG("DKIM: Invalid ed25519 public key length in DNS record.");
            return result;
         }

         EVP_PKEY *publicKey = EVP_PKEY_new_raw_public_key(EVP_PKEY_ED25519, NULL,
            (const unsigned char*) rawPublicKey.GetBuffer(), 32);

         if (!publicKey)
         {
            LOG_DEBUG("DKIM: Unable to load ed25519 public key found in DNS record.");
            return result;
         }

         MimeCodeBase64 encoder;
         encoder.SetInput(tagB.GetBuffer(), tagB.GetLength(), false);

         AnsiString signature;
         encoder.GetOutput(signature);

         EVP_MD_CTX *verificationContext = EVP_MD_CTX_create();

         if (EVP_DigestVerifyInit(verificationContext, NULL, NULL, NULL, publicKey) == 1 &&
             EVP_DigestVerify(verificationContext,
                              (const unsigned char*) signature.GetBuffer(), signature.GetLength(),
                              (const unsigned char*) canonicalizedHeader.GetBuffer(), canonicalizedHeader.GetLength()) == 1)
         {
            LOG_DEBUG("DKIM: Message passed validation (ed25519).");
            result = Pass;
         }
         else
         {
            LOG_DEBUG("DKIM: Header verification failed (ed25519).");
         }

         EVP_MD_CTX_destroy(verificationContext);
         EVP_PKEY_free(publicKey);

         return result;
      }

      // base64 decode the public key.
      EVP_PKEY *publicKey = _GetPublicKey(publicKeyString);
      if (!publicKey)
      {
         // unable to extract public key from record. broken?
         LOG_DEBUG("DKIM: Unable to base64 decode public key found in DNS record. Key: " + publicKeyString);
         return result;
      }

	  EVP_MD_CTX* hdr__ctx = EVP_MD_CTX_create();
      EVP_MD_CTX_init( hdr__ctx );

      if (tagA == "rsa-sha256")
         EVP_VerifyInit( hdr__ctx, EVP_sha256() );
      else
         EVP_VerifyInit( hdr__ctx, EVP_sha1() );

      if (EVP_VerifyUpdate( hdr__ctx, canonicalizedHeader.GetBuffer(), canonicalizedHeader.GetLength() ) == 1)
      {
         // base64 decode the signature. we're working with binary
         // data here so we can't store it in a normal string. 
         MimeCodeBase64 encoder;
         encoder.SetInput(tagB.GetBuffer(), tagB.GetLength(), false);
         
         AnsiString signature;
         encoder.GetOutput(signature);

         if (EVP_VerifyFinal( hdr__ctx, (unsigned char *) signature.GetBuffer(), signature.GetLength(), publicKey) == 1)
         {
            LOG_DEBUG("DKIM: Message passed validation.");
            result = Pass;
         }
         else
         {
            LOG_DEBUG("DKIM: Header verification failed.");
         }
      }

      EVP_MD_CTX_destroy( hdr__ctx );
      EVP_PKEY_free(publicKey);

      return result;
   }

   bool 
   DKIM::ValidateBodyHash_(const String &fileName, const DKIMParameters &signatureParams, std::shared_ptr<Canonicalization> canonicalization)
   {
      AnsiString tagA = signatureParams.GetValue("a");
      AnsiString tagBH = signatureParams.GetValue("bh");
      
      // Whitespace is ignored in this value and MUST be ignored when reassembling the original signature. 
      tagBH.Replace(" ", "");

      AnsiString messageBody = canonicalization->CanonicalizeBody(PersistentMessage::LoadBody(fileName));

      AnsiString tagBodyLengthCount = signatureParams.GetValue("l");
      if (!tagBodyLengthCount.IsEmpty())
      {
         if (!StringParser::IsNumeric(tagBodyLengthCount))
            return false;

         int trimmedBodyLength = atoi(tagBodyLengthCount);
         if (trimmedBodyLength > messageBody.GetLength())
            return false;

         messageBody = messageBody.Mid(0, trimmedBodyLength);
      }
      
      HashCreator shaer (tagA == "rsa-sha1" ? HashCreator::SHA1 : HashCreator::SHA256);
      AnsiString bodyHash = shaer.GenerateHashNoSalt(messageBody, HashCreator::base64);

      if (tagBH.IsEmpty() || tagBH.Compare(bodyHash) != 0)
         return false;

      return true;

   }

   bool
   DKIM::ValidateHeaderContents_(const DKIMParameters &signatureParams)
   {
      AnsiString tagH = signatureParams.GetValue("h");
      /*
         Verifiers MUST ignore DKIM-Signature header fields with a "v=" tag
         that is inconsistent with this specification and return PERMFAIL
         (incompatible version).
      */

      AnsiString tagV = signatureParams.GetValue("v");
      if (tagV != "1")
      {
         LOG_DEBUG("DKIM: Header in message incomplete. Unsupported version. Aborting DKIM test.");
         return false;
      }

      AnsiString tagQ = signatureParams.GetValue("q");
      if (tagQ != "" && tagQ != "dns/txt")
      {
         LOG_DEBUG("DKIM: Header in message incomplete. Unsupported query method. Aborting DKIM test.");
         return false; // unsupported method.
      }

      /*
         If any tag listed as "required" in Section 3.5 is omitted from the
         DKIM-Signature header field, the verifier MUST ignore the DKIM-
         Signature header field and return PERMFAIL (signature missing
         required tag).
      */

      AnsiString tagA = signatureParams.GetValue("a");

      if (tagA.IsEmpty()) return false;
      if (signatureParams.GetValue("b").IsEmpty()) return false;
      if (signatureParams.GetValue("bh").IsEmpty()) return false;
      if (signatureParams.GetValue("d").IsEmpty()) return false;
      if (tagH.IsEmpty()) return false;

      if (tagA != "rsa-sha1" && tagA != "rsa-sha256" && tagA != "ed25519-sha256")
      {
         LOG_DEBUG("DKIM: Header in message incomplete. Unsupported algorithm. Aborting DKIM test.");
         return false;
      }

      /*
         Verifiers MUST confirm that the domain specified in the "d=" tag is
         the same as or a parent domain of the domain part of the "i=" tag.
         If not, the DKIM-Signature header field MUST be ignored and the
         verifier should return PERMFAIL (domain mismatch).
      */

      AnsiString tagD = signatureParams.GetValue("d");
      AnsiString tagI = signatureParams.GetValue("i");

      if (!tagI.IsEmpty())
      {
         AnsiString tagIDomain = StringParser::ExtractDomain(tagI);
         if (tagIDomain.CompareNoCase(tagD) != 0 && !tagIDomain.EndsWith("." + tagD))
         {
            String sMessage;
            sMessage.Format(_T("DKIM: Header in message incomplete. Tag I mismatch (%s - %s). Aborting DKIM test."), String(tagD).c_str(), String(tagIDomain).c_str());
            LOG_DEBUG(sMessage);

            return false;
         }
      }

      /*
         If the "h=" tag does not include the From header field, the verifier
         MUST ignore the DKIM-Signature header field and return PERMFAIL (From
         field not signed).
      */

      bool found = false;
      std::vector<AnsiString> headerFields = StringParser::SplitString(tagH,":");
      for (AnsiString headerField : headerFields)
      {
         headerField.Trim();
         headerField.ToLower();
         if (headerField == "from")
         {
            found = true;
            break;
         }
      }

      if (!found)
      {
         LOG_DEBUG("DKIM: Header in message incomplete. From field not found. Aborting DKIM test.");
         return false;
      }

      return true;
   }

   DKIM::Result
   DKIM::RetrievePublicKey_(const DKIMParameters &signatureParams, AnsiString &publicKey, AnsiString &flags)
   {
      // 6.1.2.  Get the Public Key
      AnsiString tagDomain = signatureParams.GetValue("d");
      AnsiString tagSelector = signatureParams.GetValue("s");
      AnsiString keyName = tagSelector + "._domainkey." + tagDomain;

      std::vector<String> results;
      DNSResolver resolver;
      if (!resolver.GetTXTRecords(keyName, results))
      {
         LOG_DEBUG("DKIM: Error when retrieving public key. Failed to do DNS/TXT lookup.");

         // The lookup did not complete, so this signature is neither verified nor
         // refused and the message is accepted with no DKIM verdict at all. While
         // the resolver is unavailable that is true of every signed message that
         // arrives, so it is said once in the application log: a check that has
         // silently stopped checking is indistinguishable from one that is
         // working. Note that a selector which does not exist is not this case -
         // that is an answer, and it is a PermFail below.
         AntiSpamDiagnostics::ReportCheckIncomplete(AntiSpamDiagnostics::CheckDkim,
            Formatter::Format("DKIM: The DNS lookup of the public key {0} did not complete. Signatures cannot be verified while that continues, and affected messages are accepted without a DKIM verdict.", keyName));

         return TempFail;
      }

      if (results.size() == 0)
      {
         /*
            3.  If the query for the public key fails because the corresponding
            key record does not exist, the verifier MUST immediately return
            PERMFAIL (no key for signature).
         */

         LOG_DEBUG("DKIM: Error when retrieving public key. No key for signature.");
         return PermFail;
      }

      /* example:
         Line breaks won't actually exist.
         k=rsa; t=y; p=MHwwDQYJKoZIhvcNAQEBBQADawAwaAJhAOFzgIeFCw/TN5euR2O/oMHz+rv97OjqCxwt
                       Gk8BbiPnoNP3lYCF/147zz2B9gUWc9SFLAB1Dsrfd3yN5yiFdmK/KJ5ASv9oX0iNRJ9vGp
                       JyM2IRZ8qSOCeQscnre5iVjwIDAQAB;
      */
      
      // A selector can legitimately hold more than one TXT record - most often
      // while a sender rotates its key. Inspecting only the first meant roughly
      // half of that sender's mail failed verification (and lost DMARC
      // alignment) until the rotation completed, so try each record and use the
      // first one that is usable for this signature.
      DKIMParameters dnsKeyParams;
      bool foundUsableKey = false;

      for (const AnsiString &result : results)
      {
         // A fresh instance per record: Load merges into the existing values
         // rather than replacing them, so reusing one object would blend the
         // tags of several records together.
         DKIMParameters candidateParams;
         candidateParams.Load(result);

         if (!ValidateDNSEntry_(candidateParams, signatureParams))
            continue;

         dnsKeyParams = candidateParams;
         foundUsableKey = true;
         break;
      }

      if (!foundUsableKey)
      {
         LOG_DEBUG("DKIM: Error when retrieving public key. Validation of DNS entry failed.");
         return PermFail;
      }

      publicKey = dnsKeyParams.GetValue("p");

      // An empty value means that this public key has been revoked. 
      if (publicKey.IsEmpty())
      {
         LOG_DEBUG("DKIM: Error when retrieving public key. Public key has been revoked.");
         return PermFail;
      }

      flags = dnsKeyParams.GetValue("t");

      if (flags.Find("s") >= 0)
      {
         /*
            Flag: s
            Any DKIM-Signature header fields using the "i=" tag MUST have
            the same domain value on the right-hand side of the "@" in
            the "i=" tag and the value of the "d=" tag. 
         */
         
         AnsiString tagI = signatureParams.GetValue("i");

         if (!tagI.IsEmpty())
         {
            AnsiString tagD = signatureParams.GetValue("d");
            AnsiString tagIDomain = StringParser::ExtractDomain(tagI);
            if (tagIDomain.CompareNoCase(tagD) != 0)
            {
               
               String sMessage;
               sMessage.Format(_T("DKIM: Header in message incomplete. Tag I mismatch (%s - %s). Aborting."), String(tagD).c_str(), String(tagIDomain).c_str());
               LOG_DEBUG(sMessage);

               return PermFail;
            }
         }
      }
      
      return Pass;
   }

   bool 
   DKIM::ValidateDNSEntry_(const DKIMParameters &entryParams, const DKIMParameters &headerParams)
   {
      if (entryParams.GetParamCount() == 0)
         return false;

      AnsiString tagV = entryParams.GetValue("v");
      if (tagV != "" && tagV != "DKIM1")
         return false;

      AnsiString tagP = entryParams.GetValue("p");
      if (tagP == "")
         return false;

      /*
         If the "g=" tag in the public key does not match the Local-part
         of the "i=" tag in the message signature header field, the
         verifier MUST ignore the key record and return PERMFAIL
         (inapplicable key).
      */
      AnsiString tagI = headerParams.GetValue("i");
      AnsiString tagILocal = StringParser::ExtractAddress(tagI);
      
      if (entryParams.GetIsSet("g"))
      {
         AnsiString tagG = entryParams.GetValue("g");

         if (tagILocal.IsEmpty())
         {
            /*
               If the Local-part of the "i=" tag on the
               message signature is not present, the "g=" tag must be "*" (valid
               for all addresses in the domain) or the entire g= tag must be
               omitted (which defaults to "g=*")
            */

            if (tagG != "*")
               return false;
         }
         else
         {
            // case sensitive!
            if (!StringParser::WildcardMatch(tagG, tagILocal))
               return false;
         }
      }

      /*
         If the "h=" tag exists in the public key record and the hash
         algorithm implied by the a= tag in the DKIM-Signature header
         field is not included in the contents of the "h=" tag, the
         verifier MUST ignore the key record and return PERMFAIL
         (inappropriate hash algorithm).
      */
      AnsiString tagH = entryParams.GetValue("h");
      if (!tagH.IsEmpty())
      {
         AnsiString tagA = headerParams.GetValue("a");
         // The "a=" tag has the form "<key-type>-<hash>" (e.g. "rsa-sha256", "ed25519-sha256").
         // Extract the hash portion after the first '-' to compare against the DNS "h=" list.
         int dashPos = tagA.Find("-");
         AnsiString usedHash = dashPos >= 0 ? tagA.Mid(dashPos + 1) : tagA;
         if (tagH.Find(usedHash) < 0)
            return false;
      }

      return true;
   }

   std::shared_ptr<Canonicalization> 
   DKIM::CreateCanonicalization_(Canonicalization::CanonicalizeMethod method)
   {
      switch (method)
      {
      case Canonicalization::Simple:
         return std::shared_ptr<Canonicalization>(new SimpleCanonicalization);
      case Canonicalization::Relaxed:
         return std::shared_ptr<Canonicalization>(new RelaxedCanonicalization);
      }

      std::shared_ptr<Canonicalization> pEmpty;
      return pEmpty;
   }

   String 
   DKIM::BuildSignatureHeader_(const String &tagA, const String &tagD, const String &tagS, const String &tagC, const String &tagQ, const String &timestampTags, const String &fieldList, const String &bodyHash, const String &signatureString)
   {
      // One format string, built once, for both callers.
      //
      // This is called twice per signature - once with an empty signatureString to
      // produce the bytes that get hashed, and once with the real signature to produce
      // the field that gets written - and RFC 6376 3.7 requires the two to be identical
      // up to and including "b=". It used to be two separate Format calls with the tag
      // list spelled out twice, which is a shape where adding a tag to one and not the
      // other produces a signature that cannot verify anywhere, with nothing failing
      // locally to say so. Adding t= and x= is exactly the change that would have done
      // it, so the duplication goes first.
      //
      // timestampTags is either empty or carries its own leading space and trailing
      // semicolon, so with no timestamps the output is byte-identical to the previous
      // version.
      String headerValue;
      headerValue.Format(_T("v=1; a=%s; d=%s; s=%s;\r\n")
         _T("\tc=%s; q=%s;%s h=%s;\r\n")
         _T("\tbh=%s;\r\n")
         _T("\tb="), tagA.c_str(), tagD.c_str(), tagS.c_str(), tagC.c_str(), tagQ.c_str(),
         timestampTags.c_str(), String(fieldList).c_str(), bodyHash.c_str());

      if (signatureString.IsEmpty())
         return headerValue;

      // The fold positions are part of the bytes a verifier canonicalizes, so this
      // length is not a free number to tune.
      const int lineLength = 250;

      String splitSignatureString;
      for (int i = 0; i < signatureString.GetLength(); i += lineLength)
      {
         if (splitSignatureString.GetLength() > 0)
            splitSignatureString += "\r\n\t";

         splitSignatureString += signatureString.Mid(i, lineLength);
      }

      return headerValue + splitSignatureString;
   }

   String
   DKIM::BuildTimestampTags_(__int64 signingTime, int validitySeconds)
   {
      String tags;

      // Leading space and trailing semicolon belong to this string, so that
      // BuildSignatureHeader_ can splice it in with no conditional of its own and an
      // empty value reproduces the previous bytes exactly.
      if (validitySeconds > 0)
         tags.Format(_T(" t=%I64d; x=%I64d;"), signingTime, signingTime + (__int64) validitySeconds);
      else
         tags.Format(_T(" t=%I64d;"), signingTime);

      return tags;
   }

   bool
   DKIM::IsSignatureExpired_(const AnsiString &tagX, __int64 verificationTime, int toleranceSeconds)
   {
      // No x= means no expiry was claimed, which is the common case and not a failure.
      if (tagX.IsEmpty())
         return false;

      // A malformed x= is not an expiry either. RFC 6376 3.5 defines the value as an
      // unsigned decimal integer; anything else is a broken signature, and treating it
      // as expired here would let a corrupt tag decide the verdict. Rejecting a value
      // too long to fit is what keeps _atoi64 from silently saturating - nineteen
      // digits is the most a signed 64-bit value has.
      const int maxDigits = 19;

      if (tagX.GetLength() > maxDigits)
         return false;

      for (int i = 0; i < tagX.GetLength(); i++)
      {
         if (tagX[i] < '0' || tagX[i] > '9')
            return false;
      }

      __int64 expiry = _atoi64(tagX.c_str());

      // The tolerance is RFC 6376 3.5's "fudge factor" for clock drift between us and
      // the signer. Without it our own clock running a few minutes fast turns other
      // people's perfectly valid mail into a DKIM failure.
      return verificationTime > (expiry + (__int64) toleranceSeconds);
   }

   bool
   DKIM::HasSignatureForDomain_(MimeHeader &mimeHeader, const AnsiString &domain)
   {
      std::vector<std::pair<AnsiString, AnsiString>> signatures = GetSignatureFields(mimeHeader);
      for (const auto &sig : signatures)
      {
         AnsiString headerValue = sig.second;
         MimeField::UnfoldField(headerValue);

         DKIMParameters params;
         params.Load(headerValue);

         AnsiString sigDomain = params.GetValue("d");
         if (sigDomain.CompareNoCase(domain) == 0)
            return true;
      }

      return false;
   }

   std::vector<std::pair<AnsiString, AnsiString> >
   DKIM::GetSignatureFields(MimeHeader &mimeHeader, bool *moreThanWeVerify)
   {
      std::vector<std::pair<AnsiString, AnsiString>> result;
      std::vector<MimeField> &fields = mimeHeader.Fields();

      size_t signatureFieldCount = 0;

      for(MimeField f : fields)
      {
         AnsiString name = f.GetName();
         if (name.CompareNoCase("DKIM-Signature") == 0)
         {
            signatureFieldCount++;

            // Keep counting past the limit rather than breaking out, so that the
            // caller can be told the difference between a message with exactly
            // the limit and one with more than we are willing to verify.
            if (result.size() < MaxSignaturesToVerify)
            {
               AnsiString headerValue = f.GetValue();
               result.push_back(std::make_pair(name, headerValue));
            }
         }
      };

      if (moreThanWeVerify)
         *moreThanWeVerify = signatureFieldCount > MaxSignaturesToVerify;

      return result;
   }

}
