// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

using System.Reflection;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.Sieve
{
   /// <summary>
   ///    Exercises the Sieve (RFC 5228) syntax checker exposed through the COM
   ///    Utilities API (CheckSieveSyntax). The method is invoked late-bound via
   ///    IDispatch so the test does not depend on the registered type library
   ///    being regenerated.
   /// </summary>
   [TestFixture]
   public class SieveSyntax : TestFixtureBase
   {
      private string CheckSyntax(string script)
      {
         object utilities = _application.Utilities;
         object result = utilities.GetType().InvokeMember(
            "CheckSieveSyntax",
            BindingFlags.InvokeMethod,
            null,
            utilities,
            new object[] { script });

         return (string)result;
      }

      [Test]
      [Description("A syntactically valid Sieve script returns an empty error string.")]
      public void TestValidScripts()
      {
         Assert.IsEmpty(CheckSyntax(""));
         Assert.IsEmpty(CheckSyntax("keep;"));
         Assert.IsEmpty(CheckSyntax("require \"fileinto\";\r\nif header :contains \"Subject\" \"hello\" {\r\n  fileinto \"INBOX\";\r\n}"));
         Assert.IsEmpty(CheckSyntax("if size :over 100K {\r\n  discard;\r\n} else {\r\n  keep;\r\n}"));
         Assert.IsEmpty(CheckSyntax("if anyof (header :is \"From\" \"a@b.com\", exists \"X-Spam\") {\r\n  redirect \"x@y.com\";\r\n}"));

         // The mailbox extension (RFC 5490): mailboxexists and fileinto :create
         // are valid under their require.
         Assert.IsEmpty(CheckSyntax("require [\"mailbox\", \"fileinto\"];\r\nif mailboxexists \"Archive\" {\r\n  fileinto :create \"Archive\";\r\n}"));

         // The regex extension (draft-ietf-sieve-regex) under its require.
         Assert.IsEmpty(CheckSyntax("require \"regex\";\r\nif header :regex \"Subject\" \"^inv[o0]ice #[0-9]+\" {\r\n  discard;\r\n}"));

         // The ihave grant (RFC 5463): the guarded block uses the body TEST with no
         // require "body" anywhere, because the guard is the availability check.
         // This passing is what makes ihave a feature rather than a trap. The
         // granted feature must be one the validator actually gates on require -
         // the require-gated features are tests and tagged arguments; implemented
         // COMMANDS such as fileinto have always been accepted leniently without
         // their require, so they cannot exercise the grant.
         Assert.IsEmpty(CheckSyntax("require \"ihave\";\r\nif ihave \"body\" {\r\n  if body :contains \"x\" {\r\n    keep;\r\n  }\r\n}"));

         // The grant flows through allof, whose every conjunct must have been true
         // for the block to run.
         Assert.IsEmpty(CheckSyntax("require \"ihave\";\r\nif allof(ihave \"body\", true) {\r\n  if body :contains \"x\" {\r\n    keep;\r\n  }\r\n}"));

         // environment under its require.
         Assert.IsEmpty(CheckSyntax("require \"environment\";\r\nif environment :is \"name\" \"hMailServer\" {\r\n  keep;\r\n}"));

         // notify and its tests under their require, mailto being the one method.
         Assert.IsEmpty(CheckSyntax("require \"enotify\";\r\nnotify :importance \"1\" :message \"hi\" \"mailto:a@example.test\";"));
         Assert.IsEmpty(CheckSyntax("require \"enotify\";\r\nif valid_notify_method \"mailto:a@example.test\" {\r\n  keep;\r\n}"));

         // include, return and global under their requires.
         Assert.IsEmpty(CheckSyntax("require \"include\";\r\ninclude :personal :once \"helper\";\r\nreturn;"));
         Assert.IsEmpty(CheckSyntax("require [\"include\", \"variables\"];\r\nglobal \"shared\";\r\nset \"shared\" \"v\";"));

         // reject and ereject under their requires.
         Assert.IsEmpty(CheckSyntax("require \"reject\";\r\nreject \"go away\";"));
         Assert.IsEmpty(CheckSyntax("require \"ereject\";\r\nereject \"refused\";"));

         // variables under its require: set with modifiers, and the string test.
         Assert.IsEmpty(CheckSyntax("require \"variables\";\r\nset \"a\" \"b\";\r\nif string :is \"${a}\" \"b\" {\r\n  keep;\r\n}"));
         Assert.IsEmpty(CheckSyntax("require \"variables\";\r\nset :length :upper \"n\" \"abc\";"));

         // editheader under its require.
         Assert.IsEmpty(CheckSyntax("require \"editheader\";\r\naddheader \"X-Note\" \"v\";\r\ndeleteheader :contains \"X-Old\" \"x\";"));

         // duplicate under its require, in all three identifier spellings.
         Assert.IsEmpty(CheckSyntax("require \"duplicate\";\r\nif duplicate {\r\n  discard;\r\n}"));
         Assert.IsEmpty(CheckSyntax("require \"duplicate\";\r\nif duplicate :header \"X-Token\" :seconds 3600 :last {\r\n  discard;\r\n}"));
         Assert.IsEmpty(CheckSyntax("require \"duplicate\";\r\nif duplicate :uniqueid \"tok\" :handle \"w\" {\r\n  discard;\r\n}"));

         // spamtest under its require, with :percent under spamtestplus.
         Assert.IsEmpty(CheckSyntax("require \"spamtest\";\r\nif spamtest :is \"0\" {\r\n  keep;\r\n}"));
         Assert.IsEmpty(CheckSyntax("require [\"spamtest\", \"spamtestplus\"];\r\nif spamtest :percent :is \"100\" {\r\n  keep;\r\n}"));

         // date, currentdate and :index under their requires.
         Assert.IsEmpty(CheckSyntax("require \"date\";\r\nif currentdate :is \"weekday\" \"0\" {\r\n  keep;\r\n}"));
         Assert.IsEmpty(CheckSyntax("require \"date\";\r\nif date :zone \"+0200\" \"date\" \"hour\" \"15\" {\r\n  keep;\r\n}"));
         Assert.IsEmpty(CheckSyntax("require \"index\";\r\nif header :index 2 :last :is \"Received\" \"x\" {\r\n  keep;\r\n}"));

         // A comment plus a multi-name require of extensions that ARE implemented.
         // This line used to require "reject", which the parser accepted and then
         // silently ignored - so the script was reported valid and the reject never
         // happened. require now refuses unknown extensions at upload time, which is
         // what RFC 5228 section 3.2 asks for, so the negative case moved to
         // TestInvalidScripts below.
         Assert.IsEmpty(CheckSyntax("# a comment\r\nrequire [\"fileinto\", \"imap4flags\"];\r\nif not exists \"Date\" {\r\n  fileinto :flags \"\\\\Seen\" \"INBOX\";\r\n}"));
      }

      [Test]
      [Description("Syntactically invalid Sieve scripts return a non-empty error string.")]
      public void TestInvalidScripts()
      {
         // Missing semicolon / block terminator.
         Assert.IsNotEmpty(CheckSyntax("keep"));
         // Unbalanced brace.
         Assert.IsNotEmpty(CheckSyntax("if true {\r\n  keep;"));
         // Unknown command.
         Assert.IsNotEmpty(CheckSyntax("frobnicate;"));
         // Unknown test.
         Assert.IsNotEmpty(CheckSyntax("if bogustest \"x\" {\r\n  keep;\r\n}"));
         // require must precede other commands.
         Assert.IsNotEmpty(CheckSyntax("keep;\r\nrequire \"fileinto\";"));
         // Unterminated quoted string.
         Assert.IsNotEmpty(CheckSyntax("redirect \"unterminated;"));
         // An extension this server does not implement must be refused at upload,
         // not accepted and then silently ignored at delivery. "reject" was the
         // case that proved this rule (a script requiring it was once reported
         // valid while the reject never happened); reject SHIPPED on 16 August
         // 2026, so the sentinel here is an extension still genuinely absent.
         Assert.IsNotEmpty(CheckSyntax("require [\"fileinto\", \"vnd.example.absent\"];\r\nif not exists \"Date\" {\r\n  keep;\r\n}"));

         // The mailbox extension's pieces without its require line. Both must name
         // the missing extension rather than pass: a script written for a server
         // that has it must fail HERE at upload, not change meaning.
         Assert.IsNotEmpty(CheckSyntax("if mailboxexists \"Archive\" {\r\n  keep;\r\n}"));
         Assert.IsNotEmpty(CheckSyntax("require \"fileinto\";\r\nfileinto :create \"Archive\";"));

         // :create is a fileinto tag; on keep it is an error even with the
         // extension required.
         Assert.IsNotEmpty(CheckSyntax("require [\"mailbox\", \"fileinto\"];\r\nkeep :create;"));

         // :regex without its require.
         Assert.IsNotEmpty(CheckSyntax("if header :regex \"Subject\" \"^x\" {\r\n  keep;\r\n}"));

         // A ':regex' key that cannot compile must be refused AT UPLOAD with the
         // line named, not stored and silently never matched at delivery - the
         // exact failure mode the legacy rules engine had for years (HM6042).
         Assert.IsNotEmpty(CheckSyntax("require \"regex\";\r\nif header :regex \"Subject\" \"(unclosed\" {\r\n  keep;\r\n}"));

         // The numeric comparator only defines equality; a regex under it is
         // meaningless.
         Assert.IsNotEmpty(CheckSyntax("require [\"regex\", \"comparator-i;ascii-numeric\"];\r\nif header :regex :comparator \"i;ascii-numeric\" \"Subject\" \"^x\" {\r\n  keep;\r\n}"));

         // The ihave grant is scoped to the guarded block: the same body test
         // outside it still needs its require.
         Assert.IsNotEmpty(CheckSyntax("require \"ihave\";\r\nif ihave \"body\" {\r\n  keep;\r\n}\r\nif body :contains \"x\" {\r\n  keep;\r\n}"));

         // anyof does not grant - the block can run while the ihave conjunct is
         // false, so nothing about availability is proven inside it.
         Assert.IsNotEmpty(CheckSyntax("require \"ihave\";\r\nif anyof(ihave \"body\", true) {\r\n  if body :contains \"x\" {\r\n    keep;\r\n  }\r\n}"));

         // ihave and environment without their requires.
         Assert.IsNotEmpty(CheckSyntax("if ihave \"fileinto\" {\r\n  keep;\r\n}"));
         Assert.IsNotEmpty(CheckSyntax("if environment :is \"name\" \"x\" {\r\n  keep;\r\n}"));

         // notify: a scheme this server cannot notify by is refused with the
         // scheme named; a bad importance; the missing require.
         Assert.IsNotEmpty(CheckSyntax("require \"enotify\";\r\nnotify \"xmpp:a@example.test\";"));
         Assert.IsNotEmpty(CheckSyntax("require \"enotify\";\r\nnotify :importance \"4\" \"mailto:a@example.test\";"));
         Assert.IsNotEmpty(CheckSyntax("notify \"mailto:a@example.test\";"));

         // include: contradictory placement tags, a bad script name, global
         // without variables, and the missing require.
         Assert.IsNotEmpty(CheckSyntax("require \"include\";\r\ninclude :personal :global \"x\";"));
         Assert.IsNotEmpty(CheckSyntax("require \"include\";\r\ninclude \"../escape\";"));
         Assert.IsNotEmpty(CheckSyntax("require \"include\";\r\nglobal \"shared\";"));
         Assert.IsNotEmpty(CheckSyntax("include \"x\";"));

         // reject without its require, and with a stray tag.
         Assert.IsNotEmpty(CheckSyntax("reject \"go away\";"));
         Assert.IsNotEmpty(CheckSyntax("require \"reject\";\r\nreject :copy \"go away\";"));

         // variables: contradictory modifiers, an all-digit (match-variable)
         // name, an illegal name, and the missing require.
         Assert.IsNotEmpty(CheckSyntax("require \"variables\";\r\nset :lower :upper \"a\" \"b\";"));
         Assert.IsNotEmpty(CheckSyntax("require \"variables\";\r\nset \"1\" \"b\";"));
         Assert.IsNotEmpty(CheckSyntax("require \"variables\";\r\nset \"bad name\" \"b\";"));
         Assert.IsNotEmpty(CheckSyntax("set \"a\" \"b\";"));
         Assert.IsNotEmpty(CheckSyntax("if string :is \"a\" \"a\" {\r\n  keep;\r\n}"));

         // editheader: the trace headers are protected, the field name must be
         // legal, and the require is needed at all.
         Assert.IsNotEmpty(CheckSyntax("require \"editheader\";\r\ndeleteheader \"Received\";"));
         Assert.IsNotEmpty(CheckSyntax("require \"editheader\";\r\naddheader \"Bad Name\" \"v\";"));
         Assert.IsNotEmpty(CheckSyntax("addheader \"X-Note\" \"v\";"));

         // duplicate: :header and :uniqueid contradict each other, positionals are
         // not part of its grammar, and the require is needed at all.
         Assert.IsNotEmpty(CheckSyntax("require \"duplicate\";\r\nif duplicate :header \"X\" :uniqueid \"y\" {\r\n  discard;\r\n}"));
         Assert.IsNotEmpty(CheckSyntax("require \"duplicate\";\r\nif duplicate \"stray\" {\r\n  discard;\r\n}"));
         Assert.IsNotEmpty(CheckSyntax("if duplicate {\r\n  discard;\r\n}"));

         // :percent needs spamtestplus, not just spamtest; and spamtest needs
         // its require at all.
         Assert.IsNotEmpty(CheckSyntax("require \"spamtest\";\r\nif spamtest :percent :is \"100\" {\r\n  keep;\r\n}"));
         Assert.IsNotEmpty(CheckSyntax("if spamtest :is \"0\" {\r\n  keep;\r\n}"));

         // The date tests: unknown date part, malformed zone, :originalzone on
         // currentdate (which has no original), :last without :index, and :index
         // without its require - each refused at upload with the reason named.
         Assert.IsNotEmpty(CheckSyntax("require \"date\";\r\nif currentdate :is \"dayofweek\" \"0\" {\r\n  keep;\r\n}"));
         Assert.IsNotEmpty(CheckSyntax("require \"date\";\r\nif currentdate :zone \"UTC+2\" \"hour\" \"15\" {\r\n  keep;\r\n}"));
         Assert.IsNotEmpty(CheckSyntax("require \"date\";\r\nif currentdate :originalzone \"hour\" \"15\" {\r\n  keep;\r\n}"));
         Assert.IsNotEmpty(CheckSyntax("require \"index\";\r\nif header :last :is \"Received\" \"x\" {\r\n  keep;\r\n}"));
         Assert.IsNotEmpty(CheckSyntax("if header :index 2 :is \"Received\" \"x\" {\r\n  keep;\r\n}"));
      }

      /// <summary>
      ///    Every Sieve construct this server does not implement must be refused at
      ///    upload rather than parsed and quietly skipped at delivery.
      ///
      ///    This is the single property the roadmap's whole Sieve section leans on,
      ///    and it had already been false once: the parser used to accept `reject`,
      ///    report the script valid, and then keep the mail the author believed was
      ///    being refused. The allowlists in SieveParser fixed that, and nothing
      ///    pinned the fix beyond the one `reject` case - so adding any of these
      ///    names to a known-command or known-test list without implementing it
      ///    would silently re-create the hazard, with no test failing.
      ///
      ///    The list is deliberately the same set the roadmap names as not
      ///    implemented. When one of them IS implemented, its line moves out of here
      ///    and into a fixture that proves it works end to end - which is the same
      ///    rule the ManageSieve capability line follows.
      /// </summary>
      [Test]
      [Description("Every unimplemented Sieve construct is refused at upload, so no script can silently do nothing.")]
      public void UnimplementedConstructsAreRefusedRatherThanIgnored()
      {
         // HISTORY: this list once held reject, ereject, notify, addheader,
         // deleteheader, include, return, set, string, duplicate, mailboxexists,
         // ihave, environment, spamtest, date and currentdate. Every one of them
         // SHIPPED on 15-16 August 2026, each moving out into a fixture that
         // proves it end to end - which is this test's own documented rule. What
         // remains is the general property those names were instances of: a
         // construct outside the allowlists is refused, never parsed-and-ignored.
         var commands = new[]
         {
            "frobnicate_command \"x\";",
            "vnd_example_absent;",
         };

         foreach (string command in commands)
         {
            Assert.IsNotEmpty(CheckSyntax(command),
               "The command '" + command + "' was accepted. It is not implemented, so a script using it " +
               "would report success at upload and then do nothing at delivery - the exact failure the " +
               "allowlist in SieveParser exists to prevent.");
         }

         // Tests, each inside the smallest script that can carry it. The date and
         // currentdate lines that used to sit here also shipped (16 August 2026,
         // SieveDateDelivery.cs) - they were being refused for the require they
         // lacked by then, not for being unknown, which is the trap of leaving a
         // shipped name in this list.
         var tests = new[]
         {
            "frobnicate_test \"x\"",
            "vnd_example_absent \"x\"",
         };

         foreach (string test in tests)
         {
            string script = "if " + test + " {\r\n  keep;\r\n}";

            Assert.IsNotEmpty(CheckSyntax(script),
               "The test '" + test + "' was accepted. It is not implemented, so it would evaluate false " +
               "at delivery and send every script using it down the wrong branch, silently.");
         }
      }

      /// <summary>
      ///    The parser is recursive descent, once per level, and a Sieve script is
      ///    supplied by an authenticated user through ManageSieve PUTSCRIPT or
      ///    CHECKSCRIPT. Both recursions were unbounded, and the script size limit is
      ///    1 MB - "if true{" is eight bytes, so a script well inside the limit asks
      ///    for tens of thousands of levels.
      ///
      ///    Against the unfixed build these do not fail, they take the server down: a
      ///    stack overflow is not an exception the process can catch. Worth knowing
      ///    before running them against an old binary and assuming the harness broke.
      /// </summary>
      [Test]
      [Description("A script nesting blocks far deeper than any real script is refused, not parsed until the stack runs out.")]
      public void DeeplyNestedBlocksAreRefusedRatherThanRecursedInto()
      {
         const int levels = 5000;

         var script = new System.Text.StringBuilder();
         for (int i = 0; i < levels; i++)
            script.Append("if true {\r\n");
         script.Append("keep;\r\n");
         for (int i = 0; i < levels; i++)
            script.Append("}\r\n");

         string error = CheckSyntax(script.ToString());

         Assert.IsNotEmpty(error, "A script nested " + levels + " blocks deep must be refused.");
         StringAssert.Contains("deep", error,
            "The refusal should say the script is nested too deeply rather than report some incidental syntax error. Got: " + error);
      }

      [Test]
      [Description("The same for nested tests, which recurse through a separate path (anyof/allof/not).")]
      public void DeeplyNestedTestsAreRefusedRatherThanRecursedInto()
      {
         const int levels = 5000;

         // anyof(anyof(anyof(... exists "X" ...)))
         var script = new System.Text.StringBuilder("if ");
         for (int i = 0; i < levels; i++)
            script.Append("anyof(");
         script.Append("exists \"X\"");
         script.Append(new string(')', levels));
         script.Append(" {\r\n  keep;\r\n}");

         string error = CheckSyntax(script.ToString());

         Assert.IsNotEmpty(error, "A test nested " + levels + " deep must be refused.");
         StringAssert.Contains("deep", error,
            "The refusal should name the nesting depth rather than an incidental syntax error. Got: " + error);
      }

      [Test]
      [Description("The negative control: ordinary nesting still parses, so the limit cannot be satisfied by refusing everything.")]
      public void OrdinarilyNestedScriptsStillParse()
      {
         // Ten levels of each, which is past anything a person writes by hand and
         // past what a rule builder generates, and well inside the limit.
         var blocks = new System.Text.StringBuilder();
         for (int i = 0; i < 10; i++)
            blocks.Append("if true {\r\n");
         blocks.Append("keep;\r\n");
         for (int i = 0; i < 10; i++)
            blocks.Append("}\r\n");

         Assert.IsEmpty(CheckSyntax(blocks.ToString()), "Ten nested blocks is an ordinary script and must still parse.");

         var tests = new System.Text.StringBuilder("if ");
         for (int i = 0; i < 10; i++)
            tests.Append("anyof(");
         tests.Append("exists \"X\"");
         tests.Append(new string(')', 10));
         tests.Append(" {\r\n  keep;\r\n}");

         Assert.IsEmpty(CheckSyntax(tests.ToString()), "Ten nested tests is an ordinary script and must still parse.");
      }
   }
}
