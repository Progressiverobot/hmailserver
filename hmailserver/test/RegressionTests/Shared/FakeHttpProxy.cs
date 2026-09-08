// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace RegressionTests.Shared
{
   /// <summary>
   ///    A forward proxy on the loopback, for HttpProxy: a CONNECT tunnel for an https
   ///    target, and forwarding of the absolute URL for a plain-http one. Every target
   ///    it was asked for is recorded, oldest first, in the form the client sent it -
   ///    "host:port" for CONNECT, the URL for a forwarded request. A CONNECT to a port
   ///    nothing listens on is answered 502, which is the refusal path the server
   ///    reports by the proxy's name.
   /// </summary>
   public sealed class FakeHttpProxy : IDisposable
   {
      private readonly TcpListener _listener;
      private readonly object _lock = new object();
      private readonly List<string> _targets = new List<string>();
      private volatile bool _stopping;

      public FakeHttpProxy()
      {
         _listener = new TcpListener(IPAddress.Loopback, 0);
         _listener.Start();
         Port = ((IPEndPoint) _listener.LocalEndpoint).Port;
         new Thread(Serve) { IsBackground = true, Name = "FakeHttpProxy" }.Start();
      }

      public int Port { get; }

      /// <summary>What HttpProxy is set to.</summary>
      public string Address
      {
         get { return "127.0.0.1:" + Port; }
      }

      public List<string> Targets
      {
         get
         {
            lock (_lock)
               return new List<string>(_targets);
         }
      }

      public void Dispose()
      {
         _stopping = true;
         _listener.Stop();
      }

      private void Serve()
      {
         while (!_stopping)
         {
            TcpClient client;
            try
            {
               client = _listener.AcceptTcpClient();
            }
            catch (SocketException)
            {
               return;
            }
            catch (ObjectDisposedException)
            {
               return;
            }

            // One thread per connection: a tunnel relays until one side closes.
            new Thread(() => Handle(client)) { IsBackground = true }.Start();
         }
      }

      private void Handle(TcpClient client)
      {
         try
         {
            using (client)
            using (var stream = client.GetStream())
            {
               stream.ReadTimeout = 10000;

               var head = new StringBuilder();
               var buffer = new byte[4096];
               // The body bytes that arrived with the header outlive the nested blocks
               // below, so the declaration disposes it once this connection is done -
               // on every path out, including the early returns.
               using var leftover = new MemoryStream();
               int headerEnd = -1;
               while (headerEnd < 0)
               {
                  int read = stream.Read(buffer, 0, buffer.Length);
                  if (read <= 0)
                     return;
                  string chunk = Encoding.ASCII.GetString(buffer, 0, read);
                  int before = head.Length;
                  head.Append(chunk);
                  headerEnd = head.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
                  if (headerEnd >= 0)
                  {
                     // Bytes after the blank line belong to the body (or to TLS).
                     int bodyStart = headerEnd + 4 - before;
                     if (bodyStart < read)
                        leftover.Write(buffer, bodyStart, read - bodyStart);
                  }
               }

               string headerBlock = head.ToString().Substring(0, headerEnd);
               string[] lines = headerBlock.Split(new[] { "\r\n" }, StringSplitOptions.None);
               string[] requestLine = lines[0].Split(' ');
               if (requestLine.Length < 3)
                  return;

               string method = requestLine[0];
               string target = requestLine[1];
               lock (_lock)
                  _targets.Add(target);

               if (method == "CONNECT")
               {
                  int colon = target.LastIndexOf(':');
                  string host = target.Substring(0, colon);
                  int port = int.Parse(target.Substring(colon + 1));

                  TcpClient upstream;
                  try
                  {
                     upstream = new TcpClient(host, port);
                  }
                  catch (SocketException)
                  {
                     Write(stream, "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                     return;
                  }

                  using (upstream)
                  using (var up = upstream.GetStream())
                  {
                     Write(stream, "HTTP/1.1 200 Connection established\r\n\r\n");
                     if (leftover.Length > 0)
                        up.Write(leftover.ToArray(), 0, (int) leftover.Length);
                     Relay(stream, up);
                  }
                  return;
               }

               // Plain http: the absolute URL says where; the request goes on in
               // origin form with the same headers and whatever body followed.
               Uri url;
               if (!Uri.TryCreate(target, UriKind.Absolute, out url))
               {
                  Write(stream, "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                  return;
               }

               TcpClient origin;
               try
               {
                  origin = new TcpClient(url.Host, url.Port);
               }
               catch (SocketException)
               {
                  Write(stream, "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                  return;
               }

               using (origin)
               using (var up = origin.GetStream())
               {
                  var forwarded = new StringBuilder();
                  forwarded.Append(method).Append(' ').Append(url.PathAndQuery).Append(' ').Append(requestLine[2]).Append("\r\n");
                  for (int i = 1; i < lines.Length; i++)
                     forwarded.Append(lines[i]).Append("\r\n");
                  forwarded.Append("\r\n");
                  Write(up, forwarded.ToString());
                  if (leftover.Length > 0)
                     up.Write(leftover.ToArray(), 0, (int) leftover.Length);
                  Relay(stream, up);
               }
            }
         }
         catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException)
         {
            // A connection the server abandoned is not this proxy's failure to report.
         }
      }

      private static void Write(Stream stream, string text)
      {
         byte[] bytes = Encoding.ASCII.GetBytes(text);
         stream.Write(bytes, 0, bytes.Length);
         stream.Flush();
      }

      /// <summary>Copies both ways until either side closes, then returns.</summary>
      private static void Relay(NetworkStream client, NetworkStream upstream)
      {
         // Either pump finishing ends the relay, but the other is still blocked in its
         // read at that moment and the Join below is bounded, so a pump can outlive this
         // call. A ManualResetEvent would then have to be disposed while a live thread
         // could still Set() it, which is a race no ordering here can close; a monitor on
         // a plain object carries the same signal and needs no disposal at all.
         var done = new object();
         bool finished = false;
         ThreadStart pump = () => { };
         Action<Stream, Stream> copy = (from, to) =>
         {
            try
            {
               var buffer = new byte[8192];
               int read;
               while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
               {
                  to.Write(buffer, 0, read);
                  to.Flush();
               }
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException)
            {
               // The far side closing under a blocking read is how a tunnel ordinarily
               // ends, so the pump records why it stopped rather than failing anything.
               // The finally still runs, and whoever waits on the other pump is released.
               Trace.WriteLine("FakeHttpProxy: a relay pump stopped on " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
               lock (done)
               {
                  finished = true;
                  Monitor.PulseAll(done);
               }
            }
         };
         var toUpstream = new Thread(() => copy(client, upstream)) { IsBackground = true };
         var toClient = new Thread(() => copy(upstream, client)) { IsBackground = true };
         toUpstream.Start();
         toClient.Start();
         lock (done)
         {
            // A pump that finished before this lock was taken has already set the flag,
            // which is the latched state a ManualResetEvent gave us for free.
            if (!finished)
               Monitor.Wait(done, 15000);
         }

         // Closing either stream ends the other pump's blocking read. Each close stands
         // on its own: a stream whose pump has already faulted throws on the way out, and
         // the other side must be closed anyway.
         CloseRelayStream(client);
         CloseRelayStream(upstream);
         toUpstream.Join(2000);
         toClient.Join(2000);
         GC.KeepAlive(pump);
      }

      /// <summary>
      ///    Closes one end of a relay. The pumps are blocked in a read on these streams and
      ///    closing them is how those reads are ended, so a stream that is already torn down
      ///    - or that faults on its way out - is the expected case here, and not something
      ///    the proxy has anything to report.
      /// </summary>
      private static void CloseRelayStream(NetworkStream stream)
      {
         try
         {
            stream.Close();
         }
         catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException)
         {
            Trace.WriteLine("FakeHttpProxy: closing a relay stream threw " + ex.GetType().Name + ": " + ex.Message);
         }
      }
   }
}
