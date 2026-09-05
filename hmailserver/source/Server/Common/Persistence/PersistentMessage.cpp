// Copyright (c) 2005 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"

#include "PersistentMessage.h"
#include "../Application/IniFileSettings.h"
#include "../Util/Strings/Formatter.h"
#include "PersistentDomain.h"
#include "PersistentAccount.h"
#include "PersistentIMAPFolder.h"
#include "PersistentAlias.h"
#include "PersistentDistributionList.h"
#include "PersistentServerMessage.h"
#include "PersistentMessageMetaData.h"
#include "PersistentMessageIndex.h"
#include "../Util/MailerDaemonAddressDeterminer.h"
#include "../../SMTP/SMTPConfiguration.h"
#include "../../IMAP/IMAPConfiguration.h"
#include "../../SMTP/RecipientParser.h"
#include "../BO/Account.h"
#include "../BO/Message.h"
#include "../BO/MessageRecipient.h"
#include "../BO/MessageRecipients.h"
#include "../BO/MessageData.h"
#include "../BO/ServerMessages.h"
#include "../BO/IMAPFolder.h"
#include "../Util/File.h"
#include "../Util/Time.h"
#include "../Util/GUIDCreator.h"
#include "../Cache/CacheContainer.h"
#include "../Cache/AccountSizeCache.h"
#include "..\Util\FolderManipulationLock.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   PersistentMessage::PersistentMessage()
   {
      
   }

   PersistentMessage::~PersistentMessage()
   {

   }

   bool
   PersistentMessage::DeleteObject(std::shared_ptr<Message> pMessage)
   {
      LOG_DEBUG("Deleting message");
      __int64 iMessageID = pMessage->GetID();

      if (iMessageID > 0)
      {
         SQLCommand command(_T("delete from hm_messages where messageid = @MESSAGEID"));
         command.AddParameter("@MESSAGEID", iMessageID);
         
         if (!Application::Instance()->GetDBManager()->Execute(command))
            return false;

         // RFC 7162 (QRESYNC): when a message belonging to an IMAP folder is expunged, record a
         // tombstone (folder + UID + a freshly bumped mod-sequence) so the server can report the
         // removal via "* VANISHED (EARLIER)" to clients resyncing after a disconnect.
         {
            __int64 expungeAccountID = pMessage->GetAccountID();
            __int64 expungeFolderID = pMessage->GetFolderID();

            if (expungeAccountID > 0 && expungeFolderID > 0)
            {
               __int64 expungeModSeq = PersistentIMAPFolder::GetNextModSeq(expungeAccountID, expungeFolderID);
               PersistentIMAPFolder::AddExpunged(expungeAccountID, expungeFolderID, pMessage->GetUID(), expungeModSeq);
            }
         }

         // If the message is placed into an account, there won't be any recipients
         // connected to it. If the message is still in the queue, we must delete the
         // recipients as well.
         if (pMessage->GetState() != Message::Delivered)
         {
            // Delete recipients.
            SQLCommand deleteCommand("delete from hm_messagerecipients where recipientmessageid = @MESSAGEID");
            deleteCommand.AddParameter("@MESSAGEID", iMessageID);
            
            if (!Application::Instance()->GetDBManager()->Execute(deleteCommand))
            {
               return false;
            }
         }


         // Update the account size cache.
         if (pMessage->GetAccountID() > 0)
         {
            AccountSizeCache::Instance()->ModifySize(pMessage->GetAccountID(), pMessage->GetSize(), false);
         }

         // Delete meta-data connected to this message.
         if (Configuration::Instance()->GetMessageIndexing())
         {
            PersistentMessageMetaData md;
            md.DeleteForMessage(pMessage);
         }

         // And its full-text terms. The indexer's orphan sweep would collect
         // these eventually, so this is promptness rather than correctness: a
         // search only ever iterates a folder's live messages, so a dead
         // message's terms cannot produce a wrong answer, only dead rows.
         PersistentMessageIndex::DeleteForMessage(iMessageID);

         // Reset the message ID.
         pMessage->SetID(0);

         std::shared_ptr<const Account> account;

         if (pMessage->GetAccountID() > 0)
         {
            account = CacheContainer::Instance()->GetAccount(pMessage->GetAccountID());

            // Without the account we cannot build the account-folder path. GetFileName
            // then falls back on a public-folder path (the folder id of a delivered
            // message is non-zero), deleting that path "succeeds" because nothing is
            // there, and the real file is left on disk with no row pointing at it -
            // invisible to quota, to expunge and to the consistency scan, which only
            // walks rows. Say so rather than leaking silently.
            if (!account)
            {
               String logMessage;
               logMessage.Format(_T("PersistentMessage::DeleteObject - account %I64d could not be loaded for message %I64d, so its file may be left on disk. File name: %s"),
                  pMessage->GetAccountID(), iMessageID, pMessage->GetPartialFileName().c_str());

               LOG_APPLICATION(logMessage);
            }
         }

         if (!DeleteFile(account, pMessage))
         {
            return false;
         }
      }

      return true;
   }

   bool
   PersistentMessage::GetMessageID(const String &fileName, __int64 &messageID, bool &isPartialFilename)
   {
      messageID = 0;
      isPartialFilename = false;

      // Create a partial file name from the full path.
      String partialFileName;
      if (GetPartialFilename(fileName, partialFileName))
      {
         // Check if the partial file name exists.
         SQLCommand command("select messageid from hm_messages where messagefilename = @FILENAME");
         command.AddParameter("@FILENAME", partialFileName);

         std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
         if (!pRS)
            return false;

         if (!pRS->IsEOF())
         {
            messageID = pRS->GetInt64Value("messageid");
            isPartialFilename = true;
            return true;
         }
      }

      // Check if the full path exists.
      SQLCommand command("select messageid from hm_messages where messagefilename = @FILENAME");
      command.AddParameter("@FILENAME", fileName);

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!pRS)
         return false;

      if (!pRS->IsEOF())
      {
         messageID = pRS->GetInt64Value("messageid");
         return true;
      }

      return true;
      
   }

   bool
   PersistentMessage::DeleteFile(std::shared_ptr<const Account> account, std::shared_ptr<Message> pMessage)
   {
      if (pMessage->GetPartialFileName().IsEmpty())
         return true;

      String messageFile = GetFileName(account, pMessage);

      String sLogMessage;
      sLogMessage.Format(_T("Deleting message file."));
      LOG_DEBUG(sLogMessage);

      // We do not allow deletion of file if the message still
      // exists in the database.

      if (pMessage->GetID() > 0)
      {
         // A message with this ID already exists. Disallow deletion and log to 
         // the event logger.
         String sErrorMessage;
         sErrorMessage.Format(_T("Tried to delete the file %s even though the message was not deleted."), messageFile.c_str());

         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5025, "PersistentAccount::DeleteFile", sErrorMessage);
         return false;
      }


      bool bResult = FileUtilities::DeleteFile(messageFile);   

      return bResult;
   }

   bool
   PersistentMessage::UnlockAll()
   {
      SQLCommand command("update hm_messages set messagelocked = 0 where messagetype = 1 and messagelocked = 1");
      return Application::Instance()->GetDBManager()->Execute(command);
   }

   bool 
   PersistentMessage::ReadObject(std::shared_ptr<DALRecordset> pRS, std::shared_ptr<Message> pMessage, bool bReadRecipients)
   {
      pMessage->SetID(pRS->GetInt64Value("messageid"));
      pMessage->SetAccountID(pRS->GetLongValue("messageaccountid"));
      pMessage->SetPartialFileName(pRS->GetStringValue("messagefilename"));

      pMessage->SetState((Message::State) pRS->GetLongValue("messagetype"));
      pMessage->SetFromAddress(pRS->GetStringValue("messagefrom"));
      pMessage->SetCreateTime(pRS->GetStringValue("messagecreatetime"));
      pMessage->SetSaveDate(pRS->GetStringValue("messagesavedate"));
      pMessage->SetEmailId(pRS->GetStringValue("messageemailid"));
      pMessage->SetSize(pRS->GetLongValue("messagesize"));
      pMessage->SetNoOfRetries((unsigned short) pRS->GetLongValue("messagecurnooftries"));
      pMessage->SetFolderID(pRS->GetLongValue("messagefolderid"));

      pMessage->SetFlags((short) pRS->GetLongValue("messageflags"));
      pMessage->SetUID((unsigned int) pRS->GetLongValue("messageuid"));
      pMessage->SetModSeq(pRS->GetInt64Value("messagemodseq"));

      if (bReadRecipients)
      {
         // The message recipients has been parsed.
         //
         // Unchecked, and this function then returned true - so a database failure
         // while loading the recipients produced a Message object that looked
         // completely valid and had none. SMTPDeliverer::DeliverMessage sees a message
         // with no recipients, reports HM5007 "No remaining recipients", and DELETES
         // it. A transient database error while reading one table therefore destroyed a
         // queued message that had nothing wrong with it.
         //
         // ReadRecipients_ answers false only when OpenRecordset fails, i.e. only for a
         // real database error - never for a message that legitimately has none - so
         // failing the load here cannot refuse anything that should have been accepted.
         if (!ReadRecipients_(pMessage))
            return false;
      }


      if (pMessage->GetFolderID() > 0 && pMessage->GetUID() == 0)
      {
         // May be removed if it turns out not to result in any error.
         String sErrorMessage;
         sErrorMessage.Format(_T("The message %I64d in mailbox %I64d in account %I64d has no UID set."), 
            pMessage->GetID(), pMessage->GetFolderID(), pMessage->GetAccountID());

         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5025, "PersistentMessage::ReadObject", sErrorMessage);         
      }

      return true;
   }

   bool
   PersistentMessage::ReadRecipients_(std::shared_ptr<Message> pMessage)
   {
   
      std::shared_ptr<MessageRecipients> pRecipients = pMessage->GetRecipients();

      SQLCommand command("select * from hm_messagerecipients where recipientmessageid = @MESSAGEID");
      command.AddParameter("@MESSAGEID", pMessage->GetID());

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!pRS)
         return false;

      while (pRS->IsEOF() == false)
      {
     
         std::shared_ptr<MessageRecipient> pRecipient = std::shared_ptr<MessageRecipient>(new MessageRecipient());

         pRecipient->SetAddress(pRS->GetStringValue("recipientaddress"));
         pRecipient->SetLocalAccountID(pRS->GetLongValue("recipientlocalaccountid"));
         pRecipient->SetMessageID(pRS->GetLongValue ("recipientmessageid"));
         pRecipient->SetOriginalAddress(pRS->GetStringValue("recipientoriginaladdress"));
         pRecipient->SetDSNNotify((int) pRS->GetLongValue("recipientdsnnotify"));

         String sAddress = pRecipient->GetAddress();

         if (!MailerDaemonAddressDeterminer::IsMailerDaemonAddress(sAddress))
         {
            if (sAddress.IsEmpty())
               ErrorManager::Instance()->ReportError(ErrorManager::Medium, 4201, "PersistentAccount::ReadRecipients_", "Read recipient from database without an address.");
            else
               pRecipients->Add(pRecipient);
         }

         pRS->MoveNext();
      }

   
      return true;
   }

   bool 
   PersistentMessage::ReadObject(std::shared_ptr<Message> pMessage, const SQLCommand &command)
   {
      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);

      if (!pRS || pRS->IsEOF())
         return false;

      // The result was discarded and true returned regardless, which is the second
      // half of the same hole: even once ReadObject above can say the recipients did
      // not load, this would have gone on reporting a complete message.
      return ReadObject(pRS, pMessage);
   }

   bool 
   PersistentMessage::ReadObject(std::shared_ptr<Message> pMessage, __int64 ObjectID)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Reads an object from the database.
   //---------------------------------------------------------------------------()
   {

      SQLCommand command("select * from hm_messages where messageid = @MESSAGEID");
      command.AddParameter("@MESSAGEID", ObjectID);

      return ReadObject(pMessage, command);

   }

   bool
   PersistentMessage::SaveRecipients_(std::shared_ptr<Message> pMessage)
   {
      std::vector<std::shared_ptr<MessageRecipient> > vecRecipients = pMessage->GetRecipients()->GetVector();
      auto iterRecipient = vecRecipients.begin();

      while (iterRecipient != vecRecipients.end())
      {
         std::shared_ptr<MessageRecipient> pRecipient = (*iterRecipient);

         // Check that the recipient address is really specified
         if (pRecipient->GetAddress().IsEmpty() && pRecipient->GetLocalAccountID() == 0)
         {
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 4224, "PersistentMessage::SaveRecipients_", "Tried to save recipient without an address.");

            iterRecipient++;
            continue;
         }

         // Update message ID in memory.
         // ONLY if not already set or dupe emails issue
         // https://www.progressiverobot.com/forum/viewtopic.php?f=10&t=21404
         if (pRecipient->GetMessageID() == 0) 
            pRecipient->SetMessageID(pMessage->GetID());

         // Do the saving
         SQLStatement oStatement;
         oStatement.AddColumnInt64("recipientmessageid", pRecipient->GetMessageID());
         oStatement.AddColumn("recipientaddress", pRecipient->GetAddress());
         oStatement.AddColumnInt64("recipientlocalaccountid", pRecipient->GetLocalAccountID());
         oStatement.AddColumn("recipientoriginaladdress", pRecipient->GetOriginalAddress());
         oStatement.AddColumnInt64("recipientdsnnotify", pRecipient->GetDSNNotify());

         oStatement.SetTable ("hm_messagerecipients");
         oStatement.SetStatementType(SQLStatement::STInsert);
         oStatement.SetIdentityColumn("recipientid");
      
         bool bResult = Application::Instance()->GetDBManager()->Execute(oStatement);

         if (!bResult)
            return false;

         iterRecipient++;

      }

      return true;
   }

   bool
   PersistentMessage::LockObject(__int64 ObjectID)
   {
      ASSERT(ObjectID > 0);

      SQLCommand command("update hm_messages set messagelocked = 1 where messageid = @MESSAGEID");
      command.AddParameter("@MESSAGEID", ObjectID);

      return Application::Instance()->GetDBManager()->Execute(command);
   
      return false;
   }

   bool
   PersistentMessage::LockObject(std::shared_ptr<Message> pMessage)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Locks the message in the database.
   //---------------------------------------------------------------------------()
   {
      return LockObject (pMessage->GetID());
   }  

   bool
   PersistentMessage::UnlockObject(std::shared_ptr<Message> pMessage)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Unlocks the object in the database.
   //---------------------------------------------------------------------------()
   {
      ASSERT(pMessage->GetID() > 0);

      SQLCommand command("update hm_messages set messagelocked = 0 where messageid = @MESSAGEID");
      command.AddParameter("@MESSAGEID", pMessage->GetID());

      return Application::Instance()->GetDBManager()->Execute(command);
   }

   std::shared_ptr<Message>
   PersistentMessage::CopyToQueue(std::shared_ptr<const Account> sourceAccount, std::shared_ptr<Message> sourceMessage)
   {
      std::shared_ptr<Message> newMessage = CreateCopy_(sourceMessage, 0);
      newMessage->SetState(Message::Delivering);

      // Copy the message file.
      const String sourceFile = GetFileName(sourceAccount, sourceMessage);
      const String destinationFile = GetFileName(newMessage, QueueFolder);

      if (!FileUtilities::Copy(sourceFile, destinationFile, true))
      {
         std::shared_ptr<Message> pEmpty;
         return pEmpty;
      }

      return newMessage;
   }

   std::shared_ptr<Message>
   PersistentMessage::CopyToIMAPFolder(std::shared_ptr<Message> sourceMessage, std::shared_ptr<IMAPFolder> destinationFolder)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Copies a message's bytes into another IMAP folder, which may belong to a
   // DIFFERENT account than the one the message is in.
   //
   // Both accounts are derived here, from the objects themselves, rather than
   // passed in - and that is the whole point of this function's shape.
   //
   // It used to take a single `sourceAccount` and use it for both the source
   // path and the destination path, under a comment that read "Copy within the
   // same account". That was true for as long as it was true: before the
   // "#Users" namespace, a folder was only ever reachable by the account that
   // owned it, so one account really did serve both ends.
   //
   // Opening delegated mailboxes turned that assumption into silent mail loss.
   // A delegate copying into somebody else's folder wrote the bytes under the
   // CALLER's directory while the row named the OWNER's folder, so no read path
   // could ever resolve the file - FETCH answered with the "file does not exist"
   // placeholder - and MOVE compounded it by expunging the source afterwards,
   // destroying the only copy anyone could still read. The read paths had
   // already been corrected for this (IMAPConnection::GetAccountOwningCurrentFolder);
   // the write paths had not.
   //
   // Deriving both ends from the source message and the destination folder makes
   // the mismatch unrepresentable, which matters more than fixing the three call
   // sites: a caller that CAN pass the wrong account eventually does.
   //---------------------------------------------------------------------------()
   {
      std::shared_ptr<Message> messageCopy = CreateCopy_(sourceMessage, (int) destinationFolder->GetAccountID());
      messageCopy->SetState(Message::Delivered);
      messageCopy->SetFolderID(destinationFolder->GetID());

      // The source bytes live under the account that owns the SOURCE message. An
      // account id of zero is a public-folder message, where the address is not
      // part of the path and an empty one is correct.
      String sourceAddress;

      if (sourceMessage->GetAccountID() > 0)
      {
         std::shared_ptr<const Account> sourceAccount =
            CacheContainer::Instance()->GetAccount(sourceMessage->GetAccountID());

         // Refusing beats guessing. Falling back to any other account would name
         // a directory that has never held this message, and the copy would
         // "succeed" against a file that is not there.
         if (!sourceAccount)
            return std::shared_ptr<Message>();

         sourceAddress = sourceAccount->GetAddress();
      }

      const String sourceFile = GetFileName(sourceAddress, sourceMessage);

      String destinationFile;

      if (destinationFolder->IsPublicFolder())
      {
         destinationFile = GetFileName(messageCopy, PublicFolder);
      }
      else
      {
         // The copy belongs under the account that owns the DESTINATION folder,
         // which is the owner for a delegated folder and the caller for an
         // ordinary one.
         std::shared_ptr<const Account> destinationAccount =
            CacheContainer::Instance()->GetAccount(destinationFolder->GetAccountID());

         if (!destinationAccount)
            return std::shared_ptr<Message>();

         destinationFile = GetFileName(destinationAccount->GetAddress(), messageCopy, AccountFolder);
      }

      if (!FileUtilities::Copy(sourceFile, destinationFile, true))
      {
         std::shared_ptr<Message> pEmpty;
         return pEmpty;
      }

      return messageCopy;
   }

   std::shared_ptr<Message>
   PersistentMessage::CopyFromQueueToInbox(std::shared_ptr<Message> sourceMessage, std::shared_ptr<const Account> destinationAccount, const String &linkFrom, bool &linked)
   {
      std::shared_ptr<Message> messageCopy = CreateCopy_(sourceMessage, (int) destinationAccount->GetID());
      messageCopy->SetState(Message::Delivered);

      // Locate the inbox ID
      __int64  inboxID = CacheContainer::Instance()->GetInboxIDCache().GetUserInboxFolder(destinationAccount->GetID());
      if (inboxID == 0)
      {
         std::shared_ptr<Message> empty;
         return empty;
      }

      messageCopy->SetFolderID(inboxID);

      const String sourceFile = GetFileName(sourceMessage, QueueFolder);
      const String destinationFile = GetFileName(destinationAccount, messageCopy, AccountFolder);
      String destinationPath = FileUtilities::GetFilePath(destinationFile);

      // One file, this recipient's name for it - when LocalDelivery has a finished
      // copy to link from (DeliveryHardLinks). Every rewrite of a message file is
      // a temporary file renamed into place, so a change to one recipient's copy
      // never reaches the others. Falls back to a copy of the queue file when the
      // link cannot be made (another volume, the 1023-name limit of NTFS, a file
      // system without links), which is exactly the behaviour without the setting.
      linked = false;
      if (!linkFrom.IsEmpty())
      {
         FileUtilities::CreateDirectory(destinationPath);
         linked = ::CreateHardLink(destinationFile, linkFrom, NULL) != FALSE;
         LOG_DEBUG(Formatter::Format(linked ? "Local copy {0} is a name for {1}." : "Local copy {0} could not be linked from {1} (Windows error {2}); copied instead.", destinationFile, linkFrom, (int) GetLastError()));
      }

      if (!linked && !FileUtilities::Copy(sourceFile, destinationFile, true))
      {
         std::shared_ptr<Message> pEmpty;
         return pEmpty;
      }

      return messageCopy;
   }

   std::shared_ptr<Message>
   PersistentMessage::CreateCopy_(std::shared_ptr<Message> sourceMessage, int destinationAccountID)
   {
      LOG_DEBUG("Copying mail contents");
      std::shared_ptr<Message> pTo = std::shared_ptr<Message>(new Message(true));

      std::shared_ptr<MessageRecipients> pToRecipients = pTo->GetRecipients();
      pToRecipients->Clear();

      pTo->SetAccountID(destinationAccountID);
      pTo->SetSize(sourceMessage->GetSize());
      pTo->SetFromAddress(sourceMessage->GetFromAddress());
      pTo->SetState(sourceMessage->GetState());
      pTo->SetCreateTime(sourceMessage->GetCreateTime());

      // RFC 8474: a copy is the same message content, so it keeps the EMAILID -
      // the exact opposite of the save date, which a copy gets fresh.
      pTo->SetEmailId(sourceMessage->GetEmailId());

      // RFC 3030's binary mark rides along in here: it is bit 256 of the flags
      // now, so a copy of a binary message is still binary without a second
      // statement that could be forgotten.
      pTo->SetFlags(sourceMessage->GetFlags());

      return pTo;
   }

   bool
   PersistentMessage::SaveObject(std::shared_ptr<Message> pMessage, String &errorMessage, PersistenceMode mode)
   {
      // errorMessage - not supported yet.
      return SaveObject(pMessage);
   }


   bool
   PersistentMessage::SaveObject(std::shared_ptr<Message> pMessage)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Saves the object in the database. If the message already exist, it is
   // updated. After a message has been added, the message-id of it is updated.
   //---------------------------------------------------------------------------()
   {
   
      if (pMessage->GetState() == Message::Created)
      {
         // The message should either be delivered, or it should currently
         // be delivering.
         ErrorManager::Instance()->ReportError(ErrorManager::High, 5111, "PersistentMessage::SaveObject", "The message could not be saved in the database. Message state is 'Created'");
         return false;
      }

      bool bNewMessage = pMessage->GetID() == 0;

      if (pMessage->GetAccountID() > 0)
      {
         // Increase account size cache.
         if (pMessage->GetUID() == 0)
         {
            AccountSizeCache::Instance()->ModifySize(pMessage->GetAccountID(), pMessage->GetSize(), true);
         }
      }

      if (!AddObject(pMessage))
         return false;

      // A DELIVERED message being saved again is a message whose file may just
      // have been rewritten - the COM API lets a script set Body and Save, and
      // the rules engine edits headers. Its full-text terms now describe
      // content that is no longer there, and stale terms do not merely waste
      // space: the index is consulted to prove a message CANNOT contain a
      // string, so a term set describing the old body can exclude a message
      // that now genuinely matches. That is a wrong search result, which is
      // worse than a slow one. Dropping the terms here returns the message to
      // "not indexed", where every search reads it exactly as it did before the
      // index existed, until the indexer re-reads it.
      if (!bNewMessage && pMessage->GetState() == Message::Delivered)
         PersistentMessageIndex::DeleteForMessage(pMessage->GetID());

      // Should we fetch the message id now?
      if (bNewMessage && pMessage->GetRecipients()->GetCount())
      {
         // If there are any recipients, save them in the database.
         if (!SaveRecipients_(pMessage))
            return false;

         // Message is now completely saved so we may unlock it so that
         // it can be delivered by the SMTPDeliveryManager.
         SQLCommand command("update hm_messages set messagelocked = 0 where messageid = @MESSAGEID");
         command.AddParameter("@MESSAGEID", pMessage->GetID());

         bool bResult = Application::Instance()->GetDBManager()->Execute(command);
         
         if (!bResult)
            return false;

      }

      return true;
   }

   bool 
   PersistentMessage::AddObject(const std::shared_ptr<Message> pMessage)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Adds an object to the database. If the message already exists, 
   // it is updated. The contents of pMessage is not modified, so don't
   // use this if you want to be able to fetch thed ID after just having
   // inserted the ID.
   //---------------------------------------------------------------------------()
   {
      if (!pMessage->GetSize())
      {
         LOG_DEBUG("Aborting save since the message is zero bytes.");
         return false;
      }

      if (pMessage->GetState() == Message::Delivered && pMessage->GetFolderID() == 0)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 5213, "PersistentMessage::AddObject", "Aborting save since no folder was specified.");
         return false;
      }

      bool bNewObject = true;
      if (pMessage->GetID())
         bNewObject = false;

      // If recipients exists, we need to save the message as locked
      // since the recipients aren't saved in the database yet.
      bool bRecipientsExists = pMessage->GetRecipients()->GetCount() > 0;

      SQLStatement oStatement;
      oStatement.SetTable("hm_messages");

      if (bNewObject)
         pMessage->SetFlagRecent(true);

      oStatement.AddColumnInt64("messageaccountid", pMessage->GetAccountID());
      oStatement.AddColumn("messagefilename", pMessage->GetPartialFileName());
      oStatement.AddColumn("messagetype", pMessage->GetState());
      oStatement.AddColumn("messagefrom", pMessage->GetFromAddress());
      oStatement.AddColumn("messagesize", pMessage->GetSize());
      oStatement.AddColumn("messageflags", pMessage->GetFlags());
      oStatement.AddColumnInt64("messagefolderid", pMessage->GetFolderID());
    
      LOG_DEBUG("Saving message: " + pMessage->GetPartialFileName());

      // We need to retrieve a new unique ID for this folder. Lock it.
      FolderManipulationLock folderLock((int) pMessage->GetAccountID(), (int) pMessage->GetFolderID());
      
      if (pMessage->GetState() == Message::Delivered)
      {
         // If we're placing messages in a mailbox, we must synchronize the access to it.
         // The message UID's must be inserted in a strictly ascending fashion.
         folderLock.Lock();   
      }
   
      /*
         Check if this message is moved into a mailbox. If it is, we need to assign 
         an UID to the message.
      */
      if (pMessage->GetFolderID() > 0 && pMessage->GetUID() == 0 )
      {
         // Retrieve the last Unique ID for this folder.
         unsigned currentUID = PersistentIMAPFolder::GetUniqueMessageID(pMessage->GetAccountID(), pMessage->GetFolderID());

         if (currentUID == 0)
         {
            String errorMessage;
            errorMessage.Format(_T("Failed to generate UID for message. Account ID: %I64d, Folder ID: %I64d"), pMessage->GetAccountID(), pMessage->GetFolderID());
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5205, "PersistentMessage::AddObject", errorMessage);

            return false;
         }

         // Set back the new UID to the message.
         pMessage->SetUID(currentUID);

         oStatement.AddColumnInt64("messageuid", pMessage->GetUID());

         // RFC 7162 (CONDSTORE/QRESYNC): a message arriving in a mailbox is a
         // metadata change, so assign it the next per-mailbox mod-sequence.
         __int64 newModSeq = PersistentIMAPFolder::GetNextModSeq(pMessage->GetAccountID(), pMessage->GetFolderID());
         if (newModSeq > 0)
            pMessage->SetModSeq(newModSeq);

         oStatement.AddColumnInt64("messagemodseq", pMessage->GetModSeq());
      }
      else
      {
         // Save the already existing UID. This happens for example if we update an existing
         // message or restore a backup.
         oStatement.AddColumnInt64("messageuid", pMessage->GetUID());

         oStatement.AddColumnInt64("messagemodseq", pMessage->GetModSeq());
      }


      if (bNewObject)
      {
         
         // Save current time as create time
         String sCreateTime = pMessage->GetCreateTime();
         if (sCreateTime.IsEmpty())
         {
            sCreateTime = Time::GetCurrentDateTime();
            pMessage->SetCreateTime(sCreateTime);
         }

         oStatement.SetStatementType(SQLStatement::STInsert);
         oStatement.SetIdentityColumn("messageid");

         oStatement.AddColumn("messagelocked", bRecipientsExists ? 1 : 0);
         oStatement.AddColumn("messagecreatetime", sCreateTime);

         // RFC 8514: a new row IS a save into a mailbox, so the save date is
         // now - never the client-supplied APPEND date, which only shapes the
         // create time / INTERNALDATE. CreateCopy_ deliberately does not carry
         // this field, so a COPY's row gets a fresh save date here too.
         String sSaveDate = Time::GetCurrentDateTime();
         pMessage->SetSaveDate(sSaveDate);
         oStatement.AddColumn("messagesavedate", sSaveDate);

         // RFC 8474: the email id is stamped once, at the first save, and kept
         // for the row's life. CreateCopy_ carries it into copies on purpose -
         // the same content keeps the same EMAILID - so only a message that
         // does not have one yet gets one here.
         if (pMessage->GetEmailId().IsEmpty())
         {
            String emailId = _T("M") + GUIDCreator::GetGUID();
            emailId.Replace(_T("{"), _T(""));
            emailId.Replace(_T("}"), _T(""));
            emailId.Replace(_T("-"), _T(""));
            pMessage->SetEmailId(emailId);
         }

         oStatement.AddColumn("messageemailid", pMessage->GetEmailId());
         oStatement.AddColumn("messagecurnooftries", 0);
         oStatement.AddColumn("messagenexttrytime",  "1901-01-01");
      }
      else
      {
         oStatement.SetStatementType(SQLStatement::STUpdate);

         String sWhere;
         sWhere.Format(_T("messageid = %I64d"), pMessage->GetID());

         oStatement.SetWhereClause(sWhere);

      }

      __int64 iDBID = 0;
      bool bRetVal = Application::Instance()->GetDBManager()->Execute(oStatement, bNewObject ? &iDBID : 0);
      if (bRetVal && bNewObject)
      {
         pMessage->SetID(iDBID);
      }

      return bRetVal;
   }

   bool
   PersistentMessage::SetNextTryTime(__int64 iMessageID, bool bUpdateNoOfTries, long lNoOfMinutes)
   {
      LOG_DEBUG("PersistentMessage::SetNextTryTime()");

      String sUpdateSQL = Formatter::Format("update hm_messages set messagenexttrytime = {0} ", SQLStatement::GetCurrentTimestampPlusMinutes(lNoOfMinutes));
      
      // This is needed because of ETRN/HOLD to force type back to 1
      // to tell queue to try delivering again
      sUpdateSQL += " , messagetype = 1 ";

      if (bUpdateNoOfTries)
         sUpdateSQL += " , messagecurnooftries = messagecurnooftries + 1 ";
   
      // To prevent already delivered messages from being updated
      // () needed around messagetypes to fix order of evaluation
      // otherwise all HOLD's mistakenly queued & ID not reset to 0
      String sWhereClause = _T("where messageid = @MESSAGEID and (messagetype = 1 or messagetype = 3)");

      sUpdateSQL += sWhereClause;

      SQLCommand command(sUpdateSQL);
      command.AddParameter("@MESSAGEID", iMessageID);

      bool bResult = Application::Instance()->GetDBManager()->Execute(command);

      LOG_DEBUG("PersistentMessage::~SetNextTryTime()");

      return bResult;
   }

   bool
   PersistentMessage::MoveFileToPublicFolder(const String &sourceLocation, std::shared_ptr<Message> pMessage)
   {
      String dataDirectory = IniFileSettings::Instance()->GetDataDirectory();
      String publicFolder = FileUtilities::Combine(dataDirectory, IMAPConfiguration::GetPublicFolderDiskName());

      String destinationFileName = GetFileName(pMessage, PublicFolder);
      String destinationPath = FileUtilities::GetFilePath(destinationFileName);

      // Before we move the file to the new path, make sure that the directory exists.
      // We start by checking if it already exists. If not, attempt to create. We used
      // to create each folder before. Checking first will save some disk access.
      if (!FileUtilities::Exists(destinationPath))
         FileUtilities::CreateDirectory(destinationPath);

      // Move the old file to the new path.
      if (!FileUtilities::Move(sourceLocation, destinationFileName))
         return false;

      return true;
   }

   bool
   PersistentMessage::MoveFileToUserFolder(const String &sourceLocation, std::shared_ptr<Message> pMessage, std::shared_ptr<const Account> destinationAccount)
   {
      String dataDirectory = IniFileSettings::Instance()->GetDataDirectory();

      String domainName = StringParser::ExtractDomain(destinationAccount->GetAddress());
      String mailboxName  = StringParser::ExtractAddress(destinationAccount->GetAddress());
         
      String destinationFileName = GetFileName(destinationAccount->GetAddress(), pMessage, AccountFolder);
      String destinationPath = FileUtilities::GetFilePath(destinationFileName);

      // Before we move the file to the new path, make sure that the directory exists.
      // We start by checking if it already exists. If not, attempt to create. We used
      // to create each folder before. Checking first will save some disk access.
      if (!FileUtilities::Exists(destinationPath))
         FileUtilities::CreateDirectory(destinationPath);

      // Move the old file to the new path.
      if (!FileUtilities::Move(sourceLocation, destinationFileName))
         return false;

      return true;
   }

   void
   PersistentMessage::EnsureFileExistance(std::shared_ptr<const Account> account, std::shared_ptr<Message> pMessage)
   {
      String sFileName = GetFileName(account, pMessage);
      if (FileUtilities::Exists(sFileName))
         return;

      // File doesn't exist. We need to create it, so that we can deliver
      // something useful to the client. We start of by creating the directory
      // in which the message should be put. If the dir doesn't exist, we'll
      // have slight problems creating a file in it.
      String sPath = FileUtilities::GetFilePath(sFileName);

      if (!FileUtilities::Exists(sPath) && !FileUtilities::CreateDirectory(sPath))
      {
         // Nothing below can succeed without the directory, and the message row must
         // be left describing the message it was written for rather than a placeholder
         // that does not exist. CreateDirectory has already reported the reason.
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6012, "PersistentMessage::EnsureFileExistance",
            "The directory for message file " + sFileName + " could not be created, so no placeholder could be written for the missing message. The message row was left unchanged.");
         return;
      }

      // The file does not exists. May have been deleted
      // by anti virus software.
      String sMessageUndeliverable = Configuration::Instance()->GetServerMessages()->GetMessage("MESSAGE_UNDELIVERABLE");
      String sMessageBody = Configuration::Instance()->GetServerMessages()->GetMessage("MESSAGE_FILE_MISSING");

      // Replace macros
      sMessageBody.Replace(_T("%MACRO_FILE%"), sFileName);

      String sErrorMessage;
      sErrorMessage.Format(_T("From: Postmaster\r\n")
                           _T("Subject: %s\r\n")
                           _T("Date: %s\r\n")
                           _T("\r\n")
                           _T("%s")
                           _T("\r\n"),
                           sMessageUndeliverable.c_str(),
                           Time::GetCurrentMimeDate().c_str(),
                           sMessageBody.c_str());

      if (!FileUtilities::WriteToFile(sFileName, sErrorMessage, false))
      {
         // This result used to be dropped, and the size below was then taken from a
         // file which does not exist - FileUtilities::FileSize answers 0 for that. A
         // zero size makes AddObject refuse the update ("message is zero bytes"), so
         // the failure was invisible twice over, and the caller went on to stream a
         // message whose row claimed a size nothing on disk could satisfy.
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6012, "PersistentMessage::EnsureFileExistance",
            "A placeholder could not be written for the missing message file " + sFileName + ". The message row was left unchanged.");
         return;
      }

      // Update the database with the new size of the file. Both are long, so that
      // FileSize is stored without a narrowing conversion of its own; the conversion
      // into SetSize is the one this code has always made.
      const long previousSize = pMessage->GetSize();
      const long placeholderSize = FileUtilities::FileSize(sFileName);

      pMessage->SetSize(placeholderSize);

      // Save the new size.
      if (!SaveObject(pMessage))
      {
         // The row still claims the size of the message that is gone. Worth a line in
         // the log, because the next consistency report will find the file present and
         // the size wrong, and this is the reason.
         String logMessage;
         logMessage.Format(_T("PersistentMessage::EnsureFileExistance - the size of message %I64d could not be updated to that of the placeholder written for its missing file. File: %s"),
            pMessage->GetID(), sFileName.c_str());

         LOG_APPLICATION(logMessage);
      }
      else if (pMessage->GetAccountID() > 0)
      {
         // The row just shrank from the size of the message to the size of the
         // placeholder, but the cached account size - which is what quota enforcement
         // in LocalDelivery and IMAP QUOTA both report - was left at the old value. The
         // account therefore stayed charged for a message that no longer exists on disk
         // until the cache happened to be dropped, and an account close to its limit
         // stopped accepting mail because of bytes that were already gone.
         //
         // Two calls rather than one signed delta: the placeholder can be larger than
         // the message it replaces (a one-line message), and the direction of a
         // subtraction that can go either way is easy to get wrong.
         AccountSizeCache::Instance()->ModifySize(pMessage->GetAccountID(), previousSize, false);
         AccountSizeCache::Instance()->ModifySize(pMessage->GetAccountID(), placeholderSize, true);
      }

      // Log the error.
      ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5026, "PersistentMessage::EnsureFileExistance", "Message retrieval failed because message file " + sFileName + " did not exist.");
   }

   bool
   PersistentMessage::GetAllMessageFilesAreInDataFolder()
   {
      String sDataDir = IniFileSettings::Instance()->GetDataDirectory();

      int iLen = sDataDir.GetLength();
      
      String leftFilenameDataDir = SQLStatement::GetLeftFunction("messagefilename", iLen);
      String leftFilenameFirstChar = SQLStatement::GetLeftFunction("messagefilename", 1);


      SQLCommand command(Formatter::Format("select count(*) as msgcount from hm_messages where {0} <> @DATADIR and {1} <> @BRACE", leftFilenameDataDir, leftFilenameFirstChar));
      command.AddParameter("@DATADIR", sDataDir);
      command.AddParameter("@BRACE", "{");

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!pRS)
         return false;

      long lMsgCount = pRS->GetLongValue("msgcount");

      if (lMsgCount == 0)
         return true;
      else
         return false;
   }

   bool
   PersistentMessage::GetAllMessageFilesArePartialNames()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Returns true if all message files in the database only have their partial
   // named stored (i.e. {abc} rather than C:\datadir\{abc}
   //---------------------------------------------------------------------------()
   {
      String leftFilenameFirstChar = SQLStatement::GetLeftFunction("messagefilename", 1);

      SQLCommand command(Formatter::Format("select count(*) as msgcount from hm_messages where {0} <> @BRACE", leftFilenameFirstChar));
      command.AddParameter("@BRACE", "{");

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!pRS)
         return false;

      long lMsgCount = pRS->GetLongValue("msgcount");

      if (lMsgCount == 0)
         return true;
      else
         return false;
   }


   int
   PersistentMessage::GetTotalMessageCount()
   {
      SQLCommand command("select count(*) as c from hm_messages");

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!pRS)
         return false;

      int result = pRS->GetLongValue("c");

      return result;
   }

   int
   PersistentMessage::GetLatestMessageId()
   {
      SQLCommand command("select max(messageid) as m from hm_messages");

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!pRS)
         return false;

      int result = pRS->GetLongValue("m");

      return result;
   }

   int
   PersistentMessage::GetTotalMessageCountDelivered()
   {
      SQLCommand command ("select count(*) as c from hm_messages where messagetype = @MESSAGETYPE");
      command.AddParameter("@MESSAGETYPE", Message::Delivered);
      
      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!pRS)
         return false;

      int result = pRS->GetLongValue("c");

      return result;
   }

   int
   PersistentMessage::GetDeliveryQueueCount()
   {
      // Messages awaiting/undergoing SMTP delivery (the delivery queue shown in
      // the Administrator), i.e. those in the Delivering state.
      SQLCommand command ("select count(*) as c from hm_messages where messagetype = @MESSAGETYPE");
      command.AddParameter("@MESSAGETYPE", Message::Delivering);

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!pRS)
         return 0;

      int result = pRS->GetLongValue("c");

      return result;
   }

   int
   PersistentMessage::GetMissingFileCount()
   {
      return static_cast<int>(GetMissingFileDetails().size());
   }

   std::vector<String>
   PersistentMessage::GetMissingFileDetails()
   {
      // Walk every message row and confirm its backing file exists on disk. The
      // account address (needed to resolve account-folder paths) is joined in so
      // GetFileName can reconstruct the exact on-disk path the server would use.
      // Returns one tab-separated "messageid<TAB>account<TAB>expected-path" line
      // per message whose backing file is missing.
      std::vector<String> missing;

      SQLCommand command(
         "select m.messageid, m.messagefilename, m.messageaccountid, m.messagefolderid, a.accountaddress "
         "from hm_messages m left join hm_accounts a on m.messageaccountid = a.accountid");

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!pRS)
         return missing;

      while (!pRS->IsEOF())
      {
         String partialFileName = pRS->GetStringValue("messagefilename");

         if (!partialFileName.IsEmpty())
         {
            std::shared_ptr<Message> message = std::shared_ptr<Message>(new Message);
            message->SetPartialFileName(partialFileName);
            message->SetAccountID(pRS->GetLongValue("messageaccountid"));
            message->SetFolderID(pRS->GetLongValue("messagefolderid"));

            String accountAddress = pRS->GetStringValue("accountaddress");
            String fullFileName = GetFileName(accountAddress, message);

            if (!FileUtilities::Exists(fullFileName))
            {
               String line;
               line.Format(_T("%I64d\t%s\t%s"),
                  pRS->GetInt64Value("messageid"),
                  accountAddress.c_str(),
                  fullFileName.c_str());
               missing.push_back(line);
            }
         }

         pRS->MoveNext();
      }

      return missing;
   }

   bool
   PersistentMessage::DeleteByAccountID(__int64 iAccountID)
   {
      // The account address is selected alongside the rows because messagefilename
      // normally holds only the {guid}.eml part; the directory it lives in has to be
      // reconstructed from the address and the message's location. This used to hand
      // the partial name straight to DeleteFile, which resolved it against the
      // process working directory, found nothing there, and answered "deleted" -
      // FileUtilities::DeleteFile treats an absent file as success. Every message file
      // in the account was therefore left on disk after its row had gone.
      SQLCommand selectCommand(
         "select m.messagefilename, m.messageaccountid, m.messagefolderid, a.accountaddress "
         "from hm_messages m left join hm_accounts a on m.messageaccountid = a.accountid "
         "where m.messageaccountid = @ACCOUNTID");
      selectCommand.AddParameter("@ACCOUNTID", iAccountID);

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(selectCommand);
      if (!pRS)
         return false;

      SQLCommand deleteCommand ("delete from hm_messages where messageaccountid =  @ACCOUNTID");
      deleteCommand.AddParameter("@ACCOUNTID", iAccountID);

      bool bDeleteOK = Application::Instance()->GetDBManager()->Execute(deleteCommand);
      if (!bDeleteOK)
         return false;

      while (!pRS->IsEOF())
      {
         String partialFileName = pRS->GetStringValue("messagefilename");

         if (!partialFileName.IsEmpty())
         {
            // false: this message exists only to resolve a path, so it must not be
            // given a freshly generated file name.
            std::shared_ptr<Message> message = std::shared_ptr<Message>(new Message(false));
            message->SetPartialFileName(partialFileName);
            message->SetAccountID(pRS->GetLongValue("messageaccountid"));
            message->SetFolderID(pRS->GetLongValue("messagefolderid"));

            String sFileName = GetFileName(pRS->GetStringValue("accountaddress"), message);

            if (!FileUtilities::DeleteFile(sFileName))
            {
               String sErrorMessage;
               sErrorMessage.Format(_T("Failed to delete file %s while deleting messages in account %I64d"), sFileName.c_str(), iAccountID);

               ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5024, "PersistentAccount::DeleteMessages", sErrorMessage);
            }
         }

         pRS->MoveNext();
      }

      return true;
   }

   AnsiString
   PersistentMessage::LoadHeader(const String &fileName)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Reads the entire message from the disk.
   //---------------------------------------------------------------------------()
   {
      return LoadHeader(fileName, true);
   }

   AnsiString
   PersistentMessage::LoadHeader(const String &fileName, bool reportError)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Reads the entire message from the disk.
   //---------------------------------------------------------------------------()
   {
      // 50000 seems inefficient to read in headers especially since default cluster is 4096
      // Let's allow user to define READ size but keep buffer hard-coded
      int iHeaderReadSize = IniFileSettings::Instance()->GetLoadHeaderReadSize();
      const int iReadBufferSize = 50000;

      // We need to take care not to overflow buffer. A non-positive size in the ini file
      // would reach ReadFile as a huge DWORD, so it falls back to the buffer size as well.
      if (iHeaderReadSize <= 0 || iHeaderReadSize > iReadBufferSize) iHeaderReadSize = iReadBufferSize;

      String sHeaderData; 

      HANDLE handleFile;

      handleFile = CreateFile(fileName, 
         GENERIC_READ, 
         FILE_SHARE_READ, 
         NULL, // LPSECURITY_ATTRIBUTES
         OPEN_EXISTING, // -- open or create.
         FILE_ATTRIBUTE_NORMAL, // attributes
         NULL // file template
         );

      if (handleFile == INVALID_HANDLE_VALUE || handleFile < 0) 
      {
         if (reportError)
         {
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 4403, "PersistentMessage::LoadHeader", "Could not read the message header, since the file was not available. File: " + fileName);
         }

         return sHeaderData;
      }

      int iHeaderEnd = -1;

      // On the heap: 50 KB on the stack of an IOCP or delivery thread is a
      // quarter of what some of them have, and this runs on every message read.
      std::vector<BYTE> bufferStorage(iReadBufferSize + 1);
      BYTE *buf = &bufferStorage[0];

      unsigned long nbytes = 0;
      BOOL bMoreData = TRUE;
      int nBytesSent = 0;

      while (bMoreData)
      {
         // We're using defined read size vs buffer size (read will always be <= buffer due to test above)
         if (!ReadFile(handleFile,buf,iHeaderReadSize, &nbytes, NULL))
         {
            // End of file is reported as a successful read of zero bytes, so a failure here is a
            // real I/O error. The data collected so far may stop in the middle of the header, and
            // returning it would give the caller a truncated header that looks complete.
            int iLastError = ::GetLastError();

            CloseHandle(handleFile);

            if (reportError)
            {
               String sErrorMessage;
               sErrorMessage.Format(_T("Could not read the message header. Windows error code: %d. File: %s"), iLastError, fileName.c_str());

               ErrorManager::Instance()->ReportError(ErrorManager::Medium, 4403, "PersistentMessage::LoadHeader", sErrorMessage);
            }

            return "";
         }

         if (nbytes)
         {
            sHeaderData += AnsiString((char*)buf, nbytes);
         }
         else
            bMoreData = FALSE;

         // Check if we have read the entire header.
         iHeaderEnd = sHeaderData.Find(_T("\r\n\r\n"));

         if (iHeaderEnd >= 0)
            bMoreData = FALSE;

      }

      CloseHandle(handleFile);

      if (iHeaderEnd == -1)
         return sHeaderData;

      iHeaderEnd += 2;

      return sHeaderData.Left(iHeaderEnd);
   }

   AnsiString
   PersistentMessage::LoadBody(const String &fileName)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Reads the entire message from the disk.
   //---------------------------------------------------------------------------()
   {
      // 10000 seems inefficient since default cluster is 4096
      // Let's allow user to define READ size but keep buffer hard-coded
      int iBodyReadSize = IniFileSettings::Instance()->GetLoadBodyReadSize();
      const int iReadBufferSize = 50000;

      // We need to take care not to overflow buffer. A non-positive size in the ini file
      // would reach ReadFile as a huge DWORD, so it falls back to the buffer size as well.
      if (iBodyReadSize <= 0 || iBodyReadSize > iReadBufferSize) iBodyReadSize = iReadBufferSize;

      HANDLE handleFile = CreateFile(fileName, 
         GENERIC_READ, 
         FILE_SHARE_READ, 
         NULL, // LPSECURITY_ATTRIBUTES
         OPEN_EXISTING, // -- open or create.
         FILE_ATTRIBUTE_NORMAL, // attributes
         NULL // file template
         );

      if (handleFile == INVALID_HANDLE_VALUE || handleFile < 0) 
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 4403, "PersistentMessage::LoadBody", "Could not read the message body, since the file was not available. File: " + fileName);
         return "";
      }

      int iHeaderEnd = -1;

      // On the heap: 50 KB on the stack of an IOCP or delivery thread is a
      // quarter of what some of them have, and this runs on every message read.
      std::vector<BYTE> bufferStorage(iReadBufferSize + 1);
      BYTE *buf = &bufferStorage[0];

      unsigned long nbytes = 0;
      int nBytesSent = 0;
      bool foundHeader = false;

      AnsiString retVal; 

      while (true)
      {
         // We're using defined read size vs buffer size (read will always be <= buffer due to test above)
         if (!ReadFile(handleFile,buf,iBodyReadSize, &nbytes, NULL))
         {
            // End of file is reported as a successful read of zero bytes, so a failure here is a
            // real I/O error. Returning the part of the body read so far would silently hand the
            // caller a truncated message.
            int iLastError = ::GetLastError();

            CloseHandle(handleFile);

            String sErrorMessage;
            sErrorMessage.Format(_T("Could not read the message body. Windows error code: %d. File: %s"), iLastError, fileName.c_str());

            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 4403, "PersistentMessage::LoadBody", sErrorMessage);

            return "";
         }

         if (!nbytes)
            break;

         AnsiString readData = AnsiString((char*)buf, nbytes);

         // Check if we have read the entire header.
         if (!foundHeader)
         {
            iHeaderEnd = readData.Find("\r\n\r\n");
            if (iHeaderEnd >= 0)
            {
               int startReadPos = iHeaderEnd+4;
               int remainingLength = nbytes -startReadPos;
               if(remainingLength>0)
               {
                  readData = readData.Mid(startReadPos, remainingLength );
                  foundHeader = true;
               }
            }
         }

         if (foundHeader)
            retVal.append(readData);

      }

      CloseHandle(handleFile);

      return retVal;
   }

   String 
   PersistentMessage::GetFileName(std::shared_ptr<const Message> message)
   {
      std::shared_ptr<Account> account;

      return GetFileName(account, message);
   }

   String 
   PersistentMessage::GetFileName(std::shared_ptr<const Message> message, FileLocation location)
   {
      return GetFileName("", message, location);
   }

   String 
   PersistentMessage::GetFileName(std::shared_ptr<const Account> account, std::shared_ptr<const Message> message)
   {
      String accountAddress = account ? account->GetAddress() : "";

      return GetFileName(accountAddress, message);
   }

   String 
   PersistentMessage::GetFileName(std::shared_ptr<const Account> account, std::shared_ptr<const Message> message, FileLocation location)
   {
      String accountAddress = account ? account->GetAddress() : "";

      return GetFileName(accountAddress, message, location);
   }

   String 
   PersistentMessage::GetFileName(const String &accountAddress, std::shared_ptr<const Message> message)
   {
      FileLocation location;

      if (accountAddress.GetLength() > 0 && message->GetAccountID() > 0)
      {
         location = AccountFolder;
      }
      else if (message->GetFolderID() > 0)
      {
         location = PublicFolder;
      }
      else
      {
         location = QueueFolder;
      }

      return GetFileName(accountAddress, message, location);
   }

   String 
   PersistentMessage::GetFileName(const String &accountAddress, std::shared_ptr<const Message> message, FileLocation location)
   {
      String partialFileName = message->GetPartialFileName();

      if (FileUtilities::IsFullPath(partialFileName))
         return partialFileName;

      String dataDirectory = IniFileSettings::Instance()->GetDataDirectory();

      String fullFileName;

      switch (location)
      {
      case AccountFolder:
         {
            // Message is placed in an account folder.
            String domainName = StringParser::ExtractDomain(accountAddress);
            String domainFolder = FileUtilities::Combine(dataDirectory, domainName);

            String accountFolderName = StringParser::ExtractAddress(accountAddress);
            String accountFolder = FileUtilities::Combine(domainFolder, accountFolderName);

            // The message is placed in a folder containing the two first characters of the guid file name.
            String guidFolder = FileUtilities::Combine(accountFolder, partialFileName.Mid(1,2));

            fullFileName = FileUtilities::Combine(guidFolder, partialFileName);
            break;
         }
      case PublicFolder:
         {
            // Message is placed in public folder.
            String publicFolder = FileUtilities::Combine(dataDirectory, IMAPConfiguration::GetPublicFolderDiskName());

            // The message is placed in a folder containing the two first characters of the guid file name.
            String guidFolder = FileUtilities::Combine(publicFolder, partialFileName.Mid(1,2));

            fullFileName = FileUtilities::Combine(guidFolder, partialFileName);
            break;
         }
      case QueueFolder:
         {
             fullFileName = FileUtilities::Combine(dataDirectory, partialFileName);        
             break;
         }
      }

      return fullFileName;
   }


   bool
   PersistentMessage::GetPartialFilename(const String &fullPath, String &partialPath)
   {
      // The file must be located in the data directory. Make sure this is the case.
      const String dataDirectory = IniFileSettings::Instance()->GetDataDirectory();

      if (!fullPath.StartsWith(dataDirectory))
         return false;

      // Trim away the data directory
      String filePath = fullPath.Mid(dataDirectory.GetLength() + 1);

      // Is the file in the public folder?
      String publicFolderName = IMAPConfiguration::GetPublicFolderDiskName();

      if (filePath.StartsWith(publicFolderName))
      {
         // Trim it away.
         filePath = filePath.Mid(publicFolderName.GetLength() + 1);

         int guidSlashPos = filePath.Find(FileUtilities::PathSeparator);
         if (guidSlashPos <= 0)
            return false;

         // Make sure the message is located in a correctly named folder.
         if (guidSlashPos != 2)
            return false;

         // Mid(0, guidSlashPos), not Mid(guidSlashPos). The one-argument form returns
         // everything FROM the separator onwards ("\{guid}.eml") rather than the
         // two-character fan-out folder in front of it, so the comparison below could
         // never succeed and no path inside the public folder was ever recognised as
         // part of the message store. Two consequences: GetMessageID could not find a
         // public-folder message by its partial name, so the importer took an
         // already-imported message for a new one; and MailImporter, told it could not
         // build a partial name, generated a new GUID and moved a file that was already
         // correctly placed - a move that can fail and take the message with it. The
         // account-folder branch below always used the two-argument form and was fine.
         String lastLevelName = filePath.Mid(0, guidSlashPos);

         filePath = filePath.Mid(guidSlashPos+1);

         // Compared without case: this is a Windows path, where the fan-out directory
         // that was created from these two characters and the two characters in the
         // name are the same folder whatever case the caller wrote them in. A
         // case-sensitive comparison here rejected the path and sent the file off to be
         // renamed and moved for no reason.
         if (lastLevelName.CompareNoCase(filePath.Mid(1,2).c_str()) != 0)
            return false;
      }
      else
      {
         // Is the file located in a sub directory? (In a domain folder).
         int domainSlashPos = filePath.Find(FileUtilities::PathSeparator);
         if (domainSlashPos >= 0)
         {
            // Yes, we need to trim it away. 
            int accountSlashPos = filePath.Find(FileUtilities::PathSeparator, domainSlashPos+1);
            if (accountSlashPos <= 0)
               return false;

            int guidSlashPos = filePath.Find(FileUtilities::PathSeparator, accountSlashPos+1);
            if (guidSlashPos <= 0)
               return false;

            // Make sure the message is located in a correctly named folder.
            int lastLevelLength = guidSlashPos - accountSlashPos-1;
            if (lastLevelLength != 2)
               return false;

            String lastLevelName = filePath.Mid(accountSlashPos+1, lastLevelLength);

            filePath = filePath.Mid(guidSlashPos+1);

            // Case-insensitive for the same reason as in the public-folder branch above.
            if (lastLevelName.CompareNoCase(filePath.Mid(1,2).c_str()) != 0)
               return false;

         }
      }

      partialPath = filePath;
      return true;
   }

   bool 
   PersistentMessage::SaveFlags(std::shared_ptr<Message> message)
   {
      // RFC 7162 (CONDSTORE/QRESYNC): a flag change is a metadata change, so assign
      // the message the next per-mailbox mod-sequence and persist it alongside the flags.
      __int64 newModSeq = PersistentIMAPFolder::GetNextModSeq(message->GetAccountID(), message->GetFolderID());
      if (newModSeq > 0)
         message->SetModSeq(newModSeq);

      // Create a statement object.
      String statement = "UPDATE hm_messages SET messageflags = @FLAGS, messagemodseq = @MODSEQ WHERE messageid = @MESSAGEID";

      SQLCommand sqlCommand(statement);
      sqlCommand.AddParameter("@FLAGS", message->GetFlags());
      sqlCommand.AddParameter("@MODSEQ", message->GetModSeq());
      sqlCommand.AddParameter("@MESSAGEID", message->GetID());

      return Application::Instance()->GetDBManager()->Execute(sqlCommand);
   }

   bool 
   PersistentMessage::IsPartialPath(const String &path)
   {
      return !path.Contains(FileUtilities::PathSeparator);
   }
}
