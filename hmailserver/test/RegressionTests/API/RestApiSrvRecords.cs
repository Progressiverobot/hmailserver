// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using RegressionTests.SSL;

namespace RegressionTests.API
{
   /// <summary>
   ///    GET /api/v1/srv: ready-to-publish client-discovery SRV records
   ///    (RFC 6186/8314 plus the Outlook _autodiscover convention), generated
   ///    from the ports that are actually configured - the same
   ///    one-source-of-truth idea as /api/v1/tlsa, which hashes the real
   ///    certificates instead of asking the administrator to restate them.
   ///
   ///    What these tests pin: a record exists for every service that is
   ///    genuinely enabled, and - the half that matters more - no record
   ///    exists for a service that is not. A published SRV pointing at a port
   ///    nothing serves sends every client of that domain to a dead socket,
   ///    which is worse than publishing nothing. Specifically: no SSL port
   ///    means no _imaps/_pop3s/_submissions, port 25 is never advertised as
   ///    client submission, a loopback-bound port is not advertised at all,
   ///    and the OpenAPI document describes the route (the same contract
   ///    RestApiQuarantineAndAliases enforces for every other route).
   /// </summary>
   [TestFixture]
   public class RestApiSrvRecords : TestFixtureBase
   {
      // From the 11420-11429 range reserved for this work; every other REST
      // fixture uses 9098 and both can never run at once, but a distinct port
      // means a leaked listener from either fixture cannot fail the other.
      private const int RestPort = 11420;

      // TestSetup.Authenticate() already expects this to be the administrator
      // password, and the REST API authenticates against the same credential.
      private const string AdminPassword = "testar";

      // Issues a minimal authenticated HTTP/1.0 request against the REST
      // listener and returns the parsed status code and body. The connect is
      // retried briefly to absorb the listener bind race right after a
      // reinitialize.
      private static (int status, string body) Http(string method, string path)
      {
         string credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes("Administrator:" + AdminPassword));

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
               byte[] request = Encoding.ASCII.GetBytes(
                  method + " " + path + " HTTP/1.0\r\n" +
                  "Host: 127.0.0.1\r\n" +
                  "Authorization: Basic " + credentials + "\r\n" +
                  "Connection: close\r\n\r\n");

               stream.Write(request, 0, request.Length);

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

      [SetUp]
      public void StartRestApi()
      {
         _settings.SetAdministratorPassword(AdminPassword);

         IniFileSetting.Write("RestApiBindAddress", "127.0.0.1");
         IniFileSetting.Write("RestApiPort", RestPort.ToString());

         // Reinitialize (not Stop/Start): RestApiPort is cached in
         // IniFileSettings, which is only re-read by InitInstance().
         _application.Reinitialize();

         (int status, string body) probe = Http("GET", "/api/v1/status");
         Assert.AreEqual(200, probe.status, "REST API did not answer /api/v1/status. Body: " + probe.body);
      }

      [TearDown]
      public void StopRestApi()
      {
         IniFileSetting.Write("RestApiPort", "0");
         _application.Reinitialize();
      }

      [Test]
      [Description("The default port set yields _imap/_pop3/_submission records, no secure-service records, no port 25 and no autodiscover")]
      public void SrvRecordsCoverEnabledServicesAndOmitDisabled()
      {
         // PerformBasicSetup has restored the default port set: 25/SMTP,
         // 110/POP3, 143/IMAP and 587/SMTP, all plain, all on 0.0.0.0. So the
         // enabled services are exactly the three plain client protocols.
         (int status, string body) = Http("GET", "/api/v1/srv");
         Assert.AreEqual(200, status, body);

         StringAssert.Contains("\"service\":\"_imap._tcp\",\"priority\":0,\"weight\":1,\"port\":143", body);
         StringAssert.Contains("\"service\":\"_pop3._tcp\",\"priority\":0,\"weight\":1,\"port\":110", body);
         StringAssert.Contains("\"service\":\"_submission._tcp\",\"priority\":0,\"weight\":1,\"port\":587", body);

         // No implicit-TLS listener exists in this configuration, so the
         // secure-service names must be absent - the "omit what is not
         // served" half, which is the half a lazy implementation gets wrong
         // by emitting a fixed record set.
         Assert.IsFalse(body.Contains("_imaps._tcp"),
            "No SSL IMAP port exists, so no _imaps record may be advertised. Body: " + body);
         Assert.IsFalse(body.Contains("_pop3s._tcp"),
            "No SSL POP3 port exists, so no _pop3s record may be advertised. Body: " + body);
         Assert.IsFalse(body.Contains("_submissions._tcp"),
            "No SSL SMTP port exists, so no _submissions record may be advertised. Body: " + body);

         // Port 25 is the MX port. Whatever its security setting, it must
         // never be advertised for client submission.
         Assert.IsFalse(body.Contains("\"port\":25}"),
            "Port 25 must never be advertised as a client-discovery service. Body: " + body);
         Assert.IsFalse(body.Contains(" IN SRV 0 1 25 "),
            "Port 25 must never appear in a published record. Body: " + body);

         // Autodiscover requires a RUNNING web-services HTTPS listener, and
         // there is none in this configuration.
         Assert.IsFalse(body.Contains("_autodiscover._tcp"),
            "No web-services HTTPS listener is running, so no autodiscover record may be advertised. Body: " + body);

         // And the records are per-domain and ready to publish: owner name
         // under the fixture's domain, trailing-dot target.
         StringAssert.Contains("\"record\":\"_imap._tcp.example.test. IN SRV 0 1 143 ", body);
      }

      [Test]
      [Description("An SSL port is advertised as its secure service; a loopback-bound port is not advertised at all")]
      public void SslPortIsAdvertisedAndLoopbackPortIsNot()
      {
         // A certificate is required to save an SSL port over COM. The SSL
         // examples directory is the same one SslSetup builds listeners from.
         string sslPath = SslSetup.GetSslCertPath();

         SSLCertificate certificate = _settings.SSLCertificates.Add();
         certificate.Name = "SrvRecordsTest";
         certificate.CertificateFile = Path.Combine(sslPath, "example.crt");
         certificate.PrivateKeyFile = Path.Combine(sslPath, "example.key");
         certificate.Save();

         try
         {
            // A reachable implicit-TLS IMAP port. A configuration row only -
            // nothing binds it until a restart - which is exactly the
            // semantics every client-discovery answer this server gives.
            TCPIPPort imapsPort = _settings.TCPIPPorts.Add();
            imapsPort.Address = "0.0.0.0";
            imapsPort.PortNumber = 993;
            imapsPort.Protocol = eSessionType.eSTIMAP;
            imapsPort.ConnectionSecurity = eConnectionSecurity.eCSTLS;
            imapsPort.SSLCertificateID = certificate.ID;
            imapsPort.Save();

            // A loopback-bound implicit-TLS POP3 port: real configuration,
            // but no machine that resolves a published SRV record can reach
            // it, so it must not be advertised.
            TCPIPPort loopbackPop3s = _settings.TCPIPPorts.Add();
            loopbackPop3s.Address = "127.0.0.1";
            loopbackPop3s.PortNumber = 11428;
            loopbackPop3s.Protocol = eSessionType.eSTPOP3;
            loopbackPop3s.ConnectionSecurity = eConnectionSecurity.eCSTLS;
            loopbackPop3s.SSLCertificateID = certificate.ID;
            loopbackPop3s.Save();

            (int status, string body) = Http("GET", "/api/v1/srv");
            Assert.AreEqual(200, status, body);

            StringAssert.Contains("\"service\":\"_imaps._tcp\",\"priority\":0,\"weight\":1,\"port\":993", body);
            StringAssert.Contains("\"record\":\"_imaps._tcp.example.test. IN SRV 0 1 993 ", body);

            Assert.IsFalse(body.Contains("_pop3s._tcp"),
               "A loopback-bound port must not be advertised: only remote clients resolve SRV records. Body: " + body);
            Assert.IsFalse(body.Contains("11428"),
               "The loopback-bound port number must not appear anywhere in the answer. Body: " + body);
         }
         finally
         {
            // The default port set every other fixture assumes, then the
            // certificate the ports referenced. Order matters: a certificate
            // still referenced by a port row should not be deleted first.
            _settings.TCPIPPorts.SetDefault();
            _settings.SSLCertificates.Clear();
         }
      }

      [Test]
      [Description("The OpenAPI document describes /api/v1/srv - a route without documentation is how the description drifts")]
      public void OpenApiDocumentDescribesSrvRoute()
      {
         // RestApiQuarantineAndAliases pins the full route list; this pins the
         // route this fixture owns, so the two contracts fail independently.
         (int status, string body) = Http("GET", "/api/v1/openapi.json");

         Assert.AreEqual(200, status, body);
         StringAssert.Contains("\"/api/v1/srv\"", body,
            "The OpenAPI document must describe /api/v1/srv.");
      }
   }
}
