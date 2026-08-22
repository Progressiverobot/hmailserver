// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    Pins the Prometheus exposition conventions of the /metrics endpoint
   ///    (hMailServer.ini [Settings] MetricsServerPort).
   ///
   ///    The exporter used to get five things wrong, and each of them cost the
   ///    operator something concrete rather than merely being unidiomatic:
   ///
   ///    - hmailserver_state was a numeric enum, so every alert and every dashboard
   ///      panel had to carry its own copy of the 1/2/3/4 mapping, and a wrong copy
   ///      meant "stopping" read as "running".
   ///    - hmailserver_uptime_seconds counted up from zero, so a restart was
   ///      indistinguishable from a scrape gap. A start timestamp makes a restart a
   ///      step change that changes() detects.
   ///    - There was no build/schema identity at all, so "which version is that
   ///      instance on" was not answerable from monitoring.
   ///    - The two latency families were declared "summary" but emitted only _sum
   ///      and _count, so the only derivable statistic was a mean - which is exactly
   ///      the statistic that hides a latency tail.
   ///    - hmailserver_database_up sat next to Prometheus's own synthetic up, and a
   ///      scrape failure looked like a database failure.
   ///
   ///    The most important assertion in here is the last one: that the declared TYPE
   ///    of each latency family is identical across two scrapes taken before and
   ///    after live traffic. An earlier attempt at this work made the histogram
   ///    conditional on an observation having been recorded, so the family announced
   ///    itself as a histogram on an idle server and as a summary once mail flowed.
   ///    A metric family whose type changes at runtime is worse than no histogram:
   ///    Prometheus keeps the first type it saw and silently mis-handles the rest.
   /// </summary>
   [TestFixture]
   public class PrometheusConventions : TestFixtureBase
   {
      private const int MetricsPort = 9101;

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

      // Splits an exposition line into its series identity ("name" or
      // "name{labels}") and its value. Returns false for comments and blank lines.
      private static bool TrySplitSeries(string raw, out string identity, out string value)
      {
         identity = null;
         value = null;

         string line = raw.Trim();
         if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            return false;

         int space = line.LastIndexOf(' ');
         if (space <= 0 || space == line.Length - 1)
            return false;

         identity = line.Substring(0, space);
         value = line.Substring(space + 1);
         return true;
      }

      // Value of one exact series identity, or NaN when the series is absent.
      private static double ParseSeries(string body, string identity)
      {
         foreach (string raw in body.Split('\n'))
         {
            string lineIdentity;
            string lineValue;
            if (!TrySplitSeries(raw, out lineIdentity, out lineValue))
               continue;

            if (lineIdentity != identity)
               continue;

            double parsed;
            if (double.TryParse(lineValue, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
               return parsed;
         }

         return double.NaN;
      }

      // The "# TYPE <family> <type>" line for a family, or null when absent. The
      // trailing space matters: without it "hmailserver_db_query_seconds" would also
      // match a longer family name that starts with it.
      private static string ParseDeclaredType(string body, string family)
      {
         string prefix = "# TYPE " + family + " ";

         foreach (string raw in body.Split('\n'))
         {
            string line = raw.Trim();
            if (line.StartsWith(prefix, StringComparison.Ordinal))
               return line.Substring(prefix.Length).Trim();
         }

         return null;
      }

      // The le bounds and cumulative counts of a histogram family, in the order the
      // exporter emitted them.
      private static List<KeyValuePair<string, double>> ParseBuckets(string body, string family)
      {
         var buckets = new List<KeyValuePair<string, double>>();
         var pattern = new Regex("^" + Regex.Escape(family) + "_bucket\\{le=\"([^\"]+)\"\\}$");

         foreach (string raw in body.Split('\n'))
         {
            string identity;
            string value;
            if (!TrySplitSeries(raw, out identity, out value))
               continue;

            Match match = pattern.Match(identity);
            if (!match.Success)
               continue;

            double parsed;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
               buckets.Add(new KeyValuePair<string, double>(match.Groups[1].Value, parsed));
         }

         return buckets;
      }

      private static void AssertHistogramIsCoherent(string body, string family, string[] expectedBounds)
      {
         Assert.AreEqual("histogram", ParseDeclaredType(body, family),
            family + " must be declared as a histogram, not a summary. Body: " + body);

         List<KeyValuePair<string, double>> buckets = ParseBuckets(body, family);

         var actualBounds = new List<string>();
         foreach (KeyValuePair<string, double> bucket in buckets)
            actualBounds.Add(bucket.Key);

         Assert.AreEqual(expectedBounds, actualBounds.ToArray(),
            family + " bucket bounds must be the compiled-in layout, in ascending order, ending with +Inf. " +
            "The bounds are part of the series identity, so a change here invalidates every stored histogram. Body: " + body);

         // Cumulative buckets never decrease.
         double previous = -1;
         foreach (KeyValuePair<string, double> bucket in buckets)
         {
            Assert.GreaterOrEqual(bucket.Value, previous,
               family + " buckets must be cumulative, but le=\"" + bucket.Key + "\" went backwards. Body: " + body);
            previous = bucket.Value;
         }

         // The +Inf bucket is the total, so it must equal _count. If these disagree,
         // histogram_quantile() interpolates against a total it does not have.
         double infinite = buckets[buckets.Count - 1].Value;
         double count = ParseSeries(body, family + "_count");

         Assert.AreEqual(infinite, count,
            family + "_count must equal the le=\"+Inf\" bucket. Body: " + body);

         Assert.IsFalse(double.IsNaN(ParseSeries(body, family + "_sum")),
            family + "_sum must still be emitted - a histogram carries _sum and _count as well as buckets, " +
            "so anything already deriving a mean from them keeps working. Body: " + body);
      }

      private static readonly string[] CommandLatencyBounds =
      {
         "0.0001", "0.00025", "0.0005", "0.001", "0.0025", "0.005",
         "0.01", "0.025", "0.05", "0.1", "0.5", "1", "+Inf"
      };

      private static readonly string[] DatabaseLatencyBounds =
      {
         "0.001", "0.0025", "0.005", "0.01", "0.025", "0.05",
         "0.1", "0.25", "0.5", "1", "5", "10", "+Inf"
      };

      [Test]
      [Description("The exporter uses StateSet, a start timestamp, _info, histograms and _connected, " +
                   "and exposes queue age and certificate expiry, with no duplicate series")]
      public void ExporterFollowsPrometheusConventions()
      {
         WriteSetting("MetricsServerBindAddress", "127.0.0.1");
         WriteSetting("MetricsServerPort", MetricsPort.ToString());
         _application.Reinitialize();

         try
         {
            // Real traffic first: a delivered message drives both latency families,
            // which is what makes the +Inf bucket assertions below meaningful rather
            // than trivially true against a table of zeroes.
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "prom@example.test", "test");
            SmtpClientSimulator.StaticSend("sender@external.example", "prom@example.test", "Prometheus", "Body");
            string delivered = Pop3ClientSimulator.AssertGetFirstMessageText("prom@example.test", "test");
            Assert.IsTrue(delivered.Contains("Body"), "Message body was not delivered. Got: " + delivered);

            // The AssertNoReportedError below is checking that this test's own scrape
            // did not trip the listener's exception barrier, so the log has to start
            // empty. TestFixtureBase.SetUp deletes only the default log, so an entry
            // left by an earlier fixture would otherwise fail this test with an
            // unrelated message.
            LogHandler.DeleteErrorLog();

            (int status, string body) scrape = HttpGet("/metrics");
            Assert.AreEqual(200, scrape.status, "Metrics should be 200. Body: " + scrape.body);

            string body = scrape.body;

            // ---- 1. hmailserver_state is a StateSet, not a numeric enum ----------
            // Today the exporter emits a bare "hmailserver_state 3", so the labelled
            // series below do not exist at all.
            string[] states = { "unknown", "stopped", "starting", "running", "stopping" };

            int statesSetToOne = 0;
            foreach (string state in states)
            {
               double value = ParseSeries(body, "hmailserver_state{state=\"" + state + "\"}");
               Assert.IsFalse(double.IsNaN(value),
                  "hmailserver_state{state=\"" + state + "\"} is missing. A StateSet emits every state, not only the active one, " +
                  "so a PromQL alert can compare against a state that is currently 0. Body: " + body);
               Assert.IsTrue(value == 0 || value == 1,
                  "hmailserver_state series must be boolean, got " + value + " for " + state + ". Body: " + body);

               if (value == 1)
                  statesSetToOne++;
            }

            Assert.AreEqual(1, statesSetToOne,
               "Exactly one hmailserver_state series must carry 1. Body: " + body);
            Assert.AreEqual(1.0, ParseSeries(body, "hmailserver_state{state=\"running\"}"),
               "The server is running during the suite, so state=\"running\" must be the series set to 1. Body: " + body);

            // The old unlabelled enum must be gone: leaving both would give the same
            // family two shapes and let a dashboard keep reading the magic number.
            Assert.IsTrue(double.IsNaN(ParseSeries(body, "hmailserver_state")),
               "The unlabelled numeric hmailserver_state must no longer be emitted. Body: " + body);

            // ---- 2. start timestamp, not an uptime counter -----------------------
            double startTime = ParseSeries(body, "hmailserver_start_time_seconds");
            Assert.IsFalse(double.IsNaN(startTime),
               "hmailserver_start_time_seconds is missing. Body: " + body);

            double nowUnix = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

            // A plausible Unix timestamp, not an uptime in seconds: an uptime would be
            // a small number and would fail the lower bound here.
            Assert.Greater(startTime, 1735689600.0,
               "hmailserver_start_time_seconds must be a Unix timestamp (after 2025-01-01), not an elapsed-seconds counter. Body: " + body);
            Assert.LessOrEqual(startTime, nowUnix + 120,
               "hmailserver_start_time_seconds must not be in the future. Body: " + body);

            Assert.IsFalse(body.Contains("hmailserver_uptime_seconds"),
               "hmailserver_uptime_seconds must be replaced, not merely supplemented - uptime is now " +
               "time() - hmailserver_start_time_seconds. Body: " + body);

            // ---- 3. build identity via the _info convention ----------------------
            Match buildInfo = Regex.Match(body, @"^hmailserver_build_info\{(?<labels>[^}]*)\} (?<value>\S+)$",
               RegexOptions.Multiline);

            Assert.IsTrue(buildInfo.Success,
               "hmailserver_build_info is missing. Body: " + body);
            Assert.AreEqual("1", buildInfo.Groups["value"].Value.Trim(),
               "An _info metric always carries the value 1; the information is in the labels. Body: " + body);

            string labels = buildInfo.Groups["labels"].Value;
            Assert.IsTrue(Regex.IsMatch(labels, "version=\"[^\"]+\""),
               "hmailserver_build_info needs a non-empty version label. Labels: " + labels);
            Assert.IsTrue(Regex.IsMatch(labels, "database_schema_version=\"[0-9]+\""),
               "hmailserver_build_info needs a numeric database_schema_version label - " +
               "\"which schema is that instance on\" has to be answerable from monitoring. Labels: " + labels);

            // ---- 4. histograms, not mean-only summaries --------------------------
            AssertHistogramIsCoherent(body, "hmailserver_command_processing_seconds", CommandLatencyBounds);
            AssertHistogramIsCoherent(body, "hmailserver_db_query_seconds", DatabaseLatencyBounds);

            // The buckets have to be fed by the real observation path, not declared and
            // left at zero. Traffic above guarantees both families observed something.
            Assert.Greater(ParseSeries(body, "hmailserver_command_processing_seconds_count"), 0.0,
               "The command histogram is declared but never observed - the buckets are not wired to " +
               "ServerStatus::OnCommandProcessed. Body: " + body);
            Assert.Greater(ParseSeries(body, "hmailserver_db_query_seconds_count"), 0.0,
               "The database histogram is declared but never observed - the buckets are not wired to " +
               "ServerStatus::OnDatabaseQuery. Body: " + body);

            // ---- 5. _connected, not _up ------------------------------------------
            Assert.AreEqual(1.0, ParseSeries(body, "hmailserver_database_connected"),
               "hmailserver_database_connected should be 1 during the suite. Body: " + body);
            Assert.IsFalse(body.Contains("hmailserver_database_up"),
               "hmailserver_database_up must be gone: sitting next to Prometheus's own synthetic up, it made a " +
               "scrape failure look like a database failure. Body: " + body);

            // ---- additions: queue age, certificate expiry ------------------------
            double oldestAge = ParseSeries(body, "hmailserver_delivery_queue_oldest_message_age_seconds");
            Assert.IsFalse(double.IsNaN(oldestAge),
               "hmailserver_delivery_queue_oldest_message_age_seconds is missing. Queue depth alone cannot " +
               "distinguish a burst from a stuck relay. Body: " + body);
            Assert.GreaterOrEqual(oldestAge, 0.0,
               "Queue age must never be negative - a clock adjustment must not produce a message from the future. Body: " + body);

            // Certificate expiry cannot be asserted present: a stock test install has
            // no SSL certificate configured, and this fixture deliberately does not add
            // one. What is asserted is the property that made the previous attempt at
            // this metric dangerous - see the duplicate-series check below, which
            // covers the certificate family along with everything else.

            // ---- no duplicate series --------------------------------------------
            // Prometheus rejects an ENTIRE scrape that contains the same series twice,
            // so one duplicated certificate series would take every metric above down
            // with it. This assertion passes against the current code too - there are no
            // duplicates today - so it proves nothing about the fix. It is here as a
            // regression guard, because the failure it guards against is silent from
            // the server's side and total from the operator's.
            var seen = new HashSet<string>();
            foreach (string raw in body.Split('\n'))
            {
               string identity;
               string value;
               if (!TrySplitSeries(raw, out identity, out value))
                  continue;

               Assert.IsTrue(seen.Add(identity),
                  "Duplicate series \"" + identity + "\". Prometheus rejects the whole scrape, not just the " +
                  "duplicate. Body: " + body);
            }

            // The exception barrier at the top of the listener thread must not have
            // fired. If it did, something in the scrape threw - which before the
            // barrier existed was std::terminate() and a dead mail server.
            CustomAsserts.AssertNoReportedError();
         }
         finally
         {
            WriteSetting("MetricsServerPort", "0");
            _application.Reinitialize();
            LogHandler.DeleteErrorLog();
         }
      }

      [Test]
      [Description("The declared type of each latency family is identical before and after traffic - " +
                   "a metric family must never change type at runtime")]
      public void LatencyFamilyTypeDoesNotChangeAtRuntime()
      {
         WriteSetting("MetricsServerBindAddress", "127.0.0.1");
         WriteSetting("MetricsServerPort", MetricsPort.ToString());
         _application.Reinitialize();

         try
         {
            // Same reason as above: AssertNoReportedError at the end of this test is
            // about this test's own scrapes, so the log has to start empty rather than
            // carrying whatever an earlier fixture left behind.
            LogHandler.DeleteErrorLog();

            (int status, string body) first = HttpGet("/metrics");
            Assert.AreEqual(200, first.status, "Metrics should be 200. Body: " + first.body);

            string commandTypeBefore = ParseDeclaredType(first.body, "hmailserver_command_processing_seconds");
            string databaseTypeBefore = ParseDeclaredType(first.body, "hmailserver_db_query_seconds");

            // Drive both families with real work.
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "promtype@example.test", "test");
            SmtpClientSimulator.StaticSend("sender@external.example", "promtype@example.test", "Prometheus type", "Body");
            string delivered = Pop3ClientSimulator.AssertGetFirstMessageText("promtype@example.test", "test");
            Assert.IsTrue(delivered.Contains("Body"), "Message body was not delivered. Got: " + delivered);

            (int status, string body) second = HttpGet("/metrics");

            string commandTypeAfter = ParseDeclaredType(second.body, "hmailserver_command_processing_seconds");
            string databaseTypeAfter = ParseDeclaredType(second.body, "hmailserver_db_query_seconds");

            // The type must be a histogram - this half fails against the current code,
            // which declares both families as summaries.
            Assert.AreEqual("histogram", commandTypeBefore,
               "hmailserver_command_processing_seconds must be a histogram on an idle server too. Body: " + first.body);
            Assert.AreEqual("histogram", databaseTypeBefore,
               "hmailserver_db_query_seconds must be a histogram on an idle server too. Body: " + first.body);

            // And it must not have moved - this half is the regression guard. An
            // earlier attempt emitted a histogram until the first observation and a
            // summary afterwards; Prometheus keeps the first type it saw for the
            // scrape target and silently mis-handles everything after the flip.
            Assert.AreEqual(commandTypeBefore, commandTypeAfter,
               "hmailserver_command_processing_seconds changed its declared type between two scrapes of the same " +
               "process. Before: " + first.body + " After: " + second.body);
            Assert.AreEqual(databaseTypeBefore, databaseTypeAfter,
               "hmailserver_db_query_seconds changed its declared type between two scrapes of the same " +
               "process. Before: " + first.body + " After: " + second.body);

            // Same for the bucket layout: the bounds are part of the series identity,
            // so they must not appear, disappear or reorder as traffic arrives.
            var boundsBefore = new List<string>();
            foreach (KeyValuePair<string, double> bucket in ParseBuckets(first.body, "hmailserver_command_processing_seconds"))
               boundsBefore.Add(bucket.Key);

            var boundsAfter = new List<string>();
            foreach (KeyValuePair<string, double> bucket in ParseBuckets(second.body, "hmailserver_command_processing_seconds"))
               boundsAfter.Add(bucket.Key);

            Assert.AreEqual(boundsBefore.ToArray(), boundsAfter.ToArray(),
               "The command histogram bucket layout changed between two scrapes of the same process. " +
               "Before: " + first.body + " After: " + second.body);

            CustomAsserts.AssertNoReportedError();
         }
         finally
         {
            WriteSetting("MetricsServerPort", "0");
            _application.Reinitialize();
            LogHandler.DeleteErrorLog();
         }
      }
   }
}
