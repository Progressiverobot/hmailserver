// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using hMailServer;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   [TestFixture]
   public class DnsResolution : TestFixtureBase
   {
      [OneTimeSetUp]
      public void ServeTheZoneTheseLookupsNeed()
      {
         // hmailserver.com's live zone has exactly this shape - an MX, and a name that
         // is a CNAME to the domain, which the resolver must follow to reach the MX.
         // Served locally so that the test is of CNAME following, not of whether
         // somebody else's DNS answered in time.
         SuiteDns.Zone
            .WithMx("hmailserver.com", 10, "mail.hmailserver.test")
            .WithA("mail.hmailserver.test", "192.0.2.25")
            .WithCname("cname-test.hmailserver.com", "hmailserver.com");
      }

      [OneTimeTearDown]
      public void ForgetIt()
      {
         SuiteDns.Reset();
      }

      [OneTimeSetUp]
      public void OneTimeSetUp()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();
         _utilities = application.Utilities;
      }

      private Utilities _utilities;

      [Test]
      public void CNameRecordsShouldBeFollowed()
      {
         // If a MX record contains a CNAME record, the CNAME record should be followed.
         // According to RFC, a server owner should not add a CNAME record to MX record,
         // but many do and hMailServer has supported this historically.
         var actualServer = _utilities.GetMailServer("example@cname-test.hmailserver.com");
         var expectedServer = _utilities.GetMailServer("example@hmailserver.com");

         Assert.AreEqual(expectedServer, actualServer);
      }

      [Test]
      public void NoneExistentRecordsShouldNotResolve()
      {
         // If a MX record contains a CNAME record, the CNAME record should be followed.
         // According to RFC, a server owner should not add a CNAME record to MX record,
         // but many do and hMailServer has supported this historically.
         var actualServer = _utilities.GetMailServer("example@invalid.hmailserver.com");

         Assert.AreEqual("", actualServer);
      }
   }
}