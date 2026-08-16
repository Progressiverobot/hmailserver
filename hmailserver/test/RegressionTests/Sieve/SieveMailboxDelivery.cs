// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System.Reflection;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.Sieve
{
   /// <summary>
   ///    The "mailbox" extension (RFC 5490): the mailboxexists test and fileinto
   ///    :create, asserted through real deliveries in the pattern this project's
   ///    Sieve fixtures follow - every assertion is on where a message was FILED,
   ///    never on what the evaluator reported.
   ///
   ///    mailboxexists is the first Sieve test in this engine whose answer comes
   ///    from outside the message: the evaluator asks the delivery path, and the
   ///    delivery path answers from the recipient's real folder list using the same
   ///    lookup fileinto itself uses (MessageUtilities::FolderExistsForDelivery,
   ///    kept beside MoveToIMAPFolder so the two cannot drift). The negative control
   ///    matters accordingly: a callback that answered "true" for everything would
   ///    pass every positive test here and is exactly the bug the control catches.
   /// </summary>
   [TestFixture]
   public class SieveMailboxDelivery : TestFixtureBase
   {
      private const string Password = "secret";

      private static void SetScript(Account account, string script)
      {
         // Late-bound for the same reason the sibling Sieve fixtures are: the test
         // must not depend on the registered type library having been regenerated
         // after an IDL change.
         account.GetType().InvokeMember(
            "SieveScript", BindingFlags.SetProperty, null, account, new object[] { script });
      }

      private Account DeliverUnder(string script, bool createArchive)
      {
         Account recipient = SingletonProvider<TestSetup>.Instance.AddAccount(
            _domain, "sieve-mailbox@example.test", Password);

         if (createArchive)
            recipient.IMAPFolders.Add("Archive");

         SetScript(recipient, script);

         SmtpClientSimulator.StaticSend(
            "sieve-mailbox-sender@example.test", recipient.Address, "Mailbox test", "Body text.");

         // Drain the queue before reading any counts, so no assertion races the
         // delivery it is asking about.
         CustomAsserts.AssertRecipientsInDeliveryQueue(0);

         return recipient;
      }

      [Test]
      [Description("mailboxexists on a folder that exists is true, so the message is filed there.")]
      public void MailboxexistsOnAnExistingFolderIsTrue()
      {
         Account recipient = DeliverUnder(
            "require [\"mailbox\", \"fileinto\"];\r\n" +
            "if mailboxexists \"Archive\" {\r\n" +
            "  fileinto \"Archive\";\r\n" +
            "}\r\n",
            createArchive: true);

         IMAPFolder archive = recipient.IMAPFolders.get_ItemByName("Archive");
         CustomAsserts.AssertFolderMessageCount(archive, 1);

         IMAPFolder inbox = recipient.IMAPFolders.get_ItemByName("INBOX");
         CustomAsserts.AssertFolderMessageCount(inbox, 0);
      }

      /// <summary>
      ///    The negative control the fixture depends on. The folder genuinely does
      ///    not exist, so the test must be false and the message must stay in INBOX.
      ///    An implementation that answered "true" for every name - a callback wired
      ///    to the wrong account, or none at all with a default of true - passes
      ///    every other test here and fails only this one.
      /// </summary>
      [Test]
      [Description("mailboxexists on a folder that does not exist is false - the control the rest depend on.")]
      public void MailboxexistsOnAMissingFolderIsFalse()
      {
         Account recipient = DeliverUnder(
            "require [\"mailbox\", \"fileinto\"];\r\n" +
            "if mailboxexists \"NoSuchFolder\" {\r\n" +
            "  fileinto \"NoSuchFolder\";\r\n" +
            "}\r\n",
            createArchive: false);

         IMAPFolder inbox = recipient.IMAPFolders.get_ItemByName("INBOX");
         CustomAsserts.AssertFolderMessageCount(inbox, 1);
      }

      /// <summary>
      ///    RFC 5490 3.1: the test is true only when EVERY listed mailbox exists.
      ///    One real folder in the list must not carry a missing one.
      /// </summary>
      [Test]
      [Description("mailboxexists with two names, one missing, is false - all listed mailboxes must exist.")]
      public void AllListedMailboxesMustExist()
      {
         Account recipient = DeliverUnder(
            "require [\"mailbox\", \"fileinto\"];\r\n" +
            "if mailboxexists [\"Archive\", \"NoSuchFolder\"] {\r\n" +
            "  fileinto \"Archive\";\r\n" +
            "}\r\n",
            createArchive: true);

         IMAPFolder inbox = recipient.IMAPFolders.get_ItemByName("INBOX");
         CustomAsserts.AssertFolderMessageCount(inbox, 1);

         IMAPFolder archive = recipient.IMAPFolders.get_ItemByName("Archive");
         CustomAsserts.AssertFolderMessageCount(archive, 0);
      }

      /// <summary>
      ///    fileinto :create - the folder does not exist before delivery and must
      ///    afterwards, with the message in it. Checked over IMAP rather than COM,
      ///    because the folder was created during delivery and the question that
      ///    matters is whether a real client can now SELECT it and find the mail.
      /// </summary>
      [Test]
      [Description("fileinto :create files into a folder that did not exist, creating it.")]
      public void CreateFilesIntoAFolderThatDidNotExist()
      {
         Account recipient = DeliverUnder(
            "require [\"mailbox\", \"fileinto\"];\r\n" +
            "fileinto :create \"AutoCreated\";\r\n",
            createArchive: false);

         var imap = new ImapClientSimulator();
         try
         {
            Assert.IsTrue(imap.ConnectAndLogon(recipient.Address, Password), "IMAP logon failed.");
            Assert.IsTrue(imap.SelectFolder("AutoCreated"),
               "'fileinto :create' did not create the folder: a client cannot SELECT it after delivery.");
            Assert.AreEqual(1, imap.GetMessageCount("AutoCreated"),
               "The folder was created but the message is not in it.");
            Assert.AreEqual(0, imap.GetMessageCount("INBOX"),
               "The message was filed into the created folder AND kept in INBOX; it must exist exactly once.");
         }
         finally
         {
            imap.Disconnect();
         }
      }
   }
}
