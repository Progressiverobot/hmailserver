// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
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

      // Everything the aggregate reporter (RFC 7489 section 7.2) needs to know
      // about one evaluation, filled by Verify when the caller asks. The
      // policy_domain is the domain the record was FOUND at - the From domain,
      // or its organizational domain after the section 6.6.3 fallback - which
      // is where the report must be addressed and what its policy_published
      // block must name.
      struct Evaluation
      {
         bool policy_found = false;
         String policy_domain;
         String adkim;                 // as published; empty means the default (r)
         String aspf;
         String p;
         String sp;
         String np;                    // empty when the record carries none
         int pct = 100;
         bool spf_aligned = false;
         bool dkim_aligned = false;
      };

      // fromHeaderDomain  - domain of the RFC5322.From address.
      // envelopeFromDomain- domain of the RFC5321.MailFrom address (SPF identity).
      // spfPassed         - whether SPF evaluation returned Pass.
      // dkimPassingDomains- d= domains of DKIM signatures that verified.
      // evaluation        - optional; filled for the aggregate reporter.
      Result Verify(const String &fromHeaderDomain,
                    const String &envelopeFromDomain,
                    bool spfPassed,
                    const std::vector<AnsiString> &dkimPassingDomains,
                    Evaluation *evaluation = nullptr);

      // Extracts the email address part from a From-header value, e.g.
      // "Display Name <user@example.com>" -> "user@example.com".
      static String ExtractAddressFromHeaderValue(const String &headerValue);

      // Returns the organizational domain per RFC 7489 3.2, e.g.
      // mail.example.co.uk -> example.co.uk, resolved against the bundled
      // Public Suffix List (see PublicSuffixList.h). A domain that is itself
      // a public suffix is returned unchanged.
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

      // Whether RFC 9989 np= applies: the From domain is a subdomain of the policy
      // domain AND does not exist in the DNS. Answers false on a resolver failure,
      // so an outage cannot turn np=reject loose on subdomains that do exist.
      bool SubdomainIsNonExistent_(const String &domain);
   };
}
