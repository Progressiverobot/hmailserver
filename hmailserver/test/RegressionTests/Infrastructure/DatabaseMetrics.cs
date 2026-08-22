// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

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
   ///    Exercises the backend-agnostic database query observability exposed on the
   ///    metrics listener (hMailServer.ini [Settings] MetricsServerPort): the
   ///    hmailserver_db_query_seconds summary and the hmailserver_db_slow_queries_total
   ///    counter, fed from the single DatabaseConnectionManager Execute/OpenRecordset
   ///    chokepoint so every statement is measured regardless of database engine.
   /// </summary>
   [TestFixture]
   public class DatabaseMetrics : TestFixtureBase
   {
      private const int MetricsPort = 9097;

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

      // Parses a Prometheus integer counter/gauge value from a /metrics body. Returns -1 if absent.
      private static long ParseCounter(string body, string name)
      {
         foreach (string raw in body.Split('\n'))
         {
            string line = raw.Trim();
            if (line.StartsWith(name + " "))
            {
               string[] parts = line.Split(' ');
               long value;
               if (parts.Length >= 2 && long.TryParse(parts[parts.Length - 1], out value))
                  return value;
            }
         }

         return -1;
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

      [Test]
      [Description("The metrics listener exposes the DB query latency summary and slow-query counter, " +
                   "and the query count advances after real database activity (proving the chokepoint is wired).")]
      public void TestDatabaseQueryMetrics()
      {
         WriteSetting("MetricsServerBindAddress", "127.0.0.1");
         WriteSetting("MetricsServerPort", MetricsPort.ToString());
         _application.Reinitialize();

         try
         {
            (int status, string body) before = HttpGet("/metrics");
            Assert.AreEqual(200, before.status, "Metrics should be 200. Body: " + before.body);

            Assert.IsTrue(before.body.Contains("hmailserver_db_query_seconds_sum"),
               "Metrics body missing DB query latency sum. Body: " + before.body);
            Assert.IsTrue(before.body.Contains("hmailserver_db_query_seconds_count"),
               "Metrics body missing DB query latency count. Body: " + before.body);
            Assert.IsTrue(before.body.Contains("hmailserver_db_slow_queries_total"),
               "Metrics body missing DB slow-query counter. Body: " + before.body);

            long beforeCount = ParseCounter(before.body, "hmailserver_db_query_seconds_count");
            Assert.GreaterOrEqual(beforeCount, 0, "DB query count should be present and numeric.");

            // Generate real database activity: create an account and deliver a message
            // to it, then read it back. Each step issues multiple statements through the
            // connection manager.
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "dbmetrics@example.test", "test");
            SmtpClientSimulator.StaticSend("sender@external.example", "dbmetrics@example.test", "DB metrics", "Body");
            string message = Pop3ClientSimulator.AssertGetFirstMessageText("dbmetrics@example.test", "test");
            Assert.IsTrue(message.Contains("Body"), "Message body was not delivered. Got: " + message);

            (int status, string body) after = HttpGet("/metrics");
            long afterCount = ParseCounter(after.body, "hmailserver_db_query_seconds_count");
            Assert.Greater(afterCount, beforeCount,
               "DB query count should increase after database activity. Body: " + after.body);

            // The slow-query counter is present and never goes backwards.
            long slowQueries = ParseCounter(after.body, "hmailserver_db_slow_queries_total");
            Assert.GreaterOrEqual(slowQueries, 0, "Slow-query counter should be present and numeric.");
         }
         finally
         {
            WriteSetting("MetricsServerPort", "0");
            WriteSetting("SlowQueryLogMilliseconds", "0");
            _application.Reinitialize();
         }
      }
   }
}
