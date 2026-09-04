// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.SMTP
{
   /// <summary>
   /// PROXY protocol (HAProxy, v1 and v2) and XCLIENT (Postfix) support on the SMTP
   /// listener: a trusted load balancer / upstream MTA forwards the REAL client's
   /// address, and the session's IP-based controls - IP ranges, auth requirements,
   /// DNSBL/SPF/greylisting, auto-ban, the Received header - evaluate that address
   /// instead of the proxy's.
   ///
   /// Both mechanisms are OFF by default and only take effect for peers on an
   /// explicitly configured trusted list (hMailServer.ini [Settings]
   /// SMTPProxyProtocolEnabled/SMTPProxyProtocolTrustedIPs and
   /// SMTPXClientEnabled/SMTPXClientTrustedIPs), because a peer that can rewrite its
   /// own source address has defeated every IP-based control on the server.
   ///
   /// The positive tests here are deliberately built as negative controls too: each
   /// one asserts a REFUSAL (530) that only happens when the asserted address
   /// actually reaches the IP-range machinery. If the rewrite were inert, the
   /// session would run under the localhost range - which requires no
   /// authentication - and the RCPT would be answered 250, failing the test.
   /// </summary>
   [TestFixture]
   public class ProxyProtocol : TestFixtureBase
   {
      private Account _account;

      [SetUp]
      public new void SetUp()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "proxyproto@example.test", "test");
      }

      private const string ExternalSender = "sender@dummy-example.com";

      // ----------------------------------------------------------------- helpers

      /// <summary>
      ///    A raw TCP client for port 25 with byte-level sends (the PROXY protocol
      ///    v2 header is binary; a string round-trip through an encoding would
      ///    mangle address bytes >= 0x80) and strictly bounded reads, so a
      ///    misbehaving server fails a test rather than hanging it.
      /// </summary>
      private sealed class RawSmtpSocket : IDisposable
      {
         private readonly TcpClient _client;
         private readonly NetworkStream _stream;

         public RawSmtpSocket()
         {
            _client = new TcpClient();
            _client.Connect("127.0.0.1", 25);
            _stream = _client.GetStream();
            _stream.ReadTimeout = 15000;
            _stream.WriteTimeout = 15000;
         }

         public void Send(byte[] bytes)
         {
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
         }

         public void Send(string text)
         {
            Send(Encoding.ASCII.GetBytes(text));
         }

         public string ReadUntil(string token)
         {
            var received = new StringBuilder();
            var buffer = new byte[4096];
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);

            while (DateTime.UtcNow < deadline)
            {
               int read = _stream.Read(buffer, 0, buffer.Length);

               if (read == 0)
                  Assert.Fail("The server closed the connection while the test waited for \"" + token +
                              "\". Received so far: " + received);

               received.Append(Encoding.ASCII.GetString(buffer, 0, read));

               if (received.ToString().Contains(token))
                  return received.ToString();
            }

            Assert.Fail("Timed out waiting for \"" + token + "\". Received so far: " + received);
            return null;
         }

         /// <summary>
         ///    Reads until the server closes the connection (graceful FIN or reset)
         ///    and returns whatever bytes arrived first. Fails the test if the
         ///    server leaves the connection open instead.
         /// </summary>
         public string ReadUntilRemoteClose()
         {
            var received = new StringBuilder();
            var buffer = new byte[4096];
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);

            while (DateTime.UtcNow < deadline)
            {
               int read;

               try
               {
                  read = _stream.Read(buffer, 0, buffer.Length);
               }
               catch (System.IO.IOException ex)
               {
                  var socketException = ex.InnerException as SocketException;

                  if (socketException != null && socketException.SocketErrorCode == SocketError.TimedOut)
                     Assert.Fail("The server did not close the connection. Received so far: " + received);

                  // A reset also counts as the server dropping the connection.
                  return received.ToString();
               }

               if (read == 0)
                  return received.ToString();

               received.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            Assert.Fail("The server did not close the connection. Received so far: " + received);
            return null;
         }

         public void Dispose()
         {
            try
            {
               _stream.Dispose();
               _client.Close();
            }
            catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
            {
               // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
            }
         }
      }

      /// <summary>
      ///    Builds a PROXY protocol v2 header. The 12-byte signature is the exact
      ///    constant from the specification, and the length field is computed from
      ///    the address block rather than hand-counted.
      /// </summary>
      private static byte[] BuildProxyV2Header(bool proxyCommand, byte familyAndTransport, byte[] addressBlock)
      {
         var header = new List<byte>();

         // \x0D\x0A\x0D\x0A\x00\x0D\x0A\x51\x55\x49\x54\x0A ("\r\n\r\n\0\r\nQUIT\n")
         header.AddRange(new byte[] { 0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A });

         // Version 2 in the high nibble; command PROXY (\x1) or LOCAL (\x0) in the low.
         header.Add(proxyCommand ? (byte) 0x21 : (byte) 0x20);

         header.Add(familyAndTransport);

         header.Add((byte) (addressBlock.Length >> 8));
         header.Add((byte) (addressBlock.Length & 0xFF));

         header.AddRange(addressBlock);

         return header.ToArray();
      }

      private static byte[] BuildTcp4AddressBlock(string sourceIp, string destinationIp, int sourcePort, int destinationPort)
      {
         var block = new List<byte>();

         block.AddRange(IPAddress.Parse(sourceIp).GetAddressBytes());
         block.AddRange(IPAddress.Parse(destinationIp).GetAddressBytes());
         block.Add((byte) (sourcePort >> 8));
         block.Add((byte) (sourcePort & 0xFF));
         block.Add((byte) (destinationPort >> 8));
         block.Add((byte) (destinationPort & 0xFF));

         return block.ToArray();
      }

      private void EnableProxyProtocol(string trustedList)
      {
         IniFileSetting.Write("SMTPProxyProtocolEnabled", "1");
         IniFileSetting.Write("SMTPProxyProtocolTrustedIPs", trustedList);
         _application.Reinitialize();
      }

      private void EnableXclient(string trustedList)
      {
         IniFileSetting.Write("SMTPXClientEnabled", "1");
         IniFileSetting.Write("SMTPXClientTrustedIPs", trustedList);
         _application.Reinitialize();
      }

      private void DisableFeatures()
      {
         IniFileSetting.Delete("SMTPProxyProtocolEnabled");
         IniFileSetting.Delete("SMTPProxyProtocolTrustedIPs");
         IniFileSetting.Delete("SMTPXClientEnabled");
         IniFileSetting.Delete("SMTPXClientTrustedIPs");
         _application.Reinitialize();
      }

      /// <summary>
      ///    A single-address IP range that outranks the defaults ("My computer" has
      ///    priority 30, "Internet" 10), so a session whose effective address is
      ///    <paramref name="ip"/> is governed by THIS range - which is exactly what
      ///    the tests assert on. PerformBasicSetup restores the default ranges
      ///    before every test, so these need no explicit cleanup.
      /// </summary>
      private void AddSecurityRange(string name, string ip, bool requireSmtpAuthExternalToLocal)
      {
         var range = _application.Settings.SecurityRanges.Add();
         range.Name = name;
         range.Priority = 50;
         range.LowerIP = ip;
         range.UpperIP = ip;
         range.AllowSMTPConnections = true;
         range.AllowDeliveryFromRemoteToLocal = true;
         range.AllowDeliveryFromLocalToLocal = true;
         range.RequireSMTPAuthExternalToLocal = requireSmtpAuthExternalToLocal;
         range.EnableSpamProtection = false;
         range.Save();
      }

      // ------------------------------------------------------------- PROXY tests

      [Test]
      [Description("A PROXY protocol v1 header from a trusted peer rewrites the session's client " +
                   "address, and the rewritten address is what the IP-range security settings " +
                   "evaluate: a range for the asserted address that requires SMTP authentication " +
                   "refuses the unauthenticated RCPT with 530. Negative control built in: were the " +
                   "rewrite inert, the session would run under the localhost range, which requires " +
                   "no authentication, and the RCPT would be answered 250 - failing this test.")]
      public void ProxyV1FromTrustedPeerAppliesClientAddressToIpRangeChecks()
      {
         try
         {
            AddSecurityRange("proxy-v1-authrequired", "198.51.100.77", true);
            EnableProxyProtocol("127.0.0.1");

            using (var socket = new RawSmtpSocket())
            {
               // The header precedes the banner: the server reads it first and
               // greets only afterwards.
               socket.Send("PROXY TCP4 198.51.100.77 127.0.0.1 45123 25\r\n");
               socket.ReadUntil("220 ");

               socket.Send("EHLO proxy.example.test\r\n");
               socket.ReadUntil("250 HELP");

               socket.Send("MAIL FROM:<" + ExternalSender + ">\r\n");
               socket.ReadUntil("250");

               socket.Send("RCPT TO:<" + _account.Address + ">\r\n");
               string rcptReply = socket.ReadUntil("\r\n");

               StringAssert.StartsWith("530", rcptReply,
                  "The RCPT must be refused with 530 because the range for the PROXY-asserted " +
                  "address requires authentication. A 250 here means the rewrite never reached " +
                  "the IP-range check. Got: " + rcptReply);

               socket.Send("QUIT\r\n");
            }
         }
         finally
         {
            DisableFeatures();
         }
      }

      [Test]
      [Description("A binary PROXY protocol v2 header from a trusted peer rewrites the session's " +
                   "client address, evaluated the same way as v1: 530 at RCPT because the range " +
                   "for the asserted address requires authentication.")]
      public void ProxyV2FromTrustedPeerAppliesClientAddress()
      {
         try
         {
            AddSecurityRange("proxy-v2-authrequired", "198.51.100.78", true);
            EnableProxyProtocol("127.0.0.1");

            using (var socket = new RawSmtpSocket())
            {
               byte[] header = BuildProxyV2Header(true, 0x11 /* TCP over IPv4 */,
                  BuildTcp4AddressBlock("198.51.100.78", "127.0.0.1", 45124, 25));

               socket.Send(header);
               socket.ReadUntil("220 ");

               socket.Send("EHLO proxy.example.test\r\n");
               socket.ReadUntil("250 HELP");

               socket.Send("MAIL FROM:<" + ExternalSender + ">\r\n");
               socket.ReadUntil("250");

               socket.Send("RCPT TO:<" + _account.Address + ">\r\n");
               string rcptReply = socket.ReadUntil("\r\n");

               StringAssert.StartsWith("530", rcptReply,
                  "The RCPT must be refused with 530 because the range for the v2-asserted " +
                  "address requires authentication. Got: " + rcptReply);

               socket.Send("QUIT\r\n");
            }
         }
         finally
         {
            DisableFeatures();
         }
      }

      [Test]
      [Description("The PROXY-asserted client address is what the Received header records - the " +
                   "trace a delivered message carries names the real client, not the proxy.")]
      public void ProxyRewrittenAddressAppearsInReceivedHeader()
      {
         try
         {
            AddSecurityRange("proxy-v1-open", "198.51.100.80", false);
            EnableProxyProtocol("127.0.0.1");

            using (var socket = new RawSmtpSocket())
            {
               socket.Send("PROXY TCP4 198.51.100.80 127.0.0.1 45125 25\r\n");
               socket.ReadUntil("220 ");

               socket.Send("EHLO proxy.example.test\r\n");
               socket.ReadUntil("250 HELP");

               socket.Send("MAIL FROM:<" + ExternalSender + ">\r\n");
               socket.ReadUntil("250");

               socket.Send("RCPT TO:<" + _account.Address + ">\r\n");
               socket.ReadUntil("250");

               socket.Send("DATA\r\n");
               socket.ReadUntil("354");

               socket.Send("From: " + ExternalSender + "\r\n" +
                           "To: " + _account.Address + "\r\n" +
                           "Subject: Proxy protocol received header\r\n" +
                           "\r\n" +
                           "Body line.\r\n" +
                           ".\r\n");
               socket.ReadUntil("250");

               socket.Send("QUIT\r\n");
            }

            string message = Pop3ClientSimulator.AssertGetFirstMessageText(_account.Address, "test");

            StringAssert.Contains("198.51.100.80", message,
               "The Received header must record the PROXY-asserted client address.");
         }
         finally
         {
            DisableFeatures();
         }
      }

      [Test]
      [Description("A PROXY header from a peer that is NOT on the trusted list is a protocol " +
                   "violation that drops the connection without an SMTP reply. Ignoring it and " +
                   "carrying on would leave the real client's commands being read as though the " +
                   "proxy had sent them.")]
      public void ProxyHeaderFromUntrustedPeerDropsTheConnection()
      {
         try
         {
            // Enabled, but localhost is not the trusted proxy - so this
            // connection is an ordinary SMTP session (the banner arrives
            // first), and the header it then sends is an intrusion attempt.
            EnableProxyProtocol("192.0.2.99");

            using (var socket = new RawSmtpSocket())
            {
               socket.ReadUntil("220 ");

               socket.Send("PROXY TCP4 198.51.100.77 127.0.0.1 45123 25\r\n");

               string received = socket.ReadUntilRemoteClose();

               ClassicAssert.AreEqual("", received,
                  "The server must drop the connection without answering a PROXY header from " +
                  "an untrusted peer, but it sent: " + received);
            }
         }
         finally
         {
            DisableFeatures();
         }
      }

      [Test]
      [Description("Off by default means OFF: with the feature disabled, a PROXY line is the " +
                   "unknown command it always was (503) and the session carries on - nothing is " +
                   "rewritten and nothing is dropped.")]
      public void ProxyVerbIsInertWhenFeatureDisabled()
      {
         // No settings written: the shipped default state.
         using (var socket = new RawSmtpSocket())
         {
            socket.ReadUntil("220 ");

            socket.Send("PROXY TCP4 198.51.100.77 127.0.0.1 45123 25\r\n");
            string reply = socket.ReadUntil("\r\n");

            StringAssert.StartsWith("503", reply,
               "With the feature off, PROXY must remain an unknown command. Got: " + reply);

            socket.Send("NOOP\r\n");
            socket.ReadUntil("250");

            socket.Send("QUIT\r\n");
         }
      }

      [Test]
      [Description("PROXY v1 UNKNOWN carries no client address: per the specification the session " +
                   "must keep the real peer address, so an unauthenticated local delivery - " +
                   "permitted for localhost - still works.")]
      public void ProxyV1UnknownKeepsRealPeerAddress()
      {
         try
         {
            EnableProxyProtocol("127.0.0.1");

            using (var socket = new RawSmtpSocket())
            {
               socket.Send("PROXY UNKNOWN\r\n");
               socket.ReadUntil("220 ");

               socket.Send("EHLO proxy.example.test\r\n");
               socket.ReadUntil("250 HELP");

               socket.Send("MAIL FROM:<" + ExternalSender + ">\r\n");
               socket.ReadUntil("250");

               socket.Send("RCPT TO:<" + _account.Address + ">\r\n");
               string rcptReply = socket.ReadUntil("\r\n");

               StringAssert.StartsWith("250", rcptReply,
                  "With no address asserted, the session must run under the real (localhost) " +
                  "address, whose range does not require authentication. Got: " + rcptReply);

               socket.Send("QUIT\r\n");
            }
         }
         finally
         {
            DisableFeatures();
         }
      }

      [Test]
      [Description("A PROXY v2 LOCAL command (a health check) carries no client address: the " +
                   "session continues on the real peer address.")]
      public void ProxyV2LocalKeepsRealPeerAddress()
      {
         try
         {
            EnableProxyProtocol("127.0.0.1");

            using (var socket = new RawSmtpSocket())
            {
               byte[] header = BuildProxyV2Header(false /* LOCAL */, 0x00 /* AF_UNSPEC */, new byte[0]);

               socket.Send(header);
               socket.ReadUntil("220 ");

               socket.Send("EHLO proxy.example.test\r\n");
               socket.ReadUntil("250 HELP");

               socket.Send("NOOP\r\n");
               socket.ReadUntil("250");

               socket.Send("QUIT\r\n");
            }
         }
         finally
         {
            DisableFeatures();
         }
      }

      // ----------------------------------------------------------- XCLIENT tests

      [Test]
      [Description("XCLIENT must be invisible to untrusted peers: not advertised in EHLO (an " +
                   "advertisement invites the attempt and leaks the deployment shape) and " +
                   "answered 550 if attempted anyway - after which the session is still usable.")]
      public void XclientNotAdvertisedAndRefusedForUntrustedPeer()
      {
         try
         {
            // Enabled, but localhost is not on the trusted list.
            EnableXclient("192.0.2.99");

            using (var socket = new RawSmtpSocket())
            {
               socket.ReadUntil("220 ");

               socket.Send("EHLO client.example.test\r\n");
               string ehloReply = socket.ReadUntil("250 HELP");

               ClassicAssert.IsFalse(ehloReply.Contains("XCLIENT"),
                  "XCLIENT must not be advertised to an untrusted peer. EHLO reply: " + ehloReply);

               socket.Send("XCLIENT ADDR=198.51.100.77\r\n");
               string xclientReply = socket.ReadUntil("\r\n");

               StringAssert.StartsWith("550", xclientReply,
                  "XCLIENT from an untrusted peer must be answered 550. Got: " + xclientReply);

               socket.Send("NOOP\r\n");
               socket.ReadUntil("250");

               socket.Send("QUIT\r\n");
            }
         }
         finally
         {
            DisableFeatures();
         }
      }

      [Test]
      [Description("XCLIENT from a trusted peer: advertised in EHLO, ADDR accepted with a fresh " +
                   "220 greeting, re-EHLO works, and the asserted address is what the IP-range " +
                   "auth requirement evaluates (530 at RCPT). Negative control built in: were " +
                   "XCLIENT inert, the localhost range would answer the RCPT 250.")]
      public void XclientFromTrustedPeerRewritesAddressAndReEhloWorks()
      {
         try
         {
            AddSecurityRange("xclient-authrequired", "198.51.100.81", true);
            EnableXclient("127.0.0.1");

            using (var socket = new RawSmtpSocket())
            {
               socket.ReadUntil("220 ");

               socket.Send("EHLO upstream.example.test\r\n");
               string ehloReply = socket.ReadUntil("250 HELP");

               StringAssert.Contains("XCLIENT ADDR NAME PORT PROTO HELO LOGIN", ehloReply,
                  "XCLIENT must be advertised to a trusted peer. EHLO reply: " + ehloReply);

               socket.Send("XCLIENT ADDR=198.51.100.81 NAME=client.example.org PORT=45200\r\n");
               string xclientReply = socket.ReadUntil("\r\n");

               StringAssert.StartsWith("220", xclientReply,
                  "A successful XCLIENT is answered with a fresh 220 greeting. Got: " + xclientReply);

               // The session restarted: the upstream re-issues EHLO.
               socket.Send("EHLO client.example.org\r\n");
               socket.ReadUntil("250 HELP");

               socket.Send("MAIL FROM:<" + ExternalSender + ">\r\n");
               socket.ReadUntil("250");

               socket.Send("RCPT TO:<" + _account.Address + ">\r\n");
               string rcptReply = socket.ReadUntil("\r\n");

               StringAssert.StartsWith("530", rcptReply,
                  "The RCPT must be refused with 530 because the range for the XCLIENT-asserted " +
                  "address requires authentication. A 250 here means the rewrite never reached " +
                  "the IP-range check. Got: " + rcptReply);

               socket.Send("QUIT\r\n");
            }
         }
         finally
         {
            DisableFeatures();
         }
      }

      [Test]
      [Description("XCLIENT with the defined \"don't know\" values [UNAVAILABLE]/[TEMPUNAVAIL] is " +
                   "accepted (220) but must not rewrite anything: the session keeps the real " +
                   "peer address, under which an unauthenticated local delivery still works.")]
      public void XclientUnavailableValuesAcceptedWithoutRewrite()
      {
         try
         {
            EnableXclient("127.0.0.1");

            using (var socket = new RawSmtpSocket())
            {
               socket.ReadUntil("220 ");

               socket.Send("EHLO upstream.example.test\r\n");
               socket.ReadUntil("250 HELP");

               socket.Send("XCLIENT ADDR=[UNAVAILABLE] NAME=[TEMPUNAVAIL]\r\n");
               string xclientReply = socket.ReadUntil("\r\n");

               StringAssert.StartsWith("220", xclientReply,
                  "XCLIENT with [UNAVAILABLE]/[TEMPUNAVAIL] values must be accepted. Got: " + xclientReply);

               socket.Send("EHLO upstream.example.test\r\n");
               socket.ReadUntil("250 HELP");

               socket.Send("MAIL FROM:<" + ExternalSender + ">\r\n");
               socket.ReadUntil("250");

               socket.Send("RCPT TO:<" + _account.Address + ">\r\n");
               string rcptReply = socket.ReadUntil("\r\n");

               StringAssert.StartsWith("250", rcptReply,
                  "With no address asserted, the session must still run under the real " +
                  "(localhost) address. Got: " + rcptReply);

               socket.Send("QUIT\r\n");
            }
         }
         finally
         {
            DisableFeatures();
         }
      }
   }
}
