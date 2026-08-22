// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    Pruning the message archive.
   ///
   ///    ArchiveDir is a raw filesystem copy of every message that passes through the
   ///    server, and nothing has ever removed anything from it. On a busy server that
   ///    is a directory that only grows, and the administrator who finds out is the
   ///    one whose disk filled up.
   ///
   ///    Two things are worth pinning, and only one of them is "old files go". The
   ///    other is what the sweep leaves alone: the archive is somebody's directory,
   ///    it is frequently kept for a legal reason, and a retention task that took a
   ///    file it was not asked to take would be destroying evidence. So the fixture
   ///    plants a recent message and a file that is not a message alongside the old
   ///    one, and both have to survive.
   /// </summary>
   [TestFixture]
   public class ArchiveRetentionTests : TestFixtureBase
   {
      private string archiveRoot_;

      [SetUp]
      public new void SetUp()
      {
         archiveRoot_ = Path.Combine(Path.GetTempPath(), "hmail-archive-retention-" + Guid.NewGuid().ToString("N"));
         Directory.CreateDirectory(archiveRoot_);
      }

      [TearDown]
      public void RemoveTheArchive()
      {
         ServerIniFile.SetSetting("ArchiveDir", null);
         ServerIniFile.SetSetting("ArchiveRetentionDays", null);

         try
         {
            if (archiveRoot_ != null && Directory.Exists(archiveRoot_))
               Directory.Delete(archiveRoot_, true);
         }
         catch
         {
            // A leftover temp directory is not worth failing a test over.
         }
      }

      private string Plant(string relativePath, int daysOld)
      {
         string full = Path.Combine(archiveRoot_, relativePath);
         Directory.CreateDirectory(Path.GetDirectoryName(full));
         File.WriteAllText(full, "From: someone@example.test\r\n\r\nArchived.\r\n");
         File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddDays(-daysOld));
         return full;
      }

      [Test]
      [Description("An archived message past the retention window is removed; a recent one and a file that is not a message are left where they are")]
      public void OnlyExpiredMessagesAreRemoved()
      {
         string expired = Plant(@"example.test\alice\Sent-old.eml", 30);
         string recent = Plant(@"example.test\alice\Sent-new.eml", 1);
         string inbound = Plant(@"Inbound\very-old.eml", 400);

         // Not a message. The archive belongs to whoever set it up and may hold an
         // index, a README or a checksum file; none of that is this task's business.
         string notAMessage = Plant(@"example.test\alice\notes.txt", 400);

         ServerIniFile.SetSetting("ArchiveDir", archiveRoot_);
         ServerIniFile.SetSetting("ArchiveRetentionDays", "7");

         // The sweep runs once promptly at startup, so a restart is the trigger.
         RestartServerAndReacquireCom();

         // It is a scheduled task rather than something the restart waits for, so
         // give it a moment - and poll rather than sleeping a fixed time, because a
         // fixed sleep is either flaky or slow and usually both.
         for (int attempt = 0; attempt < 30 && File.Exists(expired); attempt++)
            Thread.Sleep(500);

         ClassicAssert.IsFalse(File.Exists(expired),
            "A message archived 30 days ago is past a 7-day window and should have been removed: " + expired);
         ClassicAssert.IsFalse(File.Exists(inbound),
            "The Inbound folder is swept too, not just the per-user ones: " + inbound);

         ClassicAssert.IsTrue(File.Exists(recent),
            "A message archived yesterday is inside the window and must be kept: " + recent);
         ClassicAssert.IsTrue(File.Exists(notAMessage),
            "The sweep must only ever remove .eml files. Anything else in the archive belongs to " +
            "whoever put it there: " + notAMessage);
      }

      [Test]
      [Description("With ArchiveRetentionDays at its default of 0 nothing is removed however old it is - an archive is usually kept on purpose")]
      public void RetentionIsOffByDefault()
      {
         string ancient = Plant(@"Inbound\ancient.eml", 5000);

         ServerIniFile.SetSetting("ArchiveDir", archiveRoot_);
         ServerIniFile.SetSetting("ArchiveRetentionDays", "0");

         RestartServerAndReacquireCom();

         // Long enough that the sweep would have run had it been going to.
         Thread.Sleep(3000);

         ClassicAssert.IsTrue(File.Exists(ancient),
            "With retention disabled, a message archived thirteen years ago must still be there. " +
            "An archive is normally kept for a reason, and an upgrade that started deleting from one " +
            "would be destroying exactly what it was told to keep: " + ancient);
      }
   }
}
