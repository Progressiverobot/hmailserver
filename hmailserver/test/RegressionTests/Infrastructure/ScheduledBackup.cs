// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    Exercises scheduled (automatic) backups and backup retention
   ///    (hMailServer.ini [Settings] ScheduledBackupTime,
   ///    ScheduledBackupIntervalMinutes, ScheduledBackupKeepCount and
   ///    ScheduledBackupMaxAgeDays).
   ///
   ///    Until this feature existed the only way to get a backup was for something
   ///    outside the server to call BackupManager.StartBackup() over COM, which in
   ///    practice meant most installations had no backups at all.
   ///
   ///    Why each test fails against the server as it was before this change:
   ///
   ///      * TestScheduleStartsABackupWithNoExternalCaller - no backup task is
   ///        registered, so nothing ever creates an archive and the poll times out.
   ///      * TestRetentionKeepsTheNewestArchives and
   ///        TestRetentionDeletesArchivesOlderThanMaxAgeDays - no retention exists,
   ///        so every archive that was in the destination before the backup is still
   ///        there afterwards and the expected counts and identities are wrong.
   ///      * TestRetentionAgeRuleAlwaysLeavesAnEarlierGeneration - fails against no
   ///        retention (nothing is deleted) and equally against the obvious
   ///        implementation of "delete everything older than N days", which leaves
   ///        only the archive it has just written.
   ///      * TestFailedBackupDeletesNoArchives - a negative control for the ordering
   ///        rule that separates retention from a data-loss feature ("never delete an
   ///        old archive until the new one has completed successfully"). It fails
   ///        against pruning to make room before writing, and against pruning from
   ///        the scheduler rather than from the point in BackupExecuter that every
   ///        failure path has already returned past.
   ///      * TestNoScheduleIsRegisteredByDefault - passes before and after, and is
   ///        here on purpose: it is what catches a schedule being registered on
   ///        installations that never asked for one.
   /// </summary>
   [TestFixture]
   public class ScheduledBackup : TestFixtureBase
   {
      [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
      private static extern bool WritePrivateProfileString(string section, string key, string value, string filePath);

      // Exactly the shape BackupExecuter::StartBackup generates and
      // BackupRetention::ParseArchiveName recognises: "HMBackup YYYY-MM-DD HHMMSS.7z".
      // Matched here rather than handed to Directory.GetFiles as a wildcard because a
      // wildcard also matches 8.3 short names, and this fixture counts archives for a
      // living.
      private static readonly Regex ArchiveNamePattern =
         new Regex(@"^HMBackup [0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{6}\.7z$", RegexOptions.IgnoreCase);

      private string _backupDirectory;
      private string _previousDestination;

      // Named differently from the base class's SetUp on purpose. A [SetUp] method
      // that hides the base one by having the same name is the classic way to lose
      // PerformBasicSetup, and this fixture needs it - the backup reads the domain
      // that the base setup creates, and the base setup is also what asserts the
      // error log was clean before this fixture started.
      [SetUp]
      public void SetUpScheduledBackup()
      {
         _backupDirectory = Path.Combine(Path.GetTempPath(), "hMailScheduledBackup-" + TestSetup.UniqueString());
         Directory.CreateDirectory(_backupDirectory);

         var backup = _application.Settings.Backup;
         _previousDestination = backup.Destination;

         backup.BackupDomains = true;
         backup.BackupSettings = true;
         backup.CompressDestinationFiles = true;

         // Messages are excluded deliberately. What is under test is when a backup
         // happens and which archives survive afterwards; copying the whole message
         // store to answer that would add minutes to every test here. Whether the
         // message store round-trips is already covered by BackupRestore.
         backup.BackupMessages = false;

         backup.Destination = _backupDirectory;

         CustomAsserts.AssertDeleteFile(backup.LogFile);
      }

      [TearDown]
      public void TearDownScheduledBackup()
      {
         // The order here matters more than it looks.
         //
         // The schedule is switched off and the server reloaded *before* the
         // directory is removed. Reinitialize stops the servers, which removes the
         // maintenance work queue and waits for its threads, so once it returns no
         // backup is running and none can start - a settings-only backup finishes far
         // inside that wait. Deleting the directory first would instead let a run
         // that was already in flight fail, and a failed backup reports a critical
         // error (HM5014) that would take out the next fixture's clean-error-log
         // assertion rather than this one.
         WriteSetting("ScheduledBackupTime", null);
         WriteSetting("ScheduledBackupIntervalMinutes", "0");
         WriteSetting("ScheduledBackupKeepCount", "0");
         WriteSetting("ScheduledBackupMaxAgeDays", "0");

         _application.Reinitialize();

         _application.Settings.Backup.Destination = _previousDestination;

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

      private void WriteSetting(string key, string value)
      {
         // The server reads hMailServer.ini from its bin directory: a registered
         // install resolves to {InstallLocation}\Bin, while a developer build that is
         // not registered reads the ini next to the running executable. Write to
         // every candidate that already exists, so the file the service actually
         // reads is updated whichever layout this is, and no stray ini files are
         // created. A null value removes the key, which is what "not configured"
         // means here - and what the default-off test needs.
         string programDirectory = _application.Settings.Directories.ProgramDirectory;
         string[] candidates =
         {
            Path.Combine(programDirectory, "hMailServer.ini"),
            Path.Combine(programDirectory, "Bin", "hMailServer.ini"),
         };

         bool wroteAny = false;
         foreach (string iniPath in candidates)
         {
            if (!File.Exists(iniPath))
               continue;

            Assert.IsTrue(
               WritePrivateProfileString("Settings", key, value, iniPath),
               "Failed to write " + key + " to " + iniPath + ".");
            wroteAny = true;
         }

         Assert.IsTrue(wroteAny, "Could not locate an existing hMailServer.ini to update.");
      }

      private string CreateArchive(DateTime timestamp)
      {
         // The retention pass identifies its own archives by name and never opens
         // them, so the contents are irrelevant - but the name has to be exactly the
         // one the server generates, or (correctly) nothing will touch it.
         string name = "HMBackup " + timestamp.ToString("yyyy-MM-dd HHmmss") + ".7z";
         string path = Path.Combine(_backupDirectory, name);

         File.WriteAllText(path, "placeholder for a backup archive");

         return name;
      }

      private List<string> ArchiveNames()
      {
         var names = new List<string>();

         foreach (string file in Directory.GetFiles(_backupDirectory))
         {
            string name = Path.GetFileName(file);

            if (ArchiveNamePattern.IsMatch(name))
               names.Add(name);
         }

         names.Sort(StringComparer.OrdinalIgnoreCase);

         return names;
      }

      private static string Describe(IEnumerable<string> names)
      {
         return "[" + string.Join(", ", names) + "]";
      }

      private string ReadBackupLog()
      {
         string logFile = _application.Settings.Backup.LogFile;

         try
         {
            if (!File.Exists(logFile))
               return "(no backup log)";

            // FileShare.ReadWrite: the server holds this log open, so an exclusive
            // open would fail rather than read.
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

      // Polls the backup log without using any NUnit assertion, so that a read which
      // happens while the server is mid-write cannot record a failure against a test
      // that then goes on to succeed.
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

      private void RunBackupAndWaitForSuccess()
      {
         _application.BackupManager.StartBackup();

         Assert.IsTrue(WaitForBackupLog("Backup completed successfully", 120),
            "The backup did not complete. Backup log: " + ReadBackupLog());
      }

      [Test]
      [Description("A configured schedule produces a backup with nothing outside the server asking for one.")]
      public void TestScheduleStartsABackupWithNoExternalCaller()
      {
         // Three archives from previous days. The task works out when the last backup
         // happened from the destination rather than from memory - so that a server
         // restarted more often than the interval still backs up - and with an
         // interval of one minute a run is immediately owed.
         CreateArchive(DateTime.Now.AddDays(-3));
         CreateArchive(DateTime.Now.AddDays(-2));
         CreateArchive(DateTime.Now.AddDays(-1));

         var preExisting = ArchiveNames();
         Assert.AreEqual(3, preExisting.Count, "Test setup should have created three archives.");

         // No retention in this test. Retention is pinned by the three tests below,
         // where the backup is started explicitly and nothing else can run in
         // between; here the schedule is left free to fire more than once, and
         // assertions about which archives survive would be racing it.
         WriteSetting("ScheduledBackupIntervalMinutes", "1");
         WriteSetting("ScheduledBackupKeepCount", "0");
         WriteSetting("ScheduledBackupMaxAgeDays", "0");

         _application.Reinitialize();

         // The scheduler sleeps a minute between passes and a task registered with
         // SetMinutesBetweenRun(1) is first due a minute after registration, so the
         // run lands on the second or third pass depending on how the two line up.
         // Bounded generously: this is a real backup of a real server.
         string newArchive = null;

         for (int attempt = 0; attempt < 180 && newArchive == null; attempt++)
         {
            foreach (string name in ArchiveNames())
            {
               if (!preExisting.Contains(name))
               {
                  newArchive = name;
                  break;
               }
            }

            if (newArchive == null)
               Thread.Sleep(1000);
         }

         Assert.IsNotNull(newArchive,
            "The schedule never produced an archive in " + _backupDirectory + ". Archives present: " +
            Describe(ArchiveNames()) + ". Backup log: " + ReadBackupLog());

         // It has to have been the schedule that started it and not anything else:
         // StartScheduledBackup writes this line and the COM path does not.
         Assert.IsTrue(WaitForBackupLog("Scheduled backup started", 30),
            "The backup log does not show the backup being started by the schedule. Backup log: " + ReadBackupLog());

         Assert.IsTrue(WaitForBackupLog("Backup completed successfully", 120),
            "The scheduled backup did not complete. Backup log: " + ReadBackupLog());

         // An unattended backup that reports a problem must be visible as one. This
         // also attributes any failure to this test rather than to whichever fixture
         // happens to run next.
         CustomAsserts.AssertNoReportedError();
      }

      [Test]
      [Description("ScheduledBackupKeepCount deletes the oldest archives after a successful backup and always keeps the new one.")]
      public void TestRetentionKeepsTheNewestArchives()
      {
         string fiveDaysOld = CreateArchive(DateTime.Now.AddDays(-5));
         string fourDaysOld = CreateArchive(DateTime.Now.AddDays(-4));
         string threeDaysOld = CreateArchive(DateTime.Now.AddDays(-3));
         string twoDaysOld = CreateArchive(DateTime.Now.AddDays(-2));

         var preExisting = ArchiveNames();
         Assert.AreEqual(4, preExisting.Count, "Test setup should have created four archives.");

         // No schedule configured: retention deliberately applies to a manual backup
         // too. A policy that only bounded scheduled runs would stop bounding the
         // directory the moment an administrator took a backup by hand into the same
         // place - which is exactly when the destination is fullest.
         WriteSetting("ScheduledBackupKeepCount", "2");
         WriteSetting("ScheduledBackupMaxAgeDays", "0");
         _application.Reinitialize();

         RunBackupAndWaitForSuccess();

         var remaining = ArchiveNames();

         Assert.AreEqual(2, remaining.Count,
            "Two archives should remain (the new one and the newest existing one), but the destination holds " +
            Describe(remaining) + ".");

         // The identities, not just the count: a count alone would also be satisfied
         // by "deleted an arbitrary three".
         Assert.IsFalse(remaining.Contains(fiveDaysOld), "The 5-day-old archive should have been deleted.");
         Assert.IsFalse(remaining.Contains(fourDaysOld), "The 4-day-old archive should have been deleted.");
         Assert.IsFalse(remaining.Contains(threeDaysOld), "The 3-day-old archive should have been deleted.");
         Assert.IsTrue(remaining.Contains(twoDaysOld), "The 2-day-old archive is inside the keep count and should have survived.");

         bool newArchivePresent = false;
         foreach (string name in remaining)
         {
            if (!preExisting.Contains(name))
               newArchivePresent = true;
         }

         Assert.IsTrue(newArchivePresent,
            "Retention must never delete the archive the backup just produced, but " + Describe(remaining) +
            " holds none of the archives this backup wrote.");

         CustomAsserts.AssertNoReportedError();
      }

      [Test]
      [Description("ScheduledBackupMaxAgeDays deletes archives outside the window and keeps the ones inside it.")]
      public void TestRetentionDeletesArchivesOlderThanMaxAgeDays()
      {
         string thirtyDaysOld = CreateArchive(DateTime.Now.AddDays(-30));
         string twentyDaysOld = CreateArchive(DateTime.Now.AddDays(-20));
         string oneDayOld = CreateArchive(DateTime.Now.AddDays(-1));

         // Age only, no count limit, so this isolates the age rule.
         WriteSetting("ScheduledBackupKeepCount", "0");
         WriteSetting("ScheduledBackupMaxAgeDays", "7");
         _application.Reinitialize();

         RunBackupAndWaitForSuccess();

         var remaining = ArchiveNames();

         Assert.IsFalse(remaining.Contains(thirtyDaysOld),
            "A 30-day-old archive is outside a 7-day window and should have been deleted. Remaining: " + Describe(remaining) + ".");

         Assert.IsFalse(remaining.Contains(twentyDaysOld),
            "A 20-day-old archive is outside a 7-day window and should have been deleted. Remaining: " + Describe(remaining) + ".");

         Assert.IsTrue(remaining.Contains(oneDayOld),
            "A 1-day-old archive is inside a 7-day window and should have been kept. Remaining: " + Describe(remaining) + ".");

         Assert.AreEqual(2, remaining.Count,
            "The day-old archive and the new one should be all that is left, but the destination holds " +
            Describe(remaining) + ".");

         CustomAsserts.AssertNoReportedError();
      }

      [Test]
      [Description("The age rule never reduces the destination to the archive it has just written, however old everything else is.")]
      public void TestRetentionAgeRuleAlwaysLeavesAnEarlierGeneration()
      {
         // Both of these are outside the window, so "delete everything older than N
         // days" would leave only the archive this backup writes.
         //
         // It must not. MaxAgeDays is the one retention input driven by the wall
         // clock, and the wall clock is the input that can be wrong by years - a VM
         // rolled forward from a snapshot, a dead CMOS battery, a bad NTP step. Any
         // of those makes every archive in the destination look ancient at once, and
         // an age rule with no floor would then reduce a month of backups to the
         // single archive it happens to be writing at the time. ScheduledBackupKeepCount
         // is a count, which no clock can corrupt, and is deliberately allowed to take
         // the destination down to one archive; the age rule is not.
         string thirtyDaysOld = CreateArchive(DateTime.Now.AddDays(-30));
         string twentyDaysOld = CreateArchive(DateTime.Now.AddDays(-20));

         WriteSetting("ScheduledBackupKeepCount", "0");
         WriteSetting("ScheduledBackupMaxAgeDays", "1");
         _application.Reinitialize();

         RunBackupAndWaitForSuccess();

         var remaining = ArchiveNames();

         Assert.AreEqual(2, remaining.Count,
            "The age rule must leave one earlier generation behind as well as the archive it just wrote, but the destination holds " +
            Describe(remaining) + ".");

         Assert.IsTrue(remaining.Contains(twentyDaysOld),
            "The newest of the existing archives must survive the age rule. Remaining: " + Describe(remaining) + ".");

         Assert.IsFalse(remaining.Contains(thirtyDaysOld),
            "The older of the two existing archives is outside the window and should have been deleted. Remaining: " +
            Describe(remaining) + ".");

         CustomAsserts.AssertNoReportedError();
      }

      [Test]
      [Description("A backup that fails must not delete anything, however aggressive the retention policy is.")]
      public void TestFailedBackupDeletesNoArchives()
      {
         CreateArchive(DateTime.Now.AddDays(-30));
         CreateArchive(DateTime.Now.AddDays(-20));
         CreateArchive(DateTime.Now.AddDays(-10));

         var preExisting = ArchiveNames();
         Assert.AreEqual(3, preExisting.Count, "Test setup should have created three archives.");

         // Keep one, delete everything older than a day: without the ordering rule
         // all three of these are deletion candidates.
         WriteSetting("ScheduledBackupKeepCount", "1");
         WriteSetting("ScheduledBackupMaxAgeDays", "1");
         _application.Reinitialize();

         // Make the backup fail at the point where it writes its XML index, by putting
         // a directory where that file has to go. The destination is otherwise
         // perfectly good, which is the point: the failure has to happen *after* an
         // implementation that pruned to make room would have pruned, and it has to
         // leave the directory readable so the assertions below mean something.
         string blockingDirectory = Path.Combine(_backupDirectory, "hMailServerBackup.xml");
         Directory.CreateDirectory(blockingDirectory);

         try
         {
            _application.BackupManager.StartBackup();

            // The exact failure, not just any failure. "BACKUP ERROR:" on its own
            // would also be satisfied by the backup being refused before it started,
            // and this test would then pass without ever reaching the point where
            // retention is considered - the only thing it is here to check.
            Assert.IsTrue(WaitForBackupLog("BACKUP ERROR: Could not write to the XML file", 120),
               "The backup was expected to fail while writing its XML index. Backup log: " + ReadBackupLog());

            var remaining = ArchiveNames();

            Assert.AreEqual(3, remaining.Count,
               "A failed backup must not delete any archive, but the destination holds " + Describe(remaining) +
               " instead of " + Describe(preExisting) + ".");

            foreach (string name in preExisting)
            {
               Assert.IsTrue(remaining.Contains(name),
                  name + " was deleted by a backup that failed. Remaining: " + Describe(remaining) + ".");
            }

            // A failed backup is reported as a critical error (HM5014), which is
            // correct and is also why it has to be consumed here: PerformBasicSetup
            // in whichever fixture runs next calls AssertNoReportedError, so an error
            // left in the log fails an unrelated test. AssertReportedError deletes
            // the log.
            CustomAsserts.AssertReportedError("HM5014");
         }
         finally
         {
            if (Directory.Exists(blockingDirectory))
               Directory.Delete(blockingDirectory, true);

            // Safety net: if an assertion above threw before the error log was
            // consumed, the error this test provoked on purpose would take an
            // innocent fixture down with it and hide the real failure.
            LogHandler.DeleteErrorLog();
         }
      }

      [Test]
      [Description("With no schedule configured - the shipped default - no backup task is created at all.")]
      public void TestNoScheduleIsRegisteredByDefault()
      {
         WriteSetting("ScheduledBackupTime", null);
         WriteSetting("ScheduledBackupIntervalMinutes", "0");
         WriteSetting("ScheduledBackupKeepCount", "0");
         WriteSetting("ScheduledBackupMaxAgeDays", "0");

         LogHandler.DeleteCurrentDefaultLog();

         _application.Reinitialize();

         // BackupScheduleTask writes its schedule to the application log once per
         // service start, from its constructor, and the constructor only runs when a
         // schedule is configured. No line means no task, which means nothing on this
         // installation will start a backup on its own - the behaviour every release
         // before this one had.
         string log = LogHandler.ReadCurrentDefaultLog();

         // Guards against the assertion below passing because nothing was read at
         // all: StartServers writes to this log on every start, so an empty log means
         // this test is not looking at what it thinks it is.
         Assert.IsTrue(log.Length > 0,
            "The application log is empty, so this test could not tell whether a schedule was registered.");

         // Reinitialize is synchronous and CreateScheduledTasks_ runs inside it, so
         // the line would already be in the log by now if the task had been created.
         // Deliberately not paired with "and no archive appeared": the destination is
         // a directory this test created seconds ago and the scheduler's first tick
         // is a minute away, so such an assertion could not fail and would be worse
         // than none.
         Assert.IsFalse(log.Contains("Scheduled backup"),
            "A default installation must not register a backup schedule. Application log: " + log);

         CustomAsserts.AssertNoReportedError();
      }
   }
}
