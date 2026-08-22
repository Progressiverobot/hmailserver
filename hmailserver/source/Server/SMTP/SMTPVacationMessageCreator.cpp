// Copyright (c) 2010 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "SMTPVacationMessageCreator.h"

#include "../Common/BO/Account.h"
#include "../Common/BO/Alias.h"
#include "../Common/BO/Message.h"
#include "../Common/BO/MessageData.h"
#include "../Common/Cache/CacheContainer.h"
#include "../Common/Mime/Mime.h"
#include "../Common/Util/Time.h"
#include "../Common/Util/Parsing/StringParser.h"
#include "../Common/Persistence/PersistentMessage.h"
#include "RecipientParser.h"
#include "../Common/BO/MessageRecipients.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{

   SMTPVacationMessageCreator::SMTPVacationMessageCreator()
   {

   }

   SMTPVacationMessageCreator::~SMTPVacationMessageCreator()
   {

   }


   void
   SMTPVacationMessageCreator::CreateVacationMessage(std::shared_ptr<const Account> recipientAccount,
                                        const String &sToAddress,
                                        const String &sVacationSubject,
                                        const String &sVacationMessage,
                                        const std::shared_ptr<Message> pOriginalMessage)
   {
      // RFC 3834 2: never answer a message with a null envelope return path
      // (MAIL FROM:<>). That is a bounce or another automatic notice, and answering
      // one is how two servers hand each other replies until one gives up. This was
      // previously enforced one level down, as CanSendVacationMessage_'s "we need a
      // recipient" check; it is a loop guard, so it is stated as one.
      if (sToAddress.IsEmpty())
         return;

      // Load the original message's header once. It answers three questions below:
      // whether an automatic reply may answer this message at all, what the original
      // subject was for the %SUBJECT% macro, and what to put after "Re: " when the
      // reply has no subject of its own.
      const String originalFileName = PersistentMessage::GetFileName(recipientAccount, pOriginalMessage);

      MimeHeader header;
      AnsiString sHeader = PersistentMessage::LoadHeader(originalFileName);
      header.Load(sHeader, sHeader.GetLength(), true);

      // The RFC 3834 gate, before the once-per-sender slot below is spent: a
      // suppressed message must not burn the one reply this sender will ever get,
      // or a colleague whose first message arrives via a mailing list is never
      // answered directly either.
      String suppressReason;
      if (ShouldSuppressAutoReply_(header, sToAddress, suppressReason))
      {
         LOG_DEBUG(_T("No out-of-office message was sent: ") + suppressReason);
         return;
      }

      if (!CanSendVacationMessage_(recipientAccount->GetAddress(), sToAddress))
         return; // We have already notified this user.

      LOG_DEBUG("Creating out-of-office message.");

      String sModifiedSubject = sVacationSubject;
      String sModifiedBody = sVacationMessage;

      String sOldSubject;
      if (header.GetField("Subject"))
         sOldSubject = header.GetField("Subject")->GetValue();

      if (sModifiedSubject.Find(_T("%")) >= 0 || sModifiedBody.Find(_T("%")) >= 0)
      {
         // Replace macros in the string.
         sModifiedSubject.ReplaceNoCase(_T("%SUBJECT%"), sOldSubject);
         sModifiedBody.ReplaceNoCase(_T("%SUBJECT%"), sOldSubject);
      }

      if (sModifiedSubject.GetLength() == 0)
      {
         // No subject was configured, so Re: the original message's subject.
         sModifiedSubject = "Re: " + sOldSubject;
      }


      // Reply to the email
      std::shared_ptr<Message> pMsg = std::shared_ptr<Message>(new Message());

      pMsg->SetState(Message::Delivering);

      // RFC 3834 4: an automatic response is sent with a null envelope return path,
      // so that a bounce of the response cannot start a second exchange. A fresh
      // Message's from address is already empty; setting it explicitly is what makes
      // that a decision rather than an accident - TraceHeaderWriter turns it into
      // "Return-Path: <>", which is what the far end's own auto-responder looks at
      // before deciding whether to answer this reply.
      pMsg->SetFromAddress(_T(""));

      const String newFileName = PersistentMessage::GetFileName(pMsg);

      std::shared_ptr<MessageData> pNewMsgData = std::shared_ptr<MessageData>(new MessageData());
      pNewMsgData->LoadFromMessage(newFileName, pMsg);

      // Required headers
      pNewMsgData->GenerateMessageID();
      pNewMsgData->SetSentTime(Time::GetCurrentMimeDate());
      pNewMsgData->SetFieldValue("Content-Type", "text/plain; charset=\"utf-8\"");

      // Optional headers
      pNewMsgData->SetFrom(recipientAccount->GetAddress());
      pNewMsgData->SetTo(sToAddress);
      pNewMsgData->SetSubject(sModifiedSubject);
      pNewMsgData->SetBody(sModifiedBody);
	  pNewMsgData->SetAutoReplied();
      pNewMsgData->IncreaseRuleLoopCount();

      // Write message data. First write of a message built from nothing - see the
      // equivalent in SMTPDeliverer. No out-of-office reply is better than one queued
      // with no file behind it.
      if (!pNewMsgData->WriteReported(newFileName, "An out-of-office reply"))
         return;

      // Add recipients.
      bool recipientOK = false;
      RecipientParser recipientParser;
      recipientParser.CreateMessageRecipientList(sToAddress, pMsg->GetRecipients(), recipientOK);

      // Save message. Checked for the same reason the WriteReported above it is: the
      // pair that leaves nothing behind is a file with no row and a row with no file,
      // and this end was unguarded while the other was.
      if (pMsg->GetRecipients()->GetCount() > 0)
      {
         if (!PersistentMessage::SaveObject(pMsg))
         {
            FileUtilities::DeleteFile(newFileName);

            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6089, "SMTPVacationMessageCreator::CreateVacationMessage",
               "The out-of-office reply could not be saved and has not been sent. Its file has been removed rather than left on disk with nothing referring to it.");
         }
      }

      // Tell app to submit mail
      Application::Instance()->SubmitPendingEmail();
   }

   bool
   SMTPVacationMessageCreator::ShouldSuppressAutoReply_(const MimeHeader &originalHeader,
                                                        const String &senderAddress,
                                                        String &reason)
   {
      // RFC 3834 2: an automatic response must not be sent to a message that is
      // itself automatic. This is one half of the contract whose other half is the
      // Auto-Submitted: auto-replied stamped onto every reply this class sends -
      // without this check, two servers each running the other half loop until one
      // of them gives up.
      MimeField *autoSubmitted = originalHeader.GetField("Auto-Submitted");
      if (autoSubmitted)
      {
         String value = autoSubmitted->GetValue();
         int semicolon = value.Find(_T(";"));
         if (semicolon >= 0)
            value = value.Mid(0, semicolon);
         value.Trim();

         if (!value.IsEmpty() && value.CompareNoCase(_T("no")) != 0)
         {
            reason = _T("the message carries an Auto-Submitted header.");
            return true;
         }
      }

      // The pre-RFC-3834 convention for the same statement.
      MimeField *precedence = originalHeader.GetField("Precedence");
      if (precedence)
      {
         String value = precedence->GetValue();
         value.Trim();

         if (value.CompareNoCase(_T("bulk")) == 0 ||
             value.CompareNoCase(_T("list")) == 0 ||
             value.CompareNoCase(_T("junk")) == 0)
         {
            reason = _T("the message has a bulk, list or junk Precedence.");
            return true;
         }
      }

      // No reply to mailing-list traffic: a holiday notice sent back to a list
      // reaches every subscriber or the list robot, and either is how auto-replies
      // get a server blacklisted. Any of the RFC 2369 / RFC 2919 headers is enough
      // to identify it.
      static const char *listHeaders[] =
      {
         "List-Id", "List-Help", "List-Subscribe", "List-Unsubscribe",
         "List-Post", "List-Owner", "List-Archive"
      };

      for (const char *listHeader : listHeaders)
      {
         if (originalHeader.FieldExists(listHeader))
         {
            reason = _T("the message came from a mailing list.");
            return true;
         }
      }

      // The Microsoft convention for "do not auto-reply to this", which Exchange
      // and Outlook emit and honour.
      MimeField *suppress = originalHeader.GetField("X-Auto-Response-Suppress");
      if (suppress)
      {
         String value = suppress->GetValue();
         value.ToLower();

         if (value.Find(_T("all")) >= 0 || value.Find(_T("oof")) >= 0 || value.Find(_T("autoreply")) >= 0)
         {
            reason = _T("the message asks that auto-replies be suppressed.");
            return true;
         }
      }

      // The two robot mailboxes RFC 3834 2 names itself. Real bounces arrive with a
      // null return path and are already refused above this, but a postmaster
      // writing with a real return path is still not a correspondent to send a
      // holiday notice to. The fuller set of robot and list-management local parts
      // lives in SieveEvaluator::IsAutomatedSenderAddress_; the two lists should be
      // consolidated into one shared home rather than grown separately.
      int at = senderAddress.Find(_T("@"));
      String localPart = at >= 0 ? senderAddress.Mid(0, at) : senderAddress;
      if (localPart.CompareNoCase(_T("mailer-daemon")) == 0 ||
          localPart.CompareNoCase(_T("postmaster")) == 0)
      {
         reason = _T("the sender is a robot mailbox.");
         return true;
      }

      return false;
   }

   bool
   SMTPVacationMessageCreator::IsInternalSender(const String &senderAddress)
   {
      if (senderAddress.IsEmpty())
         return false;

      if (!StringParser::IsValidEmailAddress(senderAddress))
         return false;

      std::shared_ptr<const Account> account = CacheContainer::Instance()->GetAccount(senderAddress);
      if (account)
         return true;

      std::shared_ptr<const Alias> alias = CacheContainer::Instance()->GetAlias(senderAddress);
      if (alias && alias->GetIsActive())
         return true;

      return false;
   }

   bool
   SMTPVacationMessageCreator::CanSendVacationMessage_(const String &sFrom, const String &sTo)
   {
      if (sTo.IsEmpty())
      {
         // We need a recipient address to be able to send.
         return false;
      }

      try
      {
         boost::lock_guard<boost::recursive_mutex> guard(mutex_);

         auto iter = mapVacationMessageRecipients.find(sFrom);

         if (iter == mapVacationMessageRecipients.end())
         {
            mapVacationMessageRecipients.insert(std::make_pair(sFrom, sTo));
            return true;
         }

         auto iterRange =
            mapVacationMessageRecipients.equal_range(sFrom);

         iter = iterRange.first;
         while (iter != iterRange.second)
         {
            String sCurTo = (*iter).second;

            if (sCurTo == sTo)
            {
               // This recipient has already receivedd
               // a vacation-reply.
               return false;
            }

            iter++;
         }

         mapVacationMessageRecipients.insert(std::make_pair(sFrom, sTo));
      }
      catch (...)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 5022, "SMTPVacationMessageCreator::CanSendVacationMessage", "Error when doing lookup.");
      }

      return true;
   }

   void
   SMTPVacationMessageCreator::VacationMessageTurnedOff(const String &sUserAddress)
   {
      try
      {
         boost::lock_guard<boost::recursive_mutex> guard(mutex_);

         auto iterRange =
            mapVacationMessageRecipients.equal_range(sUserAddress);

         auto iter = iterRange.first;
         while (iter != iterRange.second)
            iter = mapVacationMessageRecipients.erase(iter);
      }
      catch (...)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 5023, "SMTPVacationMessageCreator::VacationMessageTurnedOff", "Error when removing user from list.");
      }
   }

   void
   SMTPVacationMessageCreator::DomainVacationMessageTurnedOff(const String &sDomainName)
   {
      try
      {
         boost::lock_guard<boost::recursive_mutex> guard(mutex_);

         auto iter = mapVacationMessageRecipients.begin();
         while (iter != mapVacationMessageRecipients.end())
         {
            if (StringParser::ExtractDomain((*iter).first).CompareNoCase(sDomainName) == 0)
               iter = mapVacationMessageRecipients.erase(iter);
            else
               ++iter;
         }
      }
      catch (...)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 6320, "SMTPVacationMessageCreator::DomainVacationMessageTurnedOff", "Error when removing a domain's accounts from the list of already-sent replies.");
      }
   }
}
