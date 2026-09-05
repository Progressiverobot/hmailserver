// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Threading;
using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    The verified restore - the trailing zero in 3-2-1-1-0.
   ///
   ///    Every backup that includes messages extracts its own message store back
   ///    out, through the same code a restore uses, holds it to exactly the files
   ///    the backup staged, and reconciles the message rows in the index against
   ///    the files. The first is a question about the archive and a wrong answer
   ///    discards it; the second is a question about the server, so its answer is
   ///    reported by name and never fails the backup. Both are pinned here, and so
   ///    is the switch that turns the whole step off for a store the temp volume
   ///    cannot hold twice.
   /// </summary>
   [TestFixture]
   public class BackupVerifiedRestore : TestFixtureBase
   {
      private string _backupDirectory;

      private string _previousDestination;
      private bool _previousBackupDomains;
      private bool _previousBackupMessages;
      private bool _previousBackupSettings;
      private bool _previousCompress;

      [SetUp]
      public void SetUpBackupVerifiedRestore()
      {
         _backupDirectory = Paths.Combine(Path.GetTempPath(), "hMailBackupVerified-" + TestSetup.UniqueString());
         Directory.CreateDirectory(_backupDirectory);

         var backup = _application.Settings.Backup;

         _previousDestination = backup.Destination;
         _previousBackupDomains = backup.BackupDomains;
         _previousBackupMessages = backup.BackupMessages;
         _previousBackupSettings = backup.BackupSettings;
         _previousCompress = backup.CompressDestinationFiles;

         backup.Destination = _backupDirectory;
         backup.BackupDomains = true;
         backup.BackupMessages = true;
         backup.BackupSettings = true;
         backup.CompressDestinationFiles = true;

         CustomAsserts.AssertDeleteFile(backup.LogFile);
      }

      [TearDown]
      public void TearDownBackupVerifiedRestore()
      {
         var backup = _application.Settings.Backup;

         backup.Destination = _previousDestination;
         backup.BackupDomains = _previousBackupDomains;
         backup.BackupMessages = _previousBackupMessages;
         backup.BackupSettings = _previousBackupSettings;
         backup.CompressDestinationFiles = _previousCompress;

         try
         {
            if (Directory.Exists(_backupDirectory))
               Directory.Delete(_backupDirectory, true);
         }
         catch (IOException)
         {
            // A temporary directory left behind is not worth failing a test over.
         }
      }

      // Takes the domain rather than using _domain, because a test that has restarted
      // the service holds a dead proxy in _domain and must fetch the domain again.
      private static Account DeliverTwoMessages(Domain domain)
      {
         Account account = SingletonProvider<TestSetup>.Instance.AddAccount(domain, "verified@" + domain.Name, "test");

         SmtpClientSimulator.StaticSend("sender@" + domain.Name, account.Address, "First", "The first message.");
         SmtpClientSimulator.StaticSend("sender@" + domain.Name, account.Address, "Second", "The second message.");
         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 2);

         return account;
      }

      private string ReadBackupLog()
      {
         string logFile = _application.Settings.Backup.LogFile;

         try
         {
            if (!File.Exists(logFile))
               return "(no backup log)";

            // FileShare.ReadWrite: the server holds this log open.
            using (var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
            {
               return reader.ReadToEnd();
            }
         }
         catch (IOException exception)
         {
            return "(backup log could not be read: " + exception.Message + ")";
         }
      }

      private bool WaitForBackupLog(string expected, int timeoutSeconds)
      {
         for (int attempt = 0; attempt < timeoutSeconds * 4; attempt++)
         {
            if (ReadBackupLog().Contains(expected))
               return true;

            Thread.Sleep(250);
         }

         return false;
      }

      private string RunBackupAndWaitForSuccess()
      {
         _application.BackupManager.StartBackup();

         Assert.That(WaitForBackupLog("Backup completed successfully", 300),
            "The backup did not complete. Backup log: " + ReadBackupLog());

         return ReadBackupLog();
      }

      // The scratch directory the log names: "...was extracted to <dir> and reads back as..."
      private static string ExtractedDirectory(string log)
      {
         const string prefix = "was extracted to ";
         const string suffix = " and reads back as";

         int start = log.IndexOf(prefix, StringComparison.Ordinal);
         Assert.That(start, Is.GreaterThanOrEqualTo(0), "The log does not say where the store was extracted. Backup log: " + log);
         start += prefix.Length;

         int end = log.IndexOf(suffix, start, StringComparison.Ordinal);
         Assert.That(end, Is.GreaterThan(start), "The extraction line is not in the expected shape. Backup log: " + log);

         return log.Substring(start, end - start);
      }

      [Test]
      [Description("A backup with messages extracts its message store back out, holds it to what was staged, reconciles every row, and tidies the scratch directory away")]
      public void TheMessageStoreIsExtractedAndReconciledAfterEveryBackup()
      {
         DeliverTwoMessages(_domain);

         string log = RunBackupAndWaitForSuccess();

         StringAssert.Contains("Verified restore: the message store was extracted to", log);
         StringAssert.Contains("exactly what this run wrote", log);
         StringAssert.Contains("0 of them have no file in this backup", log);

         // The rows checked include the two just delivered; the index counts every
         // message on the server, so "at least" rather than "exactly".
         StringAssert.DoesNotContain("0 message row(s) in the index were checked", log);

         string scratch = ExtractedDirectory(log);
         ClassicAssert.IsFalse(Directory.Exists(scratch),
            "The extracted copy of the message store should be removed once the check is done: " + scratch);

         // Verified BEFORE it was called a success, not after.
         Assert.That(log.IndexOf("Verified restore:", StringComparison.Ordinal),
            Is.LessThan(log.IndexOf("Backup completed successfully", StringComparison.Ordinal)));
      }

      [Test]
      [Description("A message row whose file is gone from the store is named in the backup log, and the backup still completes - the archive faithfully records the server")]
      public void AMessageRowWhoseFileIsMissingIsReportedAndTheBackupStillCompletes()
      {
         Account account = DeliverTwoMessages(_domain);

         // Take one file away underneath the row that describes it. Nothing on the
         // server notices until something tries to read it - which is exactly the
         // state a backup is supposed to tell an administrator about.
         Message message = account.IMAPFolders.get_ItemByName("INBOX").Messages[0];
         string file = message.Filename;
         ClassicAssert.IsTrue(File.Exists(file), "The message file should exist before it is removed: " + file);
         File.Delete(file);

         string log = RunBackupAndWaitForSuccess();

         StringAssert.Contains("1 of them have no file in this backup", log);
         StringAssert.Contains("Message row(s) whose file is not in this backup: " + Path.GetFileName(file), log);
         StringAssert.Contains("exactly what this run wrote", log);

         // The row without a file must not be mistaken for an archive that lost a
         // file: the archive is still held to what was staged, and passes.
         StringAssert.DoesNotContain("does not contain what was backed up", log);
      }

      [Test]
      [Description("BackupVerifyRestore=0 skips the extraction and says so, rather than leaving a missing line to be inferred")]
      public void TheVerifiedRestoreCanBeSwitchedOff()
      {
         // Captured before the restart: _domain is a proxy into the process that is
         // about to stop, and RestartServerAndReacquireCom re-acquires _application
         // and _settings, not the domain.
         string domainName = _domain.Name;

         ServerIniFile.SetSetting("BackupVerifyRestore", "0");
         RestartServerAndReacquireCom();

         try
         {
            Domain domain = _application.Domains.get_ItemByName(domainName);
            DeliverTwoMessages(domain);

            string log = RunBackupAndWaitForSuccess();

            StringAssert.Contains("Verified restore skipped: BackupVerifyRestore is 0", log);
            StringAssert.DoesNotContain("Verified restore: the message store was extracted", log);

            // The index is still read back: the switch turns off the extraction,
            // not the check that the archive can be opened at all.
            StringAssert.Contains("Verifying the archive...", log);
         }
         finally
         {
            ServerIniFile.SetSetting("BackupVerifyRestore", null);
            RestartServerAndReacquireCom();
         }
      }
   }
}
