// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    The metric history: hm_metricsamples, the sampler that fills it, the
   ///    retention that empties it, and the two ways of reading it back.
   ///
   ///    /metrics is a stateless scrape and the Control Panel remembered three
   ///    minutes; until 5 September 2026 nothing stored a sample anywhere. The
   ///    scheduled sampler runs once a minute, which no test should wait for, so the
   ///    fixture drives it through Utilities.SampleMetricsNow - the same code, on
   ///    demand - and reads back through Utilities.GetMetricHistory and through
   ///    GET /api/v1/metrics/history.
   /// </summary>
   [TestFixture]
   public class MetricsHistory : TestFixtureBase
   {
      private const int RestPort = 11421;
      private const string AdminPassword = "testar";

      private static (int status, string body) Http(string path)
      {
         string credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes("Administrator:" + AdminPassword));

         using (var client = new TcpClient())
         {
            Exception last = null;

            for (int attempt = 0; attempt < 25; attempt++)
            {
               try
               {
                  client.Connect("127.0.0.1", RestPort);
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
               byte[] request = Encoding.ASCII.GetBytes(
                  "GET " + path + " HTTP/1.0\r\n" +
                  "Host: 127.0.0.1\r\n" +
                  "Authorization: Basic " + credentials + "\r\n" +
                  "Connection: close\r\n\r\n");
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

      private static double[] Values(string json)
      {
         var values = new System.Collections.Generic.List<double>();

         foreach (Match match in Regex.Matches(json, "\"value\":([-0-9.eE+]+)"))
            values.Add(double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));

         return values.ToArray();
      }

      [Test]
      [Description("One call to SampleMetricsNow writes a row per metric, and the history of one of them reads back as JSON naming it")]
      public void SamplingWritesARowPerMetricAndTheHistoryReadsBack()
      {
         int written = _application.Utilities.SampleMetricsNow();
         ClassicAssert.GreaterOrEqual(written, 14, "A sample is one row per metric; the sampler records fourteen.");

         string json = _application.Utilities.GetMetricHistory("sessions_smtp", 60, 1);

         StringAssert.Contains("\"metric\":\"sessions_smtp\"", json);
         StringAssert.Contains("\"known\":true", json);
         StringAssert.Contains("\"enabled\":true", json);
         StringAssert.Contains("\"samples\":[{", json);
         ClassicAssert.GreaterOrEqual(Values(json).Length, 1, json);
      }

      [Test]
      [Description("A counter is stored as the total it is: the processed-messages total advances by the message delivered between two samples")]
      public void ACounterAdvancesBetweenSamples()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "history@example.test", "test");

         _application.Utilities.SampleMetricsNow();

         SmtpClientSimulator.StaticSend("sender@example.test", account.Address, "Counted", "One message.");
         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         _application.Utilities.SampleMetricsNow();

         // Bucket 0: every sample, not an average - two samples a second apart
         // would otherwise be one point.
         double[] values = Values(_application.Utilities.GetMetricHistory("processed_messages_total", 60, 0));

         ClassicAssert.GreaterOrEqual(values.Length, 2, "Two samples were taken.");
         ClassicAssert.GreaterOrEqual(values[values.Length - 1] - values[0], 1.0,
            "The total must have advanced by at least the one message delivered in between.");
      }

      [Test]
      [Description("An unknown metric name is answered as unknown rather than as an empty history")]
      public void AnUnknownMetricIsSaidToBeUnknown()
      {
         string json = _application.Utilities.GetMetricHistory("no_such_metric", 60, 1);

         StringAssert.Contains("\"known\":false", json);
         StringAssert.Contains("\"samples\":[]", json);
      }

      [Test]
      [Description("Rows older than the retention are pruned; a sample planted in 2020 is gone after the next sampling")]
      public void RowsOlderThanTheRetentionArePruned()
      {
         _application.Database.ExecuteSQL(
            "insert into hm_metricsamples (metricsampletime, metricsamplename, metricsamplevalue) values ('2020-01-01 00:00:00', 'sessions_smtp', 42)");

         // Sampling prunes as the scheduled task does.
         _application.Utilities.SampleMetricsNow();

         string json = _application.Utilities.GetMetricHistory("sessions_smtp", 10 * 365 * 24 * 60, 0);

         StringAssert.DoesNotContain("2020-01-01", json, "A sample older than MetricsHistoryDays must have been pruned.\r\n" + json);
      }

      [Test]
      [Description("GET /api/v1/metrics/history serves the same history, bucketed by the range, and refuses an unknown metric or range with 400")]
      public void TheRestRouteServesTheHistory()
      {
         _settings.SetAdministratorPassword(AdminPassword);
         IniFileSetting.Write("RestApiBindAddress", "127.0.0.1");
         IniFileSetting.Write("RestApiPort", RestPort.ToString());
         _application.Reinitialize();

         try
         {
            _application.Utilities.SampleMetricsNow();

            (int status, string body) ok = Http("/api/v1/metrics/history?metric=sessions_imap&range=24h");
            ClassicAssert.AreEqual(200, ok.status, ok.body);
            StringAssert.Contains("\"metric\":\"sessions_imap\"", ok.body);
            StringAssert.Contains("\"bucket_minutes\":1", ok.body);
            ClassicAssert.GreaterOrEqual(Values(ok.body).Length, 1, ok.body);

            (int status, string body) week = Http("/api/v1/metrics/history?metric=sessions_imap&range=7d");
            ClassicAssert.AreEqual(200, week.status, week.body);
            StringAssert.Contains("\"bucket_minutes\":10", week.body);

            (int status, string body) unknownMetric = Http("/api/v1/metrics/history?metric=nope&range=24h");
            ClassicAssert.AreEqual(400, unknownMetric.status, unknownMetric.body);
            StringAssert.Contains("sessions_smtp", unknownMetric.body, "The refusal lists the names that exist.");

            (int status, string body) unknownRange = Http("/api/v1/metrics/history?metric=sessions_imap&range=1y");
            ClassicAssert.AreEqual(400, unknownRange.status, unknownRange.body);

            (int status, string body) document = Http("/api/v1/openapi.json");
            StringAssert.Contains("/api/v1/metrics/history", document.body, "The route must be in the OpenAPI document.");
         }
         finally
         {
            IniFileSetting.Write("RestApiPort", "0");
            _application.Reinitialize();
         }
      }

      [Test]
      [Description("MetricsHistoryDays=0 turns the sampler off: nothing is written, and the history says it is disabled")]
      public void TheSamplerCanBeTurnedOff()
      {
         ServerIniFile.SetSetting("MetricsHistoryDays", "0");
         RestartServerAndReacquireCom();

         try
         {
            ClassicAssert.AreEqual(-1, _application.Utilities.SampleMetricsNow(), "With the history off, sampling writes nothing and says so.");

            string json = _application.Utilities.GetMetricHistory("sessions_smtp", 60, 1);
            StringAssert.Contains("\"enabled\":false", json);
            StringAssert.Contains("\"retention_days\":0", json);
         }
         finally
         {
            ServerIniFile.SetSetting("MetricsHistoryDays", null);
            RestartServerAndReacquireCom();
         }
      }
   }
}
