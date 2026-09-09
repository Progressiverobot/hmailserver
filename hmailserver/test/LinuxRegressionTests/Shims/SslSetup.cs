// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Security.Cryptography.X509Certificates;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.SSL
{
   /// <summary>
   ///    The Windows SslSetup adds twelve TLS listeners and a certificate to the server
   ///    over COM and restarts it; the REST API can do none of that, and the server
   ///    this project runs against has no certificate. The half that does not touch
   ///    the server - the test certificate the simulated servers present - is the
   ///    same code as the Windows one, because SmtpServerSimulator needs it whether
   ///    or not a test then gets as far as talking to the real server.
   /// </summary>
   public class SslSetup
   {
      public static void SetupSSLPorts(Application application, SslVersions sslVersions = null)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoTlsListener);
      }

      public static string GetSslCertPath()
      {
         return Paths.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "SSL examples");
      }

      private static string GetCertificatePfx()
      {
         return Paths.Combine(GetSslCertPath(), "localhost.pfx");
      }

      public static X509Certificate2 GetCertificate()
      {
         var pfxPath = GetCertificatePfx();

         Console.WriteLine("Using certificate: " + pfxPath);
         return X509CertificateLoader.LoadPkcs12FromFile(pfxPath, "Secret1");
      }
   }
}
