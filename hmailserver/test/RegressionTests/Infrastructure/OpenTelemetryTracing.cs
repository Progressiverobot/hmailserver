// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Linq;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    Exercises the dependency-free OpenTelemetry trace exporter
   ///    (hMailServer.ini [Settings] OtelEndpoint). A throwaway in-process HTTP
   ///    collector captures the OTLP/HTTP (protobuf-over-JSON) export, and the test
   ///    asserts that real protocol + database activity produces command and DB
   ///    spans whose SQL is redacted.
   /// </summary>
   [TestFixture]
   public class OpenTelemetryTracing : TestFixtureBase
   {
      // Must not be the metrics listener port (9099): DeliveryMetrics, HealthProbes
      // and DatabaseMetrics point the server's own metrics server at that port, and
      // with NUnit running tests in parallel this collector would fail to bind.
      private const int CollectorPort = 9096;

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

      // A minimal HTTP/1.1 collector that accepts OTLP POSTs and records each body.
      private sealed class OtlpCollector
      {
         private readonly TcpListener _listener;
         private readonly Thread _thread;
         private readonly object _lock = new object();
         private readonly List<string> _bodies = new List<string>();
         private volatile bool _running;

         public OtlpCollector(int port)
         {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _running = true;
            _thread = new Thread(Run) { IsBackground = true };
            _thread.Start();
         }

         public List<string> SnapshotBodies()
         {
            lock (_lock)
               return new List<string>(_bodies);
         }

         private void Run()
         {
            while (_running)
            {
               TcpClient client;
               try
               {
                  client = _listener.AcceptTcpClient();
               }
               catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
               {
                  return; // listener stopped
               }

               try
               {
                  using (client)
                  using (NetworkStream stream = client.GetStream())
                  {
                     string body = ReadRequestBody(stream);
                     if (body != null)
                     {
                        lock (_lock)
                           _bodies.Add(body);
                     }

                     byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                     stream.Write(response, 0, response.Length);
                  }
               }
               catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
               {
                  // Ignore a malformed/aborted connection and keep serving.
               }
            }
         }

         private static string ReadRequestBody(NetworkStream stream)
         {
            using var raw = new MemoryStream();
            byte[] buffer = new byte[4096];
            int headerEnd = -1;

            // Read until we have the full header block.
            while (headerEnd < 0)
            {
               int read = stream.Read(buffer, 0, buffer.Length);
               if (read <= 0)
                  break;
               raw.Write(buffer, 0, read);
               headerEnd = IndexOf(raw.ToArray(), Encoding.ASCII.GetBytes("\r\n\r\n"));
            }

            byte[] all = raw.ToArray();
            if (headerEnd < 0)
               return null;

            string header = Encoding.ASCII.GetString(all, 0, headerEnd);
            int contentLength = 0;
            foreach (string line in header.Split(new[] { "\r\n" }, StringSplitOptions.None)
               .Where(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)))
               int.TryParse(line.Substring("Content-Length:".Length).Trim(), out contentLength);

            int bodyStart = headerEnd + 4;
            int bodyHave = all.Length - bodyStart;
            using var bodyStream = new MemoryStream();
            if (bodyHave > 0)
               bodyStream.Write(all, bodyStart, bodyHave);

            while (bodyStream.Length < contentLength)
            {
               int read = stream.Read(buffer, 0, buffer.Length);
               if (read <= 0)
                  break;
               bodyStream.Write(buffer, 0, read);
            }

            return Encoding.UTF8.GetString(bodyStream.ToArray());
         }

         private static int IndexOf(byte[] haystack, byte[] needle)
         {
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
               bool match = true;
               for (int j = 0; j < needle.Length; j++)
               {
                  if (haystack[i + j] != needle[j])
                  {
                     match = false;
                     break;
                  }
               }
               if (match)
                  return i;
            }
            return -1;
         }

         public void Stop()
         {
            _running = false;
            try { _listener.Stop(); }
            catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { /* Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding. */ }
            try { _thread.Join(2000); }
            catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { /* Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding. */ }
         }
      }

      [Test]
      [Description("With OtelEndpoint configured, the server exports OTLP spans for protocol commands " +
                   "and database queries (SQL redacted) to the collector.")]
      public void TestOtlpSpansAreExported()
      {
         var collector = new OtlpCollector(CollectorPort);

         WriteSetting("OtelEndpoint", "http://127.0.0.1:" + CollectorPort + "/v1/traces");
         WriteSetting("OtelServiceName", "hmailserver-test");
         _application.Reinitialize();

         try
         {
            // Generate real protocol + database activity: account create, SMTP
            // delivery and POP3 retrieval all dispatch commands and run statements.
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "otel@example.test", "test");
            SmtpClientSimulator.StaticSend("sender@external.example", "otel@example.test", "Otel trace", "Body");
            string message = Pop3ClientSimulator.AssertGetFirstMessageText("otel@example.test", "test");
            Assert.IsTrue(message.Contains("Body"), "Message body was not delivered. Got: " + message);

            // The exporter batches and flushes about once a second; poll the collector.
            string combined = "";
            bool sawSpans = false;
            for (int attempt = 0; attempt < 60; attempt++)
            {
               var bodies = collector.SnapshotBodies();
               var sb = new StringBuilder();
               foreach (string b in bodies)
                  sb.Append(b);
               combined = sb.ToString();

               // Wait until the DB span, a per-verb command span and the delivery
               // span (with its terminal milestone event) have all been exported.
               if (combined.Contains("resourceSpans") && combined.Contains("db.query") &&
                   combined.Contains("\"name\":\"RETR\"") && combined.Contains("delivery.delivered"))
               {
                  sawSpans = true;
                  break;
               }

               Thread.Sleep(500);
            }

            Assert.IsTrue(sawSpans,
               "Expected an OTLP export containing a db.query span, per-verb command spans and a delivery span. Captured: " + Truncate(combined));
            Assert.IsTrue(combined.Contains("\"service.name\""),
               "Export should carry the service.name resource attribute. Captured: " + Truncate(combined));
            Assert.IsTrue(combined.Contains("hmailserver-test"),
               "Export should carry the configured service name. Captured: " + Truncate(combined));

            // Command spans are now named after the protocol verb (low-cardinality):
            // the SMTP MAIL command and the POP3 RETR command must both appear.
            Assert.IsTrue(combined.Contains("\"name\":\"MAIL\""),
               "Export should contain a per-verb SMTP MAIL command span. Captured: " + Truncate(combined));
            Assert.IsTrue(combined.Contains("\"name\":\"RETR\""),
               "Export should contain a per-verb POP3 RETR command span. Captured: " + Truncate(combined));
            Assert.IsTrue(combined.Contains("hmailserver.session.id"),
               "Command spans should carry the session-id correlation attribute. Captured: " + Truncate(combined));

            // The delivery span carries milestone events for the message lifecycle.
            Assert.IsTrue(combined.Contains("\"name\":\"delivery\""),
               "Export should contain the per-message delivery span. Captured: " + Truncate(combined));
            Assert.IsTrue(combined.Contains("delivery.start") && combined.Contains("delivery.delivered"),
               "The delivery span should record start and delivered milestone events. Captured: " + Truncate(combined));

            Assert.IsTrue(combined.Contains("\"db.system\""),
               "DB spans should carry the db.system attribute. Captured: " + Truncate(combined));

            // Any SQL string literals must have been redacted to '?' before export:
            // the test account address must never appear verbatim in a db.statement.
            Assert.IsFalse(combined.Contains("otel@example.test"),
               "SQL literals must be redacted; the account address leaked into a span. Captured: " + Truncate(combined));
         }
         finally
         {
            WriteSetting("OtelEndpoint", "");
            _application.Reinitialize();
            collector.Stop();
         }
      }

      private static string Truncate(string value)
      {
         if (string.IsNullOrEmpty(value))
            return "(nothing)";
         return value.Length <= 2000 ? value : value.Substring(0, 2000) + "...";
      }
   }
}
