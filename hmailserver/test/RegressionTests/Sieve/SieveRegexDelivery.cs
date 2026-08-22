// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Reflection;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.Sieve
{
   /// <summary>
   ///    The ":regex" match type (draft-ietf-sieve-regex), asserted through real
   ///    deliveries - every assertion is on where a message was FILED.
   ///
   ///    The evaluation runs under RuleGuard's budget-and-suspend breaker, shared
   ///    with the legacy rules engine's regex criterion, because a script author is
   ///    exactly as able to write a catastrophic pattern as a rule author and both
   ///    run on the delivery thread. The last test here is the one that earns the
   ///    breaker its keep: a pattern made catastrophic by a crafted subject must
   ///    cost a bounded evaluation and a suspension, never the message.
   /// </summary>
   [TestFixture]
   public class SieveRegexDelivery : TestFixtureBase
   {
      private const string Password = "secret";
      private const string TargetFolder = "Matched";

      private static void SetScript(Account account, string script)
      {
         account.GetType().InvokeMember(
            "SieveScript", BindingFlags.SetProperty, null, account, new object[] { script });
      }

      /// <summary>
      ///    Delivers one message with the given subject under a script that files
      ///    into "Matched" when the subject matches the given pattern, and answers
      ///    where it landed.
      /// </summary>
      private bool FilesIntoMatchedFolder(string pattern, string subject, string comparatorLine = "")
      {
         Account recipient = SingletonProvider<TestSetup>.Instance.AddAccount(
            _domain, "sieve-regex@example.test", Password);

         recipient.IMAPFolders.Add(TargetFolder);

         SetScript(recipient,
            "require [\"regex\", \"fileinto\"];\r\n" +
            "if header :regex " + comparatorLine + "\"Subject\" \"" + pattern + "\" {\r\n" +
            "  fileinto \"" + TargetFolder + "\";\r\n" +
            "}\r\n");

         SmtpClientSimulator.StaticSend(
            "sieve-regex-sender@example.test", recipient.Address, subject, "Body text.");

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
      [Description("A subject matching the pattern is filed; the pattern is a search, not a whole-value match.")]
      public void APatternMatchingPartOfTheSubjectMatches()
      {
         // No anchors: the pattern hits in the middle of the subject, which is what
         // distinguishes draft-sieve-regex's search from the rules engine's
         // whole-value regex_match.
         Assert.IsTrue(
            FilesIntoMatchedFolder("inv[o0]ice #[0-9]+", "Your inv0ice #4711 is attached"),
            "A pattern matching a substring of the subject did not match - ':regex' must search, not " +
            "require the whole value to match.");
      }

      /// <summary>
      ///    The negative control: everything else in this fixture would also pass
      ///    against an implementation that matched every message.
      /// </summary>
      [Test]
      [Description("A subject not matching the pattern stays in INBOX - the control the rest depend on.")]
      public void ANonMatchingSubjectStaysInInbox()
      {
         Assert.IsFalse(
            FilesIntoMatchedFolder("inv[o0]ice #[0-9]+", "Quarterly figures for review"),
            "A subject that does not match the pattern was filed as though it did.");
      }

      [Test]
      [Description("The default comparator folds case, so an upper-case pattern matches a lower-case subject.")]
      public void TheDefaultComparatorIsCaseInsensitive()
      {
         Assert.IsTrue(
            FilesIntoMatchedFolder("INVOICE", "your invoice is here"),
            "':regex' under the default comparator (i;ascii-casemap) must match case-insensitively.");
      }

      [Test]
      [Description("Under i;octet the same pattern is case-sensitive and does not match.")]
      public void UnderOctetTheMatchIsCaseSensitive()
      {
         Assert.IsFalse(
            FilesIntoMatchedFolder("INVOICE", "your invoice is here", ":comparator \"i;octet\" "),
            "':regex' under i;octet matched across case, so the comparator is not reaching the regex.");
      }

      /// <summary>
      ///    The breaker test. The pattern is the classic catastrophic-backtracking
      ///    shape and the subject is crafted to detonate it: without the budget this
      ///    evaluation runs for astronomical time on the delivery thread, which is a
      ///    lost message at best. With it, the evaluation is abandoned or timed out,
      ///    the pattern is suspended, the test is false - and the message ARRIVES,
      ///    in INBOX. That last clause is the entire point: a hostile sender must
      ///    never be able to turn a filter into mail loss.
      /// </summary>
      [Test]
      [Description("A catastrophic pattern against a crafted subject costs a bounded evaluation, never the message.")]
      public void ACatastrophicPatternDoesNotCostTheMessage()
      {
         // The pattern carries a per-run unique tail for two reasons. The breaker
         // suspends BY PATTERN TEXT for five minutes and reports only on the
         // suspension's insertion - so a repeated run inside that window would be
         // answered from the suspension table, report nothing, and fail the
         // assertion below for the wrong reason. And the tail keeps the pattern
         // catastrophic: (a+)+ must exhaust its partitions of the 'a' run before
         // concluding the tail cannot follow.
         string uniqueTail = System.Guid.NewGuid().ToString("N");
         string pattern = "(a+)+" + uniqueTail + "$";
         string craftedSubject = new string('a', 40) + "b";

         Assert.IsFalse(
            FilesIntoMatchedFolder(pattern, craftedSubject),
            "The catastrophic pattern reported a match, which the crafted subject makes impossible - " +
            "something upstream is answering true without evaluating.");

         // The breaker must have said what it did - either abandoned by Boost's
         // complexity guard or timed out against the budget, depending on where the
         // explosion is caught - and consuming the report here is what lets the
         // next test's precondition (no unexplained server errors) hold.
         CustomAsserts.AssertReportedError("A Sieve ':regex' match");
      }
   }
}
