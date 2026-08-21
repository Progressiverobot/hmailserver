// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;   // StringAssert
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
      private const int RightCreate = 64;       // k
      private const int RightDeleteMailbox = 128; // x
      private const int RightAdminister = 1024; // a

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

      /// <summary>
      /// A message written into somebody else's folder must be READABLE afterwards.
      ///
      /// This is the assertion whose absence let a data-loss bug through a green
      /// gate. DelegateWithReadOnlyRightsIsRefusedAppend above asserts "A01 OK" and
      /// a folder row count, and both of those were true while the message bytes
      /// were being written into the WRONG ACCOUNT'S DIRECTORY: the row named the
      /// owner's folder, the file sat under the delegate's mailbox, and every FETCH
      /// - the owner's included - resolved the owner's directory, found nothing, and
      /// served the missing-file placeholder instead - a Postmaster message whose
      /// body names the path the server looked in, which is how the wrong directory
      /// was identified. "From: Postmaster" is that placeholder's hardcoded first
      /// header and is what these tests assert the absence of; its subject and body
      /// are configurable server messages and would be weaker markers.
      ///
      /// So this fetches the body back, as both parties, and asserts the bytes. It
      /// fails against the unfixed server, which is the only property that makes it
      /// worth having.
      /// </summary>
      [Test]
      public void AppendedMessageIsReadableByBothTheDelegateAndTheOwner()
      {
         var owner = CreateOwnerWithMessage("writeowner@example.test", "ExistingSubject", "Existing body");
         var delegateAccount = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "writedelegate@example.test", "test");

         var ownerInbox = owner.IMAPFolders.get_ItemByName("INBOX");
         GrantRightsOnFolder(ownerInbox, delegateAccount, RightLookup | RightRead | RightInsert);

         const string sharedPath = "#Users.writeowner@example.test.INBOX";
         const string uniqueBody = "DelegatedAppendBodyThatMustSurvive";
         var messageData = "From: writedelegate@example.test\r\n" +
                           "Subject: DelegatedAppendSubject\r\n\r\n" + uniqueBody;

         var delegateSimulator = new ImapClientSimulator();
         Assert.IsTrue(delegateSimulator.ConnectAndLogon(delegateAccount.Address, "test"));

         // Literal size computed from the payload, never written by hand - a wrong
         // count here hangs the fixture rather than failing it.
         var appendCommand = "A01 APPEND \"" + sharedPath + "\" {" + messageData.Length + "}";
         var appendResult = delegateSimulator.SendSingleCommandWithLiteral(appendCommand, messageData);
         Assert.IsTrue(appendResult.Contains("A01 OK"), appendResult);
         CustomAsserts.AssertFolderMessageCount(ownerInbox, 2);

         // The delegate can read back what they just wrote.
         Assert.IsTrue(delegateSimulator.SelectFolder(sharedPath));
         var delegateView = delegateSimulator.Fetch("2 BODY[]");
         delegateSimulator.Disconnect();

         StringAssert.Contains(uniqueBody, delegateView,
            "The delegate cannot read back the message it just appended.");
         StringAssert.DoesNotContain("From: Postmaster", delegateView,
            "The appended message resolved to the missing-file placeholder, which means " +
            "its bytes were written into a directory no read path resolves.");

         // And so can the owner - who is the one that would actually lose the mail.
         var ownerSimulator = new ImapClientSimulator();
         Assert.IsTrue(ownerSimulator.ConnectAndLogon(owner.Address, "test"));
         Assert.IsTrue(ownerSimulator.SelectFolder("INBOX"));
         var ownerView = ownerSimulator.Fetch("2 BODY[]");
         ownerSimulator.Disconnect();

         StringAssert.Contains(uniqueBody, ownerView,
            "The owner cannot read a message a delegate appended to their own INBOX.");
         StringAssert.DoesNotContain("From: Postmaster", ownerView, ownerView);
      }

      /// <summary>
      /// COPY and MOVE across the namespace, with the same read-back proof.
      ///
      /// MOVE is the one that loses mail rather than merely hiding it: the copy
      /// went to the mover's directory where nothing could resolve it, and the
      /// source row and file were then expunged - so the only readable copy was
      /// destroyed by a command that reported success.
      /// </summary>
      [Test]
      public void CopyAndMoveIntoASharedFolderKeepTheMessageReadable()
      {
         var owner = CreateOwnerWithMessage("copyowner@example.test", "OwnerSubject", "Owner body");
         var mover = CreateOwnerWithMessage("copymover@example.test", "MoverSubject", "MoverBodyThatMustSurvive");

         var ownerInbox = owner.IMAPFolders.get_ItemByName("INBOX");
         GrantRightsOnFolder(ownerInbox, mover, RightLookup | RightRead | RightInsert);

         const string sharedPath = "#Users.copyowner@example.test.INBOX";

         var simulator = new ImapClientSimulator();
         Assert.IsTrue(simulator.ConnectAndLogon(mover.Address, "test"));
         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         var copyResult = simulator.SendSingleCommand("A10 COPY 1 \"" + sharedPath + "\"");
         Assert.IsTrue(copyResult.Contains("A10 OK"), copyResult);
         CustomAsserts.AssertFolderMessageCount(ownerInbox, 2);
         simulator.Disconnect();

         // The owner must be able to read the copy.
         var ownerSimulator = new ImapClientSimulator();
         Assert.IsTrue(ownerSimulator.ConnectAndLogon(owner.Address, "test"));
         Assert.IsTrue(ownerSimulator.SelectFolder("INBOX"));
         var copied = ownerSimulator.Fetch("2 BODY[]");
         ownerSimulator.Disconnect();

         StringAssert.Contains("MoverBodyThatMustSurvive", copied,
            "The owner cannot read a message copied into their folder.");
         StringAssert.DoesNotContain("From: Postmaster", copied, copied);
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

      /// <summary>
      ///    The negative control for the RENAME fix. A delegate holding the "x"
      ///    (delete mailbox) right could - before RENAME became ownership-aware -
      ///    rename a folder out of the owner's tree and into their own, because
      ///    the guard only distinguished public from non-public and had no way to
      ///    say "non-public but somebody else's". If the fix were inert, both
      ///    renames below would succeed and every assertion after them would
      ///    fail.
      /// </summary>
      [Test]
      public void DelegateWithDeleteRightCannotRenameOwnersFolderIntoTheirOwnTree()
      {
         var owner = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "renameowner@example.test", "test");
         var delegateAccount = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "renamethief@example.test", "test");

         var ownerSimulator = new ImapClientSimulator();
         Assert.IsTrue(ownerSimulator.ConnectAndLogon(owner.Address, "test"));
         Assert.IsTrue(ownerSimulator.CreateFolder("Cases"));
         ownerSimulator.Disconnect();

         var casesFolder = CustomAsserts.AssertFolderExists(owner.IMAPFolders, "Cases");
         GrantRightsOnFolder(casesFolder, delegateAccount, RightLookup | RightDeleteMailbox);

         var sharedPath = "#Users.renameowner@example.test.Cases";

         var simulator = new ImapClientSimulator();
         Assert.IsTrue(simulator.ConnectAndLogon(delegateAccount.Address, "test"));

         // Into the delegate's own root, and into the delegate's own INBOX:
         // both are renames between owners, and the "x" right does not permit
         // either - a rename never moves message files between mailbox
         // directories, so an owner-crossing rename would strand them.
         string renameResult;
         Assert.IsFalse(simulator.RenameFolder(sharedPath, "StolenCases", out renameResult), renameResult);
         Assert.IsTrue(renameResult.Contains("different account"), renameResult);
         Assert.IsFalse(simulator.RenameFolder(sharedPath, "INBOX.StolenCases", out renameResult), renameResult);

         // Renaming it under ANOTHER account's namespace path is refused the
         // same way, decided from the path shape alone - the response must not
         // depend on whether that account exists, or it is an account oracle.
         string intoGhost;
         string intoOther;
         Assert.IsFalse(simulator.RenameFolder(sharedPath, "#Users.ghost@example.test.Cases", out intoGhost), intoGhost);
         Assert.IsFalse(simulator.RenameFolder(sharedPath, "#Users.renamethief@example.test.Cases", out intoOther), intoOther);
         Assert.AreEqual(intoGhost.Replace("ghost@example.test", "").Replace("renamethief@example.test", ""),
                         intoOther.Replace("ghost@example.test", "").Replace("renamethief@example.test", ""));

         var delegateList = simulator.List();
         Assert.IsFalse(delegateList.Contains("StolenCases"), delegateList);
         simulator.Disconnect();

         // The folder never moved: still the owner's, still selectable.
         var ownerCheck = new ImapClientSimulator();
         Assert.IsTrue(ownerCheck.ConnectAndLogon(owner.Address, "test"));
         Assert.IsTrue(ownerCheck.List().Contains("\"Cases\""));
         Assert.IsTrue(ownerCheck.SelectFolder("Cases"));
         ownerCheck.Disconnect();
      }

      /// <summary>
      ///    RENAME inside the owner's tree is the legitimate use, and RFC 4314
      ///    prices it as a delete-plus-create: "x" on the folder being renamed,
      ///    "k" on its new parent. The rights flip mid-test proves the "k" check
      ///    is live, the same pattern the APPEND test uses for "i".
      /// </summary>
      [Test]
      public void DelegateRenameWithinTheOwnersTreeDemandsDeleteAndCreateRights()
      {
         var owner = CreateOwnerWithMessage("renamehost@example.test", "RenameSubject", "Rename body");
         var delegateAccount = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "renamehand@example.test", "test");

         var ownerSimulator = new ImapClientSimulator();
         Assert.IsTrue(ownerSimulator.ConnectAndLogon(owner.Address, "test"));
         Assert.IsTrue(ownerSimulator.CreateFolder("INBOX.Drafts"));
         ownerSimulator.Disconnect();

         var ownerInbox = owner.IMAPFolders.get_ItemByName("INBOX");
         GrantRightsOnFolder(ownerInbox, delegateAccount, RightLookup | RightDeleteMailbox);

         var oldPath = "#Users.renamehost@example.test.INBOX.Drafts";
         var newPath = "#Users.renamehost@example.test.INBOX.Done";

         var simulator = new ImapClientSimulator();
         Assert.IsTrue(simulator.ConnectAndLogon(delegateAccount.Address, "test"));

         // "x" alone is not enough: the new parent (INBOX) carries no "k" for
         // this delegate yet.
         string refusedResult;
         Assert.IsFalse(simulator.RenameFolder(oldPath, newPath, out refusedResult), refusedResult);
         Assert.IsTrue(refusedResult.Contains("CreateMailbox permission denied"), refusedResult);

         UpdateRightsOnFolder(ownerInbox, delegateAccount, RightLookup | RightDeleteMailbox | RightCreate);

         string allowedResult;
         Assert.IsTrue(simulator.RenameFolder(oldPath, newPath, out allowedResult), allowedResult);
         simulator.Disconnect();

         // The owner sees the rename, in the owner's own names.
         var ownerCheck = new ImapClientSimulator();
         Assert.IsTrue(ownerCheck.ConnectAndLogon(owner.Address, "test"));
         var ownerList = ownerCheck.List();
         Assert.IsTrue(ownerList.Contains("\"INBOX.Done\""), ownerList);
         Assert.IsFalse(ownerList.Contains("\"INBOX.Drafts\""), ownerList);
         ownerCheck.Disconnect();
      }

      /// <summary>
      ///    An inbox is renameable and deletable by nobody, however it is
      ///    spelled. The historical guards compared the path text against the
      ///    single word "INBOX", which "#Users.owner@domain.INBOX" is not - and
      ///    behind that miss, DELETE would have emptied the owner's inbox of
      ///    every message and subfolder even though the inbox ROW survives.
      /// </summary>
      [Test]
      public void DelegateCannotRenameOrDeleteTheOwnersInbox()
      {
         var owner = CreateOwnerWithMessage("inboxowner@example.test", "InboxSubject", "Inbox body");
         var delegateAccount = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "inboxdelegate@example.test", "test");

         var ownerInbox = owner.IMAPFolders.get_ItemByName("INBOX");
         GrantRightsOnFolder(ownerInbox, delegateAccount, RightLookup | RightRead | RightDeleteMailbox | RightCreate);

         var sharedInbox = "#Users.inboxowner@example.test.INBOX";

         var simulator = new ImapClientSimulator();
         Assert.IsTrue(simulator.ConnectAndLogon(delegateAccount.Address, "test"));

         string renameResult;
         Assert.IsFalse(simulator.RenameFolder(sharedInbox, "#Users.inboxowner@example.test.OldMail", out renameResult), renameResult);
         Assert.IsTrue(renameResult.Contains("Cannot rename INBOX"), renameResult);

         var deleteResult = simulator.SendSingleCommand("A22 DELETE " + sharedInbox);
         Assert.IsTrue(deleteResult.Contains("You cannot delete the inbox"), deleteResult);

         simulator.Disconnect();

         // Nothing was emptied: the owner's message is where it was.
         CustomAsserts.AssertFolderMessageCount(ownerInbox, 1);
      }

      /// <summary>
      ///    DELETE of a delegated subfolder. Before the fix this crashed: the
      ///    parent folder was looked up in the CALLER's tree, where a delegated
      ///    folder's parent does not exist, and the null was dereferenced. The
      ///    first phase also pins that "x" is demanded before anything happens.
      /// </summary>
      [Test]
      public void DeleteOfADelegatedSubfolderDemandsTheDeleteRightAndWorks()
      {
         var owner = CreateOwnerWithMessage("deleteowner@example.test", "DeleteSubject", "Delete body");
         var delegateAccount = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "deletedelegate@example.test", "test");

         var ownerSimulator = new ImapClientSimulator();
         Assert.IsTrue(ownerSimulator.ConnectAndLogon(owner.Address, "test"));
         Assert.IsTrue(ownerSimulator.CreateFolder("INBOX.Old"));
         ownerSimulator.Disconnect();

         var ownerInbox = owner.IMAPFolders.get_ItemByName("INBOX");
         GrantRightsOnFolder(ownerInbox, delegateAccount, RightLookup | RightRead);

         var sharedSubfolder = "#Users.deleteowner@example.test.INBOX.Old";

         var simulator = new ImapClientSimulator();
         Assert.IsTrue(simulator.ConnectAndLogon(delegateAccount.Address, "test"));

         // Without "x": refused, folder stays.
         Assert.IsFalse(simulator.DeleteFolder(sharedSubfolder));
         Assert.IsTrue(simulator.List().Contains(sharedSubfolder), "the refused DELETE must leave the folder in place");

         // With "x" (inherited from the INBOX grant): deleted - and the session
         // survives, which is the crash regression this test exists for.
         UpdateRightsOnFolder(ownerInbox, delegateAccount, RightLookup | RightRead | RightDeleteMailbox);
         Assert.IsTrue(simulator.DeleteFolder(sharedSubfolder));

         var afterDeleteList = simulator.List();
         Assert.IsFalse(afterDeleteList.Contains(sharedSubfolder), afterDeleteList);
         Assert.IsTrue(simulator.SelectFolder("#Users.deleteowner@example.test.INBOX"),
            "the session must still be usable after deleting a delegated subfolder");
         simulator.Disconnect();

         // The owner agrees the folder is gone - and only that folder.
         var ownerCheck = new ImapClientSimulator();
         Assert.IsTrue(ownerCheck.ConnectAndLogon(owner.Address, "test"));
         var ownerList = ownerCheck.List();
         Assert.IsFalse(ownerList.Contains("\"INBOX.Old\""), ownerList);
         CustomAsserts.AssertFolderMessageCount(ownerInbox, 1);
         ownerCheck.Disconnect();
      }

      /// <summary>
      ///    The grant surface: SETACL now works on account folders, gated on the
      ///    RFC 4314 "a" (administer) right. The owner holds "a" implicitly; a
      ///    delegate holds it only when granted. The rights flip is the negative
      ///    control - if SETACL still refused account folders outright, the
      ///    second half would fail.
      /// </summary>
      [Test]
      public void SetAclOnADelegatedFolderDemandsTheAdministerRight()
      {
         var owner = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "granthost@example.test", "test");
         var delegateAccount = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "grantadmin@example.test", "test");
         var thirdAccount = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "grantthird@example.test", "test");

         var ownerSimulator = new ImapClientSimulator();
         Assert.IsTrue(ownerSimulator.ConnectAndLogon(owner.Address, "test"));
         Assert.IsTrue(ownerSimulator.CreateFolder("Team"));
         ownerSimulator.Disconnect();

         var teamFolder = CustomAsserts.AssertFolderExists(owner.IMAPFolders, "Team");
         GrantRightsOnFolder(teamFolder, delegateAccount, RightLookup | RightRead);

         var sharedPath = "#Users.granthost@example.test.Team";

         var simulator = new ImapClientSimulator();
         Assert.IsTrue(simulator.ConnectAndLogon(delegateAccount.Address, "test"));

         // "lr" can read, but cannot administer: no granting others, no reading
         // the ACL, no LISTRIGHTS.
         Assert.IsFalse(simulator.SetACL(sharedPath, thirdAccount.Address, "lr"));
         Assert.IsTrue(simulator.GetACL(sharedPath).Contains("Permission denied"), "GETACL without the a right must be refused");
         Assert.IsTrue(simulator.ListRights(sharedPath, delegateAccount.Address).Contains("Permission denied"), "LISTRIGHTS without the a right must be refused");

         var thirdBefore = new ImapClientSimulator();
         Assert.IsTrue(thirdBefore.ConnectAndLogon(thirdAccount.Address, "test"));
         Assert.IsFalse(thirdBefore.SelectFolder(sharedPath));
         thirdBefore.Disconnect();

         // With "a", the same SETACL succeeds and the new grant is live.
         UpdateRightsOnFolder(teamFolder, delegateAccount, RightLookup | RightRead | RightAdminister);

         Assert.IsTrue(simulator.SetACL(sharedPath, thirdAccount.Address, "lr"));
         var aclList = simulator.GetACL(sharedPath);
         Assert.IsTrue(aclList.Contains(thirdAccount.Address), aclList);
         simulator.Disconnect();

         var thirdAfter = new ImapClientSimulator();
         Assert.IsTrue(thirdAfter.ConnectAndLogon(thirdAccount.Address, "test"));
         Assert.IsTrue(thirdAfter.SelectFolder(sharedPath));
         thirdAfter.Disconnect();
      }

      /// <summary>
      ///    An owner shares their own folder over plain IMAP - no direct SQL -
      ///    and a repeated SETACL for the same grantee UPDATES the stored grant
      ///    rather than inserting a second row beside it. The single-entry
      ///    assertion pins that: before the fix, SETACL read the ACL through a
      ///    path that returns an empty list for account folders, so every SETACL
      ///    "updated" nothing and inserted a duplicate.
      /// </summary>
      [Test]
      public void OwnerGrantsOverImapAndRepeatedSetAclUpdatesInPlace()
      {
         var owner = CreateOwnerWithMessage("selfgrant@example.test", "SelfSubject", "Self body");
         var delegateAccount = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "selfdelegate@example.test", "test");

         var ownerSimulator = new ImapClientSimulator();
         Assert.IsTrue(ownerSimulator.ConnectAndLogon(owner.Address, "test"));

         // The owner needs no stored grant to administer their own folder.
         Assert.IsTrue(ownerSimulator.SetACL("INBOX", delegateAccount.Address, "lr"));

         // Second SETACL for the same grantee: the rights change, the entry
         // count does not.
         Assert.IsTrue(ownerSimulator.SetACL("INBOX", delegateAccount.Address, "lrs"));

         var aclList = ownerSimulator.GetACL("INBOX");
         var firstEntry = aclList.IndexOf(delegateAccount.Address);
         var lastEntry = aclList.LastIndexOf(delegateAccount.Address);
         Assert.IsTrue(firstEntry >= 0, aclList);
         Assert.AreEqual(firstEntry, lastEntry, "one grantee must produce exactly one ACL entry: " + aclList);
         Assert.IsTrue(aclList.Contains(delegateAccount.Address + " lrs"), aclList);
         ownerSimulator.Disconnect();

         // And the grant made over IMAP behaves exactly like one inserted by
         // SQL: the delegate reads the shared mailbox.
         var simulator = new ImapClientSimulator();
         Assert.IsTrue(simulator.ConnectAndLogon(delegateAccount.Address, "test"));
         Assert.IsTrue(simulator.SelectFolder("#Users.selfgrant@example.test.INBOX"));
         var myRights = simulator.GetMyRights("#Users.selfgrant@example.test.INBOX");
         Assert.IsTrue(myRights.Contains("lrs"), myRights);
         simulator.Disconnect();
      }

      /// <summary>
      ///    No SETACL/DELETEACL sequence can lock an owner out of their own
      ///    folder. The owner's rights are implicit - answered before a single
      ///    ACL row is read - so they are not stored state that a grant edit
      ///    could remove: SETACL naming the owner is refused, DELETEACL naming
      ///    the owner deletes nothing, and removing the last "a" grant merely
      ///    returns the folder to owner-only administration.
      /// </summary>
      [Test]
      public void OwnerCannotBeLockedOutOfTheirOwnFolder()
      {
         var owner = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "lockowner@example.test", "test");
         var delegateAccount = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "lockadmin@example.test", "test");

         var ownerSimulator = new ImapClientSimulator();
         Assert.IsTrue(ownerSimulator.ConnectAndLogon(owner.Address, "test"));
         Assert.IsTrue(ownerSimulator.CreateFolder("Records"));
         Assert.IsTrue(ownerSimulator.SetACL("Records", delegateAccount.Address, "lra"));
         ownerSimulator.Disconnect();

         var sharedPath = "#Users.lockowner@example.test.Records";

         var simulator = new ImapClientSimulator();
         Assert.IsTrue(simulator.ConnectAndLogon(delegateAccount.Address, "test"));

         // The administering delegate tries to demote the owner - both ways.
         var setAclResult = simulator.SendSingleCommand("A41 SETACL " + sharedPath + " " + owner.Address + " lr");
         Assert.IsTrue(setAclResult.Contains("implicit and cannot be changed"), setAclResult);

         // DELETEACL naming the owner answers OK - there is, correctly, no
         // stored row for the owner to delete - and must change nothing.
         Assert.IsTrue(simulator.DeleteACL(sharedPath, owner.Address));

         // Last, the delegate removes their own grant: the folder returns to
         // owner-only, and the delegate's own access dies with it.
         Assert.IsTrue(simulator.DeleteACL(sharedPath, delegateAccount.Address));
         Assert.IsFalse(simulator.SelectFolder(sharedPath));
         simulator.Disconnect();

         // The owner never lost a thing: still selects, still administers.
         var ownerCheck = new ImapClientSimulator();
         Assert.IsTrue(ownerCheck.ConnectAndLogon(owner.Address, "test"));
         Assert.IsTrue(ownerCheck.SelectFolder("Records"));
         Assert.IsTrue(ownerCheck.SetACL("Records", delegateAccount.Address, "lr"),
            "the owner must still hold the administer right after every grant was removed");
         ownerCheck.Disconnect();
      }

      /// <summary>
      ///    The no-probing property, extended to the commands this change
      ///    touches: for a caller without the lookup right, a real folder, a
      ///    missing folder and a missing account must answer byte-for-byte
      ///    identically - for RENAME, DELETE, SETACL, GETACL, DELETEACL,
      ///    LISTRIGHTS and MYRIGHTS, exactly as SELECT/STATUS already pin it.
      /// </summary>
      [Test]
      public void RenameDeleteAndAclCommandsAreBlindWithoutTheLookupRight()
      {
         var owner = CreateOwnerWithMessage("blindowner@example.test", "BlindSubject", "Blind body");
         var trustedDelegate = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "blindtrusted@example.test", "test");
         var outsider = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "blindoutsider@example.test", "test");

         // Shared - with somebody else. The outsider must still see nothing.
         GrantRightsOnFolder(owner.IMAPFolders.get_ItemByName("INBOX"), trustedDelegate, RightLookup | RightRead);

         var realFolder = "#Users.blindowner@example.test.INBOX";
         var missingFolder = "#Users.blindowner@example.test.NoSuchFolder";
         var missingAccount = "#Users.ghost@example.test.INBOX";

         var simulator = new ImapClientSimulator();
         Assert.IsTrue(simulator.ConnectAndLogon(outsider.Address, "test"));

         foreach (var probe in new[]
         {
            "A51 RENAME {0} SomewhereElse",
            "A52 DELETE {0}",
            "A53 SETACL {0} " + outsider.Address + " lr",
            "A54 GETACL {0}",
            "A55 DELETEACL {0} " + outsider.Address,
            "A56 LISTRIGHTS {0} " + outsider.Address,
            "A57 MYRIGHTS {0}"
         })
         {
            var realResponse = simulator.SendSingleCommand(string.Format(probe, realFolder));
            var missingFolderResponse = simulator.SendSingleCommand(string.Format(probe, missingFolder));
            var missingAccountResponse = simulator.SendSingleCommand(string.Format(probe, missingAccount));

            Assert.AreEqual(missingFolderResponse, realResponse, probe);
            Assert.AreEqual(missingAccountResponse, realResponse, probe);
         }

         simulator.Disconnect();

         // The probes changed nothing: the real folder is still there for the
         // one account genuinely granted rights on it.
         var trustedSimulator = new ImapClientSimulator();
         Assert.IsTrue(trustedSimulator.ConnectAndLogon(trustedDelegate.Address, "test"));
         Assert.IsTrue(trustedSimulator.SelectFolder(realFolder));
         trustedSimulator.Disconnect();
      }
   }
}
