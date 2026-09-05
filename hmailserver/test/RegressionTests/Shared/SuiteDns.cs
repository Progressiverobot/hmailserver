// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests
{
   /// <summary>
   ///    The whole suite's one resolver.
   ///
   ///    Until 5 September 2026 every fixture that needed a name served locally
   ///    bound its own <see cref="FakeDnsServer"/> on 127.0.0.1:53, pointed
   ///    <c>DNSServer</c> at it, restarted the service, and undid all three at the
   ///    end - fourteen fixtures, twenty-eight restarts, and every test outside
   ///    those fourteen still resolving live names. Five consecutive full-suite runs
   ///    on 19 August 2026 each exposed a fresh set of live-DNS failures, which is the
   ///    finding that made this a suite-level concern rather than a fixture-level one:
   ///    the population of tests that resolve something is larger than any single run
   ///    reveals, and pinning them one at a time costs thirty-five minutes per four.
   ///
   ///    So the zone is bound once, before the first fixture, and the server is
   ///    pointed at it once. NODATA is the correct answer for almost everything the
   ///    suite asks - "no such record" is what an SPF, DMARC or MX lookup for a test
   ///    domain should see - and the handful of names that need a positive answer are
   ///    added by the fixture that needs them and forgotten again by
   ///    <see cref="Reset"/> in its teardown. Two names are seeded for everyone,
   ///    because half a dozen fixtures want them and they are what the live answer
   ///    would be anyway: the SURBL project's permanent test point, and localhost.
   ///
   ///    A test that must serve DNS itself - the malformed-UDP retry in
   ///    CustomDnsServer - takes the port for the duration of <see cref="Suspend"/>.
   ///    A test that must point the server at some OTHER resolver restores
   ///    <see cref="Resolver"/> afterwards, not null: null would put the rest of the
   ///    run back on the system resolver, silently.
   /// </summary>
   [SetUpFixture]
   public class SuiteDns
   {
      /// <summary>What DNSServer says for the whole run.</summary>
      public const string Resolver = "127.0.0.1";

      /// <summary>
      ///    127.0.0.2 is what a URI blacklist returns for a listed name, and this is the
      ///    name the SURBL project keeps listed for exactly this purpose.
      /// </summary>
      public const string SurblTestPoint = "surbl-org-permanent-test-point.com.multi.surbl.org";

      public static FakeDnsServer Zone { get; private set; }

      [OneTimeSetUp]
      public void PointTheWholeSuiteAtOneLocalZone()
      {
         Zone = Seed_(new FakeDnsServer());

         ServerIniFile.SetSetting("DNSServer", Resolver);

         // The ini is read once, at process start.
         SingletonProvider<TestSetup>.Instance.RestartServiceAndReacquire();
      }

      [OneTimeTearDown]
      public void RestoreTheSystemResolver()
      {
         // The zone stays up until the server is back on the system resolver, so
         // the restart never runs against a dead one.
         using (Zone)
         {
            ServerIniFile.SetSetting("DNSServer", null);
            SingletonProvider<TestSetup>.Instance.RestartServiceAndReacquire();
         }

         Zone = null;
      }

      /// <summary>
      ///    Back to the seeded zone: every fixture that added a name calls this in its
      ///    teardown, so that nothing it served leaks into the fixture after it.
      /// </summary>
      public static void Reset()
      {
         Zone.Reset();
         Seed_(Zone);
      }

      /// <summary>
      ///    Gives up 127.0.0.1:53 for the duration of the returned scope, for a test
      ///    that serves DNS itself. The zone comes back seeded - and only seeded - when
      ///    the scope is disposed.
      /// </summary>
      public static IDisposable Suspend()
      {
         Zone.Dispose();
         Zone = null;
         return new Resumer_();
      }

      private sealed class Resumer_ : IDisposable
      {
         public void Dispose()
         {
            Zone = Seed_(new FakeDnsServer());
         }
      }

      private static FakeDnsServer Seed_(FakeDnsServer zone)
      {
         return zone
            .WithA(SurblTestPoint, "127.0.0.2")
            .WithA("localhost", "127.0.0.1");
      }
   }
}
