// Copyright (c) 2008 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"

#include "SMTPForwarding.h"
#include "RuleApplier.h"

#include "../Common/BO/Account.h"
#include "../Common/BO/MessageData.h"
#include "../Common/BO/Message.h"
#include "../Common/BO/MessageRecipients.h"

#include "../Common/Persistence/PersistentMessage.h"

#include "../Common/Util/SRS.h"
#include "../Common/Application/IniFileSettings.h"
#include "../Common/Cache/CacheContainer.h"
#include "../Common/BO/Domain.h"


#include "RecipientParser.h"
#include "DeliveryQueue.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   bool 
   SMTPForwarding::PerformForwarding(std::shared_ptr<const Account> pRecipientAccount, std::shared_ptr<Message> pOriginalMessage)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Apply forwarding for the message.
   //---------------------------------------------------------------------------()
   {
      if (!pRecipientAccount->GetForwardEnabled())
         return true;
      
      if (pRecipientAccount->GetForwardAddress().IsEmpty())
      {
         // Configuration error. Forward was enabled, but no address specified.
         return true;
      }

      // Don't forward when the message is classified as spam
      if (pRecipientAccount->GetForwardAbortSpamFlagged() && pOriginalMessage->GetFlagSpam())
      {
         LOG_DEBUG("SMTPForwarding::PerformForwarding aborted, message marked as spam");
         return true;
      }

      if (!pRecipientAccount->GetForwardAddress().CompareNoCase(pRecipientAccount->GetAddress()))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 4334, "SMTPDeliverer::_ApplyForwarding", "Could not forward message since target address as same as account address.");
         return true;
      }

      String sErrorMessage;
      bool bTreatSecurityAsLocal = false;
      RecipientParser recipientParser;
      if (recipientParser.CheckDeliveryPossibility(false, 
                                                    pRecipientAccount->GetAddress(), 
                                                    pRecipientAccount->GetForwardAddress(), 
                                                    sErrorMessage, 
                                                    bTreatSecurityAsLocal, 
                                                    0) != RecipientParser::DP_Possible)
      {
         String sLogMessage = Formatter::Format("Could not forward message from {0} to {1}. Reason: {2}",
                        pRecipientAccount->GetAddress(), pRecipientAccount->GetForwardAddress(), sErrorMessage);

         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 4382, "SMTPDeliverer::_ApplyForwarding", sLogMessage);

         return true;
      }


      String originalFileName = PersistentMessage::GetFileName(pRecipientAccount, pOriginalMessage);

      // Check that rule loop count is not yet reached.
      std::shared_ptr<MessageData> pOldMsgData  = std::shared_ptr<MessageData>(new MessageData());
      pOldMsgData->LoadFromMessage(originalFileName, pOriginalMessage);

      // false = check only loop counter not AutoSubmitted header because forward not rule
      if (!RuleApplier::IsGeneratedResponseAllowed(pOldMsgData, false))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 4333, "SMTPDeliverer::_ApplyForwarding", "Could not forward message. Maximum forward loop count reached.");

         return true;
      }

      LOG_DEBUG(_T("Forwarding message"));

      // Create a copy of the message
      std::shared_ptr<Message> pNewMessage = PersistentMessage::CopyToQueue(pRecipientAccount, pOriginalMessage);

      String envelopeFrom = pNewMessage->GetFromAddress();
      if (!envelopeFrom.IsEmpty())
      {
         if (IniFileSettings::Instance()->GetSRSEnabled())
         {
            // SRS (Sender Rewriting Scheme): when forwarding mail from an external
            // sender, rewrite the envelope MAIL FROM to a signed, reversible
            // address at the local forwarding domain so SPF stays aligned. Local
            // senders are left untouched (our own SPF already covers them).
            String fromDomain = StringParser::ExtractDomain(envelopeFrom);
            bool fromIsLocal = CacheContainer::Instance()->GetDomain(fromDomain) ? true : false;
            if (!fromIsLocal)
            {
               String forwarderDomain = StringParser::ExtractDomain(pRecipientAccount->GetAddress());
               String srsAddress = SRS::Forward(envelopeFrom, forwarderDomain, IniFileSettings::Instance()->GetSRSSecret());
               if (!srsAddress.IsEmpty())
                  pNewMessage->SetFromAddress(srsAddress);
            }
         }
         else if (IniFileSettings::Instance()->GetRewriteEnvelopeFromWhenForwarding())
         {
            pNewMessage->SetFromAddress(pRecipientAccount->GetAddress());
         }
      }

      pNewMessage->SetState(Message::Delivering);

      // Increase the number of rule-deliveries made.
      std::shared_ptr<MessageData> pNewMsgData = std::shared_ptr<MessageData>(new MessageData());
      const String newFileName = PersistentMessage::GetFileName(pNewMessage);
      pNewMsgData->LoadFromMessage(newFileName, pNewMessage);
      pNewMsgData->IncreaseRuleLoopCount();
      pNewMsgData->Write(newFileName);

      // Add new recipients
      bool recipientOK = false;
      recipientParser.CreateMessageRecipientList(pRecipientAccount->GetForwardAddress(), pNewMessage->GetRecipients(), recipientOK);

      // Check that there are recipients of the letter. If not, we should skip delivery.
      if (pNewMessage->GetRecipients()->GetCount() == 0)
      {
         // Delete the file since the message cannot be delivered.
         FileUtilities::DeleteFile(newFileName);
         
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 4332, "SMTPDeliverer::_ApplyForwarding", "Could not forward message; no recipients.");

         return true;
      }

      PersistentMessage::SaveObject(pNewMessage);

      bool bKeepOriginal = pRecipientAccount->GetForwardKeepOriginal();

      return bKeepOriginal;
   }

   bool
   SMTPForwarding::RedirectToAddress(std::shared_ptr<const Account> pRecipientAccount, std::shared_ptr<Message> pOriginalMessage, const String &targetAddress)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Queues a copy of the message for delivery to an arbitrary address, as used by
   // the Sieve "redirect" action. Mirrors the copy/loop-guard/SRS handling of
   // PerformForwarding but takes an explicit target and ignores the account's own
   // forward settings.
   //---------------------------------------------------------------------------()
   {
      if (targetAddress.IsEmpty())
         return false;

      // Avoid an obvious self-redirect loop.
      if (targetAddress.CompareNoCase(pRecipientAccount->GetAddress()) == 0)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 4334, "SMTPForwarding::RedirectToAddress", "Could not redirect message since target address is the same as the account address.");
         return false;
      }

      String sErrorMessage;
      bool bTreatSecurityAsLocal = false;
      RecipientParser recipientParser;
      if (recipientParser.CheckDeliveryPossibility(false,
                                                   pRecipientAccount->GetAddress(),
                                                   targetAddress,
                                                   sErrorMessage,
                                                   bTreatSecurityAsLocal,
                                                   0) != RecipientParser::DP_Possible)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 4382, "SMTPForwarding::RedirectToAddress",
            Formatter::Format("Could not redirect message from {0} to {1}. Reason: {2}", pRecipientAccount->GetAddress(), targetAddress, sErrorMessage));
         return false;
      }

      const String originalFileName = PersistentMessage::GetFileName(pRecipientAccount, pOriginalMessage);

      std::shared_ptr<MessageData> pOldMsgData = std::shared_ptr<MessageData>(new MessageData());
      pOldMsgData->LoadFromMessage(originalFileName, pOriginalMessage);

      // Guard against redirect loops via the rule loop counter.
      if (!RuleApplier::IsGeneratedResponseAllowed(pOldMsgData, false))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 4333, "SMTPForwarding::RedirectToAddress", "Could not redirect message. Maximum loop count reached.");
         return false;
      }

      std::shared_ptr<Message> pNewMessage = PersistentMessage::CopyToQueue(pRecipientAccount, pOriginalMessage);

      String envelopeFrom = pNewMessage->GetFromAddress();
      if (!envelopeFrom.IsEmpty())
      {
         if (IniFileSettings::Instance()->GetSRSEnabled())
         {
            String fromDomain = StringParser::ExtractDomain(envelopeFrom);
            bool fromIsLocal = CacheContainer::Instance()->GetDomain(fromDomain) ? true : false;
            if (!fromIsLocal)
            {
               String forwarderDomain = StringParser::ExtractDomain(pRecipientAccount->GetAddress());
               String srsAddress = SRS::Forward(envelopeFrom, forwarderDomain, IniFileSettings::Instance()->GetSRSSecret());
               if (!srsAddress.IsEmpty())
                  pNewMessage->SetFromAddress(srsAddress);
            }
         }
         else if (IniFileSettings::Instance()->GetRewriteEnvelopeFromWhenForwarding())
         {
            pNewMessage->SetFromAddress(pRecipientAccount->GetAddress());
         }
      }

      pNewMessage->SetState(Message::Delivering);

      std::shared_ptr<MessageData> pNewMsgData = std::shared_ptr<MessageData>(new MessageData());
      const String newFileName = PersistentMessage::GetFileName(pNewMessage);
      pNewMsgData->LoadFromMessage(newFileName, pNewMessage);
      pNewMsgData->IncreaseRuleLoopCount();
      pNewMsgData->Write(newFileName);

      bool recipientOK = false;
      recipientParser.CreateMessageRecipientList(targetAddress, pNewMessage->GetRecipients(), recipientOK);

      if (pNewMessage->GetRecipients()->GetCount() == 0)
      {
         FileUtilities::DeleteFile(newFileName);
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 4332, "SMTPForwarding::RedirectToAddress", "Could not redirect message; no recipients.");
         return false;
      }

      PersistentMessage::SaveObject(pNewMessage);

      // Wake the delivery manager so the redirected copy is sent promptly rather
      // than waiting for the next queue poll.
      DeliveryQueue::StartDelivery();

      return true;
   }

}

