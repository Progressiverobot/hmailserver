// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.Security
{
   [TestFixture]
   public class AutoBan : TestFixtureBase
   {
      [SetUp]
      public new void SetUp()
      {
         _settings.ClearLogonFailureList();
         LogHandler.DeleteCurrentDefaultLog();
      }

      [Test]
      public void TestDisabled()
      {
         _settings.AutoBanOnLogonFailure = false;
         _settings.MaxInvalidLogonAttempts = 3;
         _settings.MaxInvalidLogonAttemptsWithin = 5;
         _settings.AutoBanMinutes = 3;

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         var sim = new Pop3ClientSimulator();
         Assert.IsTrue(sim.ConnectAndLogon(account.Address, "test"));
         sim.Disconnect();

         // confirm that we can retrieve welcome message.
         Assert.IsTrue(sim.GetWelcomeMessage().StartsWith("+OK"));

         string errorMessage;
         // fail to log on 3 times.
         for (var i = 0; i < 5; i++)
         {
            Assert.IsFalse(sim.ConnectAndLogon(account.Address, "testA", out errorMessage));
            sim.Disconnect();
         }

         Assert.IsTrue(sim.GetWelcomeMessage().StartsWith("+OK"));

         var logText = LogHandler.ReadCurrentDefaultLog();
         Assert.IsFalse(logText.Contains("Blocked either by IP range or by connection limit."), logText);
      }

      [Test]
      public void TestIMAPLogonFailure()
      {
         _settings.AutoBanOnLogonFailure = true;
         _settings.MaxInvalidLogonAttempts = 4;
         _settings.MaxInvalidLogonAttemptsWithin = 5;
         _settings.AutoBanMinutes = 3;

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         var sim = new ImapClientSimulator();
         Assert.IsTrue(sim.ConnectAndLogon(account.Address, "test"));
         sim.Disconnect();

         // confirm that we can retrieve welcome message.
         Assert.IsTrue(sim.GetWelcomeMessage().StartsWith("* OK"));

         // fail to log on 3 times.
         for (var i = 0; i < 4; i++)
         {
            string errorMessage;

            Assert.IsFalse(sim.ConnectAndLogon(account.Address, "testA", out errorMessage));
            sim.Disconnect();

            if (i == 3) Assert.IsTrue(errorMessage.Contains("Too many invalid logon attempts."));
         }

         Assert.IsTrue(sim.GetWelcomeMessage().Length == 0);

         var logText = LogHandler.ReadCurrentDefaultLog();
         Assert.IsTrue(logText.Contains("Blocked either by IP range or by connection limit."), logText);
      }

      [Test]
      public void TestIPRangeName()
      {
         _settings.AutoBanOnLogonFailure = true;
         _settings.MaxInvalidLogonAttempts = 3;
         _settings.MaxInvalidLogonAttemptsWithin = 5;
         _settings.AutoBanMinutes = 3;

         Assert.AreEqual(2, _settings.SecurityRanges.Count);

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         var sim = new Pop3ClientSimulator();
         Assert.IsTrue(sim.ConnectAndLogon(account.Address, "test"));
         sim.Disconnect();

         // confirm that we can retrieve welcome message.
         Assert.IsTrue(sim.GetWelcomeMessage().StartsWith("+OK"));

         string errorMessage;
         // fail to log on 3 times.
         for (var i = 0; i < 3; i++)
         {
            Assert.IsFalse(sim.ConnectAndLogon(account.Address, "testA", out errorMessage));
            sim.Disconnect();

            if (i == 2) Assert.IsTrue(errorMessage.Contains("Too many invalid logon attempts."));
         }

         Assert.IsTrue(sim.GetWelcomeMessage().Length == 0);

         Assert.AreEqual(3, _settings.SecurityRanges.Count);
         Assert.AreEqual("Auto-ban: test@example.test", _settings.SecurityRanges[2].Name);
      }

      [Test]
      public void TestPOP3LogonFailure()
      {
         _settings.AutoBanOnLogonFailure = true;
         _settings.MaxInvalidLogonAttempts = 3;
         _settings.MaxInvalidLogonAttemptsWithin = 5;
         _settings.AutoBanMinutes = 3;

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "te'st@example.test", "test");

         var sim = new Pop3ClientSimulator(TestSetup.GetLocalIpAddress(), false, 110);
         Assert.IsTrue(sim.ConnectAndLogon(account.Address, "test"));
         sim.Disconnect();

         // confirm that we can retrieve welcome message.
         Assert.IsTrue(sim.GetWelcomeMessage().StartsWith("+OK"));

         string errorMessage;
         // fail to log on 3 times.
         for (var i = 0; i < 3; i++)
         {
            Assert.IsFalse(sim.ConnectAndLogon(account.Address, "testA", out errorMessage));
            sim.Disconnect();

            if (i == 2) Assert.IsTrue(errorMessage.Contains("Too many invalid logon attempts."));
         }

         Assert.IsTrue(sim.GetWelcomeMessage().Length == 0);

         var logText = LogHandler.ReadCurrentDefaultLog();
         Assert.IsTrue(logText.Contains("Blocked either by IP range or by connection limit."), logText);
      }

      [Test]
      public void TestSMTPLogonFailure()
      {
         _settings.AutoBanOnLogonFailure = true;
         _settings.MaxInvalidLogonAttempts = 2;
         _settings.MaxInvalidLogonAttemptsWithin = 5;
         _settings.AutoBanMinutes = 3;

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         var sim = new SmtpClientSimulator();

         //test@example.test / test
         string errorMessage;
         sim.ConnectAndLogon("dGVzdEBleGFtcGxlLnRlc3Q=", "dGVzdA==", out errorMessage);
         sim.Disconnect();

         // confirm that we can retrieve welcome message.
         Assert.IsTrue(sim.GetWelcomeMessage().StartsWith("220"));

         // fail to log on 3 times.
         for (var i = 0; i < 2; i++)
         {
            CustomAsserts.Throws<Exception>(() =>
               sim.ConnectAndLogon("dGVzdEBleGFtcGxlLnRlc3Q=", "Vaffe==", out errorMessage));
            sim.Disconnect();

            if (i == 2) Assert.IsTrue(errorMessage.Contains("Too many invalid logon attempts."));
         }

         Assert.IsTrue(sim.GetWelcomeMessage().Length == 0);

         var logText = LogHandler.ReadCurrentDefaultLog();
         Assert.IsTrue(logText.Contains("Blocked either by IP range or by connection limit."), logText);
      }
   }
}