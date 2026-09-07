// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace RegressionTests.Shared
{
   /// <summary>
   ///    An HTTP endpoint on the loopback address that answers with whatever the test
   ///    told it to and records every request it was sent.
   ///
   ///    A raw socket rather than HttpListener, for the reason FakeFilterEngine gives:
   ///    HttpListener needs a URL ACL reservation an unelevated run does not have, and
   ///    reading one request and answering it is thirty lines. It stands in for an
   ///    identity provider's JWK Set and introspection endpoints, which is why the
   ///    response can be changed between requests - a key rotation is the provider
   ///    publishing a different document at the same URL.
   /// </summary>
   public sealed class FakeHttpEndpoint : IDisposable
   {
      private readonly TcpListener _listener;
      private readonly List<string> _requests = new List<string>();
      private readonly object _lock = new object();
      private volatile bool _stopping;
      private int _statusCode;
      private string _body;
      private string _contentType;
      private readonly Dictionary<string, Route> _routes = new Dictionary<string, Route>(StringComparer.Ordinal);

      private sealed class Route
      {
         public int Status;
         public byte[] Body;
         public string ContentType;
         public string Location;
      }

      public FakeHttpEndpoint(int statusCode, string body, string contentType = "application/json")
      {
         _statusCode = statusCode;
         _body = body;
         _contentType = contentType;
         _listener = new TcpListener(IPAddress.Loopback, 0);
         _listener.Start();
         Port = ((IPEndPoint) _listener.LocalEndpoint).Port;
         new Thread(Serve) {IsBackground = true, Name = "FakeHttpEndpoint"}.Start();
      }

      public int Port { get; }

      public string UrlFor(string path)
      {
         return "http://127.0.0.1:" + Port + path;
      }

      /// <summary>Every request received - request line, headers and body - oldest first.</summary>
      public List<string> Requests
      {
         get
         {
            lock (_lock)
               return new List<string>(_requests);
         }
      }

      /// <summary>
      ///    What requests for one exact path are answered with, in bytes: a release's
      ///    installer and its bundle live at different paths on the same host. Paths
      ///    without a route get the default response.
      /// </summary>
      public void SetResponse(string path, int statusCode, byte[] body, string contentType = "application/octet-stream")
      {
         lock (_lock)
            _routes[path] = new Route {Status = statusCode, Body = body, ContentType = contentType};
      }

      /// <summary>A 302 from one path to a location, as a release asset download is.</summary>
      public void SetRedirect(string path, string location)
      {
         lock (_lock)
            _routes[path] = new Route {Status = 302, Body = new byte[0], ContentType = "text/plain", Location = location};
      }

      public void ClearRoutes()
      {
         lock (_lock)
            _routes.Clear();
      }

      /// <summary>What the next requests are answered with.</summary>
      public void SetResponse(int statusCode, string body, string contentType = "application/json")
      {
         lock (_lock)
         {
            _statusCode = statusCode;
            _body = body;
            _contentType = contentType;
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

            try
            {
               using (client)
               using (var stream = client.GetStream())
               {
                  stream.ReadTimeout = 10000;
                  Handle(stream);
               }
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException)
            {
               // A request the server abandoned is not this endpoint's failure to report.
            }
         }
      }

      private void Handle(NetworkStream stream)
      {
         var request = new StringBuilder();
         var buffer = new byte[4096];
         int headerEnd = -1;
         while (headerEnd < 0)
         {
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
               return;
            request.Append(Encoding.ASCII.GetString(buffer, 0, read));
            headerEnd = request.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
         }

         // The body, when the headers announce one.
         int contentLength = 0;
         var headerLines = request.ToString(0, headerEnd).Split(new[] {"\r\n"}, StringSplitOptions.None)
            .Where(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
         foreach (var line in headerLines)
            int.TryParse(line.Substring(15).Trim(), out contentLength);

         while (request.Length - (headerEnd + 4) < contentLength)
         {
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
               break;
            request.Append(Encoding.ASCII.GetString(buffer, 0, read));
         }
         var text = request.ToString();

         int status;
         byte[] bodyBytes;
         string contentType;
         string location = null;
         string requestPath = RequestPath(text);
         lock (_lock)
         {
            _requests.Add(text);
            Route route;
            if (requestPath != null && _routes.TryGetValue(requestPath, out route))
            {
               status = route.Status;
               bodyBytes = route.Body;
               contentType = route.ContentType;
               location = route.Location;
            }
            else
            {
               status = _statusCode;
               bodyBytes = Encoding.UTF8.GetBytes(_body ?? "");
               contentType = _contentType;
            }
         }

         var reason = status == 200 ? "OK" : status == 302 ? "Found" : status == 401 ? "Unauthorized" : status == 404 ? "Not Found" : "Error";
         var head = "HTTP/1.0 " + status + " " + reason + "\r\nContent-Type: " + contentType +
                    (location == null ? "" : "\r\nLocation: " + location) +
                    "\r\nContent-Length: " + bodyBytes.Length + "\r\nConnection: close\r\n\r\n";
         var headBytes = Encoding.ASCII.GetBytes(head);
         stream.Write(headBytes, 0, headBytes.Length);
         stream.Write(bodyBytes, 0, bodyBytes.Length);
         stream.Flush();
      }

      // The path of the request line, without any query.
      private static string RequestPath(string request)
      {
         int lineEnd = request.IndexOf("\r\n", StringComparison.Ordinal);
         string[] parts = (lineEnd < 0 ? request : request.Substring(0, lineEnd)).Split(' ');
         if (parts.Length < 2)
            return null;
         int query = parts[1].IndexOf('?');
         return query < 0 ? parts[1] : parts[1].Substring(0, query);
      }
   }
}
