// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Text;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.AntiSpam
{
   [TestFixture]
   public class Basics : TestFixtureBase
   {
      // The URI blacklist this fixture tests against, served locally.
      //
      // Every assertion here except one wants a NEGATIVE answer - "0 addresses found,
      // Match: False" - and a lookup that times out produces the same shape of
      // non-answer as one that completes, so the fixture could pass while proving
      // nothing. The one positive is the SURBL project's permanent test point, and it
      // is the reason the fixture used to fail at random: on 19 August 2026
      // TestMaxSizeNoLimit reported "Spam message not detected as spam" after ten
      // seconds because the lookup had not come back, which says nothing about the
      // size limit it is named for.
      //
      // 127.0.0.2 is what a URI blacklist returns for a listed name.
      private const string SurblTestPoint = "surbl-org-permanent-test-point.com.multi.surbl.org";

      private FakeDnsServer dns_;

      [OneTimeSetUp]
      public void PointTheServerAtALocalResolver()
      {
         dns_ = new FakeDnsServer()
            .WithA(SurblTestPoint, "127.0.0.2")
            // TestMissingMXRecord needs one domain that HAS an MX and one that does
            // not, and it uses microsoft.com for the first. Served, because the
            // negative half of that test only means something if the positive half
            // reaches the server at all.
            .WithMx("microsoft.com", 10, "mx.microsoft.test")
            .WithA("mx.microsoft.test", "192.0.2.40");

         ServerIniFile.SetSetting("DNSServer", "127.0.0.1");

         RestartServerAndReacquireCom();
      }

      [OneTimeTearDown]
      public void RestoreTheSystemResolver()
      {
         try
         {
            ServerIniFile.SetSetting("DNSServer", null);
            RestartServerAndReacquireCom();
         }
         finally
         {
            dns_?.Dispose();
         }
      }

      [SetUp]
      public new void SetUp()
      {
         _antiSpam = _settings.AntiSpam;
      }

      private hMailServer.AntiSpam _antiSpam;

      [Test]
      public void TestDNSBlackList()
      {
         var dnsBlackLists = SingletonProvider<TestSetup>.Instance.GetApp().Settings.AntiSpam.DNSBlackLists;

         var dnsBlackList = dnsBlackLists.Add();
         dnsBlackList.DNSHost = "zen.spamhaus.org";
         dnsBlackList.RejectMessage = "srv1";
         dnsBlackList.Score = 5;
         dnsBlackList.Active = true;
         dnsBlackList.Save();

         dnsBlackList = dnsBlackLists.Add();
         dnsBlackList.DNSHost = "bl.spamcop.net";
         dnsBlackList.RejectMessage = "srv2";
         dnsBlackList.Score = 5;
         dnsBlackList.Active = false;
         dnsBlackList.Save();

         dnsBlackList = dnsBlackLists.Add();
         dnsBlackList.DNSHost = "dnsbl.njabl.org";
         dnsBlackList.RejectMessage = "srv3";
         dnsBlackList.Score = 5;
         dnsBlackList.Active = true;
         dnsBlackList.Save();

         var application = SingletonProvider<TestSetup>.Instance.GetApp();
         _antiSpam.SpamMarkThreshold = 1;
         _antiSpam.SpamDeleteThreshold = 100;

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "dnsbltest@example.test", "test");

         LogHandler.DeleteCurrentDefaultLog();

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "TestBody");
         Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");

         var result = LogHandler.ReadCurrentDefaultLog();

         Assert.IsTrue(result.Contains(".zen.spamhaus.org, 0 addresses found: (none), Match: False"), result);
         Assert.IsTrue(result.Contains(".dnsbl.njabl.org, 0 addresses found: (none), Match: False"), result);
         Assert.IsFalse(result.Contains(".bl.spamcop.net, 0 addresses found:"), result);
      }

      [Test]
      public void TestHeloSpamTest()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();

         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         // Disallow incorrect line endings.
         _antiSpam.SpamDeleteThreshold = 100;

         _antiSpam.CheckHostInHelo = true;
         _antiSpam.CheckHostInHeloScore = 125;

         // Send a messages to this account.
         var smtpClientSimulator = new SmtpClientSimulator();

         CustomAsserts.Throws<DeliveryFailedException>(() =>
            smtpClientSimulator.Send("whitelist@microsoft.com", "whitelist@example.test", "SURBL-Match", "Test"));
      }

      [Test]
      public void TestIncorrectLineEndings()
      {
         // Create a test account
         // Fetch the default domain
         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain,
            "SpamProtectionLineEndings@example.test",
            "test");

         // Disallow incorrect line endings.
         SingletonProvider<TestSetup>.Instance.GetApp().Settings.AllowIncorrectLineEndings = false;

         // Send a messages to this account.

         var smtpClientSimulator = new SmtpClientSimulator();
         smtpClientSimulator.Send("SpamProtectionLineEndings@example.test", "SpamProtectionLineEndings@example.test",
            "INBOX",
            "This is a test message\r\n consisting of correct lines");

         CustomAsserts.Throws<DeliveryFailedException>(() => smtpClientSimulator.Send(
            "SpamProtectionLineEndings@example.test",
            "SpamProtectionLineEndings@example.test", "INBOX",
            "This is a test message\r consisting of incorrect lines"));


         CustomAsserts.Throws<DeliveryFailedException>(
            () => smtpClientSimulator.Send("SpamProtectionLineEndings@example.test",
               "SpamProtectionLineEndings@example.test", "INBOX",
               "This is a test message\n consisting of incorrect lines"));


         CustomAsserts.Throws<DeliveryFailedException>(() => smtpClientSimulator.Send(
            "SpamProtectionLineEndings@example.test",
            "SpamProtectionLineEndings@example.test", "INBOX",
            "This is a test message\n\r consisting of incorrect lines"));

         Pop3ClientSimulator.AssertMessageCount(account1.Address, "test", 1);
      }

      [Test]
      public void TestMaxSizeLimit()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();
         // Create a test account
         // Fetch the default domain
         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "surbltest@example.test", "test");

         // Disallow incorrect line endings.
         _antiSpam.SpamMarkThreshold = 1;
         _antiSpam.SpamDeleteThreshold = 100;
         _antiSpam.AddHeaderReason = true;
         _antiSpam.AddHeaderSpam = true;
         _antiSpam.PrependSubject = true;
         _antiSpam.PrependSubjectText = "ThisIsSpam";
         _antiSpam.MaximumMessageSize = 40;

         // Enable SURBL.
         var surblServer = _antiSpam.SURBLServers[0];
         surblServer.Active = true;
         surblServer.Score = 5;
         surblServer.Save();

         // Send a messages to this account.
         var smtpClientSimulator = new SmtpClientSimulator();

         var sb = new StringBuilder();
         var iterations = 40 * 1024 / 100 + 1;
         for (var i = 0; i < iterations; i++)
            sb.Append(
               "1234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890\r\n");

         smtpClientSimulator.Send("surbltest@example.test", "surbltest@example.test", "SURBL-No-Match",
            "This is a test message with a SURBL url: -> http://surbl-org-permanent-test-point.com/ <-\r\n" +
            sb);

         var sMessageContents = Pop3ClientSimulator.AssertGetFirstMessageText(account1.Address, "test");
         if (sMessageContents.Contains("X-hMailServer-Spam") ||
             sMessageContents.Contains("ThisIsSpam"))
            throw new Exception("Spam message etected as spam even though it's larger than max spam size.");

         surblServer.Active = false;
         surblServer.Save();
      }

      [Test]
      public void TestMaxSizeNoLimit()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();
         // Create a test account
         // Fetch the default domain

         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "surbltest@example.test", "test");

         // Disallow incorrect line endings.
         _antiSpam.SpamMarkThreshold = 1;
         _antiSpam.SpamDeleteThreshold = 100;
         _antiSpam.AddHeaderReason = true;
         _antiSpam.AddHeaderSpam = true;
         _antiSpam.PrependSubject = true;
         _antiSpam.PrependSubjectText = "ThisIsSpam";
         _antiSpam.MaximumMessageSize = 0;

         // Enable SURBL.
         var surblServer = _antiSpam.SURBLServers[0];
         surblServer.Active = true;
         surblServer.Score = 5;
         surblServer.Save();

         // Send a messages to this account.
         var smtpClientSimulator = new SmtpClientSimulator();

         var sb = new StringBuilder();
         var iterations = 40 * 1024 / 100 + 1;
         for (var i = 0; i < iterations; i++)
            sb.Append(
               "1234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890\r\n");

         smtpClientSimulator.Send("surbltest@example.test", "surbltest@example.test", "SURBL-No-Match",
            "This is a test message with a SURBL url: -> http://surbl-org-permanent-test-point.com/ <-\r\n" +
            sb);

         var sMessageContents = Pop3ClientSimulator.AssertGetFirstMessageText(account1.Address, "test");
         if (!sMessageContents.Contains("X-hMailServer-Spam") ||
             !sMessageContents.Contains("X-hMailServer-Reason") ||
             !sMessageContents.Contains("ThisIsSpam"))
            throw new Exception("Spam message not detected as spam.");

         surblServer.Active = false;
         surblServer.Save();
      }

      [Test]
      public void TestMissingMXRecord()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();

         // Create a test account
         // Fetch the default domain

         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "missingmxrecords@example.test",
            "test");

         // Disallow incorrect line endings.

         _antiSpam.SpamDeleteThreshold = 1;
         _antiSpam.UseMXChecks = true;
         _antiSpam.UseMXChecksScore = 2;

         // Send a messages to this account.
         var smtpClientSimulator = new SmtpClientSimulator();

         smtpClientSimulator.Send("test@microsoft.com", "missingmxrecords@example.test", "INBOX",
            "This is a test message.");

         CustomAsserts.Throws<DeliveryFailedException>(
            () => smtpClientSimulator.Send("test@domain_without_mx_records421dfsam430sasd.com", account1.Address,
               "INBOX",
               "This is a test message."));

         _antiSpam.UseMXChecks = false;

         Pop3ClientSimulator.AssertMessageCount(account1.Address, "test", 1);
      }

      [Test]
      public void TestMultiLineSurblImproperlyTerminaetdLine()
      {
         // Create a test account
         // Fetch the default domain

         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "surbltest@example.test", "test");

         // Disallow incorrect line endings.
         _antiSpam.SpamMarkThreshold = 1;
         _antiSpam.SpamDeleteThreshold = 100;
         _antiSpam.AddHeaderReason = true;
         _antiSpam.AddHeaderSpam = true;
         _antiSpam.PrependSubject = true;
         _antiSpam.PrependSubjectText = "ThisIsSpam";

         // Enable SURBL.
         var surblServer = _antiSpam.SURBLServers[0];
         surblServer.Active = true;
         surblServer.Score = 5;
         surblServer.Save();

         // Send a messages to this account.
         var smtpClientSimulator = new SmtpClientSimulator();

         smtpClientSimulator.Send("surbltest@example.test", "surbltest@example.test", "SURBL-Match",
            "Wrapped URL - <a href=3D\"http://surbl-org-perm\r\nanent-test-point.com\">Test</a>");

         var sMessageContents = Pop3ClientSimulator.AssertGetFirstMessageText(account1.Address, "test");
         Assert.IsFalse(sMessageContents.Contains("X-hMailServer-Spam"), "Non-spam message detected as spam");

         surblServer.Active = false;
         surblServer.Save();
      }

      [Test]
      public void TestSURBL()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();
         // Create a test account
         // Fetch the default domain

         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "surbltest@example.test", "test");

         // Disallow incorrect line endings.
         _antiSpam.SpamMarkThreshold = 1;
         _antiSpam.SpamDeleteThreshold = 100;
         _antiSpam.AddHeaderReason = true;
         _antiSpam.AddHeaderSpam = true;
         _antiSpam.PrependSubject = true;
         _antiSpam.PrependSubjectText = "ThisIsSpam";

         // Enable SURBL.
         var surblServer = _antiSpam.SURBLServers[0];
         surblServer.Active = true;
         surblServer.Score = 5;
         surblServer.Save();

         // Send a messages to this account.
         var smtpClientSimulator = new SmtpClientSimulator();


         smtpClientSimulator.Send("surbltest@example.test", "surbltest@example.test", "SURBL-No-Match",
            "This is a test message without a SURBL url.");

         var sMessageContents = Pop3ClientSimulator.AssertGetFirstMessageText(account1.Address, "test");
         if (sMessageContents.Length == 0 ||
             sMessageContents.Contains("X-hMailServer-Spam") ||
             sMessageContents.Contains("ThisIsSpam"))
            throw new Exception("Non-Spam message detected as spam");


         smtpClientSimulator.Send("surbltest@example.test", "surbltest@example.test", "SURBL-Match",
            "This is a test message with a SURBL url: -> http://surbl-org-permanent-test-point.com/ <-");

         sMessageContents = Pop3ClientSimulator.AssertGetFirstMessageText(account1.Address, "test");
         if (!sMessageContents.Contains("X-hMailServer-Spam") ||
             !sMessageContents.Contains("ThisIsSpam"))
            throw new Exception("Spam message not detected as spam");

         surblServer.Active = false;
         surblServer.Save();
      }

      [Test]
      public void TestSURBLCombinedWithSignature()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();
         // Create a test account
         // Fetch the default domain

         _domain.SignatureEnabled = true;
         _domain.SignaturePlainText = "MyDomainSignature";
         _domain.AddSignaturesToLocalMail = true;
         _domain.Save();

         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "surbltest@example.test", "test");

         // Disallow incorrect line endings.
         _antiSpam.SpamMarkThreshold = 1;
         _antiSpam.SpamDeleteThreshold = 100;
         _antiSpam.AddHeaderReason = true;
         _antiSpam.AddHeaderSpam = true;
         _antiSpam.PrependSubject = true;
         _antiSpam.PrependSubjectText = "ThisIsSpam";

         // Enable SURBL.
         var surblServer = _antiSpam.SURBLServers[0];
         surblServer.Active = true;
         surblServer.Score = 5;
         surblServer.Save();

         // Send a messages to this account.
         var smtpClientSimulator = new SmtpClientSimulator();

         smtpClientSimulator.Send("surbltest@example.test", "surbltest@example.test", "SURBL-No-Match",
            "This is a test message without a SURBL url.");

         var sMessageContents = Pop3ClientSimulator.AssertGetFirstMessageText(account1.Address, "test");
         if (sMessageContents.Length == 0 ||
             sMessageContents.Contains("X-hMailServer-Spam") ||
             sMessageContents.Contains("ThisIsSpam"))
            throw new Exception("Non-Spam message detected as spam");

         Assert.IsTrue(sMessageContents.Contains(_domain.SignaturePlainText));

         smtpClientSimulator.Send("surbltest@example.test", "surbltest@example.test", "SURBL-No-Match",
            "This is a test message with a SURBL url: -> http://surbl-org-permanent-test-point.com/ <-");

         sMessageContents = Pop3ClientSimulator.AssertGetFirstMessageText(account1.Address, "test");
         if (!sMessageContents.Contains("X-hMailServer-Spam") ||
             !sMessageContents.Contains("ThisIsSpam"))
            throw new Exception("Spam message not detected as spam");

         Assert.IsTrue(sMessageContents.Contains(_domain.SignaturePlainText));
      }

      [Test]
      public void TestSURBLCorrectNegative()
      {
         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "surbltest@example.test", "test");

         // Disallow incorrect line endings.
         _antiSpam.SpamMarkThreshold = 1;
         _antiSpam.SpamDeleteThreshold = 100;
         _antiSpam.AddHeaderReason = true;
         _antiSpam.AddHeaderSpam = true;
         _antiSpam.PrependSubject = true;
         _antiSpam.PrependSubjectText = "ThisIsSpam";

         // Enable SURBL.
         var surblServer = _antiSpam.SURBLServers[0];
         surblServer.Active = true;
         surblServer.Score = 5;
         surblServer.Save();

         // Send a messages to this account.
         SmtpClientSimulator.StaticSend("surbltest@example.test", "surbltest@example.test", "SURBL-Match",
            "This is a test message without a SURBL url: -> http://www.youtube.com/ <-");

         var sMessageContents = Pop3ClientSimulator.AssertGetFirstMessageText(account1.Address, "test");
         if (sMessageContents.Contains("X-hMailServer-Spam"))
            throw new Exception("Non-spam message detected as spam");

         surblServer.Active = false;
         surblServer.Save();
      }

      [Test]
      public void TestSURBLWithWrappedURL()
      {
         // Create a test account
         // Fetch the default domain
         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "surbltest@example.test", "test");

         // Disallow incorrect line endings.
         _antiSpam.SpamMarkThreshold = 1;
         _antiSpam.SpamDeleteThreshold = 100;
         _antiSpam.AddHeaderReason = true;
         _antiSpam.AddHeaderSpam = true;
         _antiSpam.PrependSubject = true;
         _antiSpam.PrependSubjectText = "ThisIsSpam";

         // Enable SURBL.
         var surblServer = _antiSpam.SURBLServers[0];
         surblServer.Active = true;
         surblServer.Score = 5;
         surblServer.Save();

         // Send a messages to this account.
         var smtpClientSimulator = new SmtpClientSimulator();

         smtpClientSimulator.Send("surbltest@example.test", "surbltest@example.test", "SURBL-Match",
            "Wrapped URL - <a href=3D\"http://surbl-org-perma=\r\nnent-test-point.com\">Test</a>");

         var sMessageContents = Pop3ClientSimulator.AssertGetFirstMessageText(account1.Address, "test");
         Assert.IsTrue(sMessageContents.Contains("X-hMailServer-Spam"), "Spam message not detected as spam");

         surblServer.Active = false;
         surblServer.Save();
      }

      [Test]
      public void TestSingleLineUrlFollowedByNewline()
      {
         // Create a test account
         // Fetch the default domain
         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "surbltest@example.test", "test");

         // Disallow incorrect line endings.
         _antiSpam.SpamMarkThreshold = 1;
         _antiSpam.SpamDeleteThreshold = 100;
         _antiSpam.AddHeaderReason = true;
         _antiSpam.AddHeaderSpam = true;
         _antiSpam.PrependSubject = true;
         _antiSpam.PrependSubjectText = "ThisIsSpam";

         // Enable SURBL.
         var surblServer = _antiSpam.SURBLServers[0];
         surblServer.Active = true;
         surblServer.Score = 5;
         surblServer.Save();

         // Send a messages to this account.
         var smtpClientSimulator = new SmtpClientSimulator();

         smtpClientSimulator.Send("surbltest@example.test", "surbltest@example.test", "SURBL-Match",
            "Wrapped URL - <a href=3D\"http://surbl-org-permanent-test-point.com\r\nHello\">Test</a>");

         var sMessageContents = Pop3ClientSimulator.AssertGetFirstMessageText(account1.Address, "test");
         Assert.IsTrue(sMessageContents.Contains("X-hMailServer-Spam"), "Spam message not detected as spam");

         surblServer.Active = false;
         surblServer.Save();
      }

      [Test]
      public void TestSurblMultipleAddresses()
      {
         // Create a test account
         // Fetch the default domain
         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "surbltest@example.test", "test");

         // Disallow incorrect line endings.
         _antiSpam.SpamMarkThreshold = 1;
         _antiSpam.SpamDeleteThreshold = 100;
         _antiSpam.AddHeaderReason = true;
         _antiSpam.AddHeaderSpam = true;
         _antiSpam.PrependSubject = true;
         _antiSpam.PrependSubjectText = "ThisIsSpam";

         // Enable SURBL.
         var surblServer = _antiSpam.SURBLServers[0];
         surblServer.Active = true;
         surblServer.Score = 5;
         surblServer.Save();

         // Send a messages to this account.
         var smtpClientSimulator = new SmtpClientSimulator();

         smtpClientSimulator.Send("surbltest@example.test", "surbltest@example.test", "SURBL-Match",
            "Wrapped URL - <a href=3D\"http://surbl-org-permanent-test-point.com\">Test</a>\r\nWrapped URL - <a href=3D\"http://surbl-org-permanent-test-point.com\">Test</a>\r\nWrapped URL - <a href=3D\"http://surbl-org-permanent-test-point.com\">Test</a>\r\n");

         var sMessageContents = Pop3ClientSimulator.AssertGetFirstMessageText(account1.Address, "test");
         Assert.IsTrue(sMessageContents.Contains("X-hMailServer-Spam"), "Spam message not detected as spam");

         surblServer.Active = false;
         surblServer.Save();
      }

      [Test]
      public void TestSurblAddressEndingWithSingleQuote()
      {
         LogHandler.DeleteCurrentDefaultLog();


         // Create a test account
         // Fetch the default domain
         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "surbltest@example.test", "test");

         // Disallow incorrect line endings.
         _antiSpam.SpamMarkThreshold = 1;
         _antiSpam.SpamDeleteThreshold = 100;
         _antiSpam.AddHeaderReason = true;
         _antiSpam.AddHeaderSpam = true;
         _antiSpam.PrependSubject = true;
         _antiSpam.PrependSubjectText = "ThisIsSpam";

         // Enable SURBL.
         var surblServer = _antiSpam.SURBLServers[0];
         surblServer.Active = true;
         surblServer.Score = 5;
         surblServer.Save();

         // Send a messages to this account.
         var smtpClientSimulator = new SmtpClientSimulator();

         smtpClientSimulator.Send("surbltest@example.test", "surbltest@example.test", "SURBL-Match",
            "Wrapped URL - <a href=3D'http://surbl-org-permanent-test-point.com'>Test</a>\r\n");

         surblServer.Active = false;
         surblServer.Save();

         Assert.IsFalse(LogHandler.DefaultLogContains("Lookup: surbl-org-permanent-test-point.com'.multi.surbl.org"));
         Assert.IsTrue(LogHandler.DefaultLogContains("Lookup: surbl-org-permanent-test-point.com.multi.surbl.org"));
      }

      [Test]
      public void TestSurblMultipleNegatives()
      {
         LogHandler.DeleteCurrentDefaultLog();

         // Create a test account
         // Fetch the default domain
         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "surbltest@example.test", "test");

         // Disallow incorrect line endings.
         _antiSpam.SpamMarkThreshold = 1;
         _antiSpam.SpamDeleteThreshold = 100;
         _antiSpam.AddHeaderReason = true;
         _antiSpam.AddHeaderSpam = true;
         _antiSpam.PrependSubject = true;
         _antiSpam.PrependSubjectText = "ThisIsSpam";

         // Enable SURBL.
         var surblServer = _antiSpam.SURBLServers[0];
         surblServer.Active = true;
         surblServer.Score = 5;
         surblServer.Save();

         // Send a messages to this account.
         var smtpClientSimulator = new SmtpClientSimulator();

         smtpClientSimulator.Send("surbltest@example.test", "surbltest@example.test", "SURBL-Match",
            "Wrapped URL - <a href=3D\"http://test.example1fdafdsfds.com\">Test</a>\r\nWrapped URL - <a href=3D\"http://test.example2fdafdsfds.com\">Test</a>\r\nWrapped URL - <a href=3D\"http://test.example3fdafdsfds.com\">Test</a>\r\n");

         var sMessageContents = Pop3ClientSimulator.AssertGetFirstMessageText(account1.Address, "test");
         Assert.IsFalse(sMessageContents.Contains("X-hMailServer-Spam"), "Spam message not detected as spam");

         surblServer.Active = false;
         surblServer.Save();

         Assert.IsTrue(LogHandler.DefaultLogContains("SURBL: Lookup: example1fdafdsfds.com.multi.surbl.org"));
         Assert.IsTrue(LogHandler.DefaultLogContains("SURBL: Lookup: example2fdafdsfds.com.multi.surbl.org"));
         Assert.IsTrue(LogHandler.DefaultLogContains("SURBL: Lookup: example3fdafdsfds.com.multi.surbl.org"));
      }

      [Test]
      public void SurblTestRealWorldBody1()
      {
         LogHandler.DeleteCurrentDefaultLog();

         // Create a test account
         // Fetch the default domain
         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "surbltest@example.test", "test");

         // Disallow incorrect line endings.
         _antiSpam.SpamMarkThreshold = 1;
         _antiSpam.SpamDeleteThreshold = 100;
         _antiSpam.AddHeaderReason = true;
         _antiSpam.AddHeaderSpam = true;
         _antiSpam.PrependSubject = true;
         _antiSpam.PrependSubjectText = "ThisIsSpam";

         // Enable SURBL.
         var surblServer = _antiSpam.SURBLServers[0];
         surblServer.Active = true;
         surblServer.Score = 5;
         surblServer.Save();

         // Send a messages to this account.
         var smtpClientSimulator = new SmtpClientSimulator();

         smtpClientSimulator.Send("surbltest@example.test", "surbltest@example.test", "SURBL-Match",
            TestResources.SecuniaBody1);

         var sMessageContents = Pop3ClientSimulator.AssertGetFirstMessageText(account1.Address, "test");
         Assert.IsFalse(sMessageContents.Contains("X-hMailServer-Spam"), "Spam message not detected as spam");

         surblServer.Active = false;
         surblServer.Save();


         Assert.IsTrue(LogHandler.DefaultLogContains("SURBL: 3 unique domain addresses found."));
         Assert.IsTrue(LogHandler.DefaultLogContains("Found URL: secunia.com"));
         Assert.IsTrue(LogHandler.DefaultLogContains("SURBL: Found URL: secunia.com"));
         Assert.IsTrue(LogHandler.DefaultLogContains("SURBL: Found URL: ca.secunia.com"));
         Assert.IsTrue(LogHandler.DefaultLogContains("SURBL: Found URL: ubuntu.com"));

         Assert.IsTrue(LogHandler.DefaultLogContains("SURBL: Lookup: secunia.com.multi.surbl.org"));
         Assert.IsTrue(LogHandler.DefaultLogContains("SURBL: Lookup: ca.secunia.com.multi.surbl.org"));
         Assert.IsTrue(LogHandler.DefaultLogContains("SURBL: Lookup: ubuntu.com.multi.surbl.org"));
      }

      [Test]
      [Description(
         "Every enabled spam test must still run when neither threshold is set. Scoring off with " +
         "greylisting on is a supported configuration, but the pipeline's early exit compared the " +
         "running score against max(mark, delete) - and 0 >= 0 is true before any test has run, so " +
         "only the first enabled test ever ran. That produced no visible scoring difference, because " +
         "neither action is armed, but PerformGreyListing looks for the SPF result in that same " +
         "result set to honour BypassGreyListingOnSPFSuccess, and SPF is the fifth test in the order.")]
      public void TestAllTestsRunWhenNeitherThresholdIsSet()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "nothreshold@example.test", "test");

         // Both actions disabled: nothing can be marked and nothing can be deleted.
         _antiSpam.SpamMarkThreshold = 0;
         _antiSpam.SpamDeleteThreshold = 0;

         // Two pre-transmission tests, first and fifth in the pipeline order. What
         // is being observed is that the fifth one still runs; neither has to
         // produce a score, and the HELO check does not even apply to a loopback
         // client - the runner logs each test it ran either way.
         _antiSpam.CheckHostInHelo = true;
         _antiSpam.CheckHostInHeloScore = 3;
         _antiSpam.UseSPF = true;
         _antiSpam.UseSPFScore = 3;

         SmtpClientSimulator.StaticSend("test@example.com", account.Address, "Test", "TestBody");

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         Assert.IsTrue(LogHandler.DefaultLogContains("Spam test: SpamTestHeloHost"),
            LogHandler.ReadCurrentDefaultLog());
         Assert.IsTrue(LogHandler.DefaultLogContains("Spam test: SpamTestSPF"),
            LogHandler.ReadCurrentDefaultLog());
      }
   }
}