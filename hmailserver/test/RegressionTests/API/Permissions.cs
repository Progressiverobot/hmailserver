using System.Runtime.InteropServices;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.API
{
   public class Permissions : TestFixtureBase
   {
      [Test]
      [Description("Issue 303, Domain administrators should not have permission to add new domains.")]
      public void DomainAdminShouldNotBeAbleToAddDomain()
      {
         var domain = SingletonProvider<TestSetup>.Instance.AddTestDomain();

         // Create an account with normal privileges.
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(domain, "test@example.test", "test");
         account.AdminLevel = eAdminLevel.hAdminLevelDomainAdmin;
         account.Save();

         var newApp = new Application();
         var authenticated = newApp.Authenticate(account.Address, "test");
         Assert.IsNotNull(authenticated);

         // This should throw an exception.
         var ex = Assert.Throws<COMException>(() => newApp.Domains.Add());
         StringAssert.Contains("You do not have access to this property / method.", ex.Message);
      }

      [Test]
      [Description("Issue 303, Normal users should not have permission to add new domains.")]
      public void NormalUserShouldNotBeAbleToAddDomain()
      {
         var domain = SingletonProvider<TestSetup>.Instance.AddTestDomain();

         // Create an account with normal privileges.
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(domain, "test@example.test", "test");

         var newApp = new Application();
         var authenticated = newApp.Authenticate(account.Address, "test");
         Assert.IsNotNull(authenticated);

         // This should throw an exception.
         var ex = Assert.Throws<COMException>(() => newApp.Domains.Add());
         StringAssert.Contains("You do not have access to this property / method.", ex.Message);
      }

      [Test]
      [Description("Issue 303, Server admin should be allowed to add domain.")]
      public void ServerAdminShouldBeAbleToAddDomain()
      {
         var domain = SingletonProvider<TestSetup>.Instance.AddTestDomain();

         // Create an account with normal privileges.
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(domain, "test@example.test", "test");
         account.AdminLevel = eAdminLevel.hAdminLevelServerAdmin;
         account.Save();

         var newApp = new Application();
         var authenticated = newApp.Authenticate(account.Address, "test");
         Assert.IsNotNull(authenticated);

         // This should throw an exception.
         var newDomain = newApp.Domains.Add();
      }

      [Test]
      public void DomainAdminShouldBeAbleToSaveDomain()
      {
         var domain = SingletonProvider<TestSetup>.Instance.AddTestDomain();

         // Create an account with normal privileges.
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(domain, "test@example.test", "test");
         account.AdminLevel = eAdminLevel.hAdminLevelDomainAdmin;
         account.Save();

         var newApp = new Application();
         var authenticated = newApp.Authenticate(account.Address, "test");
         Assert.IsNotNull(authenticated);

         Assert.AreEqual(1, newApp.Domains.Count);

         // Retrieve our domain.
         var newDomain = newApp.Domains[0];
         newDomain.Save();
      }

      [Test]
      public void UserShouldNotBeAbleToSaveDomain()
      {
         var domain = SingletonProvider<TestSetup>.Instance.AddTestDomain();

         // Create an account with normal privileges.
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(domain, "test@example.test", "test");
         account.AdminLevel = eAdminLevel.hAdminLevelNormal;
         account.Save();

         var newApp = new Application();
         var authenticated = newApp.Authenticate(account.Address, "test");
         Assert.IsNotNull(authenticated);

         Assert.AreEqual(1, newApp.Domains.Count);

         // Retrieve our domain.
         var newDomain = newApp.Domains[0];
         var ex = Assert.Throws<COMException>(() => newDomain.Save());
         StringAssert.Contains("You do not have access to this property / method.", ex.Message);
      }
   }
}