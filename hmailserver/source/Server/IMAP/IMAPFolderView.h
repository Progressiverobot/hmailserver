// Copyright (c) 2026 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class Messages;

   struct IMAPViewEntry
   {
      IMAPViewEntry() :
         message_id(0),
         uid(0)
      {
      }

      IMAPViewEntry(__int64 message_id, unsigned int uid) :
         message_id(message_id),
         uid(uid)
      {
      }

      __int64 message_id;
      unsigned int uid;
   };

   /*
      One per selected mailbox per IMAP session: this session's mapping between message
      sequence numbers and messages.

      Sequence numbers must be stable within a session (RFC 3501 2.3.1.2), and they may
      only change where the server is allowed to tell the client - an untagged EXPUNGE,
      which RFC 3501 7.4.1 forbids during FETCH, STORE, SEARCH and (RFC 5256) SORT and
      THREAD. Before this view existed every session numbered messages by their position
      in the ONE collection MessagesContainer holds per folder, so a message expunged by
      any other session renumbered everyone at once, silently: a "FETCH 2 BODY[2]" issued
      right after that returned the attachment of what used to be message 3 (upstream
      #458). The view holds message ids only; MessagesContainer still owns the objects.

      Shrink semantics: the view only shrinks in RemoveMessages, which the callers invoke
      exactly where an untagged EXPUNGE may be sent. A command that finds one of its
      messages gone in the meantime marks it vanished instead, and the next permitted
      EXPUNGE takes it out. Growth (AppendNewMessages) never renumbers anything, because
      new messages carry higher UIDs and go at the end.

      This class is a leaf: it never calls back into the connection, the notification
      server, the database or the socket, so it may be used under any of their locks.
   */
   class IMAPFolderView
   {
   public:

      IMAPFolderView(__int64 account_id, __int64 folder_id);

      __int64 GetAccountID() const { return account_id_; }
      __int64 GetFolderID() const { return folder_id_; }

      // Replaces the view with the current contents of the folder. SELECT and EXAMINE only.
      void Initialize(std::shared_ptr<Messages> messages);

      // Adds messages which have arrived since the view was last updated. Returns the
      // number added. Appending never renumbers messages the client already knows about.
      int AppendNewMessages(std::shared_ptr<Messages> messages);

      int GetMessageCount() const;

      // The largest UID in the view, 0 when it is empty. What "*" means in a UID set.
      unsigned int GetHighestUID() const;

      bool GetEntryBySequence(int sequence, IMAPViewEntry &entry) const;
      bool GetEntryByUID(unsigned int uid, int &sequence, IMAPViewEntry &entry) const;
      bool GetSequenceByMessageID(__int64 message_id, int &sequence) const;

      std::vector<std::pair<int, IMAPViewEntry> > GetEntriesBySequenceRange(int first, int last) const;
      std::vector<std::pair<int, IMAPViewEntry> > GetEntriesByUIDRange(unsigned int first, unsigned int last) const;
      std::vector<std::pair<int, IMAPViewEntry> > GetAllEntries() const;

      // Removes the given messages and returns the sequence numbers to report to the
      // client, in the order they should be sent, each adjusted for the removals that
      // precede it - RFC 3501 7.4.1's "the sequence numbers decrement by one for each
      // EXPUNGE". The UIDs of the removed messages, in the same order, are handed back
      // through removed_uids when asked for; a QRESYNC session reports those as one
      // VANISHED response instead (RFC 7162 3.2.10). May only be called where an
      // untagged EXPUNGE is allowed.
      std::vector<int> RemoveMessages(const std::vector<__int64> &message_ids, std::vector<unsigned int> *removed_uids = nullptr);

      // A command found a message in this view which no longer exists in the folder.
      // It leaves the view at the next point an untagged EXPUNGE may be sent.
      void MarkVanished(__int64 message_id);
      std::vector<__int64> TakeVanished();

   private:

      void Rebuild_();

      mutable boost::recursive_mutex mutex_;

      // Ordered by UID ascending, which is the IMAP message order.
      std::vector<IMAPViewEntry> entries_;
      std::map<__int64, size_t> offset_by_message_id_;

      std::set<__int64> vanished_;

      unsigned int highest_uid_seen_;

      __int64 account_id_;
      __int64 folder_id_;
   };
}
