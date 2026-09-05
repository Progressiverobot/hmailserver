// Copyright (c) 2010 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "IMAPCommandRangeAction.h"
#include "IMAPConnection.h"
#include "IMAPFolderView.h"
#include "../Common/BO/Messages.h"
#include "../Common/BO/Message.h"
#include "../Common/BO/IMAPFolder.h"


#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   IMAPCommandRangeAction::IMAPCommandRangeAction() :
      is_uid_(false),
      uidplus_dest_uidvalidity_(0)
   {

   }

   IMAPCommandRangeAction::~IMAPCommandRangeAction()
   {

   }

   void
   IMAPCommandRangeAction::SetIsUID(bool bIsUID)
   {
      is_uid_ = bIsUID;
   }

   bool
   IMAPCommandRangeAction::GetIsUID()
   {
      return is_uid_;
   }

   IMAPResult
   IMAPCommandRangeAction::DoForMails(std::shared_ptr<IMAPConnection> pConnection, const String &sMailNos, std::shared_ptr<IMAPCommandArgument> pArgument)
   {
      std::shared_ptr<IMAPFolderView> view = pConnection->GetCurrentFolderView();
      std::shared_ptr<IMAPFolder> pCurFolder = pConnection->GetCurrentFolder();

      if (!view || !pCurFolder)
         return IMAPResult(IMAPResult::ResultNo, "No folder selected.");

      // RFC 5182 (SEARCHRES): "$" references the result saved by "SEARCH RETURN (SAVE)".
      // Expanded here, the single chokepoint for FETCH/STORE/COPY/MOVE sequence-sets, so
      // every consumer sees a concrete set. The saved result is stored as UIDs; for a
      // sequence-number command they are mapped to the positions THIS SESSION knows them
      // by. An empty saved result expands to "0", which matches nothing.
      String sExpandedMailNos = sMailNos;
      if (sExpandedMailNos.Find(_T("$")) >= 0)
      {
         const std::vector<__int64> &savedUids = pConnection->GetSavedSearchResult();
         std::vector<String> tokens;

         for (__int64 uid : savedUids)
         {
            if (is_uid_)
            {
               String s;
               s.Format(_T("%I64d"), uid);
               tokens.push_back(s);
               continue;
            }

            int sequence = 0;
            IMAPViewEntry entry;

            if (view->GetEntryByUID((unsigned int) uid, sequence, entry))
            {
               String s;
               s.Format(_T("%d"), sequence);
               tokens.push_back(s);
            }
         }

         String sSubstitution = tokens.empty() ? String(_T("0")) : StringParser::JoinVector(tokens, _T(","));
         sExpandedMailNos.Replace(_T("$"), sSubstitution.c_str());
      }

      // Resolved against this session's view, so the numbers mean the messages they meant
      // when the client was told about them, whatever other sessions have done to the
      // folder since (RFC 3501 2.3.1.2; upstream #458).
      std::vector<std::pair<int, IMAPViewEntry> > targets = ResolveTargets_(view, sExpandedMailNos);

      if (targets.empty())
         return IMAPResult();

      std::set<__int64> message_ids;
      for (const std::pair<int, IMAPViewEntry> &target : targets)
         message_ids.insert(target.second.message_id);

      std::shared_ptr<Messages> messages = pCurFolder->GetMessages();

      std::map<__int64, std::shared_ptr<Message> > resolved = UsesLiveMessages()
         ? messages->GetItemsByIds(message_ids)
         : messages->GetCopyByIds(message_ids);

      // A message in this session's view but no longer in the folder was expunged by
      // another session, and this client has not been told - it is told the next time an
      // untagged EXPUNGE may be sent. What the command does about it depends on how it
      // was addressed: the UID variants ignore it (RFC 3501 6.4.8), the others by policy.
      MissingMessagePolicy policy = is_uid_ ? MissingMessagePolicy::Ignore : GetMissingMessagePolicy();

      bool any_missing = false;

      for (const std::pair<int, IMAPViewEntry> &target : targets)
      {
         if (resolved.find(target.second.message_id) != resolved.end())
            continue;

         view->MarkVanished(target.second.message_id);
         any_missing = true;
      }

      if (any_missing && policy == MissingMessagePolicy::FailBeforeActing)
         return IMAPResult(IMAPResult::ResultNo, "[EXPUNGEISSUED] Some of the messages no longer exist.");

      for (const std::pair<int, IMAPViewEntry> &target : targets)
      {
         auto iter = resolved.find(target.second.message_id);

         if (iter == resolved.end())
            continue;

         IMAPResult result = DoAction(pConnection, target.first, (*iter).second, pArgument);

         if (result.GetResult() != IMAPResult::ResultOK)
            return result;
      }

      if (any_missing && policy == MissingMessagePolicy::ReportAfterActing)
         return IMAPResult(IMAPResult::ResultNo, "[EXPUNGEISSUED] Some of the messages no longer exist.");

      return IMAPResult();
   }

   std::vector<std::pair<int, IMAPViewEntry> >
   IMAPCommandRangeAction::ResolveTargets_(std::shared_ptr<IMAPFolderView> view, const String &sMailNos)
   {
      std::vector<std::pair<int, IMAPViewEntry> > targets;

      const unsigned int highestUid = view->GetHighestUID();
      const int messageCount = view->GetMessageCount();

      std::vector<String> sSplitted = StringParser::SplitString(sMailNos, ",");

      for (String sCur : sSplitted)
      {
         long lColonPos = sCur.Find(_T(":"));

         String sFirstPart = lColonPos >= 0 ? sCur.Mid(0, lColonPos) : sCur;
         String sSecondPart = lColonPos >= 0 ? sCur.Mid(lColonPos + 1) : sCur;

         bool firstIsStar = sFirstPart == _T("*");
         bool secondIsStar = sSecondPart == _T("*");

         if (is_uid_)
         {
            // RFC 3501: "*" is the largest UID in use - on either side of the colon, and
            // a range is valid in either order, so "*:1" is the whole mailbox and a bare
            // "*" is the last message. An empty mailbox has no largest UID, so "*" must
            // match nothing there. As the END of a range it is left unbounded rather
            // than pinned to the largest UID, so "5:*" also reaches a message the view
            // took in after the client last asked.
            if ((firstIsStar || secondIsStar) && highestUid == 0)
               continue;

            if (lColonPos >= 0)
            {
               unsigned int startUid = firstIsStar ? highestUid : (unsigned int) _ttoi(sFirstPart);
               unsigned int endUid = secondIsStar ? 0xFFFFFFFFu : (unsigned int) _ttoi(sSecondPart);

               if (endUid < startUid)
                  std::swap(startUid, endUid);

               for (const std::pair<int, IMAPViewEntry> &entry : view->GetEntriesByUIDRange(startUid, endUid))
                  targets.push_back(entry);
            }
            else
            {
               unsigned int uid = firstIsStar ? highestUid : (unsigned int) _ttoi(sCur);

               if (uid == 0)
                  continue;

               int sequence = 0;
               IMAPViewEntry entry;

               if (view->GetEntryByUID(uid, sequence, entry))
                  targets.push_back(std::make_pair(sequence, entry));
            }
         }
         else
         {
            // Sequence numbers: "*" is the highest one in this session's view, on either
            // side of the colon, and a range is valid in either order.
            if (lColonPos >= 0)
            {
               int startIndex = firstIsStar ? messageCount : _ttoi(sFirstPart);
               int endIndex = secondIsStar ? messageCount : _ttoi(sSecondPart);

               if (endIndex < startIndex)
                  std::swap(startIndex, endIndex);

               for (const std::pair<int, IMAPViewEntry> &entry : view->GetEntriesBySequenceRange(startIndex, endIndex))
                  targets.push_back(entry);
            }
            else
            {
               int sequence = firstIsStar ? messageCount : _ttoi(sCur);

               if (sequence <= 0)
                  continue;

               IMAPViewEntry entry;

               if (view->GetEntryBySequence(sequence, entry))
                  targets.push_back(std::make_pair(sequence, entry));
            }
         }
      }

      return targets;
   }

   void
   IMAPCommandRangeAction::RecordCopyUid(unsigned int sourceUid, unsigned int destUid, unsigned int destUidValidity)
   {
      uidplus_source_uids_.push_back(sourceUid);
      uidplus_dest_uids_.push_back(destUid);
      uidplus_dest_uidvalidity_ = destUidValidity;
   }

   String
   IMAPCommandRangeAction::JoinUids_(const std::vector<unsigned int> &uids)
   {
      String result;

      for (size_t i = 0; i < uids.size(); i++)
      {
         if (i > 0)
            result += _T(",");

         String temp;
         temp.Format(_T("%u"), uids[i]);
         result += temp;
      }

      return result;
   }

   String
   IMAPCommandRangeAction::GetUIDPlusResponseCode()
   {
      if (uidplus_source_uids_.empty() || uidplus_dest_uids_.empty())
         return _T("");

      String validity;
      validity.Format(_T("%u"), uidplus_dest_uidvalidity_);

      String result = _T("[COPYUID ") + validity + _T(" ") + JoinUids_(uidplus_source_uids_) +
                      _T(" ") + JoinUids_(uidplus_dest_uids_) + _T("] ");

      return result;
   }

}
