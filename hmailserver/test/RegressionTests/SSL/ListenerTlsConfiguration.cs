// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.IO;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.SSL
{
   /// <summary>
   ///    Every TLS listener has to take its configuration from the one place that
   ///    holds it.
   ///
   ///    SslContextInitializer::InitServer is where the cipher list, the enabled
   ///    protocol versions, the option mask, the Diffie-Hellman parameters and the
   ///    key-exchange groups are applied. The mail protocols have always gone through
   ///    it. The optional HTTP listeners did not: RestApiServer and WebServicesServer
   ///    each called SSL_CTX_new, set a TLS 1.2 floor, loaded the certificate, and took
   ///    OpenSSL's defaults for everything else.
   ///
   ///    That is not a theoretical divergence. When TlsKeyExchangeGroups shipped in
   ///    August 2026 with the hybrid post-quantum KEMs, it reached SMTP, POP3, IMAP and
   ///    ManageSieve - and did not reach these two, which kept negotiating classical
   ///    key exchange with nothing anywhere saying so. Anything configured centrally
   ///    and applied per-listener diverges the same way at the next change.
   ///
   ///    How this is pinned without inspecting the handshake. A .NET client can be
   ///    told which protocol versions to offer, but it cannot report which
   ///    key-exchange group was negotiated, so the group list itself is not directly
   ///    observable from here. What is observable is *which code path* configured the
   ///    context: InitServer reports a certificate it cannot use as HM5113, and
   ///    nothing else in the server reports that code. A listener pointed at a
   ///    certificate that does not exist therefore says, in the error log, which
   ///    implementation it used - HM5113 for the shared one, and for the old
   ///    per-listener code a plain application-log line and no reported error at all.
   ///
   ///    So these are negative controls in the strict sense: they fail against the
   ///    build from before the change, for the right reason.
   /// </summary>
   [TestFixture]
   public class ListenerTlsConfiguration : TestFixtureBase
   {
      // Loopback, and a port nothing else in the suite uses. Neither listener gets
      // far enough to bind in these tests - the certificate is refused first - but a
      // port that another fixture owns would make a failure here ambiguous.
      private const int RestPort = 9107;
      private const int WebServicesHttpsPort = 9108;

      private string MissingCertificate
      {
         // Deliberately absent rather than corrupt: an unreadable path is the one
         // failure that is certain to be rejected by both implementations, so the
         // test distinguishes them by *how* the rejection is reported rather than by
         // whether it happens.
         get { return Paths.Combine(Path.GetTempPath(), "hmailserver-no-such-certificate.pem"); }
      }

      private string MissingPrivateKey
      {
         get { return Paths.Combine(Path.GetTempPath(), "hmailserver-no-such-key.pem"); }
      }

      [SetUp]
      public void EnsureTheCertificateIsAbsent()
      {
         // If a previous run left these behind, the listener would load them and the
         // test would pass without having tested anything.
         CustomAsserts.AssertDeleteFile(MissingCertificate);
         CustomAsserts.AssertDeleteFile(MissingPrivateKey);
      }

      [TearDown]
      public void DisableTheListeners()
      {
         try
         {
            IniFileSetting.Write("RestApiPort", "0");
            IniFileSetting.Write("RestApiCertificateFile", "");
            IniFileSetting.Write("RestApiPrivateKeyFile", "");
            IniFileSetting.Write("WebServicesHttpPort", "0");
            IniFileSetting.Write("WebServicesHttpsPort", "0");
            IniFileSetting.Write("WebServicesCertificateFile", "");
            IniFileSetting.Write("WebServicesPrivateKeyFile", "");

            _application.Reinitialize();
         }
         finally
         {
            // The reported error is the point of these tests, so it has to be cleared
            // here or it fails whichever fixture runs next - PerformBasicSetup asserts
            // the error log is clean.
            LogHandler.DeleteErrorLog();
         }
      }

      [Test]
      [Description("The REST API listener configures TLS through SslContextInitializer")]
      public void RestApiTlsGoesThroughTheSharedInitialiser()
      {
         _settings.SetAdministratorPassword("testar");

         IniFileSetting.Write("RestApiBindAddress", "127.0.0.1");
         IniFileSetting.Write("RestApiPort", RestPort.ToString());
         IniFileSetting.Write("RestApiCertificateFile", MissingCertificate);
         IniFileSetting.Write("RestApiPrivateKeyFile", MissingPrivateKey);

         // Reinitialize and not Stop/Start: the listener's settings are read by
         // IniFileSettings::InitInstance and cached.
         _application.Reinitialize();

         // HM5113 is SslContextInitializer's own code for a certificate it cannot
         // apply. Against the old code this fails: the listener reported the failure
         // with LOG_APPLICATION and the error log stayed empty.
         CustomAsserts.AssertReportedError("HM5113");
      }

      [Test]
      [Description("The Web Services HTTPS listener configures TLS through SslContextInitializer")]
      public void WebServicesTlsGoesThroughTheSharedInitialiser()
      {
         IniFileSetting.Write("WebServicesBindAddress", "127.0.0.1");
         IniFileSetting.Write("WebServicesHttpsPort", WebServicesHttpsPort.ToString());
         IniFileSetting.Write("WebServicesCertificateFile", MissingCertificate);
         IniFileSetting.Write("WebServicesPrivateKeyFile", MissingPrivateKey);

         _application.Reinitialize();

         CustomAsserts.AssertReportedError("HM5113");
      }

      // The two tests above prove which code path configured the context, by the error
      // code it reports. These two prove the consequence, which is the thing an
      // administrator was actually promised: that the hybrid post-quantum key exchange
      // from [Settings] TlsKeyExchangeGroups reaches these listeners. A listener that
      // built its own SSL_CTX answers X25519, because OpenSSL's default group list is
      // what it would be using.
      //
      // See Shared/TlsHandshakeProbe for why this is done with a hand-built ClientHello
      // rather than SslStream, which cannot report the negotiated group.

      private static string ExampleCertificate
      {
         get { return Paths.Combine(SslSetup.GetSslCertPath(), "example.crt"); }
      }

      private static string ExamplePrivateKey
      {
         get { return Paths.Combine(SslSetup.GetSslCertPath(), "example.key"); }
      }

      private static void AssertListenerPrefersPostQuantum(int port, string listenerName)
      {
         using (var client = new TcpClient())
         {
            client.Connect("127.0.0.1", port);
            client.ReceiveTimeout = 20000;
            client.SendTimeout = 20000;

            // Implicit TLS: these listeners answer a handshake immediately, with no
            // STARTTLS step to negotiate first.
            TlsHandshakeProbe.AssertPrefersPostQuantumGroup(client.GetStream(), listenerName);
         }
      }

      [Test]
      [Description("The REST API's HTTPS listener negotiates the hybrid post-quantum key exchange group")]
      public void RestApiPrefersThePostQuantumKeyExchangeGroup()
      {
         _settings.SetAdministratorPassword("testar");

         IniFileSetting.Write("RestApiBindAddress", "127.0.0.1");
         IniFileSetting.Write("RestApiPort", RestPort.ToString());
         IniFileSetting.Write("RestApiCertificateFile", ExampleCertificate);
         IniFileSetting.Write("RestApiPrivateKeyFile", ExamplePrivateKey);

         _application.Reinitialize();

         // The listener binds on its own thread during startup.
         Thread.Sleep(500);

         AssertListenerPrefersPostQuantum(RestPort, "REST API");
      }

      [Test]
      [Description("The Web Services HTTPS listener negotiates the hybrid post-quantum key exchange group")]
      public void WebServicesPrefersThePostQuantumKeyExchangeGroup()
      {
         IniFileSetting.Write("WebServicesBindAddress", "127.0.0.1");
         IniFileSetting.Write("WebServicesHttpsPort", WebServicesHttpsPort.ToString());
         IniFileSetting.Write("WebServicesCertificateFile", ExampleCertificate);
         IniFileSetting.Write("WebServicesPrivateKeyFile", ExamplePrivateKey);

         _application.Reinitialize();

         Thread.Sleep(500);

         AssertListenerPrefersPostQuantum(WebServicesHttpsPort, "Web Services HTTPS");
      }
   }
}
