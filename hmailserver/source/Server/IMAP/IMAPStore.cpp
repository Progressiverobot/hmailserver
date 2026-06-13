// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "IMAPStore.h"
#include "IMAPConnection.h"

#include "../Common/Application/FolderManager.h"
#include "../Common/BO/Message.h"
#include "../Common/BO/IMAPFolder.h"
#include "../Common/BO/ACLPermission.h"

#include "../Common/Tracking/ChangeNotification.h"
#include "../Common/Tracking/NotificationServer.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{

   IMAPStore::IMAPStore()
   {

   }

   IMAPStore::~IMAPStore()
   {
      
   }


   IMAPResult
   IMAPStore::DoAction(std::shared_ptr<IMAPConnection> pConnection, int messageIndex,  std::shared_ptr<Message> pMessage, const std::shared_ptr<IMAPCommandArgument> pArgument)
   {
      if (!pMessage || !pArgument)
         return IMAPResult(IMAPResult::ResultBad, "Invalid parameters");

      if (pConnection->GetCurrentFolderReadOnly())
      {
         return IMAPResult(IMAPResult::ResultNo, "Store command on read-only folder.");
      }

      // RFC 7162 (CONDSTORE): parse the optional "(UNCHANGEDSINCE <modseq>)" modifier once.
      if (!modifier_parsed_)
         ParseModifier_(pArgument->Command());

      // MODSEQ is reported in STORE responses when CONDSTORE is enabled or a conditional
      // STORE is in progress.
      bool includeModSeq = pConnection->GetCondstoreEnabled() || has_unchangedsince_;

      // Conditional STORE: a message changed since the client's mod-sequence is left
      // untouched and added to the MODIFIED set returned in the tagged response.
      if (has_unchangedsince_ && pMessage->GetModSeq() > unchangedsince_)
      {
         modified_.push_back(GetIsUID() ? pMessage->GetUID() : (unsigned int) messageIndex);
         return IMAPResult();
      }

      bool bSilent = false;

      String sCommand = pArgument->Command();
      if (sCommand.FindNoCase(_T("FLAGS.SILENT")) >= 0)
         bSilent = true;
      else
         bSilent = false;

      // Read flags from command.
      bool bSeen = (sCommand.FindNoCase(_T("\\Seen")) >= 0);
      bool bDeleted = (sCommand.FindNoCase(_T("\\Deleted")) >= 0);
      bool bDraft = (sCommand.FindNoCase(_T("\\Draft")) >= 0);
      bool bAnswered = (sCommand.FindNoCase(_T("\\Answered")) >= 0);
      bool bFlagged = (sCommand.FindNoCase(_T("\\Flagged")) >= 0);
   
      if (bSeen)
      {
         // ACL: If user tries to change the Seen flag, check that he has permission to do so.
         if (!pConnection->CheckPermission(pConnection->GetCurrentFolder(), ACLPermission::PermissionWriteSeen))
            return IMAPResult(IMAPResult::ResultNo, "ACL: WriteSeen permission denied (Required for STORE command).");
      }

      if (bDeleted)
      {
         if (!pConnection->CheckPermission(pConnection->GetCurrentFolder(), ACLPermission::PermissionWriteDeleted))
            return IMAPResult(IMAPResult::ResultNo, "ACL: DeleteMessages permission denied (Required for STORE command).");
      }

      if (bDraft || bAnswered || bFlagged)
      {
         if (!pConnection->CheckPermission(pConnection->GetCurrentFolder(), ACLPermission::PermissionWriteOthers))
            return IMAPResult(IMAPResult::ResultNo, "ACL: WriteOthers permission denied (Required for STORE command).");
      }


      if (sCommand.FindNoCase(_T("-FLAGS")) >= 0)
      {
         // Remove flags
         if (bSeen)
            pMessage->SetFlagSeen(false);
         if (bDeleted)
            pMessage->SetFlagDeleted(false);
         if (bDraft)
            pMessage->SetFlagDraft(false);
         if (bAnswered)
            pMessage->SetFlagAnswered(false);
         if (bFlagged)
            pMessage->SetFlagFlagged(false);



      }
      else if (sCommand.FindNoCase(_T("+FLAGS")) >= 0)
      {
         // Add flags
         if (bSeen)
            pMessage->SetFlagSeen(true);
         if (bDeleted)
            pMessage->SetFlagDeleted(true);
         if (bDraft)
            pMessage->SetFlagDraft(true);
         if (bAnswered)
            pMessage->SetFlagAnswered(true);
         if (bFlagged)
            pMessage->SetFlagFlagged(true);
       
      }
      else if (sCommand.FindNoCase(_T("FLAGS")) >= 0)
      {
         // Set flags
         pMessage->SetFlagSeen(bSeen);
         pMessage->SetFlagDeleted(bDeleted);
         pMessage->SetFlagDraft(bDraft);
         pMessage->SetFlagAnswered(bAnswered);
         pMessage->SetFlagFlagged(bFlagged);
      }

      bool result = Application::Instance()->GetFolderManager()->UpdateMessageFlags(
         (int) pConnection->GetCurrentFolder()->GetAccountID(), 
         (int) pConnection->GetCurrentFolder()->GetID(),
         pMessage->GetID(), pMessage->GetFlags());

      if (!result)
      {
         return IMAPResult(IMAPResult::ResultNo, "Unable to store message flags.");
      }

      if (!bSilent)
      {
         pConnection->SendAsciiData(GetMessageFlags(pMessage, messageIndex, includeModSeq));
      }
      else if (includeModSeq)
      {
         // RFC 7162: even a silent conditional/CONDSTORE STORE must report the new MODSEQ.
         String sModSeqOnly;
         sModSeqOnly.Format(_T("* %d FETCH (UID %u MODSEQ (%I64d))\r\n"), messageIndex, pMessage->GetUID(), pMessage->GetModSeq());
         pConnection->SendAsciiData(sModSeqOnly);
      }

      // BEGIN IMAP IDLE

      // Notify the mailbox notifier that the mailbox contents have changed.
      std::vector<__int64> effectedMessages;
      effectedMessages.push_back(pMessage->GetID());

      std::shared_ptr<ChangeNotification> pNotification = 
         std::shared_ptr<ChangeNotification>(new ChangeNotification(pConnection->GetCurrentFolder()->GetAccountID(), pConnection->GetCurrentFolder()->GetID(),  ChangeNotification::NotificationMessageFlagsChanged, effectedMessages));

      Application::Instance()->GetNotificationServer()->SendNotification(pConnection->GetNotificationClient(), pNotification);
      // END IMAP IDLE

      return IMAPResult();
   }

   String 
   IMAPStore::GetMessageFlags(std::shared_ptr<Message> pMessage, int messageIndex, bool includeModSeq)
   {
      // Build a flags string.
      String sFlags;

      if (pMessage->GetFlagDeleted())
      {
         if (!sFlags.IsEmpty()) 
            sFlags += " ";

         sFlags += "\\Deleted";
      }

      if (pMessage->GetFlagAnswered())
      {
         if (!sFlags.IsEmpty()) 
            sFlags += " ";

         sFlags += "\\Answered";
      }

      if (pMessage->GetFlagFlagged())
      {
         if (!sFlags.IsEmpty()) 
            sFlags += " ";

         sFlags += "\\Flagged";
      }

      if (pMessage->GetFlagDraft())
      {
         if (!sFlags.IsEmpty()) 
            sFlags += " ";

         sFlags += "\\Draft";
      }

      if (pMessage->GetFlagSeen())
      {
         if (!sFlags.IsEmpty()) 
            sFlags += " ";

         sFlags += "\\Seen";
      }


      // It really should be FETCH below...
      String sRet;
      if (includeModSeq)
         sRet.Format(_T("* %d FETCH (FLAGS (%s) UID %u MODSEQ (%I64d))\r\n"), messageIndex, sFlags.c_str(), pMessage->GetUID(), pMessage->GetModSeq());
      else
         sRet.Format(_T("* %d FETCH (FLAGS (%s) UID %u)\r\n"), messageIndex, sFlags.c_str(), pMessage->GetUID());
      return sRet;
   }

   void
   IMAPStore::ParseModifier_(const String &sCommand)
   {
      modifier_parsed_ = true;

      long lPos = sCommand.FindNoCase(_T("UNCHANGEDSINCE"));
      if (lPos < 0)
         return;

      String sRest = sCommand.Mid(lPos + 14); // length of "UNCHANGEDSINCE"
      sRest.TrimLeft();
      unchangedsince_ = _ttoi64(sRest);
      has_unchangedsince_ = true;
   }

   String
   IMAPStore::GetConditionalStoreResponseCode()
   {
      if (modified_.empty())
         return _T("");

      String sSet;
      for (size_t i = 0; i < modified_.size(); i++)
      {
         if (i > 0)
            sSet += _T(",");

         String sTemp;
         sTemp.Format(_T("%u"), modified_[i]);
         sSet += sTemp;
      }

      return _T("[MODIFIED ") + sSet + _T("] ");
   }
      

}
