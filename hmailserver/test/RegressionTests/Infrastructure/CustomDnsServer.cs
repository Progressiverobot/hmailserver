// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    Name resolution through a configured DNS server.
   ///
   ///    This fixture exists because nothing tested the DNSServer setting, and that absence
   ///    let a one-line change break every lookup for anyone who uses it - DNSBL, SURBL,
   ///    SPF, DKIM, DMARC and MX alike - while the suite stayed green, because every other
   ///    test resolves through the system resolver.
   ///
   ///    The mechanism is worth stating, because it is counter-intuitive and the obvious
   ///    "fix" is the bug. Up to 6.2.16 the custom server went to the classic DnsQuery as a
   ///    PIP4_ARRAY: a bare list of IPv4 addresses with no port field. 6.2.17 moved to
   ///    DnsQueryEx for a bounded wait, whose DNS_ADDR_ARRAY carries a full SOCKADDR per
   ///    server - and a SOCKADDR with a zero port looks like an oversight, so it got
   ///    "corrected" to 53. The DNS client supplies the port itself and REJECTS an entry
   ///    that specifies one: DnsQueryEx then returns ERROR_INVALID_PARAMETER (87) for every
   ///    query before a packet leaves the machine.
   ///
   ///    What this test can assert on any machine, with no DNS server of its own, is the
   ///    half that catches that class of defect: with a DNSServer configured, a lookup must
   ///    reach the resolver and come back with a *DNS* answer - a record, or an
   ///    authoritative "no such name" - rather than the request being rejected as malformed.
   ///    ERROR_INVALID_PARAMETER is not a DNS answer, and neither is a timeout that arrives
   ///    in the same millisecond it was requested.
   ///
   ///    It reads the debug log rather than calling the resolver directly, because the
   ///    resolver is not exposed over COM and the log line it checks was added for exactly
   ///    this purpose: a lookup that quietly found nothing used to be indistinguishable
   ///    from one that was never made, which is what hid this for a day.
   ///
   ///    This ran as [Explicit] when it was written, because changing DNSServer only takes
   ///    effect on a service restart - IniFileSettings reads the ini once at process start,
   ///    and Application.Stop()/Start() over COM does not re-read it because the process
   ///    keeps running - and a real restart broke the harness in two separate ways. It now
   ///    runs in the ordinary suite, via TestFixtureBase.RestartServerAndReacquireCom().
   ///
   ///    Both of those ways are worth knowing before writing another fixture like this one.
   ///    The COM object is an out-of-process server, so a restart disconnects every proxy
   ///    the fixture base holds - which also means the obvious way to wait for the server to
   ///    come back, calling the cached object until it answers, can never succeed. And
   ///    ServiceRestartDetector fails the next test in EVERY fixture when hMailServer.exe's
   ///    process id changes, because normally that means the server crashed; a restart we
   ///    asked for has to re-baseline it. The primitive does both.
   ///
   ///    The fix it covers was verified by hand against a real DNS server: with the port
   ///    set, all three record types return status 87 and no records; with it zero, the A
   ///    query returns its record and the AAAA query returns DNS_INFO_NO_RECORDS.
   /// </summary>
   [TestFixture]
   public class CustomDnsServer : TestFixtureBase
   {
      // 192.0.2.1 is TEST-NET-1 (RFC 5737), reserved for documentation and guaranteed not
      // to host a real service. Pointing at it means this test never depends on a
      // particular network: the request still has to be ACCEPTED by the DNS client and
      // dispatched, which is what is being checked, and whether an answer comes back is
      // beside the point.
      private const string UnreachableButValidServer = "192.0.2.1";

      private const string SettingsSection = "[Settings]";

      // The server reads its ini from the directory holding the BINARY, not from the data
      // directory - a distinction worth stating because editing the wrong hMailServer.ini
      // appears to do nothing at all, and there is one in the data directory too.
      //
      // Found by searching upwards for it rather than by counting "..\" segments, so that
      // moving the test assembly's output path cannot turn this into a silent skip.
      private static string IniPath()
      {
         var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

         while (directory != null)
         {
            var candidate = Paths.Combine(directory.FullName,
               @"source\Server\hMailServer\x64\Release\hMailServer.ini");

            if (File.Exists(candidate))
               return candidate;

            directory = directory.Parent;
         }

         Assert.Fail("Could not locate the server's hMailServer.ini by searching upwards from " +
                     AppDomain.CurrentDomain.BaseDirectory);
         return null;
      }

      // Writes a key into the FIRST [Settings] section, which is the only one that counts.
      // GetPrivateProfileString reads the first section with a given name and ignores any
      // later duplicate - so appending "[Settings]\nKey=Value" to the end of the file, the
      // obvious thing to do, silently has no effect. That mistake produced an entire
      // afternoon of invalid measurements before it was spotted.
      private static void SetIniSetting(string key, string value)
      {
         var path = IniPath();
         var lines = new System.Collections.Generic.List<string>(File.ReadAllLines(path));

         lines.RemoveAll(line => line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));

         var section = lines.FindIndex(line => line.Trim() == SettingsSection);

         Assert.Greater(section, -1, "hMailServer.ini has no [Settings] section: " + path);

         if (value != null)
            lines.Insert(section + 1, key + "=" + value);

         File.WriteAllLines(path, lines);
      }

      /// <summary>
      ///    Utilities.ResolveMXRecords resolves through the SERVER's resolver, not the
      ///    operating system's - which is the whole of issue #29: the Control Panel's
      ///    MX-query tool used to shell out to nslookup, and on any installation with
      ///    a custom DNSServer the tool and the server disagreed about where mail goes.
      ///
      ///    Same design as the test below: the configured server is TEST-NET-1, which
      ///    answers nothing, so a TIMEOUT in the resolver's log is the proof the query
      ///    went where the setting points. The old nslookup path never touched this
      ///    process, wrote nothing to this log, and answered from the system resolver.
      /// </summary>
      [Test]
      [Description("Issue 29: Utilities.ResolveMXRecords honours the configured DNS server rather than asking " +
                   "the operating system's resolver")]
      public void ResolveMXRecordsUsesTheConfiguredDnsServer()
      {
         SetIniSetting("DNSServer", UnreachableButValidServer);
         SetIniSetting("DNSQueryTimeout", "2");

         try
         {
            RestartServerAndReacquireCom();

            LogHandler.DeleteCurrentDefaultLog();

            // Nothing answers at TEST-NET-1, so the lookup FAILS - and failing is a
            // different answer from "this domain has no mail servers", which is the
            // distinction the method is required to keep (an administrator whose own
            // DNS server is unreachable must not be told the recipient's domain has
            // no MX). Asserting the failure shape rather than merely an empty string
            // is what pins that: an implementation that swallowed the resolver's
            // verdict would return empty here and pass a weaker test.
            var lookupFailure = Assert.Throws<System.Runtime.InteropServices.COMException>(
               () => { string ignored = _application.Utilities.ResolveMXRecords("mx-probe.example.invalid"); },
               "A dead DNS server did not produce a lookup failure. Either the query did not go where " +
               "DNSServer points, or the failure was reported as 'no mail servers'.");

            StringAssert.Contains("lookup failed", lookupFailure.Message,
               "The failure was raised, but not as a DNS lookup failure: " + lookupFailure.Message);

            var log = LogHandler.ReadCurrentDefaultLog();

            var dispatched = log.Contains("DNS - Result.") || log.Contains("DNS - Query timed out.");

            Assert.IsTrue(dispatched,
               "ResolveMXRecords produced neither a result line nor a timeout line in the server's log, " +
               "so it did not resolve through the server's resolver at all - which is the defect this " +
               "test exists to catch. Log:\r\n" + log);
         }
         finally
         {
            SetIniSetting("DNSServer", SuiteDns.Resolver);
            SetIniSetting("DNSQueryTimeout", null);
            RestartServerAndReacquireCom();
         }
      }

      /// <summary>
      ///    A DNS server whose UDP path emits structurally broken responses - RDLENGTH
      ///    overrunning the packet - must not end the lookup: the resolver retries the
      ///    query over TCP, where the same server answers correctly.
      ///
      ///    This is a field failure, not a hypothetical. A 5.7 user reported DKIM
      ///    validation of outlook.com mail failing on 6.x with DNS status 9502
      ///    (DNS_ERROR_BAD_PACKET) against a local DNS server that 5.7 handled fine.
      ///    The failing record is the large multi-string DKIM TXT key - the answer
      ///    big enough to expose a server that assembles such responses wrongly over
      ///    UDP. Probed against a deliberately misbehaving server, exactly two
      ///    malformations produce status 9502 - RDLENGTH past the packet end, and a
      ///    compression pointer past the packet end - and both resolve correctly when
      ///    the same question is asked over TCP. That measurement is what this fake
      ///    server replays.
      ///
      ///    The lookup is driven through Utilities.ResolveMXRecords because it is the
      ///    shortest COM path into the server's resolver, and because it exercises the
      ///    retry TWICE: once for the MX query, once for the A query of the exchange
      ///    host it returns.
      /// </summary>
      [Test]
      [Description("A structurally broken UDP response (status 9502) is retried over TCP rather than failing the lookup")]
      public void AMalformedUdpResponseIsRetriedOverTcp()
      {
         // The suite's own zone holds 127.0.0.1:53 for the whole run; this test needs
         // the port itself, so the zone is suspended for exactly as long as it does.
         using (SuiteDns.Suspend())
         using (var fakeDns = new MalformedUdpDnsServer())
         {
            SetIniSetting("DNSServer", "127.0.0.1");
            SetIniSetting("DNSQueryTimeout", "5");

            try
            {
               RestartServerAndReacquireCom();

               LogHandler.DeleteCurrentDefaultLog();

               string result = _application.Utilities.ResolveMXRecords("dnsfix.example.test");

               Assert.IsTrue(result.Contains("mail.dnsfix.example.test"),
                  "The MX lookup did not survive the malformed UDP response, so the TCP retry " +
                  "either did not happen or did not work. Result: '" + result + "'");

               Assert.IsTrue(result.Contains("127.0.0.99"),
                  "The MX host did not resolve to the address the TCP path serves, so the A-record " +
                  "retry failed. Result: '" + result + "'");

               var log = LogHandler.ReadCurrentDefaultLog();

               StringAssert.Contains("retrying the query over TCP", log,
                  "The result arrived but the log has no retry line - so it did not come through " +
                  "the 9502-retry path this test exists to cover.");

               Assert.AreEqual(0, fakeDns.UnansweredTcpQueries,
                  "The fake server saw TCP queries it did not answer.");
            }
            finally
            {
               SetIniSetting("DNSServer", SuiteDns.Resolver);
               SetIniSetting("DNSQueryTimeout", null);
               RestartServerAndReacquireCom();
            }
         }
      }

      /// <summary>
      ///    UDP: every response is malformed in the way measured to produce status 9502
      ///    (RDLENGTH = 512 with 11 bytes of RDATA in the packet). TCP: correct answers.
      ///    Binds 127.0.0.1:53 - which the DNS client uses implicitly, since a custom
      ///    server entry must not name a port at all (see the port-zero comment in
      ///    DNSResolverWinApi.cpp).
      /// </summary>
      private sealed class MalformedUdpDnsServer : IDisposable
      {
         private readonly UdpClient udp_;
         private readonly TcpListener tcp_;
         private volatile bool stopping_;
         private int unansweredTcpQueries_;

         public int UnansweredTcpQueries => unansweredTcpQueries_;

         public MalformedUdpDnsServer()
         {
            try
            {
               udp_ = new UdpClient(new IPEndPoint(IPAddress.Loopback, 53));
               tcp_ = new TcpListener(IPAddress.Loopback, 53);
               tcp_.Start();
            }
            catch (SocketException ex)
            {
               // Close whatever DID bind before failing. The ordinary shape here is
               // UDP/53 free while TCP/53 is held by something else: without this the
               // Assert.Fail threw out of the constructor, the caller's using never
               // received an instance, Dispose never ran, and the bound UDP socket
               // survived to make every later run in this process fail to bind for a
               // reason that has nothing to do with the server under test.
               udp_?.Close();
               tcp_?.Stop();

               Assert.Fail("Could not bind 127.0.0.1:53 for the fake DNS server - something on this " +
                           "machine is already serving DNS on loopback (WSL? a local resolver?): " + ex.Message);
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
                  byte[] response = BuildMalformedResponse_(query);
                  udp_.Send(response, response.Length, remote);
               }
               catch (SocketException)
               {
                  // Close() from Dispose lands here; anything else and the test's
                  // assertions report the lookup that never completed.
               }
               catch (ObjectDisposedException)
               {
                  // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
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

                     byte[] response = BuildValidResponse_(query);
                     if (response == null)
                     {
                        Interlocked.Increment(ref unansweredTcpQueries_);
                        continue;
                     }

                     stream.Write(new[] { (byte)(response.Length >> 8), (byte)(response.Length & 0xFF) }, 0, 2);
                     stream.Write(response, 0, response.Length);
                     stream.Flush();
                  }
               }
               catch (SocketException)
               {
                  // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
               }
               catch (ObjectDisposedException)
               {
                  // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
               }
               catch (IOException)
               {
                  // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
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

         private static int QuestionEnd_(byte[] query)
         {
            int i = 12;
            while (query[i] != 0)
               i += query[i] + 1;
            return i + 5; // zero byte + QTYPE(2) + QCLASS(2)
         }

         private static int QueryType_(byte[] query)
         {
            int zero = 12;
            while (query[zero] != 0)
               zero += query[zero] + 1;
            return (query[zero + 1] << 8) | query[zero + 2];
         }

         private static void WriteHeaderAndQuestion_(List<byte> m, byte[] query, int answerCount, int authorityCount = 0)
         {
            int questionEnd = QuestionEnd_(query);
            m.Add(query[0]); m.Add(query[1]);            // ID
            m.Add(0x81); m.Add(0x80);                    // QR|RD|RA, NOERROR
            m.Add(0); m.Add(1);                          // QDCOUNT
            m.Add(0); m.Add((byte)answerCount);          // ANCOUNT
            m.Add(0); m.Add((byte)authorityCount);       // NSCOUNT
            m.Add(0); m.Add(0);                          // ARCOUNT
            for (int i = 12; i < questionEnd; i++)
               m.Add(query[i]);
         }

         private static void WriteName_(List<byte> m, string name)
         {
            foreach (string label in name.Split('.'))
            {
               m.Add((byte)label.Length);
               foreach (char c in label)
                  m.Add((byte)c);
            }
            m.Add(0);
         }

         /// <summary>
         ///    The 9502 shape: answer record claims RDLENGTH 512 while the packet holds
         ///    11 bytes of RDATA. Measured to return DNS_ERROR_BAD_PACKET from DnsQueryEx
         ///    for every query type tried; see the fixture comment.
         /// </summary>
         private static byte[] BuildMalformedResponse_(byte[] query)
         {
            var m = new List<byte>();
            WriteHeaderAndQuestion_(m, query, 1);
            m.Add(0xC0); m.Add(0x0C);                    // name: pointer to the question
            m.Add((byte)(QueryType_(query) >> 8)); m.Add((byte)(QueryType_(query) & 0xFF));
            m.Add(0); m.Add(1);                          // CLASS IN
            m.Add(0); m.Add(0); m.Add(0); m.Add(60);     // TTL
            m.Add(2); m.Add(0);                          // RDLENGTH = 512...
            m.Add(10);                                   // ...but only 11 bytes follow
            for (int i = 0; i < 10; i++)
               m.Add((byte)'B');
            return m.ToArray();
         }

         private static byte[] BuildValidResponse_(byte[] query)
         {
            var m = new List<byte>();

            switch (QueryType_(query))
            {
               case 15: // MX
                  WriteHeaderAndQuestion_(m, query, 1);
                  m.Add(0xC0); m.Add(0x0C);
                  m.Add(0); m.Add(15);
                  m.Add(0); m.Add(1);
                  m.Add(0); m.Add(0); m.Add(0); m.Add(60);
                  var exchange = new List<byte>();
                  exchange.Add(0); exchange.Add(10);     // preference 10
                  WriteName_(exchange, "mail.dnsfix.example.test");
                  m.Add((byte)(exchange.Count >> 8)); m.Add((byte)(exchange.Count & 0xFF));
                  m.AddRange(exchange);
                  return m.ToArray();

               case 1: // A
                  WriteHeaderAndQuestion_(m, query, 1);
                  m.Add(0xC0); m.Add(0x0C);
                  m.Add(0); m.Add(1);
                  m.Add(0); m.Add(1);
                  m.Add(0); m.Add(0); m.Add(0); m.Add(60);
                  m.Add(0); m.Add(4);
                  m.Add(127); m.Add(0); m.Add(0); m.Add(99);
                  return m.ToArray();

               case 28:
               case 5:
                  // AAAA and CNAME: NODATA - NOERROR with no answers and the
                  // zone's SOA in the AUTHORITY section, per RFC 2308. The SOA is not
                  // decoration. Measured with a probe harness: the identical
                  // empty-NOERROR bytes WITHOUT it are answered 9501 (no records,
                  // benign) when they arrive over UDP and 9502 (DNS_ERROR_BAD_PACKET)
                  // when they arrive over TCP - the TCP response parser demands at
                  // least one record beyond the question. Omit this and every AAAA
                  // lookup "fails" in a way that has nothing to do with the code under
                  // test.
                  WriteHeaderAndQuestion_(m, query, 0, 1);
                  m.Add(0xC0); m.Add(0x0C);              // owner: the queried name
                  m.Add(0); m.Add(6);                    // TYPE SOA
                  m.Add(0); m.Add(1);                    // CLASS IN
                  m.Add(0); m.Add(0); m.Add(0); m.Add(60);
                  var soa = new List<byte>();
                  WriteName_(soa, "ns.dnsfix.example.test");
                  WriteName_(soa, "hostmaster.dnsfix.example.test");
                  for (int f = 0; f < 5; f++)            // serial/refresh/retry/expire/minimum
                  {
                     soa.Add(0); soa.Add(0); soa.Add(0); soa.Add(60);
                  }
                  m.Add((byte)(soa.Count >> 8)); m.Add((byte)(soa.Count & 0xFF));
                  m.AddRange(soa);
                  return m.ToArray();
            }

            // A query type this fake does not know how to answer. Returning null
            // rather than a catch-all NODATA is what gives UnansweredTcpQueries
            // something to count: with every switch arm returning bytes, that
            // counter could never move and the assertion on it in the test was
            // structurally incapable of failing.
            return null;
         }
      }

      [Test]
      [Description("A configured DNS server is accepted by the resolver rather than rejected as a malformed request")]
      public void AConfiguredDnsServerProducesADnsAnswerRatherThanARejectedRequest()
      {
         // Against the build that set the port, every line this test looks for reports
         // status 87 and the assertion below fails naming it - which is what makes this a
         // negative control rather than a test that happens to pass.
         SetIniSetting("DNSServer", UnreachableButValidServer);

         // Three query types are issued per lookup, and each waits the full DNSQueryTimeout
         // because the nominated server is deliberately unreachable. At the 10-second
         // default that is half a minute of test for nothing; two seconds proves the same
         // thing. Restored with DNSServer in the finally block.
         SetIniSetting("DNSQueryTimeout", "2");

         try
         {
            RestartServerAndReacquireCom();

            LogHandler.DeleteCurrentDefaultLog();

            // Any lookup will do; the SpamAssassin connection test is simply the shortest
            // path from COM to the resolver.
            string resultText;
            _settings.AntiSpam.TestSpamAssassinConnection("dns-probe.example.invalid", 783, out resultText);

            var log = LogHandler.ReadCurrentDefaultLog();

            // The request has to have been ACCEPTED and dispatched. There are two shapes
            // that prove it and the test takes either: a result line, when the nominated
            // server answers, and a timeout line, when it does not. 192.0.2.1 is TEST-NET-1
            // and answers nothing, so in practice it is the timeout - and a timeout is the
            // stronger evidence of the two. ERROR_INVALID_PARAMETER comes back in the same
            // millisecond it was asked for, without a packet leaving the machine; waiting
            // the whole DNSQueryTimeout means the query was genuinely sent and nothing came
            // back.
            var dispatched = log.Contains("DNS - Result.") || log.Contains("DNS - Query timed out.");

            Assert.IsTrue(dispatched,
               "The resolver produced neither a result line nor a timeout line, so it cannot be shown " +
               "that the lookup was attempted at all. Log:\r\n" + log);

            // 87 is ERROR_INVALID_PARAMETER: the DNS client refused the request before
            // sending anything. That is the defect this fixture exists for.
            StringAssert.DoesNotContain("status 87", log,
               "DnsQueryEx rejected the request as malformed (ERROR_INVALID_PARAMETER) with a DNS server " +
               "configured. Every lookup fails in this state - DNSBL, SPF, DKIM, DMARC and MX included - " +
               "and the most likely cause is a non-zero port in the DNS_ADDR server entry.");

            // And it has to have gone to the CONFIGURED server rather than the system one,
            // or the test proves nothing about DNSServer. The system resolver answers a name
            // in the reserved .invalid namespace with an immediate authoritative NXDOMAIN -
            // it never times out - so a timeout for this name is itself the proof that the
            // query went to 192.0.2.1. Where the query did complete, the result line says
            // which server was used, and that is checked directly.
            var usedConfiguredServer = log.Contains("DNS - Query timed out.") || log.Contains("custom server");

            Assert.IsTrue(usedConfiguredServer,
               "The lookup completed without using the configured DNS server, so this test proved nothing. " +
               "Check that DNSServer landed in the FIRST [Settings] section of the ini the server reads. " +
               "Log:\r\n" + log);
         }
         finally
         {
            SetIniSetting("DNSServer", SuiteDns.Resolver);
            SetIniSetting("DNSQueryTimeout", null);
            RestartServerAndReacquireCom();

            // This test deliberately asks for a name in the reserved .invalid namespace, so
            // SpamAssassinTestConnect reports HM5507 - correctly. Left behind, that entry
            // fails the setup of every fixture that runs afterwards, because
            // PerformBasicSetup treats the existence of the ERROR log as a failure. Settled
            // rather than deleted once, because the report is written from the thread that
            // ran the lookup and can arrive just after the COM call returns.
            LogHandler.ClearErrorLogUntilSettled();
         }
      }
   }
}
