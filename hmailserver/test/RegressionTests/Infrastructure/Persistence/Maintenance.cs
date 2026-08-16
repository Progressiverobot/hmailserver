// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System.Runtime.InteropServices;
using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure.Persistence
{
   [TestFixture]
   public class Maintenance : TestFixtureBase
   {
      [Test]
      public void TestUnknownOperation()
      {
         var ex = Assert.Throws<COMException>(() =>
            _application.Utilities.PerformMaintenance((eMaintenanceOperation)234));
         StringAssert.Contains("Unknown maintenance operation.", ex.Message);
      }

      [Test]
      public void TestUpdateIMAPFolderUID()
      {
         // Set up a basic environment which we can work with.
         var backupRestore = new BackupRestore();
         backupRestore.SetUp();
         backupRestore.SetupEnvironment();

         _application.Utilities.PerformMaintenance(eMaintenanceOperation.eUpdateIMAPFolderUID);
      }

      /// <summary>
      /// The repair must survive mail sitting in the delivery queue.
      ///
      /// RecalculateFolderUID_ grouped every row of hm_messages and returned false
      /// on the first row whose folder or uid was not positive. A message awaiting
      /// delivery is stored with messagefolderid = 0 and messageuid = 0, so on any
      /// server with a queue the grouped set contained a (0, 0) row and the whole
      /// operation failed - after updating an arbitrary number of folders, because
      /// the query has no ORDER BY. Neither complete nor a no-op, and reported only
      /// as "The maintenance operation failed".
      ///
      /// The queued row is produced by routing a domain at a closed port on
      /// 127.0.0.1: the connection is refused immediately, so the message stays in
      /// hm_messages with folder id 0 and uid 0, with no DNS lookup and no
      /// dependence on the network. Against the previous build the maintenance call
      /// throws a COMException.
      /// </summary>
      [Test]
      public void TestUpdateIMAPFolderUIDWithMessagesInTheDeliveryQueue()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "uidmaint@example.test", "test");

         // One message that IS in a folder, so the repair has something to do and
         // the assertion at the end means something.
         var smtpClient = new SmtpClientSimulator();
         smtpClient.Send(account.Address, account.Address, "In a folder", "Body");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         try
         {
            // And one that is not. A route sends this domain to a port nothing is
            // listening on, so the delivery attempt is refused at once and the
            // message stays in the queue - a row with folder id 0 and uid 0, which
            // is exactly what used to abort the whole run.
            var route = _settings.Routes.Add();
            route.DomainName = "queued.example.test";
            route.TargetSMTPHost = "127.0.0.1";
            route.TargetSMTPPort = 1;
            // Enough tries that the failure does not retire the message, and a
            // retry interval long enough that it will not be attempted again
            // while this test runs - it has to still be in the queue when the
            // maintenance operation is called.
            route.NumberOfTries = 5;
            route.MinutesBetweenTry = 5;
            route.Save();

            smtpClient.Send(account.Address, "someone@queued.example.test", "Queued", "Body");

            CustomAsserts.AssertRecipientsInDeliveryQueue(1, false);

            // The whole point: this must not throw.
            Assert.DoesNotThrow(() =>
               _application.Utilities.PerformMaintenance(eMaintenanceOperation.eUpdateIMAPFolderUID));

            // And the folder that did have a message must have been brought up to
            // date rather than skipped - the old code could abort before reaching it.
            var folder = account.IMAPFolders.get_ItemByName("Inbox");
            ClassicAssert.GreaterOrEqual(folder.CurrentUID, 1);
         }
         finally
         {
            // PerformBasicSetup does not clear the delivery queue, so a message left
            // here would be inherited by whichever fixture runs next. Routes it does
            // remove, but the queue is this test's to clean up - and so is anything
            // the refused connection put in the error log, or the next fixture's
            // AssertNoReportedError fails on this test's deliberate failure.
            TestSetup.DeleteMessagesInQueue();
            LogHandler.DeleteCurrentDefaultLog();
            LogHandler.ClearErrorLogUntilSettled();
         }
      }
   }
}