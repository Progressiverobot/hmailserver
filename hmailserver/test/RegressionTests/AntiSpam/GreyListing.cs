// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System.Collections.Generic;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.AntiSpam
{
   [TestFixture]
   public class GreyListing : TestFixtureBase
   {
      [SetUp]
      public new void SetUp()
      {
         _antiSpam = _settings.AntiSpam;
      }

      private hMailServer.AntiSpam _antiSpam;

      [Test]
      [Description("Test that grey listing can be enabled if message arrives from A or MX record.")]
      public void ItShouldBePossibleToBypassGreylistingOnMessagesArrivingFromMXorA()
      {
         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "grey@example.test", "test");

         _antiSpam.GreyListingEnabled = true;

         CustomAsserts.Throws<DeliveryFailedException>(
            () => SmtpClientSimulator.StaticSend("test@localhost.hmailserver.com", account1.Address, "Test",
               "Body"));

         _antiSpam.BypassGreylistingOnMailFromMX = true;

         SmtpClientSimulator.StaticSend("test@localhost.hmailserver.com", account1.Address, "Test",
            "Body");

         Pop3ClientSimulator.AssertGetFirstMessageText(account1.Address, "test");
      }

      [Test]
      public void TestGreyListing()
      {
         _antiSpam.GreyListingEnabled = false;

         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "grey@example.test", "test");

         var smtp = new SmtpClientSimulator();
         var recipients = new List<string>();
         recipients.Add(account1.Address);
         smtp.Send("test@example.test", recipients, "Test", "Body");

         Pop3ClientSimulator.AssertGetFirstMessageText(account1.Address, "test");

         _antiSpam.GreyListingEnabled = true;

         CustomAsserts.Throws<DeliveryFailedException>(() =>
            smtp.Send("test@example.test", recipients, "Test", "Body"));


         _antiSpam.GreyListingEnabled = false;

         smtp.Send("test@example.test", recipients, "Test", "Body");

         Pop3ClientSimulator.AssertGetFirstMessageText(account1.Address, "test");
      }

      [Test]
      public void TestGreyListingWhiteListWildcard()
      {
         _antiSpam.GreyListingEnabled = true;

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "grey@example.test", "test");

         var whiteAddresses = _antiSpam.GreyListingWhiteAddresses;
         var whiteAddress = whiteAddresses.Add();
         whiteAddress.IPAddress = "127.0.0.5";
         whiteAddress.Save();

         CustomAsserts.Throws<DeliveryFailedException>(() =>
            SmtpClientSimulator.StaticSend("external@example.com", account.Address, "Test", "Test"));

         whiteAddress.IPAddress = "*";
         whiteAddress.Save();

         SmtpClientSimulator.StaticSend("external@example.com", account.Address, "Test", "Test");

         Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");
      }

      [Test]
      public void TestGreyListingWithDomainAliases()
      {
         _antiSpam.GreyListingEnabled = false;

         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "grey@example.test", "test");

         var smtp = new SmtpClientSimulator();
         var recipients = new List<string>();
         recipients.Add(account1.Address);
         smtp.Send("test@example.test", recipients, "Test", "Body");

         Pop3ClientSimulator.AssertGetFirstMessageText(account1.Address, "test");

         _antiSpam.GreyListingEnabled = true;

         CustomAsserts.Throws<DeliveryFailedException>(() =>
            smtp.Send("test@example.test", recipients, "Test", "Body"));


         _domain.AntiSpamEnableGreylisting = false;
         _domain.Save();

         smtp.Send("test@example.test", recipients, "Test", "Body");

         Pop3ClientSimulator.AssertGetFirstMessageText(account1.Address, "test");

         var da = _domain.DomainAliases.Add();
         da.AliasName = "test2.com";
         da.Save();

         recipients = new List<string>();
         recipients.Add("grey@test2.com");

         smtp.Send("test@example.test", recipients, "Test", "Body");
         Pop3ClientSimulator.AssertGetFirstMessageText(account1.Address, "test");

         _domain.AntiSpamEnableGreylisting = true;
         _domain.Save();

         CustomAsserts.Throws<DeliveryFailedException>(() =>
            smtp.Send("test@example.test", recipients, "Test", "Body"));
      }
   }
}