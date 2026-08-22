// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.IMAP
{
   /// <summary>
   ///    PREVIEW (RFC 8970). A client rendering its message list used to fetch
   ///    body parts for every message just to show the first line under the
   ///    subject; the PREVIEW FETCH item hands it a server-generated snippet
   ///    instead - the decoded plain-text part, whitespace collapsed to single
   ///    spaces, capped at the RFC's 256 octets, emitted as a UTF-8 literal. An
   ///    HTML-only message gets its tags stripped: a preview is a glance, not a
   ///    rendering.
   /// </summary>
   [TestFixture]
   public class Preview : TestFixtureBase
   {
      private Account _account;

      [SetUp]
      public new void SetUp()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "preview@example.test", "test");
      }

      private string FetchFirstMessage(string items)
      {
         var imapSim = new ImapClientSimulator();
         imapSim.Connect();
         imapSim.Logon(_account.Address, "test");
         imapSim.SelectFolder("INBOX");

         imapSim.SendRaw("A02 FETCH 1 (" + items + ")\r\n");
         var response = imapSim.ReceiveUntil("A02 ");
         imapSim.Disconnect();

         return response;
      }

      [Test]
      [Description("The snippet is the plain-text body with its line structure collapsed to spaces.")]
      public void TheSnippetIsTheCollapsedPlainTextBody()
      {
         SmtpClientSimulator.StaticSend("sender@example.test", _account.Address, "preview test",
            "First line.\r\nSecond   line.\r\n\r\nThird line.\r\n");
         Pop3ClientSimulator.AssertMessageCount(_account.Address, "test", 1);

         var response = FetchFirstMessage("PREVIEW");

         StringAssert.Contains("PREVIEW {", response,
            "The preview must be emitted as a literal - it is UTF-8 by definition. Got: " + response);
         StringAssert.Contains("First line. Second line. Third line.", response,
            "The snippet must be the body with every whitespace run collapsed to one space. Got: " + response);
      }

      [Test]
      [Description("The LAZY modifier is accepted and answered like the plain form.")]
      public void TheLazyModifierIsAccepted()
      {
         SmtpClientSimulator.StaticSend("sender@example.test", _account.Address, "lazy preview",
            "Lazy body text.\r\n");
         Pop3ClientSimulator.AssertMessageCount(_account.Address, "test", 1);

         var response = FetchFirstMessage("PREVIEW (LAZY)");

         StringAssert.Contains("A02 OK", response,
            "PREVIEW (LAZY) is valid RFC 8970 syntax and must not be refused. Got: " + response);
         StringAssert.Contains("Lazy body text.", response,
            "LAZY permits answering NIL to avoid expensive generation; it does not require it, " +
            "and this server generates eagerly. Got: " + response);
      }

      [Test]
      [Description("An HTML-only message is stripped to its text - a glance, not a rendering.")]
      public void AnHtmlOnlyMessageIsStrippedToItsText()
      {
         var message =
            "From: sender@example.test\r\n" +
            "To: preview@example.test\r\n" +
            "Subject: html preview\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: text/html; charset=us-ascii\r\n" +
            "\r\n" +
            "<html><body><p>Hello <b>bold</b> world.</p></body></html>\r\n";

         SmtpClientSimulator.StaticSendRaw("sender@example.test", _account.Address, message);
         Pop3ClientSimulator.AssertMessageCount(_account.Address, "test", 1);

         var response = FetchFirstMessage("PREVIEW");

         StringAssert.Contains("Hello bold world.", response,
            "The HTML message's tags must be stripped from the snippet. Got: " + response);
         ClassicAssert.IsFalse(response.Contains("<b>"),
            "No markup may survive into the preview. Got: " + response);
      }

      [Test]
      [Description("A long body is truncated within the RFC's 256-octet ceiling.")]
      public void ALongBodyIsTruncatedWithinTheCeiling()
      {
         SmtpClientSimulator.StaticSend("sender@example.test", _account.Address, "long preview",
            new string('a', 5000));
         Pop3ClientSimulator.AssertMessageCount(_account.Address, "test", 1);

         var response = FetchFirstMessage("PREVIEW");

         var match = System.Text.RegularExpressions.Regex.Match(response, @"PREVIEW \{(\d+)\}");
         ClassicAssert.IsTrue(match.Success, "No PREVIEW literal in the response. Got: " + response);

         int octets = int.Parse(match.Groups[1].Value);
         ClassicAssert.IsTrue(octets > 0 && octets <= 256,
            "RFC 8970 caps the preview at 256 octets; got " + octets + ".");
      }

      [Test]
      [Description("CAPABILITY advertises PREVIEW.")]
      public void CapabilityAdvertisesPreview()
      {
         var socket = new TcpConnection();
         ClassicAssert.IsTrue(socket.Connect(143), "Could not connect to the IMAP server on port 143.");
         socket.ReadUntil("* OK");

         socket.Send("A01 CAPABILITY\r\n");
         var capabilities = socket.ReadUntil("A01 OK");

         StringAssert.Contains("PREVIEW", capabilities,
            "CAPABILITY must advertise PREVIEW. Got: " + capabilities);

         socket.Disconnect();
      }
   }
}
