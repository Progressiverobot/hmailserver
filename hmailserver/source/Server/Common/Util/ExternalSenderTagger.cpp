// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "ExternalSenderTagger.h"

#include "MessageOrigin.h"

#include "../Application/ErrorManager.h"
#include "../BO/Account.h"
#include "../BO/Domain.h"
#include "../BO/Message.h"
#include "../BO/MessageData.h"
#include "../Persistence/PersistentKnownSender.h"
#include "../Persistence/PersistentMessage.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   String
   ExternalSenderTagger::TagSubject(const String &subject, const String &tagText)
   {
      if (tagText.IsEmpty())
         return subject;

      String leading = subject;
      leading.TrimLeft();

      // Compared without case, because the tag comes back through other mail
      // systems with its case intact but its position after a Re:, and because
      // an administrator who changed [EXTERNAL] to [External] should not find
      // every reply carrying both.
      if (leading.GetLength() >= tagText.GetLength() &&
          leading.Mid(0, tagText.GetLength()).CompareNoCase(tagText) == 0)
         return subject;

      if (subject.IsEmpty())
         return tagText;

      return tagText + _T(" ") + subject;
   }

   bool
   ExternalSenderTagger::Apply(std::shared_ptr<const Account> account,
                               std::shared_ptr<const Domain> domain,
                               std::shared_ptr<Message> message,
                               const String &sendersIP)
   {
      if (!account || !domain || !message)
         return false;

      const bool wantSubject = domain->GetExternalTagSubject();
      const bool wantHeader = domain->GetExternalTagHeader();
      const bool wantFirstContact = domain->GetFirstContactTip();

      if (!wantSubject && !wantHeader && !wantFirstContact)
         return false;

      const String fileName = PersistentMessage::GetFileName(account, message);

      const AnsiString header = PersistentMessage::LoadHeader(fileName, false);

      // Nothing stops a sender writing these two fields themselves. A forged one
      // can only make their own message look more suspicious than it is, never
      // less, so it is not a hole - but a domain that has turned this on and is
      // therefore relying on the fields should have them mean what this server
      // says rather than what a stranger typed. Where the answer is yes the
      // write below replaces the field; this is the other direction.
      const bool carriesForgedExternal = !MessageOrigin::FirstFieldValue(header, ExternalHeader()).IsEmpty();
      const bool carriesForgedFirstContact = !MessageOrigin::FirstFieldValue(header, FirstContactHeader()).IsEmpty();

      MessageOrigin::Facts facts = MessageOrigin::ReadFacts(message, header, sendersIP);

      String reason;
      const bool external = MessageOrigin::IsExternalSender(facts, reason);

      if (!external)
      {
         String logText;
         logText.Format(_T("ExternalSenderTagger - Message %I64d for %s was not tagged: %s."),
            message->GetID(), account->GetAddress().c_str(), reason.c_str());
         LOG_DEBUG(logText);

         if (!carriesForgedExternal && !carriesForgedFirstContact)
            return false;
      }

      // The note is for senders from outside only. A colleague writing for the
      // first time is not a warning - and the memory would otherwise cost a
      // query on every internal message for nothing.
      bool firstContact = false;

      if (wantFirstContact && external)
      {
         const String senderAddress = facts.header_from.IsEmpty() ? facts.envelope_sender : facts.header_from;

         firstContact = PersistentKnownSender::Remember(account->GetID(), senderAddress) ==
                        PersistentKnownSender::FirstContact;
      }

      const bool stampExternal = external && wantHeader;

      if (!stampExternal && !firstContact && !(external && wantSubject) &&
          !carriesForgedExternal && !carriesForgedFirstContact)
         return false;

      MessageData data;

      if (!data.LoadFromMessage(fileName, message))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6560, "ExternalSenderTagger::Apply",
            Formatter::Format("Message {0} could not be loaded while tagging it as external for {1}. The copy is delivered untagged.",
               message->GetID(), account->GetAddress()));
         return false;
      }

      bool changed = false;

      if (stampExternal)
      {
         data.SetFieldValue(String(ExternalHeader()), _T("YES"));
         changed = true;
      }
      else if (carriesForgedExternal)
      {
         data.DeleteField(ExternalHeader());
         changed = true;
      }

      if (firstContact)
      {
         data.SetFieldValue(String(FirstContactHeader()), _T("YES"));
         changed = true;
      }
      else if (carriesForgedFirstContact)
      {
         data.DeleteField(FirstContactHeader());
         changed = true;
      }

      if (external && wantSubject)
      {
         String tagText = domain->GetExternalTagText();
         if (tagText.IsEmpty())
            tagText = DefaultTagText();

         const String subject = data.GetFieldValue(_T("Subject"));
         const String tagged = TagSubject(subject, tagText);

         if (tagged != subject)
         {
            data.SetFieldValue(_T("Subject"), tagged);
            changed = true;
         }
      }

      // A subject that already carried the tag and a header that was already
      // right leave nothing to write, and rewriting the file anyway would give
      // this recipient a copy of its own for no reason.
      if (!changed)
         return false;

      if (!data.WriteReported(fileName, "The external-sender tag"))
      {
         // WriteReported has already reported the failure itself. What belongs
         // here is the consequence: the copy is delivered as it arrived, so the
         // reader sees no tag. Nothing is lost and no mail is refused.
         String logText;
         logText.Format(_T("ExternalSenderTagger - Message %I64d was delivered to %s without its external-sender tag: the rewrite of the message file failed."),
            message->GetID(), account->GetAddress().c_str());
         LOG_APPLICATION(logText);
         return false;
      }

      return true;
   }

   void
   ExternalSenderTaggerTester::Test()
   {
      if (ExternalSenderTagger::TagSubject(_T("Invoice"), _T("[EXTERNAL]")) != _T("[EXTERNAL] Invoice"))
         throw std::logic_error("The external tag was not put in front of the subject.");

      if (ExternalSenderTagger::TagSubject(_T(""), _T("[EXTERNAL]")) != _T("[EXTERNAL]"))
         throw std::logic_error("A message with no subject did not get the tag alone.");

      // A reply coming back from outside already carries it.
      if (ExternalSenderTagger::TagSubject(_T("[EXTERNAL] Invoice"), _T("[EXTERNAL]")) != _T("[EXTERNAL] Invoice"))
         throw std::logic_error("The external tag was added twice to a subject that already carried it.");

      if (ExternalSenderTagger::TagSubject(_T("[external] Invoice"), _T("[EXTERNAL]")) != _T("[external] Invoice"))
         throw std::logic_error("The already-tagged test is case-sensitive; a second tag was added.");

      // A domain that says it in another language gets what it configured.
      if (ExternalSenderTagger::TagSubject(_T("Rechnung"), _T("[EXTERN]")) != _T("[EXTERN] Rechnung"))
         throw std::logic_error("A domain's own tag text was not used.");

      if (ExternalSenderTagger::TagSubject(_T("Invoice"), _T("")) != _T("Invoice"))
         throw std::logic_error("An empty tag text changed the subject.");
   }
}
