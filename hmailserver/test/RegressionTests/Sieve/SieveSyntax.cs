// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

using System.Reflection;
using NUnit.Framework;
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
   }
}
