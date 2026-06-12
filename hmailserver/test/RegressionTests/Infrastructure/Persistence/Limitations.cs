// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure.Persistence
{
   [TestFixture]
   public class Limitations : TestFixtureBase
   {
      [Test]
      public void TestDomainAliasRenameToSameNameAsDomain()
      {
         var domains = SingletonProvider<TestSetup>.Instance.GetApp().Domains;

         var da = _domain.DomainAliases.Add();
         da.AliasName = "test2.com";
         da.Save();

         try
         {
            da.AliasName = "example.test";
            da.Save();
         }
         catch (Exception)
         {
            return;
         }

         Assert.Fail("Domain alias with same name as domain was permitted.");
      }

      [Test]
      public void TestDomainAliasWithSameNameAsDomain()
      {
         var domains = SingletonProvider<TestSetup>.Instance.GetApp().Domains;

         var da = _domain.DomainAliases.Add();
         da.AliasName = "example.test";

         try
         {
            da.Save();
         }
         catch (Exception)
         {
            return;
         }

         Assert.Fail("Domain alias with same name as domain was permitted.");
      }

      [Test]
      public void TestDomainLimitAccountMaxSizeTest1()
      {
         _domain.MaxAccountSize = 25;
         _domain.Save();

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test1@example.test", "secret1", 1);
         account.Save();
      }

      [Test]
      public void TestDomainLimitAccountMaxSizeTest2()
      {
         _domain.MaxAccountSize = 25;
         _domain.Save();

         try
         {
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test1@example.test", "secret1", 32);
         }
         catch (Exception)
         {
            return;
         }

         Assert.Fail("Max account size limit exceeded.");
      }

      [Test]
      public void TestDomainLimitAccountMaxSizeTest3()
      {
         _domain.MaxAccountSize = 25;
         _domain.Save();

         try
         {
            var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test1@example.test", "secret1",
               0);
         }
         catch (Exception)
         {
            return;
         }

         Assert.Fail("Max account size limit exceeded (max size = 0)");
      }

      [Test]
      public void TestDomainLimitNumberOfAccounts()
      {
         _domain.MaxNumberOfAccountsEnabled = true;
         _domain.MaxNumberOfAccounts = 3;
         _domain.Save();

         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test1@example.test", "secret1");
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test2@example.test", "secret1");
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test3@example.test", "secret1");

         try
         {
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test4@example.test", "secret1");
         }
         catch (Exception)
         {
            return;
         }

         Assert.Fail("Number of accounts exceeded max no of accounts");
      }

      [Test]
      public void TestDomainLimitNumberOfAliases()
      {
         _domain.MaxNumberOfAliasesEnabled = true;
         _domain.MaxNumberOfAliases = 4;
         _domain.Save();

         SingletonProvider<TestSetup>.Instance.AddAlias(_domain, "test1@example.test", "test@example.test");
         SingletonProvider<TestSetup>.Instance.AddAlias(_domain, "test2@example.test", "test@example.test");
         SingletonProvider<TestSetup>.Instance.AddAlias(_domain, "test3@example.test", "test@example.test");
         SingletonProvider<TestSetup>.Instance.AddAlias(_domain, "test4@example.test", "test@example.test");

         try
         {
            SingletonProvider<TestSetup>.Instance.AddAlias(_domain, "test5@example.test", "test@example.test");
         }
         catch (Exception)
         {
            return;
         }

         Assert.Fail("Number of aliases exceeded max no of accounts");
      }

      [Test]
      public void TestDomainLimitNumberOfDistributionLists()
      {
         _domain.MaxNumberOfDistributionListsEnabled = true;
         _domain.MaxNumberOfDistributionLists = 2;
         _domain.Save();

         var recipients = new List<string>();
         SingletonProvider<TestSetup>.Instance.AddDistributionList(_domain, "test1@example.test", recipients);
         SingletonProvider<TestSetup>.Instance.AddDistributionList(_domain, "test2@example.test", recipients);

         try
         {
            SingletonProvider<TestSetup>.Instance.AddDistributionList(_domain, "test3@example.test", recipients);
         }
         catch (Exception)
         {
            return;
         }

         Assert.Fail("Number of aliases exceeded max no of accounts");
      }

      [Test]
      public void TestDomainMaxMessageSizeLimit()
      {
         _domain.MaxMessageSize = 0;
         _domain.Save();

         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "secret1", 0);
         var message = new StringBuilder();

         for (var i = 0; i < 10000; i++) message.Append("ABCDEFGH");

         SmtpClientSimulator.StaticSend("test@example.test", "test@example.test", "TestSubject",
            message.ToString());
         Pop3ClientSimulator.AssertMessageCount("test@example.test", "secret1", 1);
         _domain.MaxMessageSize = 50;
         _domain.Save();

         CustomAsserts.Throws<DeliveryFailedException>(() => SmtpClientSimulator.StaticSend("test@example.test",
            "test@example.test", "TestSubject",
            message.ToString()));
      }


      [Test]
      public void TestDomainMaxSizeLimit()
      {
         _domain.MaxSize = 30;
         _domain.Save();

         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test1@example.test", "secret1", 10);
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test2@example.test", "secret1", 10);
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test3@example.test", "secret1", 10);


         try
         {
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test4@example.test", "secret1", 10);
         }
         catch (Exception)
         {
            return;
         }

         Assert.Fail("Max domain size limit exceeded.");
      }

      [Test]
      public void TestDomainMaxSizeLimitZeroSizeAccount()
      {
         _domain.MaxSize = 30;
         _domain.Save();

         try
         {
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test1@example.test", "secret1", 0);
         }
         catch (Exception)
         {
            return;
         }

         Assert.Fail("Max domain size limit exceeded.");
      }

      [Test]
      public void TestDomainNameDuplicate()
      {
         var domains = SingletonProvider<TestSetup>.Instance.GetApp().Domains;
         SingletonProvider<TestSetup>.Instance.AddDomain(domains, "test2.com");

         try
         {
            SingletonProvider<TestSetup>.Instance.AddDomain(domains, "test2.com");
         }
         catch (Exception)
         {
            return;
         }

         Assert.Fail("Duplicate domain name was permitted.");
      }


      [Test]
      public void TestDomainNameDuplicateDomainRename()
      {
         var domains = SingletonProvider<TestSetup>.Instance.GetApp().Domains;
         var domain = SingletonProvider<TestSetup>.Instance.AddDomain(domains, "test2.com");

         domain.Name = "example.test";

         try
         {
            domain.Save();
         }
         catch (Exception)
         {
            return;
         }

         Assert.Fail("Duplicate domain name was permitted.");
      }

      [Test]
      public void TestDomainWithSameNameAsDomainAlias()
      {
         var domains = SingletonProvider<TestSetup>.Instance.GetApp().Domains;

         var da = _domain.DomainAliases.Add();
         da.AliasName = "test2.com";
         da.Save();

         try
         {
            SingletonProvider<TestSetup>.Instance.AddDomain(domains, "test2.com");
         }
         catch (Exception)
         {
            return;
         }

         Assert.Fail("Domain with same name as domain alias was permitted.");
      }

      [Test]
      [Description("Issue 195, Creating two SMTP routes allowed for same domain.")]
      public void TestDuplicateRoutes()
      {
         var app = SingletonProvider<TestSetup>.Instance.GetApp();
         var routes = app.Settings.Routes;

         var route = routes.Add();
         route.DomainName = "example.test";
         route.Save();

         var route2 = routes.Add();
         route2.DomainName = "example.test";

         var ex = Assert.Throws<COMException>(() => route2.Save());
         StringAssert.Contains("Another route with this name already exists.", ex.Message);
      }

      [Test]
      public void TestNameDuplicateAccount()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "secret1");

         try
         {
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "secret2");
         }
         catch (Exception)
         {
            return;
         }

         Assert.Fail("Duplicate account was permitted");
      }

      [Test]
      public void TestNameDuplicateAlias()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "secret1");

         try
         {
            SingletonProvider<TestSetup>.Instance.AddAlias(_domain, "test@example.test", "secret2");
         }
         catch (Exception)
         {
            return;
         }

         Assert.Fail("Alias with same name as account was permitted.");
      }

      [Test]
      public void TestNameDuplicateDistributionList()
      {
         SingletonProvider<TestSetup>.Instance.AddAlias(_domain, "test@example.test", "test2@example.test");

         var recipients = new List<string>();

         try
         {
            SingletonProvider<TestSetup>.Instance.AddDistributionList(_domain, "test@example.test", recipients);
         }
         catch (Exception)
         {
            return;
         }

         Assert.Fail("Distribution list with same name as alias was permitted.");
      }

      [Test]
      public void TestNameDuplicateGroup()
      {
         SingletonProvider<TestSetup>.Instance.AddGroup("test@example.test");

         try
         {
            SingletonProvider<TestSetup>.Instance.AddGroup("test@example.test");
         }
         catch (Exception)
         {
            return;
         }

         Assert.Fail("Two groups with same name was permitted.");
      }

      [Test]
      public void TestSaveInvalidIPRange()
      {
         var app = SingletonProvider<TestSetup>.Instance.GetApp();
         var range = app.Settings.SecurityRanges.Add();

         range.Name = "Test";
         range.LowerIP = "1.1.1.1";
         range.UpperIP = "0.0.0.0";
         var ex = Assert.Throws<COMException>(() => range.Save());
         StringAssert.Contains("The lower IP address must be lower or the same as the upper IP address.", ex.Message);
      }


      [Test]
      public void TestSaveValidIPRange()
      {
         var app = SingletonProvider<TestSetup>.Instance.GetApp();
         var range = app.Settings.SecurityRanges.Add();

         range.Name = "Test";
         range.LowerIP = "0.0.0.0";
         range.UpperIP = "0.0.0.1";
         range.Save();
      }
   }
}