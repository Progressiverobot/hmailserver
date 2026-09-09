// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using NUnit.Framework;

namespace RegressionTests.Shared
{
   /// <summary>
   ///    The one way a shim says "this fixture reaches something the REST API cannot
   ///    do": Assert.Ignore with a reason that names the missing surface, so that the
   ///    test counts as skipped with that reason and never as passed. NUnit turns an
   ///    IgnoreException from a test body, a SetUp or a OneTimeSetUp into an ignored
   ///    test (or fixture) rather than a failure, which is exactly the distinction
   ///    this project's result must keep: a failure here is the server's.
   ///
   ///    The reasons are collected in one place so that a run's skip list reads as a
   ///    list of API gaps, and so that when one of them closes there is one string to
   ///    delete.
   /// </summary>
   public static class NotOnThisServer
   {
      public static void Ignore(string reason)
      {
         Assert.Ignore("Not runnable against this server: " + reason);
      }

      public const string NoSettingsWrite =
         "needs a server setting written, and this server's REST API has no PUT /api/v1/settings (it arrived with wave 162)";

      public const string NoRouteCreate =
         "needs an SMTP route, and this server's REST API has no POST /api/v1/routes (it arrived with wave 162)";

      public const string NoAliasCreate =
         "needs an alias, and this server's REST API has no POST /api/v1/domains/{domain}/aliases (it arrived with wave 162)";

      public const string NoDomainCreate =
         "needs a domain created, and this server's REST API has no POST /api/v1/domains";

      public const string NoTlsListener =
         "needs TLS listeners, which the REST API cannot add and this server has none of";

      public const string NoAccountMaxSize =
         "needs an account with a maximum size, which POST /api/v1/domains/{domain}/accounts does not set";

      public const string NoSieveEvaluate =
         "needs Utilities.EvaluateSieveScript, a COM-only call with no REST equivalent";

      public const string NoMailServerLookup =
         "needs Utilities.GetMailServer, a COM-only call with no REST equivalent";

      public const string NoStatusThreadId =
         "reads Status.ThreadID, the COM server's thread id, which GET /api/v1/status does not report";

      public const string NoSuiteDns =
         "needs the suite's fake DNS zone served to the server's resolver, which this environment does not wire up";

      public const string ComWordingAsserted =
         "asserts the COM wording of a refusal that POST /api/v1/domains/{domain}/accounts makes in its own words before the server's own check runs";

      public const string NoEmptyPassword =
         "creates an account with an empty password, which POST /api/v1/domains/{domain}/accounts refuses (a password is required)";

      /// <summary>
      ///    The fixtures and tests the shims cannot stop at the point of use, ignored
      ///    from TestFixtureBase.SetUp before they start, with the reason:
      ///
      ///      - the Sieve fixtures reach Utilities.EvaluateSieveScript late-bound, and
      ///        an IgnoreException thrown inside Type.InvokeMember comes back wrapped
      ///        in a TargetInvocationException, which NUnit counts as a failure;
      ///      - the tests that need an SMTP route start a simulated remote server
      ///        before asking for the route, so the Ignore at the route would leave a
      ///        listener to dispose with an accept pending;
      ///      - the persistence tests that assert the COM wording of a refusal get the
      ///        REST route's wording first, and the empty-password test is refused at
      ///        creation.
      ///
      ///    Keyed by the fixture's full class name, or by class name and method name
      ///    joined with a dot.
      /// </summary>
      private static readonly Dictionary<string, string> Registry = new Dictionary<string, string>
      {
         { "RegressionTests.Sieve.SieveEvaluation", NoSieveEvaluate },
         { "RegressionTests.Sieve.SieveExtensionActions", NoSieveEvaluate },
         { "RegressionTests.Sieve.SieveVacation", NoSieveEvaluate },
         { "RegressionTests.SMTP.OutboundSize", NoRouteCreate },
         { "RegressionTests.SSL.SmtpDeliverySslTests", NoRouteCreate },
         { "RegressionTests.SMTP.BinaryMime.TestAuthenticatedBinarySubmissionIsRelayedViaBdat", NoRouteCreate },
         { "RegressionTests.SMTP.BinaryMime.TestAuthenticatedBinaryRelayToARemoteWithoutBinaryMimeBounces", NoRouteCreate },
         { "RegressionTests.SMTP.BinaryMime.TestDistributionListExternalMemberBouncesWith554_5_6_3", NoRouteCreate },
         { "RegressionTests.Infrastructure.Persistence.AccountNameValidation.TestAccountWithoutAddress", ComWordingAsserted },
         { "RegressionTests.Infrastructure.Persistence.AccountNameValidation.TestAccountBelongingToAnotherDomain", ComWordingAsserted },
         { "RegressionTests.Infrastructure.Persistence.AccountNameValidation.TestAccountContainingBackwardSlashInDomainName", ComWordingAsserted },
         { "RegressionTests.Infrastructure.Persistence.AccountNameValidation.TestAccountContainingForwardSlashInDomainName", ComWordingAsserted },
         { "RegressionTests.Security.Basics.TestEmptyPassword", NoEmptyPassword },
         { "RegressionTests.Sieve.SieveSyntax.DeeplyNestedBlocksAreRefusedRatherThanRecursedInto", ScriptOverRequestCeiling }
      };

      public const string ScriptOverRequestCeiling =
         "checks a 70 KB script, which is over the REST API's request ceiling - PUT /api/v1/me/filters answers 413 request too large before the parser sees it, and the COM CheckSieveSyntax has no ceiling";

      /// <summary>
      ///    Tests that are right on a Windows test host and wrong on any other, because
      ///    they build a message with AppendLine - CRLF on Windows, a bare LF elsewhere,
      ///    which the server rightly refuses with 554. The server is not at fault and
      ///    the test is not wrong where it was written, so it is skipped with the reason
      ///    only when the host is not Windows.
      /// </summary>

      // A registered reason that names a route skips only on a server without
      // that route; the registry is written for the oldest server the project
      // runs against, and the newest answers it.
      private static bool StillApplies(string reason)
      {
         if (reason == NoRouteCreate)
            return !ServerApi.HasRouteWriteRoutes;
         if (reason == NoAliasCreate)
            return !ServerApi.HasAliasWriteRoutes;
         if (reason == NoSettingsWrite)
            return !ServerApi.HasSettingsWriteRoutes;
         return true;
      }

      public static void SkipIfRegistered()
      {
         var test = TestContext.CurrentContext.Test;
         string reason;

         if (test.ClassName != null && Registry.TryGetValue(test.ClassName, out reason) && StillApplies(reason))
            Ignore(reason);

         if (test.ClassName != null && test.MethodName != null && Registry.TryGetValue(test.ClassName + "." + test.MethodName, out reason) && StillApplies(reason))
            Ignore(reason);

      }
   }
}
