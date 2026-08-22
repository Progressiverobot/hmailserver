// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.SMTP
{
   /// <summary>
   ///    Telling an account holder their mailbox is filling up, before it fills.
   ///
   ///    Nothing did, so the first anyone heard of a full mailbox was mail that
   ///    stopped arriving - and since the refusal now happens during the sending
   ///    server's SMTP conversation, the person it happens to is the last to find out.
   ///
   ///    What is worth testing here is not that a notice is sent. It is that exactly
   ///    ONE is. A warning on every delivery while a mailbox sits above the threshold
   ///    would be worse than none: dozens of identical notices, each consuming a
   ///    little of the space they are complaining about, in a mailbox already short of
   ///    room. The implementation avoids that by treating the threshold as an event
   ///    rather than a state - below before this message, at or above after it - so
   ///    the second delivery in each test is the assertion that matters.
   /// </summary>
   [TestFixture]
   public class QuotaWarning : TestFixtureBase
   {
      private const string Mailbox = "quotawarn@example.test";

      /// <summary>
      ///    A body of roughly the requested size, in lines short enough that no SMTP
      ///    line-length limit is involved in what is being measured.
      /// </summary>
      private static string BodyOfAbout(int kilobytes)
      {
         var body = new StringBuilder();
         string line = new string('x', 900) + "\r\n";

         while (body.Length < kilobytes * 1024)
            body.Append(line);

         return body.ToString();
      }

      private static int MessagesIn(string address)
      {
         return new Pop3ClientSimulator().GetMessageCount(address, "test");
      }

      [Test]
      [Description("The delivery that takes a mailbox past the threshold produces exactly one warning, and the next delivery produces none - the warning is an event, not a state")]
      public void CrossingTheThresholdWarnsOnceAndOnlyOnce()
      {
         // 1 MB quota with the warning at 50%, so two 300 KB messages straddle the
         // threshold: the first lands under it, the second crosses it.
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, Mailbox, "test");
         account.MaxSize = 1;
         account.Save();

         try
         {
            ServerIniFile.SetSetting("QuotaWarningPercent", "50");
            RestartServerAndReacquireCom();

            // Under the threshold: 300 KB of a 1024 KB quota is 29%.
            SmtpClientSimulator.StaticSend("sender@example.test", Mailbox, "first", BodyOfAbout(300));
            Pop3ClientSimulator.AssertMessageCount(Mailbox, "test", 1);

            ClassicAssert.AreEqual(1, MessagesIn(Mailbox),
               "The first message is well under the threshold, so nothing should have been sent about it.");

            // Across it: about 58%.
            SmtpClientSimulator.StaticSend("sender@example.test", Mailbox, "second", BodyOfAbout(300));
            Pop3ClientSimulator.AssertMessageCount(Mailbox, "test", 3);

            // Read message 3 without popping the mailbox: the counts below are part of
            // the assertion, so nothing here may consume a message.
            var pop3 = new Pop3ClientSimulator();
            ClassicAssert.IsTrue(pop3.ConnectAndLogon(Mailbox, "test"));
            string warning = pop3.RETR(3);
            pop3.Disconnect();

            StringAssert.Contains("mailbox is almost full", warning.ToLower(),
               "The account holder should have been told their mailbox is filling up. Got: " + warning);
            StringAssert.Contains(Mailbox, warning,
               "The notice should name the mailbox it is about. Got: " + warning);
            StringAssert.Contains("postmaster@example.test", warning,
               "The notice comes from the postmaster of the recipient's own domain, because that is " +
               "who can do something about it. Got: " + warning);

            // The assertion the whole design exists for. A third delivery leaves the
            // mailbox still over the threshold, and a naive "is it over?" check would
            // send another notice here - and another for every message after that.
            SmtpClientSimulator.StaticSend("sender@example.test", Mailbox, "third", BodyOfAbout(50));
            Pop3ClientSimulator.AssertMessageCount(Mailbox, "test", 4);

            ClassicAssert.AreEqual(4, MessagesIn(Mailbox),
               "Only the crossing warns. A mailbox that is still over the threshold must not be " +
               "warned about again on every delivery - that would fill it faster than the mail does.");
         }
         finally
         {
            ServerIniFile.SetSetting("QuotaWarningPercent", null);
            RestartServerAndReacquireCom();
         }
      }

      [Test]
      [Description("QuotaWarningPercent=0 sends nothing at all, so an operator who does not want the server writing to their users can say so")]
      public void TheWarningCanBeSwitchedOff()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, Mailbox, "test");
         account.MaxSize = 1;
         account.Save();

         try
         {
            ServerIniFile.SetSetting("QuotaWarningPercent", "0");
            RestartServerAndReacquireCom();

            // The same two messages that produced a warning above.
            SmtpClientSimulator.StaticSend("sender@example.test", Mailbox, "first", BodyOfAbout(300));
            SmtpClientSimulator.StaticSend("sender@example.test", Mailbox, "second", BodyOfAbout(300));

            Pop3ClientSimulator.AssertMessageCount(Mailbox, "test", 2);

            ClassicAssert.AreEqual(2, MessagesIn(Mailbox),
               "With the warning disabled the mailbox should hold the two delivered messages and " +
               "nothing else.");
         }
         finally
         {
            ServerIniFile.SetSetting("QuotaWarningPercent", null);
            RestartServerAndReacquireCom();
         }
      }
   }
}
