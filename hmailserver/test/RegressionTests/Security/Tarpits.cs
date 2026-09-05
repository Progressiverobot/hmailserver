// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.Security
{
   /// <summary>
   ///    The two tarpits: a refused logon that waits before it is refused, and a
   ///    RCPT TO past a count that waits before it is answered.
   ///
   ///    Both are pauses queued on the connection (TCPConnection::EnqueueDelay),
   ///    not threads asleep, which is the property the last test here checks and the
   ///    reason neither existed before: a delay slept on a worker thread is a denial
   ///    of service an attacker triggers with one cheap failed logon per thread.
   ///
   ///    Both are off by default. The logon tarpit is hMailServer.ini
   ///    LogonTarpitSeconds (per failure, times the failures so far on that
   ///    connection, capped at 30); the recipient tarpit is AntiSpam.TarpitCount and
   ///    AntiSpam.TarpitDelay, the two properties that were stubs from 5.x until now.
   /// </summary>
   [TestFixture]
   public class Tarpits : TestFixtureBase
   {
      private const string Password = "test";

      // "At once" and "delayed by at least a second" have to be told apart on a
      // machine that is also running the rest of this suite. A refusal normally
      // arrives in a few milliseconds; a 1-second pause cannot arrive before 900 ms.
      private static readonly TimeSpan AtOnce = TimeSpan.FromMilliseconds(500);
      private static readonly TimeSpan OneSecondPause = TimeSpan.FromMilliseconds(900);
      private static readonly TimeSpan TwoSecondPause = TimeSpan.FromMilliseconds(1800);

      private Account _account;

      // Connections a test opens and deliberately leaves parked; disposed in
      // TearDown rather than a per-test finally so a failed assertion still closes
      // them and the cleanup is not a manual dispose the analyzer would rather see
      // as a using (which cannot span the loop that keeps them open).
      private readonly List<TcpConnection> _parked = new List<TcpConnection>();

      [SetUp]
      public new void SetUp()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "tarpit@example.test", Password);

         // Auto-ban would turn the fourth failure into a disconnect and a banned
         // address; these tests are about the delay, not the ban.
         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();
      }

      [TearDown]
      public new void TearDown()
      {
         foreach (var connection in _parked)
            connection.Dispose();
         _parked.Clear();

         _settings.AntiSpam.TarpitCount = 0;
         _settings.AntiSpam.TarpitDelay = 0;

         if (ReadSetting("LogonTarpitSeconds") != "0")
         {
            WriteSetting("LogonTarpitSeconds", "0");
            _application.Reinitialize();
         }
      }

      [Test]
      public void OffByDefaultARefusedLogonIsAnsweredAtOnce()
      {
         Assert.AreEqual("0", ReadSetting("LogonTarpitSeconds"), "The logon tarpit must be off unless the ini turns it on.");

         using (var pop3 = OpenPop3())
         {
            Assert.Less(TimeRefusedPop3Logon(pop3, "wrong-1"), AtOnce);
            Assert.Less(TimeRefusedPop3Logon(pop3, "wrong-2"), AtOnce);
         }
      }

      [Test]
      public void EachFailureOnAConnectionWaitsLongerThanTheLastAndACorrectPasswordDoesNotWait()
      {
         EnableLogonTarpit(1);

         using (var pop3 = OpenPop3())
         {
            Assert.GreaterOrEqual(TimeRefusedPop3Logon(pop3, "wrong-1"), OneSecondPause, "The first failure waits one unit.");
            Assert.GreaterOrEqual(TimeRefusedPop3Logon(pop3, "wrong-2"), TwoSecondPause, "The second waits two.");

            // The right password on the same, already-punished connection is not
            // delayed: the tarpit is on the refusal, not on the session.
            var stopwatch = Stopwatch.StartNew();
            pop3.Send("USER " + _account.Address + "\r\n");
            pop3.ReadUntil("+OK");
            pop3.Send("PASS " + Password + "\r\n");
            string reply = pop3.ReadUntil("\r\n");
            stopwatch.Stop();

            Assert.That(reply, Does.StartWith("+OK"), reply);
            Assert.Less(stopwatch.Elapsed, AtOnce);
            pop3.Send("QUIT\r\n");
         }
      }

      [Test]
      public void SmtpAndImapRefusalsAreDelayedToo()
      {
         EnableLogonTarpit(1);

         using (var smtp = new TcpConnection())
         {
            Assert.IsTrue(smtp.Connect(25));
            smtp.ReadUntil("220");
            smtp.Send("EHLO example.com\r\n");
            smtp.ReadUntil("250 ");

            var stopwatch = Stopwatch.StartNew();
            smtp.Send("AUTH PLAIN " + Base64("\0" + _account.Address + "\0wrong") + "\r\n");
            string reply = smtp.ReadUntil("\r\n");
            stopwatch.Stop();

            Assert.That(reply, Does.StartWith("535"), reply);
            Assert.GreaterOrEqual(stopwatch.Elapsed, OneSecondPause);
            smtp.Send("QUIT\r\n");
         }

         using (var imap = new TcpConnection())
         {
            Assert.IsTrue(imap.Connect(143));
            imap.ReadUntil("* OK");

            var stopwatch = Stopwatch.StartNew();
            imap.Send("A1 LOGIN " + _account.Address + " wrong\r\n");
            string reply = imap.ReadUntil("A1 ");
            stopwatch.Stop();

            Assert.That(reply, Does.Contain("A1 NO"), reply);
            Assert.GreaterOrEqual(stopwatch.Elapsed, OneSecondPause);
            imap.Send("A2 LOGOUT\r\n");
         }
      }

      [Test]
      public void ADelayedConnectionHoldsNoThread()
      {
         // The I/O pool has fifteen threads by default. Forty connections sitting in
         // a two-second tarpit at the same time would exhaust a pool that slept
         // through the delay; a forty-first arrival then proves nobody is asleep.
         EnableLogonTarpit(2);

         // Parked in the instance list so TearDown closes them even if an assertion
         // below throws.
         for (int i = 0; i < 40; i++)
         {
            var pop3 = OpenPop3();
            pop3.Send("USER " + _account.Address + "\r\n");
            pop3.ReadUntil("+OK");
            pop3.Send("PASS wrong-" + i + "\r\n");
            _parked.Add(pop3);
         }

         var stopwatch = Stopwatch.StartNew();
         using (var fresh = OpenPop3())
         {
            fresh.Send("USER " + _account.Address + "\r\n");
            fresh.ReadUntil("+OK");
            fresh.Send("PASS " + Password + "\r\n");
            string reply = fresh.ReadUntil("\r\n");
            stopwatch.Stop();

            Assert.That(reply, Does.StartWith("+OK"), reply);
            Assert.Less(stopwatch.Elapsed, AtOnce, "A correct logon must not queue behind forty delayed refusals.");
            fresh.Send("QUIT\r\n");
         }

         // And every parked refusal still arrives, each after its own pause.
         foreach (var pop3 in _parked)
            Assert.That(pop3.ReadUntil("\r\n", TimeSpan.FromSeconds(10)), Does.StartWith("-ERR"));
      }

      [Test]
      public void RecipientsPastTheCountWaitAndAuthenticatedSessionsDoNot()
      {
         var recipients = Enumerable.Range(1, 4)
            .Select(i => SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "recipient" + i + "@example.test", Password).Address)
            .ToList();

         _settings.AntiSpam.TarpitCount = 2;
         _settings.AntiSpam.TarpitDelay = 1;

         using (var smtp = new TcpConnection())
         {
            Assert.IsTrue(smtp.Connect(25));
            smtp.ReadUntil("220");
            smtp.Send("EHLO example.com\r\n");
            smtp.ReadUntil("250 ");
            smtp.Send("MAIL FROM:<sender@example.org>\r\n");
            smtp.ReadUntil("250");

            var taken = recipients.Select(address => TimeRcpt(smtp, address)).ToList();

            Assert.Less(taken[0], AtOnce, "The first recipient is within the count.");
            Assert.Less(taken[1], AtOnce, "So is the second.");
            Assert.GreaterOrEqual(taken[2], OneSecondPause, "The third is past it and waits.");
            Assert.GreaterOrEqual(taken[3], OneSecondPause, "And so does every one after it.");
            smtp.Send("QUIT\r\n");
         }

         using (var smtp = new TcpConnection())
         {
            Assert.IsTrue(smtp.Connect(25));
            smtp.ReadUntil("220");
            smtp.Send("EHLO example.com\r\n");
            smtp.ReadUntil("250 ");
            smtp.Send("AUTH PLAIN " + Base64("\0" + _account.Address + "\0" + Password) + "\r\n");
            Assert.That(smtp.ReadUntil("\r\n"), Does.StartWith("235"));
            smtp.Send("MAIL FROM:<" + _account.Address + ">\r\n");
            smtp.ReadUntil("250");

            foreach (string address in recipients)
               Assert.Less(TimeRcpt(smtp, address), AtOnce, "An authenticated session is exempt.");

            smtp.Send("QUIT\r\n");
         }
      }

      [Test]
      public void TheTarpitPropertiesAreRealAgainAndBounded()
      {
         _settings.AntiSpam.TarpitCount = 5;
         _settings.AntiSpam.TarpitDelay = 7;

         Assert.AreEqual(5, _settings.AntiSpam.TarpitCount);
         Assert.AreEqual(7, _settings.AntiSpam.TarpitDelay);
         Assert.AreEqual("5", ReadSetting("SmtpTarpitCount"), "Stored in the ini, so it survives a restart.");
         Assert.AreEqual("7", ReadSetting("SmtpTarpitDelaySeconds"));

         Assert.Throws<COMException>(() => _settings.AntiSpam.TarpitDelay = 31, "A pause longer than 30 seconds is refused.");
         Assert.Throws<COMException>(() => _settings.AntiSpam.TarpitCount = -1);

         Assert.AreEqual(7, _settings.AntiSpam.TarpitDelay, "A refused write changes nothing.");
      }

      // ----------------------------------------------------------------------

      private void EnableLogonTarpit(int seconds)
      {
         WriteSetting("LogonTarpitSeconds", seconds.ToString());
         _application.Reinitialize();
         _settings.ClearLogonFailureList();
      }

      private static TcpConnection OpenPop3()
      {
         var pop3 = new TcpConnection();
         Assert.IsTrue(pop3.Connect(110));
         pop3.ReadUntil("+OK");
         return pop3;
      }

      private TimeSpan TimeRefusedPop3Logon(TcpConnection pop3, string wrongPassword)
      {
         pop3.Send("USER " + _account.Address + "\r\n");
         pop3.ReadUntil("+OK");

         var stopwatch = Stopwatch.StartNew();
         pop3.Send("PASS " + wrongPassword + "\r\n");
         string reply = pop3.ReadUntil("\r\n", TimeSpan.FromSeconds(10));
         stopwatch.Stop();

         Assert.That(reply, Does.StartWith("-ERR"), reply);
         return stopwatch.Elapsed;
      }

      private static TimeSpan TimeRcpt(TcpConnection smtp, string address)
      {
         var stopwatch = Stopwatch.StartNew();
         smtp.Send("RCPT TO:<" + address + ">\r\n");
         string reply = smtp.ReadUntil("\r\n", TimeSpan.FromSeconds(10));
         stopwatch.Stop();

         Assert.That(reply, Does.StartWith("250"), reply);
         return stopwatch.Elapsed;
      }

      private static string Base64(string text)
      {
         return Convert.ToBase64String(Encoding.ASCII.GetBytes(text));
      }

      private void WriteSetting(string key, string value)
      {
         bool wroteAny = false;
         foreach (string iniPath in IniCandidates().Where(File.Exists))
         {
            Assert.IsTrue(IniFile.WritePrivateProfileString("Settings", key, value, iniPath), "Failed to write " + key + " to " + iniPath);
            wroteAny = true;
         }

         Assert.IsTrue(wroteAny, "Could not locate an existing hMailServer.ini to update.");
      }

      private string ReadSetting(string key)
      {
         string iniPath = IniCandidates().FirstOrDefault(File.Exists);
         Assert.IsNotNull(iniPath, "Could not locate hMailServer.ini.");

         string line = File.ReadAllLines(iniPath)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));

         return line == null ? "0" : line.Substring(key.Length + 1).Trim();
      }

      private IEnumerable<string> IniCandidates()
      {
         string programDirectory = _application.Settings.Directories.ProgramDirectory;
         yield return Paths.Combine(programDirectory, "hMailServer.ini");
         yield return Paths.Combine(programDirectory, "Bin", "hMailServer.ini");
      }
   }
}
