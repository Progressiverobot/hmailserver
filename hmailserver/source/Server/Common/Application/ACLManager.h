// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class IMAPFolder;
   class IMAPFolders;
   class ACLPermission;
   class ACLPermissions;

   class ACLManager
   {
   public:
      ACLManager(void);
      ~ACLManager(void);
      
      // Whether folder access control is enforced at all.
      //
      // The bypass this answers - "IMAP ACL has been disabled, allow everything" -
      // was written out twice, in two neighbouring functions in IMAPConnection, each
      // reading the setting itself and each deciding separately what "allow
      // everything" means. Two copies of a security decision is one more than can be
      // changed correctly: anything that narrows this later - ACL enforced on shared
      // folders but not on a user's own, say - has to find both, and finding one is
      // indistinguishable from finding both until somebody reads other people's mail.
      //
      // It lives here rather than on IMAPConnection because IMAP is not the only
      // thing that needs the answer. The REST API and shared mailboxes both introduce
      // paths where access has to be decided against the owner of the folder rather
      // than the logged-in account, and they will ask this class, not an IMAP
      // connection. A small down payment on the single authorisation choke point:
      // one decision rather than one decision per protocol.
      static bool GetAclEnforcementEnabled();

      // The IMAP name of the RFC 2342 "Other Users" namespace root - the prefix
      // under which one account's folders appear to another account that has been
      // granted rights on them: "#Users.info@example.com.INBOX".
      //
      // One function, like IMAPConfiguration::GetPublicFolderDiskName, so that
      // NAMESPACE, LIST, path resolution and the tests all agree by construction.
      // When this becomes operator-configurable it becomes a setting read here
      // (PROPERTY_IMAPOTHERUSERSFOLDERNAME) and nothing else changes.
      static String GetOtherUsersFolderName();

      // Whether the "#Users" namespace exists at all. Deliberately the same
      // switch as ACL enforcement: every path into another user's mailbox is
      // decided by an ACL, so with enforcement off there is no decision-maker
      // left, and the only safe meaning of "off" is that the namespace does not
      // exist - NAMESPACE advertises NIL and "#Users..." paths do not resolve.
      // The historical meaning of "ACL disabled" ("public folders are open to
      // everyone") must NOT be extended to private mailboxes: an operator who
      // turned ACL off to simplify a public archive has not consented to every
      // user reading every other user's mail.
      static bool GetOtherUsersNamespaceEnabled();

      // The ids of accounts that have at least one ACL entry on one of their own
      // folders - the candidate owners for the "#Users" namespace. A pre-filter
      // so LIST does not walk every account's folder tree, NOT an access
      // decision: whether the caller may see any particular folder is decided,
      // per folder, by GetPermissionForFolder during the LIST walk, and an owner
      // none of whose folders grant the caller lookup produces no output.
      std::vector<__int64> GetAccountsWithFolderShares();

      /*
         Design record - the \Seen flag in a shared mailbox is SHARED, not
         per-user.

         This server stores flags on the message row (hm_messages), so when a
         delegate with the "s" right marks a message seen, it is seen for the
         owner and every other delegate too. Per-user \Seen - the separate
         per-user state RFC 3501 section 5.2 describes, and the ambiguity RFC
         4314's "s" right exists to arbitrate - would need a new per-account
         flag table joined on every FETCH/STORE/STATUS/SELECT: a schema change
         and a message-store rework, deliberately not smuggled into this
         feature.

         The "s" right is enforced (a delegate without it can neither STORE
         \Seen nor set it implicitly via FETCH BODY[]), so an operator who wants
         the owner's unread-state untouched grants "lr" and not "lrs". That is
         the documented trade: with "s", delegates share one read/unread state;
         without it, delegates leave no trace but their clients cannot track
         what they have read.
      */
      std::shared_ptr<ACLPermission> GetPermissionForFolder(__int64 iAccountID, std::shared_ptr<IMAPFolder> pFolder);

      // The ACL entries stored for exactly this folder (not inherited ones).
      // Exists because IMAPFolder::GetPermissions() skips the database read for
      // account-level folders - see the comment at the definition. Public
      // because everything that READS or EDITS a folder's stored ACL (GETACL,
      // SETACL, DELETEACL) has to see the account-folder rows too; going
      // through IMAPFolder::GetPermissions() instead means editing an empty
      // list that is thrown away, which on an account folder reports success
      // while changing nothing.
      std::shared_ptr<ACLPermissions> GetPermissionsSetOnFolder(std::shared_ptr<IMAPFolder> pFolder);

      bool SetACL(std::shared_ptr<IMAPFolder> pFolder, const String& sIdentifier, const String &sPermissions);

   private:

      std::shared_ptr<ACLPermission> GetPermissionForAccount_(std::shared_ptr<ACLPermissions> pPermissions, __int64 iAccountID);
   };
}