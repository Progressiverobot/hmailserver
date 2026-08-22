// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   /// The property that makes a database-backend migration possible, asserted rather
   /// than assumed.
   ///
   /// Moving an installation off SQL Server Compact - which the installer still picks
   /// by default - needs no tool: back up, point the server at the new database,
   /// restore. That works because nothing identity-shaped crosses the archive boundary.
   /// Account::XMLStore writes Name and Password and no accountid, XMLLoad sets none,
   /// so SaveObject inserts and lets the new database assign its own identities; and the
   /// message store on disk is addressed by domain name, account name and the file's own
   /// guid, never by a row id. Change every id and every file is still where the server
   /// will look for it.
   ///
   /// This fixture cannot run a real cross-backend restore, because the suite runs
   /// against one backend. What it can do is the half that actually carries the risk:
   /// force every identity in the database to be reassigned, and prove that a message
   /// delivered before the move is still found afterwards, at a byte-identical path.
   /// A different backend reassigns identities too - that is all it does differently
   /// here - so a restore that survives reassignment is a restore that survives the move.
   ///
   /// See docs/MigratingDatabaseBackend.md, which this fixture is the evidence for.
   /// </summary>
   [TestFixture]
   public class BackendPortability : TestFixtureBase
   {
      private const string AccountAddress = "portability@example.test";
      private const string AliasAddress = "portability-alias@example.test";
      private const string ListAddress = "portability-list@example.test";

      [Test]
      [Description("A restore reassigns every database identity and the mail store is still found by name - the property a backend migration relies on")]
      public void RestoreReassignsIdentitiesAndTheMailStoreIsStillFoundByName()
      {
         var backupDirectory = Path.Combine(Path.GetTempPath(), TestSetup.UniqueString());
         Directory.CreateDirectory(backupDirectory);

         try
         {
            var domainName = _domain.Name;

            var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, AccountAddress, "test");
            SingletonProvider<TestSetup>.Instance.AddAlias(_domain, AliasAddress, AccountAddress);
            SingletonProvider<TestSetup>.Instance.AddDistributionList(_domain, ListAddress,
               new List<string> {AccountAddress});

            SmtpClientSimulator.StaticSend(AccountAddress, AccountAddress, "Before the move", "BodyFromBeforeTheMove");
            Pop3ClientSimulator.AssertMessageCount(AccountAddress, "test", 1);

            var originalDomainId = _domain.ID;
            var originalAccountId = account.ID;
            var originalMessageFile = account.IMAPFolders.get_ItemByName("INBOX").Messages[0].Filename;

            // Stated as a composition rather than only compared with itself later, so the
            // test says what the layout is: data directory, domain name, the part of the
            // address before the @, then a fan-out folder taken from the guid's first two
            // characters - the filename starts with '{', so they are at index 1 and 2.
            // Not an id anywhere in it.
            var fileName = Path.GetFileName(originalMessageFile);
            var expectedFile = Path.Combine(
               _settings.Directories.DataDirectory,
               domainName,
               "portability",
               fileName.Substring(1, 2),
               fileName);

            ClassicAssert.AreEqual(expectedFile, originalMessageFile,
               "The message store is supposed to be addressed by name. If this path contains a database id, a backend migration moves the mail out from under the server.");
            ClassicAssert.IsTrue(File.Exists(originalMessageFile), "The message file is not where the database says it is.");

            Backup(backupDirectory);

            // The negative control. Without this the restore could leave everything in
            // place and every assertion below would still pass.
            while (_application.Domains.Count > 0)
               _application.Domains[0].Delete();

            ClassicAssert.AreEqual(0, _application.Domains.Count);

            Restore(backupDirectory);

            var restoredDomain = _application.Domains.get_ItemByName(domainName);
            ClassicAssert.IsNotNull(restoredDomain, "The domain did not come back from the archive.");

            var restoredAccount = restoredDomain.Accounts.get_ItemByAddress(AccountAddress);
            ClassicAssert.IsNotNull(restoredAccount, "The account did not come back from the archive.");

            // Every supported backend allocates identities from a counter that does not go
            // backwards when rows are deleted - identity columns on MSSQL and SQL CE,
            // AUTO_INCREMENT on MySQL, a sequence on PostgreSQL - so the rows the restore
            // inserted cannot have landed on the numbers the deleted ones held. That makes
            // this deterministic rather than incidental, and it is the whole point of the
            // fixture: what follows is asserted against an object graph whose every id is
            // new, which is exactly the state a restore onto a different backend produces.
            ClassicAssert.AreNotEqual(originalDomainId, restoredDomain.ID,
               "The domain kept its id, so this run did not actually exercise reassignment.");
            ClassicAssert.AreNotEqual(originalAccountId, restoredAccount.ID,
               "The account kept its id, so this run did not actually exercise reassignment.");

            // Restored by name, under new identities.
            ClassicAssert.IsNotNull(restoredDomain.Aliases.get_ItemByName(AliasAddress),
               "The alias did not come back from the archive.");
            ClassicAssert.IsNotNull(restoredDomain.DistributionLists.get_ItemByAddress(ListAddress),
               "The distribution list did not come back from the archive.");

            // The message row found its file again despite the account id having changed,
            // and the path is the one that was written before the move.
            var restoredMessage = restoredAccount.IMAPFolders.get_ItemByName("INBOX").Messages[0];
            ClassicAssert.AreEqual(originalMessageFile, restoredMessage.Filename,
               "The restored message points somewhere else on disk than it did before.");
            ClassicAssert.IsTrue(File.Exists(restoredMessage.Filename));

            // The alias and the list still deliver, so the references restored through the
            // object graph resolve to the reassigned account rather than to the old number.
            SmtpClientSimulator.StaticSend(AccountAddress, AliasAddress, "Via the alias", "BodyViaAlias");
            SmtpClientSimulator.StaticSend(AccountAddress, ListAddress, "Via the list", "BodyViaList");
            Pop3ClientSimulator.AssertMessageCount(AccountAddress, "test", 3);

            // Last, because retrieving deletes: the pre-move message is readable end to
            // end rather than merely listed, which is what proves the row, the file on
            // disk and the reassigned account id all still agree. Asserting on the body
            // it was sent with also pins it to the message from before the move rather
            // than to either of the two just delivered.
            var body = Pop3ClientSimulator.AssertGetFirstMessageText(AccountAddress, "test");
            StringAssert.Contains("BodyFromBeforeTheMove", body);
         }
         finally
         {
            try
            {
               Directory.Delete(backupDirectory, true);
            }
            catch (IOException)
            {
               // The archive is the server's file, not ours; a failure to tidy it up is
               // not a reason to fail an otherwise passing test.
            }
         }
      }

      private void Backup(string backupDirectory)
      {
         var settings = _application.Settings.Backup;
         settings.BackupDomains = true;
         settings.BackupMessages = true;

         // Settings are deliberately left out. Restoring them would replace the SSL
         // certificates, ports, IP ranges and anti-spam lists this suite runs on, and
         // none of that is what this fixture is about - the identity reassignment
         // happens on the domain tree.
         settings.BackupSettings = false;
         settings.CompressDestinationFiles = true;
         settings.Destination = backupDirectory;

         CustomAsserts.AssertDeleteFile(settings.LogFile);

         _application.BackupManager.StartBackup();

         for (var i = 0; i < 120; i++)
         {
            try
            {
               var contents = TestSetup.ReadExistingTextFile(settings.LogFile);

               if (contents.IndexOf("Backup completed successfully", StringComparison.Ordinal) > 0)
                  return;

               if (contents.IndexOf("BACKUP ERROR:", StringComparison.Ordinal) > 0)
                  Assert.Fail("Backup failed: " + contents);
            }
            catch (Exception)
            {
               // The server still has the log file open, or has not created it yet.
            }

            Thread.Sleep(250);
         }

         Assert.Fail("Timed out waiting for the backup to complete.");
      }

      private void Restore(string backupDirectory)
      {
         var files = new DirectoryInfo(backupDirectory).GetFiles("*.7z");
         ClassicAssert.AreEqual(1, files.Length, "Expected exactly one archive in the backup directory.");

         var startTimeBeforeRestore = _application.Status.StartTime;

         var backup = _application.BackupManager.LoadBackup(files[0].FullName);
         backup.RestoreDomains = true;
         backup.RestoreMessages = true;
         backup.RestoreSettings = false;
         backup.StartRestore();

         // A restore ends by restarting the server, so a changed start time is the
         // completion signal. The process itself does not restart, so the COM object
         // stays valid across it.
         for (var i = 0; i < 600; i++)
         {
            try
            {
               var startTime = _application.Status.StartTime;

               if (startTime.Length > 0 && startTime != startTimeBeforeRestore)
                  return;
            }
            catch (Exception)
            {
               // The server is mid-restart.
            }

            Thread.Sleep(100);
         }

         Assert.Fail("Timed out waiting for the restore to complete.");
      }
   }
}
