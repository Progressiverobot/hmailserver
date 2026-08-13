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
         // not accepted and then silently ignored at delivery. "reject" (RFC 5429)
         // is the case that matters: a script requiring it used to be reported valid
         // while the reject never happened, so mail the user believed was being
         // refused was quietly kept instead.
         Assert.IsNotEmpty(CheckSyntax("require [\"fileinto\", \"reject\"];\r\nif not exists \"Date\" {\r\n  reject \"no date\";\r\n}"));
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
