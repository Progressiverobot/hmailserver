// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Runtime.InteropServices;
using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure.Persistence
{
   [TestFixture]
   public class TCPIPPortTests : TestFixtureBase
   {
      [Test]
      public void CertificateIsRequiredForSSL()
      {
         var settings = SingletonProvider<TestSetup>.Instance.GetApp().Settings;
         var port = settings.TCPIPPorts[0];

         if (port.SSLCertificateID > 0)
            Assert.Inconclusive("Test cannot run using port with SSL cert.");

         port.ConnectionSecurity = eConnectionSecurity.eCSTLS;

         var ex = Assert.Throws<COMException>(() => port.Save());
         StringAssert.Contains("Certificate must be specified.", ex.Message);
      }

      [Test]
      public void CertificateIsRequiredForStartTLSOptional()
      {
         var settings = SingletonProvider<TestSetup>.Instance.GetApp().Settings;
         var port = settings.TCPIPPorts[0];

         if (port.SSLCertificateID > 0)
            Assert.Inconclusive("Test cannot run using port with SSL cert.");

         port.ConnectionSecurity = eConnectionSecurity.eCSSTARTTLSOptional;

         var ex = Assert.Throws<COMException>(() => port.Save());
         StringAssert.Contains("Certificate must be specified.", ex.Message);
      }

      [Test]
      public void CertificateIsRequiredForStartTLSRequired()
      {
         var settings = SingletonProvider<TestSetup>.Instance.GetApp().Settings;
         var port = settings.TCPIPPorts[0];

         if (port.SSLCertificateID > 0)
            Assert.Inconclusive("Test cannot run using port with SSL cert.");

         port.ConnectionSecurity = eConnectionSecurity.eCSSTARTTLSRequired;

         var ex = Assert.Throws<COMException>(() => port.Save());
         StringAssert.Contains("Certificate must be specified.", ex.Message);
      }
   }
}