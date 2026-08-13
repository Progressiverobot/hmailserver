// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.IO;
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
            SetIniSetting("DNSServer", null);
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
