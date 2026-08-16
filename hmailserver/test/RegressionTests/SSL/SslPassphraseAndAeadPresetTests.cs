// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.SSL
{
   /// <summary>
   ///    Covers two TLS features that ship together: per-certificate private key
   ///    passphrases (an encrypted PEM key loads when SSLCertificate.PrivateKeyPassword
   ///    is set, and fails exactly as before when it is not), and the AEAD-ONLY named
   ///    cipher preset (a vetted expansion of SslCipherList rather than a raw OpenSSL
   ///    string the administrator has to get right).
   ///
   ///    The encrypted key and its certificate are embedded below rather than added to
   ///    the SSL examples directory, so the fixture is self-contained. The certificate
   ///    is self-signed for CN=127.0.0.1, valid until 2046, and the key is an
   ///    AES-encrypted PKCS#8 PEM whose passphrase is TestKeyPass6170.
   /// </summary>
   [TestFixture]
   public class SslPassphraseAndAeadPresetTests : TestFixtureBase
   {
      [SetUp]
      public new void SetUp()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");
      }

      private Account _account;

      private const string EncryptedKeyPassphrase = "TestKeyPass6170";

      private const string EncryptedPrivateKeyPem =
         "-----BEGIN ENCRYPTED PRIVATE KEY-----\r\n" +
         "MIIFJDBWBgkqhkiG9w0BBQ0wSTAxBgkqhkiG9w0BBQwwJAQQ2WhVM3unJeU1H4vG\r\n" +
         "DjYKtgICCAAwDAYIKoZIhvcNAgkFADAUBggqhkiG9w0DBwQII+W0KdqUNloEggTI\r\n" +
         "JXQ8KOCV7gKE1MNqEq6h8CG4909sSFYn5yIHBALdJx7HqBHP5CQoKnRDPBY359vz\r\n" +
         "WuJl9RtxmPYMB2Kte2BB1YQ+EIRscF59E6tYQyX9JydSg5puB2HKyc35xt+Bf+UK\r\n" +
         "9avEwaDv6wO5yrLz9DkyS60KYQ068ZaQnSUYULhTXLadqWnyz2B3UCymO2y+u7i8\r\n" +
         "0wUe4O+/3NeXK4+OuDCSBAw8BU3WzNuqjlJo0FnsFYnZ0dvn+636HXGaf0pbbCGM\r\n" +
         "LfhbVjRjUkw79URXnLgjjXdpAJlG6m2L1yPArTvoPZ5ifWvHkUsAD8tDFaKAA3j2\r\n" +
         "6xbPEF2fmebKusFuDWxzWzvZMPYsjuxLQfyDh5LF18u5nN+xP6tge1eeChdMHQ8P\r\n" +
         "wvloFaoyhKOcBVKa8Klw++PdZxDi/qvHjmLeSJ8IMEr7IUTXWtwCsKGbUJOrpR/e\r\n" +
         "DbQaJUwk8j1s38aBq3Ipc0pyy4RhVPXNny7wkANjVOHJKmpiOi3X1LUP/LGhuJ1x\r\n" +
         "rbDfXCJtQONM9CbOEBC5jZZqJRu6O+jpiHn+Hq9FPsGBflMwO0qR9REiTfRmpCJ0\r\n" +
         "ZDkSeSc/QBVVhNKvcyalv3Ls1MBWa/EAGSgh68vIrDZJgJNLof4n4ehCvuA/ktFq\r\n" +
         "k01B4sW8GOGt6UQD33Ex1BrHjLnZ7TvHFXROyjAylCpWsv+n8zvXeEHn5H5uieZl\r\n" +
         "WWhiJj30CAlhBMzaKZ+njhGyNzb2vafFsyomtT3fBcv8YzgTldrOBkMoZi02Iswr\r\n" +
         "Y3l5OsUtcU7ESddzUjGUXIZncEwLSUjp39/4eRz8RF3UGOKGh5u1WbIYor5uvsPU\r\n" +
         "CknY34dlq/V7T5MR0x4rVrh4qynCdPqxOO/k23AhZkxkhdFtj4zRuTitzIRfbA0A\r\n" +
         "c6k2+WX5aziKHeSg0I9yHyMLWtTmQ1EaMyXVX2TZo5B0GCm6urwOPU9ruG99tY3C\r\n" +
         "E3zo09tor4X+DBrICUMf8l+uFv7UwpzqXecy/ec7RI/RyZ8qAihsGDHpM0mZCLqb\r\n" +
         "SZ8xw/ZkGiGk+bq6GpBANlg1ic5202BGDFG4fcXyObAym5bKyjdWdRkN/332IEzO\r\n" +
         "3MgiRH11yh0Fn3OXNHP15NraqJ4x1KdeUUhJlpA9jFtnX7YGc8G3ZC7/FfY4fizJ\r\n" +
         "csrIleUj9MBAOL46KHdwKWkY2wZ6nlEKxnHUAbOdWFiEzYb8pc4grF9OLyqGekZA\r\n" +
         "Ar8Lqp+uTymSKbumPJfRQ76Ps6dna+sJIL/QcOVKceZTKzWbfFgPAguZEi3mf9vM\r\n" +
         "1mUHFkDhDmewtauA7SS7v5J4SgApP4s9U5QJSBx5ck3bxuauCiy57G9MuVInUuKG\r\n" +
         "lh/akUVCmKpLDJRMta0KglZ+OD0PDV/bxTkpVQ2I1rC0z0yyp9Sj0NXqGPqfEl1W\r\n" +
         "mqDshWtmHaU2pXvVgUChZehEAF0HDwKMN2rJuF+lZrCJif/UrOvNp+TNYMcQgqSS\r\n" +
         "w51wN3Gvj20KelC2C25mZTVv6phAV0QEEdNP/IZtj2BhDegds2G4++x8xmYCVJ4p\r\n" +
         "Nl4gM8+9PkNK64hhjTp+/A23RLaUShf9OiC5paAPmQbQwxH1Y/bUr3WWU5dBo4fJ\r\n" +
         "QbDgeKggFiGwZGi887MSYsBaE6eBSOyH\r\n" +
         "-----END ENCRYPTED PRIVATE KEY-----\r\n";

      private const string CertificatePem =
         "-----BEGIN CERTIFICATE-----\r\n" +
         "MIIDCTCCAfGgAwIBAgIUAW2QlRxc6LauD3IaS4RMZ4LW+SUwDQYJKoZIhvcNAQEL\r\n" +
         "BQAwFDESMBAGA1UEAwwJMTI3LjAuMC4xMB4XDTI2MDgxNDA3Mzk0NloXDTQ2MDgw\r\n" +
         "OTA3Mzk0NlowFDESMBAGA1UEAwwJMTI3LjAuMC4xMIIBIjANBgkqhkiG9w0BAQEF\r\n" +
         "AAOCAQ8AMIIBCgKCAQEAoyWMDE7t5qQV9xG2eOvkWMcMK94oweUztzMMRnDIoKPc\r\n" +
         "bMeDTcUm9O+aPWcajGKHpf7VLvJ8KIhmG8p4TA7D0ROaoNw8Gq/MopIgALyLaWd4\r\n" +
         "7+yBpPcNXilLvrkvTySw9oiu9dxhBrqWTZpeKnLYR8VYby756MoIgKtbyCnft763\r\n" +
         "/C+IeARSc9P7EbjMCXrwtwXEVQ6sC3AHfu+1aDY/bys1hQVpN9+EUcaqKfEAVGzb\r\n" +
         "CC3fOCzdvpPZuMAFsDCn9Fv0d5BIHMyMVVHxBk7H5gRir9B+AV0JLM4ExPBgaqTe\r\n" +
         "SiGvL2VpWXWYZCDYph9CY6ebZtDb8f1FDVG3kiJ5nwIDAQABo1MwUTAdBgNVHQ4E\r\n" +
         "FgQUdpDGmgTG3w2cE56vdjcmY+w5lB8wHwYDVR0jBBgwFoAUdpDGmgTG3w2cE56v\r\n" +
         "djcmY+w5lB8wDwYDVR0TAQH/BAUwAwEB/zANBgkqhkiG9w0BAQsFAAOCAQEAnaBB\r\n" +
         "Gp7L8m+xAtMf7WbgjkZ/B5VaWnGSVSRiluvyAE6xypnl4eD4DCNwC/hZIy84vCzM\r\n" +
         "Z8uicQWQlCAxidADibX9G/hCoByy+jQ6rdXd1OxZ1YG+nR0lTj/ecyYx+656uR2i\r\n" +
         "BSI3Hq3o/F2kYxv12TilQoNBy5AgqP/FGrYq/rear5BlDP3xGP3J5RMY2q8xzcs+\r\n" +
         "TTs8+BfnLpIbrJEUzGI4QQwjE2FyG7CpVQKhA+i9kXsMSyBGezeunzBYzeyGYKt4\r\n" +
         "w9cvVd7TPpd78Om1Sp8ftAzjzraCkDw1PGfgDHg2Al4yipoLjNxcmbeKAJ1bF+Mk\r\n" +
         "gILT683pzNK4jH47uw==\r\n" +
         "-----END CERTIFICATE-----\r\n";

      /// <summary>
      ///    Writes the embedded PEM pair to disk and returns (certificateFile, keyFile).
      ///    Temp files rather than test resources so nothing outside this fixture has to
      ///    exist for it to run.
      /// </summary>
      private static Tuple<string, string> WriteEncryptedKeyPairToDisk()
      {
         string directory = Path.Combine(Path.GetTempPath(), "hMailServerEncryptedKeyTest");
         Directory.CreateDirectory(directory);

         string certificateFile = Path.Combine(directory, "encrypted-key-test.crt");
         string keyFile = Path.Combine(directory, "encrypted-key-test.key");

         File.WriteAllText(certificateFile, CertificatePem);
         File.WriteAllText(keyFile, EncryptedPrivateKeyPem);

         return Tuple.Create(certificateFile, keyFile);
      }

      /// <summary>
      ///    Adds an SSL certificate whose key is the embedded encrypted one, binds an
      ///    SMTP TLS listener on port 25010 to it (a port no other fixture uses), and
      ///    restarts the server so the listener comes up with the new configuration.
      /// </summary>
      private void SetUpTlsPortWithEncryptedKey(string privateKeyPassword)
      {
         var files = WriteEncryptedKeyPairToDisk();

         var sslCertificate = _application.Settings.SSLCertificates.Add();
         sslCertificate.Name = "EncryptedKeyTest";
         sslCertificate.CertificateFile = files.Item1;
         sslCertificate.PrivateKeyFile = files.Item2;
         sslCertificate.PrivateKeyPassword = privateKeyPassword;
         sslCertificate.Save();

         var ports = _application.Settings.TCPIPPorts;
         ports.SetDefault();

         var port = ports.Add();
         port.Address = "0.0.0.0";
         port.PortNumber = 25010;
         port.ConnectionSecurity = eConnectionSecurity.eCSTLS;
         port.SSLCertificateID = sslCertificate.ID;
         port.Protocol = eSessionType.eSTSMTP;
         port.Save();

         _application.Stop();
         _application.Start();

         Thread.Sleep(1000);
      }

      [Test]
      public void PrivateKeyPassword_RoundTripsThroughSaveAndRefresh()
      {
         var files = WriteEncryptedKeyPairToDisk();

         var sslCertificate = _application.Settings.SSLCertificates.Add();
         sslCertificate.Name = "PassphraseRoundTrip";
         sslCertificate.CertificateFile = files.Item1;
         sslCertificate.PrivateKeyFile = files.Item2;
         sslCertificate.PrivateKeyPassword = EncryptedKeyPassphrase;
         sslCertificate.Save();

         int id = sslCertificate.ID;
         Assert.Greater(id, 0);

         // Refresh forces the collection to re-read from the database, so the value
         // asserted below has been through ProtectSecret on the way in and
         // UnprotectSecret on the way out - the DPAPI (or legacy Blowfish) round
         // trip, not just the in-memory object.
         var certificates = _application.Settings.SSLCertificates;
         certificates.Refresh();

         var reloaded = certificates.get_ItemByDBID(id);
         Assert.AreEqual(EncryptedKeyPassphrase, reloaded.PrivateKeyPassword);

         // The stored form must not be the plaintext. The COM API deliberately never
         // exposes the stored form, so this is asserted indirectly: an empty
         // passphrase must come back empty (proving no constant filler is returned)
         // while the non-empty one above came back intact.
         reloaded.PrivateKeyPassword = "";
         reloaded.Save();
         certificates.Refresh();
         Assert.AreEqual("", certificates.get_ItemByDBID(id).PrivateKeyPassword);
      }

      [Test]
      public void EncryptedPrivateKey_WithConfiguredPassphrase_TlsHandshakeSucceeds()
      {
         SetUpTlsPortWithEncryptedKey(EncryptedKeyPassphrase);

         var smtpClientSimulator = new SmtpClientSimulator(true, SslProtocols.Tls12, 25010, IPAddress.Parse("127.0.0.1"));

         string errorMessage;
         smtpClientSimulator.Send(false, _account.Address, "test", _account.Address, _account.Address, "Test", "test",
            out errorMessage);

         Pop3ClientSimulator.AssertMessageCount(_account.Address, "test", 1);
      }

      [Test]
      public void EncryptedPrivateKey_WithoutPassphrase_TlsHandshakeFails()
      {
         // The negative control: the same encrypted key with no passphrase configured
         // must fail exactly as it always has (the key does not load, the listener
         // does not come up), rather than quietly loading with some default. Expected
         // server-side evidence is error 6170 in the hMailServer error log.
         SetUpTlsPortWithEncryptedKey("");

         var smtpClientSimulator = new SmtpClientSimulator(true, SslProtocols.Tls12, 25010, IPAddress.Parse("127.0.0.1"));

         try
         {
            string errorMessage;
            smtpClientSimulator.Send(false, _account.Address, "test", _account.Address, _account.Address, "Test",
               "test",
               out errorMessage);

            Assert.Fail("A TLS connection succeeded against a listener whose encrypted private key has no configured passphrase.");
         }
         catch (SocketException)
         {
            // The listener never opened because the key failed to load.
         }
         catch (AuthenticationException)
         {
         }
         catch (Win32Exception)
         {
         }
         catch (IOException)
         {
         }
         catch (DeliveryFailedException)
         {
            // What the simulator actually throws when it cannot reach the port at all,
            // which is this test's expected outcome: the listener never came up.
         }
         finally
         {
            // The deliberate 6170/5113 pair has to be cleared here, or it fails the NEXT
            // fixture instead of this one. TestFixtureBase.SetUp calls
            // AssertNoReportedError, which fails if the error log exists AT ALL - so a
            // test that provokes a High-severity error on purpose and leaves it behind
            // reports as a broken run somewhere unrelated. ClearErrorLogUntilSettled
            // waits for the log to stay gone rather than deleting once, because a single
            // failure writes several entries from several paths and a straight delete
            // races the stragglers.
            LogHandler.ClearErrorLogUntilSettled();
         }
      }

      [Test]
      public void AeadOnlyPreset_Tls12HandshakeNegotiatesAnAeadCipher()
      {
         string originalCipherList = _application.Settings.SslCipherList;

         try
         {
            // Lower case on purpose: the preset name is documented case-insensitive.
            _application.Settings.SslCipherList = "aead-only";

            // SetupSSLPorts restarts the server, which is when the cipher list is
            // applied to the listener contexts.
            SslSetup.SetupSSLPorts(_application);
            Thread.Sleep(1000);

            // TLS 1.2 pinned deliberately: every TLS 1.3 suite is AEAD whatever the
            // preset says, so only a TLS 1.2 handshake can prove the preset filtered
            // the cipher list.
            var smtpClientSimulator = new SmtpClientSimulator(true, SslProtocols.Tls12, 25001, IPAddress.Parse("127.0.0.1"));

            string errorMessage;
            smtpClientSimulator.Send(false, _account.Address, "test", _account.Address, _account.Address, "Test",
               "test",
               out errorMessage);

            string message = Pop3ClientSimulator.AssertGetFirstMessageText(_account.Address, "test");

            Assert.IsTrue(message.Contains("version=TLSv1.2"), message);
            Assert.IsTrue(message.Contains("GCM") || message.Contains("CHACHA20"),
               "The negotiated cipher under the AEAD-ONLY preset is not an AEAD cipher. Received headers: " + message);
         }
         finally
         {
            // Put the shipped default back so no later fixture runs under the preset.
            // The running contexts keep it until the next restart, but every fixture
            // that cares about ciphers performs its own restart via SetupSSLPorts.
            _application.Settings.SslCipherList = originalCipherList;
         }
      }
   }
}
