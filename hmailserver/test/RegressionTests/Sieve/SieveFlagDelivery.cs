// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System.Reflection;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.Sieve
{
   /// <summary>
   ///    Asserts that a flag an imap4flags script sets is actually ON THE STORED
   ///    MESSAGE, read back over IMAP.
   ///
   ///    This fixture exists because of how the defect it covers survived. The
   ///    evaluator was tested, thoroughly, through the ';'-joined action summary -
   ///    `SieveExtensionActions` has ten tests over exactly that - and the summary was
   ///    correct: a script saying `addflag "\Seen"` produced `keep;flags:\Seen`. What
   ///    nobody asserted was that anything read it. `LocalDelivery` consumed
   ///    `redirects`, `vacation`, `fileInto` and `keepLocal` from the structured
   ///    result and never touched `flags`, so the feature parsed, evaluated, reported
   ///    success, was advertised in the README - and changed nothing about the
   ///    message. Fixed on 15 August 2026.
   ///
   ///    The lesson is in where the assertions point, not in how many there are. A
   ///    test that asks the evaluator what it decided cannot tell you whether delivery
   ///    obeyed it; only the stored message can. So every assertion below goes through
   ///    a real SMTP delivery and a real IMAP FETCH.
   ///
   ///    <see cref="AScriptThatSetsNoFlagsLeavesTheMessageUnseen"/> is the negative
   ///    control and is the reason the rest mean anything: without it, code that
   ///    marked every delivered message \Seen would pass this fixture completely.
   ///
   ///    MEASURED, not assumed. On 15 August 2026 the fix was temporarily disabled and
   ///    this fixture re-run against the resulting binary: four of the six failed. The
   ///    two that passed are the two that assert a flag is ABSENT - the control above,
   ///    and RemoveflagIsObeyedRatherThanTreatedAsSilence - which is worth stating
   ///    plainly, because it means that second test does NOT discriminate between
   ///    working and broken flag delivery on its own. It earns its place by pinning the
   ///    flagsGiven-versus-empty distinction, not by catching this regression, and
   ///    anyone relying on it to catch a future one should know that.
   /// </summary>
   [TestFixture]
   public class SieveFlagDelivery : TestFixtureBase
   {
      private const string Password = "secret";

      private static void SetScript(Account account, string script)
      {
         // Late-bound for the same reason the sibling vacation fixture is: the test
         // then does not depend on the registered type library having been
         // regenerated after an IDL change.
         account.GetType().InvokeMember(
            "SieveScript", BindingFlags.SetProperty, null, account, new object[] { script });
      }

      /// <summary>
      ///    The flags IMAP reports for the first message in INBOX.
      ///
      ///    Read with a raw FETCH rather than through COM, because the question is
      ///    what a mail CLIENT sees. A COM property could agree with the database and
      ///    still disagree with what Thunderbird is told, and the flag is only worth
      ///    anything if it reaches the client.
      /// </summary>
      private static string FetchFlags(Account account)
      {
         var client = new ImapClientSimulator();

         try
         {
            Assert.IsTrue(client.ConnectAndLogon(account.Address, Password), "IMAP logon failed.");
            Assert.IsTrue(client.SelectFolder("INBOX"), "SELECT INBOX failed.");

            return client.SendSingleCommand("A10 FETCH 1 (FLAGS)");
         }
         finally
         {
            client.Disconnect();
         }
      }

      /// <summary>
      ///    Sends one message to an account carrying the given script, waits for it to
      ///    land, and returns the FETCH FLAGS response for it.
      /// </summary>
      private string DeliverUnder(string script, out Account recipient)
      {
         recipient = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-flags@example.test", Password);
         Account sender = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-flags-sender@example.test", Password);

         SetScript(recipient, script);

         SmtpClientSimulator.StaticSend(sender.Address, recipient.Address, "Quarterly numbers", "Body text.");

         // Drains the delivery queue before believing the count, so the FETCH below
         // cannot race the delivery it is asking about.
         IMAPFolder inbox = recipient.IMAPFolders.get_ItemByName("INBOX");
         CustomAsserts.AssertFolderMessageCount(inbox, 1);

         return FetchFlags(recipient);
      }

      /// <summary>
      ///    The plain case, and the one that failed silently before the fix: a script
      ///    that adds \Seen produces a message the client is told is seen.
      /// </summary>
      [Test]
      [Description("addflag \"\\Seen\" reaches the stored message, so IMAP reports the message as seen.")]
      public void AddflagSeenReachesTheStoredMessage()
      {
         string flags = DeliverUnder(
            "require [\"imap4flags\"];\r\n" +
            "addflag \"\\\\Seen\";\r\n" +
            "keep;", out Account _);

         StringAssert.Contains("\\Seen", flags,
            "The script added \\Seen and the delivered message does not carry it. Before 15 August 2026 the "
            + "evaluator decided this correctly and delivery discarded the decision. FETCH said: " + flags);
      }

      /// <summary>
      ///    The negative control. Every other test here would also pass against an
      ///    implementation that marked all delivered mail \Seen - a change that would
      ///    silently mark every user's whole inbox read. This is what rules that out.
      /// </summary>
      [Test]
      [Description("A script that sets no flags leaves the message unseen - the control that makes the others mean something.")]
      public void AScriptThatSetsNoFlagsLeavesTheMessageUnseen()
      {
         string flags = DeliverUnder(
            "require [\"fileinto\"];\r\n" +
            "keep;", out Account _);

         StringAssert.DoesNotContain("\\Seen", flags,
            "A message delivered under a script that never mentions flags came back seen, so flags are being "
            + "applied that no script asked for. FETCH said: " + flags);
      }

      /// <summary>
      ///    setflag replaces the flag set outright, where addflag adds to it. Both end
      ///    in the same place for a newly delivered message, and both have to work,
      ///    because a script that files and flags is the commonest real use of this
      ///    extension.
      /// </summary>
      [Test]
      [Description("setflag \"\\Flagged\" reaches the stored message.")]
      public void SetflagFlaggedReachesTheStoredMessage()
      {
         string flags = DeliverUnder(
            "require [\"imap4flags\"];\r\n" +
            "setflag \"\\\\Flagged\";\r\n" +
            "keep;", out Account _);

         StringAssert.Contains("\\Flagged", flags, "FETCH said: " + flags);
         StringAssert.DoesNotContain("\\Seen", flags,
            "setflag names the whole flag set, so a flag it did not name must not appear. FETCH said: " + flags);
      }

      /// <summary>
      ///    The :flags tag on the storing action is the other half of RFC 5232, and it
      ///    takes precedence over the internal flag variable. It reaches delivery by
      ///    the same route, so it is asserted the same way.
      /// </summary>
      [Test]
      [Description("A :flags tag on fileinto reaches the filed copy.")]
      public void TheFlagsTagOnFileintoReachesTheFiledCopy()
      {
         string flags = DeliverUnder(
            "require [\"imap4flags\", \"fileinto\"];\r\n" +
            "fileinto :flags \"\\\\Flagged\" \"INBOX\";", out Account _);

         StringAssert.Contains("\\Flagged", flags, "FETCH said: " + flags);
      }

      /// <summary>
      ///    removeflag has to be obeyed as an instruction, not read as "no flags were
      ///    mentioned".
      ///
      ///    The distinction is real in the code: SieveResult carries flagsGiven
      ///    separately from flags, so a script that set \Seen and then removed it
      ///    arrives at delivery as "flags were decided, and the answer is none" -
      ///    which must produce an unseen message rather than being skipped as though
      ///    the script had said nothing.
      /// </summary>
      [Test]
      [Description("addflag followed by removeflag leaves the message without the flag, rather than with it.")]
      public void RemoveflagIsObeyedRatherThanTreatedAsSilence()
      {
         string flags = DeliverUnder(
            "require [\"imap4flags\"];\r\n" +
            "addflag \"\\\\Seen\";\r\n" +
            "removeflag \"\\\\Seen\";\r\n" +
            "keep;", out Account _);

         StringAssert.DoesNotContain("\\Seen", flags, "FETCH said: " + flags);
      }

      /// <summary>
      ///    A keyword this server cannot store must not take the storable flags down
      ///    with it, and must not be dropped in silence.
      ///
      ///    messageflags is a fixed 8-bit bitmask and SELECT advertises PERMANENTFLAGS
      ///    without \*, so there is nowhere to put a keyword and no way for a client to
      ///    see one. The honest behaviour is to apply what can be applied and say what
      ///    could not - an administrator whose "file it and tag it" rule only files
      ///    needs to be told which half worked, and the alternative is a rule that
      ///    half-works forever with no trace anywhere.
      /// </summary>
      [Test]
      [Description("A keyword flag is reported in the log and does not stop the storable flags being applied.")]
      public void AnUnstorableKeywordIsReportedAndDoesNotBlockTheRest()
      {
         LogHandler.DeleteCurrentDefaultLog();

         string flags = DeliverUnder(
            "require [\"imap4flags\"];\r\n" +
            "addflag \"\\\\Seen $label1\";\r\n" +
            "keep;", out Account _);

         StringAssert.Contains("\\Seen", flags,
            "A keyword the server cannot store stopped the flags it can store from being applied. FETCH said: "
            + flags);

         string log = LogHandler.ReadCurrentDefaultLog();

         StringAssert.Contains("$label1", log,
            "The keyword was discarded without a word. It cannot be stored, which is a fact about this server "
            + "rather than a fault in the script - but discarding half of what a script asked for silently is "
            + "the failure this whole fixture exists to prevent. Log was:\r\n" + log);
      }
   }
}
