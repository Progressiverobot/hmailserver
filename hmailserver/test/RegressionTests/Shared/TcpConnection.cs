// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Linq;

namespace RegressionTests.Shared
{
   /// <summary>
   ///    Summary description for ClientSocket.
   /// </summary>
   public class TcpConnection : IDisposable
   {
      // TLS 1.2 + 1.3. (SslProtocols.Default means SSL3|TLS1.0, which the
      // OpenSSL 4 based server no longer accepts with default settings.)
      public const SslProtocols ModernProtocols = SslProtocols.Tls12 | (SslProtocols) 12288 /* Tls13 */;

      private readonly SslProtocols _sslProtocols = ModernProtocols;

      private SslStream _sslStream;
      private TcpClient _tcpClient;

      public TcpConnection()
      {
      }

      public TcpConnection(bool useSSL)
         : this(useSSL, ModernProtocols)

      {
         IsSslConnection = useSSL;
      }

      public TcpConnection(bool useSSL, SslProtocols protocols)
      {
         IsSslConnection = useSSL;
         _sslProtocols = protocols;
      }

      public TcpConnection(TcpClient client)
      {
         _tcpClient = client;
      }

      public bool IsConnected => _tcpClient != null && _tcpClient.Connected;

      public bool IsSslConnection { get; private set; }

      // The server certificate negotiated during the TLS handshake, or null on a
      // plain connection. Used to derive the RFC 5929 tls-server-end-point channel
      // binding for SCRAM-SHA-256-PLUS.
      public X509Certificate RemoteCertificate => _sslStream?.RemoteCertificate;

      public void Dispose()
      {
         Disconnect();
      }

      public bool Connect(int iPort)
      {
         return Connect(IPAddress.Parse("127.0.0.1"), iPort);
      }

      public bool Connect(IPAddress ipaddress, int iPort)
      {
         if (ipaddress == null)
            throw new ArgumentNullException("ipaddress");

         try
         {
            _tcpClient = new TcpClient(ipaddress.AddressFamily);

            var result = _tcpClient.BeginConnect(ipaddress, iPort, null, null);

            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(10), false);

            if (!success) return false;

            if (!_tcpClient.Connected) return false;
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            return false;
         }

         _tcpClient.Client.Blocking = true;

         if (IsSslConnection)
            HandshakeAsClient();

         return true;
      }

      public bool IsPortOpen(int iPort)
      {
         if (!Connect(iPort))
            return false;

         try
         {
            for (var i = 0; i < 40; i++)
            {
               if (_tcpClient.Available > 0)
                  return true;

               Thread.Sleep(25);
            }
         }
         finally
         {
            Disconnect();
         }

         return false;
      }

      public void Disconnect()
      {
         if (IsSslConnection)
            _sslStream.Close();

         if (_tcpClient != null)
            _tcpClient.Close();
      }

      /// <summary>
      /// Closes only this end's sending half, so the server sees a clean
      /// end-of-stream. Closing the socket outright while data is unread makes
      /// Windows send RST instead, which the server sees as a different error -
      /// so a test that needs the end-of-stream path must use this.
      /// </summary>
      public void HalfCloseSend()
      {
         if (_tcpClient != null)
            _tcpClient.Client.Shutdown(SocketShutdown.Send);
      }

      public void HandshakeAsClient()
      {
         // Create an SSL stream that will close the client's stream.
         _sslStream = new SslStream(_tcpClient.GetStream(), false,
            ValidateServerCertificate, null);

         _sslStream.AuthenticateAsClient("localhost", null, _sslProtocols, false);

         IsSslConnection = true;
      }

      public void HandshakeAsServer(X509Certificate2 certificate)
      {
         // Create an SSL stream that will close the client's stream.
         _sslStream = new SslStream(_tcpClient.GetStream(), false,
            ValidateServerCertificate, null);

         _sslStream.AuthenticateAsServer(certificate, false, _sslProtocols, false);

         IsSslConnection = true;
      }

      public string SendAndReceive(string sData)
      {
         Send(sData);
         return Receive();
      }

      public void Send(string s)
      {
         if (!_tcpClient.Connected)
            throw new InvalidOperationException("Connection closed - Unable to send data.");

         if (IsSslConnection)
         {
            var message = Encoding.UTF8.GetBytes(s);
            _sslStream.Write(message);
            _sslStream.Flush();
         }
         else
         {
            var buf = Encoding.UTF8.GetBytes(s);
            var stream = _tcpClient.GetStream();

            stream.Write(buf, 0, buf.Length);
         }
      }

      public string ReadUntil(string text)
      {
         return ReadUntil(text, TimeSpan.FromSeconds(10));
      }

      public string ReadUntil(string text, TimeSpan timeout)
      {
         var stopTime = DateTime.Now + timeout;

         var received = new StringBuilder(Receive());

         while (DateTime.Now < stopTime)
         {
            var result = received.ToString();

            if (result.Contains(text))
               return result;

            if (!_tcpClient.Connected)
               return "";

            received.Append(Receive());

            Thread.Sleep(10);
         }

         throw new TimeoutException("Timeout while waiting for server response: " + text);
      }


      public string ReadUntil(List<string> possibleReplies)
      {
         var received = new StringBuilder(Receive());

         for (var i = 0; i < 1000; i++)
         {
            var result = received.ToString();

            if (possibleReplies.Any(result.Contains))
               return result;

            Thread.Sleep(10);

            received.Append(Receive());
         }

         throw new InvalidOperationException("Timeout while waiting for server response");
      }

      /// <summary>
      ///    True when at least one byte can be read within the timeout; false when
      ///    the window passed with nothing, or the peer has gone. What a simulated
      ///    server uses to see whether a client sent more before waiting for a reply.
      /// </summary>
      public bool WaitForData(TimeSpan timeout)
      {
         if (!_tcpClient.Connected)
            return false;

         int microseconds = (int) Math.Min(int.MaxValue, timeout.TotalMilliseconds * 1000);

         if (!_tcpClient.Client.Poll(microseconds, SelectMode.SelectRead))
            return false;

         return _tcpClient.Available > 0;
      }

      public string Receive()
      {
         var messageData = new StringBuilder();

         var buffer = new byte[2048];
         int bytesRead;

         if (IsSslConnection)
            do
            {
               if (!_sslStream.CanRead)
                  return "";

               bytesRead = _sslStream.Read(buffer, 0, buffer.Length);
               var decoder = Encoding.UTF8.GetDecoder();
               var chars = new char[decoder.GetCharCount(buffer, 0, bytesRead)];
               decoder.GetChars(buffer, 0, bytesRead, chars, 0);
               messageData.Append(chars);
            } while (_tcpClient.Available > 0);
         else
            do
            {
               var stream = _tcpClient.GetStream();

               if (!stream.CanRead)
                  return "";

               bytesRead = stream.Read(buffer, 0, buffer.Length);
               var chars = Encoding.ASCII.GetChars(buffer);
               var s = new string(chars, 0, bytesRead);
               messageData.Append(s);
            } while (_tcpClient.Available > 0);

         return messageData.ToString();
      }

      public bool Peek()
      {
         return _tcpClient.Available > 0;
      }

      // The following method is invoked by the RemoteCertificateValidationDelegate.
      public static bool ValidateServerCertificate(
         object sender,
         X509Certificate certificate,
         X509Chain chain,
         SslPolicyErrors sslPolicyErrors)
      {
         return true;
      }


      public bool TestConnect(int iPort)
      {
         var bRetVal = Connect(iPort);
         Disconnect();
         return bRetVal;
      }
   }
}