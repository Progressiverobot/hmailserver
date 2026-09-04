// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Linq;
using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.SSL
{
   /// <summary>
   ///    Inbound client certificates (mutual TLS), configured per TCP/IP port:
   ///    TCPIPPort.ClientCertificatePolicy (0 off, 1 request, 2 require) and
   ///    TCPIPPort.ClientCertificateCAFile.
   ///
   ///    Most of the safety in this feature lives at configuration time, and that is
   ///    where most of these tests live. Save() must refuse the three combinations
   ///    that would look enforced while enforcing nothing: a policy on a plaintext
   ///    port (no handshake ever runs, so the policy can never fire), a policy with
   ///    no CA file ("require" rejects everyone, "request" verifies nothing), and
   ///    "require" on a port where STARTTLS is optional (a client that never issues
   ///    STARTTLS is never asked for a certificate at all - a lock on an open door).
   ///
   ///    The single most important test here is the negative control: every
   ///    pre-existing port has policy 0, and a policy-0 port must behave exactly as
   ///    it did before the setting existed. If that regresses, upgrading breaks
   ///    every installation's ordinary mail clients at once.
   ///
   ///    The handshake-level tests generate their own certificate authority and
   ///    client certificates in-process (CertificateRequest, .NET 4.7.2+), so they
   ///    depend on no external material and no real-world PKI: "require" must admit
   ///    a client whose certificate chains to the port's CA and reject both a
   ///    certificate-less client and a stranger's certificate; "request" must
   ///    never fail a handshake, whatever the client presents.
   /// </summary>
   [TestFixture]
   public class InboundClientCertificates : TestFixtureBase
   {
      private const int PolicyOff = 0;
      private const int PolicyRequest = 1;
      private const int PolicyRequire = 2;

      private static string ExampleCertificatePath
      {
         get { return Paths.Combine(SslSetup.GetSslCertPath(), "example.crt"); }
      }

      private static string ExamplePrivateKeyPath
      {
         get { return Paths.Combine(SslSetup.GetSslCertPath(), "example.key"); }
      }

      private static TCPIPPort FindPortByNumber(TCPIPPorts ports, int portNumber)
      {
         for (var i = 0; i < ports.Count; i++)
         {
            if (ports[i].PortNumber == portNumber)
               return ports[i];
         }

         return null;
      }

      private SSLCertificate AddExampleServerCertificate()
      {
         var certificate = _settings.SSLCertificates.Add();
         certificate.Name = "InboundClientCertificates";
         certificate.CertificateFile = ExampleCertificatePath;
         certificate.PrivateKeyFile = ExamplePrivateKeyPath;
         certificate.Save();

         return certificate;
      }

      /// <summary>
      ///    Puts the server back exactly as every other fixture expects to find it:
      ///    the four default ports (25, 110, 143, 587), no SSL certificates, and the
      ///    listeners rebound to that set. Called from the finally block of every
      ///    test that either binds an extra listener or lets a restore rewrite the
      ///    port table - a leaked port poisons every later fixture.
      /// </summary>
      private void RestoreDefaultPortsAndRebindListeners()
      {
         _application.Stop();

         try
         {
            _settings.TCPIPPorts.SetDefault();
            _settings.SSLCertificates.Clear();
         }
         finally
         {
            _application.Start();
         }
      }

      // ----------------------------------------------------------------------
      // Configuration and validation
      // ----------------------------------------------------------------------

      [Test]
      [Description("The policy setter refuses values outside 0/1/2. The server side fails closed on an " +
                   "unknown stored value (it is treated as require), so a value that slipped through here " +
                   "would not open a hole - it would create a port that rejects every client while the API " +
                   "had reported success.")]
      public void ThePolicySetterRefusesValuesOutsideTheEnum()
      {
         var port = _settings.TCPIPPorts.Add();

         // Never saved, so nothing to clean up - the object exists only in this test.
         Assert.AreEqual(PolicyOff, port.ClientCertificatePolicy,
            "A new port did not default to policy 0 (off).");

         port.ClientCertificatePolicy = PolicyRequest;

         Assert.Throws<COMException>(() => port.ClientCertificatePolicy = 3);
         Assert.Throws<COMException>(() => port.ClientCertificatePolicy = -1);
         Assert.Throws<COMException>(() => port.ClientCertificatePolicy = 99);

         // The refusals left the last valid value in place.
         Assert.AreEqual(PolicyRequest, port.ClientCertificatePolicy,
            "A refused policy value was stored anyway.");

         // And the three real values are all accepted - otherwise "validated"
         // would just mean "broken".
         port.ClientCertificatePolicy = PolicyOff;
         Assert.AreEqual(PolicyOff, port.ClientCertificatePolicy);
         port.ClientCertificatePolicy = PolicyRequest;
         Assert.AreEqual(PolicyRequest, port.ClientCertificatePolicy);
         port.ClientCertificatePolicy = PolicyRequire;
         Assert.AreEqual(PolicyRequire, port.ClientCertificatePolicy);
      }

      [Test]
      [Description("Save refuses a client certificate policy on a plaintext port. A certificate is only ever " +
                   "exchanged during a TLS handshake, so on a CSNone port the policy could never run: the " +
                   "administrator would believe client certificates gate the port while every session walks " +
                   "straight past.")]
      public void SaveRefusesAClientCertificatePolicyOnAPlaintextPort()
      {
         // A brand new plaintext port with a policy must not save...
         var newPort = _settings.TCPIPPorts.Add();
         newPort.Address = "127.0.0.1";
         newPort.PortNumber = TestSetup.GetNextFreePort();
         newPort.Protocol = eSessionType.eSTSMTP;
         newPort.ConnectionSecurity = eConnectionSecurity.eCSNone;
         newPort.ClientCertificatePolicy = PolicyRequest;
         newPort.ClientCertificateCAFile = ExampleCertificatePath;

         var ex = Assert.Throws<COMException>(() => newPort.Save());
         StringAssert.Contains("SSL/TLS or STARTTLS", ex.Message);

         Assert.AreEqual(0, newPort.ID, "The refused port was persisted anyway.");
         Assert.AreEqual(4, _settings.TCPIPPorts.Count,
            "The refused port reached the database - the port table no longer holds the four defaults.");

         // ...and neither must the existing port 25, which every mail client uses.
         var port25 = FindPortByNumber(_settings.TCPIPPorts, 25);
         Assert.IsNotNull(port25, "The default SMTP port 25 was not found.");

         port25.ClientCertificatePolicy = PolicyRequest;
         port25.ClientCertificateCAFile = ExampleCertificatePath;

         ex = Assert.Throws<COMException>(() => port25.Save());
         StringAssert.Contains("SSL/TLS or STARTTLS", ex.Message);

         // The failed Save left the stored port untouched and the port usable.
         var reRead = FindPortByNumber(_settings.TCPIPPorts, 25);
         Assert.AreEqual(PolicyOff, reRead.ClientCertificatePolicy,
            "The refused policy on port 25 reached the database.");
         Assert.AreEqual("", reRead.ClientCertificateCAFile,
            "The refused CA file on port 25 reached the database.");

         using (var connection = new TcpConnection())
         {
            Assert.IsTrue(connection.TestConnect(25),
               "Port 25 stopped accepting connections after the refused Save.");
         }
      }

      [Test]
      [Description("Save refuses a policy with no CA file. Without trust anchors, 'require' rejects every " +
                   "client (nothing chains to an empty CA set) and 'request' verifies nothing while looking " +
                   "configured - both misconfigurations the administrator should hear about at Save time, " +
                   "not discover from the error log after a restart.")]
      public void SaveRefusesAPolicyWithoutACAFile()
      {
         var savedPortId = 0;

         try
         {
            var certificate = AddExampleServerCertificate();

            var port = _settings.TCPIPPorts.Add();
            port.Address = "127.0.0.1";
            port.PortNumber = TestSetup.GetNextFreePort();
            port.Protocol = eSessionType.eSTSMTP;
            port.ConnectionSecurity = eConnectionSecurity.eCSTLS;
            port.SSLCertificateID = certificate.ID;
            port.ClientCertificatePolicy = PolicyRequire;
            port.ClientCertificateCAFile = "";

            var ex = Assert.Throws<COMException>(() => port.Save());
            StringAssert.Contains("CA certificate file", ex.Message);
            Assert.AreEqual(0, port.ID, "The refused port was persisted anyway.");

            // Whitespace is not a CA file either.
            port.ClientCertificateCAFile = "   ";
            ex = Assert.Throws<COMException>(() => port.Save());
            StringAssert.Contains("CA certificate file", ex.Message);
            Assert.AreEqual(0, port.ID, "The refused port was persisted anyway.");

            // The port is still usable: correcting the one mistake lets the same
            // object save. If this fails, the refusal broke the object rather than
            // rejecting the value.
            port.ClientCertificateCAFile = ExampleCertificatePath;
            port.Save();
            savedPortId = port.ID;
            Assert.AreNotEqual(0, savedPortId, "The corrected port did not save.");
         }
         finally
         {
            if (savedPortId != 0)
               _settings.TCPIPPorts.DeleteByDBID(savedPortId);

            _settings.SSLCertificates.Clear();
         }
      }

      [Test]
      [Description("Save refuses 'require' on a STARTTLSOptional port: a client that simply never issues " +
                   "STARTTLS is never asked for a certificate, so the requirement would gate nothing - a " +
                   "lock on a door standing open. 'Request' stays allowed there (it promises only logging), " +
                   "and 'require' stays allowed with required STARTTLS, or over-blocking would make the " +
                   "validation itself the regression.")]
      public void SaveRefusesRequireOnAPortWhereStartTlsIsOptional()
      {
         var savedPortIds = new System.Collections.Generic.List<int>();

         try
         {
            var certificate = AddExampleServerCertificate();

            var port = _settings.TCPIPPorts.Add();
            port.Address = "127.0.0.1";
            port.PortNumber = TestSetup.GetNextFreePort();
            port.Protocol = eSessionType.eSTSMTP;
            port.ConnectionSecurity = eConnectionSecurity.eCSSTARTTLSOptional;
            port.SSLCertificateID = certificate.ID;
            port.ClientCertificatePolicy = PolicyRequire;
            port.ClientCertificateCAFile = ExampleCertificatePath;

            var ex = Assert.Throws<COMException>(() => port.Save());
            StringAssert.Contains("optional STARTTLS", ex.Message);
            Assert.AreEqual(0, port.ID, "The refused port was persisted anyway.");

            // The same port with 'request' is a legal configuration - it exists so an
            // administrator can inventory which clients would survive 'require'.
            port.ClientCertificatePolicy = PolicyRequest;
            port.Save();
            savedPortIds.Add(port.ID);
            Assert.AreNotEqual(0, port.ID, "'Request' with optional STARTTLS was refused.");

            // And 'require' with required STARTTLS is the combination that does
            // enforce, so it must save.
            var requiredPort = _settings.TCPIPPorts.Add();
            requiredPort.Address = "127.0.0.1";
            requiredPort.PortNumber = TestSetup.GetNextFreePort();
            requiredPort.Protocol = eSessionType.eSTSMTP;
            requiredPort.ConnectionSecurity = eConnectionSecurity.eCSSTARTTLSRequired;
            requiredPort.SSLCertificateID = certificate.ID;
            requiredPort.ClientCertificatePolicy = PolicyRequire;
            requiredPort.ClientCertificateCAFile = ExampleCertificatePath;
            requiredPort.Save();
            savedPortIds.Add(requiredPort.ID);
            Assert.AreNotEqual(0, requiredPort.ID, "'Require' with required STARTTLS was refused.");
         }
         finally
         {
            foreach (var id in savedPortIds.Where(id => id != 0))
               _settings.TCPIPPorts.DeleteByDBID(id);

            _settings.SSLCertificates.Clear();
         }
      }

      [Test]
      [Description("The policy and the CA file survive a Save and a re-read from the database. If either is " +
                   "dropped on the way to or from storage, the port silently reverts to accepting anyone " +
                   "after the next service restart while the configuration UI still shows mutual TLS on.")]
      public void ThePolicyAndCAFileSurviveASaveAndAReRead()
      {
         var savedPortId = 0;
         var portNumber = TestSetup.GetNextFreePort();

         try
         {
            var certificate = AddExampleServerCertificate();

            var port = _settings.TCPIPPorts.Add();
            port.Address = "127.0.0.1";
            port.PortNumber = portNumber;
            port.Protocol = eSessionType.eSTSMTP;
            port.ConnectionSecurity = eConnectionSecurity.eCSTLS;
            port.SSLCertificateID = certificate.ID;
            port.ClientCertificatePolicy = PolicyRequire;
            port.ClientCertificateCAFile = ExampleCertificatePath;
            port.Save();
            savedPortId = port.ID;

            // Settings.TCPIPPorts constructs a fresh collection loaded straight from
            // hm_tcpipports on every access, so this observes what was persisted.
            var reRead = _settings.TCPIPPorts.get_ItemByDBID(savedPortId);

            Assert.AreEqual(PolicyRequire, reRead.ClientCertificatePolicy,
               "The policy did not survive the round trip to the database.");
            Assert.AreEqual(ExampleCertificatePath, reRead.ClientCertificateCAFile,
               "The CA file path did not survive the round trip to the database.");
            Assert.AreEqual(portNumber, reRead.PortNumber);
            Assert.AreEqual(eConnectionSecurity.eCSTLS, reRead.ConnectionSecurity);
         }
         finally
         {
            if (savedPortId != 0)
               _settings.TCPIPPorts.DeleteByDBID(savedPortId);

            _settings.SSLCertificates.Clear();
         }
      }

      [Test]
      [Description("An XML backup and restore round-trips the policy and the CA file. If either attribute is " +
                   "missing from the backup, restoring after a disaster silently strips mutual TLS from " +
                   "every port that had it - the port comes back listening, just no longer asking anyone " +
                   "for a certificate.")]
      public void ThePolicyAndCAFileSurviveAnXmlBackupAndRestore()
      {
         var backupDir = Paths.Combine(Path.GetTempPath(), "hmailserver-mtls-backup-" + TestSetup.UniqueString());
         Directory.CreateDirectory(backupDir);

         var portNumber = TestSetup.GetNextFreePort();

         try
         {
            var certificate = AddExampleServerCertificate();

            var port = _settings.TCPIPPorts.Add();
            port.Address = "127.0.0.1";
            port.PortNumber = portNumber;
            port.Protocol = eSessionType.eSTSMTP;
            port.ConnectionSecurity = eConnectionSecurity.eCSTLS;
            port.SSLCertificateID = certificate.ID;
            port.ClientCertificatePolicy = PolicyRequire;
            // The CA file must be a real, loadable PEM: the restore restarts the
            // listeners, and a 'require' port with an unusable CA bundle refuses to
            // start and reports an error - which would fail the next test's setup.
            port.ClientCertificateCAFile = ExampleCertificatePath;
            port.Save();

            var backupSettings = _settings.Backup;
            CustomAsserts.AssertDeleteFile(backupSettings.LogFile);
            backupSettings.BackupDomains = false;
            backupSettings.BackupMessages = false;
            backupSettings.BackupSettings = true;
            backupSettings.CompressDestinationFiles = true;
            backupSettings.Destination = backupDir;

            _application.BackupManager.StartBackup();
            Assert.IsTrue(WaitForBackupCompletion(), "The settings backup did not complete successfully.");

            // Delete the row, so the restore has something to prove.
            _settings.TCPIPPorts.DeleteByDBID(port.ID);
            Assert.IsNull(FindPortByNumber(_settings.TCPIPPorts, portNumber),
               "The port was not deleted before the restore - the test would pass without restoring anything.");

            var backupFile = new DirectoryInfo(backupDir).GetFiles()[0].FullName;
            var startTimeBeforeRestore = _application.Status.StartTime;

            var backup = _application.BackupManager.LoadBackup(backupFile);
            backup.RestoreDomains = false;
            backup.RestoreMessages = false;
            backup.RestoreSettings = true;
            backup.StartRestore();

            WaitForServerRestart(startTimeBeforeRestore);

            var restored = FindPortByNumber(_settings.TCPIPPorts, portNumber);
            Assert.IsNotNull(restored, "The port did not come back from the backup.");
            Assert.AreEqual(PolicyRequire, restored.ClientCertificatePolicy,
               "The policy did not survive the XML backup and restore.");
            Assert.AreEqual(ExampleCertificatePath, restored.ClientCertificateCAFile,
               "The CA file path did not survive the XML backup and restore.");

            // A port that never set the policy restores to 0, not to something else.
            var restored25 = FindPortByNumber(_settings.TCPIPPorts, 25);
            Assert.IsNotNull(restored25, "The default SMTP port did not come back from the backup.");
            Assert.AreEqual(PolicyOff, restored25.ClientCertificatePolicy,
               "The restore put a client certificate policy on a port that never had one.");
         }
         finally
         {
            // The restore rewrote the port table and rebound the listeners, so both
            // must go back to the defaults every other fixture expects.
            RestoreDefaultPortsAndRebindListeners();

            try
            {
               Directory.Delete(backupDir, true);
            }
            catch (IOException)
            {
               // A straggling handle on the backup file is not worth failing the
               // test over; the directory name is unique per run.
            }
         }
      }

      // ----------------------------------------------------------------------
      // The negative control
      // ----------------------------------------------------------------------

      [Test]
      [Description("THE negative control: every default port reads policy 0, and a policy-0 port behaves " +
                   "exactly as before the feature existed - a normal SMTP/POP3/IMAP session over the " +
                   "standard ports still connects, authenticates and delivers. If this regresses, " +
                   "upgrading locks every installation's ordinary mail clients out at once.")]
      public void PortsWithThePolicyOffBehaveExactlyAsBefore()
      {
         // Every default port must read 0 / empty - this is also the 'a port that
         // never set them' half of the round-trip contract.
         var ports = _settings.TCPIPPorts;
         Assert.AreEqual(4, ports.Count);

         for (var i = 0; i < ports.Count; i++)
         {
            Assert.AreEqual(PolicyOff, ports[i].ClientCertificatePolicy,
               $"Default port {ports[i].PortNumber} does not have the client certificate policy off.");
            Assert.AreEqual("", ports[i].ClientCertificateCAFile,
               $"Default port {ports[i].PortNumber} has a CA file configured out of nowhere.");
         }

         // And the ordinary end-to-end flow over those ports is untouched:
         // deliver over SMTP (25), authenticate and fetch over POP3 (110) and
         // IMAP (143), and connect to the submission port (587).
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "mtls-off@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address,
            "Client certificates off", "Delivered over the default ports");

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);
         var pop3Message = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");
         StringAssert.Contains("Client certificates off", pop3Message);

         // A SECOND message for the IMAP half, because the POP3 half consumed the first:
         // Pop3ClientSimulator.GetFirstMessageText issues DELE before QUIT, so by the
         // time IMAP selects the Inbox there is nothing in it and FETCH 1 returns only
         // "OK FETCH completed" - which is what this assertion caught.
         SmtpClientSimulator.StaticSend(account.Address, account.Address,
            "Client certificates off", "Delivered over the default ports");

         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(account.Address, "test"),
            "An ordinary IMAP logon over port 143 failed.");
         Assert.IsTrue(imap.SelectFolder("Inbox"));
         var imapMessage = imap.Fetch("1 RFC822");
         StringAssert.Contains("Client certificates off", imapMessage);
         imap.Disconnect();

         using (var connection = new TcpConnection())
         {
            Assert.IsTrue(connection.TestConnect(587),
               "The submission port stopped accepting connections.");
         }
      }

      // ----------------------------------------------------------------------
      // Handshake enforcement
      //
      // The certificate authority and the client certificates are generated
      // in-process with CertificateRequest, so these tests need no external
      // material, no machine trust store and no real-world PKI. The CA
      // certificate is written to a temp PEM file for the port's
      // ClientCertificateCAFile and deleted again in the finally block.
      // ----------------------------------------------------------------------

      [Test]
      [Description("A 'require' port admits a client whose certificate chains to the port's CA and rejects " +
                   "both a certificate-less client and one presenting a certificate from a different CA. " +
                   "The certificate-less case is enforced by verify_fail_if_no_peer_cert - the verify " +
                   "callback is never invoked when no certificate arrives, so if that flag regresses, " +
                   "'require' silently passes every client that simply declines to send one.")]
      public void ARequirePortAdmitsOnlyClientsPresentingACertificateFromItsCA()
      {
         using (var authority = CreateSelfSignedAuthority("CN=hMailServer Regression Client CA"))
         using (var trustedClient = IssueClientCertificate(authority, "CN=regression-mtls-client"))
         using (var untrustedClient = CreateSelfSignedClientCertificate("CN=regression-mtls-stranger"))
         {
            var caFile = WriteAuthorityPemFile(authority);
            var listenPort = TestSetup.GetNextFreePort();

            try
            {
               _application.Stop();
               try
               {
                  AddMutualTlsSmtpListener(listenPort, PolicyRequire, caFile);
               }
               finally
               {
                  _application.Start();
               }

               // The admitted case first: it proves the listener is up and answering,
               // so the two rejections below mean 'rejected', not 'nothing listening'.
               var banner = TryReadSmtpBannerOverTls(listenPort, trustedClient);
               Assert.IsNotNull(banner,
                  "A client presenting a certificate from the port's own CA was rejected on a 'require' port.");
               Assert.IsTrue(banner.StartsWith("220"),
                  "The mutual-TLS session did not reach the SMTP banner. Received: " + banner);

               Assert.IsNull(TryReadSmtpBannerOverTls(listenPort, null),
                  "A client that presented no certificate at all was admitted on a 'require' port.");

               Assert.IsNull(TryReadSmtpBannerOverTls(listenPort, untrustedClient),
                  "A client presenting a certificate that does not chain to the port's CA was admitted " +
                  "on a 'require' port.");
            }
            finally
            {
               RestoreDefaultPortsAndRebindListeners();
               CustomAsserts.AssertDeleteFile(caFile);
            }
         }
      }

      [Test]
      [Description("A 'request' port must never fail the handshake, whatever the client presents - it exists " +
                   "so an administrator can inventory which clients would survive 'require' before " +
                   "enforcing it. If 'request' starts rejecting, the safe inventory step becomes the " +
                   "outage it was meant to prevent.")]
      public void ARequestPortAsksButNeverFailsTheHandshake()
      {
         using (var authority = CreateSelfSignedAuthority("CN=hMailServer Regression Client CA"))
         using (var trustedClient = IssueClientCertificate(authority, "CN=regression-mtls-client"))
         using (var untrustedClient = CreateSelfSignedClientCertificate("CN=regression-mtls-stranger"))
         {
            var caFile = WriteAuthorityPemFile(authority);
            var listenPort = TestSetup.GetNextFreePort();

            try
            {
               _application.Stop();
               try
               {
                  AddMutualTlsSmtpListener(listenPort, PolicyRequest, caFile);
               }
               finally
               {
                  _application.Start();
               }

               var banner = TryReadSmtpBannerOverTls(listenPort, null);
               Assert.IsNotNull(banner,
                  "A certificate-less client was rejected on a 'request' port - request must never fail " +
                  "the handshake.");
               Assert.IsTrue(banner.StartsWith("220"),
                  "The session did not reach the SMTP banner. Received: " + banner);

               Assert.IsNotNull(TryReadSmtpBannerOverTls(listenPort, untrustedClient),
                  "A client presenting an unverifiable certificate was rejected on a 'request' port - " +
                  "the failure is to be logged, never enforced.");

               Assert.IsNotNull(TryReadSmtpBannerOverTls(listenPort, trustedClient),
                  "A client presenting a verifiable certificate was rejected on a 'request' port.");
            }
            finally
            {
               RestoreDefaultPortsAndRebindListeners();
               CustomAsserts.AssertDeleteFile(caFile);
            }
         }
      }

      // ----------------------------------------------------------------------
      // Helpers
      // ----------------------------------------------------------------------

      private void AddMutualTlsSmtpListener(int portNumber, int policy, string caFile)
      {
         var certificate = AddExampleServerCertificate();

         var port = _settings.TCPIPPorts.Add();
         port.Address = "127.0.0.1";
         port.PortNumber = portNumber;
         port.Protocol = eSessionType.eSTSMTP;
         port.ConnectionSecurity = eConnectionSecurity.eCSTLS;
         port.SSLCertificateID = certificate.ID;
         port.ClientCertificatePolicy = policy;
         port.ClientCertificateCAFile = caFile;
         port.Save();
      }

      /// <summary>
      ///    Performs a TLS handshake against 127.0.0.1:port, optionally presenting
      ///    the given client certificate, and returns the SMTP banner - or null if
      ///    the server refused the session. In TLS 1.2 a refused client certificate
      ///    fails AuthenticateAsClient itself; in TLS 1.3 the client can believe the
      ///    handshake completed and the refusal arrives as a closed connection on
      ///    the first read. Both count as 'refused' here, which is why the banner
      ///    and not the handshake is the oracle.
      /// </summary>
      private static string TryReadSmtpBannerOverTls(int port, X509Certificate2 clientCertificate)
      {
         using (var client = new TcpClient())
         {
            // A connection failure is an infrastructure problem, not a verdict, so
            // Connect stays outside the try below and fails the test loudly.
            client.Connect(IPAddress.Loopback, port);
            client.ReceiveTimeout = 20000;
            client.SendTimeout = 20000;

            LocalCertificateSelectionCallback selection = null;
            if (clientCertificate != null)
               selection = (sender, host, localCertificates, remoteCertificate, issuers) => clientCertificate;

            using (var ssl = new SslStream(client.GetStream(), false,
               (sender, certificate, chain, errors) => true, selection))
            {
               try
               {
                  var clientCertificates = clientCertificate == null
                     ? new X509CertificateCollection()
                     : new X509CertificateCollection(new X509Certificate[] {clientCertificate});

                  ssl.AuthenticateAsClient("localhost", clientCertificates, TcpConnection.ModernProtocols, false);

                  var buffer = new byte[1024];
                  var read = ssl.Read(buffer, 0, buffer.Length);

                  if (read <= 0)
                     return null;

                  return Encoding.ASCII.GetString(buffer, 0, read);
               }
               catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
               {
                  // A handshake alert or a connection closed before the banner:
                  // the server refused the session.
                  return null;
               }
            }
         }
      }

      /// <summary>
      ///    Exports and re-imports the certificate so its private key lands in a
      ///    user key container SChannel can open - SslStream cannot use the
      ///    ephemeral CNG key CertificateRequest attaches. PersistKeySet is
      ///    deliberately not passed, so the container is removed again when the
      ///    certificate is disposed.
      /// </summary>
      private static X509Certificate2 MakeSChannelUsable(X509Certificate2 certificate)
      {
         var pfx = certificate.Export(X509ContentType.Pfx, "regression");

         return new X509Certificate2(pfx, "regression",
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
      }

      private static X509Certificate2 CreateSelfSignedAuthority(string subjectName)
      {
         using (var key = new RSACng(2048))
         {
            var request = new CertificateRequest(subjectName, key,
               HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(
               new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));

            using (var ephemeral = request.CreateSelfSigned(
               DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2)))
            {
               return MakeSChannelUsable(ephemeral);
            }
         }
      }

      private static X509Certificate2 IssueClientCertificate(X509Certificate2 authority, string subjectName)
      {
         using (var key = new RSACng(2048))
         {
            var request = new CertificateRequest(subjectName, key,
               HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(
               new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
               new OidCollection {new Oid("1.3.6.1.5.5.7.3.2") /* id-kp-clientAuth */}, true));

            using (var issued = request.Create(authority,
               DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1),
               Guid.NewGuid().ToByteArray()))
            using (var withKey = issued.CopyWithPrivateKey(key))
            {
               return MakeSChannelUsable(withKey);
            }
         }
      }

      private static X509Certificate2 CreateSelfSignedClientCertificate(string subjectName)
      {
         using (var key = new RSACng(2048))
         {
            var request = new CertificateRequest(subjectName, key,
               HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(
               new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
               new OidCollection {new Oid("1.3.6.1.5.5.7.3.2")}, true));

            using (var ephemeral = request.CreateSelfSigned(
               DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1)))
            {
               return MakeSChannelUsable(ephemeral);
            }
         }
      }

      private static string WriteAuthorityPemFile(X509Certificate2 authority)
      {
         var path = Paths.Combine(Path.GetTempPath(),
            "hmailserver-client-ca-" + TestSetup.UniqueString() + ".pem");

         var pem = "-----BEGIN CERTIFICATE-----\r\n" +
                   Convert.ToBase64String(authority.Export(X509ContentType.Cert),
                      Base64FormattingOptions.InsertLineBreaks) +
                   "\r\n-----END CERTIFICATE-----\r\n";

         File.WriteAllText(path, pem);

         return path;
      }

      private bool WaitForBackupCompletion()
      {
         for (var i = 0; i < 120; i++)
         {
            try
            {
               var contents = TestSetup.ReadExistingTextFile(_settings.Backup.LogFile);

               if (contents.IndexOf("Backup completed successfully", StringComparison.Ordinal) > 0)
                  return true;

               if (contents.IndexOf("BACKUP ERROR:", StringComparison.Ordinal) > 0)
                  return false;
            }
            catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
            {
               // The log file may not exist yet, or the writer may still hold it.
            }

            Thread.Sleep(250);
         }

         return false;
      }

      private void WaitForServerRestart(string startTimeBeforeRestore)
      {
         for (var i = 0; i < 600; i++)
         {
            try
            {
               var startTime = _application.Status.StartTime;

               if (startTime.Length > 0 && startTime != startTimeBeforeRestore)
                  return;
            }
            catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
            {
               // The COM call can fail transiently while the servers restart.
            }

            Thread.Sleep(100);
         }

         throw new Exception("Timeout while waiting for the restore to complete.");
      }
   }
}
