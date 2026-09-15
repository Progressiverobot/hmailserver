// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// Ported from hMailServer upstream, https://github.com/hmailserver/hmailserver,
// commit 1beaeab9 ("Replace SPF evaluator", #645) of 13 September 2026, where
// this file carries the notice "Copyright (c) 2010 Martin Knafve /
// hMailServer.com". Both trees are AGPL-3.0-or-later, so the port is
// licence-clean and the attribution stands here.
//
// Taken unchanged.

#pragma once

namespace HM
{
   // The corners of RFC 7208 section 4 the conformance suite beside this does not
   // reach - README.md lists them. Failures are returned rather than reported, for
   // the reasons SPFRecordTester's are.
   class SPFEvaluatorTester
   {
   public:

      std::vector<AnsiString> Run();

   private:

      void TestTemporaryFailures_();
      void TestTermCounting_();
      void TestTermLimit_();
      void TestResolverAnswers_();
      void TestExplanationOnlyForFail_();
      void TestVoidLookupsAreCountedPerTerm_();
      void TestIncludeDoesNotFetchAnExplanation_();
      void TestUnusableTargetNames_();

      std::vector<AnsiString> failures_;
   };
}
