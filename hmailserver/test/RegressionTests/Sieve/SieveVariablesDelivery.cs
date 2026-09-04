// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Reflection;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.Sieve
{
   /// <summary>
   ///    variables (RFC 5229): set, ${} expansion, match variables from :matches,
   ///    modifiers, and the string test - asserted through real deliveries.
   ///
   ///    The decisive test files into a folder NAMED BY a capture from the
   ///    subject: that only works when the wildcard match records what it
   ///    consumed, the record survives to the action, and the action's argument is
   ///    expanded at evaluation. Any one of the three silently broken shows up as
   ///    the message in the wrong place.
   ///
   ///    The scoping control matters as much: WITHOUT require "variables", the
   ///    text "${a}" has no special meaning (RFC 5229 3) and a folder may
   ///    legitimately be called that - so a fileinto naming it must file into the
   ///    literal folder, not into an expansion.
   /// </summary>
   [TestFixture]
   public class SieveVariablesDelivery : TestFixtureBase
   {
      private const string Password = "secret";

      private static int accountSequence_;

      // Unique per account created by this fixture, however the tests are ordered or parallelised.
      private static int NextAccountSequence()
      {
         return Interlocked.Increment(ref accountSequence_);
      }

      private static void SetScript(Account account, string script)
      {
         account.GetType().InvokeMember(
            "SieveScript", BindingFlags.SetProperty, null, account, new object[] { script });
      }

      private Account NewRecipient(string script, params string[] folders)
      {
         int sequence = NextAccountSequence();
         Account recipient = SingletonProvider<TestSetup>.Instance.AddAccount(
            _domain, "sieve-vars-" + sequence + "@example.test", Password);

         foreach (string folder in folders)
            recipient.IMAPFolders.Add(folder);

         SetScript(recipient, script);
         return recipient;
      }

      private void Send(Account recipient, string subject)
      {
         SmtpClientSimulator.StaticSend("sieve-vars-sender@example.test", recipient.Address, subject, "Body text.");
         CustomAsserts.AssertRecipientsInDeliveryQueue(0);
      }

      private void AssertFolderCount(Account recipient, string folder, int expected)
      {
         IMAPFolder imapFolder = recipient.IMAPFolders.get_ItemByName(folder);
         CustomAsserts.AssertFolderMessageCount(imapFolder, expected);
      }

      [Test]
      [Description("A set variable expands inside a fileinto mailbox name.")]
      public void ASetVariableExpandsInFileinto()
      {
         Account recipient = NewRecipient(
            "require [\"variables\", \"fileinto\"];\r\n" +
            "set \"target\" \"Projects\";\r\n" +
            "fileinto \"${target}\";\r\n",
            "Projects");

         Send(recipient, "Anything at all");

         AssertFolderCount(recipient, "Projects", 1);
         AssertFolderCount(recipient, "INBOX", 0);
      }

      /// <summary>
      ///    The decisive test: the folder is named by what the wildcard consumed.
      ///    Subject "[team-alpha] weekly notes" files into "team-alpha".
      /// </summary>
      [Test]
      [Description("A :matches capture names the fileinto folder: ${1} is what the wildcard consumed.")]
      public void AMatchCaptureNamesTheFolder()
      {
         Account recipient = NewRecipient(
            "require [\"variables\", \"fileinto\"];\r\n" +
            "if header :matches \"Subject\" \"[*]*\" {\r\n" +
            "  fileinto \"${1}\";\r\n" +
            "}\r\n",
            "team-alpha");

         Send(recipient, "[team-alpha] weekly notes");

         AssertFolderCount(recipient, "team-alpha", 1);
         AssertFolderCount(recipient, "INBOX", 0);
      }

      /// <summary>
      ///    The scoping control (RFC 5229 3): without require "variables", "${a}"
      ///    is four ordinary characters and a folder can be called that. An
      ///    implementation that expands unconditionally passes every other test
      ///    here and fails this one - by filing into the wrong folder.
      /// </summary>
      [Test]
      [Description("Without require \"variables\", ${...} stays verbatim - the scoping control.")]
      public void WithoutTheRequireDollarBraceStaysVerbatim()
      {
         Account recipient = NewRecipient(
            "require \"fileinto\";\r\n" +
            "fileinto \"${a}\";\r\n",
            "${a}");

         Send(recipient, "Anything at all");

         AssertFolderCount(recipient, "${a}", 1);
         AssertFolderCount(recipient, "INBOX", 0);
      }

      [Test]
      [Description("An unset variable expands to the empty string, so the folder name collapses and delivery falls back to INBOX.")]
      public void AnUnsetVariableExpandsToEmpty()
      {
         // "Pre${unset}fix" collapses to "Prefix" - proven by the message landing
         // in the folder of the COLLAPSED name.
         Account recipient = NewRecipient(
            "require [\"variables\", \"fileinto\"];\r\n" +
            "fileinto \"Pre${unset}fix\";\r\n",
            "Prefix");

         Send(recipient, "Anything at all");

         AssertFolderCount(recipient, "Prefix", 1);
      }

      [Test]
      [Description("The string test compares expanded sources against keys.")]
      public void TheStringTestComparesExpandedValues()
      {
         Account recipient = NewRecipient(
            "require [\"variables\", \"fileinto\"];\r\n" +
            "set \"state\" \"urgent\";\r\n" +
            "if string :is \"${state}\" \"urgent\" {\r\n" +
            "  fileinto \"Urgent\";\r\n" +
            "}\r\n",
            "Urgent");

         Send(recipient, "Anything at all");

         AssertFolderCount(recipient, "Urgent", 1);
      }

      [Test]
      [Description("Modifiers apply in precedence order: :length of an :upper'd value is the length of the value.")]
      public void ModifiersApplyInPrecedenceOrder()
      {
         // set :length :upper "n" "abcde" - case first (precedence 40), then
         // length (10) - leaves "5". The test files on the result.
         Account recipient = NewRecipient(
            "require [\"variables\", \"fileinto\"];\r\n" +
            "set :length :upper \"n\" \"abcde\";\r\n" +
            "if string :is \"${n}\" \"5\" {\r\n" +
            "  fileinto \"Five\";\r\n" +
            "}\r\n",
            "Five");

         Send(recipient, "Anything at all");

         AssertFolderCount(recipient, "Five", 1);
      }

      [Test]
      [Description(":quotewildcard makes a value match only literally when used as a pattern.")]
      public void QuotewildcardEscapesPatternCharacters()
      {
         // The value "a*b" quoted becomes "a\*b"; used as a pattern it must match
         // the literal text "a*b" and NOT "aXXb". Both outcomes are asserted via
         // where the two messages land.
         Account recipient = NewRecipient(
            "require [\"variables\", \"fileinto\"];\r\n" +
            "set :quotewildcard \"q\" \"a*b\";\r\n" +
            "if header :matches \"Subject\" \"${q}\" {\r\n" +
            "  fileinto \"Literal\";\r\n" +
            "}\r\n",
            "Literal");

         Send(recipient, "aXXb");
         AssertFolderCount(recipient, "INBOX", 1);
         AssertFolderCount(recipient, "Literal", 0);

         Send(recipient, "a*b");
         AssertFolderCount(recipient, "Literal", 1);
      }
   }
}
