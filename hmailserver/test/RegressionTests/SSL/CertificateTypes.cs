// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.IO;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.SSL
{
   [TestFixture]
   public class CertificateTypes : TestFixtureBase
   {
      [Test]
      [Description("Test that loading a private key with password does not hang")]
      public void SetupSSLCertificateWithPassword()
      {
         var sslPath = Path.Combine(SslSetup.GetSslCertPath(), "WithPassword");

         var sslCertificate = _application.Settings.SSLCertificates.Add();
         sslCertificate.Name = "Example";
         sslCertificate.CertificateFile = sslPath + "\\server.crt";
         sslCertificate.PrivateKeyFile = sslPath + "\\server.key";
         sslCertificate.Save();

         var port = _application.Settings.TCPIPPorts.Add();
         port.Address = "0.0.0.0";
         port.PortNumber = 251;
         port.UseSSL = true;
         port.SSLCertificateID = sslCertificate.ID;
         port.Protocol = eSessionType.eSTSMTP;
         port.Save();

         _application.Stop();
         _application.Start();

         // The message changed because the limitation it described was removed. An
         // encrypted key with NO passphrase configured still fails to load and the
         // listener still does not come up - which is what this test pins - but the
         // server no longer says the feature is unsupported. It names the property to
         // set instead (SSLCertificate.PrivateKeyPassword), because it now works.
         CustomAsserts.AssertReportedError("no passphrase is configured for the certificate",
            "Failed to load private key file.");

         SingletonProvider<TestSetup>.Instance.PerformBasicSetup();
      }
   }
}