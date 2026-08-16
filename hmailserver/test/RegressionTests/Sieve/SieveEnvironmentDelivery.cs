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
   ///    The "environment" (RFC 5183) and "ihave" (RFC 5463) tests, asserted
   ///    through real deliveries - every assertion is on where a message was FILED.
   ///
   ///    ihave's dangerous half is not the test, it is the grant: a script that
   ///    says `if ihave "fileinto"` must then be allowed to USE fileinto inside
   ///    that block without a require line, or the test reports an extension
   ///    available that the script cannot reach - a trap dressed as a feature. The
   ///    scripts here deliberately omit `require "fileinto"` where an ihave guard
   ///    covers it, so the grant is what these tests exercise at upload.
   ///
   ///    environment's honest edge is the unknown item: this server cannot answer
   ///    "remote-host" (the sending client's identity does not reach the
   ///    evaluator), and RFC 5183 wants an unanswerable item to match NOTHING -
   ///    not even the empty key, which is how scripts probe whether an item is
   ///    known at all.
   /// </summary>
   [TestFixture]
   public class SieveEnvironmentDelivery : TestFixtureBase
   {
      private const string Password = "secret";
      private const string TargetFolder = "Matched";

      private static void SetScript(Account account, string script)
      {
         account.GetType().InvokeMember(
            "SieveScript", BindingFlags.SetProperty, null, account, new object[] { script });
      }

      private bool FilesIntoMatchedFolder(string script)
      {
         Account recipient = SingletonProvider<TestSetup>.Instance.AddAccount(
            _domain, "sieve-env@example.test", Password);

         recipient.IMAPFolders.Add(TargetFolder);

         SetScript(recipient, script);

         SmtpClientSimulator.StaticSend(
            "sieve-env-sender@example.test", recipient.Address, "Environment test", "Body text.");

         CustomAsserts.AssertRecipientsInDeliveryQueue(0);

         IMAPFolder inbox = recipient.IMAPFolders.get_ItemByName("INBOX");
         IMAPFolder matched = recipient.IMAPFolders.get_ItemByName(TargetFolder);

         int inInbox = inbox.Messages.Count;
         int inMatched = matched.Messages.Count;

         Assert.AreEqual(1, inInbox + inMatched,
            "Exactly one copy of the message should exist. INBOX has " + inInbox + " and " +
            TargetFolder + " has " + inMatched + ".");

         return inMatched == 1;
      }

      [Test]
      [Description("environment \"name\" answers hMailServer, case-folded under the default comparator.")]
      public void TheNameItemAnswersHmailserver()
      {
         Assert.IsTrue(
            FilesIntoMatchedFolder(
               "require [\"environment\", \"fileinto\"];\r\n" +
               "if environment :contains \"name\" \"mailserver\" {\r\n" +
               "  fileinto \"" + TargetFolder + "\";\r\n" +
               "}\r\n"),
            "'environment \"name\"' did not answer with this server's name.");
      }

      /// <summary>
      ///    The negative control for environment: a wrong expected value must not
      ///    match, or every test above is meaningless.
      /// </summary>
      [Test]
      [Description("A wrong expected value does not match - the control the rest depend on.")]
      public void AWrongNameValueDoesNotMatch()
      {
         Assert.IsFalse(
            FilesIntoMatchedFolder(
               "require [\"environment\", \"fileinto\"];\r\n" +
               "if environment :is \"name\" \"Postfix\" {\r\n" +
               "  fileinto \"" + TargetFolder + "\";\r\n" +
               "}\r\n"),
            "'environment \"name\"' matched a name that is not this server's.");
      }

      /// <summary>
      ///    RFC 5183: an item the server cannot answer matches nothing - including
      ///    the empty key, which is the probe for "is this item known". remote-host
      ///    is honestly unanswerable here: the sending client's identity does not
      ///    reach the evaluator.
      /// </summary>
      [Test]
      [Description("An unanswerable item (remote-host) matches nothing, not even the empty key.")]
      public void AnUnknownItemMatchesNothing()
      {
         Assert.IsFalse(
            FilesIntoMatchedFolder(
               "require [\"environment\", \"fileinto\"];\r\n" +
               "if environment :contains \"remote-host\" \"\" {\r\n" +
               "  fileinto \"" + TargetFolder + "\";\r\n" +
               "}\r\n"),
            "An item this server cannot answer matched the empty key; unknown items must contribute no value.");
      }

      [Test]
      [Description("Scripts run during delivery, so the phase item answers \"during\".")]
      public void ThePhaseItemAnswersDuring()
      {
         Assert.IsTrue(
            FilesIntoMatchedFolder(
               "require [\"environment\", \"fileinto\"];\r\n" +
               "if environment :is \"phase\" \"during\" {\r\n" +
               "  fileinto \"" + TargetFolder + "\";\r\n" +
               "}\r\n"),
            "'environment \"phase\"' did not answer \"during\" for a delivery-time evaluation.");
      }

      /// <summary>
      ///    The grant, end to end: no `require "body"` anywhere - the ihave guard is
      ///    what makes the body test usable in its block, at upload AND at delivery.
      ///    If the grant breaks, this script is refused at upload and the message
      ///    never files. The granted feature is a require-gated TEST deliberately:
      ///    implemented commands such as fileinto have always been accepted
      ///    leniently without their require, so they cannot exercise the grant.
      /// </summary>
      [Test]
      [Description("An ihave-guarded block may use the extension it tested for, without its require.")]
      public void IhaveGrantsTheTestedExtensionToItsBlock()
      {
         Assert.IsTrue(
            FilesIntoMatchedFolder(
               "require [\"ihave\", \"fileinto\"];\r\n" +
               "if ihave \"body\" {\r\n" +
               "  if body :contains \"Body text\" {\r\n" +
               "    fileinto \"" + TargetFolder + "\";\r\n" +
               "  }\r\n" +
               "}\r\n"),
            "'if ihave \"body\"' did not let its block use the body test - either the grant was refused " +
            "at upload or ihave answered false for an extension this server has.");
      }

      /// <summary>
      ///    The other half of ihave's honesty: an extension this server does NOT
      ///    have answers false, so the guarded block does not run. The require line
      ///    carries fileinto here because the ihave guard names something else.
      /// </summary>
      [Test]
      [Description("ihave on an extension this server lacks is false, and the guarded block does not run.")]
      public void IhaveOnAMissingExtensionIsFalse()
      {
         Assert.IsFalse(
            FilesIntoMatchedFolder(
               "require [\"ihave\", \"fileinto\"];\r\n" +
               "if ihave \"mboxmetadata\" {\r\n" +
               "  fileinto \"" + TargetFolder + "\";\r\n" +
               "}\r\n"),
            "'ihave' reported an extension this server does not implement, which turns every " +
            "ihave-guarded fallback in the wild into the wrong branch.");
      }
   }
}
