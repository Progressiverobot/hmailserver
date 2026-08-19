// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System;
using System.IO;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.AntiSpam
{
   /// <summary>
   ///    The spam quarantine: hold for review instead of refusing.
   ///
   ///    Before this, the two outcomes were "mark it" and "refuse it", and refusing
   ///    happens DURING the SMTP conversation with a 550 - the message is never
   ///    accepted, so there is nothing to review and nothing to release. Quarantining
   ///    is therefore a different decision rather than a tidier one, and the tests
   ///    below are shaped around the thing that actually changes: the SMTP reply.
   ///
   ///    A refused message is answered 550/554 and the sender knows. A quarantined one
   ///    is answered 250 and the sender believes it was delivered - which is what makes
   ///    a false positive recoverable without backscatter, and also what makes the
   ///    review queue the only place that message now exists. TheSenderIsToldItWasAccepted
   ///    is the test that pins that difference, and ANonQuarantinedServerStillRefuses is
   ///    its negative control: with the feature off, nothing about the old behaviour
   ///    moves.
   /// </summary>
   [TestFixture]
   public class Quarantine : TestFixtureBase
   {
      private const string SurblTestPoint = "surbl-org-permanent-test-point.com.multi.surbl.org";
      private const string SpamBody =
         "This is a test message with a SURBL url: -> http://surbl-org-permanent-test-point.com/ <-";

      private FakeDnsServer dns_;
      private Account _recipient;

      [OneTimeSetUp]
      public void PointTheServerAtALocalResolver()
      {
         // The verdict has to be real - this fixture is about what happens AFTER a
         // message is judged spam - but it must not depend on a live blacklist, for
         // the reasons recorded against every other fixture that used to.
         dns_ = new FakeDnsServer().WithA(SurblTestPoint, "127.0.0.2");

         ServerIniFile.SetSetting("DNSServer", "127.0.0.1");

         RestartServerAndReacquireCom();
      }

      [OneTimeTearDown]
      public void RestoreTheSystemResolver()
      {
         try
         {
            ServerIniFile.SetSetting("DNSServer", null);
            ServerIniFile.SetSetting("QuarantineEnabled", null);
            RestartServerAndReacquireCom();
         }
         finally
         {
            dns_?.Dispose();
         }
      }

      [SetUp]
      public new void SetUp()
      {
         _recipient = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "quarantined@example.test", "test");

         var antiSpam = _application.Settings.AntiSpam;
         antiSpam.SpamMarkThreshold = 1;
         antiSpam.SpamDeleteThreshold = 5;

         var surbl = antiSpam.SURBLServers[0];
         surbl.Active = true;
         surbl.Score = 10;
         surbl.Save();

         EmptyTheQuarantine();
      }

      [TearDown]
      public new void TearDown()
      {
         EmptyTheQuarantine();

         ServerIniFile.SetSetting("QuarantineEnabled", null);
         _application.Reinitialize();

         var surbl = _application.Settings.AntiSpam.SURBLServers[0];
         surbl.Active = false;
         surbl.Save();
      }

      private void EmptyTheQuarantine()
      {
         var quarantine = _application.Settings.AntiSpam.Quarantine;
         quarantine.Refresh();

         while (quarantine.Count > 0)
         {
            quarantine.DeleteByDBID(quarantine[0].ID);
            quarantine.Refresh();
         }
      }

      private void EnableQuarantine()
      {
         ServerIniFile.SetSetting("QuarantineEnabled", "1");
         _application.Reinitialize();
      }

      /// <summary>Sends the spam message and returns the server's reply to the final dot.</summary>
      private string SendSpam(string subject = "Quarantine test")
      {
         var socket = new TcpConnection();
         ClassicAssert.IsTrue(socket.Connect(25), "Could not connect to SMTP.");
         socket.ReadUntil("220");

         socket.Send("HELO test\r\n");
         socket.ReadUntil("250");

         socket.Send("MAIL FROM:<outsider@example.com>\r\n");
         socket.ReadUntil("\r\n");

         socket.Send("RCPT TO:<" + _recipient.Address + ">\r\n");
         socket.ReadUntil("\r\n");

         socket.Send("DATA\r\n");
         socket.ReadUntil("\r\n");

         socket.Send("From: outsider@example.com\r\n" +
                     "To: " + _recipient.Address + "\r\n" +
                     "Subject: " + subject + "\r\n" +
                     "\r\n" +
                     SpamBody + "\r\n" +
                     ".\r\n");

         string reply = socket.ReadUntil("\r\n");

         socket.Send("QUIT\r\n");
         socket.Disconnect();

         return reply;
      }

      [Test]
      [Description("THE difference: a quarantined message is ACCEPTED, where a refused one is not")]
      public void TheSenderIsToldItWasAccepted()
      {
         EnableQuarantine();

         string reply = SendSpam();

         StringAssert.StartsWith("250", reply,
            "A quarantined message must be accepted. The sender believing it was delivered is the " +
            "whole point - it is what makes a false positive recoverable without bouncing to a " +
            "return path that is probably forged. Got: " + reply);

         // ...and it is nowhere near the mailbox.
         ClassicAssert.AreEqual(0, new Pop3ClientSimulator().GetMessageCount(_recipient.Address, "test"),
            "Accepted is not delivered. A quarantine that also delivers is just a slow mark.");

         var quarantine = _application.Settings.AntiSpam.Quarantine;
         ClassicAssert.AreEqual(1, quarantine.Count, "The message must be held.");
      }

      [Test]
      [Description("The negative control: with quarantine off, spam is refused exactly as it always was")]
      public void ANonQuarantinedServerStillRefuses()
      {
         string reply = SendSpam();

         StringAssert.StartsWith("55", reply,
            "Off by default means off. If this ever returns 250 then an upgrade has silently " +
            "started accepting and storing mail that used to be turned away at the door. Got: " + reply);

         ClassicAssert.AreEqual(0, _application.Settings.AntiSpam.Quarantine.Count,
            "...and nothing is stored.");
      }

      [Test]
      [Description("The review queue carries what a human needs to judge it: who, to whom, subject, why and how badly")]
      public void TheQueueRecordsEnoughToDecideWith()
      {
         EnableQuarantine();
         SendSpam("Invoice 4417");

         var quarantine = _application.Settings.AntiSpam.Quarantine;
         quarantine.Refresh();

         var message = quarantine[0];

         ClassicAssert.AreEqual("outsider@example.com", message.Sender);
         StringAssert.Contains(_recipient.Address, message.Recipients,
            "Who it would be released to is the first thing an administrator needs.");
         ClassicAssert.AreEqual("Invoice 4417", message.Subject,
            "The subject is read from the stored message - a queue that cannot show one is not reviewable.");
         ClassicAssert.GreaterOrEqual(message.Score, 5, "The score that made the decision.");
         ClassicAssert.Greater(message.Size, 0);
         ClassicAssert.IsNotEmpty(message.Reason, "Why it was held.");
         ClassicAssert.IsNotEmpty(message.CreatedTime);
      }

      [Test]
      [Description("Releasing delivers the message and removes it from the store - the workflow the whole feature exists for")]
      public void ReleasingDeliversItAndClearsTheEntry()
      {
         EnableQuarantine();
         SendSpam("Released invoice");

         var quarantine = _application.Settings.AntiSpam.Quarantine;
         quarantine.Refresh();

         ClassicAssert.AreEqual(1, quarantine.Count);
         quarantine[0].ReleaseMessage();

         Pop3ClientSimulator.AssertMessageCount(_recipient.Address, "test", 1);
         string delivered = Pop3ClientSimulator.AssertGetFirstMessageText(_recipient.Address, "test");
         StringAssert.Contains("Released invoice", delivered,
            "The released message itself must arrive. Got: " + delivered);

         var after = _application.Settings.AntiSpam.Quarantine;
         ClassicAssert.AreEqual(0, after.Count,
            "A released message is no longer held - otherwise releasing twice delivers twice.");
      }

      [Test]
      [Description("Releasing does not re-run the filters that quarantined it, or it would simply be quarantined again")]
      public void AReleasedMessageIsNotQuarantinedAgain()
      {
         EnableQuarantine();
         SendSpam("Still spam by the rules");

         var quarantine = _application.Settings.AntiSpam.Quarantine;
         quarantine.Refresh();
         quarantine[0].ReleaseMessage();

         // The message is still exactly as spammy as it was; the administrator has
         // simply overruled that. Re-entering at the delivery queue rather than at
         // SMTP is what makes the override stick.
         Pop3ClientSimulator.AssertMessageCount(_recipient.Address, "test", 1);

         var after = _application.Settings.AntiSpam.Quarantine;
         ClassicAssert.AreEqual(0, after.Count,
            "If this is 1, releasing has fed the message back through the filters and the " +
            "administrator can never get it out.");
      }

      [Test]
      [Description("Deleting removes the entry and the stored file - there is no other copy, so it is a deliberate act")]
      public void DeletingRemovesTheEntryAndTheFile()
      {
         EnableQuarantine();
         SendSpam();

         var quarantine = _application.Settings.AntiSpam.Quarantine;
         quarantine.Refresh();
         ClassicAssert.AreEqual(1, quarantine.Count);

         quarantine.DeleteByDBID(quarantine[0].ID);

         var after = _application.Settings.AntiSpam.Quarantine;
         ClassicAssert.AreEqual(0, after.Count);

         ClassicAssert.AreEqual(0, new Pop3ClientSimulator().GetMessageCount(_recipient.Address, "test"),
            "Deleting must not deliver it either.");

         // The file goes with the row. A file with no row is invisible and nothing
         // would ever sweep it.
         string quarantineDirectory = Path.Combine(_application.Settings.Directories.DataDirectory, "Quarantine");

         if (Directory.Exists(quarantineDirectory))
         {
            ClassicAssert.AreEqual(0, Directory.GetFiles(quarantineDirectory, "*.eml", SearchOption.AllDirectories).Length,
               "The stored message file must be deleted with its row.");
         }
      }
   }
}
