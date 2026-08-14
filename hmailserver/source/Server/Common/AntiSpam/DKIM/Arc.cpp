// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// ARC sealing and chain evaluation (RFC 8617). See Arc.h.

#include "StdAfx.h"

#include "Arc.h"
#include "DKIM.h"
#include "DKIMParameters.h"
#include "Canonicalization.h"

#include "../../BO/Message.h"
#include "../../MIME/Mime.h"
#include "../../Persistence/PersistentMessage.h"
#include "../../TCPIP/DNSResolver.h"
#include "../../Util/TraceHeaderWriter.h"
#include "../../Util/Utilities.h"
#include "../../Util/Hashing/HashCreator.h"

#include <ctime>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      const int MaxArcInstance = 50; // RFC 8617 section 5.2

      // The longest chain we are prepared to validate in full. Every set costs
      // a signature verification and up to one DNS lookup, all on the thread
      // that still owes the client its SMTP response - and the sets are
      // attacker-supplied, with attacker-controlled d= domains whose name
      // servers can be made arbitrarily slow. Ten is the same bound DKIM
      // verification applies to signatures (MaxSignaturesToVerify), and real
      // chains run two to four sets; a longer chain is refused as Fail, which
      // for the sealing path means cv=fail and for the filtering path means
      // no score offset - never anything more permissive.
      const size_t MaxArcSetsToValidate = 10;

      // Header fields covered by the ARC-Message-Signature. ARC headers
      // themselves must never be included (RFC 8617 section 4.1.2).
      std::vector<AnsiString> GetAmsHeaderFields()
      {
         std::vector<AnsiString> fields;
         fields.push_back("From");
         fields.push_back("Sender");
         fields.push_back("Reply-To");
         fields.push_back("Subject");
         fields.push_back("Date");
         fields.push_back("Message-ID");
         fields.push_back("To");
         fields.push_back("Cc");
         fields.push_back("MIME-Version");
         fields.push_back("Content-Type");
         fields.push_back("Content-Transfer-Encoding");
         return fields;
      }
   }

   bool
   Arc::Seal(std::shared_ptr<Message> message,
             const AnsiString &domain,
             const AnsiString &selector,
             const String &privateKeyFile)
   {
      // The sealing identity is supplied by the caller and is not necessarily
      // the author domain - see DKIMSigner::Sign. Without a complete identity
      // there is nothing to seal with, so skip rather than emit a broken set.
      if (domain.IsEmpty() || selector.IsEmpty() || privateKeyFile.IsEmpty())
      {
         LOG_DEBUG("ARC: No sealing domain, selector or private key was given. Not sealing.");
         return false;
      }

      const String fileName = PersistentMessage::GetFileName(message);

      AnsiString header = PersistentMessage::LoadHeader(fileName);
      if (header.IsEmpty())
      {
         LOG_DEBUG("ARC: The message header could not be loaded. Not sealing.");
         return false;
      }

      // Locate any existing ARC sets in the message.
      std::map<int, ArcSet> existingSets;
      if (!ParseExistingSets_(header, existingSets))
      {
         LOG_DEBUG("ARC: Existing ARC chain is malformed. Not sealing.");
         return false;
      }

      int instance = existingSets.empty() ? 1 : existingSets.rbegin()->first + 1;

      if (instance > MaxArcInstance)
      {
         LOG_DEBUG("ARC: Chain has reached the maximum instance count. Not sealing.");
         return false;
      }

      // Determine the chain validation status for the cv= tag.
      //
      // This is the full RFC 8617 section 5.2 validation - every seal and the
      // newest ARC-Message-Signature - not just the most recent seal. A seal
      // that says cv=pass is an attestation downstream receivers act on; if we
      // had verified only the newest seal, we would be re-attesting a chain
      // whose earlier links and whose message signature nobody ever checked.
      // A lookup that does not complete (TempError) becomes cv=fail, as it
      // always has here: cv= has no "unknown" value, and "pass" is the one
      // thing an unverified chain must not be called.
      AnsiString chainValidation;
      if (existingSets.empty())
         chainValidation = "none";
      else
         chainValidation = ValidateChainSets_(existingSets, header, fileName) == ChainValidationStatus::Pass ? "pass" : "fail";

      AnsiString privateKeyContent = FileUtilities::ReadCompleteTextFile(privateKeyFile);
      if (privateKeyContent.IsEmpty())
      {
         LOG_DEBUG("ARC: Unable to read the DKIM private key file. Not sealing.");
         return false;
      }

      bool isEd25519 = DKIM::IsEd25519PrivateKey_(privateKeyContent);
      AnsiString algorithmTag = isEd25519 ? "ed25519-sha256" : "rsa-sha256";

      AnsiString timestamp;
      timestamp.Format("%I64d", static_cast<__int64>(time(nullptr)));

      RelaxedCanonicalization relaxed;

      // 1) ARC-Authentication-Results
      AnsiString aarValue = BuildAuthenticationResults_(instance, fileName, header);

      // 2) ARC-Message-Signature: a DKIM-style signature over the message,
      //    excluding all ARC header fields.
      AnsiString canonicalizedBody = relaxed.CanonicalizeBody(PersistentMessage::LoadBody(fileName));

      HashCreator hasher(HashCreator::SHA256);
      AnsiString bodyHash = hasher.GenerateHashNoSalt(canonicalizedBody, HashCreator::base64);

      std::pair<AnsiString, AnsiString> dummySignatureField;
      AnsiString fieldList;
      AnsiString canonicalizedHeaders = relaxed.CanonicalizeHeader(header, dummySignatureField, GetAmsHeaderFields(), fieldList);

      AnsiString amsValue;
      amsValue.Format("i=%d; a=%hs; d=%hs; s=%hs; c=relaxed/relaxed; t=%hs; bh=%hs; h=%hs; b=",
         instance, algorithmTag.c_str(), domain.c_str(), selector.c_str(), timestamp.c_str(),
         bodyHash.c_str(), fieldList.c_str());

      AnsiString amsSigningInput = canonicalizedHeaders;
      amsSigningInput += relaxed.CanonicalizeHeaderLine("arc-message-signature", amsValue);

      AnsiString amsSignature = DKIM::SignHash_(privateKeyContent, amsSigningInput, HashCreator::SHA256);
      if (amsSignature.IsEmpty())
      {
         LOG_DEBUG("ARC: Failed to create the ARC-Message-Signature. Not sealing.");
         return false;
      }

      amsValue += amsSignature;

      // 3) ARC-Seal: signs the entire ARC header chain including this set.
      AnsiString asValue;
      asValue.Format("i=%d; a=%hs; t=%hs; cv=%hs; d=%hs; s=%hs; b=",
         instance, algorithmTag.c_str(), timestamp.c_str(), chainValidation.c_str(),
         domain.c_str(), selector.c_str());

      AnsiString sealScope;
      for (auto iter = existingSets.begin(); iter != existingSets.end(); ++iter)
      {
         sealScope += relaxed.CanonicalizeHeaderLine("arc-authentication-results", iter->second.authentication_results) + "\r\n";
         sealScope += relaxed.CanonicalizeHeaderLine("arc-message-signature", iter->second.message_signature) + "\r\n";
         sealScope += relaxed.CanonicalizeHeaderLine("arc-seal", iter->second.seal) + "\r\n";
      }

      sealScope += relaxed.CanonicalizeHeaderLine("arc-authentication-results", aarValue) + "\r\n";
      sealScope += relaxed.CanonicalizeHeaderLine("arc-message-signature", amsValue) + "\r\n";
      sealScope += relaxed.CanonicalizeHeaderLine("arc-seal", asValue);

      AnsiString asSignature = DKIM::SignHash_(privateKeyContent, sealScope, HashCreator::SHA256);
      if (asSignature.IsEmpty())
      {
         LOG_DEBUG("ARC: Failed to create the ARC-Seal. Not sealing.");
         return false;
      }

      asValue += asSignature;

      // Write the three headers to the top of the message.
      std::vector<std::pair<AnsiString, AnsiString> > fieldsToWrite;
      fieldsToWrite.push_back(std::make_pair("ARC-Seal", asValue));
      fieldsToWrite.push_back(std::make_pair("ARC-Message-Signature", amsValue));
      fieldsToWrite.push_back(std::make_pair("ARC-Authentication-Results", aarValue));

      TraceHeaderWriter writer;
      bool result = writer.Write(fileName, message, fieldsToWrite);

      if (result)
      {
         String logMessage;
         logMessage.Format(_T("ARC: Sealed message with instance %d, cv=%hs."), instance, chainValidation.c_str());
         LOG_DEBUG(logMessage);
      }

      return result;
   }

   bool
   Arc::ParseExistingSets_(const AnsiString &header, std::map<int, ArcSet> &sets)
   {
      // Walk the raw header and collect unfolded ARC fields.
      std::vector<AnsiString> lines = StringParser::SplitString(header, "\n");

      AnsiString currentName;
      AnsiString currentValue;

      std::vector<std::pair<AnsiString, AnsiString> > fields;

      auto flushField = [&]()
      {
         if (!currentName.IsEmpty())
            fields.push_back(std::make_pair(currentName, currentValue));

         currentName = "";
         currentValue = "";
      };

      for (AnsiString line : lines)
      {
         line.TrimRight("\r");

         if (line.IsEmpty())
            break; // End of headers.

         if (line[0] == ' ' || line[0] == '\t')
         {
            // Folded continuation of the previous field.
            if (!currentName.IsEmpty())
               currentValue += " " + AnsiString(line).TrimLeft();
            continue;
         }

         flushField();

         int colonPosition = line.Find(":");
         if (colonPosition <= 0)
            continue;

         AnsiString name = line.Mid(0, colonPosition);
         name.Trim();
         name.MakeLower();

         if (name == "arc-seal" || name == "arc-message-signature" || name == "arc-authentication-results")
         {
            currentName = name;
            currentValue = line.Mid(colonPosition + 1);
            currentValue.Trim();
         }
      }

      flushField();

      for (auto field = fields.begin(); field != fields.end(); ++field)
      {
         DKIMParameters parameters;
         parameters.Load(field->second);

         AnsiString instanceValue = parameters.GetValue("i");
         if (!StringParser::IsNumeric(instanceValue))
            return false;

         int instance = atoi(instanceValue);
         if (instance < 1 || instance > MaxArcInstance)
            return false;

         ArcSet &set = sets[instance];

         if (field->first == "arc-seal")
         {
            if (!set.seal.IsEmpty())
               return false; // Duplicate.
            set.seal = field->second;
         }
         else if (field->first == "arc-message-signature")
         {
            if (!set.message_signature.IsEmpty())
               return false;
            set.message_signature = field->second;
         }
         else
         {
            if (!set.authentication_results.IsEmpty())
               return false;
            set.authentication_results = field->second;
         }
      }

      // Each set must be complete, and instances must be contiguous from 1.
      int expectedInstance = 1;
      for (auto iter = sets.begin(); iter != sets.end(); ++iter)
      {
         if (iter->first != expectedInstance)
            return false;

         if (iter->second.seal.IsEmpty() ||
             iter->second.message_signature.IsEmpty() ||
             iter->second.authentication_results.IsEmpty())
            return false;

         expectedInstance++;
      }

      return true;
   }

   Arc::ChainParseStatus
   Arc::ParseChain(const AnsiString &header, ChainInfo &info)
   {
      std::map<int, ArcSet> sets;
      if (!ParseExistingSets_(header, sets))
         return ChainParseStatus::Malformed;

      if (sets.empty())
         return ChainParseStatus::NoChain;

      info.instance_count = (int) sets.size();
      info.oldest_authentication_results = sets.begin()->second.authentication_results;

      for (auto iter = sets.begin(); iter != sets.end(); ++iter)
      {
         DKIMParameters sealParameters;
         sealParameters.Load(iter->second.seal);

         AnsiString sealDomain = sealParameters.GetValue("d");
         sealDomain.MakeLower();

         // A seal that does not even name its signer cannot be attributed to
         // anyone, so no trust decision about it is possible.
         if (sealDomain.IsEmpty())
            return ChainParseStatus::Malformed;

         info.sealer_domains.push_back(sealDomain);
      }

      // The newest AMS signer - see the comment on ChainInfo::sealer_domains
      // for why the content signer belongs in the trust decision.
      DKIMParameters amsParameters;
      amsParameters.Load(sets.rbegin()->second.message_signature);

      AnsiString amsDomain = amsParameters.GetValue("d");
      amsDomain.MakeLower();

      if (amsDomain.IsEmpty())
         return ChainParseStatus::Malformed;

      info.sealer_domains.push_back(amsDomain);

      return ChainParseStatus::Parsed;
   }

   Arc::ChainValidationStatus
   Arc::ValidateChain(const String &messageFileName, const AnsiString &header)
   {
      std::map<int, ArcSet> sets;
      if (!ParseExistingSets_(header, sets) || sets.empty())
         return ChainValidationStatus::Fail;

      return ValidateChainSets_(sets, header, messageFileName);
   }

   Arc::ChainValidationStatus
   Arc::ValidateChainSets_(const std::map<int, ArcSet> &sets, const AnsiString &header, const String &messageFileName)
   {
      // RFC 8617 section 5.2, in full. The order below fails as early and as
      // cheaply as possible: the cv= ladder costs only parsing, the message
      // signature costs one body hash and usually one DNS lookup, and the
      // seals cost one verification each with their key lookups cached.

      if (sets.empty())
         return ChainValidationStatus::Fail;

      if (sets.size() > MaxArcSetsToValidate)
      {
         LOG_DEBUG("ARC: The chain has more sets than are validated. Treating it as failed.");
         return ChainValidationStatus::Fail;
      }

      // 1. The cv= ladder: the first seal must say cv=none (there was no chain
      //    before it) and every later seal must say cv=pass. A cv=fail anywhere
      //    is a hop's own statement that the chain was already broken, and a
      //    chain once failed can never become valid again (RFC 8617 5.1.2).
      for (auto iter = sets.begin(); iter != sets.end(); ++iter)
      {
         DKIMParameters sealParameters;
         sealParameters.Load(iter->second.seal);

         AnsiString chainValidationTag = sealParameters.GetValue("cv");
         chainValidationTag.MakeLower();

         if (iter->first == 1)
         {
            if (chainValidationTag != "none")
               return ChainValidationStatus::Fail;
         }
         else
         {
            if (chainValidationTag != "pass")
               return ChainValidationStatus::Fail;
         }
      }

      std::map<AnsiString, KeyLookupResult> keyCache;

      // 2. The newest ARC-Message-Signature. This is the only signature over
      //    the CURRENT message content (earlier AMS instances are expected to
      //    be broken - later hops modified the message; that is why they
      //    resealed). Without it, a captured chain from any legitimately
      //    forwarded message could be transplanted onto arbitrary content.
      ChainValidationStatus messageSignatureStatus = VerifyNewestMessageSignature_(sets, header, messageFileName, keyCache);
      if (messageSignatureStatus != ChainValidationStatus::Pass)
         return messageSignatureStatus;

      // 3. Every seal, oldest first. Verifying only the newest seal would
      //    prove the last hop sealed what it received, but not that the
      //    earlier sets - including the instance-1 authentication results a
      //    filtering decision recovers - were ever signed by the domains
      //    whose names they carry.
      for (auto iter = sets.begin(); iter != sets.end(); ++iter)
      {
         ChainValidationStatus sealStatus = VerifySealSignature_(sets, iter->first, keyCache);
         if (sealStatus != ChainValidationStatus::Pass)
            return sealStatus;
      }

      return ChainValidationStatus::Pass;
   }

   Arc::ChainValidationStatus
   Arc::VerifyNewestMessageSignature_(const std::map<int, ArcSet> &sets,
                                      const AnsiString &header,
                                      const String &messageFileName,
                                      std::map<AnsiString, KeyLookupResult> &keyCache)
   {
      const int newestInstance = sets.rbegin()->first;

      // The AMS must be re-read from the raw header rather than taken from the
      // unfolded copy ParseExistingSets_ produced: with simple header
      // canonicalization the field is hashed byte for byte, folding included,
      // and unfolding it first would fail every simple-canonicalized chain.
      MimeHeader mimeHeader;
      mimeHeader.Load(header.c_str(), header.GetLength(), false);

      std::pair<AnsiString, AnsiString> signatureField;

      for (MimeField field : mimeHeader.Fields())
      {
         AnsiString fieldName = field.GetName();
         if (fieldName.CompareNoCase("ARC-Message-Signature") != 0)
            continue;

         AnsiString rawValue = field.GetValue();

         AnsiString unfoldedCandidate = rawValue;
         MimeField::UnfoldField(unfoldedCandidate);

         DKIMParameters candidateParameters;
         candidateParameters.Load(unfoldedCandidate);

         AnsiString instanceValue = candidateParameters.GetValue("i");
         if (StringParser::IsNumeric(instanceValue) && atoi(instanceValue) == newestInstance)
         {
            signatureField = std::make_pair(fieldName, rawValue);
            break;
         }
      }

      if (signatureField.first.IsEmpty())
         return ChainValidationStatus::Fail;

      AnsiString unfoldedValue = signatureField.second;
      MimeField::UnfoldField(unfoldedValue);

      DKIMParameters signatureParameters;
      signatureParameters.Load(unfoldedValue);

      AnsiString tagA = signatureParameters.GetValue("a");
      AnsiString tagB = signatureParameters.GetValue("b");
      AnsiString tagBH = signatureParameters.GetValue("bh");
      AnsiString tagD = signatureParameters.GetValue("d");
      AnsiString tagS = signatureParameters.GetValue("s");
      AnsiString tagH = signatureParameters.GetValue("h");

      if (tagA.IsEmpty() || tagB.IsEmpty() || tagBH.IsEmpty() || tagD.IsEmpty() || tagS.IsEmpty() || tagH.IsEmpty())
         return ChainValidationStatus::Fail;

      // rsa-sha1 is deliberately absent: RFC 8301 forbids verifying it, and a
      // downgrade to a broken hash is worth more to an attacker on ARC than on
      // DKIM, because a passing chain here is later allowed to offset a score.
      if (tagA != "rsa-sha256" && tagA != "ed25519-sha256")
         return ChainValidationStatus::Fail;

      std::vector<AnsiString> headerFields = StringParser::SplitString(tagH, ":");

      bool fromIsSigned = false;
      for (AnsiString headerField : headerFields)
      {
         headerField.Trim();
         headerField.MakeLower();

         // RFC 8617 4.1.2: the AMS h= MUST NOT include ARC header fields.
         if (headerField.StartsWith("arc-"))
            return ChainValidationStatus::Fail;

         if (headerField == "from")
            fromIsSigned = true;
      }

      // From must be covered, exactly as RFC 6376 5.4 requires of the
      // DKIM-Signature the AMS inherits its semantics from. This is what makes
      // a chain non-transplantable onto a different author: the filtering
      // offset only ever fires when DMARC fails for the From domain, so an
      // AMS that left From unsigned would let a captured trusted chain be
      // re-used on a message whose From was rewritten to any domain at all.
      if (!fromIsSigned)
         return ChainValidationStatus::Fail;

      // Canonicalization methods; the AMS has DKIM-Signature semantics
      // (RFC 8617 4.1.2), including the simple/simple default and the "header
      // method alone" shorthand. Split on the slash by index rather than via
      // SplitString: that helper drops an empty trailing token, so "relaxed/"
      // would yield one element and indexing the second would walk off the
      // vector - on input any stranger can send.
      AnsiString method = signatureParameters.GetValue("c");
      AnsiString headerMethod = "simple";
      AnsiString bodyMethod = "simple";

      if (!method.IsEmpty())
      {
         int slashPosition = method.Find("/");
         if (slashPosition >= 0)
         {
            headerMethod = method.Mid(0, slashPosition);
            bodyMethod = method.Mid(slashPosition + 1);
         }
         else
         {
            headerMethod = method;
         }
      }

      if ((headerMethod != "simple" && headerMethod != "relaxed") ||
          (bodyMethod != "simple" && bodyMethod != "relaxed"))
         return ChainValidationStatus::Fail;

      std::shared_ptr<Canonicalization> headerCanonicalization;
      if (headerMethod == "simple")
         headerCanonicalization = std::shared_ptr<Canonicalization>(new SimpleCanonicalization);
      else
         headerCanonicalization = std::shared_ptr<Canonicalization>(new RelaxedCanonicalization);

      std::shared_ptr<Canonicalization> bodyCanonicalization;
      if (bodyMethod == "simple")
         bodyCanonicalization = std::shared_ptr<Canonicalization>(new SimpleCanonicalization);
      else
         bodyCanonicalization = std::shared_ptr<Canonicalization>(new RelaxedCanonicalization);

      // Body hash, with the same l= handling as DKIM::ValidateBodyHash_.
      AnsiString canonicalizedBody = bodyCanonicalization->CanonicalizeBody(PersistentMessage::LoadBody(messageFileName));

      AnsiString tagL = signatureParameters.GetValue("l");
      if (!tagL.IsEmpty())
      {
         if (!StringParser::IsNumeric(tagL))
            return ChainValidationStatus::Fail;

         int coveredLength = atoi(tagL);
         if (coveredLength > canonicalizedBody.GetLength())
            return ChainValidationStatus::Fail;

         canonicalizedBody = canonicalizedBody.Mid(0, coveredLength);
      }

      HashCreator hasher(HashCreator::SHA256);
      AnsiString bodyHash = hasher.GenerateHashNoSalt(canonicalizedBody, HashCreator::base64);

      tagBH.Replace(" ", "");
      if (tagBH.Compare(bodyHash) != 0)
         return ChainValidationStatus::Fail;

      KeyLookupResult key = RetrieveArcPublicKey_(tagS, tagD, keyCache);
      if (key.status != ChainValidationStatus::Pass)
         return key.status;

      AnsiString fieldList;
      AnsiString canonicalizedHeader = headerCanonicalization->CanonicalizeHeader(header, signatureField, headerFields, fieldList);

      tagB.Replace(" ", "");

      DKIM verifier;
      if (verifier.VerifyHeaderHash_(canonicalizedHeader, tagA, tagB, key.public_key) == DKIM::Pass)
         return ChainValidationStatus::Pass;

      return ChainValidationStatus::Fail;
   }

   Arc::ChainValidationStatus
   Arc::VerifySealSignature_(const std::map<int, ArcSet> &sets,
                             int instance,
                             std::map<AnsiString, KeyLookupResult> &keyCache)
   {
      auto setIterator = sets.find(instance);
      if (setIterator == sets.end())
         return ChainValidationStatus::Fail;

      DKIMParameters sealParameters;
      sealParameters.Load(setIterator->second.seal);

      AnsiString sealDomain = sealParameters.GetValue("d");
      AnsiString sealSelector = sealParameters.GetValue("s");
      AnsiString sealAlgorithm = sealParameters.GetValue("a");
      AnsiString sealSignature = sealParameters.GetValue("b");
      sealSignature.Replace(" ", "");

      if (sealDomain.IsEmpty() || sealSelector.IsEmpty() || sealSignature.IsEmpty())
         return ChainValidationStatus::Fail;

      if (sealAlgorithm.IsEmpty())
         sealAlgorithm = "rsa-sha256";

      if (sealAlgorithm != "rsa-sha256" && sealAlgorithm != "ed25519-sha256")
         return ChainValidationStatus::Fail;

      KeyLookupResult key = RetrieveArcPublicKey_(sealSelector, sealDomain, keyCache);
      if (key.status != ChainValidationStatus::Pass)
         return key.status;

      // Reconstruct what this seal signed: the ARC sets that existed when it
      // was made - instances 1 through this one, in instance order - with the
      // b= value of THIS seal removed and no CRLF after the final line. The
      // seal always uses relaxed header canonicalization; RFC 8617 4.1.3
      // allows it no c= tag. This mirrors, field for field, the scope
      // Arc::Seal signs above.
      RelaxedCanonicalization relaxed;
      AnsiString sealScope;

      for (auto iter = sets.begin(); iter != sets.end() && iter->first <= instance; ++iter)
      {
         sealScope += relaxed.CanonicalizeHeaderLine("arc-authentication-results", iter->second.authentication_results) + "\r\n";
         sealScope += relaxed.CanonicalizeHeaderLine("arc-message-signature", iter->second.message_signature) + "\r\n";

         if (iter->first == instance)
            sealScope += relaxed.CanonicalizeHeaderLine("arc-seal", StripSealSignatureValue_(iter->second.seal));
         else
            sealScope += relaxed.CanonicalizeHeaderLine("arc-seal", iter->second.seal) + "\r\n";
      }

      DKIM verifier;
      if (verifier.VerifyHeaderHash_(sealScope, sealAlgorithm, sealSignature, key.public_key) == DKIM::Pass)
         return ChainValidationStatus::Pass;

      return ChainValidationStatus::Fail;
   }

   Arc::KeyLookupResult
   Arc::RetrieveArcPublicKey_(const AnsiString &selector,
                              const AnsiString &domain,
                              std::map<AnsiString, KeyLookupResult> &keyCache)
   {
      AnsiString lookupName = selector + "._domainkey." + domain;
      lookupName.MakeLower();

      auto cached = keyCache.find(lookupName);
      if (cached != keyCache.end())
         return cached->second;

      KeyLookupResult result;

      DNSResolver resolver;
      std::vector<String> txtRecords;
      if (!resolver.GetTXTRecords(String(lookupName), txtRecords))
      {
         // The lookup did not COMPLETE, which is not an answer. It must stay
         // distinguishable from "the key does not exist" (a definitive Fail
         // below): the filtering caller treats TempError as "act as if there
         // were no chain", while a Fail is a chain someone broke.
         result.status = ChainValidationStatus::TempError;
      }
      else
      {
         // The selector may hold several TXT records (key rotation); use the
         // first usable key record, as DKIM::RetrievePublicKey_ does. A record
         // whose p= is empty is a revoked key and is not usable.
         for (const String &record : txtRecords)
         {
            DKIMParameters keyParameters;
            keyParameters.Load(AnsiString(record));

            AnsiString versionTag = keyParameters.GetValue("v");
            if (!versionTag.IsEmpty() && versionTag != "DKIM1")
               continue;

            AnsiString keyValue = keyParameters.GetValue("p");
            if (keyValue.IsEmpty())
               continue;

            keyValue.Replace(" ", "");

            result.status = ChainValidationStatus::Pass;
            result.public_key = keyValue;
            break;
         }
      }

      keyCache[lookupName] = result;
      return result;
   }

   AnsiString
   Arc::StripSealSignatureValue_(const AnsiString &sealValue)
   {
      // Removes the value of the b= tag while keeping the tag itself,
      // mirroring DKIM signature verification rules.
      AnsiString result;
      result.reserve(sealValue.GetLength());

      int position = 0;
      int length = sealValue.GetLength();

      while (position < length)
      {
         // Find the start of the next tag.
         int tagStart = position;

         int equalsPosition = sealValue.Find("=", tagStart);
         if (equalsPosition < 0)
         {
            result += sealValue.Mid(position);
            break;
         }

         AnsiString tagName = sealValue.Mid(tagStart, equalsPosition - tagStart);
         tagName.Trim();

         int semicolonPosition = sealValue.Find(";", equalsPosition);
         int valueEnd = semicolonPosition < 0 ? length : semicolonPosition + 1;

         if (tagName.CompareNoCase("b") == 0)
         {
            result += sealValue.Mid(tagStart, equalsPosition - tagStart + 1);
            if (semicolonPosition >= 0)
               result += ";";
         }
         else
         {
            result += sealValue.Mid(tagStart, valueEnd - tagStart);
         }

         position = valueEnd;
      }

      return result;
   }

   AnsiString
   Arc::BuildAuthenticationResults_(int instance, const String &messageFileName, const AnsiString &header)
   {
      AnsiString authservId = Utilities::ComputerName();
      authservId.MakeLower();

      // DKIM result: verify the message as stored right now.
      AnsiString dkimResult = "none";

      DKIM dkim;
      switch (dkim.Verify(messageFileName))
      {
      case DKIM::Pass:
         dkimResult = "pass";
         break;
      case DKIM::PermFail:
         dkimResult = "fail";
         break;
      case DKIM::TempFail:
         dkimResult = "temperror";
         break;
      default:
         dkimResult = "none";
         break;
      }

      // SPF result: reuse the result recorded at reception time, if any.
      AnsiString spfResult = "none";

      AnsiString receivedSpf = GetHeaderFieldValue_(header, "received-spf");
      if (!receivedSpf.IsEmpty())
      {
         int spacePosition = receivedSpf.Find(" ");
         AnsiString firstToken = spacePosition > 0 ? receivedSpf.Mid(0, spacePosition) : receivedSpf;
         firstToken.Trim();
         firstToken.MakeLower();

         if (firstToken == "pass" || firstToken == "fail" || firstToken == "softfail" ||
             firstToken == "neutral" || firstToken == "none" || firstToken == "temperror" ||
             firstToken == "permerror")
         {
            spfResult = firstToken;
         }
      }

      AnsiString result;
      result.Format("i=%d; %hs; dkim=%hs; spf=%hs",
         instance, authservId.c_str(), dkimResult.c_str(), spfResult.c_str());

      return result;
   }

   AnsiString
   Arc::GetHeaderFieldValue_(const AnsiString &header, const AnsiString &fieldName)
   {
      std::vector<AnsiString> lines = StringParser::SplitString(header, "\n");

      bool inField = false;
      AnsiString value;

      for (AnsiString line : lines)
      {
         line.TrimRight("\r");

         if (line.IsEmpty())
            break;

         if (line[0] == ' ' || line[0] == '\t')
         {
            if (inField)
               value += " " + AnsiString(line).TrimLeft();
            continue;
         }

         if (inField)
            break; // Field complete.

         int colonPosition = line.Find(":");
         if (colonPosition <= 0)
            continue;

         AnsiString name = line.Mid(0, colonPosition);
         name.Trim();
         name.MakeLower();

         if (name == fieldName)
         {
            inField = true;
            value = line.Mid(colonPosition + 1);
            value.Trim();
         }
      }

      return value;
   }
}
