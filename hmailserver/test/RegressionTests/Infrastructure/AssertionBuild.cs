// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.IO;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    The Release-with-assertions build (build.ps1 -Asserts) and the reporting path
   ///    behind it.
   ///
   ///    The shipped Release binary compiles HM_ASSERT to nothing, as it always has.
   ///    The assertion build keeps every one and reports a violation as Critical
   ///    HM6364 in the ERROR log - reported rather than aborted, so that a full suite
   ///    run on that build attributes each violation to the test that provoked it,
   ///    through the same ERROR-log check every test already has, instead of ending
   ///    at the first.
   ///
   ///    This fixture runs meaningfully on both binaries. It asks the server which
   ///    kind it is, provokes one assertion on purpose, and expects the report on the
   ///    assertion build and silence on the shipped one - which is the check that the
   ///    macro really is compiled out of the binary that takes mail from the internet.
   /// </summary>
   [TestFixture]
   public class AssertionBuild : TestFixtureBase
   {
      [Test]
      public void AProvokedAssertionIsReportedOnTheAssertionBuildAndCompiledOutOfTheShippedOne()
      {
         bool keepsAssertions = _application.Diagnostics.AssertionsEnabled;

         LogHandler.DeleteErrorLog();
         _application.Diagnostics.TriggerAssertion();

         if (keepsAssertions)
         {
            // Consumed here rather than left for TearDown, which would otherwise
            // fail the test for the very line it exists to see.
            string errorLog = LogHandler.ReadAndDeleteErrorLog();
            StringAssert.Contains("6364", errorLog, "The assertion build must report a violated HM_ASSERT as HM6364. Log: " + errorLog);
            StringAssert.Contains("Assertion failed", errorLog, errorLog);
            StringAssert.Contains("TriggerAssertion", errorLog, "The report names the expression. Log: " + errorLog);
            StringAssert.Contains("InterfaceDiagnostics.cpp", errorLog, "The report names the file. Log: " + errorLog);
         }
         else
         {
            ClassicAssert.IsFalse(File.Exists(LogHandler.GetErrorLogFileName()),
               "On the shipped build HM_ASSERT is compiled out, so provoking one must write nothing.");
         }
      }
   }
}
