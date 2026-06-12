// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

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

         try
         {
            watch.Start();
            account.Save();
         }
         catch (Exception)
         {
         }

         watch.Stop();
         Assert.Greater(10000, watch.ElapsedMilliseconds);

         // an error log file may have been created. if we're using MySQL,
         // the value may have been silently truncated.
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