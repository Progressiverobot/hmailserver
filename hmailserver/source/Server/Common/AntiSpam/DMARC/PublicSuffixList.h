// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   // Longest-match lookup against the bundled Public Suffix List
   // (https://publicsuffix.org/), with full support for wildcard (*.) and
   // exception (!) rules and both the ICANN and PRIVATE sections.
   //
   // The data is a generated header compiled into the binary - see
   // build\generate-public-suffix-list.ps1 for why it is not a file loaded at
   // runtime - so there is nothing to initialize, nothing to lock, and no way
   // for the data to be absent or stale on an operator's machine.
   class PublicSuffixList
   {
   public:
      // Returns how many of the trailing labels of a domain form its public
      // suffix, per the PSL algorithm: exception rules beat all others,
      // otherwise the matching rule with the most labels wins, and a domain
      // matching no rule at all falls back to the implicit "*" rule (its
      // rightmost label is the suffix).
      //
      // labels must be the domain split on '.', already lowercased and with
      // any trailing root dot removed - exactly what GetOrganizationalDomain
      // prepares. Passing prepared labels rather than the raw domain keeps the
      // one split shared between this lookup and the caller's own label
      // arithmetic, so the two can never disagree about label boundaries.
      //
      // Returns 0 only when the compiled-in tables are empty, which a correct
      // build cannot produce; the caller decides the failure direction for
      // that case (see the comment at the call site in DMARC.cpp).
      static size_t GetPublicSuffixLabelCount(const std::vector<String> &labels);
   };
}
