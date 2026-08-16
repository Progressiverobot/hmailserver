// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "Canonicalization.h"
#include "../../Util/Hashing/HashCreator.h"

namespace HM
{
   class Message;
   class MessageData;
   class MimeHeader;
   class DKIMParameters;
   class MimeField;

   class DKIM
   {
   public:
      DKIM();

      static void Initialize();

      enum Result
      {
         Neutral = 0,
         Pass = 1,
         TempFail = 2,
         PermFail = 3
      };

      enum Settings
      {
         // Limit signing of huge messages, to prevent memory/perforamnce issues.
         MaxFileSize = 1024 * 1024 * 50
      };

      bool Sign(std::shared_ptr<Message> message,
                const AnsiString &header,
                const AnsiString &domain,
                const AnsiString &selector,
                const String &privateKey,
                HashCreator::HashType algorithm,
                Canonicalization::CanonicalizeMethod headerMethod,
                Canonicalization::CanonicalizeMethod bodyMethod);

      Result Verify(const String &messageFile);

      // DMARC support: verifies all signatures and returns the d= signing
      // domains of every signature that passed verification.
      Result Verify(const String &messageFile, std::vector<AnsiString> &passingDomains);

      // Shared signing/verification primitives, also used by ARC (RFC 8617).
      static AnsiString SignHash_(AnsiString &privateKey, AnsiString &canonicalizedHeader, HashCreator::HashType keySize);
      static bool IsEd25519PrivateKey_(AnsiString &privateKey);
      Result VerifyHeaderHash_(AnsiString canonicalizedHeader, const AnsiString &tagA, AnsiString &tagB, const AnsiString &publicKeyString);

   private:

      bool ValidateHeaderContents_(const DKIMParameters &signatureParams);
      bool ValidateBodyHash_(const String &fileName, const DKIMParameters &signatureParams, std::shared_ptr<Canonicalization> canonicalization);
      bool ValidateDNSEntry_(const DKIMParameters &entryParams, const DKIMParameters &headerParams);
      Result VerifySignature_(const String &fileName, const AnsiString &messageHeader, std::pair<AnsiString, AnsiString> signatureField, AnsiString &signingDomain);
      Result RetrievePublicKey_(const DKIMParameters &signatureParams, AnsiString &publicKey, AnsiString &flags);
      AnsiString GetDKIMWithoutSignature_(AnsiString value);

      String BuildSignatureHeader_(const String &tagA, const String &tagD, const String &tagS, const String &tagC, const String &tagQ, const String &timestampTags, const String &fieldList, const String &bodyHash, const String &signatureString);

      // RFC 6376 3.5 signature timestamps. timestampTags above is either empty or
      // carries its own leading space and trailing semicolon, e.g. " t=1786500000;".
      static String BuildTimestampTags_(__int64 signingTime, int validitySeconds);
      static bool IsSignatureExpired_(const AnsiString &tagX, __int64 verificationTime, int toleranceSeconds);
      std::shared_ptr<Canonicalization> CreateCanonicalization_(Canonicalization::CanonicalizeMethod method);
      bool HasSignatureForDomain_(MimeHeader &mimeHeader, const AnsiString &domain);
      static std::vector<AnsiString> recommendedHeaderFields_;

      // RFC 6376 5.4 oversigning: header field names to list in h= once MORE often than
      // the message carries them. The extra listing hashes as the null string (5.4.2),
      // so for a well-formed message it changes not one byte of the header hash - what
      // it binds is the COUNT. A verifier walks h= in order taking the bottom-most
      // unused instance of each name, so an instance added after we signed lands where
      // we hashed nothing and the signature fails.
      //
      // Without it, a second From: prepended to a message is what every mail client
      // displays, while a bottom-up verifier still passes on the original underneath.
      static std::vector<AnsiString> oversignHeaderFields_;

      static void InitializeOversigning_();

      // Returns the DKIM-Signature fields of the message, up to the verification
      // limit in DKIM.cpp. moreThanWeVerify, when given, is set if the message
      // carried more than that - the caller has then not seen every signature and
      // must not report the message as having failed.
      std::vector<std::pair<AnsiString, AnsiString> > GetSignatureFields(MimeHeader &mimeHeader, bool *moreThanWeVerify = nullptr);
   };

}