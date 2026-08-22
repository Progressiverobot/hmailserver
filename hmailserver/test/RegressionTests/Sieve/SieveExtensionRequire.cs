// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Reflection;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.Sieve
{
   /// <summary>
   ///    Covers the semantic half of Sieve script validation, as opposed to the
   ///    grammar covered by SieveSyntax.
   ///
   ///    This fixture exists because of a specific failure shape. Before it, the
   ///    parser accepted any name at all in a "require" and accepted commands it
   ///    had no intention of carrying out - "require" was collected and thrown
   ///    away, and an unimplemented command such as vacation parsed cleanly and
   ///    then did nothing at delivery time. A filter that silently does not run is
   ///    the worst outcome available: the author is told the script is fine and
   ///    only finds out otherwise from the mail that did not get filed, weeks
   ///    later. RFC 5228 2.10.5 is explicit that an unsupported "require" is an
   ///    error, and the only moment anyone is listening for that error is when the
   ///    script is uploaded.
   ///
   ///    Invoked late-bound via IDispatch so the test does not depend on the
   ///    registered type library being regenerated.
   /// </summary>
   [TestFixture]
   public class SieveExtensionRequire : TestFixtureBase
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
      [Description("An extension name this server cannot honour is refused by require, rather than collected and ignored.")]
      public void TestUnsupportedRequireIsRefused()
      {
         // Nothing in the server has ever known what "frobnicate" means, and until
         // now that was not an error.
         Assert.IsNotEmpty(CheckSyntax("require \"frobnicate\";\r\nkeep;"));

         // "reject" was the interesting case for years: real, well-known, and not
         // implemented - accepting its require meant accepting a script whose
         // whole purpose would silently not happen. It SHIPPED on 16 August 2026
         // (SieveRejectDelivery.cs proves the report arrives), so the sentinel
         // duty passes to a vendor name that can never ship, so this test stops
         // needing an edit every time an extension lands.
         Assert.IsEmpty(CheckSyntax("require \"reject\";\r\nkeep;"));
         Assert.IsNotEmpty(CheckSyntax("require \"vnd.example.absent\";\r\nkeep;"));

         // A list where only one entry is unsupported still fails.
         Assert.IsNotEmpty(CheckSyntax("require [\"fileinto\", \"notify\"];\r\nkeep;"));

         // require with nothing to require is not a valid statement either.
         Assert.IsNotEmpty(CheckSyntax("require;\r\nkeep;"));

         // The extensions that are implemented are still accepted.
         Assert.IsEmpty(CheckSyntax(
            "require [\"fileinto\", \"copy\", \"envelope\", \"imap4flags\", \"vacation\", \"relational\", \"subaddress\"];\r\nkeep;"));
      }

      [Test]
      [Description("An extension command or test used without its require is refused.")]
      public void TestExtensionUsedWithoutRequireIsRefused()
      {
         // Each of these parsed cleanly before and then did nothing.
         Assert.IsNotEmpty(CheckSyntax("vacation \"I am away\";"));
         Assert.IsNotEmpty(CheckSyntax("addflag \"\\\\Seen\";"));
         Assert.IsNotEmpty(CheckSyntax("if envelope :is \"from\" \"a@b.test\" {\r\n  keep;\r\n}"));
         Assert.IsNotEmpty(CheckSyntax("if hasflag \"\\\\Seen\" {\r\n  keep;\r\n}"));
         Assert.IsNotEmpty(CheckSyntax("if header :count \"eq\" \"Received\" \"1\" {\r\n  keep;\r\n}"));
         Assert.IsNotEmpty(CheckSyntax("if address :detail :is \"To\" \"news\" {\r\n  keep;\r\n}"));
         Assert.IsNotEmpty(CheckSyntax("require \"fileinto\";\r\nfileinto :copy \"Archive\";"));

         // With the require in place each one is accepted.
         Assert.IsEmpty(CheckSyntax("require \"vacation\";\r\nvacation \"I am away\";"));
         Assert.IsEmpty(CheckSyntax("require \"imap4flags\";\r\naddflag \"\\\\Seen\";"));
         Assert.IsEmpty(CheckSyntax("require \"envelope\";\r\nif envelope :is \"from\" \"a@b.test\" {\r\n  keep;\r\n}"));
         Assert.IsEmpty(CheckSyntax("require \"imap4flags\";\r\nif hasflag \"\\\\Seen\" {\r\n  keep;\r\n}"));
         Assert.IsEmpty(CheckSyntax("require \"relational\";\r\nif header :count \"eq\" \"Received\" \"1\" {\r\n  keep;\r\n}"));
         Assert.IsEmpty(CheckSyntax("require \"subaddress\";\r\nif address :detail :is \"To\" \"news\" {\r\n  keep;\r\n}"));
      }

      [Test]
      [Description("fileinto :copy is refused outright, because this server has only one local copy to give.")]
      public void TestFileIntoCopyIsRefusedRatherThanSilentlyDropped()
      {
         // hMailServer stores one local copy of a message, so "file this into
         // Archive *and* leave the implicit keep in INBOX" cannot be carried out.
         // Saying so is the point: the alternative is filing it into Archive and
         // letting the author believe there is also a copy in INBOX.
         string error = CheckSyntax("require [\"fileinto\", \"copy\"];\r\nfileinto :copy \"Archive\";");
         Assert.IsNotEmpty(error);
         StringAssert.Contains("copy", error);

         // ":copy" on redirect is supported, and is the useful half of RFC 3894.
         Assert.IsEmpty(CheckSyntax("require \"copy\";\r\nredirect :copy \"fwd@example.test\";"));
      }

      [Test]
      [Description("Malformed extension arguments are refused: a bad relation, an unknown comparator, an unknown envelope part, an unsettable flag.")]
      public void TestExtensionArgumentsAreValidated()
      {
         // ":value" needs one of the six relational operators.
         Assert.IsNotEmpty(CheckSyntax("require \"relational\";\r\nif header :value \"bigger\" \"Subject\" \"a\" {\r\n  keep;\r\n}"));
         Assert.IsEmpty(CheckSyntax("require \"relational\";\r\nif header :value \"gt\" \"Subject\" \"a\" {\r\n  keep;\r\n}"));

         // A comparator we do not have cannot be honoured.
         Assert.IsNotEmpty(CheckSyntax("if header :comparator \"i;unicode-casemap\" :is \"Subject\" \"a\" {\r\n  keep;\r\n}"));
         Assert.IsEmpty(CheckSyntax("if header :comparator \"i;octet\" :is \"Subject\" \"a\" {\r\n  keep;\r\n}"));

         // The envelope has a "from" and a "to" and nothing else here.
         Assert.IsNotEmpty(CheckSyntax("require \"envelope\";\r\nif envelope :is \"sender\" \"a@b.test\" {\r\n  keep;\r\n}"));
         Assert.IsEmpty(CheckSyntax("require \"envelope\";\r\nif envelope :is [\"from\", \"to\"] \"a@b.test\" {\r\n  keep;\r\n}"));

         // \Recent cannot be set by a client (RFC 3501), and a made-up system flag
         // is a typo, not a keyword.
         Assert.IsNotEmpty(CheckSyntax("require \"imap4flags\";\r\nsetflag \"\\\\Recent\";"));
         Assert.IsNotEmpty(CheckSyntax("require \"imap4flags\";\r\nsetflag \"\\\\Sean\";"));
         Assert.IsEmpty(CheckSyntax("require \"imap4flags\";\r\nsetflag \"\\\\Seen $label1\";"));

         // vacation :days is a number of days, and zero days is meaningless.
         Assert.IsNotEmpty(CheckSyntax("require \"vacation\";\r\nvacation :days 0 \"away\";"));
         Assert.IsEmpty(CheckSyntax("require \"vacation\";\r\nvacation :days 1 \"away\";"));

         // ":from" used to be refused outright on the grounds that accepting a tag and
         // ignoring it would mislead. It is implemented now, so refusing it would be the
         // misleading answer: it is honoured only for an address that actually reaches this
         // account - the account itself or an active alias pointing at it - which cannot be
         // checked without the account, so it is enforced at delivery time in
         // SieveVacationResponder and covered by SieveVacationDelivery.
         //
         // What the validator can still check is that it is an address at all, and it must,
         // because a malformed :from would otherwise fail silently at delivery on a script
         // the user was told was valid.
         Assert.IsEmpty(CheckSyntax("require \"vacation\";\r\nvacation :from \"other@example.test\" \"away\";"));
         Assert.IsNotEmpty(CheckSyntax("require \"vacation\";\r\nvacation :from \"not an address\" \"away\";"));
      }

      [Test]
      [Description("Regression guard: the scripts SieveSyntax already accepts keep parsing, and the ones it rejects keep failing.")]
      public void TestExistingScriptsAreUnaffected()
      {
         // This assertion set cannot fail against the current code - that is
         // deliberate. The validation pass added alongside it rejects whole
         // categories of script, and this is the guard that says it rejected only
         // the intended ones. The five core actions and nine core tests have to go
         // on behaving exactly as they did.
         Assert.IsEmpty(CheckSyntax(""));
         Assert.IsEmpty(CheckSyntax("keep;"));
         Assert.IsEmpty(CheckSyntax("require \"fileinto\";\r\nif header :contains \"Subject\" \"hello\" {\r\n  fileinto \"INBOX\";\r\n}"));
         Assert.IsEmpty(CheckSyntax("if size :over 100K {\r\n  discard;\r\n} else {\r\n  keep;\r\n}"));
         Assert.IsEmpty(CheckSyntax("if anyof (header :is \"From\" \"a@b.com\", exists \"X-Spam\") {\r\n  redirect \"x@y.com\";\r\n}"));
         Assert.IsEmpty(CheckSyntax("if not exists \"Date\" {\r\n  discard;\r\n}"));
         Assert.IsEmpty(CheckSyntax("if allof (header :matches \"Subject\" \"*win*\", address :is :domain \"From\" \"evil.com\") {\r\n  discard;\r\n  stop;\r\n}"));

         // fileinto is deliberately still accepted without require "fileinto",
         // which the RFC would not allow. Scripts in the field rely on it, and
         // tightening it would move mail that is being filed today back into INBOX.
         Assert.IsEmpty(CheckSyntax("if exists \"X-Spam-Flag\" {\r\n  fileinto \"Junk\";\r\n} else {\r\n  keep;\r\n}"));

         Assert.IsNotEmpty(CheckSyntax("keep"));
         Assert.IsNotEmpty(CheckSyntax("if true {\r\n  keep;"));
         Assert.IsNotEmpty(CheckSyntax("frobnicate;"));
         Assert.IsNotEmpty(CheckSyntax("if bogustest \"x\" {\r\n  keep;\r\n}"));
         Assert.IsNotEmpty(CheckSyntax("keep;\r\nrequire \"fileinto\";"));
         Assert.IsNotEmpty(CheckSyntax("redirect \"unterminated;"));
      }
   }
}
