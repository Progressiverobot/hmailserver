// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;   // StringAssert moved here in NUnit 4
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    Covers the two logging settings that the COM API and the Control Panel
   ///    have always offered and that nothing implemented:
   ///    eLogOutputFormat.hLogFormatCSA (NCSA Common Log Format) and
   ///    eLogDevice.hLogDeviceSQL (log entries written to the database).
   ///
   ///    Both were stored in hm_settings and read by nobody, so selecting either
   ///    was accepted without complaint and changed nothing - and in the SQL case
   ///    an administrator believed their logging had moved to the database while
   ///    it had in fact stayed in the files they had stopped looking at.
   /// </summary>
   [TestFixture]
   public class LogDeviceAndFormat : TestFixtureBase
   {
      // The line the SQL log device writes to the *file* log after a batch of
      // entries has been inserted. It is only ever emitted once a real INSERT has
      // succeeded - not when the CREATE TABLE ran, which proves nothing because
      // those statements carry the DAL's [IGNORE-ERRORS] marker - which is what
      // makes it usable as evidence that rows are landing in the table.
      private const string SqlDeviceReadyLine = "SQL log device: writing log entries to table hm_log.";

      // The summary the device writes when it is switched off, carrying the number
      // of entries that reached the table during the spell that just ended.
      private const string SqlDeviceStoppedPrefix = "SQL log device: stopped.";

      // A CLF date field: [dd/Mmm/yyyy:HH:MM:SS +hhmm].
      private const string ClfDate = @"\[\d{2}/[A-Za-z]{3}/\d{4}:\d{2}:\d{2}:\d{2} [+-]\d{4}\]";

      [TearDown]
      public void RestoreLoggingSettings()
      {
         // Every other fixture in the suite reads the log *file*, so leaving either
         // of these set would fail unrelated tests in ways that are very hard to
         // attribute. Restoring here as well as in each test's finally block means
         // an assertion failure mid-test cannot leak the setting either.
         var logging = _application.Settings.Logging;

         if (logging.Device == eLogDevice.hLogDeviceSQL)
            logging.Device = eLogDevice.hLogDeviceFile;

         if (logging.LogFormat != eLogOutputFormat.hLogFormatDefault)
            logging.LogFormat = eLogOutputFormat.hLogFormatDefault;

         // TestSetup.PerformBasicSetup - which runs in every test's SetUp, in every
         // fixture - fails outright if an error log exists, so a report left behind
         // here would fail the *next* test rather than this one and bury the real
         // cause. It is consumed and printed instead, and if this test had passed
         // then an unexpected report is itself a failure: the SQL log device is
         // meant to reach the database on a working installation, and HM5890 says
         // it did not.
         if (File.Exists(LogHandler.GetErrorLogFileName()))
         {
            var errors = LogHandler.ReadAndDeleteErrorLog();

            Console.WriteLine("hMailServer error log:");
            Console.WriteLine(errors);

            if (TestContext.CurrentContext.Result.FailCount == 0)
               Assert.Fail("The server reported an error while this test ran:" + Environment.NewLine + errors);
         }
      }

      /// <summary>
      ///    Selecting hLogFormatCSA must produce NCSA Common Log Format lines:
      ///
      ///       host ident authuser [date] "request" status bytes
      ///
      ///    Fails against the unfixed code because nothing read the logformat
      ///    setting: the log stayed in hMailServer's tab-separated
      ///    "SMTPD"\t&lt;thread&gt;\t... shape, so the pattern below never matches
      ///    and the final assertion (that the tab-separated form is gone) also
      ///    fails.
      /// </summary>
      [Test]
      [Description("hLogFormatCSA emits NCSA Common Log Format lines instead of hMailServer's tab-separated format.")]
      public void TestNcsaLogFormatIsEmitted()
      {
         var logging = _application.Settings.Logging;

         try
         {
            logging.LogFormat = eLogOutputFormat.hLogFormatCSA;

            // Deleted *after* the format change: TestFixtureBase.SetUp calls
            // TestSetup.GetLocalIpAddress(), which connects to port 25, so the file
            // already holds genuine default-format lines that would make the
            // negative assertion below meaningless.
            LogHandler.DeleteCurrentDefaultLog();

            // The banner is the one conversation line whose content is fixed
            // ("SENT: 220 <host> ESMTP"), so it can be matched exactly - including
            // the reply code, which proves the status column is really populated
            // from the protocol text and is not a hard-coded hyphen.
            var bannerPattern = new Regex(
               @"^(?<host>\S+) - (?<user>session-\d+) " + ClfDate + @" ""SMTPD SENT: 220 [^""\r\n]*"" 220 -\r?$",
               RegexOptions.Multiline);

            Match match = null;
            var log = string.Empty;

            for (var attempt = 0; attempt < 20; attempt++)
            {
               new SmtpClientSimulator().GetWelcomeMessage();

               log = LogHandler.ReadCurrentDefaultLog();
               match = bannerPattern.Match(log);

               if (match.Success)
                  break;

               Thread.Sleep(250);
            }

            Assert.IsTrue(match.Success,
               "No NCSA Common Log Format line was written for the SMTP banner. Log was:" + Environment.NewLine + log);

            Assert.AreEqual("127.0.0.1", match.Groups["host"].Value,
               "The NCSA host field should carry the remote address.");

            // A conversation entry has to be attributable to its session, which is
            // what the authuser column carries; see NcsaLogFormatter.h.
            StringAssert.StartsWith("session-", match.Groups["user"].Value);

            Assert.IsFalse(log.Contains("\"SMTPD\"\t"),
               "hMailServer's tab-separated format was still used while the NCSA format was selected. Log was:" +
               Environment.NewLine + log);
         }
         finally
         {
            logging.LogFormat = eLogOutputFormat.hLogFormatDefault;
         }
      }

      /// <summary>
      ///    A quoted CLF field must survive content that came off the socket. A
      ///    HELO argument containing a double quote must appear escaped, and a
      ///    multi-line reply - whose text really does contain CR and LF - must
      ///    stay on one line and still report its reply code.
      ///
      ///    Fails against the unfixed code for the same reason as the test above:
      ///    no NCSA line is produced at all. It is a separate test because it is
      ///    the assertion that would catch a formatter that emitted the right
      ///    columns for well-behaved input and let a remote peer break the column
      ///    layout - the one failure of a log format that matters for security,
      ///    because it lets a client forge fields in somebody else's analyser.
      /// </summary>
      [Test]
      [Description("The NCSA request field escapes quotes and line breaks so remote input cannot shift the columns.")]
      public void TestNcsaLogFormatEscapesRemoteInput()
      {
         var logging = _application.Settings.Logging;

         try
         {
            logging.LogFormat = eLogOutputFormat.hLogFormatCSA;

            LogHandler.DeleteCurrentDefaultLog();

            var client = new SmtpClientSimulator();
            client.Connect();
            client.Receive();

            // EHLO is answered with one string containing embedded CRLFs, so the
            // single log entry it produces is the one that exercises control
            // characters inside a quoted field.
            client.SendAndReceive("EHLO test\r\n");

            // Not a valid HELO argument; the point is only that the command is
            // logged verbatim as it was received.
            client.SendAndReceive("HELO a\"b\r\n");

            client.Disconnect();

            // \r\n as the two-character escape rather than a real line break, and
            // a request field with no raw line break in it at all.
            var continuedReply = new Regex(
               @"^\S+ - session-\d+ " + ClfDate + @" ""SMTPD SENT: 250-[^""\r\n]*\\r\\n250-[^""\r\n]*"" 250 -\r?$",
               RegexOptions.Multiline);

            var quotedHelo = new Regex(
               @"^\S+ - session-\d+ " + ClfDate + @" ""SMTPD RECEIVED: HELO a\\""b"" - -\r?$",
               RegexOptions.Multiline);

            var log = string.Empty;
            var found = false;

            for (var attempt = 0; attempt < 40; attempt++)
            {
               log = LogHandler.ReadCurrentDefaultLog();

               found = continuedReply.IsMatch(log) && quotedHelo.IsMatch(log);

               if (found)
                  break;

               Thread.Sleep(250);
            }

            Assert.IsTrue(continuedReply.IsMatch(log),
               "The multi-line EHLO reply did not produce one NCSA line carrying status 250 with escaped line breaks. Log was:" +
               Environment.NewLine + log);

            Assert.IsTrue(quotedHelo.IsMatch(log),
               "A double quote sent by the client was not escaped inside the NCSA request field. Log was:" +
               Environment.NewLine + log);
         }
         finally
         {
            logging.LogFormat = eLogOutputFormat.hLogFormatDefault;
         }
      }

      /// <summary>
      ///    With the default format selected the log must keep the exact
      ///    tab-separated shape every existing tool and test expects. This is a
      ///    negative control rather than a proof of the new behaviour: it passes
      ///    against the unfixed code, and its job is to fail if routing every Log*
      ///    function through one renderer changed the default output by so much as
      ///    a tab. That refactor is the main regression risk in this change.
      /// </summary>
      [Test]
      [Description("The default log format is unchanged after the format-selection refactor.")]
      public void TestDefaultLogFormatIsUnchanged()
      {
         Assert.AreEqual(eLogOutputFormat.hLogFormatDefault, _application.Settings.Logging.LogFormat,
            "This test only means anything with the default format selected.");

         LogHandler.DeleteCurrentDefaultLog();

         // "SMTPD"<tab>thread<tab>session<tab>"time"<tab>"ip"<tab>"SENT: 220 ..."
         var conversationPattern = new Regex(
            "^\"SMTPD\"\t\\d+\t\\d+\t\"[^\"]+\"\t\"[^\"]+\"\t\"SENT: 220 [^\"]*\"\r?$",
            RegexOptions.Multiline);

         // An entry that belongs to no session has always had one column fewer,
         // and nothing in the refactor may give it a session column.
         var noSessionPattern = new Regex(
            "^\"(APPLICATION|DEBUG|TCPIP)\"\t\\d+\t\"[^\"]+\"\t\"[^\"]*\"\r?$",
            RegexOptions.Multiline);

         var log = string.Empty;
         var matched = false;

         for (var attempt = 0; attempt < 20; attempt++)
         {
            new SmtpClientSimulator().GetWelcomeMessage();

            log = LogHandler.ReadCurrentDefaultLog();
            matched = conversationPattern.IsMatch(log) && noSessionPattern.IsMatch(log);

            if (matched)
               break;

            Thread.Sleep(250);
         }

         Assert.IsTrue(conversationPattern.IsMatch(log),
            "The default format of a conversation entry is no longer the shape it has always had. Log was:" +
            Environment.NewLine + log);

         Assert.IsTrue(noSessionPattern.IsMatch(log),
            "The default format of an entry with no session is no longer the shape it has always had. Log was:" +
            Environment.NewLine + log);
      }

      /// <summary>
      ///    Selecting hLogDeviceSQL must actually write log entries to the
      ///    database, must stop writing them to the log file while it is selected,
      ///    and must hand logging back to the file device when it is deselected -
      ///    including when it is deselected by storing hLogDeviceUnknown, which is
      ///    the value every CreateTables script inserts and therefore the value
      ///    every installation that has never touched the setting still has.
      ///
      ///    Fails against the unfixed code on the first assertion: nothing read the
      ///    logdevice setting, so no table was created, no row was inserted, and no
      ///    readiness line was ever written. The second assertion is the reason the
      ///    gap mattered - before this change the entries carried on going to the
      ///    file, which at least meant they existed; the danger was an
      ///    administrator who believed they were querying them in SQL.
      /// </summary>
      [Test]
      [Description("hLogDeviceSQL creates hm_log, inserts log entries into it, takes them out of the log file, and gives them back when deselected.")]
      public void TestSqlLogDeviceWritesToTheDatabase()
      {
         var logging = _application.Settings.Logging;
         var originalDevice = logging.Device;

         try
         {
            LogHandler.DeleteCurrentDefaultLog();

            logging.Device = eLogDevice.hLogDeviceSQL;

            // The flush thread wakes once a second: on its first pass it runs the
            // create statements and starts accepting entries, and on a later pass
            // it inserts the first batch and reports having done so. Traffic on
            // each poll guarantees there is something for it to insert.
            var becameReady = false;
            var log = string.Empty;

            for (var attempt = 0; attempt < 40 && !becameReady; attempt++)
            {
               new SmtpClientSimulator().GetWelcomeMessage();
               Thread.Sleep(500);

               log = LogHandler.ReadCurrentDefaultLog();
               becameReady = log.Contains(SqlDeviceReadyLine);
            }

            Assert.IsTrue(becameReady,
               "The SQL log device never reported inserting a batch into hm_log. Log was:" + Environment.NewLine + log);

            // From here on the conversation entries belong to the database, not the
            // file. The file is emptied first so that the lines logged before the
            // device became ready - which correctly went to the file - cannot be
            // mistaken for a failure to route.
            LogHandler.DeleteCurrentDefaultLog();

            new SmtpClientSimulator().GetWelcomeMessage();
            Thread.Sleep(2500);

            var afterReady = LogHandler.ReadCurrentDefaultLog();

            Assert.IsFalse(afterReady.Contains("SMTPD"),
               "SMTP conversation entries were still written to the log file while the SQL log device was selected. Log was:" +
               Environment.NewLine + afterReady);

            // Switching the device off makes it flush and then report how many
            // entries reached the table. A non-zero count is the only assertion
            // available here that distinguishes "rows were inserted" from "the
            // device merely started", because the regression tests have no database
            // access of their own.
            //
            // Unknown rather than File on purpose: it is the stored default, so
            // this is also the assertion that an untouched installation logs to
            // file rather than to a device it never chose.
            LogHandler.DeleteCurrentDefaultLog();

            logging.Device = eLogDevice.hLogDeviceUnknown;

            var summaryPattern = new Regex(
               @"SQL log device: stopped\. (?<written>\d+) entries written to table hm_log, (?<tofile>\d+) written to file instead\.");

            Match summary = null;

            for (var attempt = 0; attempt < 40; attempt++)
            {
               log = LogHandler.ReadCurrentDefaultLog();
               summary = summaryPattern.Match(log);

               if (summary.Success)
                  break;

               Thread.Sleep(250);
            }

            Assert.IsTrue(summary != null && summary.Success,
               "The SQL log device did not report a summary when it was switched off. Expected a line starting \"" +
               SqlDeviceStoppedPrefix + "\". Log was:" + Environment.NewLine + log);

            var written = long.Parse(summary.Groups["written"].Value);

            Assert.IsTrue(written > 0,
               "The SQL log device reported that no entries at all reached hm_log: " + summary.Value);

            // And the file device has it back.
            LogHandler.DeleteCurrentDefaultLog();

            new SmtpClientSimulator().GetWelcomeMessage();

            Assert.IsTrue(LogHandler.DefaultLogContains("\"SMTPD\"\t"),
               "Conversation entries did not return to the log file after the SQL log device was deselected. Log was:" +
               Environment.NewLine + LogHandler.ReadCurrentDefaultLog());
         }
         finally
         {
            // Two steps on purpose: off first, so the device flushes and stops,
            // then whatever the setting was before this test.
            if (logging.Device == eLogDevice.hLogDeviceSQL)
               logging.Device = eLogDevice.hLogDeviceFile;

            logging.Device = originalDevice;
         }
      }
   }
}
