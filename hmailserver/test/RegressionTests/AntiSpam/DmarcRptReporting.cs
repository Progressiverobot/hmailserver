// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.AntiSpam
{
   /// <summary>
   ///    DMARC aggregate reporting (RFC 7489 section 7.2), end to end: a DMARC
   ///    evaluation recorded on a real inbound delivery is discovered via the
   ///    policy domain's _dmarc rua= tag, built into the Appendix C XML and
   ///    delivered as mail to the report mailbox.
   ///
   ///    The trigger is Utilities.SendDmarcReports, which exists for the same
   ///    reason as its TLS-RPT sibling: the hourly task only mails days that
   ///    are over, so without it neither an administrator nor a test could see
   ///    a report without waiting until tomorrow.
   ///
   ///    The external-destination control (RFC 7489 7.1) is pinned with a
   ///    pair: a policy whose rua points outside its own organizational domain
   ///    WITHOUT the <policy-domain>._report._dmarc.<target-domain> record is
   ///    skipped - anyone can publish a rua naming anyone's mailbox, and
   ///    honouring it unverified turns every reporter into a mail cannon -
   ///    while the same shape WITH the record is reported. Three policy
   ///    domains, three verdicts: internal (sent), external-unverified
   ///    (skipped), external-verified (sent).
   /// </summary>
   [TestFixture]
   public class DmarcRptReporting : TestFixtureBase
   {
      // Internal rua target: org domain of the policy domain and the report
      // mailbox are both example.test, so no verification is required.
      private const string InternalPolicyDomain = "sender-dom.example.test";

      // External rua targets: these policy domains live under other
      // organizational domains, so mailing reports to example.test requires
      // their DNS to authorize it - which one publishes and one does not.
      private const string ExternalUnverifiedPolicyDomain = "sender.no-authz.test";
      private const string ExternalVerifiedPolicyDomain = "sender.with-authz.test";

      [Test]
      [Description("SendDmarcReports refuses when DmarcRptFromAddress is not configured, naming the setting - running would discard the statistics it was asked to send")]
      public void SendDmarcReportsRefusesWhenUnconfigured()
      {
         var error = Assert.Throws<COMException>(
            () => _application.Utilities.SendDmarcReports(true),
            "With no DmarcRptFromAddress the diagnostic can only pop-and-discard, so it must refuse.");

         StringAssert.Contains("DmarcRptFromAddress", error.Message,
            "The refusal must name the setting to configure. Got: " + error.Message);
      }

      [Test]
      [Description("A server with no DmarcRptFromAddress says so once at startup, in the application log and not as a reported error")]
      public void InertDmarcReportingIsAnnouncedAtStartup()
      {
         try
         {
            // The shipped default, made explicit: an earlier fixture could have
            // left a sender address behind.
            SetIniSetting("DmarcRptFromAddress", null);

            LogHandler.DeleteCurrentDefaultLog();
            LogHandler.DeleteErrorLog();

            // The notice is written where the scheduled task is constructed,
            // which is once per StartServers - so a reinitialization is a
            // startup for this purpose.
            _application.Reinitialize();

            string defaultLog = LogHandler.ReadCurrentDefaultLog();

            Assert.IsTrue(defaultLog.Contains("DmarcRptFromAddress"),
               "Startup should name the setting that leaves DMARC reporting inert. Log: " + defaultLog);

            // Once per start - and counted with the setting name in the needle,
            // because the shorter phrase is deliberately shared with the
            // TLS-RPT reporter's sibling notice.
            Assert.AreEqual(1,
               defaultLog.Split(new[] { "will never send a report: DmarcRptFromAddress" }, StringSplitOptions.None).Length - 1,
               "The notice should appear exactly once per start. Log: " + defaultLog);

            // The critical half. This is the default configuration, so it must
            // not reach the ERROR log.
            CustomAsserts.AssertNoReportedError();
         }
         finally
         {
            LogHandler.DeleteErrorLog();
         }
      }

      [Test]
      [Description("An evaluation recorded on inbound delivery is reported to the rua mailbox as RFC 7489 XML; external rua targets are honoured only when the receiver's DNS authorizes them")]
      public void AReportIsGeneratedDiscoveredAndDelivered()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "seedrcpt@example.test", "test");
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "dmarcreports@example.test", "test");

         var antiSpam = _application.Settings.AntiSpam;
         antiSpam.SpamMarkThreshold = 100;
         antiSpam.SpamDeleteThreshold = 1000;
         antiSpam.DMARCEnabled = true;

         using (var fakeDns = new FakeDnsServer())
         {
            try
            {
               SetIniSetting("DNSServer", "127.0.0.1");
               SetIniSetting("DNSQueryTimeout", "5");
               SetIniSetting("DmarcRptFromAddress", "postmaster@example.test");

               RestartServerAndReacquireCom();

               // Three inbound messages, one per policy domain. Each From
               // domain publishes p=none, so every seed is delivered and every
               // evaluation is recorded; none of the domains has SPF or DKIM,
               // so each records an aligned-fail row - the row a domain owner
               // deploys DMARC to see.
               SendSeed_(InternalPolicyDomain);
               SendSeed_(ExternalUnverifiedPolicyDomain);
               SendSeed_(ExternalVerifiedPolicyDomain);

               // The seeds are in the mailbox, so their spam-time evaluations -
               // which run before delivery - are all recorded.
               Pop3ClientSimulator.AssertMessageCount("seedrcpt@example.test", "test", 3);

               int reportCount = (int) _application.Utilities.SendDmarcReports(true);

               ClassicAssert.AreEqual(2, reportCount,
                  "Two reports: the internal rua target and the external-verified one. " +
                  "The external target without a _report._dmarc authorization record must be skipped.");

               Pop3ClientSimulator.AssertMessageCount("dmarcreports@example.test", "test", 2);

               var reports = new List<string>
               {
                  Pop3ClientSimulator.AssertGetFirstMessageText("dmarcreports@example.test", "test"),
                  Pop3ClientSimulator.AssertGetFirstMessageText("dmarcreports@example.test", "test")
               };

               string combined = reports[0] + "\n===\n" + reports[1];

               StringAssert.Contains("<domain>" + InternalPolicyDomain + "</domain>", combined,
                  "The internal-target policy domain must be reported. Got: " + combined);
               StringAssert.Contains("<domain>" + ExternalVerifiedPolicyDomain + "</domain>", combined,
                  "The external-verified policy domain must be reported. Got: " + combined);
               ClassicAssert.IsFalse(combined.Contains(ExternalUnverifiedPolicyDomain),
                  "The unverified external target must not have produced a report - this is the " +
                  "RFC 7489 7.1 mail-cannon control. Got: " + combined);

               string internalReport = reports[0].Contains("<domain>" + InternalPolicyDomain + "</domain>")
                  ? reports[0]
                  : reports[1];

               // The RFC 7489 7.2.1.1 envelope.
               StringAssert.Contains("Report Domain: " + InternalPolicyDomain, internalReport,
                  "The subject must carry the policy domain. Got: " + internalReport);
               StringAssert.Contains("!" + InternalPolicyDomain + "!", internalReport,
                  "The attachment filename carries receiver!policy-domain!begin!end. Got: " + internalReport);

               // And the Appendix C body.
               StringAssert.Contains("<org_name>", internalReport, "Got: " + internalReport);
               StringAssert.Contains("<email>postmaster@example.test</email>", internalReport,
                  "The configured sender is the report contact. Got: " + internalReport);
               StringAssert.Contains("<p>none</p>", internalReport,
                  "The published policy travels in policy_published. Got: " + internalReport);
               StringAssert.Contains("<source_ip>127.0.0.1</source_ip>", internalReport,
                  "The row must carry the connecting address. Got: " + internalReport);
               StringAssert.Contains("<disposition>none</disposition>", internalReport,
                  "p=none evaluated to disposition none. Got: " + internalReport);
               StringAssert.Contains("<dkim>fail</dkim>", internalReport,
                  "No DKIM signature can align. Got: " + internalReport);
               StringAssert.Contains("<spf>fail</spf>", internalReport,
                  "An envelope from another organizational domain can never align, whatever " +
                  "the raw SPF result was. Got: " + internalReport);
               StringAssert.Contains("<spf><domain>unaligned-envelope.test</domain>", internalReport,
                  "auth_results carries the RAW SPF identity - the envelope domain - which is " +
                  "exactly what policy_evaluated's aligned verdict above is not. Got: " + internalReport);
               StringAssert.Contains("<header_from>" + InternalPolicyDomain + "</header_from>", internalReport,
                  "The identifiers block names the From domain. Got: " + internalReport);

               // The pop is destructive by design: nothing new has been
               // recorded, so a second call has nothing to send.
               ClassicAssert.AreEqual(0, (int) _application.Utilities.SendDmarcReports(true),
                  "The first call popped the day; with no evaluations since, the second has nothing.");
            }
            finally
            {
               SetIniSetting("DNSServer", null);
               SetIniSetting("DNSQueryTimeout", null);
               SetIniSetting("DmarcRptFromAddress", null);

               // Restart before any COM restore - proxies taken before the
               // mid-test restart point at the old process (the TLS-RPT
               // fixture's first run proved what happens otherwise). Nothing
               // else needs restoring: no ports, certificates or IP ranges
               // were touched, and PerformBasicSetup resets the anti-spam
               // settings for the next fixture.
               RestartServerAndReacquireCom();
            }
         }
      }

      private static void SendSeed_(string fromDomain)
      {
         string address = "user@" + fromDomain;

         string message =
            "From: <" + address + ">\r\n" +
            "To: <seedrcpt@example.test>\r\n" +
            "Subject: dmarc rua seed for " + fromDomain + "\r\n" +
            "Date: Thu, 13 Aug 2026 10:00:00 +0000\r\n" +
            "\r\n" +
            "Seed body\r\n";

         // The envelope sender is deliberately a DIFFERENT organizational
         // domain from the From header. The SPF evaluator gives 127.0.0.1 a
         // free pass (discovered when this fixture's first run reported
         // spf=pass), so an envelope matching the From domain would ALIGN,
         // turn the whole evaluation into a DMARC pass, and record the one row
         // shape this fixture is least interested in. Unaligned, the raw SPF
         // result can be whatever the environment makes it - the ALIGNED
         // verdict in policy_evaluated is deterministically fail, which is
         // also the aligned-vs-raw distinction the report schema exists to
         // keep apart.
         SmtpClientSimulator.StaticSendRaw("bounce@unaligned-envelope.test", "seedrcpt@example.test", message);
      }

      // Writes a key into the FIRST [Settings] section of the ini the server
      // binary reads. Same mechanics as CustomDnsServer, for the same reasons.
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
      ///    A DNS server for 127.0.0.1:53, UDP and TCP. Answers the _dmarc TXT
      ///    of the three policy domains (all p=none, rua pointing at
      ///    dmarcreports@example.test) and the _report._dmarc authorization
      ///    record for the one external domain that consents. Everything else
      ///    is NODATA with the zone's SOA in the AUTHORITY section - the RFC
      ///    2308 shape; without the SOA the TCP response parser answers 9502
      ///    and the lookup fails for a reason that has nothing to do with the
      ///    code under test (measured; see CustomDnsServer).
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
            var name = new StringBuilder();
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

         private static byte[] TxtResponse_(byte[] query, string record)
         {
            var m = new List<byte>();
            WriteHeaderAndQuestion_(m, query, 1);
            WriteAnswerHeader_(m, 16);
            var rdata = new List<byte> { (byte) record.Length };
            foreach (char c in record)
               rdata.Add((byte) c);
            m.Add((byte) (rdata.Count >> 8)); m.Add((byte) (rdata.Count & 0xFF));
            m.AddRange(rdata);
            return m.ToArray();
         }

         private byte[] BuildResponse_(byte[] query)
         {
            string name = QueryName_(query);
            int type = QueryType_(query);

            if (type == 16)
            {
               const string policy = "v=DMARC1; p=none; rua=mailto:dmarcreports@example.test";

               if (name == "_dmarc." + InternalPolicyDomain ||
                   name == "_dmarc." + ExternalUnverifiedPolicyDomain ||
                   name == "_dmarc." + ExternalVerifiedPolicyDomain)
                  return TxtResponse_(query, policy);

               // The consent record for the one external domain that grants it
               // (RFC 7489 7.1). Its sibling deliberately has no such record.
               if (name == ExternalVerifiedPolicyDomain + "._report._dmarc.example.test")
                  return TxtResponse_(query, "v=DMARC1");
            }

            // Everything else: NODATA - NOERROR, no answers, the zone's SOA in
            // the AUTHORITY section per RFC 2308. See the class comment.
            var m = new List<byte>();
            WriteHeaderAndQuestion_(m, query, 0, 1);
            WriteAnswerHeader_(m, 6);
            var soa = new List<byte>();
            WriteName_(soa, "ns.example.test");
            WriteName_(soa, "hostmaster.example.test");
            for (int f = 0; f < 5; f++)
            {
               soa.Add(0); soa.Add(0); soa.Add(0); soa.Add(60);
            }
            m.Add((byte) (soa.Count >> 8)); m.Add((byte) (soa.Count & 0xFF));
            m.AddRange(soa);
            return m.ToArray();
         }
      }
   }
}
