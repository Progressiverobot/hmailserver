// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    The two operator-facing privacy operations: per-account export
   ///    (Account.ExportMessages) and address erasure
   ///    (Utilities.EraseAddressTraces).
   ///
   ///    Erasure is deliberately a separate operation from deleting the account,
   ///    because the two answer different requests: "close this mailbox" must
   ///    not silently destroy an archive that may be under a legal hold, and
   ///    "remove this person" usually arrives after the mailbox is already gone.
   ///    So the eraser works on an ADDRESS, and these tests never create an
   ///    account for the address they erase.
   /// </summary>
   [TestFixture]
   public class PrivacyTools : TestFixtureBase
   {
      [Test]
      [Description("Export mirrors the folder tree as .eml files and reports how many - a partial export raises an error instead of a smaller number")]
      public void ExportMirrorsTheFolderTreeAsEmlFiles()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "exportsubject@example.test", "test");

         // Two messages into the inbox the ordinary way.
         SmtpClientSimulator.StaticSend("sender@dummy-example.com", account.Address, "First", "The first exported body.");
         SmtpClientSimulator.StaticSend("sender@dummy-example.com", account.Address, "Second", "The second exported body.");
         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 2);

         // And one in a folder the person made themselves, because an export
         // that only handles the inbox is an export of the easy half.
         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(account.Address, "test"));
         Assert.IsTrue(imap.CreateFolder("Projects"));
         imap.SendSingleCommandWithLiteral("A10 APPEND Projects {59}",
            "Subject: Filed\r\n\r\nA body filed under Projects for export.\r\n");
         imap.Disconnect();

         string exportDirectory = Path.Combine(Path.GetTempPath(), "hm-export-" + Guid.NewGuid().ToString("N"));

         try
         {
            int exported = account.ExportMessages(exportDirectory);

            ClassicAssert.AreEqual(3, exported,
               "Two delivered messages plus one appended one should export as exactly three.");

            var files = Directory.GetFiles(exportDirectory, "*.eml", SearchOption.AllDirectories);
            ClassicAssert.AreEqual(3, files.Length,
               "The reported count must be the number of files actually written.");

            // The structure is the point: the filed message sits under a
            // directory named for its folder, not in one flat heap.
            string filed = null;
            foreach (string file in files)
            {
               if (Directory.GetParent(file).Name.Equals("Projects", StringComparison.OrdinalIgnoreCase))
                  filed = file;
            }

            Assert.IsNotNull(filed, "The appended message should be under a 'Projects' directory. Files: " +
               string.Join(", ", files));

            StringAssert.Contains("A body filed under Projects for export.", File.ReadAllText(filed),
               "The exported file must be the message itself, byte content included.");
         }
         finally
         {
            if (Directory.Exists(exportDirectory))
               Directory.Delete(exportDirectory, true);
         }
      }

      [Test]
      [Description("Erasure removes the traces account deletion leaves: aliases, list memberships, greylisting triplets and quarantine rows - and a second pass finds nothing")]
      public void ErasureRemovesWhatAccountDeletionLeaves()
      {
         const string erased = "erased-person@example.test";

         var target = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "erasetarget@example.test", "test");

         // An alias forwarding the person's mail, and a list they are on.
         SingletonProvider<TestSetup>.Instance.AddAlias(_domain, "erased-alias@example.test", erased);
         SingletonProvider<TestSetup>.Instance.AddDistributionList(_domain, "erased-list@example.test",
            new List<string> {erased});

         // A greylisting triplet made the real way: greylisting defers the
         // first attempt and records (ip, sender, recipient).
         var antiSpam = _settings.AntiSpam;
         antiSpam.GreyListingEnabled = true;

         try
         {
            CustomAsserts.Throws<DeliveryFailedException>(
               () => SmtpClientSimulator.StaticSend(erased, target.Address, "Greylisted", "Body"));
         }
         finally
         {
            antiSpam.GreyListingEnabled = false;
         }

         // A quarantine row, seeded directly. Its file deliberately does not
         // exist: QuarantineStore.Delete removes the row first and treats a
         // missing file as already gone, so this pins that the eraser survives
         // the store's own broken-entry case.
         SingletonProvider<TestSetup>.Instance.GetApp().Database.ExecuteSQL(
            "insert into hm_quarantine (quarantinefilename, quarantinesender, quarantinerecipients, quarantinesubject, quarantinereason, quarantinescore, quarantinesize, quarantinecreated) " +
            "values ('privacy-test-no-such-file.eml', '" + erased + "', 'somebody@dummy-example.com', 'subject', 'reason', 10, 100, GETDATE())");

         int removed = SingletonProvider<TestSetup>.Instance.GetApp().Utilities.EraseAddressTraces(erased, true);

         // One alias + one list membership + at least one triplet + one
         // quarantine row. ">= 4" rather than "== 4" because greylisting is
         // free to have recorded more than one row for the deferred attempt.
         Assert.GreaterOrEqual(removed, 4,
            "The alias, the list membership, the greylisting triplet and the quarantine row should all be counted.");

         // The functional half: the list genuinely no longer carries the member.
         var list = _domain.DistributionLists.get_ItemByAddress("erased-list@example.test");
         ClassicAssert.AreEqual(0, list.Recipients.Count,
            "The erased address must be off the list, not merely uncounted.");

         // And a second pass finds nothing, which is the cheapest complete
         // proof that the first pass reached everything it counts.
         int second = SingletonProvider<TestSetup>.Instance.GetApp().Utilities.EraseAddressTraces(erased, true);
         ClassicAssert.AreEqual(0, second,
            "A second erasure of the same address must find nothing left to remove.");
      }
   }
}
