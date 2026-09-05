// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Reflection;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.Sieve
{
   /// <summary>
   ///    The "envelope" extension (RFC 5228 5.4), proven the way the ManageSieve
   ///    capability line demands before a name goes into it: through real
   ///    deliveries, with every assertion on where a message was FILED.
   ///
   ///    The point of envelope over address is that it reads the SMTP envelope and
   ///    not the headers, so each positive test here is paired with the header that
   ///    would have given the other answer: a From header naming someone else, a To
   ///    header naming a list. A script that quietly read the header instead would
   ///    file into the "Wrong" folder these tests also create, and the count there
   ///    is asserted to stay at zero.
   /// </summary>
   [TestFixture]
   public class SieveEnvelopeDelivery : TestFixtureBase
   {
      private const string Password = "secret";

      private static void SetScript(Account account, string script)
      {
         // Late-bound, as the sibling Sieve fixtures are: the test must not depend
         // on the registered type library having been regenerated after an IDL change.
         account.GetType().InvokeMember(
            "SieveScript", BindingFlags.SetProperty, null, account, new object[] { script });
      }

      private Account Recipient(string script, params string[] folders)
      {
         Account recipient = SingletonProvider<TestSetup>.Instance.AddAccount(
            _domain, "sieve-envelope@example.test", Password);

         foreach (string folder in folders)
            recipient.IMAPFolders.Add(folder);

         SetScript(recipient, script);
         return recipient;
      }

      private static void Deliver(string envelopeFrom, string envelopeTo, string headerFrom, string headerTo)
      {
         SmtpClientSimulator.StaticSendRaw(envelopeFrom, envelopeTo,
            "From: " + headerFrom + "\r\n" +
            "To: " + headerTo + "\r\n" +
            "Subject: Envelope test\r\n" +
            "\r\n" +
            "Body text.\r\n");

         // Drain the queue before reading any counts, so no assertion races the
         // delivery it is asking about.
         CustomAsserts.AssertRecipientsInDeliveryQueue(0);
      }

      private static void AssertFiled(Account recipient, string folder, int count)
      {
         IMAPFolder target = recipient.IMAPFolders.get_ItemByName(folder);
         CustomAsserts.AssertFolderMessageCount(target, count);
      }

      [Test]
      [Description("envelope \"from\" is the MAIL FROM address, not the From header - the header names someone else and the message is filed by the envelope.")]
      public void FromIsTheEnvelopeSenderNotTheHeader()
      {
         Account recipient = Recipient(
            "require [\"envelope\", \"fileinto\"];\r\n" +
            "if envelope :is \"from\" \"impostor@elsewhere.test\" {\r\n" +
            "  fileinto \"Wrong\";\r\n" +
            "} elsif envelope :is \"from\" \"boss@example.test\" {\r\n" +
            "  fileinto \"Boss\";\r\n" +
            "}\r\n",
            "Boss", "Wrong");

         Deliver("boss@example.test", recipient.Address, "impostor@elsewhere.test", recipient.Address);

         AssertFiled(recipient, "Boss", 1);
         AssertFiled(recipient, "Wrong", 0);
         AssertFiled(recipient, "INBOX", 0);
      }

      [Test]
      [Description("envelope \"to\" is the RCPT TO address, not the To header - a message addressed to a list in its header is still recognised as delivered to this mailbox.")]
      public void ToIsTheEnvelopeRecipientNotTheHeader()
      {
         Account recipient = Recipient(
            "require [\"envelope\", \"fileinto\"];\r\n" +
            "if envelope :is \"to\" \"everyone@example.test\" {\r\n" +
            "  fileinto \"Wrong\";\r\n" +
            "} elsif envelope :is \"to\" \"sieve-envelope@example.test\" {\r\n" +
            "  fileinto \"Direct\";\r\n" +
            "}\r\n",
            "Direct", "Wrong");

         Deliver("boss@example.test", recipient.Address, "boss@example.test", "everyone@example.test");

         AssertFiled(recipient, "Direct", 1);
         AssertFiled(recipient, "Wrong", 0);
         AssertFiled(recipient, "INBOX", 0);
      }

      [Test]
      [Description(":domain on the envelope sender matches the part after the @, so one rule files everything from a domain.")]
      public void DomainPartOfTheSenderMatches()
      {
         Account recipient = Recipient(
            "require [\"envelope\", \"fileinto\"];\r\n" +
            "if envelope :is :domain \"from\" \"example.test\" {\r\n" +
            "  fileinto \"Internal\";\r\n" +
            "}\r\n",
            "Internal");

         Deliver("anyone@example.test", recipient.Address, "anyone@example.test", recipient.Address);

         AssertFiled(recipient, "Internal", 1);
         AssertFiled(recipient, "INBOX", 0);
      }

      /// <summary>
      ///    A bounce has a null return path, MAIL FROM:&lt;&gt;. RFC 5228 5.4 says the
      ///    envelope sender of such a message is the empty string, which is a value
      ///    and not an absence, so envelope :is "from" "" is how a script recognises
      ///    a bounce - and filing bounces out of the way is the first thing anyone
      ///    writes an envelope rule for.
      /// </summary>
      [Test]
      [Description("A null sender (MAIL FROM:<>) is the empty envelope sender, so a bounce can be filed by matching \"\".")]
      public void ANullSenderIsTheEmptyString()
      {
         Account recipient = Recipient(
            "require [\"envelope\", \"fileinto\"];\r\n" +
            "if envelope :is \"from\" \"\" {\r\n" +
            "  fileinto \"Bounces\";\r\n" +
            "}\r\n",
            "Bounces");

         Deliver("", recipient.Address, "MAILER-DAEMON@example.test", recipient.Address);

         AssertFiled(recipient, "Bounces", 1);
         AssertFiled(recipient, "INBOX", 0);
      }

      /// <summary>
      ///    The negative control the others depend on: a rule for a sender who did
      ///    not send leaves the message where implicit keep puts it. An evaluator
      ///    that answered true for every envelope test passes every positive test
      ///    above and fails only this one.
      /// </summary>
      [Test]
      [Description("A rule for a different envelope sender does not fire - the message stays in INBOX.")]
      public void ARuleForAnotherSenderDoesNotFire()
      {
         Account recipient = Recipient(
            "require [\"envelope\", \"fileinto\"];\r\n" +
            "if envelope :is \"from\" \"nobody@elsewhere.test\" {\r\n" +
            "  fileinto \"Wrong\";\r\n" +
            "}\r\n",
            "Wrong");

         Deliver("boss@example.test", recipient.Address, "boss@example.test", recipient.Address);

         AssertFiled(recipient, "Wrong", 0);
         AssertFiled(recipient, "INBOX", 1);
      }
   }
}
