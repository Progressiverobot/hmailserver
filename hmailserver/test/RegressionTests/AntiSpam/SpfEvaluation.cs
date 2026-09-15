// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.RegularExpressions;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.AntiSpam
{
   /// <summary>
   ///    SPF, RFC 7208, evaluated against a policy this suite publishes itself.
   ///
   ///    This fixture could not be written before 15 September 2026, and the reason is
   ///    the whole point of it. The evaluator was RMSPF, a vendored C library that did
   ///    its own DNS: it called DnsQuery_A in DNSAPI.DLL, so it ignored the DNSServer
   ///    setting every other lookup in this server honours, and <see cref="SuiteDns" />
   ///    - the zone the whole suite resolves through - was invisible to it. No test
   ///    could put a policy in front of it and no test could assert a verdict, which is
   ///    why the fixtures that touch SPF up to now all say, in as many words, that they
   ///    do not depend on which verdict it reached. The void-lookup limit had to be
   ///    pinned by handing the library a policy through a back door, and the module's
   ///    own self-test evaluated the live zone of hmailserver.com and died when that
   ///    zone began answering SERVFAIL.
   ///
   ///    The RFC 7208 evaluator that replaced it resolves through this server's own
   ///    DNSResolver. So the policy below is published in the suite's zone, the server
   ///    reads it from there, and the verdict is asserted - which is the end-to-end
   ///    proof that the evaluator is wired to this server's resolver rather than merely
   ///    correct in isolation. The 203 cases of the openspf.org conformance suite prove
   ///    the second half, in process, through Utilities.RunTestSuite.
   ///
   ///    All four results asserted here are reported through the RFC 7208 section 9.1
   ///    Received-SPF header, which needs ReceivedSpfHeaderEnabled=1, and through the
   ///    RFC 8601 Authentication-Results header, which needs
   ///    AuthenticationResultsEnabled=1. Both default to 0 and both are restored in a
   ///    finally block: IniFileSettings is cached for the life of the server process,
   ///    so a leaked value poisons every fixture after this one.
   ///
   ///    Two of the four - none and softfail - are results this server could not report
   ///    at all until the evaluator was replaced. SPF::Result had three values and
   ///    everything that was not a pass or a fail was collapsed onto neutral, so a
   ///    domain that publishes no policy, a domain asking that a message be accepted
   ///    anyway, and a domain that genuinely makes no assertion were one answer.
   /// </summary>
   [TestFixture]
   public class SpfEvaluation : TestFixtureBase
   {
      /// <summary>
      ///    The client address every message in this fixture arrives from: the suite's
      ///    simulators connect to the server over loopback, so this is what the policies
      ///    below are written about.
      /// </summary>
      private const string ClientAddress = "127.0.0.1";

      [SetUp]
      public new void SetUp()
      {
         var antiSpam = _settings.AntiSpam;

         antiSpam.UseSPF = true;
         antiSpam.UseSPFScore = 3;

         // The thresholds are already 10000 from the per-test setup, so a fail cannot
         // mark or delete the message this fixture then has to fetch. The verdict is
         // what is under test, not the scoring - Basics covers that.

         IniFileSetting.Write("ReceivedSpfHeaderEnabled", "1");
         IniFileSetting.Write("AuthenticationResultsEnabled", "1");
         _application.Reinitialize();
      }

      [TearDown]
      public new void TearDown()
      {
         IniFileSetting.Write("ReceivedSpfHeaderEnabled", "0");
         IniFileSetting.Write("AuthenticationResultsEnabled", "0");
         _application.Reinitialize();

         _settings.AntiSpam.UseSPF = false;

         // Nothing this fixture published may be visible to the fixture after it: the
         // zone is the whole suite's, and a policy left in it would decide a verdict
         // somewhere that is not asserting one.
         SuiteDns.Reset();
      }

      /// <summary>
      ///    The verdict word of the Received-SPF header, lower-cased, or "" where the
      ///    message carries no such header. RFC 7208 section 9.1 puts the result first.
      /// </summary>
      private static string ReceivedSpfResult(string messageData)
      {
         var match = Regex.Match(messageData, @"^Received-SPF:\s*(\w+)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);

         return match.Success ? match.Groups[1].Value.ToLowerInvariant() : "";
      }

      /// <summary>
      ///    The spf= keyword of the Authentication-Results header, lower-cased, or ""
      ///    where there is none.
      ///
      ///    Read out of the unfolded message, because MimeCode folds a header value at
      ///    76 characters on the last space: whether spf= lands on the first line or a
      ///    continuation line depends on how long the fields ahead of it happen to be,
      ///    which for this header means the length of the sender's own domain.
      /// </summary>
      private static string AuthenticationResultsSpf(string messageData)
      {
         string unfolded = Regex.Replace(messageData, "\r\n[ \t]+", " ");

         var match = Regex.Match(unfolded, @"spf=(\w+)", RegexOptions.IgnoreCase);

         return match.Success ? match.Groups[1].Value.ToLowerInvariant() : "";
      }

      /// <summary>
      ///    Sends one inbound message from senderDomain to a fresh local account and
      ///    returns the delivered message as stored. The envelope sender is what SPF is
      ///    evaluated against (RFC 7208 section 2.4), so the domain is the input.
      /// </summary>
      private string SendFrom(string accountName, string senderDomain)
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, accountName + "@example.test", "test");

         SmtpClientSimulator.StaticSend("sender@" + senderDomain, account.Address, "SPF probe", "Probe body");

         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var imap = new ImapClientSimulator();
         imap.ConnectAndLogon(account.Address, "test");
         imap.SelectFolder("Inbox");

         return imap.Fetch("1 RFC822");
      }

      [Test]
      [Description("A domain whose policy names the connecting client passes, and both trace headers say so. " +
                   "This is the assertion no test could make before the RFC 7208 evaluator landed: the policy " +
                   "is published in the suite's own DNS zone, so a pass here proves the evaluator resolves " +
                   "through this server's configured resolver. If it regresses, SPF is either not being " +
                   "evaluated at all or is resolving somewhere other than where this server was told to.")]
      public void APolicyThatNamesTheClientPasses()
      {
         SuiteDns.Zone.WithTxt("spf-pass.test", "v=spf1 ip4:" + ClientAddress + " -all");

         var messageData = SendFrom("spf-pass", "spf-pass.test");

         Assert.AreEqual("pass", ReceivedSpfResult(messageData),
            "The policy names the connecting client, so the verdict is a pass.\r\n" + messageData);

         Assert.AreEqual("pass", AuthenticationResultsSpf(messageData),
            "Authentication-Results must agree with Received-SPF.\r\n" + messageData);
      }

      [Test]
      [Description("A domain whose policy ends in -all and names nobody fails, and both trace headers say so. " +
                   "The negative half of the pair: a pass that is reported for every sender is not a check. " +
                   "If this regresses, a forged envelope sender from a domain that publishes a strict policy " +
                   "is accepted with no verdict against it, and DMARC loses its SPF half with it.")]
      public void APolicyThatNamesNobodyFails()
      {
         SuiteDns.Zone.WithTxt("spf-fail.test", "v=spf1 -all");

         var messageData = SendFrom("spf-fail", "spf-fail.test");

         Assert.AreEqual("fail", ReceivedSpfResult(messageData),
            "The policy authorizes nobody, so the verdict is a fail.\r\n" + messageData);

         Assert.AreEqual("fail", AuthenticationResultsSpf(messageData),
            "Authentication-Results must agree with Received-SPF.\r\n" + messageData);
      }

      [Test]
      [Description("A domain whose policy ends in ~all softfails rather than failing. RFC 7208 section 8.4 " +
                   "is the domain saying the client is probably not authorized but asking that the message be " +
                   "accepted anyway, and it is one of the four results this server could not report before the " +
                   "evaluator was replaced. If it regresses as a fail, mail from every domain still rolling " +
                   "out SPF is scored as forged; as a neutral, a receiver downstream cannot tell the domain " +
                   "said anything at all.")]
      public void APolicyEndingInTildeAllSoftFails()
      {
         SuiteDns.Zone.WithTxt("spf-softfail.test", "v=spf1 ~all");

         var messageData = SendFrom("spf-softfail", "spf-softfail.test");

         Assert.AreEqual("softfail", ReceivedSpfResult(messageData),
            "~all is a softfail, which is neither a fail nor a neutral.\r\n" + messageData);

         Assert.AreEqual("softfail", AuthenticationResultsSpf(messageData),
            "Authentication-Results must agree with Received-SPF.\r\n" + messageData);
      }

      [Test]
      [Description("A domain that publishes no SPF record at all is a none, not a neutral. RFC 8601 keeps " +
                   "those apart because they say different things: none is 'the domain has no policy' and " +
                   "neutral is 'the domain has a policy and it makes no assertion about this client'. Until " +
                   "the RFC 7208 evaluator landed this server reported both as neutral, so a downstream " +
                   "filter acting on our header was told the domain had spoken when it had not.")]
      public void ADomainWithNoRecordIsNoneRatherThanNeutral()
      {
         // Nothing published: the suite's zone answers NODATA for a name it has not been
         // given, which is exactly what a domain with no SPF record looks like.
         var messageData = SendFrom("spf-none", "spf-none.test");

         Assert.AreEqual("none", ReceivedSpfResult(messageData),
            "A domain that publishes no policy produces a none.\r\n" + messageData);

         Assert.AreEqual("none", AuthenticationResultsSpf(messageData),
            "Authentication-Results must agree with Received-SPF.\r\n" + messageData);
      }
   }
}
