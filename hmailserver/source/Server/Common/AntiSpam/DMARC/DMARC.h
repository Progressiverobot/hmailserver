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

      // How this server worked out an organizational domain. RFC 9990 3.1.1.5
      // reports it in policy_published/discovery_method, where "psl" is the RFC
      // 7489 method and "treewalk" is RFC 9989's - and it asks which one WAS
      // USED, not which one is configured. Those differ: the tree walk falls
      // back to the list when a lookup fails transiently, so a report naming the
      // ini setting would describe the configuration rather than the message.
      enum DiscoveryMethod
      {
         DiscoveryPublicSuffixList = 0,
         DiscoveryTreeWalk = 1
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
         // The t= (testing) tag as published; empty when the record carries none.
         // Parsed for the RFC 9990 report's policy_published/testing element and
         // for nothing else: this server does not yet act on t=, and reporting
         // what a domain published is a statement about the DNS record rather
         // than about what the receiver did with it.
         String t;
         int pct = 100;
         DiscoveryMethod discovery_method = DiscoveryPublicSuffixList;
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

      // The same answer, plus which mechanism produced it. Not derivable from
      // the configuration by the caller: DmarcTreeWalkEnabled selects the walk,
      // but the walk hands back to the Public Suffix List when a lookup fails
      // transiently, and it is the mechanism that ANSWERED that RFC 9990's
      // discovery_method reports. methodUsed is always written.
      static String GetOrganizationalDomain(const String &domain, DiscoveryMethod &methodUsed);

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
