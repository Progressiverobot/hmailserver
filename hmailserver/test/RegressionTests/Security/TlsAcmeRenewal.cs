// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.IO;
using System.Text;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.Security
{
   /// <summary>
   ///    A failed ACME renewal has to be visible.
   ///
   ///    AcmeRenewalTask::DoWork logged "Certificate is missing or expires soon.
   ///    Requesting a new certificate." and then called RequestCertificate() and threw
   ///    the return value away. Several of the failure paths inside it returned false
   ///    without logging anything at all, so a renewal that failed could leave the log
   ///    holding one hopeful line and nothing else - and an hour later the task ran
   ///    again and wrote the same line. Read afterwards, the log looked like a renewal
   ///    permanently in progress, right up to the day the certificate expired and every
   ///    TLS client started refusing the server.
   ///
   ///    Automatic renewal whose failure is invisible is worse than manual renewal,
   ///    because with manual renewal somebody at least has the date in a calendar.
   ///
   ///    DoWork now looks at the result, says so in the application log, and reports an
   ///    error once the certificate the renewal was meant to replace is within seven
   ///    days of expiry or already expired - which is the point at which continued
   ///    silence means TLS stops working. Failures with weeks of slack left stay in the
   ///    application log on purpose: renewal starts thirty days out, and an ERROR entry
   ///    for a first failed attempt would teach an operator to ignore the error log.
   ///
   ///    This test drives the already-expired case, which is the one where the
   ///    consequence is certain.
   /// </summary>
   [TestFixture]
   public class TlsAcmeRenewal : TestFixtureBase
   {
      // Nothing listens here, so the client's very first request - fetching the ACME
      // directory - is refused immediately and the whole attempt fails in well under a
      // second without touching a real CA. Inside this fixture's assigned port range.
      private const int UnreachableAcmePort = 9481;

      // An expired self-signed throwaway. Same fixture certificate as
      // TlsCertificateValidity, and expired for the same reason: it is what makes the
      // "already expired" branch reachable without waiting a year.
      private const string ExpiredCertificatePem =
         "-----BEGIN CERTIFICATE-----\r\n" +
         "MIIDDTCCAfWgAwIBAgIJANYEiQbtD4r7MA0GCSqGSIb3DQEBCwUAMEYxJTAjBgNV\r\n" +
         "BAoTHGhNYWlsU2VydmVyIHJlZ3Jlc3Npb24gdGVzdHMxHTAbBgNVBAMTFGV4cGly\r\n" +
         "ZWQuZXhhbXBsZS50ZXN0MB4XDTE5MDEwMTAwMDAwMFoXDTIwMDEwMTAwMDAwMFow\r\n" +
         "RjElMCMGA1UEChMcaE1haWxTZXJ2ZXIgcmVncmVzc2lvbiB0ZXN0czEdMBsGA1UE\r\n" +
         "AxMUZXhwaXJlZC5leGFtcGxlLnRlc3QwggEiMA0GCSqGSIb3DQEBAQUAA4IBDwAw\r\n" +
         "ggEKAoIBAQC1FCO2iYR/tS7xbvkpCAO8iuXJTfCiFnK//YY24k7zMBgMBF4D5QrC\r\n" +
         "oNNbDgnQSY6T4X/Y9JTUI0gAeQrZetvDH9IX1vjSBZK1G8mO9oA7aZSyogcp1KoR\r\n" +
         "/QzM1G2LXCP8imM/OpVvjw5T1eWj1CbHeyKk4DPiBVYfd/S/wcVsuHi/2D+HvHwR\r\n" +
         "UrJ5EOoHQa98DOrbFiyHhv/1N0IT+duM87Ka73CcBvv+HeQbMHTVSRe60LZnkXxC\r\n" +
         "AmpJq8vKzRAymAgXzxCPVryJD/HMNPSyTtPYdzzkpKyBUJXjphp6vw7YRyeInF03\r\n" +
         "Esajv7UPC75SLDWGCUb7gRGUrc4aPialAgMBAAEwDQYJKoZIhvcNAQELBQADggEB\r\n" +
         "AFXm4U3mguZzLJ1ykVZgwyHGSHNPrDDMomL5THIvHMKSbMc9fU9YDFftRPI+iuEz\r\n" +
         "lSqpn1YYuYfIpenTYS1M9hGuo/pyHzkNtiQdETEWVz9r0jnTlSGwZ142VySaYQXI\r\n" +
         "lnETnjov/ePtO2ffWdejnoqsBdO3H0fhujFnyNrG/wqlvT/2lOfoZp0vZggZyUNB\r\n" +
         "BvD4oLV0PByt4UbIxD2EzwlBKogJFdJBPgfq6dWy26TMOpJ1d6TfRhQGcZ2KLcmH\r\n" +
         "AslJZ0KjDKDhwlufZGBKYxQUsFzL/0lHFfR5jBaKU/a0OFomoaul/Qc4vL0nNdsy\r\n" +
         "0kA2YDU2bElrCXM5I6GAraw=\r\n" +
         "-----END CERTIFICATE-----\r\n";

      // Under the server's own program directory, not the temp directory: the
      // certificate has to be readable by the *service*, which need not be running as
      // the account the tests run as. OAuth2Bearer puts its key file in the same place
      // for the same reason.
      private string AcmeDirectory
      {
         get { return Path.Combine(_application.Settings.Directories.ProgramDirectory, "hm_acme_test"); }
      }

      private string AcmeCertificateFile
      {
         get { return Path.Combine(AcmeDirectory, "fullchain.pem"); }
      }

      [TearDown]
      public void DisableAcme()
      {
         try
         {
            // A null value removes the key, so each setting goes back to its built-in
            // default rather than to an empty string that only looks like one.
            // AcmeEnabled has to be off again before this fixture ends: the hourly
            // renewal task is registered at server start, and a task left enabled
            // would keep failing - and keep reporting - through every fixture that
            // runs after this one.
            IniFileSetting.Write("AcmeEnabled", null);
            IniFileSetting.Write("AcmeDomains", null);
            IniFileSetting.Write("AcmeDirectoryUrl", null);
            IniFileSetting.Write("AcmeCertificateDirectory", null);

            _application.Reinitialize();
         }
         finally
         {
            LogHandler.DeleteErrorLog();

            CustomAsserts.AssertDeleteFile(AcmeCertificateFile);

            // Leave the install as it was found. Guarded rather than asserted: the
            // directory not existing, or something still holding it, is not a reason
            // to fail a test that has already made its point.
            try
            {
               if (Directory.Exists(AcmeDirectory))
                  Directory.Delete(AcmeDirectory, true);
            }
            catch (IOException)
            {
            }
         }
      }

      [Test]
      [Description("A failed ACME renewal is reported when the certificate it was renewing has already expired")]
      public void FailedRenewalWithAnExpiredCertificateIsReported()
      {
         Directory.CreateDirectory(AcmeDirectory);
         File.WriteAllText(AcmeCertificateFile, ExpiredCertificatePem, new UTF8Encoding(false));

         IniFileSetting.Write("AcmeCertificateDirectory", AcmeDirectory);
         IniFileSetting.Write("AcmeDirectoryUrl", "https://127.0.0.1:" + UnreachableAcmePort + "/directory");
         IniFileSetting.Write("AcmeDomains", "mail.example.test");
         IniFileSetting.Write("AcmeEnabled", "1");

         // Reinitialize rather than Stop/Start: the ACME settings live in the ini and
         // are cached at InitInstance. It also re-registers the renewal task, and the
         // startup instance of that task is a RunOnce, which Scheduler::ScheduleTask
         // puts straight onto the maintenance queue - so the attempt happens now
         // rather than on the hour.
         _application.Reinitialize();

         // Against the build before the fix this fails with an empty error log. The
         // renewal fails there too, but DoWork discarded the result, so the only trace
         // was one application-log line saying a renewal was about to be attempted.
         CustomAsserts.AssertReportedError("HM5992", "has already expired");
      }
   }
}
