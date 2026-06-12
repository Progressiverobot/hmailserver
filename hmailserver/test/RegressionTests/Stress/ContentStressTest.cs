// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System.IO;
using System.Net.Sockets;
using System.Text;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.Stress
{
   [TestFixture]
   public class ContentStressTest : TestFixtureBase
   {
      [Test]
      public void TestLongLineInData()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");
         var sb = new StringBuilder();
         for (var i = 0; i < 11000; i++) sb.Append("1234567890");

         var sim = new SmtpClientSimulator();
         CustomAsserts.Throws<DeliveryFailedException>(() =>
            sim.SendRaw("test@example.test", "test@example.test", sb.ToString()));
      }


      /// <summary>
      ///    RFC 2683, 3.2.1.5. Long Command Lines
      ///    A client should limit the length of the command lines it generates to
      ///    approximately 1000 octets (including all quoted strings but not
      ///    including literals).  If the client is unable to group things into
      ///    ranges so that the command line is within that length, it should
      ///    split the request into multiple commands.  The client should use
      ///    literals instead of long quoted strings, in order to keep the command
      ///    length down.
      ///    For its part, a server should allow for a command line of at least
      ///    8000 octets.  This provides plenty of leeway for accepting reasonable
      ///    length commands from clients.  The server should send a BAD response
      ///    to a command that does not end within the server's maximum accepted
      ///    command length.
      /// </summary>
      [Test]
      public void TestExcessiveDataInIMAPConversation()
      {
         var sb = new StringBuilder();
         for (var i = 0; i < 100000; i++) sb.Append("1234567890");

         sb.Append(".com\r\n");

         var command = "A03 NOOP " + sb;

         var socket = new TcpConnection();
         Assert.IsTrue(socket.Connect(143));
         socket.Receive();
         socket.Send(command);

         try
         {
            var response = socket.Receive();
            Assert.IsTrue(response.StartsWith("* BYE"));
         }
         catch (IOException ex)
         {
            AssertIsConnectionTerminatedException(ex);
         }


         socket.Disconnect();
      }

      [Test]
      public void TestExcessiveDataInPOP3Conversation()
      {
         var sb = new StringBuilder();
         for (var i = 0; i < 100000; i++) sb.Append("1234567890");


         var command = "HELP " + sb;

         var socket = new TcpConnection();
         Assert.IsTrue(socket.Connect(110));
         socket.Receive();
         socket.Send(command + "\r\n");

         try
         {
            var response = socket.Receive();
            Assert.IsTrue(response.StartsWith("-ERR"));

            socket.Disconnect();
         }
         catch (IOException ex)
         {
            AssertIsConnectionTerminatedException(ex);
         }
      }


      [Test]
      public void TestLongLineInPOP3Conversation()
      {
         var sb = new StringBuilder();
         for (var i = 0; i < 400; i++) sb.Append("1234567890");

         sb.Append(".com");

         var command = "NOOP " + sb;

         var socket = new TcpConnection();
         Assert.IsTrue(socket.Connect(110));
         socket.Receive();
         socket.Send(command + "\r\n");

         var response = socket.Receive();
         Assert.IsTrue(response.StartsWith("-ERR Line too long."));

         socket.Disconnect();
      }

      [Test]
      public void TestExcessiveDataInSMTPConversation()
      {
         var sb = new StringBuilder();
         for (var i = 0; i < 100000; i++) sb.Append("1234567890");

         sb.Append(".com");

         var command = "HELO " + sb;

         var socket = new TcpConnection();
         Assert.IsTrue(socket.Connect(25));
         socket.Receive();
         socket.Send(command + "\r\n");

         try
         {
            var response = socket.Receive();
            Assert.IsTrue(response.StartsWith("421"));

            socket.Disconnect();
         }
         catch (IOException ex)
         {
            AssertIsConnectionTerminatedException(ex);
         }
      }

      [Test]
      public void TestLongLineInSMTPConversation()
      {
         var sb = new StringBuilder();
         for (var i = 0; i < 400; i++) sb.Append("1234567890");

         sb.Append(".com");

         var command = "HELO " + sb;

         var socket = new TcpConnection();
         Assert.IsTrue(socket.Connect(25));
         socket.Receive();
         socket.Send(command + "\r\n");

         var response = socket.Receive();
         Assert.IsTrue(response.StartsWith("500"));

         socket.Disconnect();
      }

      /// <summary>
      ///    Test to send a message where the MIME part boundaries overlap.
      ///    i.e. one boundary string is a part of another boundary string.
      /// </summary>
      [Test]
      public void TestOverlappingBoundaryNames()
      {
         var content = "Return-Path: <check@example.test>\r\n" +
                       "From: \"test\" <check@example.test>\r\n" +
                       "To: \"Test\" <test@example.test>\r\n" +
                       "Subject: rtest\r\n" +
                       "Date: Thu, 22 Jan 2009 13:20:32 +0100\r\n" +
                       "MIME-Version: 1.0\r\n" +
                       "Content-Type: multipart/mixed;\r\n" +
                       "    boundary=\"----=_NextPart_000_000D_01C97C94.33D5E670\"\r\n" +
                       "\r\n" +
                       "This is a multi-part message in MIME format.\r\n" +
                       "\r\n" +
                       "------=_NextPart_000_000D_01C97C94.33D5E670\r\n" +
                       "Content-Type: multipart/alternative;\r\n" +
                       "    boundary=\"----=_NextPart_000_000D_01C97C94.33D5E670.ALT\"\r\n" +
                       "\r\n" +
                       "\r\n" +
                       "------=_NextPart_000_000D_01C97C94.33D5E670.ALT\r\n" +
                       "Content-Type: text/plain;\r\n" +
                       "    charset=\"iso-8859-1\"\r\n" +
                       "Content-Transfer-Encoding: quoted-printable\r\n" +
                       "\r\n" +
                       "test\r\n" +
                       "------=_NextPart_000_000D_01C97C94.33D5E670.ALT\r\n" +
                       "Content-Type: text/html;\r\n" +
                       "    charset=\"iso-8859-1\"\r\n" +
                       "Content-Transfer-Encoding: quoted-printable\r\n" +
                       "\r\n" +
                       "<a>test</a>\r\n" +
                       "\r\n" +
                       "------=_NextPart_000_000D_01C97C94.33D5E670.ALT--\r\n" +
                       "\r\n" +
                       "------=_NextPart_000_000D_01C97C94.33D5E670\r\n" +
                       "Content-Type: application/octet-stream;\r\n" +
                       "    name=\"test.vbs\"\r\n" +
                       "Content-Transfer-Encoding: 7bit\r\n" +
                       "Content-Disposition: attachment;\r\n" +
                       "    filename=\"test.vbs\"\r\n" +
                       "\r\n" +
                       "s = \"abc\"\r\n" +
                       "\r\n" +
                       "msgbox mid(s,1,100000)\r\n" +
                       "------=_NextPart_000_000D_01C97C94.33D5E670--\r\n" +
                       "\r\n" +
                       "";


         // Add an account
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "mimetest@example.test", "test");

         var rule = account.Rules.Add();
         rule.Name = "Criteria test";
         rule.Active = true;

         var ruleCriteria = rule.Criterias.Add();
         ruleCriteria.UsePredefined = false;
         ruleCriteria.HeaderField = "Subject";
         ruleCriteria.MatchType = eRuleMatchType.eMTContains;
         ruleCriteria.MatchValue = "rtest";
         ruleCriteria.Save();

         // Add action
         var ruleAction = rule.Actions.Add();
         ruleAction.Type = eRuleActionType.eRASetHeaderValue;
         ruleAction.HeaderName = "SomeHeader";
         ruleAction.Value = "SomeValue";
         ruleAction.Save();

         // Save the rule in the database
         rule.Save();

         var smtpClientSimulator = new SmtpClientSimulator();

         // Spam folder
         smtpClientSimulator.SendRaw("mimetest@example.test", "mimetest@example.test", content);

         var sContents = Pop3ClientSimulator.AssertGetFirstMessageText("mimetest@example.test", "test");

         Assert.IsTrue(sContents.IndexOf("SomeHeader: SomeValue") > 0);
         Assert.IsTrue(sContents.IndexOf("------=_NextPart_000_000D_01C97C94.33D5E670.ALT--") > 0);
      }

      private void AssertIsConnectionTerminatedException(IOException exception)
      {
         var inner = exception.InnerException as SocketException;
         Assert.IsNotNull(inner);


         Assert.AreEqual(10054, inner.ErrorCode);
      }
   }
}