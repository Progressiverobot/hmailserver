// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

using System.Reflection;
using System.Text;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.Sieve
{
   /// <summary>
   ///    The spamtest test (RFC 3685, with RFC 5235's :percent), asserted through
   ///    real deliveries whose spam verdict comes from the REAL antispam pipeline -
   ///    the SURBL permanent test point, the same trigger AntiSpam.Basics uses -
   ///    never from a header the test writes itself.
   ///
   ///    That last point is the fixture's reason for being shaped this way: a
   ///    sender can write "X-hMailServer-Spam: YES" into their own message, and a
   ///    spamtest that trusted it would let senders steer recipients' filters.
   ///    The value flows ONLY from the message's spam FLAG, set by the server's
   ///    own classification and passed into the evaluator by the delivery path -
   ///    so it works with every verdict-header option switched off, and a forged
   ///    header works in neither direction. That grounding also fixes the
   ///    granularity: the pipeline persists no score for unclassified mail, so
   ///    the honest values are 0 (no verdict) and 10 (classified), nothing between.
   /// </summary>
   [TestFixture]
   public class SieveSpamtestDelivery : TestFixtureBase
   {
      private const string Password = "secret";
      private const string TargetFolder = "Junk";

      private static int accountSequence_;

      private static void SetScript(Account account, string script)
      {
         account.GetType().InvokeMember(
            "SieveScript", BindingFlags.SetProperty, null, account, new object[] { script });
      }

      /// <summary>
      ///    Delivers one message - spam-classified via SURBL or clean - under the
      ///    given script, and answers whether it was filed into Junk.
      /// </summary>
      private bool FilesIntoJunk(string script, bool makeItSpam)
      {
         accountSequence_++;
         Account recipient = SingletonProvider<TestSetup>.Instance.AddAccount(
            _domain, "sieve-spamtest-" + accountSequence_ + "@example.test", Password);

         recipient.IMAPFolders.Add(TargetFolder);
         SetScript(recipient, script);

         hMailServer.AntiSpam antiSpam = _settings.AntiSpam;

         antiSpam.SpamMarkThreshold = 1;
         antiSpam.SpamDeleteThreshold = 100;

         // Deliberately NO verdict headers: the value must flow from the message's
         // spam flag through the delivery path, or this extension only works on
         // installations that happen to write diagnostic headers.
         antiSpam.AddHeaderSpam = false;
         antiSpam.AddHeaderReason = false;
         antiSpam.PrependSubject = false;

         SURBLServer surblServer = antiSpam.SURBLServers[0];
         surblServer.Active = makeItSpam;
         surblServer.Score = 5;
         surblServer.Save();

         try
         {
            string body = makeItSpam
               ? "A SURBL url: -> http://surbl-org-permanent-test-point.com/ <-\r\n"
               : "An ordinary message with no interesting links.\r\n";

            SmtpClientSimulator.StaticSend(
               "sieve-spamtest-sender@example.test", recipient.Address, "Spamtest", body);

            CustomAsserts.AssertRecipientsInDeliveryQueue(0);

            IMAPFolder inbox = recipient.IMAPFolders.get_ItemByName("INBOX");
            IMAPFolder junk = recipient.IMAPFolders.get_ItemByName(TargetFolder);

            int inInbox = inbox.Messages.Count;
            int inJunk = junk.Messages.Count;

            Assert.AreEqual(1, inInbox + inJunk,
               "Exactly one copy of the message should exist. INBOX has " + inInbox + " and " +
               TargetFolder + " has " + inJunk + ".");

            return inJunk == 1;
         }
         finally
         {
            surblServer.Active = false;
            surblServer.Save();
         }
      }

      private static string FileIntoJunkIf(string test)
      {
         return "require [\"spamtest\", \"spamtestplus\", \"fileinto\", \"relational\", \"comparator-i;ascii-numeric\"];\r\n" +
                "if " + test + " {\r\n" +
                "  fileinto \"" + TargetFolder + "\";\r\n" +
                "}\r\n";
      }

      [Test]
      [Description("A message the server itself classified as spam reports spamtest 10, with every verdict header off.")]
      public void AClassifiedMessageReportsTen()
      {
         Assert.IsTrue(
            FilesIntoJunk(FileIntoJunkIf("spamtest :value \"ge\" :comparator \"i;ascii-numeric\" \"10\""), makeItSpam: true),
            "The server classified this message as spam and spamtest did not say 10 - the flag is not " +
            "reaching the evaluator through the delivery path.");
      }

      /// <summary>
      ///    The negative control: a clean message must not report a spam verdict.
      ///    With no verdict recorded, the honest RFC answer is "0" - so the ge-10
      ///    filter must leave it in INBOX.
      /// </summary>
      [Test]
      [Description("A clean message does not report a verdict, so the filter leaves it in INBOX.")]
      public void ACleanMessageStaysInInbox()
      {
         Assert.IsFalse(
            FilesIntoJunk(FileIntoJunkIf("spamtest :value \"ge\" :comparator \"i;ascii-numeric\" \"10\""), makeItSpam: false),
            "A message nothing classified was filed as spam - spamtest is reporting a verdict that " +
            "does not exist.");
      }

      [Test]
      [Description("A message with no verdict matches spamtest \"0\" - the RFC's spelling of \"no information\".")]
      public void AnUntestedMessageReportsZero()
      {
         Assert.IsTrue(
            FilesIntoJunk(FileIntoJunkIf("spamtest :is \"0\""), makeItSpam: false),
            "A message with no recorded verdict did not report \"0\".");
      }

      [Test]
      [Description("Under :percent (RFC 5235) the classified message reports 100.")]
      public void PercentReportsOneHundredForClassifiedMail()
      {
         Assert.IsTrue(
            FilesIntoJunk(FileIntoJunkIf("spamtest :percent :value \"ge\" :comparator \"i;ascii-numeric\" \"100\""), makeItSpam: true),
            "':percent' did not report 100 for a message the server classified as spam.");
      }

      /// <summary>
      ///    The spoofing control, and the reason spamtest reads the delivery-path
      ///    FLAG rather than the verdict headers: the sender writes the server's
      ///    own header convention into their message, nothing classifies it, and
      ///    the filter must not fire on the sender's say-so. An implementation
      ///    that read X-hMailServer-Spam or X-hMailServer-Reason-Score from the
      ///    message would pass every other test in this fixture and fail only
      ///    this one - in the direction that lets senders steer recipients'
      ///    filters.
      /// </summary>
      [Test]
      [Description("A sender-written verdict header does not file the message - the spoofing control.")]
      public void ASenderWrittenVerdictHeaderDoesNotCount()
      {
         accountSequence_++;
         Account recipient = SingletonProvider<TestSetup>.Instance.AddAccount(
            _domain, "sieve-spamtest-" + accountSequence_ + "@example.test", Password);

         recipient.IMAPFolders.Add(TargetFolder);
         SetScript(recipient, FileIntoJunkIf("spamtest :value \"ge\" :comparator \"i;ascii-numeric\" \"1\""));

         string raw =
            "From: sieve-spamtest-sender@example.test\r\n" +
            "To: " + recipient.Address + "\r\n" +
            "Subject: Spoof attempt\r\n" +
            "X-hMailServer-Spam: YES\r\n" +
            "X-hMailServer-Reason-Score: 999\r\n" +
            "\r\n" +
            "Nothing classified this message.\r\n";

         var smtp = new SmtpClientSimulator();
         smtp.SendRaw("sieve-spamtest-sender@example.test", recipient.Address, raw);

         CustomAsserts.AssertRecipientsInDeliveryQueue(0);

         IMAPFolder inbox = recipient.IMAPFolders.get_ItemByName("INBOX");
         IMAPFolder junk = recipient.IMAPFolders.get_ItemByName(TargetFolder);

         Assert.AreEqual(1, inbox.Messages.Count + junk.Messages.Count, "Exactly one copy should exist.");
         Assert.AreEqual(0, junk.Messages.Count,
            "A message carrying sender-written verdict headers was filed as spam although nothing " +
            "classified it - spamtest is trusting headers a sender controls.");
      }
   }
}
