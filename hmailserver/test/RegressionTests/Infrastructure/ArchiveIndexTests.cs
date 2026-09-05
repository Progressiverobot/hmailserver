// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    The archive index (hm_archiveindex, schema 6029): every copy the archiver
   ///    writes gets a row - who, when, what, where - so the archive can be searched
   ///    by record, a copy can be put on legal hold, and the sweep and the address
   ///    eraser forget what they remove. Every assertion here is on the index as
   ///    the server reports it through COM, against files that really exist on
   ///    disk or really do not.
   /// </summary>
   [TestFixture]
   public class ArchiveIndexTests : TestFixtureBase
   {
      private string archiveRoot_;

      [SetUp]
      public void CreateTheArchiveRoot()
      {
         archiveRoot_ = Paths.Combine(Path.GetTempPath(), "hmail-archive-index-" + Guid.NewGuid().ToString("N"));
         Directory.CreateDirectory(archiveRoot_);
      }

      [TearDown]
      public void RemoveTheArchive()
      {
         ServerIniFile.SetSetting("ArchiveDir", null);
         ServerIniFile.SetSetting("ArchiveRetentionDays", null);
         RestartServerAndReacquireCom();

         try
         {
            if (archiveRoot_ != null && Directory.Exists(archiveRoot_))
               Directory.Delete(archiveRoot_, true);
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
         }
      }

      private string Search(string domain = "", string mailbox = "", string sender = "", string recipient = "",
         string subject = "", bool holdOnly = false)
      {
         return _application.Utilities.SearchArchive(domain, mailbox, sender, recipient, subject, "", "", holdOnly, 100);
      }

      // Rows outlive the fixtures that wrote them - the archive outlives its accounts
      // by design, and a deleted directory does not delete its records - so every
      // count here is scoped to a subject no other test or run has used.
      private static string UniqueNeedle()
      {
         return "needle " + Guid.NewGuid().ToString("N").Substring(0, 8);
      }

      private static int Count(string json, string needle)
      {
         int count = 0;
         for (int at = json.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = json.IndexOf(needle, at + 1, StringComparison.Ordinal))
            count++;
         return count;
      }

      private static long FirstId(string json)
      {
         const string needle = "\"id\":";
         int at = json.IndexOf(needle, StringComparison.Ordinal);
         Assert.IsTrue(at >= 0, "No entry in: " + json);
         int start = at + needle.Length;
         int end = json.IndexOf(',', start);
         return long.Parse(json.Substring(start, end - start));
      }

      private static string PathOf(string json)
      {
         const string needle = "\"path\":\"";
         int at = json.IndexOf(needle, StringComparison.Ordinal);
         Assert.IsTrue(at >= 0, "No path in: " + json);
         int start = at + needle.Length;
         int end = json.IndexOf('"', start);
         return json.Substring(start, end - start).Replace("\\\\", "\\");
      }

      private void ArchiveOneMessage(string from, string to, string subject)
      {
         SmtpClientSimulator.StaticSend(from, to, subject, "Body text.");
         CustomAsserts.AssertRecipientsInDeliveryQueue(0);
      }

      [Test]
      [Description("A message between two local users leaves a Sent row for the sender and a Received row for the recipient, each naming the copy that exists on disk, searchable by domain, mailbox, sender, recipient and subject.")]
      public void ArchivedCopiesAreIndexed()
      {
         // Addresses as strings: an Account proxy does not survive the restart below.
         string alice = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "alice@example.test", "test").Address;
         string bob = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "bob@example.test", "test").Address;

         ServerIniFile.SetSetting("ArchiveDir", archiveRoot_);
         RestartServerAndReacquireCom();

         string needle = UniqueNeedle();
         ArchiveOneMessage(alice, bob, needle);

         string all = Search(domain: "example.test", subject: needle);
         Assert.AreEqual(2, Count(all, "\"id\":"), "One row per copy - the sender's Sent copy and the recipient's copy: " + all);
         StringAssert.Contains("\"direction\":\"sent\"", all);
         StringAssert.Contains("\"direction\":\"received\"", all);
         StringAssert.Contains("\"subject\":\"" + needle + "\"", all);
         StringAssert.Contains("\"sender\":\"alice@example.test\"", all);
         StringAssert.Contains("\"recipients\":\"bob@example.test\"", all);
         StringAssert.Contains("\"message_id\":\"", all);
         StringAssert.Contains("\"hold\":false", all);

         string bobsCopy = Search(domain: "example.test", mailbox: "bob", subject: needle);
         Assert.AreEqual(1, Count(bobsCopy, "\"id\":"), bobsCopy);
         StringAssert.Contains("\"direction\":\"received\"", bobsCopy);
         Assert.IsTrue(File.Exists(PathOf(bobsCopy)), "The row names a copy that exists: " + PathOf(bobsCopy));

         Assert.AreEqual(2, Count(Search(subject: needle.Substring(3)), "\"id\":"), "subject is a contains match");
         Assert.AreEqual(2, Count(Search(sender: "alice@", subject: needle), "\"id\":"), "sender is a contains match");
         Assert.AreEqual(2, Count(Search(recipient: "bob@example", subject: needle), "\"id\":"), "recipient is a contains match");
         Assert.AreEqual(0, Count(Search(domain: "other.test", subject: needle), "\"id\":"), "another domain sees nothing");
         Assert.AreEqual(0, Count(Search(subject: "no such subject"), "\"id\":"));
      }

      [Test]
      [Description("A copy on legal hold survives the retention sweep with its row; the copy without a hold is removed and its row goes with it.")]
      public void AHeldCopySurvivesTheSweepAndARemovedCopyLosesItsRow()
      {
         // Addresses as strings: an Account proxy does not survive the restart below.
         string alice = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "alice@example.test", "test").Address;
         string bob = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "bob@example.test", "test").Address;

         ServerIniFile.SetSetting("ArchiveDir", archiveRoot_);
         RestartServerAndReacquireCom();

         string needle = UniqueNeedle();
         ArchiveOneMessage(alice, bob, needle);

         string bobsCopy = Search(domain: "example.test", mailbox: "bob", subject: needle);
         string alicesCopy = Search(domain: "example.test", mailbox: "alice", subject: needle);
         long heldId = FirstId(bobsCopy);
         string heldPath = PathOf(bobsCopy);
         string sweptPath = PathOf(alicesCopy);
         Assert.IsTrue(File.Exists(heldPath) && File.Exists(sweptPath));

         Assert.IsTrue(_application.Utilities.SetArchiveHold((int) heldId, true), "the row exists, so the hold is placed");
         Assert.IsFalse(_application.Utilities.SetArchiveHold(987654321, true), "no such row, so nothing to hold");
         StringAssert.Contains("\"hold\":true", Search(domain: "example.test", subject: needle, holdOnly: true));

         // Both copies are older than the window now; only the hold keeps one.
         File.SetLastWriteTimeUtc(heldPath, DateTime.UtcNow.AddDays(-30));
         File.SetLastWriteTimeUtc(sweptPath, DateTime.UtcNow.AddDays(-30));

         ServerIniFile.SetSetting("ArchiveRetentionDays", "7");
         RestartServerAndReacquireCom();
         for (int attempt = 0; attempt < 30 && File.Exists(sweptPath); attempt++)
            Thread.Sleep(500);

         Assert.IsFalse(File.Exists(sweptPath), "The copy without a hold is past the window and is removed: " + sweptPath);
         Assert.IsTrue(File.Exists(heldPath), "The held copy is past the window and stays: " + heldPath);

         string after = Search(domain: "example.test", subject: needle);
         Assert.AreEqual(1, Count(after, "\"id\":"), "The removed copy's row went with it; the held one's stayed: " + after);
         StringAssert.Contains("\"mailbox\":\"bob\"", after);
         StringAssert.Contains("\"hold\":true", after);

         Assert.IsTrue(_application.Utilities.SetArchiveHold((int) heldId, false), "the hold is lifted");
         StringAssert.Contains("\"hold\":false", Search(domain: "example.test", subject: needle));
      }

      [Test]
      [Description("Erasing an address's traces forgets its archive rows along with its files, except a row on hold.")]
      public void ErasingAnAddressForgetsItsRowsExceptAHeldOne()
      {
         // Addresses as strings: an Account proxy does not survive the restart below.
         string alice = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "alice@example.test", "test").Address;
         string carol = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "carol@example.test", "test").Address;

         ServerIniFile.SetSetting("ArchiveDir", archiveRoot_);
         RestartServerAndReacquireCom();

         string needle = UniqueNeedle();
         ArchiveOneMessage(alice, carol, needle + " first");
         ArchiveOneMessage(alice, carol, needle + " second");

         string carols = Search(domain: "example.test", mailbox: "carol", subject: needle);
         Assert.AreEqual(2, Count(carols, "\"id\":"), carols);
         long held = FirstId(carols);
         Assert.IsTrue(_application.Utilities.SetArchiveHold((int) held, true));

         // Alice's two Sent rows name carol as the recipient, so they go too.
         Assert.AreEqual(4, Count(Search(recipient: "carol@example.test", subject: needle), "\"id\":"));

         _application.Utilities.EraseAddressTraces(carol, true);

         string remaining = Search(recipient: "carol@example.test", subject: needle);
         Assert.AreEqual(1, Count(remaining, "\"id\":"), "Only the held row survives an erasure: " + remaining);
         StringAssert.Contains("\"hold\":true", remaining);
      }
   }
}
