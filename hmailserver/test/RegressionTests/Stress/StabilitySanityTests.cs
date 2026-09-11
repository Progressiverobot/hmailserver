// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.Stress
{
   [TestFixture]
   public class SanityTests : TestFixtureBase
   {
      [Test]
      public void TestDeletionOfMessageInDeletedFolder()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();
         var deletedMessageText = _settings.ServerMessages.get_ItemByName("MESSAGE_FILE_MISSING").Text;
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "SampleBody");
         var inbox = account.IMAPFolders.get_ItemByName("Inbox");

         CustomAsserts.AssertFolderMessageCount(inbox, 1);

         var messages = inbox.Messages;

         var message = messages[0];
         var dir = new DirectoryInfo(Path.GetFullPath(message.Filename));
         var parent = dir.Parent.Parent.Parent;
         parent.Delete(true);

         var timeBeforeDelete = DateTime.Now;
         messages.DeleteByDBID(message.ID);

         var executionTime = DateTime.Now - timeBeforeDelete;

         Assert.Greater(1500, executionTime.TotalMilliseconds);
      }

      [Test]
      public void TestInsertionOfTooLongString()
      {
         var watch = new Stopwatch();

         var application = SingletonProvider<TestSetup>.Instance.GetApp();
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         var sb = new StringBuilder();
         for (var i = 0; i < 1000; i++)
            sb.Append("abcdefgh");

         account.PersonFirstName = sb.ToString();

         // Refused, and refused with a reason. This used to swallow whatever came
         // back and assert only that the attempt was quick, which stopped meaning
         // anything the moment the refusal moved earlier: with the length checked
         // before the statement is built, the save never reaches the database, no
         // error log is written, and what was left was a stopwatch over an in-memory
         // string comparison - a test that could not fail and would have passed just
         // as well with the check taken out.
         //
         // What the fixture is for is that an absurd value neither hangs nor crashes
         // the server, so that is still timed; but the outcome is pinned too, because
         // the alternative the server had until 6.3.0 was to attempt the insert, fail
         // in the driver, and report nothing a caller could act on - which over REST
         // was a 500 for what is plainly the caller's mistake.
         Exception refusal = null;

         try
         {
            watch.Start();
            account.Save();
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            refusal = fatalCheck;
         }

         watch.Stop();
         Assert.Greater(10000, watch.ElapsedMilliseconds);

         Assert.IsNotNull(refusal,
            "An 8,000-character first name was accepted for a column that holds 60. It has to be refused: " +
            "silently truncating it loses data, and reporting nothing leaves a REST caller with a 500 for " +
            "their own mistake.");

         string refusalMessage = refusal?.Message ?? "";
         Assert.That(refusalMessage, Does.Contain("60"),
            "The refusal should name the limit it enforced, so a caller can fix the value. Got: " + refusalMessage);

         // Nothing should have been written to the ERROR log: this is a refused
         // request, not a server fault. AssertDeleteFile is kept because a failure
         // here must not leave a log behind to fail the next test's setup.
         CustomAsserts.AssertDeleteFile(LogHandler.GetErrorLogFileName());
      }

      [Test]
      [Description("Confirms that hMailServer behaves properly if a specific port is in use.")]
      public void TestPortInUse()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();
         application.Stop();

         var sock = new TcpConnection();
         using (var serverSocket = new TcpServer(1, 25, eConnectionSecurity.eCSNone))
         {
            serverSocket.StartListen();

            application.Start();

            // make sure it's possible to connect to the non blocked port.

            sock.IsPortOpen(110);
            sock.IsPortOpen(143);

            //let this our temp server die.
            sock.IsPortOpen(25);

            // make sure that hMailServer reported an error during start up because the ports were blocked.
            CustomAsserts.AssertReportedError("Failed to bind to local port.");
         }

         // restart hMailServer again. everything is now back to normal.
         application.Stop();

         application.Start();
         sock.IsPortOpen(25);
      }

      [Test]
      public void TestRetrievalOfDeletedMessage()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();
         var deletedMessageText = _settings.ServerMessages.get_ItemByName("MESSAGE_FILE_MISSING").Text;

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "SampleBody");

         var inbox = account.IMAPFolders.get_ItemByName("Inbox");


         CustomAsserts.AssertFolderMessageCount(inbox, 1);

         var message = inbox.Messages[0];

         File.Delete(message.Filename);

         var text = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");
         Assert.IsTrue(text.Contains(deletedMessageText.Replace("%MACRO_FILE%", message.Filename)));

         CustomAsserts.AssertReportedError("Message retrieval failed because message file");
      }

      [Test]
      public void TestRetrievalOfMessageInDeletedFolder()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();
         var deletedMessageText = _settings.ServerMessages.get_ItemByName("MESSAGE_FILE_MISSING").Text;

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "SampleBody");

         var inbox = account.IMAPFolders.get_ItemByName("Inbox");


         CustomAsserts.AssertFolderMessageCount(inbox, 1);

         var message = inbox.Messages[0];

         var dir = new DirectoryInfo(Path.GetFullPath(message.Filename));
         var parent = dir.Parent.Parent.Parent;
         parent.Delete(true);

         var text = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");
         Assert.IsTrue(text.Contains(deletedMessageText.Replace("%MACRO_FILE%", message.Filename)));
         CustomAsserts.AssertReportedError("Message retrieval failed because message file");
      }


      [Test]
      public void TestRetrievalOfMessageInDeletedFolderUsingIMAP()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();
         var deletedMessageText = _settings.ServerMessages.get_ItemByName("MESSAGE_FILE_MISSING").Text;

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "SampleBody");

         var inbox = account.IMAPFolders.get_ItemByName("Inbox");


         CustomAsserts.AssertFolderMessageCount(inbox, 1);

         var message = inbox.Messages[0];

         var dir = new DirectoryInfo(Path.GetFullPath(message.Filename));
         var parent = dir.Parent.Parent.Parent;
         parent.Delete(true);

         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.SelectFolder("INBOX");
         var result = sim.Fetch("1 BODY[1]");

         Assert.IsTrue(result.Contains(deletedMessageText.Replace("%MACRO_FILE%", message.Filename)));
         CustomAsserts.AssertReportedError("Message retrieval failed because message file");
      }
   }
}