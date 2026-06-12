// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"

#include "DKIM.h"
#include "DKIMParameters.h"

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
   std::vector<AnsiString> DKIM::recommendedHeaderFields_;

   DKIM::DKIM()
   {

   }

   void 
   DKIM::Initialize()
   {
      // OpenSSL 1.1.0+ initializes itself automatically; the legacy
      // ERR_load_EVP_strings() call is deprecated and no longer needed.

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
         LOG_DEBUG("Message was not signed using DKIM since the size of the message exceeded the max DKIM size of 10MB.");
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
   
      AnsiString privateKeyContent = FileUtilities::ReadCompleteTextFile(String(privateKey));

      bool isEd25519Key = IsEd25519PrivateKey_(privateKeyContent);

      String tagV = "1";
      String tagA = isEd25519Key ? "ed25519-sha256" : (algorithm == HashCreator::SHA1 ? "rsa-sha1" : "rsa-sha256");
      String tagC = headerMethod == Canonicalization::Simple ? "simple/" : "relaxed/";
      tagC.append(bodyMethod == Canonicalization::Simple ? _T("simple") : _T("relaxed"));
      String tagQ = "dns/txt";

      String tagDomain = domain;
      String tagSelector = selector;

      String headerValue = BuildSignatureHeader_(tagA, tagDomain, tagSelector, tagC, tagQ, fieldList, bodyHash, "");
      
      canonicalizedHeader += headerCanonicalization->CanonicalizeHeaderLine("dkim-signature", headerValue);

      AnsiString signatureString = SignHash_(privateKeyContent, canonicalizedHeader, algorithm);
      if (signatureString == "")
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5308, "DKIM::Sign", "Failed to create signature.");
         return false;
      }
      
      headerValue = BuildSignatureHeader_(tagA, tagDomain, tagSelector, tagC, tagQ, fieldList, bodyHash, signatureString);

      // output to file.
      std::vector<std::pair<AnsiString, AnsiString> > fieldsToWrite;
      fieldsToWrite.push_back(std::make_pair("dkim-signature", headerValue));

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

      std::vector<std::pair<AnsiString, AnsiString> > signatureFields = GetSignatureFields(mimeHeader);

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
         if (signatureResult == Pass)
         {
            if (!signingDomain.IsEmpty())
               passingDomains.push_back(signingDomain);

            result = Pass;
         }
         else if (result != Pass)
         {
            result = signatureResult;
         }
      };

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
         return testMode ? Pass : PermFail;
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

      HashCreator shaer(tagA == "rsa-sha256" ? HashCreator::SHA256 : HashCreator::SHA1);
      AnsiString headerHash = shaer.GenerateHashNoSalt(canonicalizedHeader, HashCreator::base64);

      AnsiString tagB = signatureParams.GetValue("b");
      

      Result result = VerifyHeaderHash_(canonicalizedHeader, tagA, tagB, publicKeyString);

      return testMode ? Pass : result;
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
      
      AnsiString result = results[0];

      DKIMParameters dnsKeyParams;
      dnsKeyParams.Load(result);

      if (!ValidateDNSEntry_(dnsKeyParams, signatureParams))
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
   DKIM::BuildSignatureHeader_(const String &tagA, const String &tagD, const String &tagS, const String &tagC, const String &tagQ, const String &fieldList, const String &bodyHash, const String &signatureString)
   {
      String headerValue;

      if (signatureString.IsEmpty())
      {
         headerValue.Format(_T("v=1; a=%s; d=%s; s=%s;\r\n")
            _T("\tc=%s; q=%s; h=%s;\r\n")
            _T("\tbh=%s;\r\n")
            _T("\tb="), tagA.c_str(), tagD.c_str(), tagS.c_str(), tagC.c_str(), tagQ.c_str(), String(fieldList).c_str(), bodyHash.c_str());
      }
      else
      {
         String splitSignatureString;
         int lineLength = 250;
         for (int i = 0; i < signatureString.GetLength(); i += lineLength)
         {
            if (splitSignatureString.GetLength() > 0)
               splitSignatureString += "\r\n\t";

            splitSignatureString += signatureString.Mid(i, lineLength);
         }

         headerValue.Format(_T("v=1; a=%s; d=%s; s=%s;\r\n")
            _T("\tc=%s; q=%s; h=%s;\r\n")
            _T("\tbh=%s;\r\n")
            _T("\tb=%s"), tagA.c_str(), tagD.c_str(), tagS.c_str(), tagC.c_str(), tagQ.c_str(), String(fieldList).c_str(), bodyHash.c_str(), splitSignatureString.c_str());
      }

      return headerValue;
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
   DKIM::GetSignatureFields(MimeHeader &mimeHeader)
   {
      std::vector<std::pair<AnsiString, AnsiString>> result;
      std::vector<MimeField> &fields = mimeHeader.Fields();

      for(MimeField f : fields)
      {
         AnsiString name = f.GetName();
         if (name.CompareNoCase("DKIM-Signature") == 0)
         {
            AnsiString headerValue = f.GetValue();
            result.push_back(std::make_pair(name, headerValue));

            if (result.size() >= 5)
               break;
         }
      };

      return result;
   }

}
