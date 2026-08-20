// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// In-memory store of inbound DMARC evaluation outcomes, aggregated per UTC
// day and policy domain. Used to generate DMARC aggregate reports (RFC 7489
// section 7.2) - the rua reports this server has always consumed policies
// for but never produced.
//
// The sibling of TlsRptStore, and deliberately shaped like it: a day is a
// map of buckets, a completed day is one before today, and popping a day is
// destructive - the reporter owns whatever it pops.

#pragma once

namespace HM
{
   class DmarcRptStore : public Singleton<DmarcRptStore>
   {
   public:

      // One <record> element of the aggregate report: every message that
      // shared this exact combination of source, verdict and identities,
      // counted rather than stored.
      struct Row
      {
         AnsiString source_ip;
         AnsiString disposition;        // "none" / "quarantine" / "reject"
         AnsiString dkim;               // aligned DKIM: "pass" / "fail"
         AnsiString spf;                // aligned SPF: "pass" / "fail"
         AnsiString header_from;
         AnsiString envelope_from_domain;   // SPF identity; may be empty
         bool spf_passed = false;           // raw SPF result for auth_results
         std::vector<AnsiString> dkim_passing_domains;

         // The selector each of those signatures used, index-aligned with the
         // domains above. RFC 7489 Appendix C puts a <selector> inside
         // auth_results/dkim, and without it a domain owner reading their report can
         // see that something of theirs signed the message but not WHICH key - which
         // is the one thing they need during a rotation or after a compromise.
         std::vector<AnsiString> dkim_passing_selectors;
         int count = 0;
      };

      // Everything recorded for one policy domain in one UTC day, plus the
      // policy that was published when the first message arrived.
      struct DomainBucket
      {
         AnsiString policy_domain;
         AnsiString adkim;              // "r" / "s"
         AnsiString aspf;               // "r" / "s"
         AnsiString p;
         AnsiString sp;                 // empty when the record has none

         // The three below exist only for the RFC 9990 (DMARCbis) form of the
         // report; the RFC 7489 Appendix C schema has nowhere to put any of
         // them. They are recorded whatever the configured schema version is,
         // because the setting is read when a day is REPORTED and the rows were
         // recorded up to a day earlier - collecting only what today's setting
         // needs would produce a report missing half its policy block for
         // anybody who changed the setting.
         AnsiString np;                 // empty when the record has none
         AnsiString testing;            // the t= tag as published; empty when absent

         // "psl" or "treewalk": which mechanism actually answered when this
         // domain's policy was discovered, not which one is configured. See
         // DMARC::DiscoveryMethod.
         AnsiString discovery_method;

         // Dropped by RFC 9990, which has no pct element at all. Still recorded
         // and still reported in the 7489 form, which remains the default.
         int pct = 100;

         std::vector<Row> rows;

         // Unique row keys refused once rows hit the cap. Nothing in the
         // report schema can carry this, so the reporter logs it instead -
         // a report that silently under-counts is the failure mode to avoid.
         int dropped_rows = 0;
      };

      void Record(const AnsiString &policyDomain,
                  const AnsiString &adkim, const AnsiString &aspf,
                  const AnsiString &p, const AnsiString &sp, const AnsiString &np,
                  const AnsiString &testing, int pct, const AnsiString &discoveryMethod,
                  const AnsiString &sourceIp, const AnsiString &disposition,
                  bool dkimAligned, bool spfAligned,
                  const AnsiString &headerFrom,
                  const AnsiString &envelopeFromDomain, bool spfPassed,
                  const std::vector<AnsiString> &dkimPassingDomains,
                  const std::vector<AnsiString> &dkimPassingSelectors);

      // Returns the day keys (yyyy-mm-dd, UTC) of all days before the
      // current one which still hold data.
      std::vector<AnsiString> GetCompletedDays();

      // Removes and returns the data for one day.
      std::map<AnsiString, DomainBucket> PopDay(const AnsiString &dayKey);

      static AnsiString GetCurrentDayKey();

   private:

      boost::recursive_mutex mutex_;
      std::map<AnsiString, std::map<AnsiString, DomainBucket> > days_;
   };
}
