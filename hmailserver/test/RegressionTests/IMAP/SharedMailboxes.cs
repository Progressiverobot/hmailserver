// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using hMailServer;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.IMAP
{
   /// <summary>
   ///    Shared and delegated mailboxes: another account's folders reached through
   ///    the RFC 2342 "Other Users" namespace, "#Users.owner@domain.folder", gated
   ///    per folder on the RFC 4314 ACL the owner's folder carries.
   ///
   ///    Two decisions these tests pin on purpose:
   ///
   ///    1. A caller without the lookup ("l") right must not be able to LEARN
   ///       anything: the folder is absent from LIST, and SELECT/EXAMINE/STATUS of
   ///       the real path answer byte-for-byte the same as for a folder - or an
   ///       account - that does not exist. The negative tests here are the point
   ///       of the feature; the failure mode being defended against is reading
   ///       somebody else's mail, or proving it exists.
   ///
   ///    2. \Seen on a shared folder is SHARED state, not per-user. Flags live on
   ///       the message row, so per-user \Seen would need a schema change; instead
   ///       the RFC 4314 "s" right decides whether a delegate may touch the shared
   ///       flag at all. See the design record in ACLManager.h. The test below
   ///       that shows the owner seeing the delegate's \Seen is asserting the
   ///       DOCUMENTED behaviour, not tolerating a bug.
   /// </summary>
   [TestFixture]
   public class SharedMailboxes : TestFixtureBase
   {
      // RFC 4314 rights as stored in hm_acl.aclvalue (ACLPermission::ePermission).
      private const int RightLookup = 1;        // l
      private const int RightRead = 2;          // r
      private const int RightWriteSeen = 4;     // s
      private const int RightInsert = 16;       // i

      /// <summary>
      ///    Grants rights on one of an account's own folders to another account by
      ///    inserting the hm_acl row directly. Deliberate: both grant surfaces the
      ///    server exposes today - IMAP SETACL and the COM Permissions collection -
      ///    refuse account-level folders, so until one of them is opened up for
      ///    folder owners, the database is the only way a delegation can exist.
      ///    The server reads hm_acl fresh on every access decision, so the grant
      ///    takes effect immediately, without a restart.
      /// </summary>
      private void GrantRightsOnFolder(IMAPFolder folder, Account delegateAccount, int rightsMask)
      {
         SingletonProvider<TestSetup>.Instance.GetApp().Database.ExecuteSQL(
            string.Format(
               "insert into hm_acl (aclsharefolderid, aclpermissiontype, aclpermissiongroupid, aclpermissionaccountid, aclvalue) " +
               "values ({0}, 0, 0, {1}, {2})",
               folder.ID, delegateAccount.ID, rightsMask));
      }

      private void UpdateRightsOnFolder(IMAPFolder folder, Account delegateAccount, int rightsMask)
      {
         SingletonProvider<TestSetup>.Instance.GetApp().Database.ExecuteSQL(
            string.Format(
               "update hm_acl set aclvalue = {0} where aclsharefolderid = {1} and aclpermissionaccountid = {2}",
               rightsMask, folder.ID, delegateAccount.ID));
      }

      /// <summary>
      ///    Creates the owner account with one message in its INBOX (which also
      ///    materializes the INBOX folder row the grant needs to point at).
      /// </summary>
      private Account CreateOwnerWithMessage(string address, string subject, string body)
      {
         var owner = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "test");

         SmtpClientSimulator.StaticSend("sender@example.test", owner.Address, subject, body);
         CustomAsserts.AssertFolderMessageCount(owner.IMAPFolders.get_ItemByName("INBOX"), 1);

         return owner;
      }

      [Test]
      public void NamespaceCommandAdvertisesOtherUsersNamespace()
      {
         var publicFolderName = _settings.IMAPPublicFolderName;

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "nsuser@example.test", "test");

         var simulator = new ImapClientSimulator();
         Assert.IsTrue(simulator.ConnectAndLogon(account.Address, "test"));

         var result = simulator.Send("A01 NAMESPACE");

         var expected = "* NAMESPACE ((\"\" \".\")) ((\"#Users\" \".\")) ((\"" + publicFolderName + "\" \".\"))";
         Assert.IsTrue(result.Contains(expected), result);

         simulator.Disconnect();
      }

      [Test]
      public void DelegateWithLookupAndReadRightsCanListSelectAndReadMessage()
      {
         var owner = CreateOwnerWithMessage("shareowner@example.test", "SharedSubject", "Shared mailbox body");
         var delegateAccount = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "delegate@example.test", "test");

         GrantRightsOnFolder(owner.IMAPFolders.get_ItemByName("INBOX"), delegateAccount, RightLookup | RightRead);

         var sharedPath = "#Users.shareowner@example.test.INBOX";

         var simulator = new ImapClientSimulator();
         Assert.IsTrue(simulator.ConnectAndLogon(delegateAccount.Address, "test"));

         // LIST shows the namespace root, the owner node, and the shared folder -
         // all three, so a client can walk the hierarchy down to the mailbox.
         var listResult = simulator.List();
         Assert.IsTrue(listResult.Contains("\"#Users\"\r\n"), listResult);
         Assert.IsTrue(listResult.Contains("\"#Users.shareowner@example.test\"\r\n"), listResult);
         Assert.IsTrue(listResult.Contains("\"" + sharedPath + "\""), listResult);

         // With l and r but nothing else the mailbox is readable but read-only,
         // and PERMANENTFLAGS is empty: this delegate may change no flag at all.
         string selectResult;
         Assert.IsTrue(simulator.SelectFolder(sharedPath, out selectResult), selectResult);
         Assert.IsTrue(selectResult.Contains("[READ-ONLY]"), selectResult);
         Assert.IsFalse(selectResult.Contains("[READ-WRITE]"), selectResult);
         Assert.IsTrue(selectResult.Contains("[PERMANENTFLAGS ()]"), selectResult);

         var fetchResult = simulator.Fetch("1 BODY[]");
         Assert.IsTrue(fetchResult.Contains("Shared mailbox body"), fetchResult);

         simulator.Disconnect();

         // Without the "s" right the delegate's FETCH BODY[] must not have set
         // the (shared) \Seen flag: the owner's unread state is untouched.
         var ownerSimulator = new ImapClientSimulator();
         Assert.IsTrue(ownerSimulator.ConnectAndLogon(owner.Address, "test"));
         Assert.IsTrue(ownerSimulator.SelectFolder("INBOX"));
         var ownerFlags = ownerSimulator.GetFlags(1);
         Assert.IsFalse(ownerFlags.Contains("\\Seen"), ownerFlags);
         ownerSimulator.Disconnect();
      }

      [Test]
      public void DelegateWithoutRightsCannotSeeTheFolderOrProveItExists()
      {
         var owner = CreateOwnerWithMessage("hiddenowner@example.test", "PrivateSubject", "Private body");
         var trustedDelegate = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "trusted@example.test", "test");
         var outsider = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "outsider@example.test", "test");

         // The folder IS shared - just not with the outsider. This is the
         // stronger form of the test: the namespace is live, LIST has something
         // to show somebody, and the outsider must still see none of it.
         GrantRightsOnFolder(owner.IMAPFolders.get_ItemByName("INBOX"), trustedDelegate, RightLookup | RightRead);

         var simulator = new ImapClientSimulator();
         Assert.IsTrue(simulator.ConnectAndLogon(outsider.Address, "test"));

         var listResult = simulator.List();
         Assert.IsFalse(listResult.Contains("hiddenowner@example.test"), listResult);
         Assert.IsFalse(listResult.Contains("#Users"), listResult);

         // SELECT of the real-but-forbidden folder, of a folder that does not
         // exist under a real account, and of an account that does not exist at
         // all must be byte-for-byte identical: any difference is an oracle for
         // which mailboxes and accounts are real.
         var realFolderResponse = simulator.SendSingleCommand("A15 SELECT #Users.hiddenowner@example.test.INBOX");
         var missingFolderResponse = simulator.SendSingleCommand("A15 SELECT #Users.hiddenowner@example.test.NoSuchFolder");
         var missingAccountResponse = simulator.SendSingleCommand("A15 SELECT #Users.ghost@example.test.INBOX");

         Assert.AreEqual(missingFolderResponse, realFolderResponse);
         Assert.AreEqual(missingAccountResponse, realFolderResponse);

         // STATUS goes through the same resolution and must be just as blind.
         var realStatusResponse = simulator.Status("#Users.hiddenowner@example.test.INBOX", "MESSAGES");
         var missingStatusResponse = simulator.Status("#Users.hiddenowner@example.test.NoSuchFolder", "MESSAGES");
         Assert.AreEqual(missingStatusResponse, realStatusResponse);

         simulator.Disconnect();

         // And the grant that does exist still works - proving the outsider's
         // blindness above was the ACL, not a broken namespace.
         var trustedSimulator = new ImapClientSimulator();
         Assert.IsTrue(trustedSimulator.ConnectAndLogon(trustedDelegate.Address, "test"));
         Assert.IsTrue(trustedSimulator.SelectFolder("#Users.hiddenowner@example.test.INBOX"));
         trustedSimulator.Disconnect();
      }

      [Test]
      public void DelegateWithReadOnlyRightsIsRefusedAppend()
      {
         var owner = CreateOwnerWithMessage("appendowner@example.test", "ExistingSubject", "Existing body");
         var delegateAccount = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "appenddelegate@example.test", "test");

         var ownerInbox = owner.IMAPFolders.get_ItemByName("INBOX");
         GrantRightsOnFolder(ownerInbox, delegateAccount, RightLookup | RightRead);

         var sharedPath = "#Users.appendowner@example.test.INBOX";
         var messageData = "From: appenddelegate@example.test\r\n\r\nDelegated append body";

         var simulator = new ImapClientSimulator();
         Assert.IsTrue(simulator.ConnectAndLogon(delegateAccount.Address, "test"));

         // The literal size is computed from the payload, never written by hand.
         var appendCommand = "A01 APPEND \"" + sharedPath + "\" {" + messageData.Length + "}";

         var refusedResult = simulator.SendSingleCommandWithLiteral(appendCommand, messageData);
         Assert.IsTrue(refusedResult.Contains("ACL: Insert permission denied"), refusedResult);
         CustomAsserts.AssertFolderMessageCount(ownerInbox, 1);

         // Adding the "i" right flips the same command to success - rights are
         // read live from hm_acl, per decision, with no restart.
         UpdateRightsOnFolder(ownerInbox, delegateAccount, RightLookup | RightRead | RightInsert);

         var allowedResult = simulator.SendSingleCommandWithLiteral(appendCommand, messageData);
         Assert.IsTrue(allowedResult.Contains("A01 OK"), allowedResult);
         CustomAsserts.AssertFolderMessageCount(ownerInbox, 2);

         simulator.Disconnect();
      }

      [Test]
      public void SeenFlagOnSharedFolderIsSharedWithTheOwner()
      {
         var owner = CreateOwnerWithMessage("seenowner@example.test", "SeenSubject", "Seen body");
         var readOnlyDelegate = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "seenreadonly@example.test", "test");
         var seenDelegate = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "seenwriter@example.test", "test");

         var ownerInbox = owner.IMAPFolders.get_ItemByName("INBOX");
         GrantRightsOnFolder(ownerInbox, readOnlyDelegate, RightLookup | RightRead);
         GrantRightsOnFolder(ownerInbox, seenDelegate, RightLookup | RightRead | RightWriteSeen);

         var sharedPath = "#Users.seenowner@example.test.INBOX";

         // Without the "s" right, STORE \Seen is refused.
         var readOnlySimulator = new ImapClientSimulator();
         Assert.IsTrue(readOnlySimulator.ConnectAndLogon(readOnlyDelegate.Address, "test"));
         Assert.IsTrue(readOnlySimulator.SelectFolder(sharedPath));
         Assert.IsFalse(readOnlySimulator.SetSeenFlag(1));
         readOnlySimulator.Disconnect();

         // With it, the delegate may store \Seen - and PERMANENTFLAGS said so.
         var seenSimulator = new ImapClientSimulator();
         Assert.IsTrue(seenSimulator.ConnectAndLogon(seenDelegate.Address, "test"));
         string selectResult;
         Assert.IsTrue(seenSimulator.SelectFolder(sharedPath, out selectResult));
         Assert.IsTrue(selectResult.Contains("[PERMANENTFLAGS (\\Seen)]"), selectResult);
         Assert.IsTrue(seenSimulator.SetSeenFlag(1));
         seenSimulator.Disconnect();

         // The owner now sees the message as read. This is the documented,
         // deliberate consequence of storing flags on the message row: \Seen in
         // a shared mailbox is one shared state, arbitrated by the "s" right,
         // not one state per user. See the design record in ACLManager.h.
         var ownerSimulator = new ImapClientSimulator();
         Assert.IsTrue(ownerSimulator.ConnectAndLogon(owner.Address, "test"));
         Assert.IsTrue(ownerSimulator.SelectFolder("INBOX"));
         var ownerFlags = ownerSimulator.GetFlags(1);
         Assert.IsTrue(ownerFlags.Contains("\\Seen"), ownerFlags);
         ownerSimulator.Disconnect();
      }

      [Test]
      public void RightsAreInheritedFromTheParentFolder()
      {
         var owner = CreateOwnerWithMessage("inheritowner@example.test", "InheritSubject", "Inherit body");
         var delegateAccount = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "inheritdelegate@example.test", "test");

         // The owner creates a subfolder; the grant is on INBOX only, so access
         // to the subfolder exists purely through ACL inheritance up the OWNER's
         // tree - the walk that public folders never exercised.
         var ownerSimulator = new ImapClientSimulator();
         Assert.IsTrue(ownerSimulator.ConnectAndLogon(owner.Address, "test"));
         Assert.IsTrue(ownerSimulator.CreateFolder("INBOX.Requests"));
         ownerSimulator.Disconnect();

         GrantRightsOnFolder(owner.IMAPFolders.get_ItemByName("INBOX"), delegateAccount, RightLookup | RightRead);

         var simulator = new ImapClientSimulator();
         Assert.IsTrue(simulator.ConnectAndLogon(delegateAccount.Address, "test"));

         var listResult = simulator.List();
         Assert.IsTrue(listResult.Contains("\"#Users.inheritowner@example.test.INBOX.Requests\""), listResult);

         Assert.IsTrue(simulator.SelectFolder("#Users.inheritowner@example.test.INBOX.Requests"));

         simulator.Disconnect();
      }

      [Test]
      public void AclEnforcementSwitchedOffDisablesTheNamespaceEntirely()
      {
         var owner = CreateOwnerWithMessage("aclowner@example.test", "AclSubject", "Acl body");
         var delegateAccount = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "acldelegate@example.test", "test");

         // A real grant exists, so with enforcement ON everything below would be
         // visible. With enforcement OFF there is no decision-maker left for
         // cross-account access, so the namespace must not exist at all - "ACL
         // disabled" means public folders are open, NOT that every user may read
         // every other user's mail.
         GrantRightsOnFolder(owner.IMAPFolders.get_ItemByName("INBOX"), delegateAccount, RightLookup | RightRead);

         _settings.IMAPACLEnabled = false;
         try
         {
            var simulator = new ImapClientSimulator();
            Assert.IsTrue(simulator.ConnectAndLogon(delegateAccount.Address, "test"));

            var namespaceResult = simulator.Send("A01 NAMESPACE");
            Assert.IsTrue(namespaceResult.Contains("* NAMESPACE ((\"\" \".\")) NIL"), namespaceResult);

            var listResult = simulator.List();
            Assert.IsFalse(listResult.Contains("#Users"), listResult);

            var realFolderResponse = simulator.SendSingleCommand("A15 SELECT #Users.aclowner@example.test.INBOX");
            var missingFolderResponse = simulator.SendSingleCommand("A15 SELECT #Users.aclowner@example.test.NoSuchFolder");
            Assert.AreEqual(missingFolderResponse, realFolderResponse);

            simulator.Disconnect();
         }
         finally
         {
            _settings.IMAPACLEnabled = true;
         }

         // And back on, the same grant is live again.
         var enforcedSimulator = new ImapClientSimulator();
         Assert.IsTrue(enforcedSimulator.ConnectAndLogon(delegateAccount.Address, "test"));
         Assert.IsTrue(enforcedSimulator.SelectFolder("#Users.aclowner@example.test.INBOX"));
         enforcedSimulator.Disconnect();
      }
   }
}
