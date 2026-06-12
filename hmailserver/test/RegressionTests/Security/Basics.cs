// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System.Security.Authentication;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.Security
{
   [TestFixture]
   public class Basics : TestFixtureBase
   {
      [Test]
      public void TestEmptyPassword()
      {
         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "");

         string message;
         var sim = new Pop3ClientSimulator();
         Assert.IsFalse(sim.ConnectAndLogon(account1.Address, "", out message));


         var simIMAP = new ImapClientSimulator();
         Assert.IsFalse(simIMAP.ConnectAndLogon(account1.Address, "", out message));
         Assert.AreEqual("A01 NO Invalid user name or password.\r\n", message);

         var simSMTP = new SmtpClientSimulator();
         CustomAsserts.Throws<AuthenticationException>(() =>
            simSMTP.ConnectAndLogon("dGVzdEB0ZXN0LmNvbQ==", "", out message));
         Assert.AreEqual("535 Authentication failed. Restarting authentication process.\r\n", message);
      }
   }
}