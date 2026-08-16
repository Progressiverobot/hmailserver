// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#include "StdAfx.h"

#include "SieveNotifyResponder.h"

#include "../BO/Account.h"
#include "../BO/Message.h"
#include "../BO/MessageData.h"
#include "../BO/MessageRecipients.h"
#include "../Persistence/PersistentMessage.h"
#include "../../SMTP/RecipientParser.h"
#include "../Util/FileUtilities.h"
#include "../Util/Time.h"
#include "../Application/Application.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   bool
   SieveNotifyResponder::Send(std::shared_ptr<const Account> account, const SieveNotifyDecision &decision)
   {
      if (!account || decision.to.IsEmpty())
         return false;

      try
      {
         std::shared_ptr<Message> notification = std::shared_ptr<Message>(new Message());
         notification->SetState(Message::Delivering);

         // Null envelope return path (RFC 5436 2.7.1, same reasoning as RFC 3834
         // for vacation): a bounce of a notification must die, not converse.
         notification->SetFromAddress(_T(""));

         const String fileName = PersistentMessage::GetFileName(notification);

         MessageData data;
         if (!data.LoadFromMessage(fileName, notification))
         {
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5902, "SieveNotifyResponder::Send",
               "Could not initialise the message data for a Sieve notification.");
            return false;
         }

         data.GenerateMessageID();
         data.SetSentTime(Time::GetCurrentMimeDate());
         data.SetFrom(decision.from.IsEmpty() ? String(account->GetAddress()) : decision.from);
         data.SetTo(decision.to);
         data.SetSubject(decision.subject);

         // RFC 5436 2.7.1: the marker that stops the next auto-responder from
         // answering a notification. "auto-notified" rather than "auto-replied",
         // so the two kinds of generated mail stay distinguishable in a trace.
         data.SetFieldValue(_T("Auto-Submitted"), _T("auto-notified"));
         data.SetFieldValue(_T("X-Auto-Response-Suppress"), _T("All"));

         // RFC 5435 3.2's three-level importance, in the header vocabulary mail
         // clients actually read.
         if (decision.importance == 1)
            data.SetFieldValue(_T("Importance"), _T("High"));
         else if (decision.importance == 3)
            data.SetFieldValue(_T("Importance"), _T("Low"));

         data.SetRuleLoopCount(decision.loopCount + 1);

         String body;
         body += _T("You have new mail.\r\n\r\n");
         if (!decision.originalFrom.IsEmpty())
            body += _T("From: ") + decision.originalFrom + _T("\r\n");
         if (!decision.originalSubject.IsEmpty())
            body += _T("Subject: ") + decision.originalSubject + _T("\r\n");

         data.SetFieldValue(_T("Content-Type"), _T("text/plain; charset=\"utf-8\""));
         data.SetBody(body);

         if (!data.Write(fileName))
         {
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5902, "SieveNotifyResponder::Send",
               "Could not write the message file for a Sieve notification.");
            return false;
         }

         bool recipientOK = false;
         RecipientParser recipientParser;
         recipientParser.CreateMessageRecipientList(decision.to, notification->GetRecipients(), recipientOK);

         if (notification->GetRecipients()->GetCount() == 0)
         {
            // No deliverable recipient: leaving the file would leak one message
            // file per attempt with no database row ever collecting it.
            FileUtilities::DeleteFile(fileName);

            String message;
            message.Format(_T("Sieve notify: no notification was sent, no deliverable recipient could be built from '%s'."),
               decision.to.c_str());
            LOG_APPLICATION(message);
            return false;
         }

         if (!PersistentMessage::SaveObject(notification))
         {
            FileUtilities::DeleteFile(fileName);
            return false;
         }

         Application::Instance()->SubmitPendingEmail();

         return true;
      }
      catch (...)
      {
         // A notification is a courtesy; the delivery it describes must never
         // depend on it.
         return false;
      }
   }
}
