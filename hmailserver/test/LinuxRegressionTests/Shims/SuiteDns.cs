// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using NUnit.Framework;

namespace RegressionTests.Shared
{
   /// <summary>
   ///    The suite-wide fake DNS zone, the Windows one's shape exactly: a
   ///    <see cref="FakeDnsServer"/> bound on 127.0.0.1:53 before the first fixture,
   ///    the server's DNSServer pointed at it for the run, the two names every
   ///    fixture may assume seeded, and every fixture that adds a name calling
   ///    <see cref="Reset"/> in its teardown. What differs is how the server is
   ///    told: DNSServer is written through PUT /api/v1/settings/ini/DNSServer and
   ///    read again through POST /api/v1/server/reinitialize, which runs
   ///    LoadSettings afresh - the POSIX resolver takes the configured server from
   ///    that setting, on port 53, as the Windows one does.
   ///
   ///    The zone can only be served to a server on this machine, from a process
   ///    allowed to bind port 53 (the CI job lowers ip_unprivileged_port_start for
   ///    the purpose; a root shell needs nothing). When either is not so, or the
   ///    server is older than the two routes, nothing here throws from the
   ///    run-wide setup - an ignore there would ignore the whole run - and the
   ///    first fixture to reach for <see cref="Zone"/> is skipped with the reason.
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

      private const string IniRoute = "/api/v1/settings/ini/DNSServer";

      private static FakeDnsServer zone_;

      // Why the zone is not up, or null while it is.
      private static string notServed_;

      public static FakeDnsServer Zone
      {
         get
         {
            if (zone_ == null)
               throw NotOnThisServer.Skipped(notServed_ ?? NotOnThisServer.NoSuiteDns);
            return zone_;
         }
      }

      [OneTimeSetUp]
      public void PointTheWholeSuiteAtOneLocalZone()
      {
         if (!TestTarget.IsLocal)
         {
            notServed_ = NotOnThisServer.NoSuiteDns + " (the server is on " + TestTarget.Host + ", and the zone can only be served on 127.0.0.1)";
            return;
         }

         if (!ServerApi.HasRoute("/api/v1/settings/ini/{name}", "put") ||
             !ServerApi.HasRoute("/api/v1/server/reinitialize", "post"))
         {
            notServed_ = NotOnThisServer.NoSuiteDns + " (this server's REST API has no PUT /api/v1/settings/ini/{name}, which arrived after 6.3.2)";
            return;
         }

         try
         {
            zone_ = Seed_(new FakeDnsServer());
         }
         catch (AssertionException bind)
         {
            // FakeDnsServer fails the test when it cannot bind; here there is no
            // test to fail, only a reason to give.
            notServed_ = NotOnThisServer.NoSuiteDns + " (" + bind.Message.Trim() + ")";
            return;
         }

         ServerApi.Put(IniRoute, "{\"value\":" + ServerApi.Quote(Resolver) + "}").Expect(200, "PUT " + IniRoute);
         RegressionTests.SSL.SslSetup.Reinitialize();
         notServed_ = null;
      }

      [OneTimeTearDown]
      public void RestoreTheSystemResolver()
      {
         if (zone_ == null)
            return;

         // The zone stays up until the server is back on the system resolver, so
         // the reinitialise never runs against a dead one.
         using (zone_)
         {
            ServerApi.Delete(IniRoute).Expect(200, "DELETE " + IniRoute);
            RegressionTests.SSL.SslSetup.Reinitialize();
         }

         zone_ = null;
         notServed_ = NotOnThisServer.NoSuiteDns + " (the run has ended)";
      }

      /// <summary>
      ///    Back to the seeded zone: every fixture that added a name calls this in its
      ///    teardown, so that nothing it served leaks into the fixture after it. A
      ///    no-op while the zone is not up, because the fixture was skipped before
      ///    it added anything.
      /// </summary>
      public static void Reset()
      {
         if (zone_ == null)
            return;

         zone_.Reset();
         Seed_(zone_);
      }

      /// <summary>
      ///    Gives up 127.0.0.1:53 for the duration of the returned scope, for a test
      ///    that serves DNS itself. The zone comes back seeded - and only seeded - when
      ///    the scope is disposed.
      /// </summary>
      public static IDisposable Suspend()
      {
         FakeDnsServer zone = Zone;
         zone.Dispose();
         zone_ = null;
         return new Resumer_();
      }

      private sealed class Resumer_ : IDisposable
      {
         public void Dispose()
         {
            zone_ = Seed_(new FakeDnsServer());
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
