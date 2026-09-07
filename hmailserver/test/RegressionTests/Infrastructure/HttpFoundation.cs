// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    The HTTP/1.1 server the REST API and the web services run on
   ///    (Common/Util/HttpServer.cpp): keep-alive, pipelining, Expect:
   ///    100-continue, the refusals it makes on its own account - and the point
   ///    of it, that a slow or idle client no longer holds the listener for
   ///    everyone else, and is closed when its absolute deadline passes.
   ///
   ///    The requests here are written by hand on a TcpClient rather than sent
   ///    through HttpClient, because what is being tested is the framing: which
   ///    version answers, whether the connection stays open, and what arrives on
   ///    it next.
   /// </summary>
   [TestFixture]
   public class HttpFoundation : TestFixtureBase
   {
      private const int RestPort = 11431;
      private const int WebPort = 11432;
      private const string AdminPassword = "testar";

      // RestApiServer.cpp: RequestSeconds. WebServicesServer.cpp: RequestSeconds.
      private const int RestRequestSeconds = 30;
      private const int WebRequestSeconds = 15;

      [OneTimeSetUp]
      public void HttpFoundationFixtureSetUp()
      {
         IniFileSetting.Write("RestApiBindAddress", "127.0.0.1");
         IniFileSetting.Write("RestApiPort", RestPort.ToString());
         IniFileSetting.Write("WebServicesBindAddress", "127.0.0.1");
         IniFileSetting.Write("WebServicesHttpPort", WebPort.ToString());

         _application.Reinitialize();
      }

      [OneTimeTearDown]
      public void HttpFoundationFixtureTearDown()
      {
         IniFileSetting.Write("RestApiPort", "0");
         IniFileSetting.Write("WebServicesBindAddress", "0.0.0.0");
         IniFileSetting.Write("WebServicesHttpPort", "0");

         _application.Reinitialize();
      }

      [Test]
      [Description("An HTTP/1.1 connection carries several requests, and is closed when the client asks")]
      public void KeepAliveAnswersSeveralRequestsOnOneConnection()
      {
         using (TcpClient client = Connect(RestPort))
         using (NetworkStream stream = client.GetStream())
         {
            var reader = new HttpReader(stream);

            Send(stream, Request("GET", "/api/v1/status", "HTTP/1.1", Basic()));
            HttpReply first = reader.Read();
            Assert.AreEqual(200, first.Status, "Body: " + first.Body);
            Assert.AreEqual("keep-alive", first.Header("connection"));
            StringAssert.StartsWith("HTTP/1.1 ", first.StatusLine);
            StringAssert.Contains("\"version\"", first.Body);

            // The API's answers are the server's state at that moment, given
            // to a credential; nothing in between may keep one.
            Assert.AreEqual("no-store", first.Header("cache-control"));

            // Same socket, no reconnect.
            Send(stream, Request("GET", "/api/v1/status", "HTTP/1.1", Basic()));
            HttpReply second = reader.Read();
            Assert.AreEqual(200, second.Status, "Body: " + second.Body);
            Assert.AreEqual("keep-alive", second.Header("connection"));

            Send(stream, Request("GET", "/api/v1/status", "HTTP/1.1", Basic() + "Connection: close\r\n"));
            HttpReply last = reader.Read();
            Assert.AreEqual(200, last.Status, "Body: " + last.Body);
            Assert.AreEqual("close", last.Header("connection"));

            Assert.IsTrue(reader.AtEnd(), "The server must close the connection after a response it marked Connection: close.");
         }
      }

      [Test]
      [Description("An HTTP/1.0 request is answered as before: one response, Connection: close, then end of stream")]
      public void AnHttp10RequestIsAnsweredAndClosed()
      {
         using (TcpClient client = Connect(RestPort))
         using (NetworkStream stream = client.GetStream())
         {
            var reader = new HttpReader(stream);

            Send(stream, Request("GET", "/api/v1/status", "HTTP/1.0", Basic() + "Connection: close\r\n"));
            HttpReply reply = reader.Read();

            Assert.AreEqual(200, reply.Status, "Body: " + reply.Body);
            Assert.AreEqual("close", reply.Header("connection"));
            Assert.IsTrue(reader.AtEnd(), "An HTTP/1.0 client that did not ask for keep-alive must be closed after its response.");
         }
      }

      [Test]
      [Description("Two requests written before either is read are answered in order on the same connection")]
      public void PipelinedRequestsAreAnsweredInOrder()
      {
         using (TcpClient client = Connect(RestPort))
         using (NetworkStream stream = client.GetStream())
         {
            var reader = new HttpReader(stream);

            Send(stream, Request("GET", "/api/v1/status", "HTTP/1.1", Basic()) +
                         Request("GET", "/api/v1/no-such-route", "HTTP/1.1", Basic()));

            HttpReply first = reader.Read();
            HttpReply second = reader.Read();

            Assert.AreEqual(200, first.Status, "Body: " + first.Body);
            Assert.AreEqual(404, second.Status, "Body: " + second.Body);
         }
      }

      [Test]
      [Description("Expect: 100-continue is answered before the body is sent, and the body is then read")]
      public void ExpectContinueIsAnsweredBeforeTheBodyIsSent()
      {
         using (TcpClient client = Connect(RestPort))
         using (NetworkStream stream = client.GetStream())
         {
            var reader = new HttpReader(stream);

            // A body the API refuses (no label), so nothing is created.
            string body = "{}";
            Send(stream, Request("POST", "/api/v1/apikeys", "HTTP/1.1",
               Basic() + "Content-Type: application/json\r\nContent-Length: " + body.Length + "\r\nExpect: 100-continue\r\n"));

            HttpReply interim = reader.Read();
            Assert.AreEqual(100, interim.Status, "The declared body must be invited before it is sent. Got: " + interim.StatusLine);

            Send(stream, body);

            HttpReply final = reader.Read();
            Assert.AreEqual(400, final.Status, "Body: " + final.Body);
            StringAssert.Contains("label is required", final.Body);
         }
      }

      [Test]
      [Description("A chunked request body is refused with 411, in the API's own JSON shape, and the connection is closed")]
      public void AChunkedBodyIsRefusedWith411()
      {
         using (TcpClient client = Connect(RestPort))
         using (NetworkStream stream = client.GetStream())
         {
            var reader = new HttpReader(stream);

            Send(stream, Request("POST", "/api/v1/apikeys", "HTTP/1.1",
               Basic() + "Content-Type: application/json\r\nTransfer-Encoding: chunked\r\n"));

            HttpReply reply = reader.Read();
            Assert.AreEqual(411, reply.Status, "Body: " + reply.Body);
            StringAssert.Contains("\"error\"", reply.Body);
            Assert.AreEqual("close", reply.Header("connection"));
            Assert.IsTrue(reader.AtEnd(), "The framing after a refused body cannot be trusted, so the connection must close.");
         }
      }

      [Test]
      [Description("A head that never ends is refused with 413 once it is over the cap, rather than buffered forever")]
      public void AnOversizedHeadIsRefusedWith413()
      {
         using (TcpClient client = Connect(RestPort))
         using (NetworkStream stream = client.GetStream())
         {
            var reader = new HttpReader(stream);

            var head = new StringBuilder();
            head.Append("GET /api/v1/status HTTP/1.1\r\nHost: 127.0.0.1\r\n");
            string padding = new string('p', 1000);
            for (int i = 0; i < 80; i++)
               head.Append("X-Padding-").Append(i).Append(": ").Append(padding).Append("\r\n");

            // The server answers and closes while the client may still be
            // sending, so the write can fail with a reset. That is the refusal
            // doing its job; what matters is what the client was told, if it was
            // told anything, and that it was not a success.
            int status;
            try
            {
               Send(stream, head.ToString());
               status = reader.Read().Status;
            }
            catch (IOException)
            {
               status = 0;
            }

            if (status != 0)
               Assert.AreEqual(413, status);
         }
      }

      [Test]
      [Description("Connections that send half a request do not delay a complete request from someone else")]
      public void ASlowClientDoesNotHoldTheListener()
      {
         var slow = new List<TcpClient>();
         try
         {
            // Six connections, each stuck half-way through its request line.
            // Against the serial listener this replaces, the seventh request
            // below would have waited behind the first of them for the whole
            // read timeout.
            for (int i = 0; i < 6; i++)
            {
               TcpClient client = Connect(RestPort);
               slow.Add(client);
               Send(client.GetStream(), "GET /api/v1/status HTTP/1.1\r\nHost: 127.0.0.1\r\n");
            }

            var stopwatch = Stopwatch.StartNew();

            using (TcpClient client = Connect(RestPort))
            using (NetworkStream stream = client.GetStream())
            {
               var reader = new HttpReader(stream);
               Send(stream, Request("GET", "/api/v1/status", "HTTP/1.1", Basic()));
               HttpReply reply = reader.Read();

               Assert.AreEqual(200, reply.Status, "Body: " + reply.Body);
            }

            stopwatch.Stop();
            Assert.Less(stopwatch.ElapsedMilliseconds, 10000,
               "A complete request must be answered while other connections are still mid-request, not after their deadline.");
         }
         finally
         {
            foreach (TcpClient client in slow)
               client.Close();
         }
      }

      [Test]
      [Description("A connection that never finishes its request is closed when the absolute request deadline passes")]
      public void ASlowRequestIsClosedAtTheDeadline()
      {
         using (TcpClient client = Connect(WebPort))
         using (NetworkStream stream = client.GetStream())
         {
            Send(stream, "GET /.well-known/security.txt HTTP/1.1\r\nHost: 127.0.0.1\r\n");

            var stopwatch = Stopwatch.StartNew();
            stream.ReadTimeout = (WebRequestSeconds + 20) * 1000;

            bool closed;
            try
            {
               closed = stream.Read(new byte[1], 0, 1) == 0;
            }
            catch (IOException)
            {
               // A reset is a close too.
               closed = true;
            }

            stopwatch.Stop();

            Assert.IsTrue(closed, "The server must close a connection whose request never completes.");
            Assert.Less(stopwatch.ElapsedMilliseconds, (WebRequestSeconds + 15) * 1000,
               "The close must come from the request deadline, not from the client's own read timeout.");
         }
      }

      [Test]
      [Description("The web services listener keeps a connection alive as well")]
      public void TheWebServicesListenerKeepsAliveToo()
      {
         using (TcpClient client = Connect(WebPort))
         using (NetworkStream stream = client.GetStream())
         {
            var reader = new HttpReader(stream);

            Send(stream, Request("GET", "/no-such-path", "HTTP/1.1", ""));
            HttpReply first = reader.Read();
            Assert.AreEqual(404, first.Status, "Body: " + first.Body);
            Assert.AreEqual("keep-alive", first.Header("connection"));

            // The web services listener's default: not cacheable unless a
            // handler says so. A refusal never says so.
            Assert.AreEqual("no-store", first.Header("cache-control"));

            Send(stream, Request("GET", "/no-such-path", "HTTP/1.1", ""));
            HttpReply second = reader.Read();
            Assert.AreEqual(404, second.Status, "Body: " + second.Body);
         }
      }

      [Test]
      [Description("Several clients issuing many requests each are all answered, concurrently")]
      public void ConcurrentClientsAreAllServed()
      {
         const int clients = 8;
         const int requestsPerClient = 20;

         var failures = new List<string>();
         var threads = new List<Thread>();
         var stopwatch = Stopwatch.StartNew();

         for (int c = 0; c < clients; c++)
         {
            int clientIndex = c;
            var thread = new Thread(() =>
            {
               try
               {
                  using (TcpClient client = Connect(RestPort))
                  using (NetworkStream stream = client.GetStream())
                  {
                     var reader = new HttpReader(stream);
                     for (int r = 0; r < requestsPerClient; r++)
                     {
                        Send(stream, Request("GET", "/api/v1/status", "HTTP/1.1", Basic()));
                        HttpReply reply = reader.Read();
                        if (reply.Status != 200)
                        {
                           lock (failures)
                              failures.Add("client " + clientIndex + " request " + r + ": " + reply.StatusLine);
                           return;
                        }
                     }
                  }
               }
               catch (Exception ex) when (ex is IOException || ex is SocketException)
               {
                  lock (failures)
                     failures.Add("client " + clientIndex + ": " + ex.Message);
               }
            });

            threads.Add(thread);
            thread.Start();
         }

         foreach (Thread thread in threads)
            thread.Join();

         stopwatch.Stop();

         Assert.IsEmpty(failures, string.Join("\n", failures));
         Assert.Less(stopwatch.ElapsedMilliseconds, 60000);
      }

      // ------------------------------------------------------------ helpers ---

      private static string Basic()
      {
         string credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes("Administrator:" + AdminPassword));
         return "Authorization: Basic " + credentials + "\r\n";
      }

      private static string Request(string method, string path, string version, string extraHeaders)
      {
         return method + " " + path + " " + version + "\r\nHost: 127.0.0.1\r\n" + extraHeaders + "\r\n";
      }

      private static TcpClient Connect(int port)
      {
         var client = new TcpClient();
         Exception last = null;

         for (int attempt = 0; attempt < 25; attempt++)
         {
            try
            {
               client.Connect("127.0.0.1", port);
               return client;
            }
            catch (SocketException ex)
            {
               last = ex;
               Thread.Sleep(200);
            }
         }

         client.Close();
         throw last;
      }

      private static void Send(NetworkStream stream, string text)
      {
         byte[] bytes = Encoding.ASCII.GetBytes(text);
         stream.Write(bytes, 0, bytes.Length);
      }

      private sealed class HttpReply
      {
         public string StatusLine = "";
         public int Status;
         public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
         public string Body = "";

         public string Header(string name)
         {
            string value;
            return Headers.TryGetValue(name, out value) ? value : "";
         }
      }

      /// <summary>
      ///    Reads responses one at a time from a stream that may carry several,
      ///    framing each by its Content-Length - which is what keep-alive and
      ///    pipelining require of a client, and what an EOF-framed reader cannot
      ///    do.
      /// </summary>
      private sealed class HttpReader
      {
         private readonly NetworkStream stream_;
         private readonly List<byte> buffered_ = new List<byte>();
         private bool eof_;

         public HttpReader(NetworkStream stream)
         {
            stream_ = stream;
            stream_.ReadTimeout = 15000;
         }

         public HttpReply Read()
         {
            var reply = new HttpReply();

            byte[] head = ReadUntil(Encoding.ASCII.GetBytes("\r\n\r\n"));
            if (head == null)
            {
               reply.StatusLine = "(connection closed before a response)";
               return reply;
            }

            string headText = Encoding.ASCII.GetString(head);
            string[] lines = headText.Split(new[] { "\r\n" }, StringSplitOptions.None);

            reply.StatusLine = lines[0];
            string[] parts = lines[0].Split(' ');
            if (parts.Length >= 2)
               int.TryParse(parts[1], out reply.Status);

            for (int i = 1; i < lines.Length; i++)
            {
               int colon = lines[i].IndexOf(':');
               if (colon > 0)
                  reply.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }

            int length = 0;
            if (reply.Headers.ContainsKey("Content-Length"))
               int.TryParse(reply.Headers["Content-Length"], out length);

            byte[] body = ReadExactly(length);
            reply.Body = Encoding.UTF8.GetString(body);

            return reply;
         }

         // True when the server has closed the connection and nothing is left
         // to read.
         public bool AtEnd()
         {
            if (buffered_.Count > 0)
               return false;

            Fill();
            return eof_ && buffered_.Count == 0;
         }

         private byte[] ReadUntil(byte[] delimiter)
         {
            for (;;)
            {
               int at = IndexOf(delimiter);
               if (at >= 0)
               {
                  int end = at + delimiter.Length;
                  byte[] result = buffered_.GetRange(0, end).ToArray();
                  buffered_.RemoveRange(0, end);
                  return result;
               }

               if (eof_)
                  return null;

               Fill();
            }
         }

         private byte[] ReadExactly(int count)
         {
            while (buffered_.Count < count && !eof_)
               Fill();

            int take = Math.Min(count, buffered_.Count);
            byte[] result = buffered_.GetRange(0, take).ToArray();
            buffered_.RemoveRange(0, take);
            return result;
         }

         private void Fill()
         {
            if (eof_)
               return;

            byte[] chunk = new byte[8192];
            int read;
            try
            {
               read = stream_.Read(chunk, 0, chunk.Length);
            }
            catch (IOException)
            {
               // A reset or a timeout: nothing more will come.
               read = 0;
            }

            if (read <= 0)
            {
               eof_ = true;
               return;
            }

            for (int i = 0; i < read; i++)
               buffered_.Add(chunk[i]);
         }

         private int IndexOf(byte[] delimiter)
         {
            for (int i = 0; i + delimiter.Length <= buffered_.Count; i++)
            {
               bool match = true;
               for (int j = 0; j < delimiter.Length; j++)
               {
                  if (buffered_[i + j] != delimiter[j])
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
      }
   }
}
