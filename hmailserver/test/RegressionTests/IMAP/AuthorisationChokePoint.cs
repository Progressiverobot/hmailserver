// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.IMAP
{
   /// <summary>
   ///    Every "may this account do this to that folder" is one decision, ACLManager's,
   ///    and the enforcement switch has exactly two meanings there. For folder access
   ///    - the caller's own tree and the public tree - "off" means everything is
   ///    allowed, the historical meaning ("public folders are open"), which every IMAP
   ///    command applied and which delivery and rules now apply too instead of asking
   ///    ACL rows nobody is enforcing. For a right one account grants another - the p
   ///    right that lets somebody send as the owner, the l right that makes a folder
   ///    observable under #Users - "off" means nothing is granted, because there is no
   ///    decision-maker left and "ACL disabled" must never become "everybody may act
   ///    for everybody". Both meanings are pinned here, each with its switch turned
   ///    back on in the same test so the two answers are seen side by side.
   /// </summary>
   [TestFixture]
   public class AuthorisationChokePoint : TestFixtureBase
   {
      private const string Alice = "alice@example.test";
      private const string Bob = "bob@example.test";
      private const string Carol = "carol@example.test";

      [OneTimeSetUp]
      public void TurnTheSenderCheckOn()
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

      [TearDown]
      public void EnforceAgain()
      {
         _settings.IMAPACLEnabled = true;
      }

      private Account Add(string address)
      {
         return SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "test");
      }

      // "" when the message was accepted; otherwise the refusal.
      private static string SendAuthenticated(string user, string from, string to)
      {
         try
         {
            new SmtpClientSimulator().Send(false, user, "test", from, to, "Choke point", "Body.", out string ignored);
            return "";
         }
         catch (DeliveryFailedException ex)
         {
            return ex.Message;
         }
      }

      [Test]
      [Description("Folder access: with enforcement off a rule files into a public folder that grants it nothing - the same folder IMAP has always opened when the switch is off - and turned back on, the same rule is refused for want of the insert right.")]
      public void WithEnforcementOffARuleFilesIntoAPublicFolder()
      {
         Account account = Add("filer@example.test");

         var open = _settings.PublicFolders.Add("Open");
         open.Save();

         Rule rule = account.Rules.Add();
         rule.Name = "file into the public folder";
         rule.Active = true;

         RuleCriteria criterion = rule.Criterias.Add();
         criterion.UsePredefined = true;
         criterion.PredefinedField = eRulePredefinedField.eFTSubject;
         criterion.MatchType = eRuleMatchType.eMTContains;
         criterion.MatchValue = "shared";
         criterion.Save();

         RuleAction action = rule.Actions.Add();
         action.Type = eRuleActionType.eRAMoveToImapFolder;
         action.IMAPFolder = "#public.Open";
         action.Save();
         rule.Save();

         _settings.IMAPACLEnabled = false;
         SmtpClientSimulator.StaticSend(Carol, account.Address, "shared, first", "Filed while enforcement is off.");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "#public.Open", 1);
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "INBOX", 0);

         _settings.IMAPACLEnabled = true;
         SmtpClientSimulator.StaticSend(Carol, account.Address, "shared, second", "Refused while enforcement is on.");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "INBOX", 1);
      }

      [Test]
      [Description("A granted right: with enforcement off the post right on the owner's INBOX grants nothing to Send-As, and turned back on the same grant does.")]
      public void WithEnforcementOffAGrantedPostRightGrantsNothing()
      {
         Account alice = Add(Alice);
         Account bob = Add(Bob);
         Add(Carol);

         SmtpClientSimulator.StaticSend(Carol, Bob, "Seed", "Creates the INBOX.");
         Pop3ClientSimulator.AssertMessageCount(Bob, "test", 1);

         var inbox = bob.IMAPFolders.get_ItemByName("INBOX");
         var grant = inbox.Permissions.Add();
         grant.PermissionType = eACLPermissionType.ePermissionTypeUser;
         grant.PermissionAccountID = alice.ID;
         grant.set_Permission(eACLPermission.ePermissionPost, true);
         grant.Save();

         _settings.IMAPACLEnabled = false;
         string refused = SendAuthenticated(Alice, Bob, Carol);
         StringAssert.Contains("550", refused, "With enforcement off nothing is granted, so Alice may not send as Bob.");
         StringAssert.Contains("Sender address rejected", refused);

         _settings.IMAPACLEnabled = true;
         ClassicAssert.AreEqual("", SendAuthenticated(Alice, Bob, Carol), "With enforcement on the same grant lets Alice send as Bob.");
         Pop3ClientSimulator.AssertMessageCount(Carol, "test", 1);
      }
   }
}
