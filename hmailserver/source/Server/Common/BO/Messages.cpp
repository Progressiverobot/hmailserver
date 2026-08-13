// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "Messages.h"

using namespace std;

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{


   Messages::Messages(__int64 iAccountID, __int64 iFolderID) :
      account_id_(iAccountID),
      folder_id_(iFolderID),
      last_refreshed_uid_(0)
   {

   }

   Messages::~Messages()
   {
   
   }

   std::vector<std::shared_ptr<Message>>
   Messages::GetCopy()
   {
      boost::lock_guard<boost::recursive_mutex> guard(_mutex);

      std::vector<std::shared_ptr<Message>> result;

      for(std::shared_ptr<Message> oMessage : vecObjects)
      {
         std::shared_ptr<Message> messageCopy = std::shared_ptr<Message>(new Message(*oMessage.get()));
         result.push_back(messageCopy);
      }

      return result;

   }

   long
   Messages::GetNoOfSeen() const
   {
      boost::lock_guard<boost::recursive_mutex> guard(_mutex);

      long lNoOfSeen = 0;

      for(std::shared_ptr<Message> oMessage : vecObjects)
      {
         if (oMessage->GetFlagSeen()) 
            lNoOfSeen ++;
      }

      return lNoOfSeen;
   }

   long
   Messages::GetSize() const
   {
      boost::lock_guard<boost::recursive_mutex> guard(_mutex);

      long lSize = 0;

      for(std::shared_ptr<Message> oMessage : vecObjects)
      {
         lSize += oMessage->GetSize();
      }

      return lSize;
   }

   __int64
   Messages::GetFirstUnseenUID() const
   {
      boost::lock_guard<boost::recursive_mutex> guard(_mutex);

      for(std::shared_ptr<Message> message : vecObjects)
      {
         if (!message->GetFlagSeen())
            return message->GetUID();
      }

      return 0;
   }

   __int64
   Messages::GetFirstUnseenSequenceNumber() const
   {
      boost::lock_guard<boost::recursive_mutex> guard(_mutex);

      __int64 sequence_number = 0;

      for(std::shared_ptr<Message> message : vecObjects)
      {
         sequence_number++;

         if (!message->GetFlagSeen())
            return sequence_number;
      }

      return 0;
   }

   std::set<__int64>
   Messages::GetRecentMessageIDs() const
   {
      boost::lock_guard<boost::recursive_mutex> guard(_mutex);

      std::set<__int64> recent_message_ids;

      for(std::shared_ptr<Message> message : vecObjects)
      {
         if (message->GetFlagRecent())
            recent_message_ids.insert(message->GetID());
      }

      return recent_message_ids;
   }


   void
   Messages::Save()
   {
      boost::lock_guard<boost::recursive_mutex> guard(_mutex);

      LOG_DEBUG("Messages::Save()");

      for(std::shared_ptr<Message> oMessage : vecObjects)
      {
         LOG_DEBUG("Messages::Save() - Iteration");

         // Both were unchecked. No mail is lost either way - the file and the row are
         // both still there - but this is where IMAP flag changes are persisted, so a
         // failure means a client was told its STORE succeeded and will find the flags
         // back as they were at the next reconnect, with nothing anywhere explaining
         // it. Reported rather than propagated: Save() has no way to answer, its
         // callers are mid-command, and one message failing is not a reason to abandon
         // the rest of the folder.
         if (oMessage->GetFlagDeleted())
         {
            if (!PersistentMessage::DeleteObject(oMessage))
            {
               ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6093, "Messages::Save",
                  Formatter::Format("Message {0} is flagged as deleted but could not be removed from the database. It will still be there at the next refresh.", oMessage->GetID()));
            }
         }
         else
         {
            if (!PersistentMessage::SaveObject(oMessage))
            {
               ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6094, "Messages::Save",
                  Formatter::Format("Changes to message {0} could not be saved. Any flags just set on it will revert at the next refresh.", oMessage->GetID()));
            }
         }
      }

      LOG_DEBUG("Messages::~Save()");
   
   }

   void
   Messages::DeleteMessages(std::function<bool(int, std::shared_ptr<Message>)> &filter)
   {
      boost::lock_guard<boost::recursive_mutex> guard(_mutex);

      std::vector<int> vecExpungedMessages;
      auto iterMessage = vecObjects.begin();

      int index = 0;
      while (iterMessage != vecObjects.end())
      {
         index++;

         std::shared_ptr<Message> message = (*iterMessage);

         if (filter(index, message))
         {
            // This is what an IMAP EXPUNGE runs, and the delete was unchecked while
            // the erase below happened regardless - so a message the database refused
            // to delete was still removed from the collection, the client was sent an
            // untagged EXPUNGE for it, and every message after it renumbered. It comes
            // back at the next reconnect, in a mailbox whose numbering the client
            // believes it already knows.
            //
            // Left in place on failure instead, so the sequence numbers the client is
            // given match the mailbox that actually exists.
            if (!PersistentMessage::DeleteObject(message))
            {
               ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6107, "Messages::DeleteMessages",
                  Formatter::Format("Message {0} could not be deleted, so it has been kept rather than reported to the client as expunged.", message->GetID()));

               iterMessage++;
               continue;
            }

            iterMessage = vecObjects.erase(iterMessage);
            index--;
         }
         else
            iterMessage++;
      }

   }


   void
   Messages::Refresh(bool update_recent_flags)
   {
      boost::lock_guard<boost::recursive_mutex> guard(_mutex);

	  // int startTime = GetTickCount();

      bool retrieveQueue = account_id_ == -1;

      if (retrieveQueue && last_refreshed_uid_ > 0)
      {
         /*
            We can't do partial refreshes of messages in the queue. Why? 
            Because we use the message UID to determine which part of the
            queue we need to read from the database, and UID's aren't given
            to messages before they are inserted into the queue.
         */
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5204, "Messages::Refresh", "Refresh not supported on the current collection.");
         return;

      }

      SQLCommand command;

      // Build SQL statement that will be used to fetch list of messages.
      String sSQL;
      sSQL = "select * from hm_messages where "; 

      if (retrieveQueue)
         sSQL += " messagetype = 1 ";
      else
      {
         // Messages connected to a specific account
         sSQL += _T(" messageaccountid = @MESSAGEACCOUNTID ");
         command.AddParameter("@MESSAGEACCOUNTID", account_id_); 
      }
  
      // Should we fetch a specific folder?
      if (folder_id_ != -1)
      {
         sSQL.AppendFormat(_T(" and messagefolderid = @MESSAGEFOLDERID "));
         command.AddParameter("@MESSAGEFOLDERID", folder_id_); 
      }

      // Should we do an incremental refresh?
      if (last_refreshed_uid_ > 0)
      {
         sSQL.AppendFormat(_T(" and messageuid > @MESSAGEUID "));
         command.AddParameter("@MESSAGEUID", last_refreshed_uid_); 
      }

      if (retrieveQueue)
         sSQL += " order by messageid asc";
      else
         sSQL += " order by messageuid asc";


      command.SetQueryString(sSQL);

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!pRS)
         return;

      AddToCollection(pRS);

     
   }

   void
   Messages::AddToCollection(std::shared_ptr<DALRecordset> pRS)
   {
      boost::lock_guard<boost::recursive_mutex> guard(_mutex);

      if (!pRS->IsEOF())
      {
         long lRecCount = pRS->RecordCount();
         if (lRecCount < 0)
            lRecCount = 0;

		 LOG_DEBUG("Reading messages from database.");

         vecObjects.reserve(vecObjects.size() + lRecCount);

         while (!pRS->IsEOF())
         {
            std::shared_ptr<Message> msg = std::shared_ptr<Message> (new Message(false));
            PersistentMessage::ReadObject(pRS, msg, false);
                  
            vecObjects.push_back(msg);

            lRecCount--;

            pRS->MoveNext();
         }

         std::shared_ptr<Message> pLastMessage = vecObjects[vecObjects.size() -1];
         last_refreshed_uid_ = pLastMessage->GetUID();
      }
   }

   void  
   Messages::RemoveRecentFlags()
   {
      boost::lock_guard<boost::recursive_mutex> guard(_mutex);

      for(std::shared_ptr<Message> message : vecObjects)
      {
         message->SetFlagRecent(false);
      }

      // When a message is added to the database, the \Recent flag is set. When this message loads the message
      // list from the database, the \Recent flag will be set to the message in memory. Future sessions should
      // not see the message \Recent flag, so we remove the flag from all messages in the account folder now.
      if (account_id_ != -1 && folder_id_ != -1)
      {
         // Mark all messages as recent
         String sql = "update hm_messages set messageflags = messageflags & ~ @FLAGS where messageaccountid = @ACCOUNTID and messagefolderid = @FOLDERID";

         SQLCommand updateCommand;
         updateCommand.AddParameter("@FLAGS", Message::FlagRecent);
         updateCommand.AddParameter("@ACCOUNTID", account_id_);
         updateCommand.AddParameter("@FOLDERID", folder_id_);
         updateCommand.SetQueryString(sql);

         Application::Instance()->GetDBManager()->Execute(updateCommand);
      }


   }

   bool
   Messages::PreSaveObject(std::shared_ptr<Message> pMessage, XNode *node)
   {
      boost::lock_guard<boost::recursive_mutex> guard(_mutex);

      pMessage->SetAccountID(account_id_);
      pMessage->SetFolderID(folder_id_);
      return true;
   }

   void 
   Messages::Remove(__int64 iDBID)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Removes a message from the message list. This function does not delete
   // the message from the actual database. It should only be used to remove
   // an object, that has already been deleted in the database.
   //---------------------------------------------------------------------------()
   {
      boost::lock_guard<boost::recursive_mutex> guard(_mutex);

      auto iterMessage = vecObjects.begin();

      // Locate the message
      while (iterMessage != vecObjects.end() && (*iterMessage)->GetID() != iDBID)
         iterMessage++;

      // If the message is found, remove it from the list now
      if (iterMessage != vecObjects.end())
         vecObjects.erase(iterMessage);
   }

   std::shared_ptr<Message>
   Messages::GetItemByUID(unsigned int uid)
   {
      unsigned int dummy = 0;
      return GetItemByUID(uid, dummy);
   }


   std::shared_ptr<Message>
   Messages::GetItemByUID(unsigned int uid, unsigned int &foundIndex)
   {
      boost::lock_guard<boost::recursive_mutex> guard(_mutex);
      foundIndex = 0;
      for(std::shared_ptr<Message> item : vecObjects)
      {
         foundIndex++;

         if (item->GetUID() == uid)
            return item;
      }

      std::shared_ptr<Message> empty;
      return empty;
   }

}
