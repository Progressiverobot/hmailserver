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

      private const string DmarcPolicy = "v=DMARC1; p=none; rua=mailto:dmarcreports@example.test";

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
            ServerIniFile.SetSetting("DmarcRptFromAddress", null);

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
         antiSpam.DKIMVerificationEnabled = true;

         // The zone this test needs, served locally: each policy domain publishes
         // p=none with a rua, and exactly one of the two external domains publishes the
         // RFC 7489 7.1 consent record that authorizes reports to be sent to it.
         using (var fakeDns = new FakeDnsServer()
            // example.test publishes a bare policy of its own, and it has to: under the
            // RFC 9989 tree walk the organizational domain is the record found at the
            // FEWEST labels, so with nothing published here sender-dom.example.test
            // would be its own organizational domain - and its rua, which points at
            // example.test, would become an EXTERNAL destination needing authorization.
            // That is the correct DMARCbis reading rather than a quirk of the harness:
            // publishing a record is how a name declares itself, and the internal case
            // this fixture is built on only exists while a parent has declared one. A
            // real org domain running DMARC publishes exactly this.
            .WithTxt("_dmarc.example.test", "v=DMARC1; p=none")
            .WithTxt("_dmarc." + InternalPolicyDomain, DmarcPolicy)
            .WithTxt("_dmarc." + ExternalUnverifiedPolicyDomain, DmarcPolicy)
            .WithTxt("_dmarc." + ExternalVerifiedPolicyDomain, DmarcPolicy)
            .WithTxt(ExternalVerifiedPolicyDomain + "._report._dmarc.example.test", "v=DMARC1")

            // The signing key for the seed below, so one row in the internal report
            // is a DKIM pass with a selector to name rather than another fail.
            .WithTxt("reportsel._domainkey." + InternalPolicyDomain, "v=DKIM1; k=rsa; p=MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAit1HZshVeIm3Yu3dBqKzIAQDM+k5hPu+S9RzJeaFnPQw888jfvuBQkVTinZWn65X4TLhcEjsV7iDgWzVhcEKUUphhpR9i+JgOjncOSxs7zvv2xOpFuYweOqVrWV9brr8DEt3f+MdfYUiz62toL82Za447DOhNI/YAVEJqCmgbeSycN2emmZC6Z8dXV7fxKM3IeJ6G8hVLbvWhZe8fHkJ0+tJXeARBHhowFW1VXgkOGOHFtPjpmNrJRbbDKf8+IqyUk9uV51y3GEIunovr1Yc3vvExpXwWLZIdqKtvGVFBxyvTtuAmtw7Ebmz0evN41wH7vTWgui0VgsZqNIIUwz+fQIDAQAB"))
         {
            try
            {
               ServerIniFile.SetSetting("DNSServer", "127.0.0.1");
               ServerIniFile.SetSetting("DNSQueryTimeout", "5");
               ServerIniFile.SetSetting("DmarcRptFromAddress", "postmaster@example.test");

               RestartServerAndReacquireCom();

               // Three inbound messages, one per policy domain. Each From
               // domain publishes p=none, so every seed is delivered and every
               // evaluation is recorded; none of the domains has SPF or DKIM,
               // so each records an aligned-fail row - the row a domain owner
               // deploys DMARC to see.
               SendSeed_(InternalPolicyDomain);
               SendSeed_(ExternalUnverifiedPolicyDomain);
               SendSeed_(ExternalVerifiedPolicyDomain);

               // ...and one that is really signed, by a key this test publishes, so
               // the report has a passing DKIM row. Everything else here produces
               // failures, and a report where nothing ever passes cannot show
               // whether the selector is being recorded or merely omitted.
               SmtpClientSimulator.StaticSendRaw("bounce@unaligned-envelope.test", "seedrcpt@example.test",
                                                 SignedSeed);

               // The seeds are in the mailbox, so their spam-time evaluations -
               // which run before delivery - are all recorded.
               Pop3ClientSimulator.AssertMessageCount("seedrcpt@example.test", "test", 4);

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

               // The selector, which is what turns "something of yours signed this"
               // into "this key signed this" - the difference between a report a
               // domain owner can read and one they can act on during a rotation or
               // after a key is compromised.
               StringAssert.Contains("<selector>reportsel</selector>", internalReport,
                  "auth_results/dkim should name the selector that signed (RFC 7489 Appendix C). Got: " +
                  internalReport);

               // ...and the SPF identity's scope, which says WHICH identity was
               // evaluated. Without it a reader cannot tell an envelope-sender check
               // from a HELO one.
               StringAssert.Contains("<scope>mfrom</scope>", internalReport,
                  "auth_results/spf should say which identity SPF was evaluated on. Got: " + internalReport);

               // The pop is destructive by design: nothing new has been
               // recorded, so a second call has nothing to send.
               ClassicAssert.AreEqual(0, (int) _application.Utilities.SendDmarcReports(true),
                  "The first call popped the day; with no evaluations since, the second has nothing.");
            }
            finally
            {
               ServerIniFile.SetSetting("DNSServer", null);
               ServerIniFile.SetSetting("DNSQueryTimeout", null);
               ServerIniFile.SetSetting("DmarcRptFromAddress", null);

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

      /// <summary>
      ///    A genuinely rsa-sha256-signed message from the internal policy domain,
      ///    signed with the repository's example key, whose public half the zone
      ///    above publishes at reportsel._domainkey. Pre-computed rather than signed
      ///    at run time so the fixture is testing the REPORT rather than a signer.
      /// </summary>
      private const string SignedSeed =
            "DKIM-Signature: v=1; a=rsa-sha256; c=simple/simple; d=sender-dom.example.test; s=reportsel; h=from:to:subject:date; bh=UiW1Vq02S/LU/SLyPMpS2+090dMUxUOeE/rTgKpvR2E=; b=E2azn0KKCQj/gs1l8KO8dyBJndQbtqTT2XnB1I83hnsvTM5Sns7nESoLIEyg4wBH8otamGx0zhxmK2l2hT/wQc4tpPkcr5mcuzAXqX3wky62Jk/CpQBvRIFHmch2t2Z9lftVeHqzXxjub3X9R11ocr+lTnionM8fA+QlCOsv3JfoHxb/36O8gzfIeX/zOQgD2p7dy61EW/1QU6APPtwSaWYcEceHAgDM+ZOzInX8wonrinub3pKgJrng2gQXTzm1SPHekwLZUgKABoYSfCJzFKow8hamNziBKQQoKKuWsySgNWOl2w179/8I0exJ0wEhgTjDJ/DFLBpKDu8m39eCAQ==\r\n" +
            "From: <user@sender-dom.example.test>\r\n" +
            "To: <seedrcpt@example.test>\r\n" +
            "Subject: dkim selector seed\r\n" +
            "Date: Thu, 20 Aug 2026 10:00:00 +0000\r\n" +
            "\r\n" +
            "Signed so the aggregate report has a selector to name.\r\n";

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

   }
}
