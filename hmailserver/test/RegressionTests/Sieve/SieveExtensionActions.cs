// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System.Reflection;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.Sieve
{
   /// <summary>
   ///    Exercises the Sieve extensions the evaluator gained beyond RFC 5228's
   ///    core: imap4flags (RFC 5232), copy (RFC 3894), envelope (RFC 5228 5.4),
   ///    relational (RFC 5231) and subaddress (RFC 5233).
   ///
   ///    Everything is asserted through the ';'-joined action summary that
   ///    EvaluateSieveScript returns, because that summary is also what the
   ///    delivery path reads. Two properties of its shape matter enough to be
   ///    tested rather than assumed:
   ///
   ///    - the flag and vacation tokens are appended, never substituted, so a
   ///      reader that only understands keep/fileinto/discard/redirect still sees
   ///      the same delivery decision it always saw;
   ///    - a side-effect-only script (say, one that just sets a flag) must still
   ///      produce the implicit keep. Treating "the script did something" as "the
   ///      script decided where the message goes" would silently drop the mail,
   ///      which is why localDecided_ exists separately from the action list.
   ///
   ///    Invoked late-bound via IDispatch so the test does not depend on the
   ///    registered type library being regenerated.
   /// </summary>
   [TestFixture]
   public class SieveExtensionActions : TestFixtureBase
   {
      private string Evaluate(string script, string rawMessage)
      {
         object utilities = _application.Utilities;
         object result = utilities.GetType().InvokeMember(
            "EvaluateSieveScript",
            BindingFlags.InvokeMethod,
            null,
            utilities,
            new object[] { script, rawMessage });

         return (string)result;
      }

      // A message shaped like one that has just been accepted for local delivery:
      // Return-Path is written by LocalDelivery before the script runs, which is
      // what lets envelope "from" work. Two X-Tag headers give ":count" something
      // to count, and the plus-addressed To gives subaddress something to split.
      private const string Message =
         "Return-Path: <boss@example.test>\r\n" +
         "From: \"The Boss\" <boss@example.test>\r\n" +
         "To: user+news@example.test\r\n" +
         "Subject: Quarterly numbers\r\n" +
         "X-Priority: 5\r\n" +
         "X-Tag: one\r\n" +
         "X-Tag: two\r\n" +
         "\r\n" +
         "Body text.";

      [Test]
      [Description("setflag / addflag / removeflag produce a flag set for the local copy and never cancel the implicit keep.")]
      public void TestFlagCommands()
      {
         // A script whose only action is a flag change still keeps the message.
         // Before imap4flags existed this returned a bare "keep" because the
         // command was skipped; the risk being guarded against is the opposite
         // mistake, where the flag command counts as "an action was taken" and the
         // implicit keep disappears along with the mail.
         Assert.AreEqual("keep;flags:\\Seen",
            Evaluate("require \"imap4flags\";\r\naddflag \"\\\\Seen\";", Message));

         // Several flags may share one string, and setflag replaces the set.
         Assert.AreEqual("keep;flags:\\Seen \\Flagged",
            Evaluate("require \"imap4flags\";\r\nsetflag \"\\\\Seen \\\\Flagged\";", Message));

         // removeflag takes them away again, and case is canonicalised on the way
         // in so that "\\seen" and "\\Seen" are one flag.
         Assert.AreEqual("keep;flags:\\Flagged",
            Evaluate("require \"imap4flags\";\r\nsetflag [\"\\\\seen\", \"\\\\Flagged\"];\r\nremoveflag \"\\\\SEEN\";", Message));

         // Keywords as well as system flags.
         Assert.AreEqual("keep;flags:$label1",
            Evaluate("require \"imap4flags\";\r\naddflag \"$label1\";", Message));
      }

      [Test]
      [Description(":flags on fileinto sets the stored copy's flags - and is no longer mistaken for the mailbox name.")]
      public void TestFlagsTagOnFileIntoAndKeep()
      {
         // This is the sharpest of the imap4flags cases. ":flags" takes a value, so
         // before the parser and evaluator agreed about that, the first string in
         // the argument list was read as the mailbox: the message went into a
         // folder literally called "\Seen \Flagged".
         Assert.AreEqual("fileinto:Archive;flags:\\Seen \\Flagged",
            Evaluate("require [\"imap4flags\", \"fileinto\"];\r\nfileinto :flags \"\\\\Seen \\\\Flagged\" \"Archive\";", Message));

         Assert.AreEqual("keep;flags:\\Seen",
            Evaluate("require \"imap4flags\";\r\nkeep :flags \"\\\\Seen\";", Message));

         // An explicit :flags on the action that stores the message wins over the
         // internal flag variable.
         Assert.AreEqual("keep;flags:\\Flagged",
            Evaluate("require \"imap4flags\";\r\naddflag \"\\\\Seen\";\r\nkeep :flags \"\\\\Flagged\";", Message));
      }

      [Test]
      [Description("hasflag tests the flags the script has set so far.")]
      public void TestHasFlagTest()
      {
         Assert.AreEqual("fileinto:Seen;flags:\\Seen",
            Evaluate("require \"imap4flags\";\r\naddflag \"\\\\Seen\";\r\nif hasflag \"\\\\Seen\" {\r\n  fileinto \"Seen\";\r\n}", Message));

         // Nothing has been flagged, so the test is false and the implicit keep
         // stands. No flags token either: the script never touched the flag set.
         Assert.AreEqual("keep",
            Evaluate("require \"imap4flags\";\r\nif hasflag \"\\\\Seen\" {\r\n  discard;\r\n}", Message));
      }

      [Test]
      [Description("redirect :copy forwards a copy and leaves the implicit keep in place; a plain redirect still cancels it.")]
      public void TestRedirectCopy()
      {
         // RFC 3894: ":copy" means "as well as", so the local copy survives.
         Assert.AreEqual("redirect:fwd@example.test;keep",
            Evaluate("require \"copy\";\r\nredirect :copy \"fwd@example.test\";", Message));

         // Without it, the behaviour is the historical one and must not drift.
         Assert.AreEqual("redirect:fwd@example.test",
            Evaluate("redirect \"fwd@example.test\";", Message));
      }

      [Test]
      [Description("A redirect cancels only the implicit keep; an explicit keep or fileinto survives it.")]
      public void TestRedirectDoesNotCancelAnExplicitKeep()
      {
         // RFC 5228 4.2: redirect cancels the *implicit* keep. A keep the script
         // actually asked for is not the implicit keep and must survive, whichever
         // order the two commands appear in. Getting this wrong loses the local
         // copy of every "keep; redirect ...;" script - the message is forwarded
         // and then silently not delivered, which is the shape of bug nobody
         // notices until the mail is already gone.
         Assert.AreEqual("keep;redirect:fwd@example.test",
            Evaluate("keep;\r\nredirect \"fwd@example.test\";", Message));

         Assert.AreEqual("redirect:fwd@example.test;keep",
            Evaluate("redirect \"fwd@example.test\";\r\nkeep;", Message));

         Assert.AreEqual("fileinto:Archive;redirect:fwd@example.test",
            Evaluate("require \"fileinto\";\r\nfileinto \"Archive\";\r\nredirect \"fwd@example.test\";", Message));

         // A discard before the redirect still means no local copy: there was no
         // keep to preserve.
         Assert.AreEqual("discard;redirect:fwd@example.test",
            Evaluate("discard;\r\nredirect \"fwd@example.test\";", Message));
      }

      [Test]
      [Description("The envelope test reads the SMTP envelope, falling back to the Return-Path that local delivery writes.")]
      public void TestEnvelopeTest()
      {
         Assert.AreEqual("fileinto:Boss",
            Evaluate("require \"envelope\";\r\nif envelope :is :domain \"from\" \"example.test\" {\r\n  fileinto \"Boss\";\r\n}", Message));

         Assert.AreEqual("fileinto:Boss",
            Evaluate("require \"envelope\";\r\nif envelope :is \"from\" \"boss@example.test\" {\r\n  fileinto \"Boss\";\r\n}", Message));

         Assert.AreEqual("keep",
            Evaluate("require \"envelope\";\r\nif envelope :is \"from\" \"someone@elsewhere.test\" {\r\n  discard;\r\n}", Message));

         // A null return path is the empty string, not a missing value, so it
         // compares equal to "". This is how a bounce is recognised.
         const string bounce =
            "Return-Path: <>\r\n" +
            "From: MAILER-DAEMON@example.test\r\n" +
            "Subject: Undelivered Mail\r\n" +
            "\r\n" +
            "body";

         Assert.AreEqual("fileinto:Bounces",
            Evaluate("require \"envelope\";\r\nif envelope :is \"from\" \"\" {\r\n  fileinto \"Bounces\";\r\n}", bounce));

         // With no envelope and no Return-Path at all the value is unknown, which
         // is not the same as empty: the test finds nothing to compare.
         const string noReturnPath = "From: a@b.test\r\nSubject: x\r\n\r\nbody";
         Assert.AreEqual("keep",
            Evaluate("require \"envelope\";\r\nif envelope :is \"from\" \"\" {\r\n  discard;\r\n}", noReturnPath));
      }

      [Test]
      [Description("relational :count counts matching header instances and :value compares them with an ordering.")]
      public void TestRelationalTests()
      {
         // Two X-Tag headers.
         Assert.AreEqual("discard",
            Evaluate("require \"relational\";\r\nif header :count \"ge\" \"X-Tag\" \"2\" {\r\n  discard;\r\n}", Message));

         Assert.AreEqual("keep",
            Evaluate("require \"relational\";\r\nif header :count \"gt\" \"X-Tag\" \"2\" {\r\n  discard;\r\n}", Message));

         // X-Priority: 5 is greater than 3 numerically. Lexically "5" > "3" too, so
         // the interesting half is that 10 also beats 3 - which it would not do as
         // text.
         Assert.AreEqual("discard",
            Evaluate("require [\"relational\", \"comparator-i;ascii-numeric\"];\r\n" +
                     "if header :value \"gt\" :comparator \"i;ascii-numeric\" \"X-Priority\" \"3\" {\r\n  discard;\r\n}", Message));

         const string priorityTen =
            "From: a@b.test\r\nSubject: x\r\nX-Priority: 10\r\n\r\nbody";

         Assert.AreEqual("discard",
            Evaluate("require [\"relational\", \"comparator-i;ascii-numeric\"];\r\n" +
                     "if header :value \"gt\" :comparator \"i;ascii-numeric\" \"X-Priority\" \"9\" {\r\n  discard;\r\n}", priorityTen));

         // The default comparator orders as text, where "Quarterly" sorts after "A".
         Assert.AreEqual("discard",
            Evaluate("require \"relational\";\r\nif header :value \"gt\" \"Subject\" \"A\" {\r\n  discard;\r\n}", Message));
      }

      [Test]
      [Description("subaddress splits the local part into :user and :detail around the '+' separator.")]
      public void TestSubaddressParts()
      {
         Assert.AreEqual("fileinto:News",
            Evaluate("require \"subaddress\";\r\nif address :detail :is \"To\" \"news\" {\r\n  fileinto \"News\";\r\n}", Message));

         Assert.AreEqual("fileinto:Mine",
            Evaluate("require \"subaddress\";\r\nif address :user :is \"To\" \"user\" {\r\n  fileinto \"Mine\";\r\n}", Message));

         // An address with no separator has no detail at all, and an absent detail
         // matches nothing - not even the empty string.
         const string plain = "From: a@b.test\r\nTo: user@example.test\r\nSubject: x\r\n\r\nbody";

         Assert.AreEqual("keep",
            Evaluate("require \"subaddress\";\r\nif address :detail :is \"To\" \"\" {\r\n  discard;\r\n}", plain));

         // ...but :user is then the whole local part.
         Assert.AreEqual("discard",
            Evaluate("require \"subaddress\";\r\nif address :user :is \"To\" \"user\" {\r\n  discard;\r\n}", plain));
      }

      [Test]
      [Description("Regression guard: the five core actions still produce byte-identical summaries.")]
      public void TestCoreActionSummariesUnchanged()
      {
         // These cannot fail against the current code, deliberately. The implicit
         // keep now hangs off a separate flag rather than "the action list is
         // empty", and this is the guard that the change is invisible to every
         // script that does not use an extension.
         Assert.AreEqual("keep", Evaluate("", Message));
         Assert.AreEqual("keep", Evaluate("keep;", Message));
         Assert.AreEqual("discard", Evaluate("discard;", Message));
         Assert.AreEqual("fileinto:Spam", Evaluate("require \"fileinto\";\r\nfileinto \"Spam\";", Message));
         Assert.AreEqual("redirect:a@b.test", Evaluate("redirect \"a@b.test\";", Message));
         Assert.AreEqual("discard", Evaluate("discard;\r\nstop;\r\nkeep;", Message));
         Assert.AreEqual("fileinto:A;fileinto:B",
            Evaluate("require \"fileinto\";\r\nfileinto \"A\";\r\nfileinto \"B\";", Message));
      }

      [Test]
      [Description("A script that cannot be run yields an error, which the delivery path turns into an implicit keep.")]
      public void TestUnrunnableScriptStillReportsError()
      {
         // Losing mail to a filter typo is not acceptable, so every failure mode
         // here has to be reported as an error rather than as an action: the
         // delivery path treats "error:" as "keep the message".
         StringAssert.StartsWith("error:", Evaluate("if true {\r\n  keep;", Message));
         StringAssert.StartsWith("error:", Evaluate("require \"frobnicate\";\r\nkeep;", Message));
         StringAssert.StartsWith("error:", Evaluate("vacation \"away\";", Message));
         StringAssert.StartsWith("error:", Evaluate("require \"imap4flags\";\r\nsetflag \"\\\\Recent\";", Message));
      }
   }
}
