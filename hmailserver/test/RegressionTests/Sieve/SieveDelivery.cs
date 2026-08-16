// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System.Reflection;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.Sieve
{
   /// <summary>
   ///    End-to-end verification that a per-account Sieve script is evaluated
   ///    during local delivery and that fileinto / discard actions take effect.
   ///    The script is configured late-bound via the COM Account.SieveScript
   ///    property.
   /// </summary>
   [TestFixture]
   public class SieveDelivery : TestFixtureBase
   {
      private static void SetScript(object account, string script)
      {
         account.GetType().InvokeMember(
            "SieveScript", BindingFlags.SetProperty, null, account, new object[] { script });
      }

      [Test]
      [Description("A fileinto action routes a matching message into the named folder instead of INBOX.")]
      public void TestFileIntoRoutesMessage()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-fileinto@example.test", "test");
         account.IMAPFolders.Add("Spam");

         SetScript(account,
            "require \"fileinto\";\r\n" +
            "if header :contains \"Subject\" \"lottery\" {\r\n" +
            "  fileinto \"Spam\";\r\n" +
            "}");

         SmtpClientSimulator.StaticSend("sender@example.test", account.Address, "You won the lottery!", "body");

         IMAPFolder spam = account.IMAPFolders.get_ItemByName("Spam");
         CustomAsserts.AssertFolderMessageCount(spam, 1);

         IMAPFolder inbox = account.IMAPFolders.get_ItemByName("INBOX");
         CustomAsserts.AssertFolderMessageCount(inbox, 0);
      }

      [Test]
      [Description("A discard action silently drops a matching message while normal delivery still works.")]
      public void TestDiscardDropsMessage()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-discard@example.test", "test");

         SetScript(account,
            "if header :contains \"Subject\" \"blockme\" {\r\n" +
            "  discard;\r\n" +
            "}");

         // The matching message is dropped; a normal message is still delivered.
         SmtpClientSimulator.StaticSend("sender@example.test", account.Address, "Please blockme now", "body");
         SmtpClientSimulator.StaticSend("sender@example.test", account.Address, "An ordinary message", "body");

         IMAPFolder inbox = account.IMAPFolders.get_ItemByName("INBOX");
         CustomAsserts.AssertFolderMessageCount(inbox, 1);
      }

      [Test]
      [Description("A redirect action forwards a copy to another address and cancels the implicit local keep.")]
      public void TestRedirectForwardsAndCancelsKeep()
      {
         var source = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-redir@example.test", "test");
         var target = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sieve-target@example.test", "test");

         SetScript(source,
            "if header :contains \"Subject\" \"fwdme\" {\r\n" +
            "  redirect \"sieve-target@example.test\";\r\n" +
            "}");

         SmtpClientSimulator.StaticSend("sender@example.test", source.Address, "Please fwdme along", "body");

         // The target receives the redirected copy.
         IMAPFolder targetInbox = target.IMAPFolders.get_ItemByName("INBOX");
         CustomAsserts.AssertFolderMessageCount(targetInbox, 1);

         // The source keeps no local copy (redirect cancels the implicit keep).
         IMAPFolder sourceInbox = source.IMAPFolders.get_ItemByName("INBOX");
         CustomAsserts.AssertFolderMessageCount(sourceInbox, 0);
      }
   }
}
