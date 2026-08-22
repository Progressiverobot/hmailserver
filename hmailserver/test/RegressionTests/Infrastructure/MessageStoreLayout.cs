// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    The message store's two halves - the hm_messages row and the .eml file it
   ///    names - have to keep agreeing with each other. These tests cover the three
   ///    places where they were allowed to drift apart:
   ///    <list type="bullet">
   ///       <item>
   ///          a path inside the public folder was never recognised as belonging to the
   ///          store, so the importer moved and renamed a correctly placed file while
   ///          its row went on naming the old one;
   ///       </item>
   ///       <item>
   ///          the placeholder written for a message whose file has gone resized the
   ///          row but not the cached account size, so the account stayed charged for
   ///          bytes that no longer existed;
   ///       </item>
   ///       <item>
   ///          a message file that could not be written was reported to nobody.
   ///       </item>
   ///    </list>
   /// </summary>
   [TestFixture]
   public class MessageStoreLayout : TestFixtureBase
   {
      [Test]
      [Description("A correctly placed public-folder message file must be recognised as already stored, not moved out from under its row.")]
      public void TestPublicFolderMessageIsRecognisedByItsPartialFileName()
      {
         var publicFolder = _settings.PublicFolders.Add("Share1");
         publicFolder.Save();

         var message = publicFolder.Messages.Add();
         message.Subject = "Public folder message";
         message.Save();

         // {DataDir}\#Public\{2 first GUID chars}\{GUID}.eml - the layout the server
         // itself writes, and the layout the importer has to recognise.
         var storedPath = message.Filename;
         Assert.IsTrue(File.Exists(storedPath), "Expected the public folder message file to exist on disk.");

         // Importing a file that is already in the store and already has a row must be
         // a no-op that answers success, exactly as it is for a message in an account
         // folder (see UtilitiesTests.TestReplaceFullPathWithPartialPath).
         //
         // Before the fix this answered false, and not before moving the file: unable to
         // reduce the path to a partial name, the importer generated a new GUID and
         // renamed the file into a different fan-out folder, then refused the import
         // because the file was not in the data-directory root. The row was left naming
         // the original file, which no longer existed.
         Assert.IsTrue(_application.Utilities.ImportMessageFromFile(storedPath, 0),
            "Importing an already-stored public folder message should succeed as a no-op.");

         Assert.IsTrue(File.Exists(storedPath),
            "The importer must not move a public folder message file that is already correctly placed.");

         var reloadedFolder = _settings.PublicFolders.get_ItemByName("Share1");
         Assert.AreEqual(1, reloadedFolder.Messages.Count, "The import must not have added a second row for the same file.");
         Assert.AreEqual(storedPath, reloadedFolder.Messages[0].Filename,
            "The row must still name the file that is on disk.");
      }

      [Test]
      [Description("Replacing a missing message with a placeholder must release the space the missing message occupied.")]
      public void TestPlaceholderForMissingFileReleasesTheAccountSize()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "missingfile@example.test", "test");

         // Large enough to be visible in Account.Size, which is reported in MB with
         // three decimals: this body is about 0.3 MB, a placeholder is 0.000. Written as
         // ordinary short lines rather than one enormous one, so that nothing in the
         // path is being tested on line length by accident.
         var bodyBuilder = new StringBuilder();
         for (var line = 0; line < 4000; line++)
            bodyBuilder.Append(new string('x', 76)).Append("\r\n");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Message with a body", bodyBuilder.ToString());
         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         var message = account.IMAPFolders.get_ItemByName("Inbox").Messages[0];
         var messageFile = message.Filename;

         // Reading the size here is what puts the account into the size cache, which is
         // the state a live server is always in: the cache is populated by the quota
         // check on the first delivery.
         var sizeBeforeLoss = account.Size;
         Assert.Greater(sizeBeforeLoss, 0.2f, "Expected the delivered message to account for at least 0.2 MB.");

         // The file goes; the row stays. This is what an anti-virus scanner deleting a
         // stored message looks like to the server.
         File.Delete(messageFile);

         // Fetched over IMAP rather than POP3 on purpose: the POP3 helper deletes the
         // message on the way out, and a delete adjusts the cached size too, which would
         // blur the thing being measured.
         var imap = new ImapClientSimulator(account.Address, "test", "INBOX");
         var retrieved = imap.Fetch("1 BODY[1]");
         imap.Logout();

         AssertIsMissingFilePlaceholder(retrieved, messageFile);

         // The row now describes a placeholder of a few hundred bytes. The cached
         // account size has to describe the same thing: before the fix it still held
         // the size of the message that had gone, so the account stayed charged for it
         // until the cache was dropped, and an account near its quota went on refusing
         // mail because of bytes that were no longer on disk.
         var sizeAfterLoss = account.Size;
         Assert.Less(sizeAfterLoss, sizeBeforeLoss / 2,
            "The account size should have dropped to the size of the placeholder.");

         CustomAsserts.AssertReportedError("Message retrieval failed because message file");
      }

      [Test]
      [Description("A message file that cannot be written must be reported rather than silently skipped.")]
      public void TestFailedRewriteOfMessageFileIsReported()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "readonly@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Original subject", "Original body");
         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         var message = account.IMAPFolders.get_ItemByName("Inbox").Messages[0];
         var messageFile = message.Filename;
         var originalContent = File.ReadAllText(messageFile);

         // A read-only file is the cheapest way to make the write fail for real: the
         // stored message is opened for writing, which is refused, so the failure
         // happens exactly where a full disk or a revoked permission would put it.
         File.SetAttributes(messageFile, FileAttributes.ReadOnly);

         try
         {
            message.Subject = "Rewritten subject";
            Assert.Throws<COMException>(() => message.Save(),
               "Saving a message whose file cannot be written must fail rather than report success.");
         }
         finally
         {
            File.SetAttributes(messageFile, FileAttributes.Normal);
         }

         Assert.AreEqual(originalContent, File.ReadAllText(messageFile),
            "A failed rewrite must leave the stored message exactly as it was.");

         // Before the fix the failure was reported nowhere: MessageData::Write dropped
         // the result of the write, stamped the message with whatever size the file
         // happened to have, and returned. Most of its callers - rules, the virus
         // scanner, forwarding - ignore its return value, so nothing at all was logged.
         CustomAsserts.AssertReportedError("Failed to write the message file");
      }

      // The placeholder body is a configured server message with a macro in it, so the
      // expected text has to be built from the template rather than hard-coded.
      private void AssertIsMissingFilePlaceholder(string retrievedMessage, string expectedMessageFile)
      {
         var template = _settings.ServerMessages.get_ItemByName("MESSAGE_FILE_MISSING").Text;

         Assert.IsTrue(retrievedMessage.Contains(template.Replace("%MACRO_FILE%", expectedMessageFile)),
            "Expected the retrieved message to be the missing-file placeholder. Actual: " + retrievedMessage);
      }
   }
}
