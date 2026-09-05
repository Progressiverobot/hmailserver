// Copyright (c) 2010 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "IMAPCommandUID.h"
#include "IMAPCommandAppend.h"
#include "IMAPConnection.h"
#include "IMAPSimpleCommandParser.h"


#include "IMAPFetch.h"
#include "IMAPCopy.h"
#include "IMAPMove.h"
#include "IMAPStore.h"
#include "IMAPFolderView.h"
#include "IMAPNotificationClient.h"
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
      // ranges. starValue is what "*" stands for: normally the largest UID in the
      // mailbox, but see the VANISHED call site for why it is sometimes unbounded.
      std::vector<std::pair<unsigned int, unsigned int>> ParseUidSet_(const String &sUidSet, unsigned int starValue)
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

               // RFC 3501: "*" is valid on either side of the colon, and a range is
               // valid in either order.
               unsigned int start = (first == _T("*")) ? starValue : (unsigned int) _ttoi(first);
               unsigned int end = (second == _T("*")) ? starValue : (unsigned int) _ttoi(second);

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
               ranges.push_back(std::pair<unsigned int, unsigned int>(starValue, starValue));
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

         // "$" allowed (RFC 5182 SEARCHRES); DoForMails expands it to the saved result.
         if (!StringParser::ValidateString(sUidMailNo, "01234567890,.:*$"))
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
         std::shared_ptr<IMAPCommandSEARCH> pCommand = std::shared_ptr<IMAPCommandSEARCH> (new IMAPCommandSEARCH(IMAPSearchModeSearch));
         pCommand->SetIsUID();
         IMAPResult result = pCommand->ExecuteCommand(pConnection, pArgument);

         if (result.GetResult() == IMAPResult::ResultOK)
            pConnection->SendAsciiData(sTag + " OK UID completed\r\n");

         return result;
      }
      else if (sTypeOfUID.CompareNoCase(_T("SORT")) == 0)
      {
         std::shared_ptr<IMAPCommandSEARCH> pCommand = std::shared_ptr<IMAPCommandSEARCH> (new IMAPCommandSEARCH(IMAPSearchModeSort));
         pCommand->SetIsUID();
         IMAPResult result = pCommand->ExecuteCommand(pConnection, pArgument);

         if (result.GetResult() == IMAPResult::ResultOK)
            pConnection->SendAsciiData(sTag + " OK UID completed\r\n");

         return result;
      }
      else if (sTypeOfUID.CompareNoCase(_T("THREAD")) == 0)
      {
         // RFC 5256: UID THREAD is THREAD with UIDs in the tree instead of
         // sequence numbers. Same shape as UID SORT above.
         std::shared_ptr<IMAPCommandSEARCH> pCommand = std::shared_ptr<IMAPCommandSEARCH> (new IMAPCommandSEARCH(IMAPSearchModeThread));
         pCommand->SetIsUID();
         IMAPResult result = pCommand->ExecuteCommand(pConnection, pArgument);

         if (result.GetResult() == IMAPResult::ResultOK)
            pConnection->SendAsciiData(sTag + " OK UID completed\r\n");

         return result;
      }
      else if (sTypeOfUID.CompareNoCase(_T("REPLACE")) == 0)
      {
         // RFC 8508: UID REPLACE <uid> <mailbox> <append-data>. The APPEND
         // handler owns the whole flow - literal state machine, atomic save,
         // the target's removal - and only needs to know the target parameter
         // is a UID rather than a message sequence number.
         std::shared_ptr<IMAPCommandAppend> pAppendHandler = pConnection->GetAppendCommandHandler();
         pAppendHandler->SetReplaceUidMode(true);

         // Strip the leading "UID " so the handler sees the same command shape
         // the sequence form has.
         String sFullCommand = pArgument->Command();
         int iReplacePos = sFullCommand.FindNoCase(_T("REPLACE"));

         std::shared_ptr<IMAPCommandArgument> pReplaceArgument = std::shared_ptr<IMAPCommandArgument>(new IMAPCommandArgument);
         pReplaceArgument->Command(sFullCommand.Mid(iReplacePos));
         pReplaceArgument->Tag(pArgument->Tag());
         pReplaceArgument->Literals(pArgument->Literals());

         return pAppendHandler->ExecuteCommand(pConnection, pReplaceArgument);
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

         // RFC 5182 (SEARCHRES): expand "$" to the saved result before parsing the set.
         if (sUidSet.Find(_T("$")) >= 0)
         {
            const std::vector<__int64> &savedUids = pConnection->GetSavedSearchResult();
            std::vector<String> tokens;
            for (__int64 uid : savedUids)
            {
               String s;
               s.Format(_T("%I64d"), uid);
               tokens.push_back(s);
            }
            String sSubstitution = tokens.empty() ? String(_T("0")) : StringParser::JoinVector(tokens, _T(","));
            sUidSet.Replace(_T("$"), sSubstitution.c_str());
         }

         if (sUidSet.IsEmpty() || !StringParser::ValidateString(sUidSet, "01234567890,.:*"))
            return IMAPResult(IMAPResult::ResultBad, "Incorrect mail number");

         std::shared_ptr<IMAPFolderView> view = pConnection->GetCurrentFolderView();
         if (!view)
            return IMAPResult(IMAPResult::ResultNo, "No folder selected.");

         auto messages = MessagesContainer::Instance()->GetMessages(pCurFolder->GetAccountID(), pCurFolder->GetID());
         view->AppendNewMessages(messages);

         // "*" is the largest UID this session knows about - see IMAPCommandRangeAction.
         std::vector<std::pair<unsigned int, unsigned int>> ranges = ParseUidSet_(sUidSet, view->GetHighestUID());

         std::set<__int64> candidate_ids;

         for (const std::pair<int, IMAPViewEntry> &entry : view->GetAllEntries())
         {
            unsigned int uid = entry.second.uid;

            for (const std::pair<unsigned int, unsigned int> &range : ranges)
            {
               if (uid >= range.first && uid <= range.second)
               {
                  candidate_ids.insert(entry.second.message_id);
                  break;
               }
            }
         }

         std::map<__int64, std::shared_ptr<Message> > candidates = messages->GetCopyByIds(candidate_ids);

         std::set<__int64> messages_to_delete;

         for (__int64 message_id : candidate_ids)
         {
            auto iter = candidates.find(message_id);

            if (iter == candidates.end())
            {
               // Expunged by another session. Reported below, together with this one's.
               view->MarkVanished(message_id);
               continue;
            }

            if ((*iter).second->GetFlagDeleted())
               messages_to_delete.insert(message_id);
         }

         std::vector<__int64> deleted_message_ids = messages->DeleteMessagesById(messages_to_delete);

         std::vector<__int64> ids_to_report = deleted_message_ids;
         std::vector<__int64> vanished_elsewhere = view->TakeVanished();
         ids_to_report.insert(ids_to_report.end(), vanished_elsewhere.begin(), vanished_elsewhere.end());

         std::vector<unsigned int> expunged_uids;
         std::vector<int> expunged_sequences = view->RemoveMessages(ids_to_report, &expunged_uids);

         pConnection->RemoveRecentMessages(ids_to_report);

         String sResponse = IMAPNotificationClient::FormatExpungeResponses(pConnection, expunged_sequences, expunged_uids);

         if (!sResponse.IsEmpty())
            pConnection->SendAsciiData(sResponse);

         if (!deleted_message_ids.empty())
         {
            std::shared_ptr<ChangeNotification> pNotification =
               std::shared_ptr<ChangeNotification>(new ChangeNotification(pCurFolder->GetAccountID(), pCurFolder->GetID(), ChangeNotification::NotificationMessageDeleted, deleted_message_ids));

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

      // "$" allowed (RFC 5182 SEARCHRES); DoForMails expands it to the saved result.
      if (!StringParser::ValidateString(sMailNo, "01234567890,.:*$"))
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

      // RFC 7162 (QRESYNC): the "* VANISHED (EARLIER)" response is emitted BEFORE
      // the FETCH responses, and the order is a MUST, not a style choice - 3.2.6:
      // "Any VANISHED (EARLIER) responses MUST be returned before any FETCH
      // responses, otherwise the client might get confused about how message
      // numbers map to UIDs." The client shrinks its model by the vanished set
      // first, so the sequence numbers implied by the FETCHes that follow land on
      // the mailbox as it now is. This block therefore runs before DoForMails,
      // which is what streams the FETCH responses; if the FETCH then fails, the
      // untagged data already sent was still true - those UIDs really are gone.
      if (fetchVanished)
      {
         std::shared_ptr<IMAPFolder> pCurFolder = pConnection->GetCurrentFolder();
         if (pCurFolder)
         {
            // "*" stays unbounded here: an expunged UID can be higher than
            // any UID still in the mailbox, so resolving it to the largest
            // surviving UID would hide the very messages this reports.
            std::vector<std::pair<unsigned int, unsigned int>> vanishedRanges =
               ParseUidSet_(sMailNo, (unsigned int) 0xFFFFFFFF);

            String sVanishedSet;

            if (PersistentIMAPFolder::RemembersExpungesSince(pCurFolder->GetID(), fetchVanishedSince))
            {
               std::vector<__int64> expunged = PersistentIMAPFolder::GetExpungedUIDsSince(pCurFolder->GetID(), fetchVanishedSince);

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

               sVanishedSet = IMAPConnection::CompactUidSet(reported);
            }
            else
            {
               /*
                  The expunge records covering this mod-sequence have been pruned
                  (see IMAPExpungeRetentionTask), and RFC 7162 section 3.2.6 is
                  explicit about what has to happen then:

                     "Note: A server that receives a mod-sequence smaller than
                     <minmodseq>, where <minmodseq> is the value of the smallest
                     expunged mod-sequence it remembers minus one, MUST behave as
                     if it was requested to report all expunged messages from the
                     provided UID set parameter."

                  Here the client DID provide a UID set, so unlike SELECT this
                  stays inside the ranges it asked about. "*" is resolved to the
                  mailbox's highest issued UID rather than left unbounded, because
                  a UID above that never existed and naming it tells the client
                  nothing.
               */
               sVanishedSet = IMAPConnection::CompactMissingUidSet(pCurFolder->GetMessages(), vanishedRanges,
                                                                  pCurFolder->GetCurrentUID());
            }

            if (!sVanishedSet.IsEmpty())
            {
               String sVanished;
               sVanished.Format(_T("* VANISHED (EARLIER) %s\r\n"), sVanishedSet.c_str());
               pConnection->SendAsciiData(sVanished);
            }
         }
      }

      // Execute the command. If we have gotten this far, it means that the syntax
      // of the command is correct. If we fail now, we should return NO.
      IMAPResult result = command_->DoForMails(pConnection, sMailNo, pArgument);

      if (result.GetResult() == IMAPResult::ResultOK)
      {
         pConnection->SendAsciiData(pArgument->Tag() + " OK " + command_->GetUIDPlusResponseCode() + command_->GetConditionalStoreResponseCode() + "UID completed\r\n");
      }

      return result;
   }

}