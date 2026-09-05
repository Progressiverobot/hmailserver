// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "Collection.h"

#include "../BO/Message.h"
#include "../Persistence/PersistentMessage.h"

namespace HM
{

   class Messages : public Collection<Message, PersistentMessage>
   {
   public:
	   Messages(__int64 iAccountID, __int64 iFolderID);
	   virtual ~Messages();

      void Save();

      long GetSize() const;
      __int64 GetFirstUnseenUID() const;

      // 1-based sequence number of the first message without the \Seen flag, or
      // 0 when there is none. RFC 3501 requires a sequence number (not a UID) in
      // the [UNSEEN] response code of SELECT/EXAMINE.
      __int64 GetFirstUnseenSequenceNumber() const;

      // The ids of the messages currently carrying \Recent, collected under the
      // collection lock. Callers must not walk GetVector() themselves: another
      // session can erase from the shared vector while it is being iterated.
      std::set<__int64> GetRecentMessageIDs() const;

      long GetNoOfSeen() const;
      
      std::vector<std::shared_ptr<Message>> GetCopy();

      std::shared_ptr<Message> GetItemByUID(unsigned int uid);
      std::shared_ptr<Message> GetItemByUID(unsigned int uid, unsigned int &foundIndex);

      void DeleteMessages(std::function<bool(int, std::shared_ptr<Message>)> &filter);

      // False when the messages could not be loaded, so the caller can retry
      // instead of treating an empty collection as an empty folder.
      bool Refresh(bool update_recent_flags);

      void AddToCollection(std::shared_ptr<DALRecordset> pRS);
      
      void Remove(__int64 iDBID);

      void RemoveRecentFlags();

      __int64 GetAccountID() {return account_id_; }
      __int64 GetFolderID() {return folder_id_; }

   protected:
      virtual String GetCollectionName() const {return "Messages"; }
      virtual bool PreSaveObject(std::shared_ptr<Message> pMessage, XNode *node);
   private:

      unsigned int last_refreshed_uid_;

      __int64 account_id_;
      __int64 folder_id_;
   };
}
