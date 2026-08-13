// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    Two properties of the metrics listener that looked right and were not.
   ///
   ///    ONE. /readyz answered 200 for a server whose database was unreachable.
   ///    Readiness asked DatabaseConnectionManager::GetIsConnected(), which counts
   ///    connection OBJECTS in the pool - not whether any of them can still reach a
   ///    server. The pool is built once at start-up and nothing empties it when the
   ///    database goes away, so that function keeps answering "connected" throughout
   ///    an outage, /readyz keeps answering 200, and the load balancer that
   ///    docs/HighAvailabilityRunbook.md section 3 tells operators to drive from
   ///    /readyz keeps routing mail to a node that cannot look up a single recipient.
   ///    Every message it accepts is then rejected or deferred. A readiness probe
   ///    that cannot go unready is not a readiness probe, and
   ///    hmailserver_database_connected had the same defect, so an operator alerting
   ///    on it had an alert that could not fire.
   ///
   ///    Readiness now rests on a real round trip - one "select * from hm_dbversion"
   ///    - and, just as importantly, that round trip happens on a SECOND thread
   ///    rather than inside the request. That part is not tidiness. A query goes
   ///    through DatabaseConnectionManager::GetConnection_, which waits up to
   ///    [Settings] DBConnectionAcquireTimeout - sixty seconds by default - for a
   ///    pooled connection, so putting it on the single-threaded listener would park
   ///    /livez behind it for a minute. Three failed liveness probes is a killed
   ///    container: a transient database stall would restart a healthy mail server,
   ///    and the restart would make the stall worse. Which is exactly what the two
   ///    hm_messages aggregates behind /metrics used to do, and why they moved too.
   ///
   ///    TWO. /healthz published the session counts. Those are among the things the
   ///    503 on /metrics exists to withhold - the listener refuses to serve
   ///    "delivery-queue depth, session counts, authentication-failure counts and the
   ///    build and schema versions" to an unauthenticated caller on a non-loopback
   ///    bind - and then /healthz, which is deliberately open and unauthenticated in
   ///    EVERY configuration because a load balancer has nowhere to keep a
   ///    credential, handed the session counts out anyway. In the runbook's own
   ///    topology (0.0.0.0, plain HTTP, no credential) anyone who could reach the
   ///    port could poll /healthz and watch this server's SMTP, IMAP and POP3
   ///    concurrency. An access-control rule that one path enforces and another path
   ///    hands out is not a rule.
   ///
   ///    Both tests fail against the build before this work, and each says how.
   ///
   ///    Every test restores the shipped configuration in a finally block and the
   ///    fixture does it again in a OneTimeTearDown, because these settings live in
   ///    hMailServer.ini rather than the database and the whole suite runs against
   ///    one live service - a leftover MetricsServerBindAddress of 0.0.0.0 would turn
   ///    HealthProbes, PrometheusConventions, DatabaseMetrics and DeliveryMetrics into
   ///    503s that have nothing to do with what they test.
   /// </summary>
   [TestFixture]
   public class MetricsReadiness : TestFixtureBase
   {
      // 9560-9569 is this fixture's allocated range. A port per concern, so a
      // listener left behind by a failing test cannot be mistaken for the listener
      // the next test is asserting about.
      private const int ReadinessPort = 9560;
      private const int LeakPort = 9561;

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

      // Writes every setting these tests depend on, so each of them states its whole
      // configuration instead of inheriting whatever the previous one left behind.
      private void ConfigureListener(int port, string bindAddress)
      {
         WriteSetting("MetricsServerBindAddress", bindAddress);
         WriteSetting("MetricsServerPort", port.ToString());
         WriteSetting("MetricsServerAuthToken", "");
         WriteSetting("MetricsServerAuthUsername", "");
         WriteSetting("MetricsServerAuthPassword", "");
         WriteSetting("MetricsServerCertificateFile", "");
         WriteSetting("MetricsServerPrivateKeyFile", "");
      }

      private void RestoreDefaults()
      {
         ConfigureListener(port: 0, bindAddress: "127.0.0.1");

         _application.Reinitialize();
      }

      [OneTimeTearDown]
      public void MetricsReadinessFixtureTearDown()
      {
         RestoreDefaults();
      }

      private sealed class HttpResult
      {
         public int Status;
         public string Headers = "";
         public string Body = "";
      }

      private static HttpResult HttpGet(int port, string path)
      {
         using (TcpClient client = ConnectWithRetry(port))
         {
            // Bounded, so a listener that answers nothing fails this test rather than
            // hanging the whole suite. Comfortably longer than the server's own
            // five-second request deadline.
            client.ReceiveTimeout = 20000;

            using (NetworkStream stream = client.GetStream())
            using (var memory = new MemoryStream())
            {
               byte[] request = Encoding.ASCII.GetBytes(
                  "GET " + path + " HTTP/1.0\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");

               stream.Write(request, 0, request.Length);
               stream.Flush();

               byte[] buffer = new byte[4096];
               int read;
               while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                  memory.Write(buffer, 0, read);

               string raw = Encoding.UTF8.GetString(memory.ToArray());

               var result = new HttpResult();

               string[] lines = raw.Split(new[] { "\r\n" }, StringSplitOptions.None);
               if (lines.Length > 0)
               {
                  string[] parts = lines[0].Split(' ');
                  if (parts.Length >= 2)
                  {
                     int status;
                     if (int.TryParse(parts[1], out status))
                        result.Status = status;
                  }
               }

               int separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
               if (separator >= 0)
               {
                  result.Headers = raw.Substring(0, separator);
                  result.Body = raw.Substring(separator + 4);
               }
               else
               {
                  result.Headers = raw;
               }

               return result;
            }
         }
      }

      // Connects, absorbing the bind race immediately after a service restart. A
      // fresh socket per attempt: a TcpClient whose Connect has thrown is not
      // reliably reusable.
      private static TcpClient ConnectWithRetry(int port)
      {
         SocketException last = null;

         for (int attempt = 0; attempt < 25; attempt++)
         {
            var client = new TcpClient();

            try
            {
               client.Connect("127.0.0.1", port);
               return client;
            }
            catch (SocketException ex)
            {
               last = ex;
               client.Close();
               Thread.Sleep(200);
            }
         }

         throw last;
      }

      // Value of one exact series identity in an exposition body, or NaN when absent.
      private static double ParseSeries(string body, string identity)
      {
         foreach (string raw in body.Split('\n'))
         {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
               continue;

            int space = line.LastIndexOf(' ');
            if (space <= 0 || space == line.Length - 1)
               continue;

            if (line.Substring(0, space) != identity)
               continue;

            double parsed;
            if (double.TryParse(line.Substring(space + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
               return parsed;
         }

         return double.NaN;
      }

      // The "# HELP <family> ..." text for a family, or null when absent.
      private static string ParseHelp(string body, string family)
      {
         string prefix = "# HELP " + family + " ";

         foreach (string raw in body.Split('\n'))
         {
            string line = raw.Trim();
            if (line.StartsWith(prefix, StringComparison.Ordinal))
               return line.Substring(prefix.Length);
         }

         return null;
      }

      [Test]
      [Description("Readiness is proved by a database round trip that a second thread performs, not by the connection pool holding objects")]
      public void DatabaseReadinessRestsOnARoundTripRefreshedOffTheRequestThread()
      {
         ConfigureListener(port: ReadinessPort, bindAddress: "127.0.0.1");

         // The AssertNoReportedError at the end is about this test's own scrapes and
         // about the new refresh thread's exception barrier (HM6045), so the log has
         // to start empty rather than carrying whatever an earlier fixture left.
         LogHandler.DeleteErrorLog();

         _application.Reinitialize();

         try
         {
            HttpResult first = HttpGet(ReadinessPort, "/metrics");
            Assert.AreEqual(200, first.Status, "Metrics should be 200. Response: " + first.Headers + first.Body);

            // ---- the probe is exposed at all -----------------------------------
            // Neither series exists in the build before this work, because nothing
            // ever asked the database whether it could still answer.
            double firstTimestamp = ParseSeries(first.Body, "hmailserver_database_probe_success_timestamp_seconds");
            Assert.IsFalse(double.IsNaN(firstTimestamp),
               "hmailserver_database_probe_success_timestamp_seconds is missing. Without it there is no way to tell " +
               "from monitoring whether this node has ever proved it can reach the database, which is the whole " +
               "difference between /readyz meaning something and /readyz meaning \"the process is alive\". Body: " +
               first.Body);

            double firstAge = ParseSeries(first.Body, "hmailserver_database_probe_age_seconds");
            Assert.IsFalse(double.IsNaN(firstAge),
               "hmailserver_database_probe_age_seconds is missing. Body: " + first.Body);

            // A plausible Unix timestamp, not an elapsed-seconds counter.
            double nowUnix = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

            Assert.Greater(firstTimestamp, 1735689600.0,
               "hmailserver_database_probe_success_timestamp_seconds must be a Unix timestamp (after 2025-01-01). Body: " +
               first.Body);
            Assert.LessOrEqual(firstTimestamp, nowUnix + 120,
               "hmailserver_database_probe_success_timestamp_seconds must not be in the future. Body: " + first.Body);

            // ---- the connected gauge means the round trip ----------------------
            Assert.AreEqual(1.0, ParseSeries(first.Body, "hmailserver_database_connected"),
               "hmailserver_database_connected should be 1 during the suite. Body: " + first.Body);

            string connectedHelp = ParseHelp(first.Body, "hmailserver_database_connected");
            Assert.IsNotNull(connectedHelp,
               "hmailserver_database_connected has no HELP line. Body: " + first.Body);
            Assert.IsTrue(connectedHelp.Contains("round trip"),
               "hmailserver_database_connected must document that it means the database ANSWERED, not that the pool " +
               "holds connection objects - the old meaning made an alert on this series one that could never fire. " +
               "HELP: " + connectedHelp);

            // ---- the round trip is NOT on the request path ---------------------
            // This is the assertion that separates the fix from the obvious wrong
            // version of it. If the probe were issued inside the request handler, the
            // age would be zero on every single scrape, because the probe would have
            // completed microseconds earlier - and the handler would inherit the
            // sixty-second connection-acquire wait that makes a database stall look
            // like a dead process to a liveness probe.
            //
            // Sampled over about eleven seconds so at least two of the background
            // refresher's intervals fall inside the window.
            var ages = new List<double>();
            var timestamps = new List<double>();

            ages.Add(firstAge);
            timestamps.Add(firstTimestamp);

            for (int sample = 0; sample < 13; sample++)
            {
               Thread.Sleep(800);

               HttpResult scrape = HttpGet(ReadinessPort, "/metrics");
               Assert.AreEqual(200, scrape.Status, "Metrics should be 200. Response: " + scrape.Headers + scrape.Body);

               double age = ParseSeries(scrape.Body, "hmailserver_database_probe_age_seconds");
               double timestamp = ParseSeries(scrape.Body, "hmailserver_database_probe_success_timestamp_seconds");

               Assert.IsFalse(double.IsNaN(age),
                  "hmailserver_database_probe_age_seconds disappeared between scrapes. Body: " + scrape.Body);
               Assert.IsFalse(double.IsNaN(timestamp),
                  "hmailserver_database_probe_success_timestamp_seconds disappeared between scrapes. Body: " + scrape.Body);

               ages.Add(age);
               timestamps.Add(timestamp);
            }

            double maxAge = 0;
            foreach (double age in ages)
            {
               if (age > maxAge)
                  maxAge = age;
            }

            Assert.GreaterOrEqual(maxAge, 1.0,
               "hmailserver_database_probe_age_seconds was zero on every one of " + ages.Count + " scrapes, which means " +
               "the database round trip is being made by the request itself. It must be made by the background " +
               "refresher instead: a query on the listener thread waits up to DBConnectionAcquireTimeout (sixty " +
               "seconds by default) for a pooled connection, and /livez queues behind it - three failed liveness " +
               "probes and an orchestrator kills a mail server that was perfectly healthy. Ages: " +
               string.Join(", ", ages.ConvertAll(a => a.ToString(CultureInfo.InvariantCulture)).ToArray()));

            // And it must not be wedged: an age that only grows means the refresher
            // stopped completing round trips, which is the state the staleness ceiling
            // in /readyz exists to catch.
            Assert.LessOrEqual(maxAge, 60.0,
               "hmailserver_database_probe_age_seconds reached " + maxAge + " seconds, so the background probe is not " +
               "completing. Ages: " +
               string.Join(", ", ages.ConvertAll(a => a.ToString(CultureInfo.InvariantCulture)).ToArray()));

            // The timestamp advanced, and never went backwards: the refresher is alive
            // and each of its round trips really reached the database.
            for (int i = 1; i < timestamps.Count; i++)
            {
               Assert.GreaterOrEqual(timestamps[i], timestamps[i - 1],
                  "hmailserver_database_probe_success_timestamp_seconds went backwards between scrapes " + (i - 1) +
                  " and " + i + ". Timestamps: " +
                  string.Join(", ", timestamps.ConvertAll(t => t.ToString(CultureInfo.InvariantCulture)).ToArray()));
            }

            Assert.Greater(timestamps[timestamps.Count - 1], timestamps[0],
               "hmailserver_database_probe_success_timestamp_seconds did not advance across eleven seconds, so nothing " +
               "is re-proving that the database can answer. Readiness would then be frozen on whatever was true when " +
               "the listener started. Timestamps: " +
               string.Join(", ", timestamps.ConvertAll(t => t.ToString(CultureInfo.InvariantCulture)).ToArray()));

            // ---- and the probes agree with it ----------------------------------
            HttpResult ready = HttpGet(ReadinessPort, "/readyz");
            Assert.AreEqual(200, ready.Status,
               "/readyz must be 200 while the server is running and the database is answering. Response: " +
               ready.Headers + ready.Body);
            Assert.IsTrue(ready.Body.Contains("ready"), "/readyz body: " + ready.Body);

            HttpResult health = HttpGet(ReadinessPort, "/healthz");
            Assert.AreEqual(200, health.Status, "/healthz must be 200. Response: " + health.Headers + health.Body);
            Assert.IsTrue(health.Body.Contains("\"database\":\"up\""),
               "/healthz must report the database up, and \"up\" must now mean it answered. Body: " + health.Body);

            // The refresh thread is the top of a raw std::thread, so an exception that
            // escaped it would be std::terminate() and a dead mail server. Its barrier
            // reports HM6045; nothing above may have fired it.
            CustomAsserts.AssertNoReportedError();
         }
         finally
         {
            RestoreDefaults();
            LogHandler.DeleteErrorLog();
         }
      }

      [Test]
      [Description("The always-open health probe must not publish the session counts that /metrics answers 503 to withhold")]
      public void HealthProbeDoesNotPublishTheSessionCountsMetricsRefuses()
      {
         ConfigureListener(port: LeakPort, bindAddress: "127.0.0.1");

         _application.Reinitialize();

         try
         {
            // Phase one, loopback: the omission is unconditional, so it holds here too.
            // Asserted first because it is the cheap half and because it makes the
            // failure output unambiguous - if this fails, the body simply still carries
            // the counts and no bind-address behaviour is involved.
            HttpResult health = HttpGet(LeakPort, "/healthz");
            Assert.AreEqual(200, health.Status, "/healthz must be 200. Response: " + health.Headers + health.Body);
            Assert.IsTrue(health.Body.Contains("\"status\":\"ok\""), "/healthz body: " + health.Body);
            Assert.IsTrue(health.Body.Contains("\"state\":\"running\""), "/healthz body: " + health.Body);
            Assert.IsTrue(health.Body.Contains("\"database\":\"up\""), "/healthz body: " + health.Body);
            Assert.IsTrue(health.Body.Contains("\"uptime_seconds\""), "/healthz body: " + health.Body);

            Assert.IsFalse(health.Body.Contains("sessions"),
               "/healthz must not publish the session counts. It is deliberately open and unauthenticated in every " +
               "configuration - a load balancer health check has nowhere to keep a credential - and the session " +
               "counts are among the values /metrics answers 503 to withhold from an unauthenticated caller. A rule " +
               "one path enforces and another hands out is not a rule. Body: " + health.Body);

            // The control: the numbers still exist, behind whatever protection
            // /metrics has. This is what stops the assertion above being satisfied by
            // simply deleting the metric.
            HttpResult metrics = HttpGet(LeakPort, "/metrics");
            Assert.AreEqual(200, metrics.Status, "Metrics should be 200 on a loopback bind with no credential. Response: " +
               metrics.Headers + metrics.Body);
            Assert.IsTrue(metrics.Body.Contains("hmailserver_sessions{protocol=\"smtp\"}"),
               "The session counts must still be published on /metrics - they are a useful metric, they are simply " +
               "not something an unauthenticated probe should hand out. Body: " + metrics.Body);

            // Phase two: the configuration docs/HighAvailabilityRunbook.md section 3
            // ships, verbatim apart from the port - bound where the network can reach
            // it, plain HTTP, no credential. This is the deployment in which the leak
            // actually mattered.
            WriteSetting("MetricsServerBindAddress", "0.0.0.0");
            _application.Reinitialize();

            HttpResult closedMetrics = HttpGet(LeakPort, "/metrics");
            Assert.AreEqual(503, closedMetrics.Status,
               "/metrics must not be served unauthenticated on a non-loopback bind. Response: " +
               closedMetrics.Headers + closedMetrics.Body);
            Assert.IsFalse(closedMetrics.Body.Contains("hmailserver_"),
               "A refused /metrics must not carry any part of the exposition. Body: " + closedMetrics.Body);

            // The probes stay open - that is rule 1 and it outranks everything else -
            // and now they carry nothing the 503 above was protecting.
            HttpResult exposedLive = HttpGet(LeakPort, "/livez");
            Assert.AreEqual(200, exposedLive.Status,
               "/livez must answer without a credential on a non-loopback bind. Response: " +
               exposedLive.Headers + exposedLive.Body);

            HttpResult exposedReady = HttpGet(LeakPort, "/readyz");
            Assert.AreEqual(200, exposedReady.Status,
               "/readyz must answer without a credential on a non-loopback bind - it is the probe the high " +
               "availability runbook points the VIP health check at. Response: " +
               exposedReady.Headers + exposedReady.Body);

            HttpResult exposedHealth = HttpGet(LeakPort, "/healthz");
            Assert.AreEqual(200, exposedHealth.Status,
               "/healthz must answer without a credential on a non-loopback bind. Response: " +
               exposedHealth.Headers + exposedHealth.Body);
            Assert.IsTrue(exposedHealth.Body.Contains("\"status\":\"ok\""),
               "/healthz body: " + exposedHealth.Body);

            Assert.IsFalse(exposedHealth.Body.Contains("sessions"),
               "This is the configuration that made the leak matter: /metrics answered 503 here specifically so that " +
               "session counts would not be readable by anyone who can reach the port, and /healthz published them " +
               "anyway - so this server's SMTP, IMAP and POP3 concurrency was pollable by an anonymous caller on a " +
               "routed network. Body: " + exposedHealth.Body);
         }
         finally
         {
            RestoreDefaults();
         }
      }
   }
}
