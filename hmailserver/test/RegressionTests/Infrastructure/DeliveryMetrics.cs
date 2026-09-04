// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Linq;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    Exercises the SMTP delivery-outcome counters on the metrics endpoint:
   ///    hmailserver_messages_delivered_total / _deferred_total / _bounced_total,
   ///    incremented from the SMTP delivery threads in <c>SMTPDeliverer</c>.
   /// </summary>
   [TestFixture]
   public class DeliveryMetrics : TestFixtureBase
   {
      private const int MetricsPort = 9099;

      private void WriteSetting(string key, string value)
      {
         string programDirectory = _application.Settings.Directories.ProgramDirectory;
         string[] candidates =
         {
            Paths.Combine(programDirectory, "hMailServer.ini"),
            Paths.Combine(programDirectory, "Bin", "hMailServer.ini"),
         };

         bool wroteAny = false;
         foreach (string iniPath in candidates.Where(File.Exists))
         {
            Assert.IsTrue(
               IniFile.WritePrivateProfileString("Settings", key, value, iniPath),
               "Failed to write " + key + " to " + iniPath + ".");
            wroteAny = true;
         }

         Assert.IsTrue(wroteAny, "Could not locate an existing hMailServer.ini to update.");
      }

      // Parses a Prometheus counter value from a /metrics body. Returns -1 if absent.
      private static int ParseCounter(string body, string name)
      {
         // The sample's value is the last token of the first line for the name that parses.
         return body.Split('\n')
            .Select(raw => raw.Trim())
            .Where(line => line.StartsWith(name + " "))
            .Select(line => int.TryParse(line.Substring(line.LastIndexOf(' ') + 1), out int value) ? value : (int?)null)
            .FirstOrDefault(value => value.HasValue) ?? -1;
      }

      private static string ScrapeMetrics()
      {
         using (TcpClient client = new TcpClient())
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
            using (MemoryStream memory = new MemoryStream())
            {
               byte[] request = Encoding.ASCII.GetBytes("GET /metrics HTTP/1.0\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
               stream.Write(request, 0, request.Length);

               byte[] buffer = new byte[4096];
               int read;
               while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                  memory.Write(buffer, 0, read);

               string raw = Encoding.UTF8.GetString(memory.ToArray());
               int separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
               return separator >= 0 ? raw.Substring(separator + 4) : "";
            }
         }
      }

      [Test]
      [Description("A successfully delivered local message increments hmailserver_messages_delivered_total.")]
      public void TestDeliveredCounterIncrements()
      {
         WriteSetting("MetricsServerBindAddress", "127.0.0.1");
         WriteSetting("MetricsServerPort", MetricsPort.ToString());
         _application.Reinitialize();

         try
         {
            var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "delivery@example.test", "test");

            string baseline = ScrapeMetrics();
            Assert.IsTrue(baseline.Contains("hmailserver_messages_delivered_total"), "Metrics body missing delivered counter. Body: " + baseline);
            Assert.IsTrue(baseline.Contains("hmailserver_messages_deferred_total"), "Metrics body missing deferred counter. Body: " + baseline);
            Assert.IsTrue(baseline.Contains("hmailserver_messages_bounced_total"), "Metrics body missing bounced counter. Body: " + baseline);

            int beforeDelivered = ParseCounter(baseline, "hmailserver_messages_delivered_total");
            Assert.GreaterOrEqual(beforeDelivered, 0, "delivered counter should be present and numeric.");

            SmtpClientSimulator.StaticSend(account.Address, account.Address, "Delivered", "Body");
            Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

            // Delivery completes asynchronously; poll the counter until it advances.
            int afterDelivered = beforeDelivered;
            for (int attempt = 0; attempt < 50; attempt++)
            {
               afterDelivered = ParseCounter(ScrapeMetrics(), "hmailserver_messages_delivered_total");
               if (afterDelivered > beforeDelivered)
                  break;

               System.Threading.Thread.Sleep(200);
            }

            Assert.Greater(afterDelivered, beforeDelivered,
               "delivered counter should increase after a successful local delivery.");
         }
         finally
         {
            WriteSetting("MetricsServerPort", "0");
            _application.Reinitialize();
         }
      }
   }
}
