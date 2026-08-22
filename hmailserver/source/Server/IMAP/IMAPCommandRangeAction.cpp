// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "IMAPCommandRangeAction.h"
#include "IMAPConnection.h"
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

   unsigned int
   IMAPCommandRangeAction::GetMaxUid_(const std::vector<std::shared_ptr<Message>> &messages)
   {
      // Scanned rather than read off the last element: the collection is not
      // guaranteed to be ordered by UID, and a COPY can introduce a message
      // whose UID is lower than its predecessor's.
      unsigned int maxUid = 0;

      for (const std::shared_ptr<Message> &message : messages)
      {
         unsigned int uid = message->GetUID();

         if (uid > maxUid)
            maxUid = uid;
      }

      return maxUid;
   }

   IMAPResult
   IMAPCommandRangeAction::DoForMails(std::shared_ptr<IMAPConnection> pConnection, const String &sMailNos, std::shared_ptr<IMAPCommandArgument> pArgument)
   {
      long lColonPos = -1;

      // RFC 5182 (SEARCHRES): "$" references the result saved by "SEARCH RETURN (SAVE)".
      // Expand it here, the single chokepoint for FETCH/STORE/COPY/MOVE sequence-sets, so
      // every consumer sees a concrete set. The saved result is stored as UIDs; for a
      // sequence-number command they are mapped to the current message positions. An empty
      // saved result expands to "0", which matches nothing (the command succeeds, no-op).
      String sExpandedMailNos = sMailNos;
      if (sExpandedMailNos.Find(_T("$")) >= 0)
      {
         const std::vector<__int64> &savedUids = pConnection->GetSavedSearchResult();
         std::vector<String> tokens;

         if (is_uid_)
         {
            for (__int64 uid : savedUids)
            {
               String s;
               s.Format(_T("%I64d"), uid);
               tokens.push_back(s);
            }
         }
         else if (pConnection->GetCurrentFolder())
         {
            std::shared_ptr<Messages> messages = pConnection->GetCurrentFolder()->GetMessages();
            for (__int64 uid : savedUids)
            {
               unsigned int foundIndex = 0;
               std::shared_ptr<Message> message = messages->GetItemByUID((unsigned int) uid, foundIndex);
               if (message)
               {
                  String s;
                  s.Format(_T("%u"), foundIndex);
                  tokens.push_back(s);
               }
            }
         }

         String sSubstitution = tokens.empty() ? String(_T("0")) : StringParser::JoinVector(tokens, _T(","));
         sExpandedMailNos.Replace(_T("$"), sSubstitution.c_str());
      }

      std::vector<String> sSplitted = StringParser::SplitString(sExpandedMailNos, ",");

      if (is_uid_)
      {
         for(String sCur : sSplitted)
         {
            lColonPos = sCur.Find(_T(":"));

            if (lColonPos >= 0)
            {
               String sFirstPart = sCur.Mid(0, lColonPos);
               String sSecondPart = sCur.Mid(lColonPos + 1);

               std::vector<std::shared_ptr<Message>> messages = pConnection->GetCurrentFolder()->GetMessages()->GetCopy();

               // RFC 3501: "*" is the largest UID in use - on EITHER side of the
               // colon - and a range is valid in either order, so "*:1" means the
               // whole mailbox. Resolving "*" only as the range end left it as 0
               // everywhere else, which made "UID FETCH *" a silent no-op and
               // "UID STORE *:* +FLAGS (\Deleted)" flag the entire mailbox.
               const unsigned int maxUid = GetMaxUid_(messages);

               unsigned int lStartDBID = sFirstPart == _T("*") ? maxUid : (unsigned int) _ttoi(sFirstPart);
               unsigned int lEndDBID = sSecondPart == _T("*") ? maxUid : (unsigned int) _ttoi(sSecondPart);

               if (lEndDBID < lStartDBID)
                  std::swap(lStartDBID, lEndDBID);

               // An empty mailbox has no largest UID, so "*" must match nothing.
               if (maxUid == 0 && (sFirstPart == _T("*") || sSecondPart == _T("*")))
                  continue;

               int index = 0;
               for(std::shared_ptr<Message> pMessage: messages)
               {
                  index++;
                  unsigned int uid = pMessage->GetUID();

                  if (uid >= lStartDBID)
                  {
                     if (uid <= lEndDBID)
                     {
                        // UID doesn't fail just because the message is missing.
                        // This is why we don't check the return value.
                        IMAPResult result = DoAction(pConnection, index, pMessage, pArgument);
                        if (result.GetResult() != IMAPResult::ResultOK)
                        {
                           return result;
                        }
                     }
                  }
               }

            }
            else 
            {
               std::shared_ptr<Messages> messages = pConnection->GetCurrentFolder()->GetMessages();

               // A bare "*" addresses the message with the largest UID.
               unsigned int uid = sCur == _T("*")
                  ? GetMaxUid_(messages->GetCopy())
                  : (unsigned int) _ttoi(sCur);

               if (uid == 0)
                  continue;

               unsigned int foundIndex = 0;
               std::shared_ptr<Message> message = messages->GetItemByUID(uid, foundIndex);
               if (!message)
                  continue;
               
               IMAPResult result = DoAction(pConnection, foundIndex, message, pArgument);
               if (result.GetResult() != IMAPResult::ResultOK)
               {
                  return result;
               }
            }
         }            

      }
      else
      {
         for(String sCur: sSplitted)
         {
            lColonPos = sCur.Find(_T(":"));

            if (lColonPos >= 0)
            {
               String sFirstPart = sCur.Mid(0, lColonPos);
               String sSecondPart = sCur.Mid(lColonPos + 1);

               auto vecMessages = pConnection->GetCurrentFolder()->GetMessages()->GetCopy();

               // See the UID branch above: "*" is the highest message sequence
               // number on either side of the colon, and ranges are valid in
               // either order.
               const int maxSequenceNumber = (int) vecMessages.size();

               int lStartIndex = sFirstPart == _T("*") ? maxSequenceNumber : _ttoi(sFirstPart);
               int lEndIndex = sSecondPart == _T("*") ? maxSequenceNumber : _ttoi(sSecondPart);

               if (lEndIndex < lStartIndex)
                  std::swap(lStartIndex, lEndIndex);

               int index = 0;
               for(std::shared_ptr<Message> message : vecMessages)
               {
                  index++;

                  if (index >= lStartIndex)
                  {
                     if (index <= lEndIndex)
                     {
                        IMAPResult result = DoAction(pConnection, index, message, pArgument);
                        if (result.GetResult() != IMAPResult::ResultOK)
                        {
                           return result;
                        }
                     }
                  }
               }

            }
            else 
            {
               std::shared_ptr<Messages> messages = pConnection->GetCurrentFolder()->GetMessages();

               // A bare "*" addresses the last message in the mailbox.
               int messageIndex = sCur == _T("*") ? messages->GetCount() : _ttoi(sCur);

               if (messageIndex <= 0)
                  continue;

               std::shared_ptr<Message> pMessage = messages->GetItem(messageIndex-1);

               if (!pMessage)
                  continue;

               IMAPResult result = DoAction(pConnection, messageIndex, pMessage, pArgument);
               if (result.GetResult() != IMAPResult::ResultOK)
               {
                  return result;
               }
            }
         }   

      }

      return IMAPResult();

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
