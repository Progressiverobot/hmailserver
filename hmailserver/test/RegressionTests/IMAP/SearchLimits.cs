// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.IO;
using System.Text;
using System.Linq;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.IMAP
{
   /// <summary>
   ///    SEARCH BODY and SEARCH TEXT have no index behind them - hm_message_metadata
   ///    indexes only date, from, subject, to and cc - so they are resolved by reading
   ///    and MIME parsing every message in the mailbox. One authenticated client could
   ///    therefore occupy a worker thread for as long as its mailbox takes to read, as
   ///    often as it liked.
   ///
   ///    Two absolute ceilings now bound that, both measured from the start of the
   ///    search and neither re-arming: IMAPSearchTimeout (seconds) and
   ///    IMAPSearchMaxMegabytes (megabytes of message content examined). These tests
   ///    drive the byte ceiling because it is the deterministic one - the same mailbox
   ///    produces the same outcome on fast and slow storage, which the time ceiling by
   ///    its nature cannot.
   ///
   ///    The behaviour being pinned is as much about the answer as the bound. An
   ///    over-budget search must return a tagged NO and no untagged SEARCH data at
   ///    all. Returning the matches found so far would be indistinguishable from a
   ///    complete result - RFC 3501 has no partial-SEARCH concept - and a client that
   ///    acts on a search result in bulk (Thunderbird's Search Messages window, any
   ///    archiver whose rule is "keep what the search returned") would act on a set
   ///    quietly missing matches. A truncated result set loses mail; a failed command
   ///    does not.
   /// </summary>
   [TestFixture]
   public class SearchLimits : TestFixtureBase
   {

      private const string Needle = "SearchLimitsNeedleToken";

      /// <summary>
      ///    Sets both ceilings and reloads them. The server reads hMailServer.ini from its
      ///    bin directory (Utilities::GetBinDirectory): a registered install resolves to
      ///    {InstallLocation}\Bin, while a developer build that is not registered reads the
      ///    ini next to the running executable. Write to every existing candidate so the
      ///    file the service actually reads is updated regardless of layout.
      /// </summary>
      private void SetSearchLimits(int timeoutSeconds, int maxMegabytes)
      {
         string programDirectory = _application.Settings.Directories.ProgramDirectory;
         string[] candidates =
         {
            Paths.Combine(programDirectory, "hMailServer.ini"),
            Paths.Combine(programDirectory, "Bin", "hMailServer.ini"),
         };

         bool wroteAny = false;
         foreach (string iniPath in candidates.Where(File.Exists))
         {
            Assert.IsTrue(
               IniFile.WritePrivateProfileString("Settings", "IMAPSearchTimeout", timeoutSeconds.ToString(), iniPath),
               "Failed to write IMAPSearchTimeout to " + iniPath + ".");
            Assert.IsTrue(
               IniFile.WritePrivateProfileString("Settings", "IMAPSearchMaxMegabytes", maxMegabytes.ToString(), iniPath),
               "Failed to write IMAPSearchMaxMegabytes to " + iniPath + ".");
            wroteAny = true;
         }

         Assert.IsTrue(wroteAny, "Could not locate an existing hMailServer.ini to update.");

         // Both settings are cached in IniFileSettings at startup; Reinitialize reloads them.
         _application.Reinitialize();
      }

      /// <summary>
      ///    A body of roughly the requested size, carrying the search token. Built from
      ///    short lines so nothing in the SMTP or MIME path has to cope with one enormous
      ///    line, and from plain alphanumerics so no line can look like end-of-data.
      /// </summary>
      private static string BuildBody(int approximateBytes, int ordinal)
      {
         var body = new StringBuilder(approximateBytes + 128);
         body.Append("Message ");
         body.Append(ordinal);
         body.Append(" contains ");
         body.Append(Needle);
         body.Append("\r\n");

         while (body.Length < approximateBytes)
            body.Append("0123456789abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnopqrstuvw\r\n");

         return body.ToString();
      }

      /// <summary>
      ///    Four 700 kB messages, so a one-megabyte ceiling is passed after the second one
      ///    with two still outstanding - the bound has to have work left to stop, since it
      ///    deliberately never discards a search that has already finished. MaxSize 0 keeps
      ///    the account unlimited, and the global and domain message-size limits are cleared
      ///    because another fixture may have left a small one behind.
      /// </summary>
      private hMailServer.Account CreateAccountWithLargeMessages(string address, string subjectPrefix)
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "test", 0);

         _settings.MaxMessageSize = 0;
         _domain.MaxMessageSize = 0;
         _domain.Save();

         var smtpClientSimulator = new SmtpClientSimulator();
         for (int i = 1; i <= 4; i++)
            smtpClientSimulator.Send(account.Address, account.Address, subjectPrefix + " " + i,
               BuildBody(700 * 1024, i));

         ImapClientSimulator.AssertMessageCount(account.Address, "test", "INBOX", 4);

         return account;
      }

      [Test]
      [Description("An IMAP SEARCH BODY that would examine more message content than " +
                   "IMAPSearchMaxMegabytes allows is abandoned: the server answers a tagged NO with the " +
                   "LIMIT response code, sends no untagged SEARCH data that a client could mistake for a " +
                   "complete result, and reports it in the application log rather than the error log. The " +
                   "same search with the ceiling disabled returns the full result set unchanged.")]
      public void BodySearchPastTheByteCeilingFailsRatherThanTruncating()
      {
         int originalMaxMessageSize = _settings.MaxMessageSize;

         try
         {
            var account = CreateAccountWithLargeMessages("searchlimits@example.test", "Search limits test");

            // With the byte ceiling disabled, the search reads all four messages (about
            // 2.8 MB) and returns every one of them. This is the control: it is the
            // result the bound must not change when it is not exceeded.
            SetSearchLimits(60, 0);

            var unboundedSimulator = new ImapClientSimulator();
            Assert.IsTrue(unboundedSimulator.ConnectAndLogon(account.Address, "test"));
            Assert.IsTrue(unboundedSimulator.SelectFolder("INBOX"));

            string unbounded = unboundedSimulator.SendSingleCommand("A01 SEARCH BODY \"" + Needle + "\"");
            unboundedSimulator.Close();

            Assert.IsTrue(unbounded.Contains("* SEARCH 1 2 3 4"),
               "With the ceiling disabled the search must return all four messages. " + unbounded);
            Assert.IsTrue(unbounded.Contains("A01 OK"),
               "With the ceiling disabled the search must succeed. " + unbounded);

            // One megabyte: two 700 kB messages pass it, so the third is reached with the
            // ceiling already exceeded and two messages still to go.
            SetSearchLimits(60, 1);
            LogHandler.DeleteCurrentDefaultLog();

            var boundedSimulator = new ImapClientSimulator();
            Assert.IsTrue(boundedSimulator.ConnectAndLogon(account.Address, "test"));
            Assert.IsTrue(boundedSimulator.SelectFolder("INBOX"));

            string bounded = boundedSimulator.SendSingleCommand("A01 SEARCH BODY \"" + Needle + "\"");
            boundedSimulator.Close();

            Assert.IsTrue(bounded.Contains("A01 NO"),
               "A search past the byte ceiling must fail with a tagged NO. " + bounded);

            // The important half. Untagged data is processed by clients whatever the
            // tagged status turns out to be, so a truncated "* SEARCH 1 2" here would be
            // taken as the complete answer and acted on.
            Assert.IsFalse(bounded.Contains("* SEARCH"),
               "An abandoned search must send no untagged SEARCH data at all. " + bounded);
            Assert.IsFalse(bounded.Contains("* ESEARCH"),
               "An abandoned search must send no untagged ESEARCH data at all. " + bounded);

            Assert.IsTrue(bounded.Contains("[LIMIT]"),
               "The NO should carry the RFC 9051 LIMIT response code so a client can tell this " +
               "apart from an unparseable search. " + bounded);

            // Named account and measured cost, at application level so an administrator
            // can see it happening.
            Assert.IsTrue(LogHandler.DefaultLogContains("SEARCH abandoned for account " + account.Address),
               "The abandoned search must be reported in the application log, naming the account.");

            // And specifically not in the error log: a user searching a large mailbox is
            // not a server fault, and an entry here would fail every later fixture's
            // clean-log assertion.
            CustomAsserts.AssertNoReportedError();

            // A header search over the same mailbox is charged only for the headers it
            // reads, not for whole messages, so the one-megabyte ceiling must leave it
            // alone. Without this the "bound" would simply break search on any large
            // mailbox.
            var headerSimulator = new ImapClientSimulator();
            Assert.IsTrue(headerSimulator.ConnectAndLogon(account.Address, "test"));
            Assert.IsTrue(headerSimulator.SelectFolder("INBOX"));

            string headerSearch = headerSimulator.SendSingleCommand("A01 SEARCH SUBJECT \"Search limits test\"");
            headerSimulator.Close();

            Assert.IsTrue(headerSearch.Contains("* SEARCH 1 2 3 4"),
               "A header search reads only headers and must not be affected by the byte ceiling. " +
               headerSearch);
            Assert.IsTrue(headerSearch.Contains("A01 OK"),
               "A header search must still succeed under the byte ceiling. " + headerSearch);
         }
         finally
         {
            // Restore the shipped defaults so later fixtures see stock behaviour.
            SetSearchLimits(60, 2048);
            _settings.MaxMessageSize = originalMaxMessageSize;
         }
      }

      [Test]
      [Description("The ceiling is per-search, not per-connection: IMAPConnection reuses one SEARCH " +
                   "handler for every SEARCH on a connection, so a spent budget must be reset. A cheap " +
                   "search after an abandoned one still runs, and the expensive one fails again rather " +
                   "than succeeding on a budget something topped up.")]
      public void TheCeilingDoesNotLeakBetweenSearchesOnOneConnection()
      {
         int originalMaxMessageSize = _settings.MaxMessageSize;

         try
         {
            var account = CreateAccountWithLargeMessages("searchlimits2@example.test", "Search limits reuse");

            SetSearchLimits(60, 1);

            var simulator = new ImapClientSimulator();
            Assert.IsTrue(simulator.ConnectAndLogon(account.Address, "test"));
            Assert.IsTrue(simulator.SelectFolder("INBOX"));

            // Burn the budget once.
            string first = simulator.SendSingleCommand("A01 SEARCH BODY \"" + Needle + "\"");

            // A budget that was not reset would make every later search on this
            // connection fail too - including cheap ones.
            string second = simulator.SendSingleCommand("A02 SEARCH SUBJECT \"Search limits reuse\"");

            // And the expensive one must fail again rather than succeeding on a budget
            // the intervening search somehow topped up.
            string third = simulator.SendSingleCommand("A03 SEARCH BODY \"" + Needle + "\"");
            simulator.Close();

            Assert.IsTrue(first.Contains("A01 NO"),
               "The first expensive search must fail. " + first);
            Assert.IsTrue(second.Contains("* SEARCH 1 2 3 4"),
               "A cheap search after an abandoned one must still run. " + second);
            Assert.IsTrue(second.Contains("A02 OK"),
               "A cheap search after an abandoned one must still succeed. " + second);
            Assert.IsTrue(third.Contains("A03 NO"),
               "The expensive search must fail again on the same connection. " + third);
         }
         finally
         {
            SetSearchLimits(60, 2048);
            _settings.MaxMessageSize = originalMaxMessageSize;
         }
      }
   }
}
