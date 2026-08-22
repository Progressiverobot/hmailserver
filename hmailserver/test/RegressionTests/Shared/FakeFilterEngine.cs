// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NUnit.Framework;

namespace RegressionTests.Shared
{
   /// <summary>
   ///    A stand-in for rspamd: accepts an HTTP POST carrying a message and answers
   ///    with whatever JSON the test told it to.
   ///
   ///    Deliberately a raw socket rather than HttpListener. HttpListener needs a URL
   ///    ACL reservation on Windows, which an unelevated test run does not have, and
   ///    the one thing this has to do - read a request, answer it - is thirty lines.
   ///
   ///    It also records what it was sent, because half of what is worth asserting
   ///    about a filter hook is not the verdict but the request: whether the envelope
   ///    and the connecting address actually reached the engine, and whether the
   ///    message itself did.
   /// </summary>
   public sealed class FakeFilterEngine : IDisposable
   {
      private readonly TcpListener listener_;
      private readonly string response_;
      private readonly int delayMilliseconds_;
      private volatile bool stopping_;

      private readonly List<string> requests_ = new List<string>();

      public int Port { get; }

      /// <summary>
      ///    Every request received, headers and body, oldest first.
      /// </summary>
      public List<string> Requests
      {
         get
         {
            lock (requests_)
               return new List<string>(requests_);
         }
      }

      /// <summary>
      ///    <paramref name="responseJson"/> is answered with 200. A null body answers
      ///    500 instead, which is how the "engine is there but broken" case is
      ///    tested. <paramref name="delayMilliseconds"/> makes it answer late, for
      ///    the timeout case.
      /// </summary>
      public FakeFilterEngine(string responseJson, int delayMilliseconds = 0)
      {
         response_ = responseJson;
         delayMilliseconds_ = delayMilliseconds;

         listener_ = new TcpListener(IPAddress.Loopback, 0);
         listener_.Start();

         Port = ((IPEndPoint) listener_.LocalEndpoint).Port;

         new Thread(Serve_) { IsBackground = true }.Start();
      }

      public string Url => "http://127.0.0.1:" + Port + "/checkv2";

      private void Serve_()
      {
         while (!stopping_)
         {
            TcpClient client;

            try
            {
               client = listener_.AcceptTcpClient();
            }
            catch
            {
               return;
            }

            try
            {
               using (client)
               using (var stream = client.GetStream())
               {
                  client.ReceiveTimeout = 10000;

                  // Read the headers, then exactly as many body bytes as
                  // Content-Length promises. Reading to end-of-stream would deadlock:
                  // the client keeps the connection open waiting for this answer.
                  var received = new MemoryStream();
                  var buffer = new byte[8192];
                  int headerEnd = -1;
                  int contentLength = 0;

                  while (true)
                  {
                     int read = stream.Read(buffer, 0, buffer.Length);
                     if (read <= 0)
                        break;

                     received.Write(buffer, 0, read);

                     string sofar = Encoding.ASCII.GetString(received.ToArray());

                     if (headerEnd < 0)
                     {
                        headerEnd = sofar.IndexOf("\r\n\r\n", StringComparison.Ordinal);

                        if (headerEnd >= 0)
                        {
                           foreach (string line in sofar.Substring(0, headerEnd).Split('\n'))
                           {
                              if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                 int.TryParse(line.Substring(15).Trim(), out contentLength);
                           }
                        }
                     }

                     if (headerEnd >= 0 && received.Length >= headerEnd + 4 + contentLength)
                        break;
                  }

                  lock (requests_)
                     requests_.Add(Encoding.UTF8.GetString(received.ToArray()));

                  if (delayMilliseconds_ > 0)
                     Thread.Sleep(delayMilliseconds_);

                  byte[] reply = response_ == null
                     ? Encoding.ASCII.GetBytes("HTTP/1.1 500 Internal Server Error\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
                     : Encoding.UTF8.GetBytes(
                          "HTTP/1.1 200 OK\r\n" +
                          "Content-Type: application/json\r\n" +
                          "Content-Length: " + Encoding.UTF8.GetByteCount(response_) + "\r\n" +
                          "Connection: close\r\n\r\n" + response_);

                  stream.Write(reply, 0, reply.Length);
                  stream.Flush();
               }
            }
            catch
            {
               // A test that has moved on closes the socket under us. Not a failure.
            }
         }
      }

      public void Dispose()
      {
         stopping_ = true;

         try
         {
            listener_.Stop();
         }
         catch
         {
         }
      }
   }
}
