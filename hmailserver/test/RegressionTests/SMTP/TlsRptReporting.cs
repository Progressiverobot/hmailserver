// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using RegressionTests.SSL;

namespace RegressionTests.SMTP
{
   /// <summary>
   ///    SMTP TLS reporting (RFC 8460), end to end: a TLS session outcome recorded
   ///    during a real MX-path delivery is discovered via the recipient domain's
   ///    _smtp._tls TXT record, built into an application/tlsrpt+json report and
   ///    delivered as mail to the rua= mailbox.
   ///
   ///    Nothing in the shipped schedule can be tested directly - the reporter
   ///    runs hourly and only mails days that are over - which is exactly why
   ///    Utilities.SendTlsRptReports exists: it sends what has been collected
   ///    right now, optionally including today's still-accumulating bucket. An
   ///    administrator uses it to see a first report minutes after configuring
   ///    TlsRptFromAddress; this fixture uses it as the trigger that makes the
   ///    whole path observable.
   ///
   ///    The delivery that seeds the statistics is a real one: the fixture's DNS
   ///    server answers the MX lookup with 127.0.0.1, where our own port 25 -
   ///    temporarily STARTTLS-required with the test certificate - plays the
   ///    remote server. The outbound client attempts opportunistic STARTTLS,
   ///    certificate verification fails (self-signed), RFC 7435 forgiveness
   ///    completes the handshake anyway, and the session lands in the TLS-RPT
   ///    store. The RCPT is then refused (the My computer range is set to require
   ///    authentication for local-to-external, and the server-to-server hop is
   ///    unauthenticated), so the message cannot loop back into the queue.
   /// </summary>
   [TestFixture]
   public class TlsRptReporting : TestFixtureBase
   {
      private const string RemoteDomain = "rpt.example.test";
      private const string SeedPort = "25778";

      [Test]
      [Description("SendTlsRptReports refuses when TlsRptFromAddress is not configured, naming the setting - running would discard the statistics it was asked to send")]
      public void SendTlsRptReportsRefusesWhenUnconfigured()
      {
         var error = Assert.Throws<COMException>(
            () => _application.Utilities.SendTlsRptReports(true),
            "With no TlsRptFromAddress the diagnostic can only pop-and-discard, so it must refuse.");

         StringAssert.Contains("TlsRptFromAddress", error.Message,
            "The refusal must name the setting to configure. Got: " + error.Message);
      }

      [Test]
      [Description("A TLS session recorded during MX-path delivery is discovered via _smtp._tls, reported as RFC 8460 JSON and mailed to the rua target")]
      public void AReportIsGeneratedDiscoveredAndDelivered()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sender@example.test", "test");
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "tlsreports@example.test", "test");

         eConnectionSecurity originalOutboundSecurity = _settings.SMTPConnectionSecurity;

         SSLCertificate certificate = null;
         TCPIPPort port25 = null;
         eConnectionSecurity originalPort25Security = eConnectionSecurity.eCSNone;
         int originalPort25Certificate = 0;
         TCPIPPort seedPort = null;

         using (var fakeDns = new FakeDnsServer())
         {
            try
            {
               // The test certificate, so port 25 can offer STARTTLS.
               var sslPath = SslSetup.GetSslCertPath();
               certificate = _settings.SSLCertificates.Add();
               certificate.Name = "TlsRptReporting";
               certificate.CertificateFile = Path.Combine(sslPath, "example.crt");
               certificate.PrivateKeyFile = Path.Combine(sslPath, "example.key");
               certificate.Save();

               // Port 25 plays the remote MX. STARTTLS-required rather than
               // optional so the unauthenticated server-to-server hop cannot
               // degrade to a session that records nothing.
               var ports = _settings.TCPIPPorts;
               for (int i = 0; i < ports.Count; i++)
               {
                  var candidate = ports[i];
                  if (candidate.PortNumber == 25 && candidate.Protocol == eSessionType.eSTSMTP)
                  {
                     port25 = candidate;
                     break;
                  }
               }

               ClassicAssert.IsNotNull(port25, "No SMTP port 25 exists in the test environment.");

               originalPort25Security = port25.ConnectionSecurity;
               originalPort25Certificate = port25.SSLCertificateID;

               port25.ConnectionSecurity = eConnectionSecurity.eCSSTARTTLSRequired;
               port25.SSLCertificateID = certificate.ID;
               port25.Save();

               // The seed submission needs a port that is still plaintext.
               seedPort = ports.Add();
               seedPort.Address = "0.0.0.0";
               seedPort.PortNumber = Convert.ToInt32(SeedPort);
               seedPort.ConnectionSecurity = eConnectionSecurity.eCSNone;
               seedPort.Protocol = eSessionType.eSTSMTP;
               seedPort.Save();

               // The outbound client attempts opportunistic STARTTLS on MX-path
               // deliveries.
               _settings.SMTPConnectionSecurity = eConnectionSecurity.eCSSTARTTLSOptional;

               // The unauthenticated self-delivery hop must be refused at RCPT,
               // or the accepted message would resolve MX back to ourselves and
               // loop until the hop guard. The authenticated seed still passes.
               var myComputer = _settings.SecurityRanges.get_ItemByName("My computer");
               myComputer.RequireSMTPAuthLocalToExternal = true;
               myComputer.Save();

               SetIniSetting("DNSServer", "127.0.0.1");
               SetIniSetting("DNSQueryTimeout", "5");
               SetIniSetting("TlsRptFromAddress", "postmaster@example.test");

               RestartServerAndReacquireCom();

               // The seed: a real submission whose delivery is a real MX-path
               // session. The RCPT of that session is refused; the TLS outcome
               // was recorded before any RCPT was sent.
               string errorMessage;
               new SmtpClientSimulator(false, Convert.ToInt32(SeedPort)).Send(
                  false, "sender@example.test", "test",
                  "sender@example.test", "user@" + RemoteDomain, "tls-rpt seed", "Seed.", out errorMessage);

               // Delivery is asynchronous; ask until the session has been
               // recorded and reported. Popping an as-yet-empty day is harmless -
               // a session recorded afterwards recreates the bucket.
               int reportCount = 0;
               for (int attempt = 0; attempt < 120; attempt++)
               {
                  reportCount = _application.Utilities.SendTlsRptReports(true);
                  if (reportCount > 0)
                     break;

                  Thread.Sleep(250);
               }

               ClassicAssert.AreEqual(1, reportCount,
                  "One report - for the one domain with recorded sessions - should have been submitted.");

               Pop3ClientSimulator.AssertMessageCount("tlsreports@example.test", "test", 1);
               string report = Pop3ClientSimulator.AssertGetFirstMessageText("tlsreports@example.test", "test");

               // RFC 8460 section 5.3: the report mail's envelope.
               StringAssert.Contains("Report Domain: " + RemoteDomain, report,
                  "The subject must carry the policy domain. Got: " + report);
               StringAssert.Contains("TLS-Report-Domain: " + RemoteDomain, report,
                  "The TLS-Report-Domain field is required. Got: " + report);
               StringAssert.Contains("application/tlsrpt+json", report,
                  "The report body must be an application/tlsrpt+json part. Got: " + report);
               StringAssert.Contains("!" + RemoteDomain + "!", report,
                  "The attachment filename carries sender!policy-domain!date. Got: " + report);

               // And the report itself.
               StringAssert.Contains("\"policy-domain\":\"" + RemoteDomain + "\"", report,
                  "The JSON must name the policy domain. Got: " + report);
               StringAssert.Contains("\"contact-info\":\"postmaster@example.test\"", report,
                  "The JSON must carry the configured contact. Got: " + report);
               ClassicAssert.IsFalse(
                  report.Contains("\"total-successful-session-count\":0,\"total-failure-session-count\":0"),
                  "The delivery session must have been counted - a report of nothing proves nothing. Got: " + report);

               // The pop is destructive by design: the day was taken, so a second
               // call with no new sessions has nothing to send.
               ClassicAssert.AreEqual(0, (int) _application.Utilities.SendTlsRptReports(true),
                  "The first call popped the day; with no sessions since, the second has nothing.");
            }
            finally
            {
               SetIniSetting("DNSServer", null);
               SetIniSetting("DNSQueryTimeout", null);
               SetIniSetting("TlsRptFromAddress", null);

               // The mid-test service restart killed every COM proxy taken
               // before it - port25 and the rest point at the old process, and a
               // Save() on one throws RPC_S_SERVER_UNAVAILABLE out of this very
               // block (the first run of this fixture proved it). So: restart
               // first, which re-acquires _application and _settings, then look
               // everything up afresh and restore by value.
               RestartServerAndReacquireCom();

               var ports = _settings.TCPIPPorts;
               for (int i = ports.Count - 1; i >= 0; i--)
               {
                  var candidate = ports[i];
                  if (candidate.Protocol != eSessionType.eSTSMTP)
                     continue;

                  if (candidate.PortNumber == 25 && port25 != null)
                  {
                     candidate.ConnectionSecurity = originalPort25Security;
                     candidate.SSLCertificateID = originalPort25Certificate;
                     candidate.Save();
                  }
                  else if (candidate.PortNumber == Convert.ToInt32(SeedPort))
                  {
                     candidate.Delete();
                  }
               }

               var certificates = _settings.SSLCertificates;
               for (int i = certificates.Count - 1; i >= 0; i--)
               {
                  if (certificates[i].Name == "TlsRptReporting")
                     certificates[i].Delete();
               }

               _settings.SMTPConnectionSecurity = originalOutboundSecurity;
               _settings.SecurityRanges.SetDefault();

               // Rebind the restored ports; a certificate-less port 25 must not
               // keep requiring STARTTLS for the fixtures that follow.
               _application.Stop();
               _application.Start();
            }
         }
      }

      // Writes a key into the FIRST [Settings] section of the ini the server
      // binary reads. Same mechanics as CustomDnsServer, for the same reasons:
      // GetPrivateProfileString reads the first section of a given name, and the
      // server's ini is the one beside the binary, not the data directory's.
      private static void SetIniSetting(string key, string value)
      {
         var path = IniPath();
         var lines = new List<string>(File.ReadAllLines(path));

         lines.RemoveAll(line => line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));

         var section = lines.FindIndex(line => line.Trim() == "[Settings]");

         Assert.Greater(section, -1, "hMailServer.ini has no [Settings] section: " + path);

         if (value != null)
            lines.Insert(section + 1, key + "=" + value);

         File.WriteAllLines(path, lines);
      }

      private static string IniPath()
      {
         var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

         while (directory != null)
         {
            var candidate = Path.Combine(directory.FullName,
               @"source\Server\hMailServer\x64\Release\hMailServer.ini");

            if (File.Exists(candidate))
               return candidate;

            directory = directory.Parent;
         }

         Assert.Fail("Could not locate the server's hMailServer.ini by searching upwards from " +
                     AppDomain.CurrentDomain.BaseDirectory);
         return null;
      }

      /// <summary>
      ///    A DNS server for 127.0.0.1:53, UDP and TCP, answering the four
      ///    questions this fixture's delivery and discovery ask: the MX of the
      ///    remote domain (127.0.0.1, i.e. ourselves), the A record of that MX
      ///    host, and the _smtp._tls TXT record naming the rua mailbox.
      ///    Everything else is answered NODATA with the zone's SOA in the
      ///    AUTHORITY section - the RFC 2308 shape; without the SOA the TCP
      ///    response parser answers 9502 and the lookup fails for a reason that
      ///    has nothing to do with the code under test (measured; see
      ///    CustomDnsServer).
      /// </summary>
      private sealed class FakeDnsServer : IDisposable
      {
         private readonly UdpClient udp_;
         private readonly TcpListener tcp_;
         private volatile bool stopping_;

         public FakeDnsServer()
         {
            try
            {
               udp_ = new UdpClient(new IPEndPoint(IPAddress.Loopback, 53));
               tcp_ = new TcpListener(IPAddress.Loopback, 53);
               tcp_.Start();
            }
            catch (SocketException ex)
            {
               udp_?.Close();
               tcp_?.Stop();

               Assert.Fail("Could not bind 127.0.0.1:53 for the fake DNS server - something on this " +
                           "machine is already serving DNS on loopback: " + ex.Message);
            }

            new Thread(ServeUdp_) { IsBackground = true }.Start();
            new Thread(ServeTcp_) { IsBackground = true }.Start();
         }

         public void Dispose()
         {
            stopping_ = true;
            udp_.Close();
            tcp_.Stop();
         }

         private void ServeUdp_()
         {
            while (!stopping_)
            {
               try
               {
                  var remote = new IPEndPoint(IPAddress.Any, 0);
                  byte[] query = udp_.Receive(ref remote);
                  byte[] response = BuildResponse_(query);
                  udp_.Send(response, response.Length, remote);
               }
               catch (SocketException)
               {
               }
               catch (ObjectDisposedException)
               {
               }
            }
         }

         private void ServeTcp_()
         {
            while (!stopping_)
            {
               try
               {
                  using (TcpClient client = tcp_.AcceptTcpClient())
                  using (NetworkStream stream = client.GetStream())
                  {
                     byte[] lengthPrefix = ReadExactly_(stream, 2);
                     int queryLength = (lengthPrefix[0] << 8) | lengthPrefix[1];
                     byte[] query = ReadExactly_(stream, queryLength);

                     byte[] response = BuildResponse_(query);
                     stream.Write(new[] { (byte) (response.Length >> 8), (byte) (response.Length & 0xFF) }, 0, 2);
                     stream.Write(response, 0, response.Length);
                     stream.Flush();
                  }
               }
               catch (SocketException)
               {
               }
               catch (ObjectDisposedException)
               {
               }
               catch (IOException)
               {
               }
            }
         }

         private static byte[] ReadExactly_(NetworkStream stream, int count)
         {
            var buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
               int n = stream.Read(buffer, read, count - read);
               if (n <= 0)
                  throw new IOException("Peer closed mid-message.");
               read += n;
            }
            return buffer;
         }

         private static string QueryName_(byte[] query)
         {
            var name = new System.Text.StringBuilder();
            int i = 12;
            while (query[i] != 0)
            {
               if (name.Length > 0)
                  name.Append('.');
               for (int c = 1; c <= query[i]; c++)
                  name.Append((char) query[i + c]);
               i += query[i] + 1;
            }
            return name.ToString().ToLowerInvariant();
         }

         private static int QueryType_(byte[] query)
         {
            int zero = 12;
            while (query[zero] != 0)
               zero += query[zero] + 1;
            return (query[zero + 1] << 8) | query[zero + 2];
         }

         private static int QuestionEnd_(byte[] query)
         {
            int i = 12;
            while (query[i] != 0)
               i += query[i] + 1;
            return i + 5;
         }

         private static void WriteHeaderAndQuestion_(List<byte> m, byte[] query, int answerCount, int authorityCount = 0)
         {
            int questionEnd = QuestionEnd_(query);
            m.Add(query[0]); m.Add(query[1]);            // ID
            m.Add(0x81); m.Add(0x80);                    // QR|RD|RA, NOERROR
            m.Add(0); m.Add(1);                          // QDCOUNT
            m.Add(0); m.Add((byte) answerCount);         // ANCOUNT
            m.Add(0); m.Add((byte) authorityCount);      // NSCOUNT
            m.Add(0); m.Add(0);                          // ARCOUNT
            for (int i = 12; i < questionEnd; i++)
               m.Add(query[i]);
         }

         private static void WriteName_(List<byte> m, string name)
         {
            foreach (string label in name.Split('.'))
            {
               m.Add((byte) label.Length);
               foreach (char c in label)
                  m.Add((byte) c);
            }
            m.Add(0);
         }

         private static void WriteAnswerHeader_(List<byte> m, int recordType)
         {
            m.Add(0xC0); m.Add(0x0C);                    // owner: the queried name
            m.Add((byte) (recordType >> 8)); m.Add((byte) (recordType & 0xFF));
            m.Add(0); m.Add(1);                          // CLASS IN
            m.Add(0); m.Add(0); m.Add(0); m.Add(60);     // TTL
         }

         private static void WriteRdata_(List<byte> m, List<byte> rdata)
         {
            m.Add((byte) (rdata.Count >> 8)); m.Add((byte) (rdata.Count & 0xFF));
            m.AddRange(rdata);
         }

         private byte[] BuildResponse_(byte[] query)
         {
            string name = QueryName_(query);
            int type = QueryType_(query);

            var m = new List<byte>();

            if (type == 15 && name == RemoteDomain)
            {
               WriteHeaderAndQuestion_(m, query, 1);
               WriteAnswerHeader_(m, 15);
               var rdata = new List<byte> { 0, 10 };     // preference 10
               WriteName_(rdata, "mail." + RemoteDomain);
               WriteRdata_(m, rdata);
               return m.ToArray();
            }

            if (type == 1 && name == "mail." + RemoteDomain)
            {
               WriteHeaderAndQuestion_(m, query, 1);
               WriteAnswerHeader_(m, 1);
               WriteRdata_(m, new List<byte> { 127, 0, 0, 1 });
               return m.ToArray();
            }

            if (type == 16 && name == "_smtp._tls." + RemoteDomain)
            {
               const string record = "v=TLSRPTv1; rua=mailto:tlsreports@example.test";

               WriteHeaderAndQuestion_(m, query, 1);
               WriteAnswerHeader_(m, 16);
               var rdata = new List<byte> { (byte) record.Length };
               foreach (char c in record)
                  rdata.Add((byte) c);
               WriteRdata_(m, rdata);
               return m.ToArray();
            }

            // Everything else: NODATA - NOERROR, no answers, the zone's SOA in
            // the AUTHORITY section per RFC 2308. See the class comment for why
            // the SOA is not decoration.
            WriteHeaderAndQuestion_(m, query, 0, 1);
            WriteAnswerHeader_(m, 6);
            var soa = new List<byte>();
            WriteName_(soa, "ns." + RemoteDomain);
            WriteName_(soa, "hostmaster." + RemoteDomain);
            for (int f = 0; f < 5; f++)
            {
               soa.Add(0); soa.Add(0); soa.Add(0); soa.Add(60);
            }
            WriteRdata_(m, soa);
            return m.ToArray();
         }
      }
   }
}
