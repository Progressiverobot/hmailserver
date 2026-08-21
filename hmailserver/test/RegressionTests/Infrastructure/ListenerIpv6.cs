// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    IPv6 on the management and observability listeners. The REST API,
   ///    metrics and web-services listeners are hand-rolled sockets that were
   ///    AF_INET only: an IPv6 literal in the bind-address setting was
   ///    rejected as invalid, so none of them could be reached over IPv6 at
   ///    all.
   ///
   ///    Making them IPv6-capable has a security edge, and that edge is what
   ///    the most important test here pins: several of these listeners key
   ///    real decisions on "is the bind loopback" - the REST API refuses to
   ///    run without TLS unless bound to loopback, and the metrics endpoint
   ///    closes /metrics on a non-loopback bind with no credential. ::1 is
   ///    loopback in every sense that matters (only a process on this machine
   ///    can connect), so it must satisfy those checks; treating it as
   ///    non-loopback would be a false refusal, and the reverse mistake -
   ///    treating some non-loopback v6 address as loopback - would silently
   ///    remove a credential requirement. Both directions are asserted.
   ///
   ///    The dual-stack rule is also pinned: these listeners have exactly one
   ///    bind-address setting each, so :: is the only way to serve both
   ///    families and is deliberately made dual-stack (IPV6_V6ONLY cleared) -
   ///    unlike the mail protocols, which take one port row per address and
   ///    leave the Windows default alone. And :: is NOT loopback, so the
   ///    metrics credential gate must still apply to it.
   /// </summary>
   [TestFixture]
   public class ListenerIpv6 : TestFixtureBase
   {
      // From the 11420-11429 range reserved for this work.
      private const int RestPort = 11421;
      private const int MetricsPort = 11422;
      private const int MetricsAnyPort = 11423;
      private const int WebPort = 11424;

      private const string AdminPassword = "testar";

      // A belt-and-braces reset, the MetricsSecurity pattern: if a test fails
      // part way through its own finally block, a leftover ini value would
      // otherwise fail unrelated fixtures - a lingering MetricsServerAuthToken
      // turns every later unauthenticated scrape into a 401.
      [OneTimeTearDown]
      public void ListenerIpv6FixtureTearDown()
      {
         IniFileSetting.Write("RestApiBindAddress", "127.0.0.1");
         IniFileSetting.Write("RestApiPort", "0");
         IniFileSetting.Write("MetricsServerBindAddress", "127.0.0.1");
         IniFileSetting.Write("MetricsServerPort", "0");
         IniFileSetting.Write("MetricsServerAuthToken", "");
         IniFileSetting.Write("WebServicesBindAddress", "0.0.0.0");
         IniFileSetting.Write("WebServicesHttpPort", "0");

         _application.Reinitialize();
      }

      // Issues one HTTP/1.0 GET against connectAddress:port and returns the
      // status and body. The socket family follows the address family, which
      // is the point of this fixture: "::1" really connects over IPv6.
      // authorization, when non-null, becomes the Authorization header
      // verbatim. The connect is retried briefly to absorb the listener bind
      // race right after a reinitialize.
      private static (int status, string body) HttpGet(string connectAddress, int port, string path,
         string authorization, string host = null)
      {
         IPAddress address = IPAddress.Parse(connectAddress);

         using (var client = new TcpClient(address.AddressFamily))
         {
            Exception last = null;
            for (int attempt = 0; attempt < 25; attempt++)
            {
               try
               {
                  client.Connect(address, port);
                  last = null;
                  break;
               }
               catch (SocketException ex)
               {
                  last = ex;
                  Thread.Sleep(200);
               }
            }
            if (last != null)
               throw last;

            using (NetworkStream stream = client.GetStream())
            using (var memory = new MemoryStream())
            {
               var request = new StringBuilder();
               request.Append("GET ").Append(path).Append(" HTTP/1.0\r\n");
               request.Append("Host: ").Append(host ?? "localhost").Append("\r\n");

               if (authorization != null)
                  request.Append("Authorization: ").Append(authorization).Append("\r\n");

               request.Append("Connection: close\r\n\r\n");

               byte[] requestBytes = Encoding.ASCII.GetBytes(request.ToString());
               stream.Write(requestBytes, 0, requestBytes.Length);

               byte[] buffer = new byte[8192];
               int read;
               while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                  memory.Write(buffer, 0, read);

               string raw = Encoding.UTF8.GetString(memory.ToArray());

               int statusCode = 0;
               string[] lines = raw.Split(new[] { "\r\n" }, StringSplitOptions.None);
               if (lines.Length > 0)
               {
                  string[] parts = lines[0].Split(' ');
                  if (parts.Length >= 2)
                     int.TryParse(parts[1], out statusCode);
               }

               int separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
               string body = separator >= 0 ? raw.Substring(separator + 4) : "";

               return (statusCode, body);
            }
         }
      }

      private static string BasicAdminHeader()
      {
         return "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("Administrator:" + AdminPassword));
      }

      [Test]
      [Description("A ::1 bind accepts an IPv6 connection, and ::1 satisfies the REST API's TLS-mandatory-unless-loopback gate")]
      public void RestApiIpv6LoopbackBindIsServedAndCountsAsLoopback()
      {
         _settings.SetAdministratorPassword(AdminPassword);

         IniFileSetting.Write("RestApiBindAddress", "::1");
         IniFileSetting.Write("RestApiPort", RestPort.ToString());

         try
         {
            _application.Reinitialize();

            // The load-bearing part is that anything answers at all: no TLS
            // certificate is configured, and the listener refuses to start
            // without TLS unless the bind is loopback. So a 200 here proves
            // both halves at once - the socket really is an IPv6 socket, and
            // ::1 passed the loopback security check rather than being
            // refused as a non-loopback bind.
            (int status, string body) probe;

            try
            {
               probe = HttpGet("::1", RestPort, "/api/v1/status", BasicAdminHeader());
            }
            catch (SocketException ex)
            {
               throw new AssertionException(
                  "Nothing accepted an IPv6 connection on [::1]:" + RestPort + ". Either the listener " +
                  "cannot bind an IPv6 literal, or ::1 was not treated as loopback and the no-TLS start " +
                  "was refused. " + ex.Message);
            }

            Assert.AreEqual(200, probe.status, "Body: " + probe.body);
            StringAssert.Contains("\"status\"", probe.body);
         }
         finally
         {
            IniFileSetting.Write("RestApiBindAddress", "127.0.0.1");
            IniFileSetting.Write("RestApiPort", "0");
            _application.Reinitialize();
         }
      }

      [Test]
      [Description("The IPv4 configuration every existing install runs is untouched by the IPv6 support")]
      public void RestApiIpv4ConfigurationStillWorksUnchanged()
      {
         _settings.SetAdministratorPassword(AdminPassword);

         IniFileSetting.Write("RestApiBindAddress", "127.0.0.1");
         IniFileSetting.Write("RestApiPort", RestPort.ToString());

         try
         {
            _application.Reinitialize();

            // The negative control for the family dispatch: the same fixture
            // that proves ::1 works proves 127.0.0.1 still does, so a
            // regression in either direction fails here rather than in a
            // fixture that was never about address families.
            (int status, string body) probe = HttpGet("127.0.0.1", RestPort, "/api/v1/status", BasicAdminHeader());

            Assert.AreEqual(200, probe.status, "Body: " + probe.body);
         }
         finally
         {
            IniFileSetting.Write("RestApiPort", "0");
            _application.Reinitialize();
         }
      }

      [Test]
      [Description("A ::1 metrics bind is loopback for the credential gate: /metrics is open without a credential, exactly as on 127.0.0.1")]
      public void MetricsIpv6LoopbackBindIsLoopbackForTheCredentialGate()
      {
         IniFileSetting.Write("MetricsServerBindAddress", "::1");
         IniFileSetting.Write("MetricsServerPort", MetricsPort.ToString());
         IniFileSetting.Write("MetricsServerAuthToken", "");

         try
         {
            _application.Reinitialize();

            // 200 and the exposition, not 503. Against a build that fails to
            // classify ::1 as loopback this answers 503 "credential required"
            // for a bind no remote client can reach - a false refusal that
            // teaches operators the loopback rules are IPv4 folklore.
            (int status, string body) metrics = HttpGet("::1", MetricsPort, "/metrics", null);

            Assert.AreEqual(200, metrics.status,
               "A ::1 bind is loopback; /metrics must be served without a credential. Body: " + metrics.body);
            StringAssert.Contains("hmailserver_", metrics.body);

            // The probes, as always.
            Assert.AreEqual(200, HttpGet("::1", MetricsPort, "/livez", null).status);
         }
         finally
         {
            IniFileSetting.Write("MetricsServerBindAddress", "127.0.0.1");
            IniFileSetting.Write("MetricsServerPort", "0");
            _application.Reinitialize();
         }
      }

      [Test]
      [Description("A :: bind is dual-stack - one listener serves IPv4 and IPv6 clients alike")]
      public void MetricsAnyBindServesBothFamilies()
      {
         const string token = "ipv6-dual-stack-scrape-token";

         IniFileSetting.Write("MetricsServerBindAddress", "::");
         IniFileSetting.Write("MetricsServerPort", MetricsAnyPort.ToString());

         // :: is not loopback, so rule 2 closes /metrics without a
         // credential; the token keeps the exposition scrapeable so both
         // families can be asserted against the real endpoint.
         IniFileSetting.Write("MetricsServerAuthToken", token);

         try
         {
            _application.Reinitialize();

            // The dual-stack assertion: the SAME listener answers over both
            // families. On Windows an AF_INET6 socket is v6-only unless
            // IPV6_V6ONLY is cleared, so the IPv4 half of this fails against
            // a build that binds :: without clearing it.
            Assert.AreEqual(200, HttpGet("::1", MetricsAnyPort, "/livez", null).status,
               "An IPv6 client must reach the :: bind.");
            Assert.AreEqual(200, HttpGet("127.0.0.1", MetricsAnyPort, "/livez", null).status,
               "An IPv4 client must reach the :: bind - dual-stack is the documented behaviour of ::.");

            Assert.AreEqual(200, HttpGet("::1", MetricsAnyPort, "/metrics", "Bearer " + token).status);
            Assert.AreEqual(200, HttpGet("127.0.0.1", MetricsAnyPort, "/metrics", "Bearer " + token).status);

            // And the credential is REQUIRED: a request without one is
            // refused. :: must not be mistaken for loopback by the credential
            // gate just because it now parses - that mistake would open an
            // unauthenticated exposition on every interface.
            Assert.AreEqual(401, HttpGet("::1", MetricsAnyPort, "/metrics", null).status,
               "A :: bind is not loopback; /metrics must demand the configured credential.");
         }
         finally
         {
            IniFileSetting.Write("MetricsServerBindAddress", "127.0.0.1");
            IniFileSetting.Write("MetricsServerPort", "0");
            IniFileSetting.Write("MetricsServerAuthToken", "");
            _application.Reinitialize();
         }
      }

      [Test]
      [Description("A :: metrics bind with NO credential still closes /metrics: dual-stack any is not loopback")]
      public void MetricsAnyBindWithoutCredentialStillClosesMetrics()
      {
         IniFileSetting.Write("MetricsServerBindAddress", "::");
         IniFileSetting.Write("MetricsServerPort", MetricsAnyPort.ToString());
         IniFileSetting.Write("MetricsServerAuthToken", "");

         try
         {
            _application.Reinitialize();

            // The other direction of the loopback question, and the one that
            // would silently weaken the server if it regressed: making ::
            // bindable must not have made it count as loopback. 503 with the
            // remediation body, probes open - rule 2 exactly as on 0.0.0.0.
            (int status, string body) metrics = HttpGet("::1", MetricsAnyPort, "/metrics", null);

            Assert.AreEqual(503, metrics.status,
               "A :: bind with no credential must close /metrics, exactly as 0.0.0.0 does. Body: " + metrics.body);
            StringAssert.Contains("metrics unavailable", metrics.body);

            Assert.AreEqual(200, HttpGet("::1", MetricsAnyPort, "/livez", null).status,
               "The health probes answer in every configuration.");
            Assert.AreEqual(200, HttpGet("127.0.0.1", MetricsAnyPort, "/livez", null).status,
               "Over both families.");
         }
         finally
         {
            IniFileSetting.Write("MetricsServerBindAddress", "127.0.0.1");
            IniFileSetting.Write("MetricsServerPort", "0");
            _application.Reinitialize();
         }
      }

      [Test]
      [Description("The web-services listener serves autoconfig over an IPv6 loopback bind")]
      public void WebServicesIpv6LoopbackBindServesAutoconfig()
      {
         IniFileSetting.Write("WebServicesBindAddress", "::1");
         IniFileSetting.Write("WebServicesHttpPort", WebPort.ToString());

         try
         {
            _application.Reinitialize();

            (int status, string body) response =
               HttpGet("::1", WebPort, "/mail/config-v1.1.xml", null, "example.test");

            Assert.AreEqual(200, response.status, "Body: " + response.body);
            StringAssert.Contains("clientConfig", response.body,
               "The Thunderbird autoconfig XML must be served over IPv6. Body: " + response.body);
         }
         finally
         {
            IniFileSetting.Write("WebServicesBindAddress", "0.0.0.0");
            IniFileSetting.Write("WebServicesHttpPort", "0");
            _application.Reinitialize();
         }
      }
   }
}
