// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.AntiSpam
{
   /// <summary>
   ///    A host configured as a literal IP address must not be looked up in DNS.
   ///
   ///    This is issue #25: after upgrading to 6.2.18 an installation with SpamAssassin at
   ///    127.0.0.1 - the shipped default, and what almost every installation holds - lost
   ///    SpamAssassin entirely, with the error log filling up with "The IP address for
   ///    SpamAssassin could not be resolved. Aborting tests." from both the Control Panel's
   ///    test button and from live spam scanning.
   ///
   ///    The cause is that the host string was handed to the DNS resolver whatever it
   ///    contained, so "127.0.0.1" became a query for an A record NAMED "127.0.0.1".
   ///    Whether that returns anything is up to the resolver. The Windows DNS client
   ///    usually answers it from its own literal handling - which is why this went
   ///    unnoticed, and why it could not be reproduced on the development machine - but
   ///    that path is not taken when the query is aimed at a specific DNS server or has to
   ///    bypass the cache, and no upstream resolver has any reason to answer.
   ///
   ///    The IPv6 half needs no such caveats and is what these tests pin: nothing in DNS is
   ///    named "::1", so a host of "::1" failed to "resolve" on every machine, every time.
   ///    That was reproduced here before the fix and is the reason this fixture can prove
   ///    the change rather than merely accompany it.
   ///
   ///    What is asserted is the absence of a RESOLUTION failure, not a successful
   ///    connection: whether anything is listening on port 783 is a property of the machine
   ///    the suite runs on, and a test that required a live spamd would be a test of the
   ///    bench rather than of the server.
   /// </summary>
   [TestFixture]
   public class HostLiteralResolution : TestFixtureBase
   {
      private const string ResolutionFailure = "could not be resolved";

      private void AssertNoResolutionFailure(string host)
      {
         LogHandler.DeleteErrorLog();

         string resultText;
         SingletonProvider<TestSetup>.Instance.GetApp().Settings.AntiSpam
            .TestSpamAssassinConnection(host, 783, out resultText);

         // The error log is the right oracle here rather than the return value. The return
         // value is false both for "could not resolve the host" and for "nothing is
         // listening", and those are exactly the two cases this test has to tell apart.
         if (System.IO.File.Exists(LogHandler.GetErrorLogFileName()))
         {
            var log = LogHandler.ReadErrorLog();

            StringAssert.DoesNotContain(ResolutionFailure, log,
               "The host \"" + host + "\" is a literal IP address and was sent to DNS as though it were a name. " +
               "That is issue #25: on a resolver that does not answer for literals, SpamAssassin stops working " +
               "on a correctly configured server.");

            LogHandler.DeleteErrorLog();
         }
      }

      [Test]
      [Description("An IPv4 literal host is used directly rather than being looked up")]
      public void AnIPv4LiteralHostIsNotSentToDns()
      {
         AssertNoResolutionFailure("127.0.0.1");
      }

      [Test]
      [Description("An IPv6 literal host is used directly rather than being looked up")]
      public void AnIPv6LiteralHostIsNotSentToDns()
      {
         // The case that failed everywhere before the fix, and the reason this fixture
         // exists as evidence rather than as decoration.
         AssertNoResolutionFailure("::1");
      }

      [Test]
      [Description("A host name that genuinely does not resolve still reports that it does not")]
      public void ANameThatCannotResolveStillReportsAResolutionFailure()
      {
         // The negative control, and it is the important one: the fix must not silence the
         // diagnostic, only stop it firing on addresses. Without this, "no resolution
         // failures were logged" would pass just as well against a build that had stopped
         // reporting them at all.
         LogHandler.DeleteErrorLog();

         string resultText;
         var succeeded = SingletonProvider<TestSetup>.Instance.GetApp().Settings.AntiSpam
            .TestSpamAssassinConnection("no-such-host-for-hmailserver-tests.invalid", 783, out resultText);

         Assert.IsFalse(succeeded, "A host name in the reserved .invalid namespace appeared to resolve.");

         CustomAsserts.AssertReportedError(ResolutionFailure);
      }
   }
}
