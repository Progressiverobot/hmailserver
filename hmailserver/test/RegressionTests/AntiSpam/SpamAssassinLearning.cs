// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.AntiSpam
{
   /// <summary>
   ///    SpamAssassinLearnOnMove: a message the user moves into their Junk folder
   ///    is spam and one they move out of it is ham, and spamd is told so with the
   ///    spamc TELL command into its local Bayes store - the feedback loop that until
   ///    now only sa-learn on the server's console could close. Off by default,
   ///    because TELL is a command an existing spamd may refuse (its --allow-tell).
   ///
   ///    Against the simulated spamd, because the question is what was sent: the
   ///    request line, Message-class, Set, and the user. The real spamd keeps no
   ///    record of what it was told.
   /// </summary>
   [TestFixture]
   public class SpamAssassinLearning : TestFixtureBase
   {
      private const string Password = "test";
      private Account _account;
      private SpamdSimulator _spamd;

      [SetUp]
      public new void SetUp()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "learner@example.test", Password);
         _spamd = new SpamdSimulator();

         var antiSpam = _settings.AntiSpam;
         antiSpam.SpamMarkThreshold = 1;
         antiSpam.SpamDeleteThreshold = 10000;
         antiSpam.SpamAssassinEnabled = true;
         antiSpam.SpamAssassinHost = "127.0.0.1";
         antiSpam.SpamAssassinPort = _spamd.Port;
         antiSpam.SpamAssassinMergeScore = false;
         antiSpam.SpamAssassinScore = 5;

         IniFileSetting.Write("SpamAssassinLearnOnMove", "1");
         IniFileSetting.Write("SpamAssassinUser", "");
         IniFileSetting.Write("SpamAssassinUserFromRecipient", "0");
         _application.Reinitialize();
      }

      [TearDown]
      public new void TearDown()
      {
         IniFileSetting.Delete("SpamAssassinLearnOnMove");
         IniFileSetting.Delete("SpamAssassinUser");
         IniFileSetting.Delete("SpamAssassinUserFromRecipient");
         _application.Reinitialize();
         _settings.AntiSpam.SpamAssassinEnabled = false;
         if (_spamd != null)
            _spamd.Dispose();
      }

      // Delivers one message (the simulated spamd sees its PROCESS as request 0),
      // creates the Junk folder, and returns a logged-in client on the INBOX.
      private ImapClientSimulator DeliverOneAndOpenTheInbox()
      {
         SmtpClientSimulator.StaticSend("sender@example.test", _account.Address, "Lesson", "Body text.");
         Pop3ClientSimulator.AssertMessageCount(_account.Address, Password, 1);
         _spamd.WaitForRequest(0);

         var client = new ImapClientSimulator();
         Assert.IsTrue(client.ConnectAndLogon(_account.Address, Password));
         Assert.IsTrue(client.CreateFolder("Junk"));
         Assert.IsTrue(client.SelectFolder("INBOX"));
         return client;
      }

      private static void AssertTell(Dictionary<string, string> request, string messageClass)
      {
         StringAssert.StartsWith("TELL SPAMC/", request["Request"], "The lesson is spamc's TELL command.");
         Assert.AreEqual(messageClass, request["Message-class"], "What spamd was told the message is.");
         Assert.AreEqual("local", request["Set"], "Into spamd's local Bayes store.");
         Assert.IsFalse(request.ContainsKey("User"), "No user profile is named unless configured.");
      }

      [Test]
      [Description("A message moved into the Junk folder is reported to spamd as spam: TELL, Message-class: spam, Set: local.")]
      public void MovingIntoJunkTeachesSpam()
      {
         ImapClientSimulator client = DeliverOneAndOpenTheInbox();
         string response = client.SendSingleCommand("A10 MOVE 1 Junk");
         StringAssert.Contains("A10 OK", response);
         client.Disconnect();

         AssertTell(_spamd.WaitForRequest(1), "spam");
      }

      [Test]
      [Description("A message moved out of the Junk folder is reported as ham; a copy into Junk counts as a lesson too, because COPY-then-delete is how older clients move.")]
      public void MovingOutOfJunkTeachesHamAndCopyingInTeachesSpam()
      {
         ImapClientSimulator client = DeliverOneAndOpenTheInbox();
         Assert.IsTrue(client.Copy(1, "Junk"), "COPY into Junk");
         AssertTell(_spamd.WaitForRequest(1), "spam");

         Assert.IsTrue(client.SelectFolder("Junk"));
         string response = client.SendSingleCommand("A11 MOVE 1 INBOX");
         StringAssert.Contains("A11 OK", response);
         client.Disconnect();

         AssertTell(_spamd.WaitForRequest(2), "ham");
      }

      [Test]
      [Description("With SpamAssassinUserFromRecipient on, the lesson names the mailbox owner, so it lands in the store that owner's verdicts come from.")]
      public void TheLessonNamesTheOwnerWhenPreferencesFollowTheRecipient()
      {
         IniFileSetting.Write("SpamAssassinUserFromRecipient", "1");
         _application.Reinitialize();

         ImapClientSimulator client = DeliverOneAndOpenTheInbox();
         string response = client.SendSingleCommand("A12 MOVE 1 Junk");
         StringAssert.Contains("A12 OK", response);
         client.Disconnect();

         Dictionary<string, string> request = _spamd.WaitForRequest(1);
         StringAssert.StartsWith("TELL SPAMC/", request["Request"]);
         Assert.AreEqual("spam", request["Message-class"]);
         Assert.AreEqual(_account.Address, request["User"], "The owner of the Junk folder is the user spamd learns as.");
      }

      [Test]
      [Description("Off by default: without the setting a move into Junk sends spamd nothing.")]
      public void OffByDefaultNothingIsSent()
      {
         IniFileSetting.Delete("SpamAssassinLearnOnMove");
         _application.Reinitialize();

         ImapClientSimulator client = DeliverOneAndOpenTheInbox();
         string response = client.SendSingleCommand("A13 MOVE 1 Junk");
         StringAssert.Contains("A13 OK", response);
         client.Disconnect();

         // Give a lesson every chance to arrive, then make sure none did: only the
         // delivery's PROCESS is on record.
         System.Threading.Thread.Sleep(1500);
         Assert.AreEqual(1, _spamd.Requests.Count, "Only the scan at delivery, no TELL.");
      }
   }
}
