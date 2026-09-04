// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Reflection;
using System.Linq;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Sieve
{
   /// <summary>
   ///    Covers the Sieve "vacation" action (RFC 5230), and above all its
   ///    loop-prevention rules.
   ///
   ///    This fixture is mostly negative assertions on purpose. An auto-responder
   ///    that answers the wrong message does not merely annoy someone: two of them
   ///    facing each other, or one facing a mailing list or a bounce, generate mail
   ///    until an administrator intervenes. The feature is therefore only as good
   ///    as the list of things it refuses to answer, and every entry on that list
   ///    is cheap to break by accident later - a header name typo would do it -
   ///    which is exactly the kind of thing regression tests are for.
   ///
   ///    A "vacation" token in the action summary means "the checks passed, an
   ///    auto-reply is due". Its absence means no reply. The token is appended to
   ///    the delivery decision rather than replacing it, because whether the user
   ///    is on holiday has nothing to do with where the message itself goes.
   ///
   ///    Invoked late-bound via IDispatch so the test does not depend on the
   ///    registered type library being regenerated.
   /// </summary>
   [TestFixture]
   public class SieveVacation : TestFixtureBase
   {
      private string Evaluate(string script, string rawMessage)
      {
         object utilities = _application.Utilities;
         object result = utilities.GetType().InvokeMember(
            "EvaluateSieveScript",
            BindingFlags.InvokeMethod,
            null,
            utilities,
            new object[] { script, rawMessage });

         return (string)result;
      }

      // ":addresses" tells the script which addresses belong to the user. It is
      // also what lets the RFC 5230 4.5 check run here: the raw message on its own
      // does not say which recipient this delivery is for, because Delivered-To is
      // off in the shipped configuration.
      private const string Script =
         "require \"vacation\";\r\n" +
         "vacation :days 7 :subject \"Away\" :addresses \"user@example.test\" \"I am on holiday.\";";

      // A perfectly ordinary personal message addressed to the user.
      private static string Message(params string[] extraHeaders)
      {
         string headers =
            "Return-Path: <friend@example.test>\r\n" +
            "From: \"A Friend\" <friend@example.test>\r\n" +
            "To: user@example.test\r\n" +
            "Subject: Lunch?\r\n" +
            string.Concat(extraHeaders.Select(extra => extra + "\r\n"));

         return headers + "\r\nAre you free on Thursday?";
      }

      // As above but with a replaced Return-Path, for the null-return-path case.
      private static string MessageWithReturnPath(string returnPath)
      {
         return
            "Return-Path: " + returnPath + "\r\n" +
            "From: MAILER-DAEMON@example.test\r\n" +
            "To: user@example.test\r\n" +
            "Subject: Undelivered Mail Returned to Sender\r\n" +
            "\r\n" +
            "Delivery failed.";
      }

      [Test]
      [Description("A personal message gets an auto-reply, and the reply does not disturb the delivery decision.")]
      public void TestVacationRepliesToPersonalMail()
      {
         // The whole point of the mapping: vacation is a side effect, so the
         // implicit keep survives it.
         Assert.AreEqual("keep;vacation", Evaluate(Script, Message()));

         // ...including when the script also decides where the message goes.
         Assert.AreEqual("fileinto:Friends;vacation",
            Evaluate("require [\"vacation\", \"fileinto\"];\r\n" +
                     "fileinto \"Friends\";\r\n" +
                     "vacation :addresses \"user@example.test\" \"I am on holiday.\";",
               Message()));

         // ...and when it decides the message is not wanted at all. A discarded
         // message still gets an auto-reply if the script asked for one; what it
         // must not do is come back to life.
         Assert.AreEqual("discard;vacation",
            Evaluate("require \"vacation\";\r\n" +
                     "discard;\r\n" +
                     "vacation :addresses \"user@example.test\" \"I am on holiday.\";",
               Message()));

         // Without :addresses the RFC 5230 4.5 recipient check has nothing to look
         // for and is skipped rather than guessed at, so the reply still goes. This
         // is a deliberate gap, documented in SieveEvaluator::SuppressVacation_:
         // the remaining suppression rules and the per-sender rate limit are what
         // stand between it and a loop.
         Assert.AreEqual("keep;vacation",
            Evaluate("require \"vacation\";\r\nvacation \"I am on holiday.\";", Message()));
      }

      [Test]
      [Description("No auto-reply to a null return path - the shape every bounce and auto-responder uses.")]
      public void TestNoReplyToNullReturnPath()
      {
         // Answering <> is the single most direct way to build a loop: the reply
         // bounces, the bounce is answered, and so on.
         Assert.AreEqual("keep", Evaluate(Script, MessageWithReturnPath("<>")));

         // A real sender is still answered, so the check above is not just failing
         // to find anything.
         Assert.AreEqual("keep;vacation", Evaluate(Script, Message()));
      }

      [Test]
      [Description("No auto-reply to automatic mail: Auto-Submitted, and the Microsoft suppression header.")]
      public void TestNoReplyToAutomaticMail()
      {
         // RFC 3834: an automatic response must not answer an automatic message.
         Assert.AreEqual("keep", Evaluate(Script, Message("Auto-Submitted: auto-replied")));
         Assert.AreEqual("keep", Evaluate(Script, Message("Auto-Submitted: auto-generated")));
         Assert.AreEqual("keep", Evaluate(Script, Message("Auto-Submitted: auto-notified; owner-email=x@y.test")));

         // "no" is the one value that means "this was written by a person".
         Assert.AreEqual("keep;vacation", Evaluate(Script, Message("Auto-Submitted: no")));

         Assert.AreEqual("keep", Evaluate(Script, Message("X-Auto-Response-Suppress: OOF")));
         Assert.AreEqual("keep", Evaluate(Script, Message("X-Auto-Response-Suppress: All")));
         Assert.AreEqual("keep", Evaluate(Script, Message("X-Auto-Response-Suppress: DR, AutoReply")));
      }

      [Test]
      [Description("No auto-reply to bulk, list or list-managed mail.")]
      public void TestNoReplyToBulkOrListMail()
      {
         Assert.AreEqual("keep", Evaluate(Script, Message("Precedence: bulk")));
         Assert.AreEqual("keep", Evaluate(Script, Message("Precedence: list")));
         Assert.AreEqual("keep", Evaluate(Script, Message("Precedence: junk")));

         // Any one of the list headers identifies list traffic. Replying to a list
         // sends the holiday notice to every subscriber, which is how this feature
         // has embarrassed people since the 1990s.
         Assert.AreEqual("keep", Evaluate(Script, Message("List-Id: <users.example.test>")));
         Assert.AreEqual("keep", Evaluate(Script, Message("List-Unsubscribe: <mailto:leave@example.test>")));
         Assert.AreEqual("keep", Evaluate(Script, Message("List-Post: <mailto:users@example.test>")));
         Assert.AreEqual("keep", Evaluate(Script, Message("List-Help: <mailto:help@example.test>")));

         // "Precedence: first-class" is not bulk, and must not be swept up.
         Assert.AreEqual("keep;vacation", Evaluate(Script, Message("Precedence: first-class")));
      }

      [Test]
      [Description("No auto-reply to a message the server classified as spam.")]
      public void TestNoReplyToSpam()
      {
         // The same judgement the account-level auto-reply already makes with its
         // "abort when flagged as spam" option: an auto-reply confirms the address
         // is live, and the sender of spam is usually forged, so the reply lands on
         // an innocent third party.
         Assert.AreEqual("keep", Evaluate(Script, Message("X-hMailServer-Spam: YES")));

         Assert.AreEqual("keep;vacation", Evaluate(Script, Message()));
      }

      [Test]
      [Description("No auto-reply unless one of the user's own addresses is actually a recipient (RFC 5230 4.5).")]
      public void TestNoReplyWhenUserIsNotAddressed()
      {
         // Bcc'd list traffic and backscatter arrive addressed to somebody else.
         const string addressedElsewhere =
            "Return-Path: <friend@example.test>\r\n" +
            "From: friend@example.test\r\n" +
            "To: someone-else@example.test\r\n" +
            "Subject: Lunch?\r\n" +
            "\r\n" +
            "body";

         Assert.AreEqual("keep", Evaluate(Script, addressedElsewhere));

         // A Cc counts, as do the Resent- forms.
         const string ccd =
            "Return-Path: <friend@example.test>\r\n" +
            "From: friend@example.test\r\n" +
            "To: someone-else@example.test\r\n" +
            "Cc: \"The User\" <user@example.test>\r\n" +
            "Subject: Lunch?\r\n" +
            "\r\n" +
            "body";

         Assert.AreEqual("keep;vacation", Evaluate(Script, ccd));
      }

      [Test]
      [Description("No auto-reply to the user's own address, however the message got here.")]
      public void TestNoReplyToSelf()
      {
         const string fromSelf =
            "Return-Path: <user@example.test>\r\n" +
            "From: user@example.test\r\n" +
            "To: user@example.test\r\n" +
            "Subject: Note to self\r\n" +
            "\r\n" +
            "body";

         Assert.AreEqual("keep", Evaluate(Script, fromSelf));

         Assert.AreEqual("keep;vacation", Evaluate(Script, Message()));
      }

      [Test]
      [Description("At most one auto-reply per script evaluation, and the first decision is the one that stands.")]
      public void TestOnlyOneReplyPerEvaluation()
      {
         const string twice =
            "require \"vacation\";\r\n" +
            "vacation :addresses \"user@example.test\" \"First reason.\";\r\n" +
            "vacation :addresses \"user@example.test\" \"Second reason.\";";

         Assert.AreEqual("keep;vacation", Evaluate(twice, Message()));

         // And a second vacation cannot overturn a suppression the first one
         // decided: that would make the loop guards depend on script order.
         Assert.AreEqual("keep", Evaluate(twice, Message("Precedence: bulk")));
      }
   }
}
