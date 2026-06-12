// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// ARC sealing (Authenticated Received Chain, RFC 8617).
//
// When a message is relayed onward (forwarding, distribution lists), the
// SPF/DKIM evaluation of the next hop typically fails. ARC preserves the
// authentication results observed by this server in a cryptographically
// signed chain so downstream receivers can make informed decisions.

#pragma once

namespace HM
{
   class Message;

   class Arc
   {
   public:

      // Adds one ARC set (ARC-Seal, ARC-Message-Signature,
      // ARC-Authentication-Results) to the message, signed with the
      // domain's DKIM key. Returns false on error.
      bool Seal(std::shared_ptr<Message> message,
                const AnsiString &domain,
                const AnsiString &selector,
                const String &privateKeyFile);

   private:

      struct ArcSet
      {
         AnsiString authentication_results;
         AnsiString message_signature;
         AnsiString seal;
      };

      static bool ParseExistingSets_(const AnsiString &header, std::map<int, ArcSet> &sets);
      static AnsiString ValidateExistingChain_(const std::map<int, ArcSet> &sets);
      static AnsiString StripSealSignatureValue_(const AnsiString &sealValue);
      static AnsiString BuildAuthenticationResults_(int instance, const String &messageFileName, const AnsiString &header);
      static AnsiString GetHeaderFieldValue_(const AnsiString &header, const AnsiString &fieldName);
   };
}
