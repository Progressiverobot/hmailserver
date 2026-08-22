// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Reflection;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Sieve
{
   /// <summary>
   ///    Exercises per-account Sieve script storage through the COM Account
   ///    property (Account.SieveScript), which is file-backed under the data
   ///    directory. Invoked late-bound via IDispatch so the test does not depend
   ///    on the registered type library being regenerated.
   /// </summary>
   [TestFixture]
   public class SieveAccountScript : TestFixtureBase
   {
      private static string GetScript(object account)
      {
         return (string)account.GetType().InvokeMember(
            "SieveScript", BindingFlags.GetProperty, null, account, null);
      }

      private static void SetScript(object account, string script)
      {
         account.GetType().InvokeMember(
            "SieveScript", BindingFlags.SetProperty, null, account, new object[] { script });
      }

      [Test]
      [Description("An account's Sieve script round-trips through the file-backed COM property.")]
      public void TestScriptRoundTrips()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-store@example.test", "test");

         // No script configured initially.
         Assert.IsEmpty(GetScript(account));

         string script =
            "require \"fileinto\";\r\n" +
            "if header :contains \"Subject\" \"hello\" {\r\n" +
            "  fileinto \"Spam\";\r\n" +
            "}";

         SetScript(account, script);
         Assert.AreEqual(script, GetScript(account));

         // Overwriting replaces the stored script.
         string script2 = "keep;";
         SetScript(account, script2);
         Assert.AreEqual(script2, GetScript(account));

         // An empty value clears the stored script.
         SetScript(account, "");
         Assert.IsEmpty(GetScript(account));
      }
   }
}
