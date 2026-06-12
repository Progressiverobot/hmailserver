// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// ARC sealing (RFC 8617). See Arc.h.

#include "StdAfx.h"

#include "Arc.h"
#include "DKIM.h"
#include "DKIMParameters.h"
#include "Canonicalization.h"

#include "../../BO/Message.h"
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
      const String fileName = PersistentMessage::GetFileName(message);

      AnsiString header = PersistentMessage::LoadHeader(fileName);
      if (header.IsEmpty())
         return false;

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
      AnsiString chainValidation = existingSets.empty() ? "none" : ValidateExistingChain_(existingSets);

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

   AnsiString
   Arc::ValidateExistingChain_(const std::map<int, ArcSet> &sets)
   {
      // If the most recent hop recorded a failed chain, the chain stays
      // failed (RFC 8617 section 5.1.2).
      const ArcSet &latestSet = sets.rbegin()->second;

      DKIMParameters sealParameters;
      sealParameters.Load(latestSet.seal);

      AnsiString previousValidation = sealParameters.GetValue("cv");
      previousValidation.MakeLower();

      if (sets.size() > 1 && previousValidation == "fail")
         return "fail";

      // Verify the most recent ARC-Seal. Its signature covers every ARC
      // header of all preceding sets, which gives chain integrity for the
      // headers we extend.
      AnsiString sealDomain = sealParameters.GetValue("d");
      AnsiString sealSelector = sealParameters.GetValue("s");
      AnsiString sealAlgorithm = sealParameters.GetValue("a");
      AnsiString sealSignature = sealParameters.GetValue("b");
      sealSignature.Replace(" ", "");

      if (sealDomain.IsEmpty() || sealSelector.IsEmpty() || sealSignature.IsEmpty())
         return "fail";

      if (sealAlgorithm.IsEmpty())
         sealAlgorithm = "rsa-sha256";

      // Fetch the public key from DNS.
      DNSResolver resolver;
      std::vector<String> txtRecords;
      if (!resolver.GetTXTRecords(String(sealSelector) + "._domainkey." + String(sealDomain), txtRecords) || txtRecords.empty())
         return "fail";

      AnsiString publicKey;
      for (const String &record : txtRecords)
      {
         DKIMParameters keyParameters;
         keyParameters.Load(AnsiString(record));

         AnsiString keyValue = keyParameters.GetValue("p");
         if (!keyValue.IsEmpty())
         {
            keyValue.Replace(" ", "");
            publicKey = keyValue;
            break;
         }
      }

      if (publicKey.IsEmpty())
         return "fail";

      // Reconstruct the seal scope: all ARC headers in instance order, with
      // the b= value of the latest seal removed.
      RelaxedCanonicalization relaxed;
      AnsiString sealScope;

      int latestInstance = sets.rbegin()->first;

      for (auto iter = sets.begin(); iter != sets.end(); ++iter)
      {
         sealScope += relaxed.CanonicalizeHeaderLine("arc-authentication-results", iter->second.authentication_results) + "\r\n";
         sealScope += relaxed.CanonicalizeHeaderLine("arc-message-signature", iter->second.message_signature) + "\r\n";

         if (iter->first == latestInstance)
            sealScope += relaxed.CanonicalizeHeaderLine("arc-seal", StripSealSignatureValue_(iter->second.seal));
         else
            sealScope += relaxed.CanonicalizeHeaderLine("arc-seal", iter->second.seal) + "\r\n";
      }

      DKIM verifier;
      if (verifier.VerifyHeaderHash_(sealScope, sealAlgorithm, sealSignature, publicKey) == DKIM::Pass)
         return "pass";

      return "fail";
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
