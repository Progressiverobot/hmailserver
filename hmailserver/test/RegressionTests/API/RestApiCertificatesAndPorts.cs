// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using RegressionTests.SSL;
using hMailServer;

namespace RegressionTests.API
{
   /// <summary>
   ///    The certificate write routes and the TCP/IP ports with their certificate
   ///    binding: POST /api/v1/certificates, DELETE /api/v1/certificates/&lt;id&gt;,
   ///    GET and POST /api/v1/ports, PUT and DELETE /api/v1/ports/&lt;id&gt;.
   ///
   ///    Every assertion is made against what the server did, read back through
   ///    COM as the Control Panel reads it (Settings.SSLCertificates and
   ///    Settings.TCPIPPorts), never only against the response. The listeners are
   ///    created when the server starts, so the one test that proves a port is on
   ///    the wire restarts the server between the write and the connection, and
   ///    the fixture's TearDown removes every row it made before the Reinitialize
   ///    that rebuilds the listeners for the next fixture.
   ///
   ///    The port numbers are this fixture's own (25620-25629), the certificate
   ///    names all start with restcert-, and the certificate files are the
   ///    suite's example.crt and example.key.
   /// </summary>
   [TestFixture]
   public class RestApiCertificatesAndPorts : TestFixtureBase
   {
      private const int RestPort = 9122;
      private const string AdminPassword = "testar";

      private const string CertificatePrefix = "restcert-";
      private const int FirstFixturePort = 25620;
      private const int LastFixturePort = 25629;

      private static string ExampleCertificatePath
      {
         get { return Paths.Combine(SslSetup.GetSslCertPath(), "example.crt"); }
      }

      private static string ExamplePrivateKeyPath
      {
         get { return Paths.Combine(SslSetup.GetSslCertPath(), "example.key"); }
      }

      private static string UniqueCertificateName()
      {
         return CertificatePrefix + Guid.NewGuid().ToString("N").Substring(0, 8);
      }

      private void WriteSetting(string key, string value)
      {
         string programDirectory = _application.Settings.Directories.ProgramDirectory;
         string[] candidates =
         {
            Paths.Combine(programDirectory, "hMailServer.ini"),
            Paths.Combine(programDirectory, "Bin", "hMailServer.ini"),
         };

         bool wroteAny = false;
         foreach (string iniPath in candidates.Where(File.Exists))
         {
            Assert.IsTrue(
               IniFile.WritePrivateProfileString("Settings", key, value, iniPath),
               "Failed to write " + key + " to " + iniPath + ".");
            wroteAny = true;
         }

         Assert.IsTrue(wroteAny, "Could not locate an existing hMailServer.ini to update.");
      }

      [SetUp]
      public void StartRestApi()
      {
         Assert.IsTrue(File.Exists(ExampleCertificatePath), "Certificate " + ExampleCertificatePath + " was not found");
         Assert.IsTrue(File.Exists(ExamplePrivateKeyPath), "Private key " + ExamplePrivateKeyPath + " was not found");

         _settings.SetAdministratorPassword(AdminPassword);

         WriteSetting("RestApiBindAddress", "127.0.0.1");
         WriteSetting("RestApiPort", RestPort.ToString());

         _application.Reinitialize();

         (int status, string body) probe = Http("GET", "/api/v1/status");
         Assert.AreEqual(200, probe.status, "REST API did not answer /api/v1/status. Body: " + probe.body);
      }

      [TearDown]
      public void StopRestApi()
      {
         // The rows first, over COM, so that a test that failed between its
         // create and its delete leaves nothing for the next fixture: a port in
         // this fixture's range, a certificate with its prefix. The Reinitialize
         // at the end rebuilds every listener from the cleaned table, so a
         // listener a test bound is gone with its row. The suite's own setup
         // calls TCPIPPorts.SetDefault and SSLCertificates.Clear as well; this
         // is for the run that stops here.
         try
         {
            TCPIPPorts ports = _settings.TCPIPPorts;
            for (int i = ports.Count - 1; i >= 0; i--)
            {
               TCPIPPort port = ports[i];
               if (port.PortNumber >= FirstFixturePort && port.PortNumber <= LastFixturePort)
                  port.Delete();
            }

            SSLCertificates certificates = _settings.SSLCertificates;
            certificates.Refresh();
            for (int i = certificates.Count - 1; i >= 0; i--)
            {
               SSLCertificate certificate = certificates[i];
               if (certificate.Name.StartsWith(CertificatePrefix, StringComparison.OrdinalIgnoreCase))
                  certificate.Delete();
            }
         }
         finally
         {
            WriteSetting("RestApiPort", "0");
            _application.Reinitialize();
         }
      }

      // The certificate through COM, or null. Asked of COM rather than of the
      // API so that the API's answer is checked against something that did not
      // produce it.
      private SSLCertificate CertificateOverCom(int id)
      {
         SSLCertificates certificates = _settings.SSLCertificates;
         certificates.Refresh();
         try
         {
            return certificates.get_ItemByDBID(id);
         }
         catch (COMException)
         {
            return null;
         }
      }

      private TCPIPPort PortOverCom(int id)
      {
         try
         {
            return _settings.TCPIPPorts.get_ItemByDBID(id);
         }
         catch (COMException)
         {
            return null;
         }
      }

      private int CertificateCountOverCom()
      {
         SSLCertificates certificates = _settings.SSLCertificates;
         certificates.Refresh();
         return certificates.Count;
      }

      private int PortCountOverCom()
      {
         return _settings.TCPIPPorts.Count;
      }

      private static string CertificateBody(string name, string certificateFile, string privateKeyFile, string password = null)
      {
         return "{\"name\":\"" + name + "\"," +
                "\"certificate_file\":\"" + JsonString(certificateFile) + "\"," +
                "\"private_key_file\":\"" + JsonString(privateKeyFile) + "\"" +
                (password == null ? "" : ",\"private_key_password\":\"" + password + "\"") + "}";
      }

      private static string PortBody(string protocol, int port, string security, int certificateId, string address = "0.0.0.0")
      {
         return "{\"protocol\":\"" + protocol + "\",\"address\":\"" + address + "\",\"port\":" + port +
                ",\"connection_security\":\"" + security + "\",\"certificate_id\":" + certificateId + "}";
      }

      // A Windows path inside a JSON string: the backslashes doubled.
      private static string JsonString(string value)
      {
         return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
      }

      private (int id, string body) CreateCertificate(string name = null)
      {
         (int status, string body) created = Http("POST", "/api/v1/certificates",
            CertificateBody(name ?? UniqueCertificateName(), ExampleCertificatePath, ExamplePrivateKeyPath));
         Assert.AreEqual(201, created.status, created.body);
         return (ExtractInt(created.body, "id"), created.body);
      }

      // The listeners are built by IOService when the servers start, from the
      // port table as it then is. A row written over the API is on the wire
      // after this, and not before - which the test that uses it asserts both
      // ways.
      private void RestartListeners()
      {
         _application.Stop();
         _application.Start();

         (int status, string body) probe = Http("GET", "/api/v1/status");
         Assert.AreEqual(200, probe.status, "REST API did not come back after the restart. Body: " + probe.body);
      }

      [Test]
      [Description("A certificate is created, listed as the create answered, read back through COM, refused as a duplicate, deleted and gone; the second delete is 404.")]
      public void CertificateRoundTrip()
      {
         string name = UniqueCertificateName();
         int certificatesBefore = CertificateCountOverCom();

         (int status, string body) created = Http("POST", "/api/v1/certificates",
            CertificateBody(name, ExampleCertificatePath, ExamplePrivateKeyPath));
         Assert.AreEqual(201, created.status, created.body);
         StringAssert.Contains("\"name\":\"" + name + "\"", created.body);
         StringAssert.Contains("\"certificate_file\":\"" + JsonString(ExampleCertificatePath) + "\"", created.body);
         StringAssert.Contains("\"private_key_file\":\"" + JsonString(ExamplePrivateKeyPath) + "\"", created.body);
         StringAssert.DoesNotContain("private_key_password", created.body, "The create never emits the password, not even an empty one.");

         int id = ExtractInt(created.body, "id");
         Assert.Greater(id, 0);

         // The listing shows the same entry the create answered with.
         (int listStatus, string list) = Http("GET", "/api/v1/certificates");
         Assert.AreEqual(200, listStatus, list);
         StringAssert.Contains("{\"id\":" + id + ",\"name\":\"" + name + "\"", list);

         // COM sees the certificate the Control Panel would show.
         SSLCertificate certificate = CertificateOverCom(id);
         Assert.IsNotNull(certificate, "The certificate must exist through COM after POST /api/v1/certificates.");
         Assert.AreEqual(name, certificate.Name);
         Assert.AreEqual(ExampleCertificatePath, certificate.CertificateFile);
         Assert.AreEqual(ExamplePrivateKeyPath, certificate.PrivateKeyFile);
         Assert.AreEqual("", certificate.PrivateKeyPassword, "No password was sent, so none is stored.");
         Assert.AreEqual(certificatesBefore + 1, CertificateCountOverCom());

         // The same name again, in another case, is a conflict and creates nothing.
         (int duplicateStatus, string duplicateBody) = Http("POST", "/api/v1/certificates",
            CertificateBody(name.ToUpperInvariant(), ExampleCertificatePath, ExamplePrivateKeyPath));
         Assert.AreEqual(409, duplicateStatus, duplicateBody);
         Assert.AreEqual(certificatesBefore + 1, CertificateCountOverCom(), "The duplicate POST must not have created a second certificate.");

         // Deleted, and gone through COM and from the listing.
         (int deleteStatus, string deleteBody) = Http("DELETE", "/api/v1/certificates/" + id);
         Assert.AreEqual(200, deleteStatus, deleteBody);
         StringAssert.Contains("\"deleted\":true", deleteBody);
         Assert.IsNull(CertificateOverCom(id), "The certificate must be gone through COM after DELETE.");
         StringAssert.DoesNotContain("\"name\":\"" + name + "\"", Http("GET", "/api/v1/certificates").body);
         Assert.AreEqual(certificatesBefore, CertificateCountOverCom());

         Assert.AreEqual(404, Http("DELETE", "/api/v1/certificates/" + id).status);
         Assert.AreEqual(404, Http("DELETE", "/api/v1/certificates/999999999").status);
      }

      [Test]
      [Description("A missing name, certificate file or key file is 400 naming the field; a file that is not on disk is 400 naming the file; none of them creates anything.")]
      public void CertificateRefusals()
      {
         int before = CertificateCountOverCom();
         string missingCertificate = Paths.Combine(SslSetup.GetSslCertPath(), "restcert-does-not-exist.crt");
         string missingKey = Paths.Combine(SslSetup.GetSslCertPath(), "restcert-does-not-exist.key");

         (int status, string body) noName = Http("POST", "/api/v1/certificates",
            "{\"certificate_file\":\"" + JsonString(ExampleCertificatePath) + "\",\"private_key_file\":\"" + JsonString(ExamplePrivateKeyPath) + "\"}");
         Assert.AreEqual(400, noName.status, noName.body);
         StringAssert.Contains("name is required", noName.body);

         (int status, string body) noCertificate = Http("POST", "/api/v1/certificates",
            "{\"name\":\"" + UniqueCertificateName() + "\",\"private_key_file\":\"" + JsonString(ExamplePrivateKeyPath) + "\"}");
         Assert.AreEqual(400, noCertificate.status, noCertificate.body);
         StringAssert.Contains("certificate_file is required", noCertificate.body);

         (int status, string body) noKey = Http("POST", "/api/v1/certificates",
            "{\"name\":\"" + UniqueCertificateName() + "\",\"certificate_file\":\"" + JsonString(ExampleCertificatePath) + "\"}");
         Assert.AreEqual(400, noKey.status, noKey.body);
         StringAssert.Contains("private_key_file is required", noKey.body);

         // COM would save these paths and the listener would fail at the next
         // start; the route refuses now and names the file that is not there.
         (int status, string body) badCertificate = Http("POST", "/api/v1/certificates",
            CertificateBody(UniqueCertificateName(), missingCertificate, ExamplePrivateKeyPath));
         Assert.AreEqual(400, badCertificate.status, badCertificate.body);
         StringAssert.Contains("certificate_file does not exist", badCertificate.body);
         StringAssert.Contains("restcert-does-not-exist.crt", badCertificate.body, "The refusal names the file.");

         (int status, string body) badKey = Http("POST", "/api/v1/certificates",
            CertificateBody(UniqueCertificateName(), ExampleCertificatePath, missingKey));
         Assert.AreEqual(400, badKey.status, badKey.body);
         StringAssert.Contains("private_key_file does not exist", badKey.body);
         StringAssert.Contains("restcert-does-not-exist.key", badKey.body);

         Assert.AreEqual(before, CertificateCountOverCom(), "No refused POST may have created a certificate.");
      }

      [Test]
      [Description("private_key_password is stored as COM stores it and readable back through COM by the administrator, and no route ever emits it.")]
      public void PrivateKeyPasswordIsAcceptedAndNeverEmitted()
      {
         string name = UniqueCertificateName();
         const string password = "restcert-Passphrase-6170";

         (int status, string body) created = Http("POST", "/api/v1/certificates",
            CertificateBody(name, ExampleCertificatePath, ExamplePrivateKeyPath, password));
         Assert.AreEqual(201, created.status, created.body);
         StringAssert.DoesNotContain("private_key_password", created.body);
         StringAssert.DoesNotContain(password, created.body);

         int id = ExtractInt(created.body, "id");

         SSLCertificate certificate = CertificateOverCom(id);
         Assert.IsNotNull(certificate);
         Assert.AreEqual(password, certificate.PrivateKeyPassword,
            "The password is stored protected and read back by the administrator, exactly as one set in the Control Panel.");

         (int listStatus, string list) = Http("GET", "/api/v1/certificates");
         Assert.AreEqual(200, listStatus, list);
         StringAssert.Contains("\"name\":\"" + name + "\"", list);
         StringAssert.DoesNotContain("private_key_password", list);
         StringAssert.DoesNotContain(password, list);

         Assert.AreEqual(200, Http("DELETE", "/api/v1/certificates/" + id).status);
         Assert.IsNull(CertificateOverCom(id));
      }

      [Test]
      [Description("A TLS port is created bound to a certificate, read back through COM, listed, on the wire only after a restart, changed by PUT to STARTTLS (observed after another restart); the bound certificate cannot be deleted until the port is; then both go.")]
      public void PortRoundTripAndListenerAfterRestart()
      {
         (int certificateId, string certificateBody) = CreateCertificate();
         const int portNumber = FirstFixturePort + 1;
         int portsBefore = PortCountOverCom();

         (int status, string body) created = Http("POST", "/api/v1/ports", PortBody("imap", portNumber, "tls", certificateId));
         Assert.AreEqual(201, created.status, created.body);
         StringAssert.Contains("\"protocol\":\"imap\"", created.body);
         StringAssert.Contains("\"address\":\"0.0.0.0\"", created.body);
         StringAssert.Contains("\"port\":" + portNumber + ",", created.body);
         StringAssert.Contains("\"connection_security\":\"tls\"", created.body);
         StringAssert.Contains("\"certificate_id\":" + certificateId, created.body);
         StringAssert.Contains("\"client_certificate_policy\":\"off\"", created.body);

         int portId = ExtractInt(created.body, "id");
         Assert.Greater(portId, 0);

         // The listing shows the entry the create answered with.
         (int listStatus, string list) = Http("GET", "/api/v1/ports");
         Assert.AreEqual(200, listStatus, list);
         StringAssert.Contains("{\"id\":" + portId + ",\"protocol\":\"imap\",\"address\":\"0.0.0.0\",\"port\":" + portNumber +
                               ",\"connection_security\":\"tls\",\"certificate_id\":" + certificateId, list);

         // COM sees the port the Control Panel would show.
         TCPIPPort port = PortOverCom(portId);
         Assert.IsNotNull(port, "The port must exist through COM after POST /api/v1/ports.");
         Assert.AreEqual(eSessionType.eSTIMAP, port.Protocol);
         Assert.AreEqual("0.0.0.0", port.Address);
         Assert.AreEqual(portNumber, port.PortNumber);
         Assert.AreEqual(eConnectionSecurity.eCSTLS, port.ConnectionSecurity);
         Assert.AreEqual(certificateId, port.SSLCertificateID);
         Assert.AreEqual(0, port.ClientCertificatePolicy);
         Assert.AreEqual(portsBefore + 1, PortCountOverCom());

         // Not on the wire yet: the listeners were built when the server
         // started, before the row existed. This is the property the OpenAPI
         // text states, and the reason the fixture restarts below.
         using (var early = new TcpConnection())
         {
            Assert.IsFalse(early.Connect(portNumber), "A port written over the API must not be listening before the server restarts.");
         }

         RestartListeners();

         // On the wire, with the certificate: a TLS handshake straight away,
         // then the IMAP greeting inside it.
         using (var tls = new TcpConnection(true))
         {
            Assert.IsTrue(tls.Connect(portNumber), "The TLS port must accept a connection after the restart.");
            Assert.IsNotNull(tls.RemoteCertificate, "The handshake must have presented the bound certificate.");
            StringAssert.Contains("* OK", tls.Receive());
         }

         // PUT: the whole record, with the security changed. The response, the
         // listing and COM all agree; the listener does not, until the restart.
         (int putStatus, string putBody) = Http("PUT", "/api/v1/ports/" + portId, PortBody("imap", portNumber, "starttls_optional", certificateId));
         Assert.AreEqual(200, putStatus, putBody);
         StringAssert.Contains("\"id\":" + portId + ",", putBody);
         StringAssert.Contains("\"connection_security\":\"starttls_optional\"", putBody);
         StringAssert.Contains("\"connection_security\":\"starttls_optional\"", Http("GET", "/api/v1/ports").body);
         Assert.AreEqual(eConnectionSecurity.eCSSTARTTLSOptional, PortOverCom(portId).ConnectionSecurity);
         Assert.AreEqual(certificateId, PortOverCom(portId).SSLCertificateID);

         RestartListeners();

         // Plain text now, offering STARTTLS - and a handshake over it works,
         // which is the certificate binding surviving the PUT.
         using (var plain = new TcpConnection())
         {
            Assert.IsTrue(plain.Connect(portNumber));
            StringAssert.Contains("* OK", plain.Receive());
            string capabilities = plain.SendAndReceive("A1 CAPABILITY\r\n");
            StringAssert.Contains("STARTTLS", capabilities, "The port is STARTTLS after the PUT and the restart.");
            StringAssert.Contains("A2 OK", plain.SendAndReceive("A2 STARTTLS\r\n"));
            plain.HandshakeAsClient();
            Assert.IsNotNull(plain.RemoteCertificate);
            StringAssert.Contains("A3 OK", plain.SendAndReceive("A3 NOOP\r\n"));
         }

         // The certificate is bound, so it cannot go: 409 naming the port, and
         // COM still has it.
         (int boundStatus, string boundBody) = Http("DELETE", "/api/v1/certificates/" + certificateId);
         Assert.AreEqual(409, boundStatus, boundBody);
         StringAssert.Contains("bound to port " + portId, boundBody);
         StringAssert.Contains("\"port_id\":" + portId, boundBody);
         Assert.IsNotNull(CertificateOverCom(certificateId), "The refused DELETE must not have removed the certificate.");
         Assert.IsNotNull(PortOverCom(portId));

         // The port first, then the certificate; a second delete of either is 404.
         (int portDeleteStatus, string portDeleteBody) = Http("DELETE", "/api/v1/ports/" + portId);
         Assert.AreEqual(200, portDeleteStatus, portDeleteBody);
         StringAssert.Contains("\"deleted\":true", portDeleteBody);
         Assert.IsNull(PortOverCom(portId), "The port must be gone through COM after DELETE.");
         Assert.AreEqual(portsBefore, PortCountOverCom());
         StringAssert.DoesNotContain("\"port\":" + portNumber + ",", Http("GET", "/api/v1/ports").body);

         (int certificateDeleteStatus, string certificateDeleteBody) = Http("DELETE", "/api/v1/certificates/" + certificateId);
         Assert.AreEqual(200, certificateDeleteStatus, certificateDeleteBody);
         Assert.IsNull(CertificateOverCom(certificateId));

         Assert.AreEqual(404, Http("DELETE", "/api/v1/ports/" + portId).status);
         Assert.AreEqual(404, Http("PUT", "/api/v1/ports/" + portId, PortBody("imap", portNumber, "none", 0)).status);
         Assert.AreEqual(404, Http("DELETE", "/api/v1/certificates/" + certificateId).status);
      }

      [Test]
      [Description("Every refusal of a port write: missing or unrecognised fields, TLS without a certificate, a certificate id that names nothing, a client certificate policy the port cannot enforce, a duplicate address and port, an unknown id - and none of them writes a row.")]
      public void PortRefusals()
      {
         (int certificateId, string certificateBody) = CreateCertificate();
         const int portNumber = FirstFixturePort + 2;
         int portsBefore = PortCountOverCom();

         (int status, string body) noProtocol = Http("POST", "/api/v1/ports", "{\"port\":" + portNumber + "}");
         Assert.AreEqual(400, noProtocol.status, noProtocol.body);
         StringAssert.Contains("protocol is required", noProtocol.body);

         (int status, string body) badProtocol = Http("POST", "/api/v1/ports", PortBody("http", portNumber, "none", 0));
         Assert.AreEqual(400, badProtocol.status, badProtocol.body);
         StringAssert.Contains("protocol must be smtp, pop3 or imap", badProtocol.body);

         (int status, string body) noPort = Http("POST", "/api/v1/ports", "{\"protocol\":\"smtp\"}");
         Assert.AreEqual(400, noPort.status, noPort.body);
         StringAssert.Contains("port is required", noPort.body);

         (int status, string body) bigPort = Http("POST", "/api/v1/ports", PortBody("smtp", 70000, "none", 0));
         Assert.AreEqual(400, bigPort.status, bigPort.body);
         StringAssert.Contains("port must be between 1 and 65535", bigPort.body);

         (int status, string body) badAddress = Http("POST", "/api/v1/ports", PortBody("smtp", portNumber, "none", 0, "not-an-address"));
         Assert.AreEqual(400, badAddress.status, badAddress.body);
         StringAssert.Contains("address must be an IP address", badAddress.body);

         (int status, string body) badSecurity = Http("POST", "/api/v1/ports", PortBody("smtp", portNumber, "ssl", certificateId));
         Assert.AreEqual(400, badSecurity.status, badSecurity.body);
         StringAssert.Contains("connection_security must be", badSecurity.body);

         // The limitation check's own sentence, the one the Control Panel shows.
         (int status, string body) noCertificate = Http("POST", "/api/v1/ports", PortBody("smtp", portNumber, "tls", 0));
         Assert.AreEqual(400, noCertificate.status, noCertificate.body);
         StringAssert.Contains("Certificate must be specified", noCertificate.body);

         Assert.AreEqual(400, Http("POST", "/api/v1/ports", PortBody("smtp", portNumber, "starttls_required", 0)).status);

         // A certificate id that names nothing: COM stores it, this route does not.
         (int status, string body) unknownCertificate = Http("POST", "/api/v1/ports", PortBody("smtp", portNumber, "tls", 999999999));
         Assert.AreEqual(400, unknownCertificate.status, unknownCertificate.body);
         StringAssert.Contains("certificate_id does not name a certificate", unknownCertificate.body);

         // Client certificates on a plaintext port: refused by the same check
         // that refuses it over COM, in its words.
         (int status, string body) policyOnPlain = Http("POST", "/api/v1/ports",
            "{\"protocol\":\"smtp\",\"port\":" + portNumber + ",\"connection_security\":\"none\",\"client_certificate_policy\":\"require\",\"client_certificate_ca_file\":\"" + JsonString(ExampleCertificatePath) + "\"}");
         Assert.AreEqual(400, policyOnPlain.status, policyOnPlain.body);
         StringAssert.Contains("Client certificates can only be requested or required", policyOnPlain.body);

         (int status, string body) badPolicy = Http("POST", "/api/v1/ports",
            "{\"protocol\":\"smtp\",\"port\":" + portNumber + ",\"client_certificate_policy\":\"always\"}");
         Assert.AreEqual(400, badPolicy.status, badPolicy.body);
         StringAssert.Contains("client_certificate_policy must be off, request or require", badPolicy.body);

         // The suite's default SMTP port is 25 on every address; a second row
         // for it is a conflict, and so is a second row for a port this
         // fixture creates.
         (int status, string body) defaultDuplicate = Http("POST", "/api/v1/ports", PortBody("smtp", 25, "none", 0));
         Assert.AreEqual(409, defaultDuplicate.status, defaultDuplicate.body);
         StringAssert.Contains("already listens on that address and port", defaultDuplicate.body);

         Assert.AreEqual(portsBefore, PortCountOverCom(), "No refused POST may have created a port.");

         (int status, string body) created = Http("POST", "/api/v1/ports", PortBody("smtp", portNumber, "starttls_required", certificateId));
         Assert.AreEqual(201, created.status, created.body);
         int portId = ExtractInt(created.body, "id");
         Assert.AreEqual(eConnectionSecurity.eCSSTARTTLSRequired, PortOverCom(portId).ConnectionSecurity);

         (int status, string body) duplicate = Http("POST", "/api/v1/ports", PortBody("imap", portNumber, "none", 0));
         Assert.AreEqual(409, duplicate.status, duplicate.body);
         StringAssert.Contains("\"port_id\":" + portId, duplicate.body);
         Assert.AreEqual(portsBefore + 1, PortCountOverCom());

         // A PUT that would collide with another row is a conflict too, and
         // the row is unchanged; a PUT with a bad field is 400 and unchanged.
         (int status, string body) createdOther = Http("POST", "/api/v1/ports", PortBody("pop3", portNumber + 1, "none", 0));
         Assert.AreEqual(201, createdOther.status, createdOther.body);
         int otherId = ExtractInt(createdOther.body, "id");

         Assert.AreEqual(409, Http("PUT", "/api/v1/ports/" + otherId, PortBody("pop3", portNumber, "none", 0)).status);
         Assert.AreEqual(portNumber + 1, PortOverCom(otherId).PortNumber);

         Assert.AreEqual(400, Http("PUT", "/api/v1/ports/" + otherId, PortBody("pop3", portNumber + 1, "tls", 0)).status);
         Assert.AreEqual(eConnectionSecurity.eCSNone, PortOverCom(otherId).ConnectionSecurity);

         // A PUT of a row onto itself is not a conflict with itself.
         Assert.AreEqual(200, Http("PUT", "/api/v1/ports/" + otherId, PortBody("pop3", portNumber + 1, "none", 0)).status);

         Assert.AreEqual(404, Http("PUT", "/api/v1/ports/999999999", PortBody("smtp", portNumber + 3, "none", 0)).status);
         Assert.AreEqual(404, Http("DELETE", "/api/v1/ports/999999999").status);
         Assert.AreEqual(404, Http("PUT", "/api/v1/ports/abc", PortBody("smtp", portNumber + 3, "none", 0)).status,
            "A non-numeric id is not a route.");

         Assert.AreEqual(200, Http("DELETE", "/api/v1/ports/" + otherId).status);
         Assert.AreEqual(200, Http("DELETE", "/api/v1/ports/" + portId).status);
         Assert.AreEqual(200, Http("DELETE", "/api/v1/certificates/" + certificateId).status);
         Assert.AreEqual(portsBefore, PortCountOverCom());
      }

      [Test]
      [Description("A key restricted to named domains reaches none of these server-wide routes, whatever its scope; a read-only key may list ports and change nothing; an unrestricted full key does what the administrator does.")]
      public void ScopedAndReadOnlyKeysAreRefused()
      {
         (string scopedId, string scopedKey) = CreateKey("restcert - scoped", "full", "example.test");
         (string readOnlyId, string readOnlyKey) = CreateKey("restcert - readonly", "readonly", null);
         (string fullId, string fullKey) = CreateKey("restcert - full", "full", null);

         int certificatesBefore = CertificateCountOverCom();
         int portsBefore = PortCountOverCom();
         string name = UniqueCertificateName();
         const int portNumber = FirstFixturePort + 5;

         try
         {
            // Domain-scoped: every one of the routes is server-wide and refused.
            Assert.AreEqual(403, Bearer("POST", "/api/v1/certificates", scopedKey, CertificateBody(name, ExampleCertificatePath, ExamplePrivateKeyPath)).status);
            Assert.AreEqual(403, Bearer("DELETE", "/api/v1/certificates/1", scopedKey).status);
            Assert.AreEqual(403, Bearer("GET", "/api/v1/ports", scopedKey).status);
            Assert.AreEqual(403, Bearer("POST", "/api/v1/ports", scopedKey, PortBody("smtp", portNumber, "none", 0)).status);
            Assert.AreEqual(403, Bearer("PUT", "/api/v1/ports/1", scopedKey, PortBody("smtp", portNumber, "none", 0)).status);
            Assert.AreEqual(403, Bearer("DELETE", "/api/v1/ports/1", scopedKey).status);

            // Read-only: the listing is a read; everything else is refused.
            Assert.AreEqual(200, Bearer("GET", "/api/v1/ports", readOnlyKey).status);
            Assert.AreEqual(403, Bearer("POST", "/api/v1/certificates", readOnlyKey, CertificateBody(name, ExampleCertificatePath, ExamplePrivateKeyPath)).status);
            Assert.AreEqual(403, Bearer("DELETE", "/api/v1/certificates/1", readOnlyKey).status);
            Assert.AreEqual(403, Bearer("POST", "/api/v1/ports", readOnlyKey, PortBody("smtp", portNumber, "none", 0)).status);
            Assert.AreEqual(403, Bearer("PUT", "/api/v1/ports/1", readOnlyKey, PortBody("smtp", portNumber, "none", 0)).status);
            Assert.AreEqual(403, Bearer("DELETE", "/api/v1/ports/1", readOnlyKey).status);

            // No credential at all is 401.
            Assert.AreEqual(401, Http("POST", "/api/v1/ports", null, PortBody("smtp", portNumber, "none", 0)).status);

            Assert.AreEqual(certificatesBefore, CertificateCountOverCom(), "No refused request may have created a certificate.");
            Assert.AreEqual(portsBefore, PortCountOverCom(), "No refused request may have created a port.");

            // An unrestricted full key carries the administrator's authority.
            (int status, string body) created = Bearer("POST", "/api/v1/certificates", fullKey, CertificateBody(name, ExampleCertificatePath, ExamplePrivateKeyPath));
            Assert.AreEqual(201, created.status, created.body);
            int certificateId = ExtractInt(created.body, "id");
            Assert.IsNotNull(CertificateOverCom(certificateId));

            (int portStatus, string portBody) = Bearer("POST", "/api/v1/ports", fullKey, PortBody("pop3", portNumber, "starttls_optional", certificateId));
            Assert.AreEqual(201, portStatus, portBody);
            int portId = ExtractInt(portBody, "id");
            Assert.IsNotNull(PortOverCom(portId));

            Assert.AreEqual(200, Bearer("DELETE", "/api/v1/ports/" + portId, fullKey).status);
            Assert.AreEqual(200, Bearer("DELETE", "/api/v1/certificates/" + certificateId, fullKey).status);
            Assert.IsNull(PortOverCom(portId));
            Assert.IsNull(CertificateOverCom(certificateId));
         }
         finally
         {
            Http("DELETE", "/api/v1/apikeys/" + scopedId);
            Http("DELETE", "/api/v1/apikeys/" + readOnlyId);
            Http("DELETE", "/api/v1/apikeys/" + fullId);
         }
      }

      private static (string id, string key) CreateKey(string label, string scope, string domains)
      {
         string body = "{\"label\":\"" + label + "\",\"scope\":\"" + scope + "\"" +
                       (domains == null ? "" : ",\"domains\":\"" + domains + "\"") + "}";
         (int status, string created) = Http("POST", "/api/v1/apikeys", body);
         Assert.AreEqual(201, status, "POST /api/v1/apikeys must create a key. Body: " + created);
         return (Extract(created, "id"), Extract(created, "key"));
      }

      // The string value of a top-level JSON property, enough for the bodies this API returns.
      private static string Extract(string json, string key)
      {
         Match match = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
         Assert.IsTrue(match.Success, "No '" + key + "' in: " + json);
         return match.Groups[1].Value;
      }

      // The integer value of a top-level JSON property.
      private static int ExtractInt(string json, string key)
      {
         Match match = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?[0-9]+)");
         Assert.IsTrue(match.Success, "No numeric '" + key + "' in: " + json);
         return int.Parse(match.Groups[1].Value);
      }

      private static (int status, string body) Http(string method, string path, string requestBody = null)
      {
         string credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes("Administrator:" + AdminPassword));
         return Http(method, path, "Basic " + credentials, requestBody);
      }

      private static (int status, string body) Bearer(string method, string path, string token, string requestBody = null)
      {
         return Http(method, path, "Bearer " + token, requestBody);
      }

      // Issues one HTTP/1.0 request against the REST listener and returns the
      // parsed status code and body. authorization is the complete header value
      // or null to send none. The connect is retried briefly to absorb the
      // listener bind race right after a reinitialize or a restart.
      private static (int status, string body) Http(string method, string path, string authorization, string requestBody)
      {
         using (var client = new TcpClient())
         {
            Exception last = null;
            for (int attempt = 0; attempt < 25; attempt++)
            {
               try
               {
                  client.Connect("127.0.0.1", RestPort);
                  last = null;
                  break;
               }
               catch (SocketException ex)
               {
                  last = ex;
                  Thread.Sleep(200);
               }
            }

            if (last != null)
               throw last;

            using (NetworkStream stream = client.GetStream())
            using (var memory = new MemoryStream())
            {
               var headers = new StringBuilder();
               headers.Append(method + " " + path + " HTTP/1.0\r\n");
               headers.Append("Host: 127.0.0.1\r\n");
               if (authorization != null)
                  headers.Append("Authorization: " + authorization + "\r\n");

               byte[] bodyBytes = requestBody == null ? new byte[0] : Encoding.UTF8.GetBytes(requestBody);
               if (requestBody != null)
               {
                  headers.Append("Content-Type: application/json\r\n");
                  headers.Append("Content-Length: " + bodyBytes.Length + "\r\n");
               }

               headers.Append("Connection: close\r\n\r\n");

               byte[] headerBytes = Encoding.ASCII.GetBytes(headers.ToString());
               stream.Write(headerBytes, 0, headerBytes.Length);
               if (bodyBytes.Length > 0)
                  stream.Write(bodyBytes, 0, bodyBytes.Length);

               byte[] buffer = new byte[4096];
               int read;
               while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                  memory.Write(buffer, 0, read);

               string raw = Encoding.UTF8.GetString(memory.ToArray());

               int statusCode = 0;
               string[] lines = raw.Split(new[] { "\r\n" }, StringSplitOptions.None);
               if (lines.Length > 0)
               {
                  string[] parts = lines[0].Split(' ');
                  if (parts.Length >= 2)
                     int.TryParse(parts[1], out statusCode);
               }

               int separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
               string body = separator >= 0 ? raw.Substring(separator + 4) : "";

               return (statusCode, body);
            }
         }
      }
   }
}
