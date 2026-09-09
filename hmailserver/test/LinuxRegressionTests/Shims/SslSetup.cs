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
      // The twelve listeners the Windows SslSetup adds through COM, by port
      // and security, each protocol in turn.
      private static readonly (int Port, eConnectionSecurity Security, string Protocol)[] SslPorts =
      {
         (25000, eConnectionSecurity.eCSNone, "smtp"), (11000, eConnectionSecurity.eCSNone, "pop3"), (14300, eConnectionSecurity.eCSNone, "imap"),
         (25001, eConnectionSecurity.eCSTLS, "smtp"), (11001, eConnectionSecurity.eCSTLS, "pop3"), (14301, eConnectionSecurity.eCSTLS, "imap"),
         (25002, eConnectionSecurity.eCSSTARTTLSOptional, "smtp"), (11002, eConnectionSecurity.eCSSTARTTLSOptional, "pop3"), (14302, eConnectionSecurity.eCSSTARTTLSOptional, "imap"),
         (25003, eConnectionSecurity.eCSSTARTTLSRequired, "smtp"), (11003, eConnectionSecurity.eCSSTARTTLSRequired, "pop3"), (14303, eConnectionSecurity.eCSSTARTTLSRequired, "imap"),
      };

      public static void SetupSSLPorts(Application application, SslVersions sslVersions = null)
      {
         // Over the certificate, port and settings routes, then a restart in
         // place, as the Windows SslSetup restarts the application through COM.
         if (!ServerApi.HasRoute("/api/v1/ports", "post") || !ServerApi.HasRoute("/api/v1/server/reinitialize", "post"))
            NotOnThisServer.Ignore(NotOnThisServer.NoTlsListener);

         // The server reads the certificate and key from the paths it is given,
         // so it has to be on this host; a server elsewhere would need the
         // files copied to it and named by their path there.
         if (!TestTarget.IsLocal)
            NotOnThisServer.Ignore(NotOnThisServer.NoTlsListener + " (the certificate files are on this host and the server is not)");

         var sslPath = GetSslCertPath();
         var exampleCert = Paths.Combine(sslPath, "example.crt");
         var exampleKey = Paths.Combine(sslPath, "example.key");
         if (!System.IO.File.Exists(exampleCert))
            Assert.Fail("Certificate " + exampleCert + " was not found");
         if (!System.IO.File.Exists(exampleKey))
            Assert.Fail("Private key " + exampleKey + " was not found");

         long certificateId = 0;
         foreach (var certificate in ServerApi.Array(ServerApi.Get("/api/v1/certificates").Expect(200, "GET /api/v1/certificates")))
            if (string.Equals(ServerApi.StringOf(certificate, "name"), "Example", StringComparison.OrdinalIgnoreCase))
               certificateId = ServerApi.LongOf(certificate, "id");

         if (certificateId == 0)
         {
            var created = ServerApi.Post("/api/v1/certificates",
               "{\"name\":\"Example\",\"certificate_file\":" + ServerApi.Quote(exampleCert) +
               ",\"private_key_file\":" + ServerApi.Quote(exampleKey) + "}").Expect(201, "POST /api/v1/certificates Example");
            certificateId = ServerApi.LongOf(created.Json.Value, "id");
         }

         // The listeners: any earlier run's rows on these numbers go first, so
         // the set is exactly the twelve, bound to this certificate.
         foreach (var port in ServerApi.Array(ServerApi.Get("/api/v1/ports").Expect(200, "GET /api/v1/ports")))
         {
            var number = (int) ServerApi.LongOf(port, "port");
            foreach (var wanted in SslPorts)
               if (wanted.Port == number)
               {
                  var id = ServerApi.LongOf(port, "id");
                  ServerApi.Delete("/api/v1/ports/" + id).Expect(200, "DELETE /api/v1/ports/" + id);
               }
         }

         foreach (var wanted in SslPorts)
            ServerApi.Post("/api/v1/ports",
                  "{\"protocol\":\"" + wanted.Protocol + "\",\"address\":\"0.0.0.0\",\"port\":" + wanted.Port +
                  ",\"connection_security\":" + ServerApi.Quote(TestSetup.ConnectionSecurityName(wanted.Security)) +
                  ",\"certificate_id\":" + certificateId + "}")
               .Expect(201, "POST /api/v1/ports " + wanted.Port);

         ServerApi.Put("/api/v1/settings",
               "{\"tls_version_10_enabled\":" + Word(sslVersions == null || sslVersions.Tls10) +
               ",\"tls_version_11_enabled\":" + Word(sslVersions == null || sslVersions.Tls11) +
               ",\"tls_version_12_enabled\":" + Word(sslVersions == null || sslVersions.Tls12) +
               ",\"tls_version_13_enabled\":" + Word(sslVersions == null || sslVersions.Tls13) + "}")
            .Expect(200, "PUT /api/v1/settings (TLS versions)");

         Reinitialize();
      }

      private static string Word(bool value)
      {
         return value ? "true" : "false";
      }

      /// <summary>
      ///    POST /api/v1/server/reinitialize, then wait for the REST listener to
      ///    go away and come back: the answer arrives before the restart, and a
      ///    status read that answers at once is the old listener, not the new.
      /// </summary>
      public static void Reinitialize()
      {
         ServerApi.Post("/api/v1/server/reinitialize", "{}").Expect(202, "POST /api/v1/server/reinitialize");

         var deadline = DateTime.UtcNow.AddSeconds(90);
         var wentDown = false;
         while (DateTime.UtcNow < deadline)
         {
            System.Threading.Thread.Sleep(250);
            var probe = ServerApi.TryGet("/api/v1/status");
            if (probe == null)
            {
               wentDown = true;
               continue;
            }
            if (wentDown && probe.Status == 200)
               return;
         }

         Assert.Fail(wentDown
            ? "The REST listener did not come back within 90 seconds of POST /api/v1/server/reinitialize."
            : "The REST listener never went down after POST /api/v1/server/reinitialize: the restart did not happen.");
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
