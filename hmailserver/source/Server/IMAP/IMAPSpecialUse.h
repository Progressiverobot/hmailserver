// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class IMAPFolders;

   // RFC 6154 - "IMAP LIST Extension for Special-Use Mailboxes".
   //
   // Why this class exists at all: without SPECIAL-USE every client has to guess
   // which mailbox is Sent, Drafts, Junk or Trash from its name. The guess fails on
   // a mailbox whose folders are named in something other than English, and it fails
   // whenever one client has already created "Sent Items" while the next one goes
   // looking for "Sent" - the second client then creates its own sent folder and the
   // user's outgoing mail is silently split across two mailboxes with nothing in the
   // protocol to say which one is authoritative. Thunderbird, Apple Mail and Outlook
   // mobile all prefer the server's designation when the server advertises it, so
   // advertising it is what stops the duplicate happening.
   //
   // Two sources of truth are combined, in this order of precedence:
   //
   //   1. An explicit designation persisted on the folder row
   //      (hm_imapfolders.folderspecialuse), written by CREATE ... USE (\Sent).
   //      The client told us what the folder is for, so it wins outright.
   //   2. The folder-name table name_mappings_, which is the behaviour hMailServer
   //      6.2.18 already had. It is kept name-for-name compatible so that an account
   //      which has never had a designation assigned, talking to a client which never
   //      asks for SPECIAL-USE, sees exactly the LIST output it saw before. Removing
   //      the name guess would have regressed every existing installation, which is
   //      why it is a fallback rather than a replacement.
   //
   // Everything goes through Resolve(), which guarantees an attribute is handed out
   // at most once per mailbox. That guarantee is the whole point: returning \Sent on
   // two folders is worse than returning it on none, because RFC 6154 gives the
   // client no way to arbitrate, so clients pick whichever line they parsed last and
   // the user again ends up with mail in two places. Before this change an account
   // holding both "Sent" and "Sent Items" got \Sent on both, silently.
   class IMAPSpecialUse
   {
   public:

      // The RFC 6154 section 2 attributes, as a bitmask so that one folder can carry
      // more than one designation. The CREATE USE parameter is defined as a list
      // (use = "USE" SP "(" use-attr *(SP use-attr) ")"), so a client is entitled to
      // ask for two at once - \All \Archive on an archive-everything mailbox is the
      // realistic case - and a single-value column could not represent that.
      //
      // The numeric values are persisted in the database and must never be
      // renumbered; add new attributes in the free bits above \Trash instead.
      enum Designation
      {
         DesignationNone    = 0x00,
         DesignationAll     = 0x01,
         DesignationArchive = 0x02,
         DesignationDrafts  = 0x04,
         DesignationFlagged = 0x08,
         DesignationJunk    = 0x10,
         DesignationSent    = 0x20,
         DesignationTrash   = 0x40,

         // Every bit this build understands. A value written by a later version of
         // hMailServer that knows more attributes is masked down to these, because
         // echoing an unknown bit to the client would either produce no attribute at
         // all or, worse, the wrong one. The mask is applied on the way out of the
         // business object rather than on the way in, so that a downgrade followed by
         // an upgrade does not destroy a designation this build cannot name.
         DesignationMask    = 0x7F
      };

      // Parses the content of a CREATE parameter word, i.e. what the simple command
      // parser hands back for "(USE (\Drafts \Sent))" - the outer parentheses are
      // already stripped, so the input is  USE (\Drafts \Sent).
      //
      // Returns false when the word is not a USE parameter at all (the caller then
      // rejects the command as BAD - a syntax error), and returns true with
      // designations == DesignationNone when it is a USE parameter naming an
      // attribute this server does not implement (the caller then rejects with
      // NO [USEATTR], which is what RFC 6154 section 3 requires for an unsupported
      // use rather than a malformed command).
      static bool ParseUseParameter(const String &parameterContent, int &designations);

      // Parses a bare space-separated attribute list such as  \Drafts \Sent.
      // Returns false, with designations left at DesignationNone, if the list is
      // empty or if any token is not an attribute this server implements.
      static bool ParseAttributeList(const String &attributeList, int &designations);

      // Renders a designation mask as the wire attributes, space separated and with
      // no leading space, e.g. "\Drafts \Sent". Canonical order (lowest bit first)
      // so that the LIST output for a given mailbox is byte-stable between runs;
      // regression tests and human diffing both depend on that.
      static String FormatDesignations(int designations);

      // Resolves the effective designation of every folder in one account's mailbox.
      // Key is the folder database id, value is the designation mask; folders with no
      // designation are absent from the map rather than present with a zero value, so
      // that a lookup miss and "no designation" are the same thing.
      //
      // accountFolders must be the account's top-level folder collection. Public
      // folders must not be passed here: a special use is a property of one user's
      // mailbox, and a shared folder that appeared as \Trash for every user who can
      // see it would have clients expunging each other's mail.
      static void Resolve(std::shared_ptr<IMAPFolders> accountFolders, std::map<__int64, int> &designationsByFolderID);

      // The union of the designations explicitly stored on the folders of one
      // account, at any depth, ignoring the folder whose id is excludeFolderID.
      // CREATE ... USE checks the requested attributes against this to refuse a
      // second \Sent instead of quietly accepting it.
      //
      // The exclusion is what makes re-designating an existing folder possible:
      // without it, telling the server a second time that "Enviados" is \Sent would
      // be refused because "Enviados" itself already holds \Sent. Pass 0 to consider
      // every folder - no row ever has id 0, so 0 excludes nothing.
      static int GetExplicitDesignations(std::shared_ptr<IMAPFolders> accountFolders, __int64 excludeFolderID);

   private:

      // One row of the historical folder-name table: the designation a top-level
      // folder with this name is assumed to have.
      struct NameMapping
      {
         const wchar_t *name_;
         int designation_;
      };

      static const NameMapping name_mappings_[];
      static const size_t name_mapping_count_;

      static void CollectExplicit_(std::shared_ptr<IMAPFolders> folders, std::map<__int64, int> &designationsByFolderID, int &claimed, __int64 excludeFolderID, size_t depth);

      static int DesignationFromAttribute_(const String &attribute);
      static const wchar_t *AttributeFromDesignation_(int designation);
   };
}
