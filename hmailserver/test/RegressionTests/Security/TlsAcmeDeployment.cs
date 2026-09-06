// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.Security
{
   /// <summary>
   ///    A certificate the ACME client issued but never deployed is deployed at the next
   ///    check. Issue #93 left installations in exactly that state: 6.2.24 wrote a valid
   ///    fullchain.pem and privkey.pem and ended the process before the "ACME (automatic)"
   ///    record was created, and because the certificate was not due for renewal every
   ///    later run of the task looked at it and left. Now a pair on disk that no record
   ///    names is deployed once, the way a fresh issuance is; with the record in place the
   ///    check is one look-up and nothing else.
   /// </summary>
   [TestFixture]
   public class TlsAcmeDeployment : TestFixtureBase
   {
      // Nothing listens here, so ShouldRenewNow cannot ask the CA for its opinion and
      // falls back to the certificate's own dates - twenty years of them. Inside this
      // fixture's assigned port range.
      private const int UnreachableAcmePort = 9483;

      private const string RecordName = "ACME (automatic)";

      // A self-signed EC P-256 certificate valid until 2046, and its key; the same
      // fixture the server's self-test computes a TLSA record for.
      private const string CertificatePem =
         "-----BEGIN CERTIFICATE-----\n" +
         "MIIBnTCCAUOgAwIBAgIUTt9dEsQtTAfFIIM+5viNWZ1No2wwCgYIKoZIzj0EAwIw\n" +
         "JDEiMCAGA1UEAwwZdGxzYS1maXh0dXJlLmV4YW1wbGUudGVzdDAeFw0yNjA5MDYw\n" +
         "MTE5NDlaFw00NjA5MDEwMTE5NDlaMCQxIjAgBgNVBAMMGXRsc2EtZml4dHVyZS5l\n" +
         "eGFtcGxlLnRlc3QwWTATBgcqhkjOPQIBBggqhkjOPQMBBwNCAARcQAyXS3jMUHe7\n" +
         "R1o4DUn+iwKaVorSVrb/ajttx1R+PcKDb2ftkaDapR7MHbBRLTNrY02KJjzoreKa\n" +
         "mGx4JxOJo1MwUTAdBgNVHQ4EFgQU+KRDFDaeMDV/BbibOpNAb0tBARswHwYDVR0j\n" +
         "BBgwFoAU+KRDFDaeMDV/BbibOpNAb0tBARswDwYDVR0TAQH/BAUwAwEB/zAKBggq\n" +
         "hkjOPQQDAgNIADBFAiEAoRJcE3fgMRiHhf3LglFlRkznZENHdc9SwMOGjNpsUokC\n" +
         "IHt+CXzwoEOtys7tKUt6k5y/Qftj4xirfGPZCy00rzX/\n" +
         "-----END CERTIFICATE-----\n";

      private const string PrivateKeyPem =
         "-----BEGIN PRIVATE KEY-----\n" +
         "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgGTr14alyWTYqvn3W\n" +
         "b/ZUSNYvcGhToNmp8nlMVtPoTPqhRANCAARcQAyXS3jMUHe7R1o4DUn+iwKaVorS\n" +
         "Vrb/ajttx1R+PcKDb2ftkaDapR7MHbBRLTNrY02KJjzoreKamGx4JxOJ\n" +
         "-----END PRIVATE KEY-----\n";

      // The ports' certificate assignments as this fixture found them: the deployment
      // assigns the new record to every TLS port that has none, and the bench must be
      // left as it was.
      private readonly Dictionary<int, int> _portCertificates = new Dictionary<int, int>();

      // Under the server's own program directory, not the temp directory: the files
      // have to be readable by the *service*, which need not be running as the account
      // the tests run as. TlsAcmeRenewal does the same.
      private string AcmeDirectory
      {
         get { return Paths.Combine(_application.Settings.Directories.ProgramDirectory, "hm_acme_deploy_test"); }
      }

      [SetUp]
      public void RecordPortCertificates()
      {
         _portCertificates.Clear();
         TCPIPPorts ports = _application.Settings.TCPIPPorts;
         for (int i = 0; i < ports.Count; i++)
         {
            TCPIPPort port = ports[i];
            _portCertificates[port.ID] = port.SSLCertificateID;
         }
      }

      [TearDown]
      public void DisableAcmeAndRestore()
      {
         try
         {
            IniFileSetting.Write("AcmeEnabled", null);
            IniFileSetting.Write("AcmeDomains", null);
            IniFileSetting.Write("AcmeDirectoryUrl", null);
            IniFileSetting.Write("AcmeCertificateDirectory", null);

            // Any port the deployment pointed at the record goes back to what it had;
            // then the record itself goes.
            TCPIPPorts ports = _application.Settings.TCPIPPorts;
            ports.Refresh();
            for (int i = 0; i < ports.Count; i++)
            {
               TCPIPPort port = ports[i];
               int before;
               if (_portCertificates.TryGetValue(port.ID, out before) && port.SSLCertificateID != before)
               {
                  port.SSLCertificateID = before;
                  port.Save();
               }
            }

            SSLCertificate record = FindRecord();
            while (record != null)
            {
               record.Delete();
               record = FindRecord();
            }

            _application.Reinitialize();
         }
         finally
         {
            LogHandler.DeleteErrorLog();
            try
            {
               if (Directory.Exists(AcmeDirectory))
                  Directory.Delete(AcmeDirectory, true);
            }
            catch (IOException)
            {
               // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
            }
         }
      }

      private SSLCertificate FindRecord()
      {
         SSLCertificates certificates = _application.Settings.SSLCertificates;
         certificates.Refresh();
         for (int i = 0; i < certificates.Count; i++)
         {
            SSLCertificate certificate = certificates[i];
            if (certificate.Name == RecordName)
               return certificate;
         }
         return null;
      }

      private int CountRecords()
      {
         SSLCertificates certificates = _application.Settings.SSLCertificates;
         certificates.Refresh();
         int count = 0;
         for (int i = 0; i < certificates.Count; i++)
            if (certificates[i].Name == RecordName)
               count++;
         return count;
      }

      private static int CountOccurrences(string text, string needle)
      {
         int count = 0;
         int at = 0;
         while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
         {
            count++;
            at += needle.Length;
         }
         return count;
      }

      [Test]
      [Description("A certificate pair on disk with no \"ACME (automatic)\" record is deployed at the next ACME check, once; a second check finds the record and does nothing")]
      public void AnIssuedButUndeployedCertificateIsDeployedAtTheNextCheck()
      {
         Directory.CreateDirectory(AcmeDirectory);
         string certificateFile = Paths.Combine(AcmeDirectory, "fullchain.pem");
         string privateKeyFile = Paths.Combine(AcmeDirectory, "privkey.pem");
         File.WriteAllText(certificateFile, CertificatePem, new UTF8Encoding(false));
         File.WriteAllText(privateKeyFile, PrivateKeyPem, new UTF8Encoding(false));

         Assert.IsNull(FindRecord(), "The bench starts with no ACME record.");

         IniFileSetting.Write("AcmeCertificateDirectory", AcmeDirectory);
         IniFileSetting.Write("AcmeDirectoryUrl", "https://127.0.0.1:" + UnreachableAcmePort + "/directory");
         IniFileSetting.Write("AcmeDomains", "mail.example.test");
         IniFileSetting.Write("AcmeEnabled", "1");

         // Reinitialize registers the renewal task afresh, and its startup instance
         // runs at once. The certificate is not due, so against the build before this
         // change the task looked at the pair and left; now it deploys it - and the
         // deployment restarts the servers, which is why the record is polled for.
         _application.Reinitialize();

         RetryHelper.TryAction(TimeSpan.FromSeconds(60), () =>
         {
            SSLCertificate record = FindRecord();
            if (record == null)
               throw new Exception("No \"" + RecordName + "\" record yet. Log:\r\n" + LogHandler.ReadCurrentDefaultLog());

            Assert.AreEqual(certificateFile, record.CertificateFile);
            Assert.AreEqual(privateKeyFile, record.PrivateKeyFile);
         });

         RetryHelper.TryAction(TimeSpan.FromSeconds(30), () =>
         {
            string log = LogHandler.ReadCurrentDefaultLog();
            if (!log.Contains("issued but never deployed"))
               throw new Exception("The deployment is not in the log yet. Log:\r\n" + log);
            if (!log.Contains("Restarting servers to load the new certificate"))
               throw new Exception("The restart is not in the log yet. Log:\r\n" + log);
         });

         // The second check: the record exists, so the task does nothing - no second
         // record, no second deployment line. A Reinitialize runs the startup instance
         // again, and the deployment's own restart may already have run it once more;
         // either way the count of records is one and the count of deployments is one.
         _application.Reinitialize();

         // The startup instance goes straight onto the maintenance queue when
         // Reinitialize registers it, and the not-due path is a file check and one
         // database read. A few seconds is generous.
         System.Threading.Thread.Sleep(3000);

         Assert.AreEqual(1, CountRecords(), "One record, however many checks have run.");
         Assert.AreEqual(1, CountOccurrences(LogHandler.ReadCurrentDefaultLog(), "issued but never deployed"),
            "The deployment happens once; with the record in place the check is a look-up.");
      }
   }
}
