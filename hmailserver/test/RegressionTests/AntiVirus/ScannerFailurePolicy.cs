// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Linq;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.AntiVirus
{
   /// <summary>
   ///    What happens to a message the virus scanner could not examine.
   ///
   ///    Until now: it was delivered, and the only trace was one HM5406 in the
   ///    error log - so a scanner that was down, misconfigured or unreachable
   ///    meant mail went out unscanned and looked exactly like mail that had been
   ///    scanned and found clean. `AVFailAction` names that posture and lets an
   ///    administrator invert it: 0 (the shipped default) delivers, 1 holds the
   ///    message, re-attempts it every AVFailRetryMinutes at most AVFailMaxHolds
   ///    times, and then returns it to the sender.
   ///
   ///    The failure is injected rather than simulated: the custom scanner is
   ///    pointed at an executable that does not exist, which is exactly the
   ///    "unable to launch" error a wrong path or a missing engine produces in
   ///    the field.
   ///
   ///    The assertion that matters most is not that the message was withheld -
   ///    it is that a withheld message is still there afterwards. A fail-closed
   ///    policy that loses the mail it declines to deliver would be far worse
   ///    than the fail-open behaviour it replaces.
   /// </summary>
   [TestFixture]
   public class ScannerFailurePolicy : TestFixtureBase
   {
      private const string MissingScanner = @"C:\hmailserver-no-such-scanner\nothing-here.exe";

      private Account _recipient;
      private Account _sender;

      private bool _originalCustomEnabled;
      private string _originalCustomExecutable;
      private int _originalCustomReturnValue;

      [SetUp]
      public new void SetUp()
      {
         _recipient = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "avrecipient@example.test", "test");
         _sender = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "avsender@example.test", "test");

         var antiVirus = _settings.AntiVirus;
         _originalCustomEnabled = antiVirus.CustomScannerEnabled;
         _originalCustomExecutable = antiVirus.CustomScannerExecutable;
         _originalCustomReturnValue = antiVirus.CustomScannerReturnValue;
      }

      [TearDown]
      public void TearDown()
      {
         var antiVirus = _settings.AntiVirus;
         antiVirus.CustomScannerEnabled = _originalCustomEnabled;
         antiVirus.CustomScannerExecutable = _originalCustomExecutable;
         antiVirus.CustomScannerReturnValue = _originalCustomReturnValue;

         SetIniSetting("AVFailAction", null);
         SetIniSetting("AVFailRetryMinutes", null);
         SetIniSetting("AVFailMaxHolds", null);
         _application.Reinitialize();

         // Nothing of this fixture's may be left queued for the next one.
         _application.GlobalObjects.DeliveryQueue.Clear();

         // A scanner that cannot be launched reports HM5406 by design. Every
         // fixture's setup treats the existence of an error log as a failure, so
         // this one has to clear up after the errors it deliberately provoked.
         LogHandler.ClearErrorLogUntilSettled();
      }

      private void BreakTheScanner()
      {
         var antiVirus = _settings.AntiVirus;
         antiVirus.CustomScannerEnabled = true;
         antiVirus.CustomScannerExecutable = MissingScanner;
         antiVirus.CustomScannerReturnValue = 1;
      }

      [Test]
      [Description("The shipped default is unchanged: a message the scanner could not examine is still delivered")]
      public void ByDefaultAnUnscannableMessageIsStillDelivered()
      {
         BreakTheScanner();

         SmtpClientSimulator.StaticSend(_sender.Address, _recipient.Address, "unscanned by default", "Body.");

         Pop3ClientSimulator.AssertMessageCount(_recipient.Address, "test", 1);

         // And the scanner failure is not silent, which is the half that was
         // already true and must stay true.
         // The wording changed when the launcher learned to tell a failed start
         // from a timeout kill: this path is a genuinely missing executable, so
         // it must report the path problem and not the timeout one.
         CustomAsserts.AssertReportedError("Unable to launch the virus scanner");
      }

      [Test]
      [Description("AVFailAction 1 withholds the message - and the message is still in the queue afterwards, so a later successful scan delivers it")]
      public void WithFailClosedTheMessageIsHeldAndNotLost()
      {
         BreakTheScanner();

         // The default budget of 16 holds comfortably covers this test, and a long
         // retry interval keeps anything from retrying behind our back. The bound
         // is a count this policy keeps itself - deliberately neither the SMTP
         // retry budget (which ships as 0) nor the message's age (which is
         // anchored to arrival, so an old backlog would get no hold at all).
         SetIniSetting("AVFailAction", "1");
         SetIniSetting("AVFailRetryMinutes", "60");
         _application.Reinitialize();

         LogHandler.DeleteCurrentDefaultLog();

         SmtpClientSimulator.StaticSend(_sender.Address, _recipient.Address, "held for scanning", "Body.");

         // The hold is announced, and the line carries the message id - which is
         // also how this test forces the retry below.
         string heldLine = WaitForLogLine("could not examine this message");
         var messageId = Regex.Match(heldLine, @"Message (\d+):");
         ClassicAssert.IsTrue(messageId.Success, "The hold line should name the message id. Got: " + heldLine);

         // Withheld: nothing in the mailbox. Counted directly rather than through
         // AssertMessageCount, whose zero case also asserts an EMPTY delivery
         // queue - and this message is deliberately still in it.
         ClassicAssert.AreEqual(0, new Pop3ClientSimulator().GetMessageCount(_recipient.Address, "test"),
            "The message must not have been delivered while the scanner could not examine it.");

         // ...and still queued. This is the assertion that separates a hold from
         // a loss, and it is the whole point of the feature.
         CustomAsserts.AssertRecipientsInDeliveryQueue(1);

         // Now let the scan succeed and force the retry: the SAME message must be
         // delivered, proving the hold preserved it rather than quietly dropping it.
         _settings.AntiVirus.CustomScannerEnabled = false;

         // Forced more than once, and that is not belt-and-braces. The server runs
         // its own retry pass, and if one lands between the assertions above and the
         // reset below, it scans with the scanner still broken, takes another hold
         // and stamps a fresh next-try time - 60 minutes away - OVER the reset. The
         // test then waits out its timeout on a message that will not move until
         // long after the suite has finished. That happened once, in a full-suite
         // run and only there, because the window is a few milliseconds wide and it
         // takes a loaded machine to land in it.
         //
         // Re-forcing costs nothing: the message is delivered on the first attempt
         // that sees a working scanner, and each attempt that does not is a hold
         // against a budget of 16 set above. So this loop either delivers or spends
         // three of sixteen holds, and the assertion below still reads the delivered
         // message rather than a retry count - the thing being proved is that the
         // held message survived, not how many times it was poked.
         long heldMessageId = Convert.ToInt64(messageId.Groups[1].Value);

         for (int attempt = 0; attempt < 3; attempt++)
         {
            var queue = _application.GlobalObjects.DeliveryQueue;
            queue.ResetDeliveryTime(heldMessageId);
            queue.StartDelivery();

            if (new Pop3ClientSimulator().GetMessageCount(_recipient.Address, "test") > 0)
               break;

            Thread.Sleep(1000);
         }

         Pop3ClientSimulator.AssertMessageCount(_recipient.Address, "test", 1);
         string delivered = Pop3ClientSimulator.AssertGetFirstMessageText(_recipient.Address, "test");
         StringAssert.Contains("held for scanning", delivered,
            "The held message itself should have been delivered once the scanner worked. Got: " + delivered);
      }

      [Test]
      [Description("AVFailAction 1 with no holds allowed bounces to the sender rather than delivering unscanned or holding for ever")]
      public void WithFailClosedAndTheHoldWindowSpentTheMessageBounces()
      {
         BreakTheScanner();

         // A hold budget of zero: the setting an administrator picks when they
         // never want unscanned mail sitting in a queue at all, and the state any
         // held message reaches once its holds are spent.
         SetIniSetting("AVFailAction", "1");
         SetIniSetting("AVFailMaxHolds", "0");
         _application.Reinitialize();

         SmtpClientSimulator.StaticSend(_sender.Address, _recipient.Address, "bounced not delivered", "Body.");

         // The sender is told. A message that cannot be scanned and cannot be
         // held any longer must not simply vanish.
         string bounce = Pop3ClientSimulator.AssertGetFirstMessageText(_sender.Address, "test");
         StringAssert.Contains("could not be checked for viruses", bounce,
            "The non-delivery report should say why. Got: " + bounce);

         // And the recipient never got it.
         ClassicAssert.AreEqual(0, new Pop3ClientSimulator().GetMessageCount(_recipient.Address, "test"),
            "An unscannable message must not be delivered when the policy says it must not be.");
      }

      private static string WaitForLogLine(string needle)
      {
         for (int attempt = 0; attempt < 200; attempt++)
         {
            string line = LogHandler.ReadCurrentDefaultLog().Split('\n').FirstOrDefault(candidate => candidate.Contains(needle));
            if (line != null)
               return line;

            Thread.Sleep(50);
         }

         Assert.Fail("The log never contained '" + needle + "'. Log:\r\n" + LogHandler.ReadCurrentDefaultLog());
         return null;
      }

      private static void SetIniSetting(string key, string value)
      {
         var path = IniPath();
         var lines = new List<string>(File.ReadAllLines(path));
         lines.RemoveAll(line => line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
         var section = lines.FindIndex(line => line.Trim() == "[Settings]");
         Assert.Greater(section, -1, "hMailServer.ini has no [Settings] section: " + path);
         if (value != null)
            lines.Insert(section + 1, key + "=" + value);
         File.WriteAllLines(path, lines);
      }

      private static string IniPath()
      {
         var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
         while (directory != null)
         {
            var candidate = Paths.Combine(directory.FullName,
               @"source\Server\hMailServer\x64\Release\hMailServer.ini");
            if (File.Exists(candidate))
               return candidate;
            directory = directory.Parent;
         }
         Assert.Fail("Could not locate the server's hMailServer.ini by searching upwards from " +
                     AppDomain.CurrentDomain.BaseDirectory);
         return null;
      }
   }
}
