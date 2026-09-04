// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.SSL.StartTls
{
   [TestFixture]
   public class SmtpServerTests : TestFixtureBase
   {
      [OneTimeSetUp]
      public new void TestFixtureSetUp()
      {
         SslSetup.SetupSSLPorts(_application);

         Thread.Sleep(1000);
      }

      [Test]
      public void IfStartTlsNotEnabledStartTlsShouldNotBeShownInEhloResponse()
      {
         var smtpClientSimulator = new TcpConnection();
         smtpClientSimulator.Connect(25);
         var data1 = smtpClientSimulator.Receive();
         var data = smtpClientSimulator.SendAndReceive("EHLO example.com\r\n");

         Assert.IsFalse(data.Contains("STARTTLS"));
      }

      [Test]
      public void IfStartTlsIsEnabledStartTlsShouldBeShownInEhloResponse()
      {
         var smtpClientSimulator = new TcpConnection();
         smtpClientSimulator.Connect(25002);
         var data1 = smtpClientSimulator.Receive();
         var data = smtpClientSimulator.SendAndReceive("EHLO example.com\r\n");

         Assert.IsTrue(data.Contains("STARTTLS"));
      }

      [Test]
      public void StartTlsCommandShouldSwithToTls()
      {
         var smtpClientSimulator = new TcpConnection();
         smtpClientSimulator.Connect(25002);
         var banner = smtpClientSimulator.Receive();
         var capabilities1 = smtpClientSimulator.SendAndReceive("EHLO example.com\r\n");
         Assert.IsTrue(capabilities1.Contains("STARTTLS"));

         smtpClientSimulator.SendAndReceive("STARTTLS\r\n");
         smtpClientSimulator.HandshakeAsClient();

         // Send a command over TLS.
         var capabilities2 = smtpClientSimulator.SendAndReceive("EHLO example.com\r\n");
         Assert.IsFalse(capabilities2.Contains("STARTTLS"));

         // We're now on SSL.
      }

      [Test]
      [Description("A trailing space should be allowed to be compatible with malfunctioning servers.")]
      public void StartTlsWithTrailingSpaceShouldBeAccepted()
      {
         var smtpClientSimulator = new TcpConnection();
         smtpClientSimulator.Connect(25002);
         var banner = smtpClientSimulator.Receive();
         var capabilities1 = smtpClientSimulator.SendAndReceive("EHLO example.com\r\n");
         Assert.IsTrue(capabilities1.Contains("STARTTLS"));

         var startTlsResultText = smtpClientSimulator.SendAndReceive("STARTTLS \r\n");
         StringAssert.Contains("220 Ready to start TLS", startTlsResultText);

         smtpClientSimulator.HandshakeAsClient();

         // Send a command over TLS.
         var capabilities2 = smtpClientSimulator.SendAndReceive("EHLO example.com\r\n");
         Assert.IsFalse(capabilities2.Contains("STARTTLS"));
      }

      [Test]
      public void StartTlsWithParametersShouldBeRejected()
      {
         var smtpClientSimulator = new TcpConnection();
         smtpClientSimulator.Connect(25002);
         var banner = smtpClientSimulator.Receive();
         var capabilities1 = smtpClientSimulator.SendAndReceive("EHLO example.com\r\n");
         Assert.IsTrue(capabilities1.Contains("STARTTLS"));

         var startTlsResultText = smtpClientSimulator.SendAndReceive("STARTTLS A=B\r\n");
         StringAssert.Contains("501 5.5.4 Syntax error (no parameters allowed)", startTlsResultText);
      }


      [Test]
      public void HandshakeCompletionShouldBeLoggedWithCipherDetails()
      {
         var smtpClientSimulator = new TcpConnection();
         smtpClientSimulator.Connect(25002);
         var banner = smtpClientSimulator.Receive();
         var capabilities1 = smtpClientSimulator.SendAndReceive("EHLO example.com\r\n");
         Assert.IsTrue(capabilities1.Contains("STARTTLS"));

         smtpClientSimulator.SendAndReceive("STARTTLS\r\n");
         smtpClientSimulator.HandshakeAsClient();

         var capabilities2 = smtpClientSimulator.SendAndReceive("EHLO example.com\r\n");

         var default_log = LogHandler.ReadCurrentDefaultLog();

         Assert.IsTrue(default_log.Contains("Version: TLS"));
         Assert.IsTrue(default_log.Contains("Cipher: "));
         Assert.IsTrue(default_log.Contains("Bits: "));
      }

      [Test]
      public void IfStlsRequiredLogonShouldSucceedIfStls()
      {
         var smtpClientSimulator = new TcpConnection();
         smtpClientSimulator.Connect(25003);
         var banner = smtpClientSimulator.Receive();
         var capabilities1 = smtpClientSimulator.SendAndReceive("EHLO example.com\r\n");
         Assert.IsTrue(capabilities1.Contains("STARTTLS"));

         smtpClientSimulator.SendAndReceive("STARTTLS\r\n");
         smtpClientSimulator.HandshakeAsClient();

         // The greeting is discarded by the handshake (RFC 3207 section 4.2), so
         // AUTH - an extension the server has not yet advertised on the encrypted
         // session - needs the EHLO first, exactly as every real client sends it.
         smtpClientSimulator.SendAndReceive("EHLO example.com\r\n");

         var loginResult = smtpClientSimulator.SendAndReceive("AUTH LOGIN\r\n");
         Assert.IsTrue(loginResult.StartsWith("334"));
      }

      [Test]
      public void IfStlsRequiredLogonShouldFailIfNoStls()
      {
         var smtpClientSimulator = new TcpConnection();
         smtpClientSimulator.Connect(25003);
         var banner = smtpClientSimulator.Receive();
         var capabilities1 = smtpClientSimulator.SendAndReceive("EHLO example.com\r\n");
         Assert.IsTrue(capabilities1.Contains("STARTTLS"));

         var loginResult = smtpClientSimulator.SendAndReceive("AUTH LOGIN\r\n");
         Assert.IsTrue(loginResult.StartsWith("530 5.7.0 Must issue STARTTLS first."));
      }

      [Test]
      public void IfStlsOptionalButSslRequiredByIpRangeForAuthThenAuthShouldFail()
      {
         var range = SingletonProvider<TestSetup>.Instance.GetApp().Settings.SecurityRanges
            .get_ItemByName("My computer");
         range.RequireSSLTLSForAuth = true;
         range.Save();

         var smtpClientSimulator = new TcpConnection();
         smtpClientSimulator.Connect(25002);
         var banner = smtpClientSimulator.Receive();
         var capabilities1 = smtpClientSimulator.SendAndReceive("EHLO example.com\r\n");
         Assert.IsTrue(capabilities1.Contains("STARTTLS"));

         var loginResult = smtpClientSimulator.SendAndReceive("AUTH LOGIN\r\n");
         Assert.IsTrue(
            loginResult.StartsWith(
               "530 5.7.0 A SSL/TLS-connection is required for authentication.")); // must run starttls first.
      }


      [Test]
      public void TestPlaintextCommandInjection()
      {
         var smtpClientSimulator = new TcpConnection();
         smtpClientSimulator.Connect(25002);
         var banner = smtpClientSimulator.Receive();
         var capabilities1 = smtpClientSimulator.SendAndReceive("EHLO example.com\r\n");
         Assert.IsTrue(capabilities1.Contains("STARTTLS"));

         var resp = smtpClientSimulator.SendAndReceive("STARTTLS\r\nRSET\r\n");
         Assert.AreEqual("220 Ready to start TLS\r\n", resp);
         smtpClientSimulator.HandshakeAsClient();

         var quitResponse = smtpClientSimulator.SendAndReceive("QUIT\r\n");
         Assert.AreEqual(quitResponse, "221 goodbye\r\n");

         var default_log = LogHandler.ReadCurrentDefaultLog();
         Assert.IsFalse(default_log.Contains("RSET"));
      }

      /// <summary>
      ///    RFC 3207 section 4.2: the handshake resets the session to the
      ///    just-greeted state and the client must EHLO again. Until the port of
      ///    upstream #605 the state machine stayed in the transaction state, so
      ///    MAIL FROM went through on the encrypted session with the HELO host
      ///    already forgotten - which skipped the HELO-host spam test and the
      ///    OnHELO/OnEHLO script events for that session.
      /// </summary>
      [Test]
      [Description("MAIL FROM straight after the TLS handshake, without a fresh EHLO, is a bad sequence of commands.")]
      public void MailFromWithoutAFreshEhloAfterStartTlsIsRefused()
      {
         var smtpClientSimulator = new TcpConnection();
         smtpClientSimulator.Connect(25002);
         smtpClientSimulator.Receive();
         var capabilities = smtpClientSimulator.SendAndReceive("EHLO example.com\r\n");
         Assert.IsTrue(capabilities.Contains("STARTTLS"));

         smtpClientSimulator.SendAndReceive("STARTTLS\r\n");
         smtpClientSimulator.HandshakeAsClient();

         var mailFrom = smtpClientSimulator.SendAndReceive("MAIL FROM:<test@example.com>\r\n");
         StringAssert.StartsWith("503", mailFrom,
            "The greeting is discarded by STARTTLS, so a transaction may not start until the client has said EHLO again. Got: " + mailFrom);

         // And after the fresh EHLO the same command is fine.
         smtpClientSimulator.SendAndReceive("EHLO example.com\r\n");
         mailFrom = smtpClientSimulator.SendAndReceive("MAIL FROM:<test@example.com>\r\n");
         StringAssert.StartsWith("250", mailFrom, "After EHLO the transaction must start normally. Got: " + mailFrom);
      }

      /// <summary>
      ///    The same hole from the other direction, and it needs no TLS at all:
      ///    RSET is valid before EHLO (RFC 5321 section 4.1.4) and used to move
      ///    the state machine into the transaction state by itself (upstream
      ///    #604).
      /// </summary>
      [Test]
      [Description("RSET before any EHLO must not open a transaction: MAIL FROM afterwards is still a bad sequence of commands.")]
      public void ResetBeforeTheGreetingDoesNotSkipIt()
      {
         var smtpClientSimulator = new TcpConnection();
         smtpClientSimulator.Connect(25);
         smtpClientSimulator.Receive();

         var reset = smtpClientSimulator.SendAndReceive("RSET\r\n");
         StringAssert.StartsWith("250", reset, "RSET is permitted before EHLO and must succeed. Got: " + reset);

         var mailFrom = smtpClientSimulator.SendAndReceive("MAIL FROM:<test@example.com>\r\n");
         StringAssert.StartsWith("503", mailFrom,
            "No EHLO or HELO has been given, so MAIL FROM must be refused. Got: " + mailFrom);
      }
   }
}