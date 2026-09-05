// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.RegularExpressions;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.AntiSpam
{
   /// <summary>
   ///    The RFC 9990 (DMARCbis) aggregate report schema, selected by
   ///    DmarcRptSchemaVersion=2, delivered end to end.
   ///
   ///    The exact XML is pinned in C++ instead, in ClassTester, and deliberately.
   ///    BuildReportXml is a pure function of its parameters - that is why every
   ///    one of them is a parameter, including the organization name and the
   ///    generator string - so the cheapest and sharpest place to assert element
   ///    ordering, escaping and the presence or absence of individual members is
   ///    beside it, driving one hand-built bucket through both schemas and
   ///    diffing the results. Restating those assertions here would only prove
   ///    the same function again, several seconds more slowly.
   ///
   ///    What C# is needed for is everything that is NOT pure: that the ini
   ///    setting reaches the builder at all, that the value carried in
   ///    discovery_method came from a real policy lookup rather than a literal,
   ///    that the attachment filename the schema decides is the one the mail
   ///    actually carries - and, above all, that the DEFAULT did not move. A
   ///    fixture that only exercised the new schema would pass just as happily
   ///    if version 2 had silently become the default, which is the one outcome
   ///    that would break every domain this server reports to.
   /// </summary>
   [TestFixture]
   public class DmarcRpt2Schema : TestFixtureBase
   {
      /// <summary>
      ///    The policy domain, and it has to be this one: the pre-computed
      ///    signature below is over a From header naming it.
      /// </summary>
      private const string PolicyDomain = "sender-dom.example.test";

      /// <summary>
      ///    np= and t= are published so the two policy_published members that are
      ///    new in 9990 have something to report other than their defaults.
      ///    Neither changes the verdict here: np= applies only when the record was
      ///    found at the organizational domain rather than at the From domain, and
      ///    t= is read for the report and acted on nowhere.
      /// </summary>
      private const string DmarcPolicy =
         "v=DMARC1; p=none; np=reject; t=y; rua=mailto:dmarcreports@example.test";

      [Test]
      [Description("With DmarcRptSchemaVersion unset, reports stay in the RFC 7489 form - the one every deployed report processor parses")]
      public void TheDefaultRemainsTheRfc7489Form()
      {
         string report = RunOneReportingCycle_(null, null);

         ClassicAssert.IsFalse(report.Contains("urn:ietf:params:xml:ns:dmarc-2.0"),
            "The shipped default must not declare the DMARCbis namespace: almost nothing in the field " +
            "parses it yet, so a server that switched by itself would stop the domains it reports on " +
            "from being able to read their own reports. Got: " + report);

         StringAssert.Contains("<pct>", report,
            "pct exists only in the 7489 form, so its presence is what identifies this document. Got: " + report);

         ClassicAssert.IsFalse(report.Contains("<version>"),
            "The 7489 form this server has always emitted carries no version element. Got: " + report);
         ClassicAssert.IsFalse(report.Contains("<discovery_method>"),
            "discovery_method is a 9990 element and has no place in a 7489 document. Got: " + report);
         ClassicAssert.IsFalse(report.Contains("<generator>"),
            "generator is a 9990 element and has no place in a 7489 document. Got: " + report);

         // receiver "!" policy-domain "!" begin "!" end "." extension, with no
         // unique-id: unchanged from what receivers have been parsing all along.
         StringAssert.IsMatch("filename=\"[^!\"]+!" + Regex.Escape(PolicyDomain) + "![0-9]+![0-9]+\\.xml\"", report,
            "The 7489 attachment filename must be exactly the four-field form. Got: " + report);
      }

      [Test]
      [Description("DmarcRptSchemaVersion=2 emits the RFC 9990 schema: the dmarc-2.0 namespace, version 1.0 (not 2.0), no pct, and the new policy_published members")]
      public void SchemaVersionTwoEmitsTheRfc9990Form()
      {
         string report = RunOneReportingCycle_("2", null);

         StringAssert.Contains("<feedback xmlns=\"urn:ietf:params:xml:ns:dmarc-2.0\">", report,
            "The namespace is what identifies the document as the 2.0 schema. Got: " + report);

         // The trap in the whole schema. RFC 9990 3.1.1.2 says the version element
         // MUST be 1.0, and its own Appendix B sample - inside the dmarc-2.0
         // namespace - says 1.0. The 2.0 versions the SCHEMA, not the report.
         StringAssert.Contains("<version>1.0</version>", report,
            "RFC 9990 3.1.1.2: the version element MUST have the value 1.0. Got: " + report);
         ClassicAssert.IsFalse(report.Contains("<version>2.0</version>"),
            "2.0 is the namespace's version, not the report's; writing it here produces a document " +
            "that fails validation against the schema it claims. Got: " + report);

         ClassicAssert.IsFalse(report.Contains("<pct>"),
            "RFC 9990 dropped pct from the schema outright. Got: " + report);

         StringAssert.Contains("<np>reject</np>", report,
            "np= has nowhere to go in the 7489 form; reporting it is one of the reasons to send 9990. Got: " + report);
         StringAssert.Contains("<testing>y</testing>", report,
            "The published t= tag travels in policy_published/testing. Got: " + report);
         StringAssert.Contains("<generator>hMailServer", report,
            "report_metadata/generator tells the report consumer where to report a malformed report. Got: " + report);

         // The tree walk is on by default and no lookup failed, so it is the tree
         // walk that answered. The paired test below turns it off and expects the
         // other value from the same fixture - which is what makes this an
         // assertion about the mechanism rather than about a constant.
         StringAssert.Contains("<discovery_method>treewalk</discovery_method>", report,
            "discovery_method must name the mechanism that answered for this message. Got: " + report);

         // ActionDispositionType's new member, end to end. The signed seed aligns
         // its DKIM signature with its From domain, so it passed DMARC; the
         // unsigned one failed under p=none. In 7489 both are "none" and a reader
         // has to infer which from the sub-elements.
         StringAssert.Contains("<disposition>pass</disposition>", report,
            "A message that passed DMARC is reported as pass in 9990, not as none. Got: " + report);
         StringAssert.Contains("<disposition>none</disposition>", report,
            "A message that FAILED under p=none is still none - 'no action taken' and 'passed' are " +
            "exactly the two things the new member exists to separate, so relabelling the failure " +
            "would be worse than not having it. Got: " + report);

         // selector is minOccurs="1" in 9990 - required - and the signed seed
         // gives it a real one to name.
         StringAssert.Contains("<selector>reportsel</selector>", report,
            "Got: " + report);

         StringAssert.Contains("<scope>mfrom</scope>", report,
            "mfrom is the only scope 9990 admits, and the only identity DMARC evaluates. Got: " + report);

         // ...and the optional unique-id, which 9990 3.5.2 puts after the end
         // timestamp. It is what keeps two reports covering the same day for the
         // same domain from looking to the receiver like one report re-sent -
         // which 3.5.2 says may be discarded or may overwrite the first.
         StringAssert.IsMatch("filename=\"[^!\"]+!" + Regex.Escape(PolicyDomain) + "![0-9]+![0-9]+![0-9A-Za-z]+\\.xml\"", report,
            "The 9990 attachment filename must carry the extra unique-id field. Got: " + report);
      }

      [Test]
      [Description("discovery_method reports the mechanism that actually answered: with the tree walk switched off the Public Suffix List did, and the report says psl")]
      public void DiscoveryMethodFollowsTheMechanismInUse()
      {
         string report = RunOneReportingCycle_("2", "0");

         StringAssert.Contains("<discovery_method>psl</discovery_method>", report,
            "With DmarcTreeWalkEnabled=0 the Public Suffix List is what resolved organizational " +
            "domains for this message, and RFC 9990 3.1.1.5 asks which method was USED. Got: " + report);

         ClassicAssert.IsFalse(report.Contains("<discovery_method>treewalk</discovery_method>"),
            "A report naming the tree walk while the list did the work would be describing this " +
            "server's ini file rather than the message. Got: " + report);
      }

      [Test]
      [Description("An unusable DmarcRptSchemaVersion is reported and falls back to 1 rather than deciding silently what goes on the wire")]
      public void AnOutOfRangeSchemaVersionIsReported()
      {
         try
         {
            ServerIniFile.SetSetting("DmarcRptSchemaVersion", "7");

            LogHandler.DeleteErrorLog();

            RestartServerAndReacquireCom();

            // Reported rather than corrected quietly: an administrator who typed a
            // version this server cannot write is owed the reason their reports are
            // still going out in the old schema. The fallback itself - that any
            // value but 2 produces the 7489 document byte for byte - is pinned
            // beside BuildReportXml, where it can be asserted on the bytes.
            CustomAsserts.AssertReportedError("DmarcRptSchemaVersion", "HM6210");
         }
         finally
         {
            // Cleared AFTER the restart, not before it: the value is read while the
            // service starts, so a clear followed by a start with the bad value
            // still in the file would leave the error behind to fail whichever
            // fixture ran next.
            ServerIniFile.SetSetting("DmarcRptSchemaVersion", null);
            RestartServerAndReacquireCom();
            LogHandler.DeleteErrorLog();
         }
      }

      /// <summary>
      ///    Seeds one unsigned and one signed message from the policy domain, asks
      ///    for the reports now, and returns the delivered report mail.
      ///
      ///    schemaVersion and treeWalkEnabled are written to the ini as given, or
      ///    left absent when null - "absent" being the case that matters most,
      ///    since it is what a stock server has.
      /// </summary>
      private string RunOneReportingCycle_(string schemaVersion, string treeWalkEnabled)
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "seedrcpt@example.test", "test");
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "dmarcreports@example.test", "test");

         var antiSpam = _application.Settings.AntiSpam;
         antiSpam.SpamMarkThreshold = 100;
         antiSpam.SpamDeleteThreshold = 1000;
         antiSpam.DMARCEnabled = true;
         antiSpam.DKIMVerificationEnabled = true;

         SuiteDns.Zone
            // example.test publishes a policy of its own so that under the tree
            // walk it is the organizational domain of PolicyDomain - which is what
            // makes the rua target, a mailbox at example.test, an INTERNAL
            // destination needing no authorization record. See the sibling fixture
            // DmarcRptReporting for the full reasoning.
            .WithTxt("_dmarc.example.test", "v=DMARC1; p=none")
            .WithTxt("_dmarc." + PolicyDomain, DmarcPolicy)

            // The public half of the key the signed seed below was signed with.
            .WithTxt("reportsel._domainkey." + PolicyDomain, "v=DKIM1; k=rsa; p=MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAit1HZshVeIm3Yu3dBqKzIAQDM+k5hPu+S9RzJeaFnPQw888jfvuBQkVTinZWn65X4TLhcEjsV7iDgWzVhcEKUUphhpR9i+JgOjncOSxs7zvv2xOpFuYweOqVrWV9brr8DEt3f+MdfYUiz62toL82Za447DOhNI/YAVEJqCmgbeSycN2emmZC6Z8dXV7fxKM3IeJ6G8hVLbvWhZe8fHkJ0+tJXeARBHhowFW1VXgkOGOHFtPjpmNrJRbbDKf8+IqyUk9uV51y3GEIunovr1Yc3vvExpXwWLZIdqKtvGVFBxyvTtuAmtw7Ebmz0evN41wH7vTWgui0VgsZqNIIUwz+fQIDAQAB");
         try
         {
            ServerIniFile.SetSetting("DNSQueryTimeout", "5");
            ServerIniFile.SetSetting("DmarcRptFromAddress", "postmaster@example.test");
            ServerIniFile.SetSetting("DmarcRptSchemaVersion", schemaVersion);
            ServerIniFile.SetSetting("DmarcTreeWalkEnabled", treeWalkEnabled);

            RestartServerAndReacquireCom();

            // Two rows with deliberately different verdicts. The unsigned one
            // fails DMARC under p=none; the signed one aligns its d= with its
            // From and passes. Both are disposition "none" as this server
            // records them, and separating them is the whole of what 9990's
            // extra "pass" member buys.
            SmtpClientSimulator.StaticSendRaw("bounce@unaligned-envelope.test", "seedrcpt@example.test",
                                              UnsignedSeed);
            SmtpClientSimulator.StaticSendRaw("bounce@unaligned-envelope.test", "seedrcpt@example.test",
                                              SignedSeed);

            // Both are in the mailbox, so both spam-time evaluations - which run
            // before delivery - have been recorded.
            Pop3ClientSimulator.AssertMessageCount("seedrcpt@example.test", "test", 2);

            ClassicAssert.AreEqual(1, (int) _application.Utilities.SendDmarcReports(true),
               "One policy domain requested reports, so one report.");

            return Pop3ClientSimulator.AssertGetFirstMessageText("dmarcreports@example.test", "test");
         }
         finally
         {
            SuiteDns.Reset();

            ServerIniFile.SetSetting("DNSQueryTimeout", null);
            ServerIniFile.SetSetting("DmarcRptFromAddress", null);
            ServerIniFile.SetSetting("DmarcRptSchemaVersion", null);
            ServerIniFile.SetSetting("DmarcTreeWalkEnabled", null);

            // Restart before any COM restore - proxies taken before the mid-test
            // restart point at the old process.
            RestartServerAndReacquireCom();
         }
      }

      /// <summary>
      ///    The envelope sender is deliberately a different organizational domain
      ///    from the From header, so SPF cannot align whatever the raw result was.
      /// </summary>
      private const string UnsignedSeed =
            "From: <user@sender-dom.example.test>\r\n" +
            "To: <seedrcpt@example.test>\r\n" +
            "Subject: dmarc schema seed\r\n" +
            "Date: Thu, 20 Aug 2026 10:00:00 +0000\r\n" +
            "\r\n" +
            "Seed body\r\n";

      /// <summary>
      ///    A genuinely rsa-sha256-signed message from the policy domain, signed
      ///    with the repository's example key, whose public half the zone above
      ///    publishes at reportsel._domainkey. Pre-computed rather than signed at
      ///    run time so the fixture is testing the REPORT rather than a signer.
      ///
      ///    Its d= aligns with its From, so it passes DMARC - which is what gives
      ///    the 9990 document a "pass" disposition to report and a selector to
      ///    name.
      /// </summary>
      private const string SignedSeed =
            "DKIM-Signature: v=1; a=rsa-sha256; c=simple/simple; d=sender-dom.example.test; s=reportsel; h=from:to:subject:date; bh=UiW1Vq02S/LU/SLyPMpS2+090dMUxUOeE/rTgKpvR2E=; b=E2azn0KKCQj/gs1l8KO8dyBJndQbtqTT2XnB1I83hnsvTM5Sns7nESoLIEyg4wBH8otamGx0zhxmK2l2hT/wQc4tpPkcr5mcuzAXqX3wky62Jk/CpQBvRIFHmch2t2Z9lftVeHqzXxjub3X9R11ocr+lTnionM8fA+QlCOsv3JfoHxb/36O8gzfIeX/zOQgD2p7dy61EW/1QU6APPtwSaWYcEceHAgDM+ZOzInX8wonrinub3pKgJrng2gQXTzm1SPHekwLZUgKABoYSfCJzFKow8hamNziBKQQoKKuWsySgNWOl2w179/8I0exJ0wEhgTjDJ/DFLBpKDu8m39eCAQ==\r\n" +
            "From: <user@sender-dom.example.test>\r\n" +
            "To: <seedrcpt@example.test>\r\n" +
            "Subject: dkim selector seed\r\n" +
            "Date: Thu, 20 Aug 2026 10:00:00 +0000\r\n" +
            "\r\n" +
            "Signed so the aggregate report has a selector to name.\r\n";
   }
}
