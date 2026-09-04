// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   /// Runs the error handling that three sweeps in August 2026 added, by making one
   /// named database statement fail.
   ///
   /// Those sweeps fixed 51 places where the result of a database operation was
   /// discarded. Every defect they found lived in error handling that had never once
   /// executed — a `return true` on a failed delete, a String passed through a variadic
   /// %s in the very line that reports a failed restore, a UID counter read back after
   /// an unchecked increment. The fixes then added 51 more branches which, without
   /// something like this, would also never execute. Untested error handling is exactly
   /// the category that was just found to be full of defects, so shipping 51 more
   /// unexercised branches would have repeated the mistake at one remove.
   ///
   /// `[Settings] SimulateDatabaseFailureFor` names a substring of a statement. Only
   /// statements containing it fail, which is what makes this usable at all: delivery
   /// threads, scheduled tasks and other sessions keep working normally while one write
   /// is broken. It is empty out of the box, cannot be set over COM, and the server
   /// reports HM6119 for as long as it is set.
   ///
   /// The tests below are the four most serious failures of the fifty-one, not all of
   /// them. What matters is that each one now runs the branch rather than reasoning
   /// about it.
   /// </summary>
   [TestFixture]
   public class DatabaseFailureHandling : TestFixtureBase
   {
      [TearDown]
      public void StopSimulatingTheFailure()
      {
         try
         {
            IniFileSetting.Write("SimulateDatabaseFailureFor", "");
            _application.Reinitialize();
         }
         finally
         {
            // These tests exist to provoke error records, so they are cleared here
            // rather than left to fail whichever fixture runs next. HM6119 is in there
            // too, from the injector announcing itself.
            LogHandler.DeleteErrorLog();
         }
      }

      /// <summary>
      /// The setting is cached by IniFileSettings::InitInstance; Reinitialize re-reads
      /// it, and Stop()/Start() does not.
      /// </summary>
      private void FailStatementsContaining(string fragment)
      {
         IniFileSetting.Write("SimulateDatabaseFailureFor", fragment);
         _application.Reinitialize();
      }

      [Test]
      [Description("A message is refused rather than stored with a duplicate UID when the folder's UID " +
                   "counter cannot be incremented. GetUniqueMessageID read the counter back after an " +
                   "unchecked increment, so a failed UPDATE returned the previous message's UID.")]
      public void AMessageIsRefusedWhenItsUidCannotBeAllocated()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "uidfail@example.test", "test");

         // One delivered message first, so the folder exists and the counter has a
         // value to hand out twice.
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "First", "BodyOne");
         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         var firstUid = account.IMAPFolders.get_ItemByName("INBOX").Messages[0].UID;

         // The increment specifically. The bare column name also appears in the folder
         // SELECT and in every folder save, so failing that would break reading the
         // mailbox and the test would pass without ever reaching the branch it is for.
         FailStatementsContaining("foldercurrentuid + 1");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Second", "BodyTwo");

         // The second message must not arrive carrying the first one's UID. An IMAP
         // client that already has UID N will not fetch a different message with UID N,
         // so the delivered-but-invisible outcome is the one being ruled out here.
         CustomAsserts.AssertReportedError("HM5205");

         var folder = account.IMAPFolders.get_ItemByName("INBOX");

         ClassicAssert.AreEqual(1, folder.Messages.Count,
            "The message must be refused rather than stored, because storing it would duplicate a UID.");

         ClassicAssert.AreEqual(firstUid, folder.Messages[0].UID,
            "The message that was already there must be untouched.");
      }

      [Test]
      [Description("Auto-ban: when a failed logon cannot be recorded the server says so (HM6114). The " +
                   "count never rises, so the threshold is never crossed and no ban is ever created - " +
                   "brute-force protection that is enabled, configured and doing nothing.")]
      public void AFailedLogonThatCannotBeRecordedIsReported()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "banme@example.test", "test");

         // TestSetup switches auto-ban off before every test, and RegisterFailedLogin
         // returns immediately when it is off - so without this the failure is never
         // recorded in the first place and the test would pass having exercised nothing.
         _settings.AutoBanOnLogonFailure = true;

         FailStatementsContaining("hm_logon_failures");

         var pop3 = new Pop3ClientSimulator();
         ClassicAssert.IsFalse(pop3.ConnectAndLogon("banme@example.test", "the-wrong-password"),
            "The logon must still be refused.");

         CustomAsserts.AssertReportedError("HM6114");
      }

      [Test]
      [Description("IMAP SUBSCRIBE answers NO when the subscription cannot be saved. It used to answer " +
                   "OK, and a client that is told OK does not ask again.")]
      public void SubscribeAnswersNoWhenItCannotBeSaved()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "subfail@example.test", "test");

         // Created over COM rather than over IMAP, because injecting the failure goes
         // through Reinitialize, and Reinitialize stops and restarts the servers - which
         // drops every open connection. An IMAP session opened before the injection is
         // dead by the time the SUBSCRIBE is sent, and the test fails on a closed socket
         // having proved nothing.
         var folder = account.IMAPFolders.Add("SubscribeFail");
         folder.Save();

         // The assignment, not the column name, and no spaces around the "=".
         //
         // IMAPFolders::Refresh selects an explicit column list - "folderid,
         // folderparentid, foldername, folderissubscribed, ..." - so the bare name breaks
         // the folder *load*, and SUBSCRIBE is then refused with "Folder could not be
         // found": a different refusal that would have let this test pass without ever
         // reaching the branch it exists for. SQLStatement generates an update as
         // "name=@name_1" with no spaces, so this fragment appears only in the write.
         FailStatementsContaining("folderissubscribed=");

         var simulator = new ImapClientSimulator();
         ClassicAssert.IsTrue(simulator.ConnectAndLogon(account.Address, "test"));

         var result = simulator.Send("A10 SUBSCRIBE SubscribeFail");

         // The specific wording, not just "NO": failing every hm_imapfolders statement
         // could also make the folder lookup fail, which would refuse the command for a
         // different reason and let this pass without exercising the branch it is for.
         StringAssert.Contains("The subscription could not be saved", result,
            "SUBSCRIBE must be refused because the save failed. Got: " + result);

         simulator.Disconnect();
      }

      [Test]
      [Description("Restoring the default TCP/IP ports reports failure rather than claiming success. The " +
                   "existing ports are deleted first, so a silent failure leaves the server listening on " +
                   "fewer ports than it started with - possibly none.")]
      public void RestoringDefaultPortsReportsFailureRatherThanClaimingSuccess()
      {
         FailStatementsContaining("hm_tcpipports");

         // The COM call now answers with an error instead of S_OK. Caught by hand rather
         // than with a typed Assert.Throws, because which exception the interop layer
         // surfaces for a failed HRESULT is not what is being tested - that the call is
         // not silent is.
         //
         // Failing every hm_tcpipports statement also fails the deletes, so the ports
         // survive this test; and PerformBasicSetup calls SetDefault before every test
         // in any case, so a half-cleared port list could not outlive the fixture.
         bool reportedFailure = false;

         try
         {
            _settings.TCPIPPorts.SetDefault();
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            reportedFailure = true;
         }

         ClassicAssert.IsTrue(reportedFailure,
            "SetDefault must report that it could not restore the ports, rather than answering S_OK for ports it deleted and did not put back.");

         CustomAsserts.AssertReportedError("HM6079");
      }
   }
}
