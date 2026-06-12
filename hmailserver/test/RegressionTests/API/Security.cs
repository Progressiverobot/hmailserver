// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System.Runtime.InteropServices;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.API
{
   [TestFixture]
   public class Security : TestFixtureBase
   {
      [Test]
      public void TestDomainAdminAccessBackupManager()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "user@example.test", "test");
         account.AdminLevel = eAdminLevel.hAdminLevelDomainAdmin;
         account.Save();

         var newApplication = new Application();
         newApplication.Authenticate("user@example.test", "test");
         var ex = Assert.Throws<COMException>(() =>
         {
            var v = newApplication.BackupManager;
         });

         StringAssert.Contains("You do not have access to this property / method.", ex.Message);
      }

      [Test]
      public void TestDomainAdminAccessDatabase()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "user@example.test", "test");
         account.AdminLevel = eAdminLevel.hAdminLevelDomainAdmin;
         account.Save();

         var newApplication = new Application();
         newApplication.Authenticate("user@example.test", "test");
         var database = newApplication.Database;
         var ex = Assert.Throws<COMException>(() => database.ExecuteSQL("select"));
         StringAssert.Contains("You do not have access to this property / method.", ex.Message);
      }

      [Test]
      public void TestDomainAdminAccessOtherDomain()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "user@example.test", "test");
         account.AdminLevel = eAdminLevel.hAdminLevelDomainAdmin;
         account.Save();

         SingletonProvider<TestSetup>.Instance.AddDomain("example.com");

         var newApplication = new Application();
         newApplication.Authenticate("user@example.test", "test");
         Assert.AreEqual(1, newApplication.Domains.Count);

         var domains = SingletonProvider<TestSetup>.Instance.GetApp().Domains;
         Assert.AreEqual(2, domains.Count);

         try
         {
            var secondDomain = newApplication.Domains.get_ItemByName("example.com");
            Assert.Fail("Was able to access other domain.");
         }
         catch (COMException ex)
         {
            Assert.IsTrue(ex.Message.Contains("Invalid index."));
         }
      }

      [Test]
      public void TestDomainAdminAccessSettings()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "user@example.test", "test");
         account.AdminLevel = eAdminLevel.hAdminLevelDomainAdmin;
         account.Save();

         var newApplication = new Application();
         newApplication.Authenticate("user@example.test", "test");
         var ex = Assert.Throws<COMException>(() =>
         {
            var settings = newApplication.Settings;
         });

         StringAssert.Contains("You do not have access to this property / method.", ex.Message);
      }

      [Test]
      public void TestNormalUserAccessBackupManager()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "user@example.test", "test");
         account.AdminLevel = eAdminLevel.hAdminLevelNormal;
         account.Save();

         var newApplication = new Application();
         newApplication.Authenticate("user@example.test", "test");
         var ex = Assert.Throws<COMException>(() =>
         {
            var backupManager = newApplication.BackupManager;
         });
         StringAssert.Contains("You do not have access to this property / method.", ex.Message);
      }

      [Test]
      public void TestNormalUserAccessDatabase()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "user@example.test", "test");
         account.AdminLevel = eAdminLevel.hAdminLevelNormal;
         account.Save();

         var newApplication = new Application();
         newApplication.Authenticate("user@example.test", "test");
         var database = newApplication.Database;

         var ex = Assert.Throws<COMException>(() => database.ExecuteSQL("select"));
         StringAssert.Contains("You do not have access to this property / method.", ex.Message);
      }

      [Test]
      public void TestNormalUserAccessOtherAccount()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "user@example.test", "test");
         account.AdminLevel = eAdminLevel.hAdminLevelNormal;
         account.Save();

         var secondAccount = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "second@example.test", "test");
         secondAccount.AdminLevel = eAdminLevel.hAdminLevelNormal;
         secondAccount.Save();

         var newApplication = new Application();
         newApplication.Authenticate("user@example.test", "test");
         Assert.AreEqual(1, newApplication.Domains.Count);
         Assert.AreEqual(1, newApplication.Domains[0].Accounts.Count);

         var myAccount = newApplication.Domains[0].Accounts.get_ItemByAddress("user@example.test");

         try
         {
            var otherAccount = newApplication.Domains[0].Accounts.get_ItemByAddress("second@example.test");

            Assert.Fail();
         }
         catch (COMException ex)
         {
            Assert.IsTrue(ex.Message.Contains("Invalid index."));
         }

         var domains = SingletonProvider<TestSetup>.Instance.GetApp().Domains;
         Assert.AreEqual(2, domains[0].Accounts.Count);
      }

      [Test]
      public void TestNormalUserAccessOtherDomain()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "user@example.test", "test");
         account.AdminLevel = eAdminLevel.hAdminLevelNormal;
         account.Save();

         SingletonProvider<TestSetup>.Instance.AddDomain("example.com");

         var newApplication = new Application();
         newApplication.Authenticate("user@example.test", "test");
         Assert.AreEqual(1, newApplication.Domains.Count);

         var domains = SingletonProvider<TestSetup>.Instance.GetApp().Domains;
         Assert.AreEqual(2, domains.Count);

         try
         {
            var secondDomain = newApplication.Domains.get_ItemByName("example.com");
            Assert.Fail();
         }
         catch (COMException ex)
         {
            Assert.IsTrue(ex.Message.Contains("Invalid index."));
         }
      }

      [Test]
      public void TestNormalUserAccessSettings()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "user@example.test", "test");
         account.AdminLevel = eAdminLevel.hAdminLevelNormal;
         account.Save();

         var newApplication = new Application();
         newApplication.Authenticate("user@example.test", "test");
         var ex = Assert.Throws<COMException>(() =>
         {
            var settings = newApplication.Settings;
         });
         StringAssert.Contains("You do not have access to this property / method.", ex.Message);
      }
   }
}