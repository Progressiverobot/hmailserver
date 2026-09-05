// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.Security
{
   /// <summary>
   ///    SmtpAuthenticatedSenderCheck: what an authenticated session may put in
   ///    MAIL FROM, and Send-As for a shared mailbox.
   ///
   ///    Until 5 September 2026 nothing constrained this at all - an authenticated
   ///    account could send as any address whatsoever - which is why "Send-As" for a
   ///    shared mailbox was not a missing capability but a missing control. With
   ///    the check on, MAIL FROM must be the account's own address, an alias that
   ///    resolves to it, or another mailbox whose owner has granted the account the
   ///    post (p) right on their INBOX - the one RFC 4314 right whose subject is
   ///    sending mail on a mailbox's behalf, spelled by any IMAP client's SETACL.
   ///
   ///    The check is off by default and is turned on for this fixture only; the
   ///    hundreds of tests elsewhere that send as whatever address they like are the
   ///    proof of the default.
   /// </summary>
   [TestFixture]
   public class AuthenticatedSenderCheck : TestFixtureBase
   {
      private const string Alice = "alice@example.test";
      private const string Bob = "bob@example.test";
      private const string Carol = "carol@example.test";

      [OneTimeSetUp]
      public void TurnTheCheckOn()
      {
         ServerIniFile.SetSetting("SmtpAuthenticatedSenderCheck", "1");
         RestartServerAndReacquireCom();
      }

      [OneTimeTearDown]
      public void TurnItOff()
      {
         ServerIniFile.SetSetting("SmtpAuthenticatedSenderCheck", null);
         RestartServerAndReacquireCom();
      }

      private Account Add(string address)
      {
         return SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "test");
      }

      // "" when the message was accepted and delivered; otherwise the refusal.
      private static string SendAuthenticated(string user, string from, string to)
      {
         try
         {
            new SmtpClientSimulator().Send(false, user, "test", from, to, "Sender check", "Body.", out string ignored);
            return "";
         }
         catch (DeliveryFailedException ex)
         {
            return ex.Message;
         }
      }

      // The test client says HELO rather than EHLO, so the reply carries no
      // enhanced status code (RFC 2034: only after ENHANCEDSTATUSCODES was
      // offered); the permanent code and the reason are what it can see.
      private static void AssertRefusedWith550(string outcome)
      {
         StringAssert.Contains("550", outcome, "The sender should have been refused with a permanent 550.");
         StringAssert.Contains("Sender address rejected", outcome, "The refusal must say what was wrong.");
      }

      [Test]
      [Description("The account's own address is always accepted")]
      public void TheAccountsOwnAddressIsAccepted()
      {
         Add(Alice);
         Add(Bob);

         ClassicAssert.AreEqual("", SendAuthenticated(Alice, Alice, Bob));
         Pop3ClientSimulator.AssertMessageCount(Bob, "test", 1);
      }

      [Test]
      [Description("Another account's address is refused with 550 5.7.1 before any content is accepted, and nothing is delivered")]
      public void AnotherAccountsAddressIsRefused()
      {
         Add(Alice);
         Add(Bob);
         Add(Carol);

         AssertRefusedWith550(SendAuthenticated(Alice, Bob, Carol));
         Pop3ClientSimulator.AssertMessageCount(Carol, "test", 0);
      }

      [Test]
      [Description("An address nobody owns is refused too - the check is about ownership, not about local versus remote")]
      public void AnAddressNobodyOwnsIsRefused()
      {
         Add(Alice);
         Add(Bob);

         AssertRefusedWith550(SendAuthenticated(Alice, "someone@elsewhere.test", Bob));
      }

      [Test]
      [Description("An alias that resolves to the account is the account's own address")]
      public void AnAliasThatResolvesToTheAccountIsAccepted()
      {
         Add(Alice);
         Add(Bob);

         var alias = _domain.Aliases.Add();
         alias.Name = "sales@example.test";
         alias.Value = Alice;
         alias.Active = true;
         alias.Save();

         ClassicAssert.AreEqual("", SendAuthenticated(Alice, "sales@example.test", Bob));

         string delivered = Pop3ClientSimulator.AssertGetFirstMessageText(Bob, "test");
         StringAssert.Contains("Return-Path: <sales@example.test>", delivered);
      }

      [Test]
      [Description("The null sender is always allowed: an authenticated client may send a bounce")]
      public void TheNullSenderIsAccepted()
      {
         Add(Alice);
         Add(Bob);

         ClassicAssert.AreEqual("", SendAuthenticated(Alice, "", Bob));
         Pop3ClientSimulator.AssertMessageCount(Bob, "test", 1);
      }

      [Test]
      [Description("Send-As: the post (p) right on the owner's INBOX lets the grantee send as the owner, and the message carries the owner's Return-Path")]
      public void SendAsIsGrantedByThePostRightOnTheOwnersInbox()
      {
         Account alice = Add(Alice);
         Account bob = Add(Bob);
         Add(Carol);

         // The INBOX exists once something has been delivered to it.
         SmtpClientSimulator.StaticSend(Carol, Bob, "Seed", "Creates the INBOX.");
         Pop3ClientSimulator.AssertMessageCount(Bob, "test", 1);

         var inbox = bob.IMAPFolders.get_ItemByName("INBOX");
         var grant = inbox.Permissions.Add();
         grant.PermissionType = eACLPermissionType.ePermissionTypeUser;
         grant.PermissionAccountID = alice.ID;
         grant.set_Permission(eACLPermission.ePermissionPost, true);
         grant.Save();

         ClassicAssert.AreEqual("", SendAuthenticated(Alice, Bob, Carol),
            "With the post right granted on Bob's INBOX, Alice may send as Bob.");

         string delivered = Pop3ClientSimulator.AssertGetFirstMessageText(Carol, "test");
         StringAssert.Contains("Return-Path: <" + Bob + ">", delivered,
            "The envelope sender the recipient sees is the granted address.");
      }

      [Test]
      [Description("The post right on some other folder of the owner's grants nothing - a right on a folder is a right on that folder")]
      public void ThePostRightOnAnotherFolderDoesNotGrantSendAs()
      {
         Account alice = Add(Alice);
         Account bob = Add(Bob);
         Add(Carol);

         var shared = bob.IMAPFolders.Add("Shared");
         shared.Save();

         var grant = shared.Permissions.Add();
         grant.PermissionType = eACLPermissionType.ePermissionTypeUser;
         grant.PermissionAccountID = alice.ID;
         grant.set_Permission(eACLPermission.ePermissionPost, true);
         grant.Save();

         AssertRefusedWith550(SendAuthenticated(Alice, Bob, Carol));
      }

      [Test]
      [Description("An unauthenticated session is not subject to the check - it is a control on what an account of ours can do, not on what the server accepts")]
      public void AnUnauthenticatedSessionIsNotSubjectToTheCheck()
      {
         Add(Bob);

         SmtpClientSimulator.StaticSend("anyone@elsewhere.test", Bob, "Unauthenticated", "Accepted from the local range as before.");
         Pop3ClientSimulator.AssertMessageCount(Bob, "test", 1);
      }
   }
}
