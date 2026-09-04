// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    Exercises W3C Trace Context (traceparent / tracestate) ingestion and
   ///    propagation on SMTP, where the context travels as a MESSAGE header. A
   ///    throwaway in-process OTLP collector captures the trace export, and each
   ///    test asserts two sides of the same coin: what the server exported
   ///    (continuation or rejection of the inbound context) and what it stamped
   ///    on the stored message (the traceparent every downstream hop will see).
   ///
   ///    traceparent is attacker-supplied on these paths, so the rejection tests
   ///    are the point: a malformed or all-zero value must start a fresh local
   ///    trace, must never reach the exporter, must never become the value the
   ///    server passes on - and must never refuse the message that carried it.
   /// </summary>
   [TestFixture]
   public class TraceContext : TestFixtureBase
   {
      // Must not collide with the other collector ports: 9099 is the server's
      // own metrics listener (DeliveryMetrics, HealthProbes, DatabaseMetrics),
      // 9096 is OpenTelemetryTracing's collector, 9097/9098 are taken as well.
      private const int CollectorPort = 9095;

      // Matches exactly the value shape this server may ever emit: version 00,
      // lowercase hex, 32-16-2.
      private static readonly Regex WellFormedTraceparent =
         new Regex("^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$");

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

      /// <summary>
      ///    Runs one test body with tracing pointed at a fresh collector, and
      ///    always puts the configuration back - a leaked OtelEndpoint would have
      ///    every later fixture exporting spans at a port nobody is listening on.
      /// </summary>
      private void RunWithTracing(Action<OtlpCollector> test)
      {
         var collector = new OtlpCollector(CollectorPort);

         WriteSetting("OtelEndpoint", "http://127.0.0.1:" + CollectorPort + "/v1/traces");
         _application.Reinitialize();

         try
         {
            test(collector);
         }
         finally
         {
            WriteSetting("OtelEndpoint", "");
            _application.Reinitialize();
            collector.Stop();
         }
      }

      /// <summary>
      ///    Polls the collector until the combined export bodies satisfy the
      ///    predicate or the deadline passes; either way the combined text is
      ///    returned so the caller's assertions can quote it.
      /// </summary>
      private static string WaitForExport(OtlpCollector collector, Func<string, bool> predicate)
      {
         string combined = "";
         for (int attempt = 0; attempt < 60; attempt++)
         {
            var sb = new StringBuilder();
            foreach (string body in collector.SnapshotBodies())
               sb.Append(body);
            combined = sb.ToString();

            if (predicate(combined))
               return combined;

            Thread.Sleep(500);
         }

         return combined;
      }

      /// <summary>
      ///    The first traceparent header in a retrieved message - the one this
      ///    server prepended, which shadows anything the original sender put
      ///    deeper in the header block and is the value every downstream hop
      ///    parses first.
      /// </summary>
      private static string GetFirstTraceparentHeader(string messageText)
      {
         foreach (string line in messageText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
         {
            if (line.Length == 0)
               break; // end of the header block; a traceparent in the body does not count.

            if (line.StartsWith("traceparent:", StringComparison.OrdinalIgnoreCase))
               return line.Substring("traceparent:".Length).Trim();
         }

         return null;
      }

      private static string BuildMessage(string from, string to, string extraHeaderLines)
      {
         return "From: " + from + "\r\n" +
                "To: " + to + "\r\n" +
                "Subject: Trace context\r\n" +
                extraHeaderLines +
                "\r\n" +
                "Trace context test body\r\n";
      }

      [Test]
      [Description("A valid inbound traceparent message header continues the sender's trace: the exported " +
                   "smtp.receive span carries the inbound trace id and is parented to the inbound span id.")]
      public void TestValidInboundTraceparentContinuesTrace()
      {
         // Minted by the test, so it can only appear in the export if the server
         // actually parsed the header. This is the fixture's negative control:
         // were the parsing inert, every exported trace id would be locally
         // minted, this id would never surface, and this test - not the
         // rejection tests, which inert parsing satisfies trivially - would fail.
         string inboundTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
         string inboundSpanId = "00f067aa0ba902b7";

         RunWithTracing(collector =>
         {
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "trace-valid@example.test", "test");

            SmtpClientSimulator.StaticSendRaw("sender@external.example", "trace-valid@example.test",
               BuildMessage("sender@external.example", "trace-valid@example.test",
                  "traceparent: 00-" + inboundTraceId + "-" + inboundSpanId + "-01\r\n"));

            string message = Pop3ClientSimulator.AssertGetFirstMessageText("trace-valid@example.test", "test");
            Assert.IsTrue(message.Contains("Trace context test body"), "Message was not delivered. Got: " + message);

            string combined = WaitForExport(collector, c =>
               c.Contains("\"traceId\":\"" + inboundTraceId + "\"") && c.Contains("smtp.receive"));

            Assert.IsTrue(combined.Contains("\"traceId\":\"" + inboundTraceId + "\""),
               "The inbound trace id should continue into exported spans; with inert parsing it cannot appear. Captured: " + Truncate(combined));
            Assert.IsTrue(combined.Contains("smtp.receive"),
               "Reception should export an smtp.receive span. Captured: " + Truncate(combined));
            Assert.IsTrue(combined.Contains("\"parentSpanId\":\"" + inboundSpanId + "\""),
               "The smtp.receive span should be parented to the inbound span id. Captured: " + Truncate(combined));

            // The stored message carries this hop's own traceparent first: same
            // trace, a NEW span id - propagation is participation, not an echo.
            string outbound = GetFirstTraceparentHeader(message);
            Assert.IsNotNull(outbound, "The stored message should carry a prepended traceparent header. Got: " + message);
            Assert.IsTrue(WellFormedTraceparent.IsMatch(outbound), "Prepended traceparent is malformed: " + outbound);
            Assert.IsTrue(outbound.Contains(inboundTraceId), "The prepended traceparent should continue the inbound trace: " + outbound);
            Assert.IsFalse(outbound.Contains(inboundSpanId), "The prepended traceparent must carry a new span id, not echo the inbound one: " + outbound);
         });
      }

      [Test]
      [Description("A malformed traceparent is rejected: the message is still delivered, a fresh local trace is " +
                   "started, and the malformed value is neither exported nor propagated onward.")]
      public void TestMalformedTraceparentRejectedAndFreshTraceStarted()
      {
         // Right lengths, invalid alphabet - the shape a validator that only
         // counts characters would wave through.
         string malformedTraceId = "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz";
         string malformed = "00-" + malformedTraceId + "-zzzzzzzzzzzzzzzz-01";

         RunWithTracing(collector =>
         {
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "trace-malformed@example.test", "test");

            SmtpClientSimulator.StaticSendRaw("sender@external.example", "trace-malformed@example.test",
               BuildMessage("sender@external.example", "trace-malformed@example.test",
                  "traceparent: " + malformed + "\r\n"));

            // Rejection must not refuse the message.
            string message = Pop3ClientSimulator.AssertGetFirstMessageText("trace-malformed@example.test", "test");
            Assert.IsTrue(message.Contains("Trace context test body"), "Message was not delivered. Got: " + message);

            // The value the server passes on - the FIRST traceparent, which is
            // what any downstream parser reads - is freshly minted and valid,
            // never the malformed value.
            string outbound = GetFirstTraceparentHeader(message);
            Assert.IsNotNull(outbound, "The stored message should carry a prepended traceparent header. Got: " + message);
            Assert.IsTrue(WellFormedTraceparent.IsMatch(outbound),
               "The traceparent passed onward must be well-formed even when the inbound one was not: " + outbound);
            Assert.IsFalse(outbound.Contains(malformedTraceId), "The malformed value must not be propagated onward: " + outbound);

            // The fresh trace id the server minted must reach the exporter; the
            // malformed value never may.
            string freshTraceId = outbound.Substring(3, 32);
            string combined = WaitForExport(collector, c => c.Contains("\"traceId\":\"" + freshTraceId + "\""));

            Assert.IsTrue(combined.Contains("\"traceId\":\"" + freshTraceId + "\""),
               "The freshly started trace should be exported. Captured: " + Truncate(combined));
            Assert.IsFalse(combined.Contains(malformedTraceId),
               "A rejected traceparent must never reach the exporter. Captured: " + Truncate(combined));
         });
      }

      [Test]
      [Description("An all-zero trace id is hex-valid but semantically invalid, and is rejected exactly as a " +
                   "malformed value is: fresh trace, message delivered.")]
      public void TestAllZeroTraceIdRejected()
      {
         string zeroTraceId = "00000000000000000000000000000000";

         RunWithTracing(collector =>
         {
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "trace-zero@example.test", "test");

            SmtpClientSimulator.StaticSendRaw("sender@external.example", "trace-zero@example.test",
               BuildMessage("sender@external.example", "trace-zero@example.test",
                  "traceparent: 00-" + zeroTraceId + "-abcdef1234567890-01\r\n"));

            string message = Pop3ClientSimulator.AssertGetFirstMessageText("trace-zero@example.test", "test");
            Assert.IsTrue(message.Contains("Trace context test body"), "Message was not delivered. Got: " + message);

            string outbound = GetFirstTraceparentHeader(message);
            Assert.IsNotNull(outbound, "The stored message should carry a prepended traceparent header. Got: " + message);
            Assert.IsTrue(WellFormedTraceparent.IsMatch(outbound), "Prepended traceparent is malformed: " + outbound);
            Assert.IsFalse(outbound.Contains(zeroTraceId),
               "An all-zero inbound trace id must not be continued: " + outbound);

            string freshTraceId = outbound.Substring(3, 32);
            string combined = WaitForExport(collector, c => c.Contains("\"traceId\":\"" + freshTraceId + "\""));

            Assert.IsTrue(combined.Contains("\"traceId\":\"" + freshTraceId + "\""),
               "The freshly started trace should be exported. Captured: " + Truncate(combined));
            Assert.IsFalse(combined.Contains("\"traceId\":\"" + zeroTraceId + "\""),
               "The all-zero trace id must never appear in exported spans. Captured: " + Truncate(combined));
         });
      }

      [Test]
      [Description("A message that arrives with no trace context leaves with a well-formed traceparent whose span " +
                   "the server exported - outbound emission, verified end to end.")]
      public void TestOutboundEmissionIsWellFormed()
      {
         RunWithTracing(collector =>
         {
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "trace-outbound@example.test", "test");

            SmtpClientSimulator.StaticSendRaw("sender@external.example", "trace-outbound@example.test",
               BuildMessage("sender@external.example", "trace-outbound@example.test", ""));

            string message = Pop3ClientSimulator.AssertGetFirstMessageText("trace-outbound@example.test", "test");

            string outbound = GetFirstTraceparentHeader(message);
            Assert.IsNotNull(outbound, "The stored message should carry a prepended traceparent header. Got: " + message);
            Assert.IsTrue(WellFormedTraceparent.IsMatch(outbound), "Prepended traceparent is malformed: " + outbound);

            string traceId = outbound.Substring(3, 32);
            string spanId = outbound.Substring(36, 16);
            Assert.AreNotEqual("00000000000000000000000000000000", traceId, "Emitted trace id must not be all-zero.");
            Assert.AreNotEqual("0000000000000000", spanId, "Emitted span id must not be all-zero.");

            // No inbound tracestate means none may be invented on the way out.
            Assert.IsFalse(message.IndexOf("tracestate:", StringComparison.OrdinalIgnoreCase) >= 0,
               "No tracestate arrived, so none may be emitted. Got: " + message);

            // The emitted value names a span that was actually exported - a
            // traceparent pointing at a span no collector ever receives would
            // give every downstream hop a broken parent.
            string combined = WaitForExport(collector, c => c.Contains("\"spanId\":\"" + spanId + "\""));
            Assert.IsTrue(combined.Contains("\"spanId\":\"" + spanId + "\""),
               "The span named by the emitted traceparent should itself be exported. Captured: " + Truncate(combined));
            Assert.IsTrue(combined.Contains("\"traceId\":\"" + traceId + "\""),
               "The emitted trace id should appear in the export. Captured: " + Truncate(combined));
         });
      }

      [Test]
      [Description("The inbound sampled flag is information, not a decision: sampled=00 neither suppresses local " +
                   "recording nor leaks into the flag this server emits, so a sender cannot steer what is traced.")]
      public void TestInboundSampledFlagDoesNotGateRecording()
      {
         string inboundTraceId = "a3ce929d0e0e47364bf92f3577b34da6";

         RunWithTracing(collector =>
         {
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "trace-sampled@example.test", "test");

            SmtpClientSimulator.StaticSendRaw("sender@external.example", "trace-sampled@example.test",
               BuildMessage("sender@external.example", "trace-sampled@example.test",
                  "traceparent: 00-" + inboundTraceId + "-00f067aa0ba902b7-00\r\n"));

            string message = Pop3ClientSimulator.AssertGetFirstMessageText("trace-sampled@example.test", "test");

            // sampled=0 must not suppress recording - otherwise it would be an
            // attacker's opt-out from the audit trail. The mirror-image abuse,
            // sampled=1 forcing expensive sampling, cannot arise for the same
            // reason: the flag never feeds the recording decision at all.
            string combined = WaitForExport(collector, c => c.Contains("\"traceId\":\"" + inboundTraceId + "\""));
            Assert.IsTrue(combined.Contains("\"traceId\":\"" + inboundTraceId + "\""),
               "A trace arriving with sampled=00 must still be recorded locally. Captured: " + Truncate(combined));

            // And the flag emitted onward is this server's own recording
            // decision (01), not an echo of the inbound bit.
            string outbound = GetFirstTraceparentHeader(message);
            Assert.IsNotNull(outbound, "The stored message should carry a prepended traceparent header. Got: " + message);
            Assert.IsTrue(WellFormedTraceparent.IsMatch(outbound), "Prepended traceparent is malformed: " + outbound);
            Assert.IsTrue(outbound.Contains(inboundTraceId), "The inbound trace should be continued: " + outbound);
            Assert.IsTrue(outbound.EndsWith("-01"),
               "The emitted sampled flag must be this server's own decision, not the inbound 00: " + outbound);
         });
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
               catch
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
               catch
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
            foreach (string line in header.Split(new[] { "\r\n" }, StringSplitOptions.None))
            {
               if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                  int.TryParse(line.Substring("Content-Length:".Length).Trim(), out contentLength);
            }

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
            catch { }
            try { _thread.Join(2000); }
            catch { }
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
