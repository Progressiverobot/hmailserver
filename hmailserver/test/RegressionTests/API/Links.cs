// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System.Collections.Generic;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.API
{
   [TestFixture]
   public class Links : TestFixtureBase
   {
      [SetUp]
      public new void SetUp()
      {
         _links = _application.Links;
      }

      private hMailServer.Links _links;

      [Test]
      public void TestAccountLink()
      {
         var account =
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         Assert.AreEqual(account.Address, _links.get_Account(account.ID).Address);
      }

      [Test]
      public void TestAliasLink()
      {
         var alias =
            SingletonProvider<TestSetup>.Instance.AddAlias(_domain, "mytest@example.test", "test2");

         Assert.AreEqual(alias.Value, _links.get_Alias(alias.ID).Value);
      }

      [Test]
      public void TestDistributionListLink()
      {
         var recipients = new List<string>();
         recipients.Add("Vaffe");
         recipients.Add("Baffe");
         recipients.Add("Laffe");

         var list =
            SingletonProvider<TestSetup>.Instance.AddDistributionList(_domain, "mytest@example.test", recipients);

         Assert.AreEqual(recipients.Count, _links.get_DistributionList(list.ID).Recipients.Count);
      }

      [Test]
      public void TestDomainLink()
      {
         Assert.AreEqual(_domain.Name, _links.get_Domain(_domain.ID).Name);
      }
   }
}