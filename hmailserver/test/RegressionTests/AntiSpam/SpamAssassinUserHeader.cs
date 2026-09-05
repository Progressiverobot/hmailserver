// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using System.IO;
using System.Linq;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.AntiSpam
{
   /// <summary>
   ///    The User: header of the spamd PROCESS request.
   ///
   ///    spamd applies per-user preferences - a user_prefs file, or a row in its SQL
   ///    preference store - to the user a request names in its User: header, and applies
   ///    its global configuration to a request that names nobody. Until now every scan
   ///    named nobody, so a site that had gone to the trouble of per-user SpamAssassin
   ///    preferences found them ignored for mail delivered through this server.
   ///
   ///    Two [Settings] keys drive it, both off by default so that no existing spamd sees
   ///    a header it was never sent before. SpamAssassinUser names a fixed profile.
   ///    SpamAssassinUserFromRecipient=1 names the recipient instead, when the message
   ///    has exactly one: a scan happens once per message, not once per recipient, so a
   ///    message to several people cannot honestly be scanned under any one of their
   ///    preferences and falls back to the fixed profile.
   ///
   ///    These run against a simulated spamd rather than the real one, because the
   ///    question is what was sent, and only a spamd of our own can answer it. The real
   ///    one keeps answering the verdict fixtures next door.
   /// </summary>
   [TestFixture]
   public class SpamAssassinUserHeader : TestFixtureBase
   {
      private Account _account;
      private SpamdSimulator _spamd;

      [SetUp]
      public new void SetUp()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sauser@example.test", "test");

         _spamd = new SpamdSimulator();

         var antiSpam = _settings.AntiSpam;
         antiSpam.SpamMarkThreshold = 1;
         antiSpam.SpamDeleteThreshold = 10000;
         antiSpam.SpamAssassinEnabled = true;
         antiSpam.SpamAssassinHost = "127.0.0.1";
         antiSpam.SpamAssassinPort = _spamd.Port;
         antiSpam.SpamAssassinMergeScore = false;
         antiSpam.SpamAssassinScore = 5;

         WriteSetting("SpamAssassinUser", "");
         WriteSetting("SpamAssassinUserFromRecipient", "0");
         Apply();
      }

      [TearDown]
      public new void TearDown()
      {
         WriteSetting("SpamAssassinUser", "");
         WriteSetting("SpamAssassinUserFromRecipient", "0");
         Apply();

         if (_spamd != null)
            _spamd.Dispose();
      }

      [Test]
      public void ByDefaultNoUserHeaderIsSent()
      {
         Deliver(_account.Address);

         var request = _spamd.WaitForRequest(0);
         Assert.That(request["Request"], Does.StartWith("PROCESS SPAMC/"));
         Assert.IsFalse(request.ContainsKey("User"), "A User: header was sent with neither setting on: " + Describe(request));
      }

      [Test]
      public void AFixedProfileIsSentAsTheUser()
      {
         WriteSetting("SpamAssassinUser", "mailfilter");
         Apply();

         Deliver(_account.Address);

         var request = _spamd.WaitForRequest(0);
         Assert.AreEqual("mailfilter", Header(request, "User"));
      }

      [Test]
      public void TheSoleRecipientIsSentAsTheUserWhenAsked()
      {
         WriteSetting("SpamAssassinUserFromRecipient", "1");
         Apply();

         Deliver(_account.Address);

         var request = _spamd.WaitForRequest(0);
         Assert.AreEqual(_account.Address, Header(request, "User"));
      }

      [Test]
      public void AMessageToSeveralRecipientsFallsBackToTheFixedProfile()
      {
         var second = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sauser2@example.test", "test");

         WriteSetting("SpamAssassinUserFromRecipient", "1");
         WriteSetting("SpamAssassinUser", "shared");
         Apply();

         Deliver(_account.Address, second.Address);

         var request = _spamd.WaitForRequest(0);
         Assert.AreEqual("shared", Header(request, "User"), "Two recipients cannot both be the user a single scan runs as.");
      }

      [Test]
      public void AMessageToSeveralRecipientsSendsNoUserWithoutAFixedProfile()
      {
         var second = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sauser3@example.test", "test");

         WriteSetting("SpamAssassinUserFromRecipient", "1");
         Apply();

         Deliver(_account.Address, second.Address);

         var request = _spamd.WaitForRequest(0);
         Assert.IsFalse(request.ContainsKey("User"), Describe(request));
      }

      [Test]
      public void TheMessageStillArrivesAfterTheSimulatedScan()
      {
         WriteSetting("SpamAssassinUser", "mailfilter");
         Apply();

         Deliver(_account.Address);

         var text = Pop3ClientSimulator.AssertGetFirstMessageText(_account.Address, "test");
         Assert.That(text, Does.Contain("User header test"));
         Assert.AreEqual(1, _spamd.Requests.Count);
      }

      private void Deliver(params string[] recipients)
      {
         var smtpClientSimulator = new SmtpClientSimulator();
         smtpClientSimulator.Send("sender@example.com", new List<string>(recipients), "User header test", "This is a test message.");

         foreach (var recipient in recipients)
            Pop3ClientSimulator.AssertMessageCount(recipient, "test", 1);
      }

      private static string Header(Dictionary<string, string> request, string name)
      {
         string value;
         Assert.IsTrue(request.TryGetValue(name, out value), "No " + name + " header in the spamd request: " + Describe(request));
         return value;
      }

      private static string Describe(Dictionary<string, string> request)
      {
         return string.Join(" | ", request.Select(pair => pair.Key + ": " + pair.Value));
      }

      /// <summary>
      ///    Writes a [Settings] key to every hMailServer.ini the running service could be
      ///    reading, the way the password-cost fixture does; Apply() then has the server
      ///    re-read it.
      /// </summary>
      private void WriteSetting(string key, string value)
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
               IniFile.WritePrivateProfileString("Settings", key, value, iniPath),
               "Failed to write " + key + " to " + iniPath + ".");
            wroteAny = true;
         }

         Assert.IsTrue(wroteAny, "Could not locate an existing hMailServer.ini to update.");
      }

      private void Apply()
      {
         _application.Reinitialize();
      }
   }
}
