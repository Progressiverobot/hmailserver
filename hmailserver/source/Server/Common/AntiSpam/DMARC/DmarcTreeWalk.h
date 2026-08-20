// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// The DMARC DNS Tree Walk, RFC 9989 section 4.10.
//
// DMARCbis (RFC 9989, which obsoletes RFC 7489) replaces the Public Suffix List
// with a DNS tree walk for finding a domain's Organizational Domain. This is not
// a tidying-up: an evaluator using the PSL where the sender expects tree-walk
// semantics produces the WRONG POLICY DECISION rather than a soft failure, in
// both directions - relaxed alignment that should have matched and did not, or
// matched and should not have.
//
// The walk, from section 4.10, is:
//
//   1. Query _dmarc.<start> for TXT. Discard anything not beginning with a v=
//      tag naming this version of DMARC; discard ALL of them if more than one
//      record survives for a single target. If one survives and it carries
//      psd=n or psd=y, stop.
//   2. Count the labels. If there are eight or more, jump straight to the
//      seven-label suffix - the shortcut that bounds an eight-label domain to
//      eight queries rather than one per label.
//   3. Query each successively shorter name, applying the same discard-and-stop
//      rules, until something stops it or the labels run out.
//
// Then section 4.10.2 selects the Organizational Domain from what was found,
// longest name to shortest:
//
//   1. A record with psd=n IS the Organizational Domain.
//   2. A record with psd=y, other than at the domain the walk STARTED from,
//      means the Organizational Domain is one label below it.
//   3. Otherwise the record found at the name with the fewest labels.
//
// and if none of those apply, the domain the walk started from is its own
// Organizational Domain.
//
// THE COST IS THE INTERESTING PART. The PSL answered from a compiled-in table
// for free; a tree walk is up to eight DNS queries, and DMARC needs the answer
// for the author domain AND for each authenticated identifier being aligned
// against it. Uncached, a single message could cost two dozen lookups. So the
// answers are cached, and the cache is the reason this is usable at all rather
// than an optimisation added later.

#pragma once

#include "../../Util/Singleton.h"

#include <map>

namespace HM
{
   struct DmarcTreeWalkRecord
   {
      String domain;
      String record;
      bool psd_yes = false;
      bool psd_no = false;
   };

   class DmarcTreeWalk : public Singleton<DmarcTreeWalk>
   {
   public:
      // The Organizational Domain of a domain, per RFC 9989 4.10. Returns the
      // input domain unchanged when the walk determines nothing, which is what
      // the RFC's own fallback says. dnsError reports a TRANSIENT failure, which
      // the caller must not confuse with "no records exist" - one is a temporary
      // error and the other is a definite answer.
      String GetOrganizationalDomain(const String &domain, bool &dnsError);

      // The walk itself, exposed so the self-tests can drive it, and so the
      // selection below can be tested without DNS at all.
      static std::vector<DmarcTreeWalkRecord> Walk(const String &domain, bool &dnsError);

      // RFC 9989 4.10.2 applied to an already-collected result set. Pure: no DNS,
      // no configuration, no clock. This is where the rules that decide policy
      // live, so it is the part that is pinned by vectors rather than by
      // observing a live resolver.
      static String SelectOrganizationalDomain(const String &startDomain,
                                               const std::vector<DmarcTreeWalkRecord> &records);

      // Drops every cached answer. For the tests, and for an administrator who
      // has just changed a DNS record and does not want to wait out the TTL.
      void ClearCache();

   private:

      struct CacheEntry
      {
         String organizational_domain;
         time_t expires = 0;
      };

      // Short by design. A tree walk's answer depends on records the domain owner
      // can change, and DMARC is the mechanism they change them to control - so
      // caching for hours would mean honouring a policy they have already
      // withdrawn. Five minutes bounds the DNS cost of a busy relay to something
      // negligible while keeping the server responsive to a real change.
      static const time_t cache_seconds_ = 300;
      static const size_t max_cache_entries_ = 10000;

      boost::mutex mutex_;
      std::map<String, CacheEntry> cache_;
   };

   // Pins RFC 9989 4.10.2 against constructed result sets. The selection rules are
   // where a wrong answer becomes a wrong POLICY decision, and they are pure - so
   // they can be driven directly, without a resolver, which is the only way to
   // cover the cases (psd=y at a public suffix, two records at one name) that no
   // convenient real domain publishes.
   class DmarcTreeWalkTester
   {
   public:
      void Test();
   };
}
