// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.IO;
using System.Text;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using RegressionTests.SSL;

namespace RegressionTests.Security
{
   /// <summary>
   ///    A TLS listener pointed at an expired certificate.
   ///
   ///    Of the four ways a configured certificate can be unusable, three were already
   ///    handled: a missing file, an unreadable file and a key that does not match the
   ///    certificate all make OpenSSL refuse the pair, so InitServer reports HM5113 and
   ///    the listener does not start. The fourth was not handled at all. OpenSSL checks
   ///    the validity period of certificates it *verifies* and not of the one it is
   ///    told to serve, so an expired certificate loads without complaint: the listener
   ///    binds, TLS is offered, and every client that checks the date closes the
   ///    connection. The server side of that has no symptom - no error, no
   ///    application-log line, and a handshake failure that looks like any other
   ///    aborted session.
   ///
   ///    InitServer now inspects the leaf certificate's notBefore/notAfter after
   ///    loading it and reports HM5991. It does not refuse the listener: refusing would
   ///    mean the mail ports stop listening at the moment a certificate expires, which
   ///    turns a degraded server into an unreachable one.
   ///
   ///    The certificate below is a self-signed throwaway generated for this test with
   ///    a validity window entirely in the past (1 Jan 2019 to 1 Jan 2020). Its private
   ///    key is in the file beside it on purpose - it is not a secret, it is a fixture,
   ///    and it is expired, so it cannot be used for anything. It is embedded rather
   ///    than generated at run time so that the date this test asserts on is fixed.
   /// </summary>
   [TestFixture]
   public class TlsCertificateValidity : TestFixtureBase
   {
      // Inside this fixture's assigned range; nothing else in the suite listens here.
      private const int SmtpTlsPort = 9480;

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

      private const string ExpiredPrivateKeyPem =
         "-----BEGIN RSA PRIVATE KEY-----\r\n" +
         "MIIEpQIBAAKCAQEAtRQjtomEf7Uu8W75KQgDvIrlyU3wohZyv/2GNuJO8zAYDARe\r\n" +
         "A+UKwqDTWw4J0EmOk+F/2PSU1CNIAHkK2Xrbwx/SF9b40gWStRvJjvaAO2mUsqIH\r\n" +
         "KdSqEf0MzNRti1wj/IpjPzqVb48OU9Xlo9Qmx3sipOAz4gVWH3f0v8HFbLh4v9g/\r\n" +
         "h7x8EVKyeRDqB0GvfAzq2xYsh4b/9TdCE/nbjPOymu9wnAb7/h3kGzB01UkXutC2\r\n" +
         "Z5F8QgJqSavLys0QMpgIF88Qj1a8iQ/xzDT0sk7T2Hc85KSsgVCV46Yaer8O2Ecn\r\n" +
         "iJxdNxLGo7+1Dwu+Uiw1hglG+4ERlK3OGj4mpQIDAQABAoIBAQCXm8gW8eaM4k7u\r\n" +
         "d+KHFx3Bw22G8QmzPCZRVtwDodFGTZMkpJdunVs3/11WlFdlG+ETMa9QH99oCi4j\r\n" +
         "rzSFSBcttLu7mBJ2DZJ6rkxAtWRB2jFUqtJilJrcOsl3ybf6AWhj7h4Qd2VcpSLy\r\n" +
         "0FjXpS3ewNsNvmXSLHOiH1Y9IujAD/7BW0LX+8NAkcKF37DYt6sB/tVuZ3L9h0ez\r\n" +
         "1pqCQvgFjxEVRjcJnNcDgCcT9HrnnNJN1mPeYSpLIA1PdoII1HbaACMLGDsXGC2k\r\n" +
         "tTaR65uj9ZAr4rjHGJGF/pahfYdMveKzZDPLvDQEI6KrU5NPmVwaLrvpkg+wBZ6e\r\n" +
         "g/sKyn+VAoGBAM8CAHZFUnzvBLHtstfIhghLyEY8H/JHieQx4+lXd7UP5bcWfZK7\r\n" +
         "7g3ZaSkb4Opfc30E/IeRngXYIdBahPPOdN7uWW272VwxHBo5CwTkupndMctdJNDM\r\n" +
         "tOTcjD0wd74i6b9f2+D3oPqkVaPwGA/kFyXR+Y0HRrq/DpN3J8862KMPAoGBAN/v\r\n" +
         "KpfN2nPTXVUeDwoJW4DZYPqvINhNhHGgRulW0TN4V5F4HzFbE1e9jlEZfHTHgnwA\r\n" +
         "7Qv1J1XA+cnKVSHhfNT+u8Xsqc0GDzRIQHqkIEokMUas3YLnZ5UAdRFNuFDFfD+1\r\n" +
         "hf1GxwLt2RxXx+sF+UIHMJwOOBuJRIoMjC3vOosLAoGAAMLgH483s2/pk4HtQ2/g\r\n" +
         "Vk15ChEUiP6MWkN4tBX3QboyPQ8fHRgF0xU2lskcdaAuO4p2J0V40EqwLST4EjFz\r\n" +
         "KpKzz3x+WyFvGgWVrcntib1PfpD0HrRyAdlxxpPUDOXx+BsxIs2mUOWjzvuGCyDq\r\n" +
         "mOABy+v37Z3gPtiUU+XCgC8CgYEAxhF2PAVRFqe6YuIObVMvgz1CoRir1YZjAlnA\r\n" +
         "vv0SVxM3aSy1cmNbLX01VxhS07vv9xyejrgNTbU9ezWirTATyRVzIrKc0gJtClJp\r\n" +
         "7dAj21A94YRe/T0OimV4JpD22UKEDpnRZN/ogPe91Gr0IjYLbVKMtUuCZyC35d8J\r\n" +
         "UkvKHVcCgYEAnkQHdhpIdIVi6BOvK6PzJkndhZapN+L9nMwqWBpeu1bmvWbR6hDy\r\n" +
         "SYN5WQhAbuJJCKLrUz5E6Ryf/eS5a6aAKhWv1vqgJr+JsUak8mmzCu5I+1upr+fI\r\n" +
         "/EG/Dgx2vaVM5GwqfdJftfRNSM5If1dr1Cqe8zR7VHsOtNiUzD3CAtc=\r\n" +
         "-----END RSA PRIVATE KEY-----\r\n";

      // The server's own program directory rather than the temp directory, because
      // the file has to be readable by the *service* and not only by the test
      // process. OAuth2Bearer does the same with its RS256 key file for the same
      // reason.
      private string ExpiredCertificateFile
      {
         get { return Path.Combine(_application.Settings.Directories.ProgramDirectory, "hm_expired_test.crt"); }
      }

      private string ExpiredPrivateKeyFile
      {
         get { return Path.Combine(_application.Settings.Directories.ProgramDirectory, "hm_expired_test.key"); }
      }

      [Test]
      [Description("A listener told to serve an expired certificate reports it instead of silently failing every handshake")]
      public void ExpiredCertificateIsReported()
      {
         // No BOM: OpenSSL's PEM reader wants the file to begin with the dashes.
         var withoutByteOrderMark = new UTF8Encoding(false);

         File.WriteAllText(ExpiredCertificateFile, ExpiredCertificatePem, withoutByteOrderMark);
         File.WriteAllText(ExpiredPrivateKeyFile, ExpiredPrivateKeyPem, withoutByteOrderMark);

         try
         {
            var certificate = _settings.SSLCertificates.Add();
            certificate.Name = "Expired";
            certificate.CertificateFile = ExpiredCertificateFile;
            certificate.PrivateKeyFile = ExpiredPrivateKeyFile;
            certificate.Save();

            var port = _settings.TCPIPPorts.Add();
            port.Address = "127.0.0.1";
            port.PortNumber = SmtpTlsPort;
            port.ConnectionSecurity = eConnectionSecurity.eCSTLS;
            port.SSLCertificateID = certificate.ID;
            port.Protocol = eSessionType.eSTSMTP;
            port.Save();

            _application.Stop();
            _application.Start();

            // Against the build before the fix this fails with an empty error log:
            // the pair loads, the listener starts, and nothing anywhere records that
            // the certificate being served stopped being valid in 2020. The date is
            // asserted as well as the code, because a message that says a certificate
            // is expired without saying since when sends the administrator back to
            // the certificate file to find out.
            CustomAsserts.AssertReportedError("HM5991", "expired on 2020-01-01 00:00:00 UTC");
         }
         finally
         {
            // Before PerformBasicSetup, which asserts the error log is empty.
            LogHandler.DeleteErrorLog();

            // Drops the port and the certificate record, and restarts the server, so
            // the next fixture does not inherit a listener on an expired certificate.
            SingletonProvider<TestSetup>.Instance.PerformBasicSetup();

            CustomAsserts.AssertDeleteFile(ExpiredCertificateFile);
            CustomAsserts.AssertDeleteFile(ExpiredPrivateKeyFile);
         }
      }

      [Test]
      [Description("A certificate inside its validity period is not reported")]
      public void ValidCertificateIsNotReported()
      {
         // The other half, and the one that would break twenty other fixtures if the
         // check were wrong in the other direction: the suite's own example.crt is
         // valid until 2036, and starting the twelve SSL ports with it must stay
         // silent. A validity check that reports on a good certificate would put an
         // entry in the error log of every SSL fixture in the suite.
         try
         {
            SslSetup.SetupSSLPorts(_application);

            CustomAsserts.AssertNoReportedError();
         }
         finally
         {
            SingletonProvider<TestSetup>.Instance.PerformBasicSetup();
         }
      }
   }
}
