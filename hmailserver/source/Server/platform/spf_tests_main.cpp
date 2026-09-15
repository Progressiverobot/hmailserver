// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// The SPF module's tests as a program of their own, for the POSIX build.
//
// On Windows these same four testers run inside the shipping server:
// ClassTester::DoTests calls SPFTester::Test, and the regression suite reaches it
// through Utilities.RunTestSuite. That route does not exist here - RunTestSuite is
// a COM method, and the Linux suite's shim skips it - so the CMake build makes
// this instead, and .github/workflows/linux-build.yml runs it on both
// architectures and under both compilers.
//
// Nothing here touches the network or the configuration. Each tester answers the
// evaluation's DNS queries out of a table of its own, so the whole run is
// milliseconds and its answer depends on nothing outside this repository - which
// is the point of vendoring the openspf.org corpus rather than testing against
// somebody's live zone.
//
// It prints one line per failure and the conformance count either way, and exits
// non-zero if anything failed or if the suite decided fewer cases than it holds.
// A count that falls is a case that stopped being recognised, which would
// otherwise look exactly like nothing having changed.

#include <cstdio>

#include "../SMTP/SPF/SPFEvaluatorTester.h"
#include "../SMTP/SPF/SPFMacroExpanderTester.h"
#include "../SMTP/SPF/SPFRecordTester.h"
#include "../SMTP/SPF/Conformance/SPFConformanceTester.h"

namespace
{
   int Report(const char *what, const std::vector<HM::AnsiString> &failures)
   {
      if (failures.empty())
      {
         printf("ok    %s\n", what);
         return 0;
      }

      printf("FAIL  %s: %d\n", what, (int) failures.size());

      for (size_t i = 0; i < failures.size(); i++)
         printf("        %s\n", failures[i].c_str());

      return (int) failures.size();
   }
}

int
main(int /*argc*/, char * /*argv*/[])
{
   int failures = 0;

   HM::SPFRecordTester recordTester;
   failures += Report("the grammar of RFC 7208 section 12", recordTester.Run());

   HM::SPFMacroExpanderTester macroTester;
   failures += Report("macro expansion, RFC 7208 section 7", macroTester.Run());

   HM::SPFEvaluatorTester evaluatorTester;
   failures += Report("check_host(), RFC 7208 section 4", evaluatorTester.Run());

   HM::SPFConformance::SPFConformanceTester conformanceTester;
   failures += Report("the openspf.org conformance suite for RFC 7208", conformanceTester.Run());

   const int decided = conformanceTester.GetCasesRun();
   const int held = HM::SPFConformance::SPFConformanceTester::GetCaseCount();

   printf("      conformance: %d of %d cases decided\n", decided, held);

   if (decided < held)
   {
      printf("FAIL  the conformance suite decided fewer cases than it holds\n");
      failures++;
   }

   if (failures > 0)
   {
      printf("\n%d failure(s).\n", failures);
      return 1;
   }

   printf("\nAll SPF tests passed.\n");

   return 0;
}
