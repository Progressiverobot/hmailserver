// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

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

         CustomAsserts.AssertReportedError("The private key file has a password. hMailServer does not support this.",
            "Failed to load private key file.");

         SingletonProvider<TestSetup>.Instance.PerformBasicSetup();
      }
   }
}