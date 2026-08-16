// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System;
using System.Text.RegularExpressions;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.AntiSpam
{
   /// <summary>
   ///    RFC 8601 Authentication-Results and RFC 7208 section 9.1 Received-SPF on
   ///    inbound mail, driven by three hMailServer.ini [Settings] values:
   ///    AuthenticationResultsEnabled, ReceivedSpfHeaderEnabled and
   ///    AuthenticationResultsIdentity.
   ///
   ///    The server only writes these headers when at least one authentication check
   ///    actually produced a verdict, so most tests here turn on the SPF check - the
   ///    per-test setup has disabled every anti-spam check, and with nothing evaluated
   ///    there is nothing to write. The spam thresholds stay at 10000 from that same
   ///    setup, so whatever verdict SPF reaches for the loopback client cannot mark or
   ///    delete the test message.
   ///
   ///    Every ini value this fixture touches is restored, and the server
   ///    reinitialized, in a finally block: IniFileSettings is cached for the life of
   ///    the process, so a leaked value poisons every fixture that runs after this one.
   /// </summary>
   [TestFixture]
   public class AuthenticationResultsHeader : TestFixtureBase
   {
      [SetUp]
      public new void SetUp()
      {
         _antiSpam = _settings.AntiSpam;
      }

      private hMailServer.AntiSpam _antiSpam;

      /// <summary>
      ///    The identity used whenever a test needs to know the authserv-id in advance
      ///    (to forge a header carrying it, or to check the forgery strip matched on it).
      /// </summary>
      private const string ConfiguredIdentity = "authres-fixture.example";

      /// <summary>
      ///    Turns the SPF check on so the message arrives with a verdict to report.
      ///    The verdict itself does not matter to these tests - the assertions are on
      ///    header presence, count and authserv-id, never on pass/fail - so a change in
      ///    the sender domain's published SPF policy cannot break them.
      /// </summary>
      private void EnableSpfEvaluation()
      {
         _antiSpam.UseSPF = true;
         _antiSpam.UseSPFScore = 3;
      }

      /// <summary>
      ///    Puts every ini setting this fixture can have touched back to its shipped
      ///    default and makes the server re-read the file. Called from the finally
      ///    block of every test that writes the ini.
      /// </summary>
      private void RestoreIniDefaults()
      {
         IniFileSetting.Write("AuthenticationResultsEnabled", "0");
         IniFileSetting.Write("ReceivedSpfHeaderEnabled", "0");
         IniFileSetting.Write("AuthenticationResultsIdentity", "");
         _application.Reinitialize();
      }

      /// <summary>
      ///    Sends an inbound message - external envelope sender, unauthenticated, to a
      ///    local account - and returns the delivered message as stored.
      /// </summary>
      private string SendInboundMessageAndFetchIt(string accountName)
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, accountName, "test");

         SmtpClientSimulator.StaticSend("test@example.com", account.Address, "Test subject", "Test body");

         return FetchFirstInboxMessage(account.Address);
      }

      /// <summary>
      ///    Same, but the DATA payload is given verbatim - for messages that must
      ///    arrive already carrying an Authentication-Results header.
      /// </summary>
      private string SendRawInboundMessageAndFetchIt(string accountName, string messageText)
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, accountName, "test");

         SmtpClientSimulator.StaticSendRaw("test@example.com", account.Address, messageText);

         return FetchFirstInboxMessage(account.Address);
      }

      private static string FetchFirstInboxMessage(string address)
      {
         ImapClientSimulator.AssertMessageCount(address, "test", "Inbox", 1);

         var imap = new ImapClientSimulator();
         imap.ConnectAndLogon(address, "test");
         imap.SelectFolder("Inbox");

         return imap.Fetch("1 RFC822");
      }

      /// <summary>
      ///    Counts Authentication-Results fields. Anchored to line starts so a folded
      ///    continuation line, or the header name quoted inside another value, cannot
      ///    inflate the count.
      /// </summary>
      private static int CountAuthenticationResultsHeaders(string messageData)
      {
         return Regex.Matches(messageData, @"^Authentication-Results:",
            RegexOptions.IgnoreCase | RegexOptions.Multiline).Count;
      }

      /// <summary>
      ///    The first token of the first Authentication-Results field in the message -
      ///    the authserv-id, which RFC 8601 puts ahead of the first ';'. The server
      ///    prepends its own field above the existing header block, so on a message
      ///    that also carries someone else's field, the first one is ours.
      /// </summary>
      private static string FirstAuthenticationResultsToken(string messageData)
      {
         var match = Regex.Match(messageData, @"^Authentication-Results:[ \t]*([^;\s]+);",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);

         Assert.IsTrue(match.Success,
            "Expected an Authentication-Results header starting with an authserv-id token.\r\n" + messageData);

         return match.Groups[1].Value;
      }

      [Test]
      [Description("AuthenticationResultsEnabled and ReceivedSpfHeaderEnabled both default to 0. If this " +
                   "regresses, every unconfigured server starts rewriting each accepted message to add trace " +
                   "headers nobody asked for - disclosing the computer name to every recipient as the " +
                   "authserv-id, and silently stripping Authentication-Results headers that happen to match it.")]
      public void ByDefault_NoAuthenticationTraceHeadersAreAdded()
      {
         // SPF is evaluated, so a verdict exists and COULD be written. What keeps the
         // headers off the message must be the ini defaults alone.
         EnableSpfEvaluation();

         var messageData = SendInboundMessageAndFetchIt("defaults-off@example.test");

         Assert.IsFalse(messageData.ToLower().Contains("authentication-results:"),
            "No Authentication-Results header may be written while AuthenticationResultsEnabled is at its default of 0.\r\n" + messageData);
         Assert.IsFalse(messageData.ToLower().Contains("received-spf:"),
            "No Received-SPF header may be written while ReceivedSpfHeaderEnabled is at its default of 0.\r\n" + messageData);
      }

      [Test]
      [Description("With AuthenticationResultsEnabled=1, a delivered inbound message carries exactly one " +
                   "Authentication-Results header, and its first token is this computer's name lower-cased " +
                   "(the default authserv-id). If this regresses, a downstream filter keying on our " +
                   "authserv-id (RFC 8601 section 2.2) either finds no verdict at all, finds two fields and " +
                   "cannot tell which one is ours, or fails a case-sensitive match on the identity.")]
      public void WhenEnabled_DeliveredInboundMessageCarriesExactlyOneAuthenticationResultsHeader()
      {
         try
         {
            IniFileSetting.Write("AuthenticationResultsEnabled", "1");
            _application.Reinitialize();

            EnableSpfEvaluation();

            var messageData = SendInboundMessageAndFetchIt("authres-on@example.test");

            Assert.AreEqual(1, CountAuthenticationResultsHeaders(messageData),
               "Expected exactly one Authentication-Results header on the delivered message.\r\n" + messageData);

            // The server's own host name, not Environment.MachineName. Utilities::ComputerName
            // returns the configured Settings.HostName when there is one and only falls back
            // to the Win32 computer name when there is not - and the suite configures one. That
            // is deliberate rather than incidental: the same function supplies the name the
            // Received header says the message was received "by", so the authserv-id and the
            // Received trace always name the same server. Asserting the machine name instead
            // would pass only on a host with no host name configured.
            string expectedAuthservId = _settings.HostName;
            if (string.IsNullOrEmpty(expectedAuthservId))
               expectedAuthservId = Environment.MachineName;

            Assert.AreEqual(expectedAuthservId.ToLowerInvariant(), FirstAuthenticationResultsToken(messageData),
               "With no AuthenticationResultsIdentity configured, the authserv-id is the server's host name, lower-cased - " +
               "the same name the Received header uses for 'by'.\r\n" + messageData);

            Assert.IsTrue(messageData.ToLower().Contains("spf="),
               "The header should report the SPF check that ran.\r\n" + messageData);
         }
         finally
         {
            RestoreIniDefaults();
         }
      }

      [Test]
      [Description("AuthenticationResultsIdentity overrides the computer name as the authserv-id, and the " +
                   "header carries it lower-cased. If this regresses, a site whose downstream filters trust " +
                   "one configured identity across several servers stops matching our header - and the " +
                   "forgery strip matches on this same token, so an identity that drifts from the configured " +
                   "value also lets a header forged under the configured name survive.")]
      public void AuthenticationResultsIdentity_OverridesTheAuthservId()
      {
         try
         {
            IniFileSetting.Write("AuthenticationResultsEnabled", "1");

            // Mixed case on purpose: what must come out is the lower-cased form.
            IniFileSetting.Write("AuthenticationResultsIdentity", "MX.AuthRes-Override.Example");
            _application.Reinitialize();

            EnableSpfEvaluation();

            var messageData = SendInboundMessageAndFetchIt("authres-identity@example.test");

            Assert.AreEqual("mx.authres-override.example", FirstAuthenticationResultsToken(messageData),
               "AuthenticationResultsIdentity should replace the computer name as the authserv-id, lower-cased.\r\n" + messageData);
         }
         finally
         {
            RestoreIniDefaults();
         }
      }

      [Test]
      [Description("RFC 8601 section 5: an inbound message arriving with an Authentication-Results header " +
                   "that bears OUR authserv-id was not written by us, so it is removed and replaced by ours. " +
                   "If this regresses, any sender can write 'dkim=pass' in this server's name and every " +
                   "downstream filter that trusts our identity acts on a verdict the sender wrote themselves.")]
      public void ForgedHeaderBearingOurAuthservId_IsRemovedAndReplaced()
      {
         try
         {
            IniFileSetting.Write("AuthenticationResultsEnabled", "1");
            IniFileSetting.Write("AuthenticationResultsIdentity", ConfiguredIdentity);
            _application.Reinitialize();

            EnableSpfEvaluation();

            string forgedMessage =
               "Authentication-Results: " + ConfiguredIdentity + "; dkim=pass header.d=attacker.example\r\n" +
               "From: test@example.com\r\n" +
               "To: forged-ours@example.test\r\n" +
               "Subject: Forged Authentication-Results\r\n" +
               "\r\n" +
               "Body text\r\n";

            var messageData = SendRawInboundMessageAndFetchIt("forged-ours@example.test", forgedMessage);

            Assert.AreEqual(1, CountAuthenticationResultsHeaders(messageData),
               "Expected exactly one Authentication-Results header: the forged one removed, ours added.\r\n" + messageData);

            Assert.IsFalse(messageData.ToLower().Contains("attacker.example"),
               "The forged header's content must not survive anywhere in the delivered message.\r\n" + messageData);
            Assert.IsFalse(messageData.ToLower().Contains("dkim=pass"),
               "No DKIM verification ran, so a surviving dkim=pass can only be the forged verdict.\r\n" + messageData);

            Assert.AreEqual(ConfiguredIdentity, FirstAuthenticationResultsToken(messageData),
               "The one remaining header must be ours, under our authserv-id.\r\n" + messageData);
            Assert.IsTrue(messageData.ToLower().Contains("spf="),
               "Ours reports the check that actually ran.\r\n" + messageData);
         }
         finally
         {
            RestoreIniDefaults();
         }
      }

      [Test]
      [Description("A forged header may be FOLDED, which is ordinary RFC 5322 and needs no special effort. " +
                   "Deciding on the first physical line alone saw an empty value there, kept the field, and " +
                   "kept the continuation that actually carries our identity - so the sender's own dkim=pass " +
                   "survived under our name. The decision has to be made on the unfolded field.")]
      public void ForgedHeaderThatIsFolded_IsStillRemoved()
      {
         try
         {
            IniFileSetting.Write("AuthenticationResultsEnabled", "1");
            IniFileSetting.Write("AuthenticationResultsIdentity", ConfiguredIdentity);
            _application.Reinitialize();

            EnableSpfEvaluation();

            string forgedMessage =
               "Authentication-Results:\r\n" +
               "\t" + ConfiguredIdentity + "; dkim=pass header.d=attacker.example\r\n" +
               "From: test@example.com\r\n" +
               "To: forged-folded@example.test\r\n" +
               "Subject: Folded forged Authentication-Results\r\n" +
               "\r\n" +
               "Body text\r\n";

            var messageData = SendRawInboundMessageAndFetchIt("forged-folded@example.test", forgedMessage);

            Assert.AreEqual(1, CountAuthenticationResultsHeaders(messageData),
               "The folded forgery must be removed, leaving only ours.\r\n" + messageData);
            Assert.IsFalse(messageData.ToLower().Contains("attacker.example"),
               "No part of the folded forgery may survive.\r\n" + messageData);
            Assert.IsFalse(messageData.ToLower().Contains("dkim=pass"),
               "No DKIM verification ran, so a surviving dkim=pass is the forged verdict.\r\n" + messageData);
         }
         finally
         {
            RestoreIniDefaults();
         }
      }

      [Test]
      [Description("RFC 8601 allows the authserv-id as a quoted string, and the quotes are not part of the " +
                   "identity. Comparing with them attached meant \"ours\" did not match ours, so the forgery " +
                   "survived - and a downstream reader unquotes it and trusts it.")]
      public void ForgedHeaderWithQuotedAuthservId_IsStillRemoved()
      {
         try
         {
            IniFileSetting.Write("AuthenticationResultsEnabled", "1");
            IniFileSetting.Write("AuthenticationResultsIdentity", ConfiguredIdentity);
            _application.Reinitialize();

            EnableSpfEvaluation();

            string forgedMessage =
               "Authentication-Results: \"" + ConfiguredIdentity + "\"; dkim=pass header.d=attacker.example\r\n" +
               "From: test@example.com\r\n" +
               "To: forged-quoted@example.test\r\n" +
               "Subject: Quoted authserv-id\r\n" +
               "\r\n" +
               "Body text\r\n";

            var messageData = SendRawInboundMessageAndFetchIt("forged-quoted@example.test", forgedMessage);

            Assert.AreEqual(1, CountAuthenticationResultsHeaders(messageData),
               "A quoted authserv-id is still our identity and must be stripped.\r\n" + messageData);
            Assert.IsFalse(messageData.ToLower().Contains("attacker.example"),
               "No part of the forgery may survive.\r\n" + messageData);
         }
         finally
         {
            RestoreIniDefaults();
         }
      }

      [Test]
      [Description("The strip path rewrites the message by seeking past the header, so the header length it " +
                   "uses must be a RAW BYTE count. It was taken from PersistentMessage::LoadHeader, whose " +
                   "return value has been through a code-page round trip - so with any non-ASCII header byte " +
                   "the seek landed in the wrong place and spliced the last header line onto the body, or " +
                   "duplicated header bytes ahead of it, on a message the sender had already been told 250 for. " +
                   "This asserts the body and the other headers survive the strip intact.")]
      public void StrippingAForgedHeaderMustNotCorruptANonAsciiHeader()
      {
         try
         {
            IniFileSetting.Write("AuthenticationResultsEnabled", "1");
            IniFileSetting.Write("AuthenticationResultsIdentity", ConfiguredIdentity);
            _application.Reinitialize();

            EnableSpfEvaluation();

            // A non-ASCII byte in a kept header, on the strip path. The subject is raw
            // 8-bit rather than encoded-word on purpose: it is the byte count that the
            // defect got wrong, and an encoded-word would be pure ASCII and prove nothing.
            const string bodyMarker = "BodyMustSurviveIntact";
            string forgedMessage =
               "Authentication-Results: " + ConfiguredIdentity + "; dkim=pass header.d=attacker.example\r\n" +
               "From: test@example.com\r\n" +
               "To: forged-nonascii@example.test\r\n" +
               "Subject: café naïve über\r\n" +
               "X-Keep-Me: intact\r\n" +
               "\r\n" +
               bodyMarker + "\r\n";

            var messageData = SendRawInboundMessageAndFetchIt("forged-nonascii@example.test", forgedMessage);

            Assert.AreEqual(1, CountAuthenticationResultsHeaders(messageData),
               "The forgery must still be removed.\r\n" + messageData);
            Assert.IsTrue(messageData.Contains(bodyMarker),
               "The body must survive the strip. A mis-measured header length truncates or duplicates at the " +
               "header/body boundary.\r\n" + messageData);
            Assert.IsTrue(messageData.Contains("X-Keep-Me: intact"),
               "A header after the stripped one must survive byte for byte.\r\n" + messageData);
            Assert.IsFalse(messageData.Contains("\r\r\n"),
               "A mis-measured header length leaves \\r\\r\\n where the header/body boundary should be.\r\n" + messageData);
         }
         finally
         {
            RestoreIniDefaults();
         }
      }

      [Test]
      [Description("Only a header bearing OUR authserv-id may be stripped; one naming any other identity is" +
                   "left completely intact. If this regresses, the verdicts of legitimate upstream verifiers " +
                   "- a forwarding MX, a mailing list - are destroyed in transit, every downstream consumer " +
                   "of those verdicts goes blind, and any DKIM signature covering those header bytes breaks.")]
      public void HeaderBearingAnotherAuthservId_IsLeftCompletelyIntact()
      {
         try
         {
            IniFileSetting.Write("AuthenticationResultsEnabled", "1");
            IniFileSetting.Write("AuthenticationResultsIdentity", ConfiguredIdentity);
            _application.Reinitialize();

            EnableSpfEvaluation();

            const string foreignHeader =
               "Authentication-Results: verifier.upstream.example; dkim=pass header.d=thirdparty.example";

            string message =
               foreignHeader + "\r\n" +
               "From: test@example.com\r\n" +
               "To: foreign-id@example.test\r\n" +
               "Subject: Upstream Authentication-Results\r\n" +
               "\r\n" +
               "Body text\r\n";

            var messageData = SendRawInboundMessageAndFetchIt("foreign-id@example.test", message);

            Assert.IsTrue(messageData.Contains(foreignHeader),
               "The upstream verifier's header must survive byte for byte.\r\n" + messageData);

            Assert.AreEqual(2, CountAuthenticationResultsHeaders(messageData),
               "Expected two Authentication-Results headers: the upstream verifier's and ours.\r\n" + messageData);

            Assert.AreEqual(ConfiguredIdentity, FirstAuthenticationResultsToken(messageData),
               "Ours is prepended above the existing header block, so the first field is ours.\r\n" + messageData);
         }
         finally
         {
            RestoreIniDefaults();
         }
      }

      [Test]
      [Description("With ReceivedSpfHeaderEnabled=1 and the SPF check having run, the delivered message " +
                   "carries an RFC 7208 section 9.1 Received-SPF header - independently of " +
                   "AuthenticationResultsEnabled. If this regresses, tooling that reads Received-SPF " +
                   "(SpamAssassin rules, header-based diagnostics) loses the SPF verdict, or the two " +
                   "switches become coupled so that enabling one silently drags in the other.")]
      public void WhenReceivedSpfEnabled_AndSpfRan_ReceivedSpfHeaderIsWritten()
      {
         try
         {
            IniFileSetting.Write("ReceivedSpfHeaderEnabled", "1");
            _application.Reinitialize();

            EnableSpfEvaluation();

            var messageData = SendInboundMessageAndFetchIt("received-spf-on@example.test");

            Assert.IsTrue(messageData.ToLower().Contains("received-spf:"),
               "Expected a Received-SPF header once ReceivedSpfHeaderEnabled=1 and SPF ran.\r\n" + messageData);

            // Structural key-value pairs of RFC 7208 section 9.1, chosen because they
            // do not depend on which verdict SPF reached.
            Assert.IsTrue(messageData.ToLower().Contains("client-ip="),
               "The Received-SPF header should record the client IP the verdict was reached for.\r\n" + messageData);
            Assert.IsTrue(messageData.ToLower().Contains("envelope-from=<test@example.com>"),
               "The Received-SPF header should record the envelope sender SPF was evaluated against.\r\n" + messageData);

            Assert.IsFalse(messageData.ToLower().Contains("authentication-results:"),
               "AuthenticationResultsEnabled is still 0; the two switches must stay independent.\r\n" + messageData);
         }
         finally
         {
            RestoreIniDefaults();
         }
      }

      [Test]
      [Description("Received-SPF states a verdict SPF actually produced, so with the SPF check disabled no " +
                   "header appears even though ReceivedSpfHeaderEnabled=1. If this regresses, the server " +
                   "invents an SPF verdict with no evaluation behind it, and a downstream consumer treats a " +
                   "check that never ran as an authoritative result.")]
      public void WhenReceivedSpfEnabled_ButSpfDidNotRun_NoReceivedSpfHeaderIsWritten()
      {
         try
         {
            IniFileSetting.Write("ReceivedSpfHeaderEnabled", "1");
            _application.Reinitialize();

            // Deliberately NOT enabling the SPF check: the per-test setup has turned
            // every anti-spam check off, so no SPF verdict exists for this message.

            var messageData = SendInboundMessageAndFetchIt("received-spf-nospf@example.test");

            Assert.IsFalse(messageData.ToLower().Contains("received-spf:"),
               "No Received-SPF header may be written for a message on which the SPF check never ran.\r\n" + messageData);
         }
         finally
         {
            RestoreIniDefaults();
         }
      }
   }
}
