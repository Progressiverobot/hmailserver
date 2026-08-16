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
   ///    The duplicate test (RFC 7352), asserted through real deliveries: the same
   ///    Message-ID delivered twice, with the first landing in INBOX and only the
   ///    second where the script files duplicates.
   ///
   ///    The seen-store fails OPEN - the common script discards on "duplicate", so
   ///    a wrong "duplicate" destroys mail where a wrong "new" delivers a copy
   ///    twice - and its per-account file lives in the account's Sieve directory,
   ///    which is why every test here uses a fresh account: the store outlives a
   ///    test run on purpose (a restart must not forget what was seen), so reusing
   ///    an address would leak one test's records into the next.
   /// </summary>
   [TestFixture]
   public class SieveDuplicateDelivery : TestFixtureBase
   {
      private const string Password = "secret";
      private const string TargetFolder = "Duplicates";

      private static int accountSequence_;

      private static void SetScript(Account account, string script)
      {
         account.GetType().InvokeMember(
            "SieveScript", BindingFlags.SetProperty, null, account, new object[] { script });
      }

      private Account NewRecipient(string script)
      {
         accountSequence_++;
         Account recipient = SingletonProvider<TestSetup>.Instance.AddAccount(
            _domain, "sieve-dup-" + accountSequence_ + "@example.test", Password);

         recipient.IMAPFolders.Add(TargetFolder);
         SetScript(recipient, script);
         return recipient;
      }

      private void SendWithHeaders(Account recipient, string extraHeaders, string body = "Body text.")
      {
         string raw =
            "From: sieve-dup-sender@example.test\r\n" +
            "To: " + recipient.Address + "\r\n" +
            "Subject: Duplicate test\r\n" +
            extraHeaders +
            "\r\n" +
            body + "\r\n";

         var smtp = new SmtpClientSimulator();
         smtp.SendRaw("sieve-dup-sender@example.test", recipient.Address, raw);
         CustomAsserts.AssertRecipientsInDeliveryQueue(0);
      }

      private static string FileDuplicatesInto(string test)
      {
         return "require [\"duplicate\", \"fileinto\"];\r\n" +
                "if " + test + " {\r\n" +
                "  fileinto \"" + TargetFolder + "\";\r\n" +
                "}\r\n";
      }

      private void AssertCounts(Account recipient, int expectedInbox, int expectedDuplicates)
      {
         IMAPFolder inbox = recipient.IMAPFolders.get_ItemByName("INBOX");
         CustomAsserts.AssertFolderMessageCount(inbox, expectedInbox);

         IMAPFolder duplicates = recipient.IMAPFolders.get_ItemByName(TargetFolder);
         CustomAsserts.AssertFolderMessageCount(duplicates, expectedDuplicates);
      }

      [Test]
      [Description("The same Message-ID twice: the first is not a duplicate, the second is.")]
      public void TheSecondCopyOfAMessageIdIsADuplicate()
      {
         Account recipient = NewRecipient(FileDuplicatesInto("duplicate"));

         SendWithHeaders(recipient, "Message-ID: <same-id-1@example.test>\r\n");
         SendWithHeaders(recipient, "Message-ID: <same-id-1@example.test>\r\n");

         AssertCounts(recipient, expectedInbox: 1, expectedDuplicates: 1);
      }

      /// <summary>
      ///    The negative control: two different Message-IDs are two different
      ///    messages, and neither may be called a duplicate. An implementation
      ///    answering "seen" for everything passes the positive test and fails
      ///    here - in the direction that destroys mail under the common
      ///    discard-duplicates script.
      /// </summary>
      [Test]
      [Description("Two different Message-IDs are not duplicates of each other - the control the rest depend on.")]
      public void DifferentMessageIdsAreNotDuplicates()
      {
         Account recipient = NewRecipient(FileDuplicatesInto("duplicate"));

         SendWithHeaders(recipient, "Message-ID: <first-id@example.test>\r\n");
         SendWithHeaders(recipient, "Message-ID: <second-id@example.test>\r\n");

         AssertCounts(recipient, expectedInbox: 2, expectedDuplicates: 0);
      }

      [Test]
      [Description("A message with no Message-ID is never a duplicate and is not tracked (RFC 7352 3).")]
      public void NoMessageIdMeansNeverADuplicate()
      {
         Account recipient = NewRecipient(FileDuplicatesInto("duplicate"));

         SendWithHeaders(recipient, "");
         SendWithHeaders(recipient, "");

         AssertCounts(recipient, expectedInbox: 2, expectedDuplicates: 0);
      }

      [Test]
      [Description(":uniqueid tracks the given value, so two different Message-IDs can still be duplicates.")]
      public void UniqueidTracksTheGivenValueInstead()
      {
         Account recipient = NewRecipient(FileDuplicatesInto("duplicate :uniqueid \"fixed-token\""));

         SendWithHeaders(recipient, "Message-ID: <uid-a@example.test>\r\n");
         SendWithHeaders(recipient, "Message-ID: <uid-b@example.test>\r\n");

         AssertCounts(recipient, expectedInbox: 1, expectedDuplicates: 1);
      }

      [Test]
      [Description(":header tracks a named header's value; a message without that header is never a duplicate.")]
      public void HeaderTracksTheNamedHeader()
      {
         Account recipient = NewRecipient(FileDuplicatesInto("duplicate :header \"X-Batch-Token\""));

         SendWithHeaders(recipient, "Message-ID: <h-a@example.test>\r\nX-Batch-Token: batch-42\r\n");
         SendWithHeaders(recipient, "Message-ID: <h-b@example.test>\r\nX-Batch-Token: batch-42\r\n");
         SendWithHeaders(recipient, "Message-ID: <h-c@example.test>\r\n");

         // Two batch-42 messages: first new, second duplicate. The third has no
         // token at all, so it is never a duplicate.
         AssertCounts(recipient, expectedInbox: 2, expectedDuplicates: 1);
      }

      /// <summary>
      ///    RFC 7352 3.2: different :handle values track independently - the same
      ///    Message-ID seen by one handle is still new to another.
      /// </summary>
      [Test]
      [Description("Different :handle values track independently.")]
      public void HandlesTrackIndependently()
      {
         Account recipient = NewRecipient(
            "require [\"duplicate\", \"fileinto\"];\r\n" +
            "if duplicate :handle \"first-window\" {\r\n" +
            "  fileinto \"" + TargetFolder + "\";\r\n" +
            "}\r\n");

         SendWithHeaders(recipient, "Message-ID: <handles-1@example.test>\r\n");

         // Second delivery, same Message-ID, but the script now tracks under a
         // DIFFERENT handle - so this is that handle's first sighting and must
         // stay in INBOX.
         SetScript(recipient,
            "require [\"duplicate\", \"fileinto\"];\r\n" +
            "if duplicate :handle \"second-window\" {\r\n" +
            "  fileinto \"" + TargetFolder + "\";\r\n" +
            "}\r\n");

         SendWithHeaders(recipient, "Message-ID: <handles-1@example.test>\r\n");

         AssertCounts(recipient, expectedInbox: 2, expectedDuplicates: 0);
      }
   }
}
