// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   // Implements DMARC verification according to RFC 7489.
   //
   // DMARC ties together SPF and DKIM results with the RFC5322.From domain:
   // a message passes DMARC if SPF passes with an aligned domain, or if a
   // DKIM signature verifies with an aligned d= domain.
   class DMARC
   {
   public:
      DMARC();

      enum Result
      {
         // No DMARC policy published for the From domain.
         NoPolicy = 0,
         // Message passed DMARC (aligned SPF or aligned DKIM pass).
         Pass = 1,
         // Failed; published policy requests no action (p=none).
         FailNone = 2,
         // Failed; published policy requests quarantine.
         FailQuarantine = 3,
         // Failed; published policy requests reject.
         FailReject = 4,
         // DNS problem while retrieving policy.
         TempError = 5,
         // Policy record was malformed.
         PermError = 6
      };

      // fromHeaderDomain  - domain of the RFC5322.From address.
      // envelopeFromDomain- domain of the RFC5321.MailFrom address (SPF identity).
      // spfPassed         - whether SPF evaluation returned Pass.
      // dkimPassingDomains- d= domains of DKIM signatures that verified.
      Result Verify(const String &fromHeaderDomain,
                    const String &envelopeFromDomain,
                    bool spfPassed,
                    const std::vector<AnsiString> &dkimPassingDomains);

      // Extracts the email address part from a From-header value, e.g.
      // "Display Name <user@example.com>" -> "user@example.com".
      static String ExtractAddressFromHeaderValue(const String &headerValue);

      // Returns the organizational domain, e.g. mail.example.co.uk -> example.co.uk
      static String GetOrganizationalDomain(const String &domain);

   private:

      enum AlignmentMode
      {
         Relaxed = 0,
         Strict = 1
      };

      bool RetrievePolicy_(const String &domain, String &policyRecord, bool &dnsError);
      bool ParseTagValue_(const String &record, const String &tag, String &value);
      bool DomainsAligned_(const String &authenticatedDomain, const String &fromDomain, AlignmentMode mode);
   };
}
