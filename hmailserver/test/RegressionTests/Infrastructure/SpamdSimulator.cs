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

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    A stand-in for spamd that records what the server sends it.
   ///
   ///    The SpamAssassin fixtures run against the real spamd on port 783, which is the
   ///    right thing for verdicts: nothing but SpamAssassin can say what SpamAssassin
   ///    would score. It is the wrong thing for the request, because the real one keeps
   ///    no record of the headers it was sent, and a test that wants to know whether a
   ///    User: header went out has nothing to read. This listens on an ephemeral port,
   ///    accepts one PROCESS request per connection, keeps the request headers, and
   ///    answers as spamd would for a clean message: SPAMD/1.1 0 EX_OK, a
   ///    Content-length, and the message handed back unchanged. The server then treats
   ///    the exchange as a completed scan.
   /// </summary>
   public sealed class SpamdSimulator : IDisposable
   {
      private readonly TcpListener _listener;
      private readonly Thread _thread;
      private readonly List<Dictionary<string, string>> _requests = new List<Dictionary<string, string>>();
      private readonly object _lock = new object();
      private volatile bool _stopping;

      public SpamdSimulator()
      {
         _listener = new TcpListener(IPAddress.Loopback, 0);
         _listener.Start();
         _thread = new Thread(Serve) { IsBackground = true, Name = "SpamdSimulator" };
         _thread.Start();
      }

      public int Port
      {
         get { return ((IPEndPoint) _listener.LocalEndpoint).Port; }
      }

      /// <summary>
      ///    The headers of every PROCESS request received so far, oldest first, keyed by
      ///    header name as sent (User, Content-length, ...).
      /// </summary>
      public List<Dictionary<string, string>> Requests
      {
         get
         {
            lock (_lock)
               return new List<Dictionary<string, string>>(_requests);
         }
      }

      /// <summary>
      ///    Waits for the given number of requests to have been served, which is how a
      ///    test knows the scan the server was asked to run has actually happened.
      /// </summary>
      public Dictionary<string, string> WaitForRequest(int index, int timeoutSeconds = 30)
      {
         var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
         while (DateTime.UtcNow < deadline)
         {
            lock (_lock)
            {
               if (_requests.Count > index)
                  return _requests[index];
            }
            Thread.Sleep(50);
         }

         throw new TimeoutException("spamd simulator received " + Requests.Count + " request(s); waited for " + (index + 1) + ".");
      }

      public void Dispose()
      {
         _stopping = true;
         _listener.Stop();
         _thread.Join(TimeSpan.FromSeconds(5));
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
               // Dispose stopped the listener under the pending accept.
               return;
            }
            catch (ObjectDisposedException)
            {
               return;
            }

            try
            {
               using (client)
               using (var stream = client.GetStream())
               {
                  stream.ReadTimeout = 30000;
                  HandleRequest(stream);
               }
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException)
            {
               // A connection the server dropped mid-request is not this simulator's
               // failure to report; the test sees it as a request that never arrived.
            }
         }
      }

      private void HandleRequest(NetworkStream stream)
      {
         // Request line and headers, terminated by an empty line.
         string headerText;
         using (var headerBytes = new MemoryStream())
         {
            var window = new byte[4];
            while (true)
            {
               int b = stream.ReadByte();
               if (b < 0)
                  return;
               headerBytes.WriteByte((byte) b);
               window[0] = window[1]; window[1] = window[2]; window[2] = window[3]; window[3] = (byte) b;
               if (window[0] == '\r' && window[1] == '\n' && window[2] == '\r' && window[3] == '\n')
                  break;
            }

            headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
         }
         var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
         var lines = headerText.Split(new[] {"\r\n"}, StringSplitOptions.RemoveEmptyEntries);
         headers["Request"] = lines[0];
         for (int i = 1; i < lines.Length; i++)
         {
            int colon = lines[i].IndexOf(':');
            if (colon > 0)
               headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
         }

         long contentLength = 0;
         string lengthText;
         if (headers.TryGetValue("Content-length", out lengthText))
            long.TryParse(lengthText, out contentLength);

         var body = new byte[contentLength];
         int read = 0;
         while (read < contentLength)
         {
            int n = stream.Read(body, read, (int) (contentLength - read));
            if (n <= 0)
               break;
            read += n;
         }

         lock (_lock)
            _requests.Add(headers);

         // The reply spamd gives for PROCESS on a message it did not tag: the message
         // back, unchanged, behind the headers the server parses.
         var reply = "SPAMD/1.1 0 EX_OK\r\nContent-length: " + read + "\r\nSpam: False ; 0.0 / 5.0\r\n\r\n";
         var replyBytes = Encoding.ASCII.GetBytes(reply);
         stream.Write(replyBytes, 0, replyBytes.Length);
         stream.Write(body, 0, read);
         stream.Flush();
      }
   }
}
