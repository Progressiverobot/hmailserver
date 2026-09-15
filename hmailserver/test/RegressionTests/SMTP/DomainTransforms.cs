// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.SMTP
{
   /// <summary>
   ///    What a domain does to a message: the external-sender tag, the
   ///    first-contact note and the disclaimer.
   ///
   ///    Three transformations, all per domain and all off by default, and the
   ///    default is the first thing asserted here - a feature that is inert until
   ///    somebody opts in is only worth having if "inert" is tested rather than
   ///    assumed.
   ///
   ///    The rule for "outside" lives in one function, MessageOrigin::IsExternalSender,
   ///    and is unit-tested in C++ against a hosted-address set the test names
   ///    (MessageOriginTester, reached from Infrastructure.MainOperations.TestInternals).
   ///    What is tested HERE is the wiring: that the rule is consulted at delivery,
   ///    that its answer reaches the message file, and that each of the three things
   ///    that make a sender ours - a signed-in session, a trusted incoming relay, a
   ///    hosted address - actually suppresses the tag on a real delivery.
   ///
   ///    The disclaimer is asserted on the message as it LEAVES, captured by a
   ///    listener a route of this test's own points at, because what the outside
   ///    world receives is the whole point of it.
   /// </summary>
   [TestFixture]
   public class DomainTransforms : TestFixtureBase
   {
      private const string ExternalHeader = "X-hMailServer-External";
      private const string FirstContactHeader = "X-hMailServer-First-Contact";

      // The two forms are deliberately different sentences. A footer whose HTML
      // form contains its plain form cannot be counted - one appended copy of the
      // plain text would read as two - and "appended exactly once" is the claim
      // this feature stands or falls on.
      private const string DisclaimerText = "Confidential: this note may be privileged.";
      private const string DisclaimerHtml = "<p>Confidential in HTML: may be privileged.</p>";
      private const string DisclaimerHtmlWords = "Confidential in HTML: may be privileged.";

      private static int Occurrences(string haystack, string needle)
      {
         int count = 0;

         for (int at = haystack.IndexOf(needle, StringComparison.Ordinal); at >= 0;
              at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;

         return count;
      }

      private void EnableTagging(bool subject, bool header, string text = "")
      {
         _domain.ExternalTagSubject = subject;
         _domain.ExternalTagHeader = header;
         _domain.ExternalTagText = text;
         _domain.Save();
      }

      /// <summary>
      ///    Sends one message outwards to a domain a route points at a listener of
      ///    this test's own, and answers the message as that listener received it.
      /// </summary>
      private string SendOutwards(Account sender, string domainName, string recipient, string message)
      {
         int capturePort = TestSetup.GetNextFreePort();

         using (var capture = new SmtpServerSimulator(1, capturePort))
         {
            capture.AddRecipientResult(new Dictionary<string, int> {{recipient, 250}});
            capture.StartListen();

            var route = _settings.Routes.Add();

            try
            {
               route.DomainName = domainName;
               route.TargetSMTPHost = "localhost";
               route.TargetSMTPPort = capturePort;
               route.NumberOfTries = 1;
               route.MinutesBetweenTry = 5;
               route.Save();

               // Authenticated, because the default ranges require it for
               // local-to-remote - and because a message relayed in from outside
               // that merely claimed one of our domains must not get the footer,
               // which is a rule this path would otherwise never exercise.
               new SmtpClientSimulator().SendRaw(sender.Address, "test", sender.Address, recipient, message);
               _application.SubmitEMail();

               capture.WaitForCompletion();

               return capture.MessageData;
            }
            finally
            {
               // Taken away again, because a test that sends twice would
               // otherwise leave two routes for one domain and the second send
               // would be aimed at the first listener, which has gone.
               if (route.ID > 0)
                  _settings.Routes.DeleteByDBID(route.ID);
            }
         }
      }

      [Test]
      [Description("A disclaimer longer than 4,000 characters is saved and read back whole. SQL Server Compact refused " +
                   "any string parameter over 4,000 characters, even into ntext, so the whole domain save failed.")]
      public void ADisclaimerLongerThanFourThousandCharactersIsSaved()
      {
         string plain = new string('p', 9000);
         string html = "<p>" + new string('h', 9000) + "</p>";

         _domain.DisclaimerPlainText = plain;
         _domain.DisclaimerHTML = html;
         _domain.Save();

         var reread = SingletonProvider<TestSetup>.Instance.GetApp().Domains.get_ItemByName(_domain.Name);
         ClassicAssert.AreEqual(plain.Length, reread.DisclaimerPlainText.Length);
         ClassicAssert.AreEqual(plain, reread.DisclaimerPlainText);
         ClassicAssert.AreEqual(html, reread.DisclaimerHTML);
      }

      [Test]
      [Description("Every switch is off on a new domain, and a message from outside is delivered exactly as it arrived.")]
      public void EverySwitchIsOffByDefault()
      {
         ClassicAssert.IsFalse(_domain.ExternalTagSubject, "External subject tagging must be off by default.");
         ClassicAssert.IsFalse(_domain.ExternalTagHeader, "The external header must be off by default.");
         ClassicAssert.AreEqual("", _domain.ExternalTagText, "The tag text starts empty, which means the shipped [EXTERNAL].");
         ClassicAssert.IsFalse(_domain.FirstContactTip, "The first-contact note must be off by default.");
         ClassicAssert.IsFalse(_domain.DisclaimerEnabled, "The disclaimer must be off by default.");
         ClassicAssert.AreEqual("", _domain.DisclaimerPlainText);
         ClassicAssert.AreEqual("", _domain.DisclaimerHTML);

         var recipient = SingletonProvider<TestSetup>.Instance.AddAccount(_domain,
            TestSetup.UniqueString() + "@example.test", "test");

         SmtpClientSimulator.StaticSend("stranger@outside-transforms.test", recipient.Address, "Invoice", "Body");

         string message = Pop3ClientSimulator.AssertGetFirstMessageText(recipient.Address, "test");

         StringAssert.DoesNotContain(ExternalHeader, message, "Nothing is stamped while the switches are off.");
         StringAssert.DoesNotContain(FirstContactHeader, message, "Nothing is stamped while the switches are off.");
         StringAssert.Contains("Subject: Invoice", message, "The subject must be untouched while the switches are off.");
      }

      [Test]
      [Description("A sender outside the hosted domains is tagged in the subject and in the header; a sender at a hosted domain is not.")]
      public void AnOutsideSenderIsTaggedAndAnInsideOneIsNot()
      {
         var recipient = SingletonProvider<TestSetup>.Instance.AddAccount(_domain,
            TestSetup.UniqueString() + "@example.test", "test");
         var colleague = SingletonProvider<TestSetup>.Instance.AddAccount(_domain,
            TestSetup.UniqueString() + "@example.test", "test");

         EnableTagging(subject: true, header: true);

         SmtpClientSimulator.StaticSend("stranger@outside-transforms.test", recipient.Address, "Invoice", "Body");

         string outside = Pop3ClientSimulator.AssertGetFirstMessageText(recipient.Address, "test");
         StringAssert.Contains("Subject: [EXTERNAL] Invoice", outside,
            "A sender at no hosted domain must have the shipped tag put in front of the subject.");
         StringAssert.Contains(ExternalHeader, outside,
            "A sender at no hosted domain must be stamped for the reader's banner.");

         // The negative control, and the reason the assertion above proves
         // anything: the same delivery from an address this server hosts is
         // untouched.
         SmtpClientSimulator.StaticSend(colleague.Address, recipient.Address, "Lunch", "Body");

         string inside = Pop3ClientSimulator.AssertGetFirstMessageText(recipient.Address, "test");
         StringAssert.Contains("Subject: Lunch", inside, "A colleague's message must not be tagged.");
         StringAssert.DoesNotContain(ExternalHeader, inside, "A colleague's message must not be stamped.");

         // A domain that says it in its own words says it in its own words, and
         // the header is a separate switch.
         EnableTagging(subject: true, header: false, text: "[EXTERN]");

         SmtpClientSimulator.StaticSend("stranger@outside-transforms.test", recipient.Address, "Rechnung", "Body");

         string translated = Pop3ClientSimulator.AssertGetFirstMessageText(recipient.Address, "test");
         StringAssert.Contains("Subject: [EXTERN] Rechnung", translated, "The domain's own tag text must be used.");
         StringAssert.DoesNotContain(ExternalHeader, translated,
            "The header is a separate switch and was turned off; it must not be stamped.");
      }

      [Test]
      [Description("A message submitted by a session that signed in is never tagged, whatever address it claims to be from.")]
      public void AnAuthenticatedSenderIsNeverTagged()
      {
         var recipient = SingletonProvider<TestSetup>.Instance.AddAccount(_domain,
            TestSetup.UniqueString() + "@example.test", "test");
         var sender = SingletonProvider<TestSetup>.Instance.AddAccount(_domain,
            TestSetup.UniqueString() + "@example.test", "test");

         EnableTagging(subject: true, header: true);

         // The same envelope sender the test above had tagged, from a session
         // that signed in. Authentication is the only thing that differs.
         var client = new SmtpClientSimulator();
         client.Send(false, sender.Address, "test", "stranger@outside-transforms.test", recipient.Address,
            "Invoice", "Body", out string error);
         ClassicAssert.AreEqual("", error, "The authenticated submission was refused: " + error);

         string message = Pop3ClientSimulator.AssertGetFirstMessageText(recipient.Address, "test");
         StringAssert.Contains("Subject: Invoice", message,
            "An authenticated submission is this installation's own traffic and must not be tagged.");
         StringAssert.DoesNotContain(ExternalHeader, message,
            "An authenticated submission must not be stamped as external.");
      }

      [Test]
      [Description("A message handed to this server by a trusted incoming relay is not tagged, because the relay list is a statement that the hop is ours.")]
      public void AMessageFromATrustedIncomingRelayIsNotTagged()
      {
         var recipient = SingletonProvider<TestSetup>.Instance.AddAccount(_domain,
            TestSetup.UniqueString() + "@example.test", "test");

         EnableTagging(subject: true, header: true);

         // The control first: with no relay declared, this exact delivery is
         // tagged. Without it the assertion below could pass against a server
         // that had simply stopped tagging altogether.
         SmtpClientSimulator.StaticSend("stranger@outside-transforms.test", recipient.Address, "Before", "Body");
         StringAssert.Contains("Subject: [EXTERNAL] Before",
            Pop3ClientSimulator.AssertGetFirstMessageText(recipient.Address, "test"),
            "With no incoming relay declared, a sender from outside is tagged.");

         var relay = _settings.IncomingRelays.Add();

         try
         {
            relay.Name = "transforms-relay";
            relay.LowerIP = "127.0.0.1";
            relay.UpperIP = "127.0.0.1";
            relay.Save();

            SmtpClientSimulator.StaticSend("stranger@outside-transforms.test", recipient.Address, "After", "Body");

            string message = Pop3ClientSimulator.AssertGetFirstMessageText(recipient.Address, "test");
            StringAssert.Contains("Subject: After", message,
               "A message handed over by a declared incoming relay must not be tagged.");
            StringAssert.DoesNotContain(ExternalHeader, message,
               "A message handed over by a declared incoming relay must not be stamped.");
         }
         finally
         {
            _settings.IncomingRelays.DeleteByDBID(relay.ID);
         }
      }

      [Test]
      [Description("The first-contact note appears the first time a given outside sender writes and not on the message after it, and never on the first message an empty mailbox receives.")]
      public void TheFirstContactNoteAppearsOnceAndNotTwice()
      {
         var recipient = SingletonProvider<TestSetup>.Instance.AddAccount(_domain,
            TestSetup.UniqueString() + "@example.test", "test");

         _domain.FirstContactTip = true;
         _domain.Save();

         // A mailbox with no memory at all: everybody is new, so nobody is
         // unusual, and the note would be noise on the first message an account
         // ever receives. The sender is remembered rather than announced.
         SmtpClientSimulator.StaticSend("first@outside-transforms.test", recipient.Address, "One", "Body");
         StringAssert.DoesNotContain(FirstContactHeader,
            Pop3ClientSimulator.AssertGetFirstMessageText(recipient.Address, "test"),
            "The very first message an account receives has nothing to be unusual against.");

         // Now the account remembers somebody, so a different sender is a first
         // contact and is announced.
         SmtpClientSimulator.StaticSend("second@outside-transforms.test", recipient.Address, "Two", "Body");
         StringAssert.Contains(FirstContactHeader,
            Pop3ClientSimulator.AssertGetFirstMessageText(recipient.Address, "test"),
            "A sender this account has never had mail from must be announced.");

         // And the same sender again is not.
         SmtpClientSimulator.StaticSend("second@outside-transforms.test", recipient.Address, "Three", "Body");
         StringAssert.DoesNotContain(FirstContactHeader,
            Pop3ClientSimulator.AssertGetFirstMessageText(recipient.Address, "test"),
            "The note must appear once per sender, not on every message they send.");
      }

      [Test]
      [Description("The domain disclaimer is appended once to the plain-text part and once to the HTML part of a message leaving the organisation.")]
      public void TheDisclaimerIsAppendedOnceToTextAndHtml()
      {
         var sender = SingletonProvider<TestSetup>.Instance.AddAccount(_domain,
            TestSetup.UniqueString() + "@example.test", "test");

         _domain.DisclaimerEnabled = true;
         _domain.DisclaimerPlainText = DisclaimerText;
         _domain.DisclaimerHTML = DisclaimerHtml;
         _domain.Save();

         const string partner = "partner@outside-disclaimer.test";

         string sent = SendOutwards(sender, "outside-disclaimer.test", partner,
            "From: " + sender.Address + "\r\n" +
            "To: " + partner + "\r\n" +
            "Subject: Quotation\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/alternative; boundary=\"boundary-transforms\"\r\n" +
            "\r\n" +
            "--boundary-transforms\r\n" +
            "Content-Type: text/plain; charset=\"iso-8859-1\"\r\n" +
            "\r\n" +
            "Here is the quotation.\r\n" +
            "--boundary-transforms\r\n" +
            "Content-Type: text/html; charset=\"iso-8859-1\"\r\n" +
            "\r\n" +
            "<html><body><p>Here is the quotation.</p></body></html>\r\n" +
            "--boundary-transforms--\r\n");

         ClassicAssert.AreEqual(1, Occurrences(sent, DisclaimerText),
            "The plain-text disclaimer must be appended to the plain part exactly once: " + sent);
         ClassicAssert.AreEqual(1, Occurrences(sent, DisclaimerHtmlWords),
            "The HTML disclaimer must be appended to the HTML part exactly once: " + sent);
         StringAssert.Contains("X-hMailServer-Disclaimer", sent,
            "The message must be stamped, so that this server never foots the same message twice.");
      }

      [Test]
      [Description("A reply whose body already quotes the disclaimer does not collect a second copy, which is what stops a thread accumulating one per hop.")]
      public void TheDisclaimerIsNotAddedTwiceOnAReplyChain()
      {
         var sender = SingletonProvider<TestSetup>.Instance.AddAccount(_domain,
            TestSetup.UniqueString() + "@example.test", "test");

         _domain.DisclaimerEnabled = true;
         _domain.DisclaimerPlainText = DisclaimerText;
         _domain.Save();

         const string partner = "partner@outside-reply.test";

         // The control: a message that does not quote it gets one.
         string first = SendOutwards(sender, "outside-reply.test", partner,
            "From: " + sender.Address + "\r\n" +
            "To: " + partner + "\r\n" +
            "Subject: Quotation\r\n" +
            "\r\n" +
            "Here is the quotation.\r\n");

         ClassicAssert.AreEqual(1, Occurrences(first, DisclaimerText),
            "A message that does not already carry the disclaimer must get one: " + first);

         // The reply, quoted the way a mail client quotes it.
         string reply = SendOutwards(sender, "outside-reply.test", partner,
            "From: " + sender.Address + "\r\n" +
            "To: " + partner + "\r\n" +
            "Subject: Re: Quotation\r\n" +
            "\r\n" +
            "Thank you.\r\n" +
            "\r\n" +
            "> Here is the quotation.\r\n" +
            ">\r\n" +
            "> " + DisclaimerText + "\r\n");

         ClassicAssert.AreEqual(1, Occurrences(reply, DisclaimerText),
            "A reply that already quotes the disclaimer must not collect a second copy: " + reply);
      }

      [Test]
      [Description("A signed message is sent unchanged: appending to it would break the signature, so the disclaimer is left off and the message goes out as the sender wrote it.")]
      public void TheDisclaimerIsRefusedOnASignedMessage()
      {
         var sender = SingletonProvider<TestSetup>.Instance.AddAccount(_domain,
            TestSetup.UniqueString() + "@example.test", "test");

         _domain.DisclaimerEnabled = true;
         _domain.DisclaimerPlainText = DisclaimerText;
         _domain.Save();

         const string partner = "partner@outside-signed.test";
         const string signedBody = "Here is the signed quotation.";

         string sent = SendOutwards(sender, "outside-signed.test", partner,
            "From: " + sender.Address + "\r\n" +
            "To: " + partner + "\r\n" +
            "Subject: Signed quotation\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/signed; protocol=\"application/pkcs7-signature\"; " +
            "micalg=sha-256; boundary=\"boundary-signed\"\r\n" +
            "\r\n" +
            "--boundary-signed\r\n" +
            "Content-Type: text/plain; charset=\"iso-8859-1\"\r\n" +
            "\r\n" +
            signedBody + "\r\n" +
            "--boundary-signed\r\n" +
            "Content-Type: application/pkcs7-signature; name=\"smime.p7s\"\r\n" +
            "Content-Transfer-Encoding: base64\r\n" +
            "\r\n" +
            "bm90LWEtcmVhbC1zaWduYXR1cmU=\r\n" +
            "--boundary-signed--\r\n");

         StringAssert.Contains(signedBody, sent, "The signed message must still be delivered.");
         StringAssert.DoesNotContain(DisclaimerText, sent,
            "Nothing may be appended to a signed message: the signature covers the part that would change.");
         StringAssert.DoesNotContain("X-hMailServer-Disclaimer", sent,
            "A message the disclaimer was not added to must not be stamped as though it had been.");
      }
   }
}
