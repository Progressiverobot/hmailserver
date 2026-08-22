// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using DiagnosticsEventLog = System.Diagnostics.EventLog;
using EventLogEntry = System.Diagnostics.EventLogEntry;
using EventLogEntryType = System.Diagnostics.EventLogEntryType;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    The Windows event log sink: Critical and High errors, mirrored into the
   ///    Application log under source "hMailServer" so that Event Viewer, monitoring
   ///    agents and SIEM collectors can see them. The event ids are a published
   ///    contract (the table lives in Server/Common/Application/WindowsEventLog.h):
   ///    what these tests pin is that an error actually arrives under its published
   ///    id and type, that ordinary traffic writes nothing at all, and that the
   ///    feature is genuinely inert when switched off.
   ///
   ///    These tests read the REAL Application log through System.Diagnostics. The
   ///    events are written by the SERVICE (LocalSystem, so its registry
   ///    self-registration and its writes always succeed); the test process only
   ///    needs read access, which a standard interactive user has on the Application
   ///    log. Where an agent account genuinely cannot read it, the tests skip with a
   ///    message instead of failing: asserting on a log we cannot open proves
   ///    nothing either way.
   ///
   ///    Assertions match on entries newer than a timestamp taken just before each
   ///    provocation (with slack for the log's one-second granularity), so entries
   ///    left by earlier runs cannot satisfy them. They deliberately assert on
   ///    ReplacementStrings rather than the rendered Message, because the raw
   ///    insertion string is what the server wrote and is identical whether or not
   ///    the source's message-file registration has happened yet.
   ///
   ///    One deliberate limit: the sink throttles each event id to five events per
   ///    ten-minute window (excess goes only to the ERROR log). A full suite run
   ///    provokes at most a couple per id, but looping THIS fixture more than a
   ///    handful of times inside ten minutes will trip the throttle by design - if
   ///    PositiveEventIsWritten starts failing on the sixth consecutive re-run, wait
   ///    out the window rather than "fixing" the throttle.
   /// </summary>
   [TestFixture]
   public class WindowsEventLog : TestFixtureBase
   {
      private const string EventSource = "hMailServer";
      private const string EnabledSetting = "WindowsEventLogEnabled";

      // The published ids used here, from the table in WindowsEventLog.h.
      private const long EventIdHighSeverityCatchAll = 2001;
      private const long EventIdBackupFailed = 2013;

      [TearDown]
      public new void TearDown()
      {
         try
         {
            var scripting = _settings.Scripting;

            // Off first, so nothing can fire an event while the file is being
            // replaced; then an empty script, which compiles cleanly and
            // deregisters every handler; then the file goes entirely.
            scripting.Enabled = false;

            var file = scripting.CurrentScriptFile;

            File.WriteAllText(file, "");
            scripting.Reload();

            if (File.Exists(file))
               File.Delete(file);
         }
         catch (Exception)
         {
            // Restoring scripting is best effort; the error-log clearing below is
            // the part that must happen, because a leftover ERROR log fails every
            // fixture that runs afterwards, in setup.
         }

         var settled = LogHandler.ClearErrorLogUntilSettled();

         Assert.IsTrue(settled,
            "Deliberate errors were still arriving after the settle timeout, so the ERROR "
            + "log could not be left clean.");
      }

      /// <summary>
      ///    Opens the Application log for reading, or skips the test when this
      ///    process genuinely cannot read it.
      /// </summary>
      private static DiagnosticsEventLog OpenApplicationLogOrSkip()
      {
         try
         {
            var log = new DiagnosticsEventLog("Application");

            // Force a real read; the constructor alone touches nothing.
            var unused = log.Entries.Count;

            return log;
         }
         catch (SecurityException ex)
         {
            Assert.Ignore("This process cannot read the Application event log, so nothing about "
                          + "the Windows event log sink can be asserted from here: " + ex.Message);
         }
         catch (UnauthorizedAccessException ex)
         {
            Assert.Ignore("This process cannot read the Application event log, so nothing about "
                          + "the Windows event log sink can be asserted from here: " + ex.Message);
         }

         return null; // Unreachable; Assert.Ignore throws.
      }

      /// <summary>
      ///    The hMailServer-source entries written since <paramref name="since" />,
      ///    newest first. Scans backwards from the end of the log and stops at the
      ///    first entry older than the mark, so the size of the machine's
      ///    Application log does not matter.
      /// </summary>
      private static List<EventLogEntry> EntriesSince(DiagnosticsEventLog log, DateTime since)
      {
         var result = new List<EventLogEntry>();

         var entries = log.Entries;
         var count = entries.Count;

         for (var i = count - 1; i >= 0; i--)
         {
            var entry = entries[i];

            if (entry.TimeWritten < since)
               break;

            if (string.Equals(entry.Source, EventSource, StringComparison.OrdinalIgnoreCase))
               result.Add(entry);
         }

         return result;
      }

      /// <summary>
      ///    The text the server put into the event: the single insertion string
      ///    when present, the rendered message otherwise. Identical with or
      ///    without message-file registration.
      /// </summary>
      private static string EntryText(EventLogEntry entry)
      {
         var strings = entry.ReplacementStrings;

         if (strings != null && strings.Length > 0)
            return strings[0];

         return entry.Message ?? "";
      }

      /// <summary>
      ///    A timestamp to compare TimeWritten against. Two seconds of slack,
      ///    because the event log's timestamps have one-second granularity and
      ///    truncate: an event written at .900 of the same second this method runs
      ///    in would otherwise compare as older than the mark.
      /// </summary>
      private static DateTime MarkTime()
      {
         return DateTime.Now.AddSeconds(-2);
      }

      /// <summary>
      ///    Reports HM5710 (severity High): a script that cannot compile is handed
      ///    to a reload, which reports the compile failure and keeps the previous
      ///    scripts in force. Deterministic, immediate, and breaks nothing - the
      ///    same provocation ErrorManagerDiagnosticsTests uses.
      /// </summary>
      private void ProvokeHighSeverityError()
      {
         var scripting = _settings.Scripting;

         scripting.Language = "VBScript";

         File.WriteAllText(scripting.CurrentScriptFile, "Sub ThisWillNotCompile(\r\n");

         scripting.Enabled = true;
         scripting.Reload();
      }

      [Test]
      [Description("A High-severity error reaches the Application log under the published catch-all id, typed Error, carrying its HM code.")]
      public void PositiveEventIsWritten()
      {
         var log = OpenApplicationLogOrSkip();

         var since = MarkTime();

         ProvokeHighSeverityError();

         EventLogEntry match = null;

         RetryHelper.TryAction(TimeSpan.FromSeconds(20), () =>
         {
            match = EntriesSince(log, since)
               .Find(e => e.InstanceId == EventIdHighSeverityCatchAll && EntryText(e).Contains("HM5710"));

            if (match == null)
               throw new Exception(
                  "No Application-log event with id " + EventIdHighSeverityCatchAll +
                  " carrying HM5710 arrived from source " + EventSource + ".");
         });

         ClassicAssert.AreEqual(EventLogEntryType.Error, match.EntryType,
            "High severity must be written as an Error-type event.");

         var text = EntryText(match);

         StringAssert.Contains("Severity: High", text);
         StringAssert.Contains("ScriptServer::LoadScripts", text);

         // The provoked error was deliberate; consume it so it cannot fail a later
         // fixture's setup. (This also proves the ERROR log copy existed - the
         // event is a mirror, never a replacement.)
         CustomAsserts.AssertReportedError("HM5710");
      }

      [Test]
      [Description("A curated condition - a failed backup, HM5014 - gets its own published id rather than the catch-all.")]
      public void CuratedConditionGetsItsOwnEventId()
      {
         var log = OpenApplicationLogOrSkip();

         var backup = _settings.Backup;
         var previousDestination = backup.Destination;

         var since = MarkTime();

         try
         {
            // A destination that does not exist. The backup fails on its
            // accessibility check and reports HM5014, severity Critical.
            backup.Destination = Path.Combine(Path.GetTempPath(), "hm-eventlog-no-such-dir-" + Guid.NewGuid().ToString("N"));

            _application.BackupManager.StartBackup();

            EventLogEntry match = null;

            RetryHelper.TryAction(TimeSpan.FromSeconds(30), () =>
            {
               match = EntriesSince(log, since)
                  .Find(e => e.InstanceId == EventIdBackupFailed && EntryText(e).Contains("HM5014"));

               if (match == null)
                  throw new Exception(
                     "No Application-log event with the backup-failed id " + EventIdBackupFailed +
                     " arrived from source " + EventSource + ".");
            });

            ClassicAssert.AreEqual(EventLogEntryType.Error, match.EntryType,
               "A Critical error must be written as an Error-type event.");

            StringAssert.Contains("Severity: Critical", EntryText(match));
         }
         finally
         {
            backup.Destination = previousDestination;
         }

         CustomAsserts.AssertReportedError("HM5014");
      }

      [Test]
      [Description("Negative control: ordinary mail traffic writes nothing to the Windows event log.")]
      public void OrdinaryTrafficWritesNoEvents()
      {
         var log = OpenApplicationLogOrSkip();

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "eventlog-quiet@example.test", "test");

         var since = MarkTime();

         // A full ordinary round trip: SMTP delivery in, POP3 collection out. The
         // POP3 assertion is also the completion gate - by the time the message is
         // collectable, every part of the delivery that could have reported an
         // error has run, so the sweep below is not racing the traffic it sweeps
         // for. (The sink writes synchronously inside error reporting; there is no
         // deferred writer to wait out.)
         SmtpClientSimulator.StaticSend("sender@example.com", account.Address, "ordinary chatter", "nothing here warrants an operator's attention");
         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         var written = EntriesSince(log, since);

         ClassicAssert.AreEqual(0, written.Count,
            "Ordinary traffic must write nothing to the Windows event log, but these arrived: "
            + string.Join(" | ", written.ConvertAll(e => "id " + e.InstanceId + ": " + EntryText(e))));
      }

      [Test]
      [Description("WindowsEventLogEnabled=0 makes the sink inert: the same provocation reaches the ERROR log and no event is written.")]
      public void InertWhenSwitchedOff()
      {
         var log = OpenApplicationLogOrSkip();

         ServerIniFile.SetSetting(EnabledSetting, "0");
         _application.Reinitialize();

         try
         {
            var since = MarkTime();

            ProvokeHighSeverityError();

            // The positive control that bounds the negative one: the error
            // demonstrably fired and reached the ERROR log (this call also
            // consumes it). Only then is the event log's silence evidence of the
            // setting working, rather than of a provocation that never happened.
            CustomAsserts.AssertReportedError("HM5710");

            var written = EntriesSince(log, since)
               .FindAll(e => EntryText(e).Contains("HM5710"));

            ClassicAssert.AreEqual(0, written.Count,
               "The sink is switched off, but the provoked error still reached the Windows event log.");
         }
         finally
         {
            ServerIniFile.SetSetting(EnabledSetting, null);
            _application.Reinitialize();
         }
      }
   }
}
