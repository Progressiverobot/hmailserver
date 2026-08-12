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
            "The SpamAssassin tests did not complete");
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
            "The SpamAssassin tests did not complete");
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

      /// <summary>
      ///    SAMoveVsCopy switches how the SpamAssassin result is written back over the
      ///    message: rename the temporary file over it, rather than copy-then-delete. It
      ///    had no test coverage at all, which mattered because that path used to delete
      ///    the live message file and only then attempt the rename, retrying five times.
      ///    If all five failed - a scanner or a backup holding either file, a full disk -
      ///    the message was simply gone, and the caller ignored the return value, so
      ///    nothing said so. The sender had already been given a 250.
      ///
      ///    FileUtilities::Move no longer deletes first: boost::filesystem::rename on
      ///    Windows is MoveFileExW with MOVEFILE_REPLACE_EXISTING, so it replaces the
      ///    destination as one operation and the message file always names either the old
      ///    content or the new one. This test pins that the replace still works, because
      ///    a change that made Move stop overwriting would be silent otherwise.
      ///
      ///    The failure path itself is not reachable from out here - provoking it needs
      ///    the message or temporary file locked at the exact moment of the rename, and
      ///    there is no hook for that. What is now guaranteed instead is that a failure
      ///    is reported (HM5860/HM5861) rather than discarded.
      /// </summary>
      [Test]
      [Description("With SAMoveVsCopy the SpamAssassin result replaces the message atomically, and the message survives")]
      public void SpamAssassinResultIsWrittenBackWhenMoveIsUsed()
      {
         string iniPath = System.IO.Path.Combine(
            _application.Settings.Directories.ProgramDirectory, "hMailServer.ini");
         if (!System.IO.File.Exists(iniPath))
            Assert.Ignore("hMailServer.ini is not next to the running executable in this layout.");

         string original = NativeMethods.GetIniValue("Settings", "SAMoveVsCopy", "0", iniPath);

         try
         {
            Assert.IsTrue(NativeMethods.SetIniValue("Settings", "SAMoveVsCopy", "1", iniPath),
               "Failed to enable SAMoveVsCopy.");
            _application.Reinitialize();

            new SmtpClientSimulator().Send(account.Address, account.Address,
               "SA move test", "This is a test message.");

            string contents = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");

            // The message must still exist - that is the regression this guards - and it
            // must carry the SpamAssassin headers, which is what proves the rename
            // actually replaced the file rather than quietly failing.
            Assert.IsTrue(contents.Contains("X-Spam-Status"),
               "SpamAssassin did not run, or its result was not written back over the message.");
            Assert.IsTrue(contents.Contains("This is a test message."),
               "The message body did not survive the SpamAssassin write-back.");
         }
         finally
         {
            NativeMethods.SetIniValue("Settings", "SAMoveVsCopy", original, iniPath);
            _application.Reinitialize();
         }
      }

      private static class NativeMethods
      {
         [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
         private static extern bool WritePrivateProfileString(string section, string key, string value, string filePath);

         [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
         private static extern uint GetPrivateProfileString(string section, string key, string defaultValue,
            System.Text.StringBuilder returnValue, uint size, string filePath);

         public static bool SetIniValue(string section, string key, string value, string filePath)
            => WritePrivateProfileString(section, key, value, filePath);

         public static string GetIniValue(string section, string key, string defaultValue, string filePath)
         {
            var buffer = new System.Text.StringBuilder(256);
            GetPrivateProfileString(section, key, defaultValue, buffer, (uint) buffer.Capacity, filePath);
            return buffer.ToString();
         }
      }
   }
}