// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NUnit.Framework;
using RegressionTests.Shared;

// The namespace is the point: a SetUpFixture runs once for every test in its
// namespace and below, so this one lives at RegressionTests, over every
// fixture, as the Windows one does - in RegressionTests.Shared it ran for
// nothing, and the first Linux run resolved every name through the host.
namespace RegressionTests
{
   /// <summary>
   ///    The suite-wide fake DNS zone, the Windows one's shape exactly: a
   ///    <see cref="FakeDnsServer"/> bound on port 53 of a loopback address before
   ///    the first fixture, the server's DNSServer pointed at it for the run, the
   ///    two names every fixture may assume seeded, and every fixture that adds a
   ///    name calling <see cref="Reset"/> in its teardown. What differs is how the
   ///    server is told: DNSServer is written through PUT /api/v1/settings/ini/DNSServer
   ///    and read again through POST /api/v1/server/reinitialize, which runs
   ///    LoadSettings afresh - the POSIX resolver takes the configured server from
   ///    that setting, on port 53, as the Windows one does.
   ///
   ///    And which address. The Windows suite's is 127.0.0.1, and so is a plain
   ///    Linux host's; two benches cannot serve it there. A GitHub-hosted runner's
   ///    hardening agent runs a DNS proxy on 127.0.0.1:53, so the bind fails. WSL2
   ///    in mirrored networking mode binds 127.0.0.1:53 without complaint and then
   ///    delivers nothing sent to it - measured 13 September 2026 with a plain
   ///    responder and strace: the query leaves, nothing arrives - while the same
   ///    packet to 127.0.53.53 arrived at once. Any 127/8 address is loopback on
   ///    Linux without configuration, so the setup tries 127.0.0.1 and then
   ///    127.0.53.53, and takes the first that both binds and answers a query sent
   ///    to it from this process, before it points the server there. The fixtures
   ///    do not know which was chosen; <see cref="Resolver"/> says.
   ///
   ///    The zone can only be served to a server on this machine, from a process
   ///    allowed to bind port 53 (the CI job lowers ip_unprivileged_port_start for
   ///    the purpose; a root shell needs nothing). When either is not so, or the
   ///    server is older than the two routes, or no address serves, nothing here
   ///    throws from the run-wide setup - an ignore there would ignore the whole
   ///    run - and the first fixture to reach for <see cref="Zone"/> is skipped
   ///    with the reason.
   /// </summary>
   [SetUpFixture]
   public class SuiteDns
   {
      /// <summary>
      ///    What DNSServer says for the whole run: the address the zone is served on.
      ///    127.0.0.1 until the run-wide setup has chosen, which is before any fixture
      ///    reads it.
      /// </summary>
      public static string Resolver { get; private set; } = "127.0.0.1";

      /// <summary>
      ///    127.0.0.2 is what a URI blacklist returns for a listed name, and this is the
      ///    name the SURBL project keeps listed for exactly this purpose.
      /// </summary>
      public const string SurblTestPoint = "surbl-org-permanent-test-point.com.multi.surbl.org";

      private const string IniRoute = "/api/v1/settings/ini/DNSServer";

      // In the order they are tried. See the class comment for the two benches
      // the second one is for.
      private static readonly string[] Candidates = { "127.0.0.1", "127.0.53.53" };

      private static FakeDnsServer zone_;

      // The address the zone was served on, for the one that replaces it after a
      // Suspend.
      private static IPAddress address_;

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
         // The explicit way out, for a bench that wants the DNS fixtures skipped
         // whatever the probe below would find.
         if (Environment.GetEnvironmentVariable("HMTEST_NO_FAKE_DNS") == "1")
         {
            notServed_ = NotOnThisServer.NoSuiteDns + " (HMTEST_NO_FAKE_DNS=1: this bench runs without the zone)";
            return;
         }

         if (!TestTarget.IsLocal)
         {
            notServed_ = NotOnThisServer.NoSuiteDns + " (the server is on " + TestTarget.Host + ", and the zone can only be served on loopback)";
            return;
         }

         if (!ServerApi.HasRoute("/api/v1/settings/ini/{name}", "put") ||
             !ServerApi.HasRoute("/api/v1/server/reinitialize", "post"))
         {
            notServed_ = NotOnThisServer.NoSuiteDns + " (this server's REST API has no PUT /api/v1/settings/ini/{name}, which arrived after 6.3.2)";
            return;
         }

         var reasons = new List<string>();

         foreach (string candidate in Candidates)
         {
            IPAddress address = IPAddress.Parse(candidate);
            FakeDnsServer zone;

            try
            {
               zone = new FakeDnsServer(address);
            }
            catch (InvalidOperationException bind)
            {
               // FakeDnsServer says which address and why; here there is no test
               // to fail, only the next address to try.
               reasons.Add(bind.Message.Trim());
               continue;
            }

            string undelivered = Undelivered_(zone);

            if (undelivered != null)
            {
               zone.Dispose();
               reasons.Add(undelivered);
               continue;
            }

            zone_ = Seed_(zone);
            address_ = address;
            Resolver = candidate;
            break;
         }

         if (zone_ == null)
         {
            notServed_ = NotOnThisServer.NoSuiteDns + " (" + string.Join("; ", reasons) + ")";
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
      ///    Gives up the zone's port for the duration of the returned scope, for a
      ///    test that serves DNS itself (on the same address: <see cref="Resolver"/>).
      ///    The zone comes back seeded - and only seeded - when the scope is disposed.
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
            zone_ = Seed_(new FakeDnsServer(address_));
         }
      }

      private static FakeDnsServer Seed_(FakeDnsServer zone)
      {
         return zone
            .WithA(SurblTestPoint, "127.0.0.2")
            .WithA("localhost", "127.0.0.1");
      }

      /// <summary>
      ///    One query to the zone from a socket of this process, the way the server
      ///    will send its: null when an answer came back, otherwise why the address is
      ///    no use. A bind that succeeds proves nothing on the bench this exists for.
      /// </summary>
      private static string Undelivered_(FakeDnsServer zone)
      {
         byte[] query = ProbeQuery_();

         using (var client = new UdpClient(AddressFamily.InterNetwork))
         {
            client.Client.ReceiveTimeout = 1500;

            try
            {
               client.Send(query, query.Length, new IPEndPoint(zone.Address, 53));

               var from = new IPEndPoint(IPAddress.Any, 0);
               client.Receive(ref from);
               return null;
            }
            catch (SocketException lost)
            {
               return "a query to " + zone.Address + ":53 was not answered (" + lost.SocketErrorCode +
                      "): this bench does not deliver loopback UDP to that address";
            }
         }
      }

      // A recursion-desired A query for probe.suite-dns.invalid: twelve bytes of
      // header, the name as labels, type and class.
      private static byte[] ProbeQuery_()
      {
         var packet = new List<byte> { 0x53, 0x44, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

         foreach (string label in "probe.suite-dns.invalid".Split('.'))
         {
            packet.Add((byte) label.Length);
            packet.AddRange(Encoding.ASCII.GetBytes(label));
         }

         packet.AddRange(new byte[] { 0x00, 0x00, 0x01, 0x00, 0x01 });
         return packet.ToArray();
      }
   }
}
