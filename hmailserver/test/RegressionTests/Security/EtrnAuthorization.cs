// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Text;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using RegressionTests.SSL;

namespace RegressionTests.Security
{
   /// <summary>
   /// ETRN (RFC 1985) makes the server release the mail it is holding for a route -
   /// it asks the server to start relaying on the caller's behalf. It used to be the
   /// one SMTP command that was answered with no checks whatsoever: the
   /// STARTTLS-required gate was applied to RSET, MAIL, RCPT, HELP, DATA, BDAT and
   /// AUTH but not to ETRN, and no authentication or IP-range check was made either.
   /// A remote party could therefore trigger a queue flush in cleartext on a port
   /// which requires TLS, and without ever proving who it was.
   ///
   /// These tests pin both halves of the gate. They also pin the other direction,
   /// which matters just as much: ETRN is a legitimate feature for route domains, so
   /// a client which is entitled to relay - because it authenticated, or because its
   /// IP range may relay without authenticating - must still be served. Refusing
   /// those would be worse than the hole being closed.
   /// </summary>
   [TestFixture]
   public class EtrnAuthorization : TestFixtureBase
   {
      // The STARTTLS-required SMTP port created by SslSetup.SetupSSLPorts.
      private const int StartTlsRequiredPort = 25003;

      [OneTimeSetUp]
      public new void TestFixtureSetUp()
      {
         SslSetup.SetupSSLPorts(_application);

         Thread.Sleep(1000);
      }

      private static string EncodeBase64(string s)
      {
         return Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
      }

      private static TcpConnection ConnectAndEhlo(int port)
      {
         var socket = new TcpConnection();
         Assert.IsTrue(socket.Connect(port));
         Assert.IsTrue(socket.Receive().StartsWith("220"));
         Assert.IsTrue(socket.SendAndReceive("EHLO example.test\r\n").StartsWith("250"));
         return socket;
      }

      [Test]
      [Description("ETRN must not be processed before TLS on a STARTTLS-required port")]
      public void EtrnBeforeStartTlsIsRefused()
      {
         var socket = ConnectAndEhlo(StartTlsRequiredPort);
         var result = socket.SendAndReceive("ETRN example.test\r\n");
         socket.Disconnect();

         // Before the fix the command was carried out: the route lookup ran and
         // answered "501 ETRN not supported for example.test", which means a queue
         // flush could be requested over an unencrypted connection on a port whose
         // whole purpose is to require encryption.
         Assert.IsTrue(result.StartsWith("530 5.7.0 Must issue STARTTLS first."), result);
      }

      [Test]
      [Description("ETRN must be refused when the client is neither authenticated nor permitted to relay")]
      public void EtrnFromClientNotPermittedToRelayIsRefused()
      {
         // Deny this IP the right to relay to a remote destination without
         // authenticating - the same configuration the default "Internet" range ships
         // with, expressed for the loopback range the tests connect from.
         var range = SingletonProvider<TestSetup>.Instance.GetApp()
            .Settings.SecurityRanges.get_ItemByName("My computer");
         range.RequireSMTPAuthLocalToExternal = true;
         range.RequireSMTPAuthExternalToExternal = true;
         range.Save();

         var socket = ConnectAndEhlo(25);
         var result = socket.SendAndReceive("ETRN example.test\r\n");
         socket.Disconnect();

         // Before the fix this was answered by the route lookup (a 5xx, but only
         // because the domain happens not to be a route) - the queue flush itself was
         // never gated on anything.
         Assert.IsTrue(result.StartsWith("530"), result);
         StringAssert.Contains("authentication is required", result);
      }

      [Test]
      [Description("An authenticated client may still use ETRN when the range requires authentication")]
      public void EtrnFromAuthenticatedClientIsProcessed()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "etrn@example.test", "test");

         var range = SingletonProvider<TestSetup>.Instance.GetApp()
            .Settings.SecurityRanges.get_ItemByName("My computer");
         range.RequireSMTPAuthLocalToExternal = true;
         range.RequireSMTPAuthExternalToExternal = true;
         range.Save();

         var socket = ConnectAndEhlo(25);

         var challenge = socket.SendAndReceive("AUTH LOGIN " + EncodeBase64("etrn@example.test") + "\r\n");
         Assert.IsTrue(challenge.StartsWith("334"), challenge);

         var authResult = socket.SendAndReceive(EncodeBase64("test") + "\r\n");
         Assert.IsTrue(authResult.StartsWith("235"), authResult);

         var result = socket.SendAndReceive("ETRN example.test\r\n");
         socket.Disconnect();

         // example.test is not a route, so the route lookup answers 501. The point is
         // that the command was carried out at all: an authenticated client must not
         // be turned away with 530.
         Assert.IsFalse(result.StartsWith("530"), result);
         StringAssert.Contains("ETRN not supported", result);
      }

      [Test]
      [Description("ETRN is still served for a range that may relay without authenticating")]
      public void EtrnFromRangePermittedToRelayIsProcessed()
      {
         // The default loopback range ("My computer") allows delivery from local to
         // remote and does not require SMTP authentication for it, so an
         // unauthenticated ETRN from here must keep working exactly as before.
         var socket = ConnectAndEhlo(25);
         var result = socket.SendAndReceive("ETRN example.test\r\n");
         socket.Disconnect();

         Assert.IsFalse(result.StartsWith("530"), result);
         StringAssert.Contains("ETRN not supported", result);
      }

      [Test]
      [Description("ETRN is served once TLS has been established on a STARTTLS-required port")]
      public void EtrnAfterStartTlsIsProcessed()
      {
         var socket = ConnectAndEhlo(StartTlsRequiredPort);

         socket.SendAndReceive("STARTTLS\r\n");
         socket.HandshakeAsClient();

         Assert.IsTrue(socket.SendAndReceive("EHLO example.test\r\n").StartsWith("250"));

         var result = socket.SendAndReceive("ETRN example.test\r\n");
         socket.Disconnect();

         Assert.IsFalse(result.StartsWith("530"), result);
         StringAssert.Contains("ETRN not supported", result);
      }

      [Test]
      [Description("VRFY is not answered before TLS on a STARTTLS-required port either")]
      public void VrfyBeforeStartTlsIsRefused()
      {
         var socket = ConnectAndEhlo(StartTlsRequiredPort);
         var result = socket.SendAndReceive("VRFY postmaster\r\n");
         socket.Disconnect();

         // VRFY only ever answers 502, so this is defence in depth: no command other
         // than NOOP, EHLO, STARTTLS and QUIT should be answered before TLS.
         Assert.IsTrue(result.StartsWith("530 5.7.0 Must issue STARTTLS first."), result);
      }
   }
}
