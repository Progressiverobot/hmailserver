// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    Exercises the oper/observability health probes served on the metrics
   ///    listener (hMailServer.ini [Settings] MetricsServerPort): the Kubernetes-style
   ///    /livez (liveness), /readyz (readiness) and /healthz (JSON health) endpoints,
   ///    alongside the existing /metrics endpoint.
   /// </summary>
   [TestFixture]
   public class HealthProbes : TestFixtureBase
   {
      private const int MetricsPort = 9099;

      [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
      private static extern bool WritePrivateProfileString(string section, string key, string value, string filePath);

      private void WriteSetting(string key, string value)
      {
         string programDirectory = _application.Settings.Directories.ProgramDirectory;
         string[] candidates =
         {
            Path.Combine(programDirectory, "hMailServer.ini"),
            Path.Combine(programDirectory, "Bin", "hMailServer.ini"),
         };

         bool wroteAny = false;
         foreach (string iniPath in candidates)
         {
            if (!File.Exists(iniPath))
               continue;

            Assert.IsTrue(
               WritePrivateProfileString("Settings", key, value, iniPath),
               "Failed to write " + key + " to " + iniPath + ".");
            wroteAny = true;
         }

         Assert.IsTrue(wroteAny, "Could not locate an existing hMailServer.ini to update.");
      }

      // Parses a Prometheus counter/gauge value from a /metrics body. Returns -1 if absent.
      private static int ParseCounter(string body, string name)
      {
         foreach (string raw in body.Split('\n'))
         {
            string line = raw.Trim();
            if (line.StartsWith(name + " "))
            {
               string[] parts = line.Split(' ');
               int value;
               if (parts.Length >= 2 && int.TryParse(parts[parts.Length - 1], out value))
                  return value;
            }
         }

         return -1;
      }

      // Issues a minimal HTTP/1.0 GET against the metrics listener and returns the
      // parsed status code and body. Retries the initial connect briefly to absorb
      // the listener bind race right after a server restart.
      private static (int status, string body) HttpGet(string path)
      {
         using (var client = new TcpClient())
         {
            Exception last = null;
            for (int attempt = 0; attempt < 25; attempt++)
            {
               try
               {
                  client.Connect("127.0.0.1", MetricsPort);
                  last = null;
                  break;
               }
               catch (SocketException ex)
               {
                  last = ex;
                  System.Threading.Thread.Sleep(200);
               }
            }
            if (last != null)
               throw last;

            using (NetworkStream stream = client.GetStream())
            using (var memory = new MemoryStream())
            {
               byte[] request = Encoding.ASCII.GetBytes("GET " + path + " HTTP/1.0\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
               stream.Write(request, 0, request.Length);

               byte[] buffer = new byte[4096];
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

      [Test]
      [Description("RFC-agnostic operability: /livez, /readyz, /healthz and /metrics on the metrics listener.")]
      public void TestHealthProbes()
      {
         WriteSetting("MetricsServerBindAddress", "127.0.0.1");
         WriteSetting("MetricsServerPort", MetricsPort.ToString());

         // Reinitialize (not just Stop/Start) so the metrics listener picks up the
         // freshly written MetricsServerPort: the port is cached in IniFileSettings,
         // which is only re-read by InitInstance() during a full reinitialize.
         _application.Reinitialize();

         try
         {
            // Liveness: always 200 once the listener is up (no dependency checks).
            (int status, string body) live = HttpGet("/livez");
            Assert.AreEqual(200, live.status, "Liveness should be 200. Body: " + live.body);
            Assert.IsTrue(live.body.Contains("alive"), "Liveness body: " + live.body);

            // Readiness: server running and database connected -> 200.
            (int status, string body) ready = HttpGet("/readyz");
            Assert.AreEqual(200, ready.status, "Readiness should be 200 when running + DB up. Body: " + ready.body);
            Assert.IsTrue(ready.body.Contains("ready"), "Readiness body: " + ready.body);

            // Health JSON: ok + database up + running.
            (int status, string body) health = HttpGet("/healthz");
            Assert.AreEqual(200, health.status, "Health should be 200. Body: " + health.body);
            Assert.IsTrue(health.body.Contains("\"status\":\"ok\""), "Health body: " + health.body);
            Assert.IsTrue(health.body.Contains("\"database\":\"up\""), "Health body: " + health.body);
            Assert.IsTrue(health.body.Contains("\"state\":\"running\""), "Health body: " + health.body);

            // The existing metrics endpoint still works.
            (int status, string body) metrics = HttpGet("/metrics");
            Assert.AreEqual(200, metrics.status, "Metrics should be 200. Body: " + metrics.body);
            // Renamed to follow Prometheus convention: a start timestamp instead of an
            // uptime counter, and "connected" instead of "up" (which collided with
            // Prometheus's own synthetic up). See PrometheusConventions.cs.
            Assert.IsTrue(metrics.body.Contains("hmailserver_start_time_seconds"), "Metrics body did not contain expected gauge.");
            Assert.IsTrue(metrics.body.Contains("hmailserver_database_connected 1"), "Metrics body should report the database connected. Body: " + metrics.body);
            Assert.IsTrue(metrics.body.Contains("hmailserver_db_connections{state=\"busy\"}"), "Metrics body missing busy pool gauge. Body: " + metrics.body);
            Assert.IsTrue(metrics.body.Contains("hmailserver_db_connections{state=\"available\"}"), "Metrics body missing available pool gauge. Body: " + metrics.body);
            Assert.IsTrue(metrics.body.Contains("hmailserver_tls_handshakes_total"), "Metrics body missing TLS handshake counter. Body: " + metrics.body);
            Assert.IsTrue(metrics.body.Contains("hmailserver_tls_handshake_failures_total"), "Metrics body missing TLS handshake failure counter. Body: " + metrics.body);
            Assert.IsTrue(metrics.body.Contains("hmailserver_auth_success_total"), "Metrics body missing auth success counter. Body: " + metrics.body);
            Assert.IsTrue(metrics.body.Contains("hmailserver_auth_failures_total"), "Metrics body missing auth failure counter. Body: " + metrics.body);
            Assert.IsTrue(metrics.body.Contains("hmailserver_delivery_queue_messages"), "Metrics body missing delivery queue gauge. Body: " + metrics.body);

            // Functional check: a failed authentication increments the auth-failure counter,
            // proving the gauge is wired to the AccountLogon path (not merely emitted).
            int beforeFailures = ParseCounter(metrics.body, "hmailserver_auth_failures_total");
            Assert.GreaterOrEqual(beforeFailures, 0, "auth failure counter should be present and numeric.");

            // The command-processing summary must be present and is wired to the TCP
            // command loop; capture the baseline command count to confirm it advances.
            Assert.IsTrue(metrics.body.Contains("hmailserver_command_processing_seconds_sum"), "Metrics body missing command latency sum. Body: " + metrics.body);
            int beforeCommands = ParseCounter(metrics.body, "hmailserver_command_processing_seconds_count");
            Assert.GreaterOrEqual(beforeCommands, 0, "command count should be present and numeric.");

            Pop3ClientSimulator pop3 = new Pop3ClientSimulator();
            bool loggedOn = pop3.ConnectAndLogon("no-such-user@example.test", "definitely-wrong-password");
            Assert.IsFalse(loggedOn, "A bad login must not succeed.");

            (int status, string body) afterMetrics = HttpGet("/metrics");
            int afterFailures = ParseCounter(afterMetrics.body, "hmailserver_auth_failures_total");
            Assert.Greater(afterFailures, beforeFailures, "auth failure counter should increase after a bad login. Body: " + afterMetrics.body);

            // The bad login issued protocol command lines (USER/PASS/QUIT), so the
            // command-processing counter must have advanced too.
            int afterCommands = ParseCounter(afterMetrics.body, "hmailserver_command_processing_seconds_count");
            Assert.Greater(afterCommands, beforeCommands, "command counter should increase after protocol activity. Body: " + afterMetrics.body);

            // Unknown paths return 404.
            (int status, string body) notFound = HttpGet("/does-not-exist");
            Assert.AreEqual(404, notFound.status, "Unknown path should be 404.");
         }
         finally
         {
            WriteSetting("MetricsServerPort", "0");
            _application.Reinitialize();
         }
      }
   }
}
