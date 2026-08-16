// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System.Reflection;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.Sieve
{
   /// <summary>
   ///    Exercises the Sieve (RFC 5228) evaluator through the COM Utilities API
   ///    (EvaluateSieveScript). The method parses the script, evaluates it against
   ///    a raw RFC 822 message and returns the resulting action summary. Invoked
   ///    late-bound via IDispatch so the test does not depend on the registered
   ///    type library being regenerated.
   /// </summary>
   [TestFixture]
   public class SieveEvaluation : TestFixtureBase
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

      private const string Message =
         "From: \"Bad Sender\" <spammer@evil.com>\r\n" +
         "To: user@example.test\r\n" +
         "Subject: Cheap meds, hello there\r\n" +
         "X-Spam-Flag: YES\r\n" +
         "\r\n" +
         "This is the body of the message with some content to give it size.";

      [Test]
      [Description("A script that takes no action yields the implicit keep.")]
      public void TestImplicitKeep()
      {
         Assert.AreEqual("keep", Evaluate("", Message));
         Assert.AreEqual("keep", Evaluate("if header :contains \"Subject\" \"nomatch\" {\r\n  discard;\r\n}", Message));
      }

      [Test]
      [Description("header :contains drives fileinto when it matches.")]
      public void TestHeaderContainsFileInto()
      {
         string script = "require \"fileinto\";\r\nif header :contains \"Subject\" \"hello\" {\r\n  fileinto \"Spam\";\r\n}";
         Assert.AreEqual("fileinto:Spam", Evaluate(script, Message));
      }

      [Test]
      [Description("exists + the else branch are honoured.")]
      public void TestExistsAndElse()
      {
         string script = "if exists \"X-Spam-Flag\" {\r\n  fileinto \"Junk\";\r\n} else {\r\n  keep;\r\n}";
         Assert.AreEqual("fileinto:Junk", Evaluate(script, Message));

         string script2 = "if exists \"X-Does-Not-Exist\" {\r\n  fileinto \"Junk\";\r\n} else {\r\n  discard;\r\n}";
         Assert.AreEqual("discard", Evaluate(script2, Message));
      }

      [Test]
      [Description("The address test matches on the domain part.")]
      public void TestAddressDomain()
      {
         string script = "require \"fileinto\";\r\nif address :is :domain \"From\" \"evil.com\" {\r\n  fileinto \"Blocked\";\r\n}";
         Assert.AreEqual("fileinto:Blocked", Evaluate(script, Message));

         string script2 = "require \"fileinto\";\r\nif address :is :domain \"From\" \"good.com\" {\r\n  fileinto \"Blocked\";\r\n}";
         Assert.AreEqual("keep", Evaluate(script2, Message));
      }

      [Test]
      [Description("anyof / allof / not combine tests correctly.")]
      public void TestBooleanTests()
      {
         string anyof = "if anyof (header :is \"Subject\" \"nope\", exists \"X-Spam-Flag\") {\r\n  discard;\r\n}";
         Assert.AreEqual("discard", Evaluate(anyof, Message));

         string allof = "if allof (header :contains \"Subject\" \"hello\", address :is :domain \"From\" \"evil.com\") {\r\n  discard;\r\n}";
         Assert.AreEqual("discard", Evaluate(allof, Message));

         string notTest = "if not exists \"X-Spam-Flag\" {\r\n  discard;\r\n}";
         Assert.AreEqual("keep", Evaluate(notTest, Message));
      }

      [Test]
      [Description("size :over and :under compare against the message octet size.")]
      public void TestSizeTest()
      {
         Assert.AreEqual("discard", Evaluate("if size :over 10 {\r\n  discard;\r\n}", Message));
         Assert.AreEqual("keep", Evaluate("if size :over 100000 {\r\n  discard;\r\n}", Message));
         Assert.AreEqual("discard", Evaluate("if size :under 100000 {\r\n  discard;\r\n}", Message));
      }

      [Test]
      [Description("redirect and stop are honoured; stop halts later actions.")]
      public void TestRedirectAndStop()
      {
         Assert.AreEqual("redirect:fwd@example.test", Evaluate("redirect \"fwd@example.test\";", Message));

         string stopScript =
            "if exists \"X-Spam-Flag\" {\r\n  discard;\r\n  stop;\r\n}\r\nkeep;";
         Assert.AreEqual("discard", Evaluate(stopScript, Message));
      }

      [Test]
      [Description("A script that fails to parse returns an error description.")]
      public void TestParseErrorReported()
      {
         string result = Evaluate("if true {\r\n  keep;", Message);
         StringAssert.StartsWith("error:", result);
      }
   }
}
