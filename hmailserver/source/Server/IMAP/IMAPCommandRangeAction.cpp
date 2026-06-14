// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

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

               unsigned int lStartDBID = _ttoi(sFirstPart);
               unsigned int lEndDBID = -1;
               if (sSecondPart != _T("*"))
                  lEndDBID = _ttoi(sSecondPart);

               std::vector<std::shared_ptr<Message>> messages = pConnection->GetCurrentFolder()->GetMessages()->GetCopy();

               int index = 0;
               for(std::shared_ptr<Message> pMessage: messages)
               {
                  index++;
                  unsigned int uid = pMessage->GetUID();

                  if (uid >= lStartDBID)
                  {
                     if (lEndDBID == -1 || uid <= lEndDBID)
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
               unsigned int uid = _ttoi(sCur);

               unsigned int foundIndex = 0;
               std::shared_ptr<Messages> messages = pConnection->GetCurrentFolder()->GetMessages();
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

               int lStartIndex = _ttoi(sFirstPart);
               int lEndIndex = -1;
               if (sSecondPart != _T("*"))
                  lEndIndex = _ttoi(sSecondPart);

               auto vecMessages = pConnection->GetCurrentFolder()->GetMessages()->GetCopy();
               
               int index = 0;
               for(std::shared_ptr<Message> message : vecMessages)
               {
                  index++;

                  if (index >= lStartIndex)
                  {
                     if (lEndIndex == -1 || index <= lEndIndex)
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
               int messageIndex = _ttoi(sCur);
               std::shared_ptr<Message> pMessage = pConnection->GetCurrentFolder()->GetMessages()->GetItem(messageIndex-1);

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
