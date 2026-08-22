// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Shared;
using RegressionTests.Infrastructure;

namespace RegressionTests.SMTP
{
   /// <summary>
   /// B4 deliverability and SMTP-standards features: PIPELINING (RFC 2920),
   /// SMTPUTF8/EAI (RFC 6531/6532), ENHANCEDSTATUSCODES (RFC 2034) and
   /// DSN (RFC 3461).
   /// </summary>
   [TestFixture]
   public class Deliverability : TestFixtureBase
   {
      private string EhloAndGetCapabilities(TcpConnection socket, string helo)
      {
         Assert.IsTrue(socket.Receive().StartsWith("220"));
         socket.Send("EHLO " + helo + "\r\n");
         return socket.ReadUntil("250 HELP");
      }

      [Test]
      public void TestEhloAdvertisesNewKeywords()
      {
         var socket = new TcpConnection();
         Assert.IsTrue(socket.Connect(25));
         var capabilities = EhloAndGetCapabilities(socket, "example.com");
         socket.Disconnect();

         Assert.IsTrue(capabilities.Contains("PIPELINING"), "PIPELINING not advertised: " + capabilities);
         Assert.IsTrue(capabilities.Contains("SMTPUTF8"), "SMTPUTF8 not advertised: " + capabilities);
         Assert.IsTrue(capabilities.Contains("ENHANCEDSTATUSCODES"),
            "ENHANCEDSTATUSCODES not advertised: " + capabilities);
      }

      [Test]
      public void TestEnhancedStatusCodeOnMailFromAfterEhlo()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "esc@example.test", "test");

         var socket = new TcpConnection();
         Assert.IsTrue(socket.Connect(25));
         EhloAndGetCapabilities(socket, "example.com");

         // After EHLO the server must prefix the RFC 3463 enhanced status code.
         var mailFrom = socket.SendAndReceive("MAIL FROM:<sender@example.test>\r\n");
         Assert.IsTrue(mailFrom.StartsWith("250 2.1.0"), "Expected enhanced code on MAIL FROM. Got: " + mailFrom);

         var rcptTo = socket.SendAndReceive("RCPT TO:<esc@example.test>\r\n");
         Assert.IsTrue(rcptTo.StartsWith("250 2.1.5"), "Expected enhanced code on RCPT TO. Got: " + rcptTo);

         socket.Send("QUIT\r\n");
         socket.Disconnect();
      }

      [Test]
      public void TestNoEnhancedStatusCodeAfterHelo()
      {
         var socket = new TcpConnection();
         Assert.IsTrue(socket.Connect(25));
         Assert.IsTrue(socket.Receive().StartsWith("220"));

         socket.Send("HELO example.com\r\n");
         Assert.IsTrue(socket.Receive().StartsWith("250"));

         // HELO selects basic SMTP, so the response must not carry an enhanced code.
         var mailFrom = socket.SendAndReceive("MAIL FROM:<sender@example.test>\r\n");
         Assert.AreEqual("250 OK\r\n", mailFrom);

         socket.Send("QUIT\r\n");
         socket.Disconnect();
      }

      [Test]
      public void TestSmtpUtf8SenderAccepted()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "eai@example.test", "test");

         var socket = new TcpConnection();
         Assert.IsTrue(socket.Connect(25));
         EhloAndGetCapabilities(socket, "example.com");

         // A sender whose domain is internationalized (UTF-8) is accepted when the
         // client declares SMTPUTF8 (RFC 6531).
         var mailFrom = socket.SendAndReceive("MAIL FROM:<\u7528\u6237@\u4f8b\u5b50.com> SMTPUTF8\r\n");
         Assert.IsTrue(mailFrom.StartsWith("250"), "UTF-8 sender with SMTPUTF8 should be accepted. Got: " + mailFrom);

         var rcptTo = socket.SendAndReceive("RCPT TO:<eai@example.test>\r\n");
         Assert.IsTrue(rcptTo.StartsWith("250"), "Recipient should be accepted. Got: " + rcptTo);

         socket.Send("QUIT\r\n");
         socket.Disconnect();
      }

      [Test]
      public void TestUtf8SenderRejectedWithoutSmtpUtf8()
      {
         var socket = new TcpConnection();
         Assert.IsTrue(socket.Connect(25));
         EhloAndGetCapabilities(socket, "example.com");

         // Without SMTPUTF8 an internationalized domain must be rejected by the
         // strict (ASCII) validator.
         var mailFrom = socket.SendAndReceive("MAIL FROM:<\u7528\u6237@\u4f8b\u5b50.com>\r\n");
         Assert.IsFalse(mailFrom.StartsWith("250"),
            "UTF-8 sender without SMTPUTF8 should be rejected. Got: " + mailFrom);

         socket.Send("QUIT\r\n");
         socket.Disconnect();
      }

      [Test]
      public void TestPipelinedTransactionDelivers()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "pipe@example.test", "test");

         var socket = new TcpConnection();
         Assert.IsTrue(socket.Connect(25));
         EhloAndGetCapabilities(socket, "example.com");

         // Stream MAIL FROM, RCPT TO and DATA as a single pipelined group (RFC 2920).
         socket.Send("MAIL FROM:<sender@example.test>\r\n" +
                     "RCPT TO:<pipe@example.test>\r\n" +
                     "DATA\r\n");

         // The server must answer each command in order; the DATA reply is 354.
         var grouped = socket.ReadUntil("354");
         Assert.IsTrue(grouped.Contains("354"), "Expected 354 in pipelined replies. Got: " + grouped);

         socket.Send("Subject: pipelined\r\n\r\nPipelined body\r\n.\r\n");
         var queued = socket.ReadUntil("250");
         Assert.IsTrue(queued.Contains("250"), "Message not accepted after DATA. Got: " + queued);

         socket.Send("QUIT\r\n");
         socket.Disconnect();

         Pop3ClientSimulator.AssertMessageCount("pipe@example.test", "test", 1);
      }

      [Test]
      public void TestEhloAdvertisesDsn()
      {
         var socket = new TcpConnection();
         Assert.IsTrue(socket.Connect(25));
         var capabilities = EhloAndGetCapabilities(socket, "example.com");
         socket.Disconnect();

         Assert.IsTrue(capabilities.Contains("DSN"), "DSN not advertised: " + capabilities);
      }

      [Test]
      public void TestMailFromAcceptsRetAndEnvid()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "dsn1@example.test", "test");

         var socket = new TcpConnection();
         Assert.IsTrue(socket.Connect(25));
         EhloAndGetCapabilities(socket, "example.com");

         // RET and ENVID are valid DSN parameters on MAIL FROM (RFC 3461).
         var mailFrom = socket.SendAndReceive(
            "MAIL FROM:<sender@example.test> RET=HDRS ENVID=abc123\r\n");
         Assert.IsTrue(mailFrom.StartsWith("250"),
            "MAIL FROM with RET/ENVID should be accepted. Got: " + mailFrom);

         var rcptTo = socket.SendAndReceive("RCPT TO:<dsn1@example.test>\r\n");
         Assert.IsTrue(rcptTo.StartsWith("250"), "Recipient should be accepted. Got: " + rcptTo);

         socket.Send("QUIT\r\n");
         socket.Disconnect();
      }

      [Test]
      public void TestMailFromRejectsInvalidRet()
      {
         var socket = new TcpConnection();
         Assert.IsTrue(socket.Connect(25));
         EhloAndGetCapabilities(socket, "example.com");

         // RET only allows FULL or HDRS; anything else is a syntax error.
         var mailFrom = socket.SendAndReceive("MAIL FROM:<sender@example.test> RET=BOGUS\r\n");
         Assert.IsTrue(mailFrom.StartsWith("501"),
            "Invalid RET should be rejected with 501. Got: " + mailFrom);

         socket.Send("QUIT\r\n");
         socket.Disconnect();
      }

      [Test]
      public void TestMailFromRejectsInvalidEnvid()
      {
         var socket = new TcpConnection();
         Assert.IsTrue(socket.Connect(25));
         EhloAndGetCapabilities(socket, "example.com");

         // ENVID is xtext: a bare '=' is not allowed (must be encoded as +3D).
         var mailFrom = socket.SendAndReceive("MAIL FROM:<sender@example.test> ENVID=bad=value\r\n");
         Assert.IsTrue(mailFrom.StartsWith("501"),
            "Invalid ENVID xtext should be rejected with 501. Got: " + mailFrom);

         socket.Send("QUIT\r\n");
         socket.Disconnect();
      }

      [Test]
      public void TestRcptToAcceptsNotifyAndOrcpt()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "dsn2@example.test", "test");

         var socket = new TcpConnection();
         Assert.IsTrue(socket.Connect(25));
         EhloAndGetCapabilities(socket, "example.com");

         socket.SendAndReceive("MAIL FROM:<sender@example.test>\r\n");

         // NOTIFY and ORCPT are valid DSN parameters on RCPT TO (RFC 3461).
         var rcptTo = socket.SendAndReceive(
            "RCPT TO:<dsn2@example.test> NOTIFY=SUCCESS,FAILURE,DELAY ORCPT=rfc822;dsn2@example.test\r\n");
         Assert.IsTrue(rcptTo.StartsWith("250"),
            "RCPT TO with NOTIFY/ORCPT should be accepted. Got: " + rcptTo);

         socket.Send("QUIT\r\n");
         socket.Disconnect();
      }

      [Test]
      public void TestRcptToAcceptsNotifyNever()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "dsn3@example.test", "test");

         var socket = new TcpConnection();
         Assert.IsTrue(socket.Connect(25));
         EhloAndGetCapabilities(socket, "example.com");

         socket.SendAndReceive("MAIL FROM:<sender@example.test>\r\n");

         // NOTIFY=NEVER is the opt-out value and is accepted on its own.
         var rcptTo = socket.SendAndReceive("RCPT TO:<dsn3@example.test> NOTIFY=NEVER\r\n");
         Assert.IsTrue(rcptTo.StartsWith("250"),
            "RCPT TO with NOTIFY=NEVER should be accepted. Got: " + rcptTo);

         socket.Send("QUIT\r\n");
         socket.Disconnect();
      }

      [Test]
      public void TestRcptToRejectsInvalidNotify()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "dsn4@example.test", "test");

         var socket = new TcpConnection();
         Assert.IsTrue(socket.Connect(25));
         EhloAndGetCapabilities(socket, "example.com");

         socket.SendAndReceive("MAIL FROM:<sender@example.test>\r\n");

         // NEVER may not be combined with other keywords, and unknown keywords
         // are a syntax error.
         var combined = socket.SendAndReceive("RCPT TO:<dsn4@example.test> NOTIFY=NEVER,SUCCESS\r\n");
         Assert.IsTrue(combined.StartsWith("501"),
            "NOTIFY=NEVER,SUCCESS should be rejected with 501. Got: " + combined);

         var unknown = socket.SendAndReceive("RCPT TO:<dsn4@example.test> NOTIFY=BOGUS\r\n");
         Assert.IsTrue(unknown.StartsWith("501"),
            "Unknown NOTIFY keyword should be rejected with 501. Got: " + unknown);

         socket.Send("QUIT\r\n");
         socket.Disconnect();
      }
   }
}
