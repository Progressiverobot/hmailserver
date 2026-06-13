// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "IMAPCommandUID.h"
#include "IMAPConnection.h"
#include "IMAPSimpleCommandParser.h"


#include "IMAPFetch.h"
#include "IMAPCopy.h"
#include "IMAPMove.h"
#include "IMAPStore.h"
#include "IMAPCommandSearch.h"

#include "MessagesContainer.h"

#include "../Common/BO/ACLPermission.h"
#include "../Common/BO/IMAPFolder.h"
#include "../Common/BO/Message.h"
#include "../Common/BO/Messages.h"
#include "../Common/Persistence/PersistentIMAPFolder.h"
#include "../Common/Tracking/ChangeNotification.h"
#include "../Common/Tracking/NotificationServer.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // Parses an IMAP UID sequence-set (e.g. "1,3:5,8:*") into inclusive [first,last]
      // ranges. "*" is represented as the maximum UID value.
      std::vector<std::pair<unsigned int, unsigned int>> ParseUidSet_(const String &sUidSet)
      {
         std::vector<std::pair<unsigned int, unsigned int>> ranges;

         std::vector<String> parts = StringParser::SplitString(sUidSet, ",");
         for (const String &part : parts)
         {
            if (part.IsEmpty())
               continue;

            int colonPos = part.Find(_T(":"));
            if (colonPos >= 0)
            {
               String first = part.Mid(0, colonPos);
               String second = part.Mid(colonPos + 1);

               unsigned int start = (unsigned int) _ttoi(first);
               unsigned int end = (second == _T("*")) ? (unsigned int) 0xFFFFFFFF : (unsigned int) _ttoi(second);

               if (end < start)
               {
                  unsigned int swap = start;
                  start = end;
                  end = swap;
               }

               ranges.push_back(std::pair<unsigned int, unsigned int>(start, end));
            }
            else if (part == _T("*"))
            {
               ranges.push_back(std::pair<unsigned int, unsigned int>((unsigned int) 0, (unsigned int) 0xFFFFFFFF));
            }
            else
            {
               unsigned int uid = (unsigned int) _ttoi(part);
               ranges.push_back(std::pair<unsigned int, unsigned int>(uid, uid));
            }
         }

         return ranges;
      }
   }

   IMAPCommandUID::IMAPCommandUID()
   {

   }

   IMAPCommandUID::~IMAPCommandUID()
   {

   }


   IMAPResult
   IMAPCommandUID::ExecuteCommand(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument)
   {
      if (!pConnection->IsAuthenticated())
         return IMAPResult(IMAPResult::ResultNo, "Authenticate first");

      String sTag = pArgument->Tag();
      String sCommand = pArgument->Command();

      if (!pConnection->GetCurrentFolder())
         return IMAPResult(IMAPResult::ResultNo, "No folder selected.");

      std::shared_ptr<IMAPSimpleCommandParser> pParser = std::shared_ptr<IMAPSimpleCommandParser>(new IMAPSimpleCommandParser());

      pParser->Parse(pArgument);
      
      if (pParser->WordCount() < 2)
         return IMAPResult(IMAPResult::ResultBad, "Command requires at least 1 parameter.");

      String sTypeOfUID = pParser->Word(1)->Value();

      if (sTypeOfUID.CompareNoCase(_T("FETCH")) == 0)
      {
         if (pParser->WordCount() < 4)
            return IMAPResult(IMAPResult::ResultBad, "Command requires at least 3 parameters.");

         command_ = std::shared_ptr<IMAPFetch>(new IMAPFetch());
      }
      else if (sTypeOfUID.CompareNoCase(_T("COPY")) == 0)
      {

         if (pParser->WordCount() < 4)
            return IMAPResult(IMAPResult::ResultBad, "Command requires at least 3 parameters.");

         command_ = std::shared_ptr<IMAPCopy>(new IMAPCopy());
      }
      else if (sTypeOfUID.CompareNoCase(_T("MOVE")) == 0)
      {
         if (pParser->WordCount() < 4)
            return IMAPResult(IMAPResult::ResultBad, "Command requires at least 3 parameters.");

         if (pConnection->GetCurrentFolderReadOnly())
            return IMAPResult(IMAPResult::ResultNo, "MOVE command on read-only folder.");

         if (!pConnection->CheckPermission(pConnection->GetCurrentFolder(), ACLPermission::PermissionExpunge))
            return IMAPResult(IMAPResult::ResultBad, "ACL: Expunge permission denied (Required for MOVE command).");

         std::shared_ptr<IMAPMove> pMove = std::shared_ptr<IMAPMove>(new IMAPMove());
         pMove->SetIsUID(true);

         // Copy the first word containing the message sequence
         long lUidSecWordStartPos = sCommand.Find(_T(" "), 5) + 1;
         long lUidSecWordEndPos = sCommand.Find(_T(" "), lUidSecWordStartPos);
         long lUidSecWordLength = lUidSecWordEndPos - lUidSecWordStartPos;
         String sUidMailNo = sCommand.Mid(lUidSecWordStartPos, lUidSecWordLength);

         String sUidShowPart = sCommand.Mid(lUidSecWordEndPos + 1);

         if (sUidMailNo.IsEmpty())
            return IMAPResult(IMAPResult::ResultBad, "No mail number specified");

         if (!StringParser::ValidateString(sUidMailNo, "01234567890,.:*"))
            return IMAPResult(IMAPResult::ResultBad, "Incorrect mail number");

         pArgument->Command(sUidShowPart);

         IMAPResult result = pMove->DoForMails(pConnection, sUidMailNo, pArgument);

         if (result.GetResult() == IMAPResult::ResultOK)
         {
            String sUidPlus = pMove->GetUIDPlusResponseCode();
            pMove->ExpungeMovedMessages(pConnection);
            pConnection->SendAsciiData(sTag + " OK " + sUidPlus + "UID MOVE completed\r\n");
         }

         return result;
      }
      else if (sTypeOfUID.CompareNoCase(_T("STORE")) == 0)
      {
         if (pParser->WordCount() < 4)
            return IMAPResult(IMAPResult::ResultBad, "Command requires at least 3 parameters.");

         command_ = std::shared_ptr<IMAPStore>(new IMAPStore());
      }
      else if (sTypeOfUID.CompareNoCase(_T("SEARCH")) == 0)
      {
         std::shared_ptr<IMAPCommandSEARCH> pCommand = std::shared_ptr<IMAPCommandSEARCH> (new IMAPCommandSEARCH(false));
         pCommand->SetIsUID();
         IMAPResult result = pCommand->ExecuteCommand(pConnection, pArgument);

         if (result.GetResult() == IMAPResult::ResultOK)
            pConnection->SendAsciiData(sTag + " OK UID completed\r\n");

         return result;
      }
      else if (sTypeOfUID.CompareNoCase(_T("SORT")) == 0)
      {
         std::shared_ptr<IMAPCommandSEARCH> pCommand = std::shared_ptr<IMAPCommandSEARCH> (new IMAPCommandSEARCH(true));
         pCommand->SetIsUID();
         IMAPResult result = pCommand->ExecuteCommand(pConnection, pArgument);
         
         if (result.GetResult() == IMAPResult::ResultOK)
            pConnection->SendAsciiData(sTag + " OK UID completed\r\n");

         return result;
      }
      else if (sTypeOfUID.CompareNoCase(_T("EXPUNGE")) == 0)
      {
         // RFC 4315 (UIDPLUS): UID EXPUNGE permanently removes only the messages
         // that are both flagged \Deleted and contained in the supplied UID set.
         if (pParser->WordCount() < 3)
            return IMAPResult(IMAPResult::ResultBad, "Command requires a UID set.");

         if (pConnection->GetCurrentFolderReadOnly())
            return IMAPResult(IMAPResult::ResultNo, "Expunge command on read-only folder.");

         std::shared_ptr<IMAPFolder> pCurFolder = pConnection->GetCurrentFolder();
         if (!pCurFolder)
            return IMAPResult(IMAPResult::ResultNo, "No folder selected.");

         if (!pConnection->CheckPermission(pCurFolder, ACLPermission::PermissionExpunge))
            return IMAPResult(IMAPResult::ResultBad, "ACL: Expunge permission denied (Required for UID EXPUNGE command).");

         String sUidSet = pParser->Word(2)->Value();
         if (sUidSet.IsEmpty() || !StringParser::ValidateString(sUidSet, "01234567890,.:*"))
            return IMAPResult(IMAPResult::ResultBad, "Incorrect mail number");

         std::vector<std::pair<unsigned int, unsigned int>> ranges = ParseUidSet_(sUidSet);

         std::vector<__int64> expunged_messages_index;
         std::vector<__int64> expunged_messages_uid;
         std::vector<__int64> vanished_uids;

         std::function<bool(int, std::shared_ptr<Message>)> filter = [&expunged_messages_index, &expunged_messages_uid, &vanished_uids, &ranges](int index, std::shared_ptr<Message> message)
         {
            if (!message->GetFlagDeleted())
               return false;

            unsigned int uid = message->GetUID();
            for (const std::pair<unsigned int, unsigned int> &range : ranges)
            {
               if (uid >= range.first && uid <= range.second)
               {
                  expunged_messages_index.push_back(index);
                  expunged_messages_uid.push_back(message->GetID());
                  vanished_uids.push_back(uid);
                  return true;
               }
            }

            return false;
         };

         auto messages = MessagesContainer::Instance()->GetMessages(pCurFolder->GetAccountID(), pCurFolder->GetID());
         messages->DeleteMessages(filter);

         String sResponse;
         if (pConnection->GetQResyncEnabled() && !vanished_uids.empty())
         {
            // RFC 7162 (QRESYNC): report expunges as a single "* VANISHED" UID set.
            sResponse.Format(_T("* VANISHED %s\r\n"), IMAPConnection::CompactUidSet(vanished_uids).c_str());
         }
         else
         {
            for (__int64 expungedIndex : expunged_messages_index)
            {
               String sTemp;
               sTemp.Format(_T("* %d EXPUNGE\r\n"), (int) expungedIndex);
               sResponse += sTemp;
            }
         }

         pConnection->SendAsciiData(sResponse);

         if (!expunged_messages_uid.empty())
         {
            std::shared_ptr<ChangeNotification> pNotification =
               std::shared_ptr<ChangeNotification>(new ChangeNotification(pCurFolder->GetAccountID(), pCurFolder->GetID(), ChangeNotification::NotificationMessageDeleted, expunged_messages_index));

            Application::Instance()->GetNotificationServer()->SendNotification(pConnection->GetNotificationClient(), pNotification);
         }

         pConnection->SendAsciiData(sTag + " OK UID EXPUNGE completed\r\n");

         return IMAPResult();
      }


      if (!command_)
         return IMAPResult(IMAPResult::ResultBad, "Bad command.");

      command_->SetIsUID(true);

      // Copy the first word containing the message sequence
      long lSecWordStartPos = sCommand.Find(_T(" "), 5) + 1;
      long lSecWordEndPos = sCommand.Find(_T(" "), lSecWordStartPos);
      long lSecWordLength = lSecWordEndPos - lSecWordStartPos;
      String sMailNo = sCommand.Mid(lSecWordStartPos, lSecWordLength);
      
      // Copy the second word containing the actual command.
      String sShowPart = sCommand.Mid(lSecWordEndPos + 1);

      if (sMailNo.IsEmpty())
         return IMAPResult(IMAPResult::ResultBad, "No mail number specified");

      if (!StringParser::ValidateString(sMailNo, "01234567890,.:*"))
         return IMAPResult(IMAPResult::ResultBad, "Incorrect mail number");

      // RFC 7162 (QRESYNC): "UID FETCH <set> (… CHANGEDSINCE n VANISHED)" asks the server to also
      // report, via "* VANISHED (EARLIER)", the UIDs in <set> expunged since mod-sequence n.
      bool fetchVanished = false;
      __int64 fetchVanishedSince = 0;
      if (sTypeOfUID.CompareNoCase(_T("FETCH")) == 0)
      {
         String sShowUpper = sShowPart;
         sShowUpper.MakeUpper();
         int vanishedPos = sShowUpper.Find(_T("VANISHED"));
         int changedSincePos = sShowUpper.Find(_T("CHANGEDSINCE"));
         if (vanishedPos >= 0 && changedSincePos >= 0)
         {
            String sRest = sShowUpper.Mid(changedSincePos + (int)_tcslen(_T("CHANGEDSINCE")));
            sRest.TrimLeft();
            fetchVanishedSince = _ttoi64(sRest);
            fetchVanished = true;
         }
      }

      // Set the command to execute as argument
      pArgument->Command(sShowPart);

      // Execute the command. If we have gotten this far, it means that the syntax
      // of the command is correct. If we fail now, we should return NO. 
      IMAPResult result = command_->DoForMails(pConnection, sMailNo, pArgument);

      if (result.GetResult() == IMAPResult::ResultOK)
      {
         // RFC 7162 (QRESYNC): emit "* VANISHED (EARLIER)" for the requested UIDs expunged since n.
         if (fetchVanished)
         {
            std::shared_ptr<IMAPFolder> pCurFolder = pConnection->GetCurrentFolder();
            if (pCurFolder)
            {
               std::vector<__int64> expunged = PersistentIMAPFolder::GetExpungedUIDsSince(pCurFolder->GetID(), fetchVanishedSince);
               if (!expunged.empty())
               {
                  std::vector<std::pair<unsigned int, unsigned int>> vanishedRanges = ParseUidSet_(sMailNo);
                  std::vector<__int64> reported;
                  for (__int64 uid : expunged)
                  {
                     for (const std::pair<unsigned int, unsigned int> &range : vanishedRanges)
                     {
                        if ((unsigned int) uid >= range.first && (unsigned int) uid <= range.second)
                        {
                           reported.push_back(uid);
                           break;
                        }
                     }
                  }

                  if (!reported.empty())
                  {
                     String sVanished;
                     sVanished.Format(_T("* VANISHED (EARLIER) %s\r\n"), IMAPConnection::CompactUidSet(reported).c_str());
                     pConnection->SendAsciiData(sVanished);
                  }
               }
            }
         }

         pConnection->SendAsciiData(pArgument->Tag() + " OK " + command_->GetUIDPlusResponseCode() + command_->GetConditionalStoreResponseCode() + "UID completed\r\n");
      }

      return result;
   }

}