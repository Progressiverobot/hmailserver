// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.AntiSpam
{
   /// <summary>
   ///    The RFC 9989 (DMARCbis) section 4.10 DNS tree walk, end to end.
   ///
   ///    DmarcOrganizationalDomain.cs, the fixture next to this one, opens by saying
   ///    the org-domain computation "cannot be exercised end-to-end from this suite:
   ///    it sits behind DMARC policy discovery, which needs a DNS server that answers
   ///    TXT queries for domains the test controls, and the harness has no such
   ///    server". It now has one, so this fixture does what that one could not: it
   ///    publishes a zone and reads the verdict off the delivered message.
   ///
   ///    Every test here is built as a DISAGREEMENT between the two mechanisms. A
   ///    case both the Public Suffix List and the tree walk get right proves nothing
   ///    about which one ran - and the tree walk was implemented precisely because
   ///    the two disagree on domains that publish psd=, where the PSL's answer is not
   ///    a near miss but the wrong policy decision. So each test asserts the
   ///    tree-walk verdict with the walk on, and then asserts the OPPOSITE verdict
   ///    with DmarcTreeWalkEnabled=0. The second half is what makes the first half
   ///    evidence: without it, a test that never consulted DNS at all would pass.
   ///
   ///    The verdict is read from the RFC 8601 Authentication-Results header rather
   ///    than from a rejection, because it distinguishes all three outcomes that
   ///    matter here - dmarc=pass, dmarc=fail with the policy in parentheses, and
   ///    dmarc=none meaning "we looked and found no policy" - where a delivered or
   ///    not-delivered observation collapses two of them.
   /// </summary>
   [TestFixture]
   public class DmarcTreeWalkResolution : TestFixtureBase
   {
      /// <summary>
      ///    Reads the dmarc= token, with its policy parenthetical if it has one, out
      ///    of the delivered message's Authentication-Results header. Returns "" when
      ///    the method is absent, which is a distinct outcome from dmarc=none.
      /// </summary>
      private static string DmarcVerdict(string messageText)
      {
         var match = Regex.Match(messageText, @"dmarc=(\w+)(\s*\([^)]*\))?",
                                 RegexOptions.IgnoreCase);

         if (!match.Success)
            return "";

         return ("dmarc=" + match.Groups[1].Value + match.Groups[2].Value)
            .Replace(" ", "").ToLowerInvariant();
      }

      /// <summary>
      ///    Sends one message and returns it as delivered. Takes an address rather than
      ///    creating the account, because every COM proxy this fixture holds is taken
      ///    BEFORE the first restart and is dead afterwards - the trap the DMARC
      ///    reporting fixture found the hard way. Nothing here touches COM at all: the
      ///    SMTP and IMAP simulators are raw sockets.
      /// </summary>
      private static string SendAndReadVerdict(string address, string fromDomain, string envelopeDomain)
      {
         string message =
            "From: <user@" + fromDomain + ">\r\n" +
            "To: <" + address + ">\r\n" +
            "Subject: tree walk probe\r\n" +
            "Date: Thu, 20 Aug 2026 10:00:00 +0000\r\n" +
            "\r\n" +
            "Probe body\r\n";

         SmtpClientSimulator.StaticSendRaw("bounce@" + envelopeDomain, address, message);

         ImapClientSimulator.AssertMessageCount(address, "test", "Inbox", 1);

         var imap = new ImapClientSimulator();
         imap.ConnectAndLogon(address, "test");
         imap.SelectFolder("Inbox");

         return imap.Fetch("1 RFC822");
      }

      /// <summary>
      ///    Both tests need DMARC evaluating, the verdict written into a header, and
      ///    the scores kept well below the mark threshold - a DMARC failure scores,
      ///    and a message moved to junk or deleted carries no header to read.
      /// </summary>
      private void PrepareServerAndAccounts(params string[] accounts)
      {
         var antiSpam = _application.Settings.AntiSpam;
         antiSpam.SpamMarkThreshold = 100;
         antiSpam.SpamDeleteThreshold = 1000;
         antiSpam.DMARCEnabled = true;

         foreach (string account in accounts)
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, account, "test");

         ServerIniFile.SetSetting("AuthenticationResultsEnabled", "1");
         ServerIniFile.SetSetting("DNSQueryTimeout", "5");
      }

      private static void RestoreDefaults()
      {
         ServerIniFile.SetSetting("AuthenticationResultsEnabled", null);
         ServerIniFile.SetSetting("DmarcTreeWalkEnabled", null);
         ServerIniFile.SetSetting("DNSQueryTimeout", null);
      }

      [Test]
      [Description("A psd=n record marks an organizational boundary the Public Suffix List cannot know about, so a policy published there governs subdomain mail. With the walk disabled the same zone yields dmarc=none - the PSL looks one level too high and finds nothing.")]
      public void APsdNoRecordPublishesABoundaryThePublicSuffixListCannotKnow()
      {
         PrepareServerAndAccounts("walk-on@example.test", "walk-off@example.test");

         // division.example.test is a registrant's SUBdomain, so no suffix list will
         // ever list it. Under DMARCbis it declares itself an organizational domain
         // with psd=n, and mail from below it is then governed by the policy it
         // publishes. Under the PSL the org domain of mail.division.example.test is
         // example.test, which publishes nothing at all.
         SuiteDns.Zone
            .WithTxt("_dmarc.division.example.test", "v=DMARC1; p=reject; psd=n");
         try
         {
            ServerIniFile.SetSetting("DmarcTreeWalkEnabled", "1");

            RestartServerAndReacquireCom();
            SuiteDns.Zone.ClearQueries();

            string delivered = SendAndReadVerdict("walk-on@example.test",
                                                  "mail.division.example.test",
                                                  "unaligned-envelope.test");

            ClassicAssert.AreEqual("dmarc=fail(p=reject)", DmarcVerdict(delivered),
               "The walk should have found psd=n at division.example.test, made it the " +
               "organizational domain of mail.division.example.test, and discovered the " +
               "p=reject published there. A p=reject that a receiver never looks for is a " +
               "domain owner's enforcement silently discarded.\r\n" + delivered);

            // The walk asked for the name below the boundary first, and stopped AT the
            // boundary - it must not have gone on to example.test, because psd=n is an
            // instruction to stop, and continuing past it would find a parent's policy
            // and apply it to a domain that has declared itself independent.
            var asked = SuiteDns.Zone.Queries
               .Where(q => q.StartsWith(FakeDnsServer.TypeTxt + "/_dmarc.")).ToList();

            ClassicAssert.IsTrue(asked.Contains(FakeDnsServer.TypeTxt + "/_dmarc.mail.division.example.test"),
               "The walk starts at the domain itself. Asked: " + string.Join(", ", asked));
            ClassicAssert.IsFalse(asked.Contains(FakeDnsServer.TypeTxt + "/_dmarc.example.test"),
               "psd=n ends the walk, so nothing above division.example.test may be queried. " +
               "Asked: " + string.Join(", ", asked));

            // The negative control, same zone, same message, walk off.
            ServerIniFile.SetSetting("DmarcTreeWalkEnabled", "0");
            RestartServerAndReacquireCom();

            string withoutWalk = SendAndReadVerdict("walk-off@example.test",
                                                    "mail.division.example.test",
                                                    "unaligned-envelope.test");

            ClassicAssert.AreEqual("dmarc=none", DmarcVerdict(withoutWalk),
               "With the walk off the Public Suffix List answers example.test, which publishes " +
               "no policy - so the verdict is none. If this says fail, the setting is not an " +
               "escape hatch and the assertion above proves nothing about which mechanism " +
               "ran.\r\n" + withoutWalk);
         }
         finally
         {
            SuiteDns.Reset();

            RestoreDefaults();
            RestartServerAndReacquireCom();
         }
      }

      [Test]
      [Description("psd=y at a name makes it a public suffix, so two domains beneath it are separate organizations and cannot align with each other. The Public Suffix List, which has never heard of that name, aligns them - which is the forgery the tree walk closes.")]
      public void APsdYesRecordSeparatesTwoDomainsThePublicSuffixListWouldAlign()
      {
         PrepareServerAndAccounts("psd-on@example.test", "psd-off@example.test");

         // hosting.test declares itself a public suffix. a.hosting.test and
         // b.hosting.test are then two unrelated organizations that happen to share a
         // parent - exactly the relationship the PSL's PRIVATE section exists to
         // record for the shared-hosting providers who asked to be in it, and which
         // DMARCbis lets any provider declare for itself without asking anyone.
         //
         // SPF passes for the envelope domain because the evaluator gives 127.0.0.1 a
         // free pass, so the ONLY variable in the alignment is the organizational
         // domain each side resolves to.
         SuiteDns.Zone
            .WithTxt("_dmarc.hosting.test", "v=DMARC1; p=none; psd=y")
            .WithTxt("_dmarc.a.hosting.test", "v=DMARC1; p=none");
         try
         {
            ServerIniFile.SetSetting("DmarcTreeWalkEnabled", "1");

            RestartServerAndReacquireCom();

            string separated = SendAndReadVerdict("psd-on@example.test",
                                                  "a.hosting.test",
                                                  "b.hosting.test");

            ClassicAssert.AreEqual("dmarc=fail(p=none)", DmarcVerdict(separated),
               "b.hosting.test is a different organization from a.hosting.test once " +
               "hosting.test says psd=y, so an SPF pass at b cannot align with a From at a. " +
               "Aligning them lets any tenant of a shared parent send as any other.\r\n" +
               separated);

            ServerIniFile.SetSetting("DmarcTreeWalkEnabled", "0");
            RestartServerAndReacquireCom();

            string aligned = SendAndReadVerdict("psd-off@example.test",
                                                "a.hosting.test",
                                                "b.hosting.test");

            ClassicAssert.AreEqual("dmarc=pass", DmarcVerdict(aligned),
               "With the walk off, the Public Suffix List has never heard of hosting.test, so " +
               "it resolves both sides to hosting.test and the two tenants align. That is the " +
               "pre-DMARCbis behaviour, and its presence here is what shows the assertion " +
               "above is the walk's doing.\r\n" + aligned);
         }
         finally
         {
            SuiteDns.Reset();

            RestoreDefaults();
            RestartServerAndReacquireCom();
         }
      }
   }
}
