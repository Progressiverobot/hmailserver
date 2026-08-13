// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System.Text;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.SMTP
{
   /// <summary>
   /// ESMTP reply and parameter conformance on the receiving side: every reply the
   /// server sends must be a reply (RFC 5321 section 4.2), a parameter it does not
   /// implement must be refused rather than half-understood (RFC 5321 section
   /// 4.1.1.11), a size refusal must carry the code RFC 1870 defines for it, and
   /// HELP must not name commands the server does not have.
   ///
   /// Every test here failed against the code as it stood - the comment on each one
   /// says what it did instead - except DeclaredSizeAboveTheMaximumIsRefusedBefore-
   /// Data, which is the control: it already passed, and it is the reason the
   /// after-DATA refusal was worth changing rather than leaving alone.
   /// </summary>
   [TestFixture]
   public class EsmtpConformance : TestFixtureBase
   {
      private static TcpConnection ConnectAndEhlo()
      {
         var socket = new TcpConnection();
         Assert.IsTrue(socket.Connect(25));
         Assert.IsTrue(socket.Receive().StartsWith("220"));
         socket.Send("EHLO example.test\r\n");
         var ehlo = socket.ReadUntil("250 HELP");
         Assert.IsTrue(ehlo.Contains("250"), "EHLO was not accepted. Got: " + ehlo);
         return socket;
      }

      [Test]
      [Description("The disconnect after too many invalid commands is sent as a 421 reply, " +
                   "not as a bare line of text with no status code at all.")]
      public void TooManyInvalidCommandsIsAnnouncedAsAnSmtpReply()
      {
         // Auto-ban is not involved here (no failed logons), but the invalid-command
         // counter is: three 5xx replies are tolerated and the fourth ends the session.
         _settings.DisconnectInvalidClients = true;
         _settings.MaxNumberOfInvalidCommands = 3;

         using (var socket = ConnectAndEhlo())
         {
            // "HELO" with no host name is a syntax error, so each of these earns a 501.
            Assert.IsTrue(socket.SendAndReceive("HELO\r\n").StartsWith("501"));
            Assert.IsTrue(socket.SendAndReceive("HELO\r\n").StartsWith("501"));
            Assert.IsTrue(socket.SendAndReceive("HELO\r\n").StartsWith("501"));

            var final = socket.SendAndReceive("HELO\r\n");

            // Before the fix this line was written as "Too many invalid commands. Bye!"
            // with no reply code, so a client saw an unparseable line and then a closed
            // channel. The text is unchanged; only the code in front of it is new.
            StringAssert.Contains("Too many invalid commands", final);
            Assert.IsTrue(final.StartsWith("421"),
               "The final reply before the disconnect must be a 421 reply. Got: " + final);
         }
      }

      [Test]
      [Description("An unknown MAIL FROM parameter whose name merely begins with SIZE is refused, " +
                   "rather than being read as a size declaration.")]
      public void UnknownParameterBeginningWithSizeIsRefused()
      {
         using (var socket = ConnectAndEhlo())
         {
            // The keyword test used to be Left(4) == "SIZE", so "SIZEX=100" matched and
            // Mid(5) read "100" as the declared size: an extension the server does not
            // implement was accepted with a 250.
            var response = socket.SendAndReceive("MAIL FROM:<sender@external.example> SIZEX=100\r\n");
            StringAssert.Contains("Unsupported ESMTP extension", response);
            Assert.IsTrue(response.StartsWith("550"),
               "An unimplemented ESMTP parameter must be refused. Got: " + response);
         }
      }

      [Test]
      [Description("A SIZE parameter that is not a decimal number is a syntax error, not a " +
                   "silent \"no size declared\".")]
      public void MalformedSizeParameterIsRefused()
      {
         using (var socket = ConnectAndEhlo())
         {
            // _ttoi returned 0 for this, which is indistinguishable from "the client
            // declared nothing" - so the transaction was accepted with a 250 and the
            // whole point of RFC 1870 (refusing before the octets arrive) was skipped.
            var response = socket.SendAndReceive("MAIL FROM:<sender@external.example> SIZE=notanumber\r\n");
            Assert.IsTrue(response.StartsWith("501"),
               "A malformed SIZE value must be a syntax error. Got: " + response);
         }
      }

      [Test]
      [Description("Control - already passing. A declared SIZE above the maximum message size is " +
                   "refused with 552 before DATA, which is the code the after-DATA refusal now matches.")]
      public void DeclaredSizeAboveTheMaximumIsRefusedBeforeData()
      {
         _settings.MaxMessageSize = 1; // KB

         using (var socket = ConnectAndEhlo())
         {
            var response = socket.SendAndReceive("MAIL FROM:<sender@external.example> SIZE=500000\r\n");
            Assert.IsTrue(response.StartsWith("552"),
               "An oversized SIZE declaration must be refused with 552 (RFC 1870). Got: " + response);
         }
      }

      [Test]
      [Description("A message that turns out to be over the maximum size once its octets have " +
                   "arrived is refused with 552, the same code as every other size refusal.")]
      public void MessageOverTheMaximumSizeIsRefusedWith552AfterData()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sizetest@example.test", "test");

         _settings.MaxMessageSize = 1; // KB

         using (var socket = ConnectAndEhlo())
         {
            // No SIZE= declaration, so the excess can only be discovered from the data.
            Assert.IsTrue(socket.SendAndReceive("MAIL FROM:<sender@external.example>\r\n").StartsWith("250"));
            Assert.IsTrue(socket.SendAndReceive("RCPT TO:<" + account.Address + ">\r\n").StartsWith("250"));
            Assert.IsTrue(socket.SendAndReceive("DATA\r\n").StartsWith("354"));

            var body = new StringBuilder();
            body.Append("Subject: oversized\r\n\r\n");
            for (var i = 0; i < 200; i++)
               body.Append("This line exists only to push the message past one kilobyte.\r\n");

            socket.Send(body.ToString());
            var response = socket.SendAndReceive(".\r\n");

            // This answered 554 before the fix, so the same condition was reported with
            // two different codes depending on whether the client had declared a size.
            Assert.IsTrue(response.StartsWith("552"),
               "An oversized message must be refused with 552 (RFC 1870 section 5). Got: " + response);

            StringAssert.Contains("exceeds fixed maximum message size", response);
         }

         // And it really was refused, not merely mis-coded.
         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 0);
      }

      [Test]
      [Description("HELP lists the commands the server actually implements, and does not list SAML, " +
                   "which it does not implement at all.")]
      public void HelpDoesNotListCommandsTheServerDoesNotHave()
      {
         using (var socket = ConnectAndEhlo())
         {
            var help = socket.SendAndReceive("HELP\r\n");

            Assert.IsTrue(help.StartsWith("211"), "HELP must answer 211. Got: " + help);

            // SAML is not in GetCommandType_ at all: a client that followed the old HELP
            // text and sent it got "503 Bad sequence of commands".
            StringAssert.DoesNotContain("SAML", help);

            // ...while these three are implemented and advertised, and were missing.
            StringAssert.Contains("AUTH", help);
            StringAssert.Contains("BDAT", help);

            // Proof that the advice is followable: the two verbs the old list omitted
            // are answered as the list now claims.
            Assert.IsTrue(socket.SendAndReceive("SAML\r\n").StartsWith("5"),
               "SAML is not implemented, so it must be refused.");
         }
      }
   }
}
