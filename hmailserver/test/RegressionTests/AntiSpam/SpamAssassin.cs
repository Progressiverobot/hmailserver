// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Threading;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.AntiSpam
{
   [TestFixture]
   public class SpamAssassin : TestFixtureBase
   {
      [SetUp]
      public new void SetUp()
      {
         CustomAsserts.AssertSpamAssassinIsRunning();

         // Enable spam assassin
         var antiSpam = _settings.AntiSpam;

         account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sa@example.test", "test");

         // Disallow incorrect line endings.
         antiSpam.SpamMarkThreshold = 1;
         antiSpam.SpamDeleteThreshold = 10000;
         antiSpam.AddHeaderReason = true;
         antiSpam.AddHeaderSpam = true;
         antiSpam.PrependSubject = true;
         antiSpam.PrependSubjectText = "ThisIsSpam";

         // Enable SpamAssassin
         antiSpam.SpamAssassinEnabled = true;
         antiSpam.SpamAssassinHost = "localhost";
         antiSpam.SpamAssassinPort = 783;
         antiSpam.SpamAssassinMergeScore = false;
         antiSpam.SpamAssassinScore = 5;
      }

      private Account account;


      [Test]
      public void ItShouldBePossibleToTestSAConnectionUsingAPISuccess()
      {
         var antiSpam = _settings.AntiSpam;

         string resultText;
         Assert.IsTrue(antiSpam.TestSpamAssassinConnection("localhost", 783, out resultText));
         Assert.IsTrue(resultText.Contains("Content analysis details:"));
      }

      [Test]
      public void ItShouldBePossibleToTestSAConnectionUsingAPIFailure()
      {
         var antiSpam = _settings.AntiSpam;

         string resultText;

         Assert.IsFalse(antiSpam.TestSpamAssassinConnection("localhost", 0, out resultText));
      }


      [Test]
      public void TestBasic()
      {
         // Send a messages to this account.
         var smtpClientSimulator = new SmtpClientSimulator();

         smtpClientSimulator.Send(account.Address, account.Address, "SA test", "This is a test message.");
         var sMessageContents = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");
         if (!sMessageContents.Contains("X-Spam-Status")) Assert.Fail("SpamAssassin did not run");
      }

      [Test]
      public void TestDisabled()
      {
         var smtpClientSimulator = new SmtpClientSimulator();

         _settings.AntiSpam.SpamAssassinEnabled = false;
         _settings.AntiSpam.SpamAssassinHost = "localhost";
         smtpClientSimulator.Send(account.Address, account.Address, "SA test", "This is a test message.");

         var sMessageContents = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");
         if (sMessageContents.Contains("X-Spam-Status"))
         {
            _settings.AntiSpam.SpamAssassinEnabled = false;
            throw new Exception("Spam assassin not run");
         }
      }

      [Test]
      public void TestIncorrectHost()
      {
         var smtpClientSimulator = new SmtpClientSimulator();

         _settings.AntiSpam.SpamAssassinEnabled = true;
         _settings.AntiSpam.SpamAssassinHost = "localholst"; // <- mispelled
         smtpClientSimulator.Send(account.Address, account.Address, "SA test", "This is a test message.");
         var sMessageContents = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");
         if (sMessageContents.Contains("X-Spam-Status"))
         {
            _settings.AntiSpam.SpamAssassinEnabled = false;
            throw new Exception("Spam assassin not run");
         }

         CustomAsserts.AssertReportedError("The IP address for SpamAssassin could not be resolved.");
      }

      [Test]
      public void TestIncorrectPort()
      {
         var smtpClientSimulator = new SmtpClientSimulator();

         _settings.AntiSpam.SpamAssassinEnabled = true;
         _settings.AntiSpam.SpamAssassinHost = "localhost"; // <- mispelled
         _settings.AntiSpam.SpamAssassinPort = 12345;

         smtpClientSimulator.Send(account.Address, account.Address, "SA test", "This is a test message.");
         var sMessageContents = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");
         if (sMessageContents.Contains("X-Spam-Status"))
         {
            _settings.AntiSpam.SpamAssassinEnabled = false;
            throw new Exception("Spam assassin not run");
         }

         CustomAsserts.AssertReportedError(
            "The SpamAssassin tests did not complete. Please confirm that the configuration (host name and port) is valid and that SpamAssassin is running.");
      }

      [Test]
      public void TestIpAddressAsHostName()
      {
         var smtpClientSimulator = new SmtpClientSimulator();

         _settings.AntiSpam.SpamAssassinEnabled = true;
         _settings.AntiSpam.SpamAssassinHost = "127.0.0.1";
         smtpClientSimulator.Send(account.Address, account.Address, "SA test", "This is a test message.");
         var messageContents = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");

         if (!messageContents.Contains("X-Spam-Status")) Assert.Fail("SpamAssassin did not run");
      }

      [Test]
      public void TestMessageScore()
      {
         // Send a messages to this account.
         var smtpClientSimulator = new SmtpClientSimulator();

         smtpClientSimulator.Send(account.Address, account.Address, "SA test",
            "This is a test message with spam.\r\n XJS*C4JDBQADN1.NSBN3*2IDNEN*GTUBE-STANDARD-ANTI-UBE-TEST-EMAIL*C.34X.");

         var sMessageContents = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");

         var scoreStart = sMessageContents.IndexOf("X-Spam-Status: Yes, score") + "X-Spam-Status: Yes, score".Length +
                          1;
         var scoreEnd = sMessageContents.IndexOf(".", scoreStart);
         var scoreLength = scoreEnd - scoreStart;

         var score = sMessageContents.Substring(scoreStart, scoreLength);
         var scoreValue = Convert.ToDouble(score);

         Assert.Greater(scoreValue, 500);
      }

      [Test]
      public void TestMessageScoreNotMerged()
      {
         // Send a messages to this account.
         var smtpClientSimulator = new SmtpClientSimulator();

         smtpClientSimulator.Send(account.Address, account.Address, "SA test",
            "This is a test message with spam.\r\n XJS*C4JDBQADN1.NSBN3*2IDNEN*GTUBE-STANDARD-ANTI-UBE-TEST-EMAIL*C.34X.");

         var sMessageContents = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");

         var scoreStart = sMessageContents.IndexOf("X-hMailServer-Reason-Score");
         Assert.AreNotEqual(0, scoreStart);

         scoreStart = sMessageContents.IndexOf(":", scoreStart) + 2;
         var scoreEnd = sMessageContents.IndexOf("\r\n", scoreStart);
         var scoreLength = scoreEnd - scoreStart;
         var score = sMessageContents.Substring(scoreStart, scoreLength);

         var scoreValue = Convert.ToDouble(score);
         Assert.Less(scoreValue, 10);
      }

      [Test]
      public void TestSANotRunning()
      {
         StopSpamAssassin();

         // Send a messages to this account.
         var smtpClientSimulator = new SmtpClientSimulator();

         smtpClientSimulator.Send(account.Address, account.Address, "SA test", "This is a test message.");
         var sMessageContents = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");

         Assert.IsFalse(sMessageContents.Contains("X-Spam-Status"));

         // Only error 5508 belongs to this scenario. The "communication error with
         // SpamAssassin" that used to be asserted alongside it is error 5157, which
         // SpamAssassinClient::OnReadError reports when a connection was
         // established and then lost mid-read. With spamd stopped there is nothing
         // to connect to, so Connect() fails outright and that error never fires -
         // asserting it demanded a different failure mode from the one under test.
         CustomAsserts.AssertReportedError(
            "The SpamAssassin tests did not complete. Please confirm that the configuration (host name and port) is valid and that SpamAssassin is running.");
      }

      [Test]
      public void TestScoreMerge()
      {
         _settings.AntiSpam.SpamAssassinMergeScore = true;

         // Send a messages to this account.
         var smtpClientSimulator = new SmtpClientSimulator();

         smtpClientSimulator.Send(account.Address, account.Address, "SA test",
            "This is a test message with spam.\r\n XJS*C4JDBQADN1.NSBN3*2IDNEN*GTUBE-STANDARD-ANTI-UBE-TEST-EMAIL*C.34X.");

         var sMessageContents = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");

         var scoreStart = sMessageContents.IndexOf("X-hMailServer-Reason-Score");
         Assert.AreNotEqual(-1, scoreStart, sMessageContents);

         try
         {
            scoreStart = sMessageContents.IndexOf(":", scoreStart) + 2;
         }
         catch (Exception)
         {
            Assert.Fail(sMessageContents);
         }

         Assert.AreNotEqual(-1, scoreStart, sMessageContents);

         var scoreEnd = sMessageContents.IndexOf("\r\n", scoreStart);
         Assert.AreNotEqual(-1, scoreEnd, sMessageContents);

         var scoreLength = scoreEnd - scoreStart;
         var score = sMessageContents.Substring(scoreStart, scoreLength);

         var scoreValue = Convert.ToDouble(score);
         Assert.Greater(scoreValue, 100);
      }

      [Test]
      public void TestSpamMessage()
      {
         // Send a messages to this account.
         var smtpClientSimulator = new SmtpClientSimulator();

         smtpClientSimulator.Send(account.Address, account.Address, "SA test",
            "This is a test message with spam.\r\n XJS*C4JDBQADN1.NSBN3*2IDNEN*GTUBE-STANDARD-ANTI-UBE-TEST-EMAIL*C.34X.");

         var sMessageContents = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");
         if (!sMessageContents.Contains("X-Spam-Status: Yes"))
            Assert.Fail("Spam message not treated as spam (no X-Spam-Status-header).");

         if (!sMessageContents.Contains("X-hMailServer-Spam"))
            Assert.Fail("Spam message not treated as spam (no X-hMailServer-Spam header).");

         if (!sMessageContents.Contains("X-hMailServer-Reason"))
            Assert.Fail("Spam message not treated as spam (no X-hMailServer-Reason header).");

         if (!sMessageContents.Contains("X-hMailServer-Reason-Score"))
            Assert.Fail("Spam message not treated as spam (no X-hMailServer-Reason-Score header).");
      }

      [Test]
      [Description("Make sure that after SA has been run, the message header is still valid.")]
      public void MessageHeaderShouldBeValidAfterSAHasRun()
      {
         // Send a messages to this account.
         var smtpClient = new SmtpClientSimulator();
         smtpClient.Send(account.Address, account.Address, "SA test",
            "This is a test message with spam.\r\n XJS*C4JDBQADN1.NSBN3*2IDNEN*GTUBE-STANDARD-ANTI-UBE-TEST-EMAIL*C.34X.");

         var fullMessage = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");

         var messageHeader = fullMessage.Substring(0, fullMessage.IndexOf("\r\n\r\n"));
         Assert.IsTrue(messageHeader.Contains("Received:"));
         Assert.IsTrue(messageHeader.Contains("Return-Path:"));
         Assert.IsTrue(messageHeader.Contains("From:"));
         Assert.IsTrue(messageHeader.Contains("Subject: ThisIsSpam"));
      }

      [Test]
      public void TestWhiteList()
      {
         // First white-list the sender address
         var address = _settings.AntiSpam.WhiteListAddresses.Add();
         address.Description = "TestWhiteList";
         address.EmailAddress = "test-sender@example.test";
         address.LowerIPAddress = "0.0.0.0";
         address.UpperIPAddress = "255.255.255.255";
         address.Save();


         // Send a messages to this account.
         var smtpClientSimulator = new SmtpClientSimulator();
         smtpClientSimulator.Send("test-sender@example.test", account.Address, "SA test",
            "This is a test message with spam.\r\n XJS*C4JDBQADN1.NSBN3*2IDNEN*GTUBE-STANDARD-ANTI-UBE-TEST-EMAIL*C.34X.");

         var sMessageContents = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");

         Assert.IsFalse(sMessageContents.Contains("X-Spam-Status: Yes"));
      }


      /// <summary>
      /// Stops SpamAssassin and does not return until it has actually stopped
      /// serving. ServiceController.Stop() only asks: it returns as soon as the
      /// request is accepted, while spamd is still up and still answering on 783.
      /// The caller then sent a message straight into a live spamd and got the
      /// X-Spam-Status header it was asserting could not be there.
      ///
      /// The service reaching Stopped is necessary but not sufficient - a wrapped
      /// spamd can outlive its own service by a moment - so this waits on the port
      /// refusing connections, which is the condition the test actually depends on.
      /// </summary>
      private static void StopSpamAssassin()
      {
         try
         {
            var serviceController = new ServiceController("SpamAssassinJAM");
            if (serviceController.Status != ServiceControllerStatus.Stopped)
            {
               serviceController.Stop();
               serviceController.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
            }
         }
         catch (Exception)
         {
            Assert.Inconclusive("Unable to stop SpamAssassin process. Is SpamAssassin installed?");
         }

         for (var i = 0; i < 120; i++)
         {
            if (!IsSpamAssassinAcceptingConnections())
               return;

            Thread.Sleep(250);
         }

         Assert.Inconclusive("SpamAssassin is still accepting connections on port 783 after being stopped.");
      }

      private static bool IsSpamAssassinAcceptingConnections()
      {
         try
         {
            using (var client = new TcpClient())
            {
               var result = client.BeginConnect("127.0.0.1", 783, null, null);
               return result.AsyncWaitHandle.WaitOne(500) && client.Connected;
            }
         }
         catch
         {
            return false;
         }
      }
   }
}