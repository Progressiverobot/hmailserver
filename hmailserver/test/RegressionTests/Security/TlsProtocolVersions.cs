// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using RegressionTests.SSL;
using System;

namespace RegressionTests.Security
{
   /// <summary>
   ///    What happens when the TLS version configuration cannot produce a handshake.
   ///
   ///    SslVersions is a bitmask of the four TLS versions, and SSLv2/SSLv3 are
   ///    disabled in code and cannot be turned on. So clearing all four bits asks for
   ///    "TLS, but no version of it" - a context that can negotiate nothing at all.
   ///    OpenSSL accepts that option mask without complaint, the listener binds, the
   ///    port answers, STARTTLS is still advertised - and then every handshake on it
   ///    fails with "no protocols available", on every listener at once, for as long
   ///    as the setting stays that way. Nothing was reported, so from the server's
   ///    side the only symptom was mail not arriving.
   ///
   ///    That is the third of the three outcomes a broken TLS configuration can have,
   ///    and the one that is hardest to diagnose: not "refuses to start", not "starts
   ///    without TLS", but "starts, looks healthy, and fails every handshake".
   ///
   ///    SslContextInitializer now reports it as HM5990 and installs TLS 1.2 and TLS
   ///    1.3, the same shape of recovery the key-exchange group list already used for
   ///    a list OpenSSL rejects. Turning TLS off is done per port with
   ///    ConnectionSecurity, so nothing is being overridden that an administrator
   ///    could have meant.
   /// </summary>
   [TestFixture]
   public class TlsProtocolVersions : TestFixtureBase
   {
      private static SslVersions AllVersions(bool tls10, bool tls11, bool tls12, bool tls13)
      {
         return new SslVersions
         {
            Tls10 = tls10,
            Tls11 = tls11,
            Tls12 = tls12,
            Tls13 = tls13
         };
      }

      /// <summary>
      ///    Back to the shipped default (SslVersions = 24: TLS 1.2 and TLS 1.3 on,
      ///    1.0 and 1.1 off). PerformBasicSetup turns all four on for the next
      ///    fixture, but it runs its own error-log assertion before doing so, so this
      ///    fixture cannot rely on it and restores the versions itself.
      /// </summary>
      private void RestoreDefaultVersions()
      {
         SslSetup.SetupSSLPorts(_application, AllVersions(false, false, true, true));
         Thread.Sleep(1000);
      }

      [Test]
      [Description("With every TLS version disabled the server reports it and falls back to TLS 1.2/1.3 rather than failing every handshake in silence")]
      public void NoTlsVersionEnabled_IsReportedAndTlsKeepsWorking()
      {
         try
         {
            SslSetup.SetupSSLPorts(_application, AllVersions(false, false, false, false));

            // The listeners bind on their own threads during startup.
            Thread.Sleep(1000);

            var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "noversion@example.test", "test");

            // The negative control. Against the build before the fix this send
            // throws: the option mask had disabled every version the context could
            // have negotiated, so the implicit-TLS listener on 25001 could not
            // complete a handshake with any client at all. It passes only because
            // the fallback installed a version that works.
            var smtpClient = new SmtpClientSimulator(true, SslProtocols.Tls12, 25001, IPAddress.Parse("127.0.0.1"));

            string errorMessage;
            smtpClient.Send(false, account.Address, "test", account.Address, account.Address, "Test",
               "Body over the fallback version", out errorMessage);

            var message = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");
            Assert.IsTrue(message.Contains("Body over the fallback version"), message);

            // And the fallback is not silent either: an administrator whose mistake
            // has just been worked around has to be able to find out. Asserted last
            // because AssertReportedError deletes the error log, which the next
            // fixture's PerformBasicSetup requires to be absent.
            CustomAsserts.AssertReportedError("HM5990");
         }
         finally
         {
            RestoreDefaultVersions();

            // Safety net: if an assertion above threw before AssertReportedError ran,
            // the provoked HM5990 entries are still in the log and would fail
            // whichever fixture happens to run next, hiding this failure.
            LogHandler.DeleteErrorLog();
         }
      }

      [Test]
      [Description("A single enabled TLS version is left alone - the fallback only engages when no version at all is enabled")]
      public void OneTlsVersionEnabled_IsNotOverridden()
      {
         try
         {
            // TLS 1.3 only. If the fallback were written as "make sure 1.2 and 1.3
            // are on" rather than "only when none is on", this configuration would
            // silently gain TLS 1.2 - and an administrator who disabled 1.2 on
            // purpose would still be serving it. A TLS 1.2 client must therefore
            // still be refused here, and nothing may be reported.
            SslSetup.SetupSSLPorts(_application, AllVersions(false, false, false, true));

            Thread.Sleep(1000);

            // First, that the listener is alive and the one version left enabled does
            // negotiate. Without this the refusal below could just as well mean the
            // listener never started, and the test would pass while proving nothing.
            // The probe reads only the HelloRetryRequest, so no certificate, cipher
            // or credential is involved.
            using (var probe = new TcpClient())
            {
               probe.Connect("127.0.0.1", 25001);
               probe.ReceiveTimeout = 20000;
               probe.SendTimeout = 20000;

               TlsHandshakeProbe.GetPreferredGroup(probe.GetStream(),
                  new[] {TlsHandshakeProbe.GroupX25519Mlkem768, TlsHandshakeProbe.GroupX25519});
            }

            var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "onlytls13@example.test", "test");

            var smtpClient = new SmtpClientSimulator(true, SslProtocols.Tls12, 25001, IPAddress.Parse("127.0.0.1"));

            bool refused = false;

            try
            {
               string errorMessage;
               smtpClient.Send(false, account.Address, "test", account.Address, account.Address, "Test", "test",
                  out errorMessage);
            }
            catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
            {
               // AuthenticationException, Win32Exception or IOException depending on
               // the Windows build - the same set SslTlsVersionTests catches.
               refused = true;
            }

            Assert.IsTrue(refused, "A TLS 1.2 connection succeeded although only TLS 1.3 was enabled.");

            CustomAsserts.AssertNoReportedError();
         }
         finally
         {
            RestoreDefaultVersions();
            LogHandler.DeleteErrorLog();
         }
      }
   }
}
