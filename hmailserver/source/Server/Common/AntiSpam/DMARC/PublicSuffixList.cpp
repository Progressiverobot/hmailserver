// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "PublicSuffixList.h"

// The generated rule tables. Included here and nowhere else, so the ~270 KB of
// string data exists in exactly one translation unit.
#include "PublicSuffixListData.h"

#include <algorithm>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // Binary search over one of the generated arrays. The arrays are sorted
      // by the generator in UTF-16 code-unit order - the order wcscmp compares
      // in on Windows - which is what makes this search valid; the regression
      // fixture for the generated data asserts that ordering on every entry,
      // because an unsorted table would not fail loudly here, it would just
      // miss entries at random, and a missed entry fails towards the forgery
      // direction.
      bool ContainsRule_(const wchar_t *const *rules, size_t ruleCount, const wchar_t *candidate)
      {
         return std::binary_search(rules, rules + ruleCount, candidate,
            [](const wchar_t *left, const wchar_t *right)
            {
               return wcscmp(left, right) < 0;
            });
      }
   }

   size_t
   PublicSuffixList::GetPublicSuffixLabelCount(const std::vector<String> &labels)
   {
      using namespace PublicSuffixListData;

      // A correct build cannot hit this: the generator refuses to emit a
      // truncated table. It exists so that if a broken regeneration were ever
      // committed, the caller gets an unambiguous signal instead of every
      // domain silently resolving through the implicit "*" rule below.
      if (NormalRulesCount == 0)
         return 0;

      const size_t labelCount = labels.size();

      // No rule has more than MaxRuleLabels labels (wildcards counted at their
      // matching length), so candidates beyond that cannot match anything and
      // are not probed. This bounds the work per lookup at MaxRuleLabels
      // candidates regardless of how many labels the input has.
      const size_t maxCandidateLabels = std::min(labelCount, MaxRuleLabels);

      size_t longestExceptionMatch = 0;
      size_t longestListMatch = 0;

      // Candidates are built right-to-left: for count = 1 the candidate is the
      // TLD, for count = 2 the last two labels, and so on. Each candidate is
      // probed against all three tables, and the longest match of each kind is
      // kept, because the PSL prevailing-rule algorithm is not first-match: an
      // exception rule beats every other matching rule even when a longer
      // non-exception rule also matches, so no probe order within one pass
      // could return early without assuming facts about the data.
      String candidate;
      String previousCandidate;

      for (size_t count = 1; count <= maxCandidateLabels; count++)
      {
         previousCandidate = candidate;

         if (count == 1)
            candidate = labels[labelCount - 1];
         else
            candidate = labels[labelCount - count] + _T(".") + candidate;

         if (ContainsRule_(ExceptionRules, ExceptionRulesCount, candidate.c_str()))
            longestExceptionMatch = count;

         if (ContainsRule_(NormalRules, NormalRulesCount, candidate.c_str()))
            longestListMatch = count;

         // A wildcard rule *.X matches a candidate whose leftmost label is
         // anything and whose remainder is exactly X. The table stores X (the
         // generator strips the "*."), and the remainder of the current
         // candidate is precisely the previous iteration's candidate, so no
         // second string is ever assembled for the wildcard probe.
         if (count >= 2 && ContainsRule_(WildcardRules, WildcardRulesCount, previousCandidate.c_str()))
            longestListMatch = count;
      }

      // An exception rule names a domain that is NOT a public suffix despite a
      // wildcard saying otherwise: for !city.kobe.jp the public suffix of
      // city.kobe.jp is kobe.jp, i.e. the exception with its leftmost label
      // removed (RFC-side, "modify the prevailing rule"). The generator
      // guarantees every exception has at least two labels, so this cannot
      // collide with the 0 = "no data" sentinel above.
      if (longestExceptionMatch > 1)
         return longestExceptionMatch - 1;

      if (longestListMatch > 0)
         return longestListMatch;

      // No rule matched: the PSL's implicit "*" rule applies and the rightmost
      // label alone is the public suffix. This is what makes an unknown or
      // brand-new TLD behave sensibly (example.newtld -> example.newtld is its
      // own organizational domain) instead of unmatchable.
      return 1;
   }
}
