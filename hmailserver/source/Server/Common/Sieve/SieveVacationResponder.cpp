// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#include "StdAfx.h"

#include "SieveVacationResponder.h"

#include "SieveVacationTracker.h"

#include "../BO/Account.h"
#include "../BO/Alias.h"
#include "../BO/Message.h"
#include "../BO/MessageData.h"
#include "../BO/MessageRecipients.h"
#include "../Cache/CacheContainer.h"
#include "../Mime/Mime.h"
#include "../Persistence/PersistentMessage.h"
#include "../Util/Charset.h"
#include "../Util/FileUtilities.h"
#include "../Util/Time.h"

#include "../../SMTP/RecipientParser.h"
#include "../../SMTP/SMTPConfiguration.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      const __int64 SecondsPerDay = 86400;

      // The longest ":days" window that will be honoured, a century. The validator
      // only enforces a minimum, so without a ceiling here a script saying
      // ":days 100000000" would multiply into a number that is not a time at all;
      // the tracker would then either clamp it or, worse in an earlier arrangement,
      // read a wrapped negative value as ":seconds 0" and answer every message. A
      // century means "effectively never again", which is what such a script meant.
      const __int64 MaxWindowDays = 36500;

      // How much of the original subject is quoted back in the reply's own subject.
      // RFC 5230 4.2 asks for "Auto: " followed by the original subject and
      // explicitly allows a different subject when that would be too long; this is
      // where "too long" is decided. A subject long enough to need folding over
      // several lines is a header that some client somewhere will mangle, and the
      // point of quoting it at all is only to help the recipient recognise which of
      // their messages this answers.
      const int MaxQuotedSubjectLength = 180;

      // The headers a ":mime" reason is allowed to contribute to the reply.
      //
      // This is a whitelist rather than a blacklist on purpose. RFC 5230 4.3 says the
      // reason is a MIME entity whose headers describe the body, which is exactly the
      // "Content-*" set plus MIME-Version. Everything else is refused, and the reason
      // is a security one rather than tidiness: a reason string is script text, so a
      // blacklist would have to enumerate every header that must not be forgeable. It
      // would need to include Auto-Submitted (clearing it re-enables the loops this
      // whole feature is built to avoid), Return-Path, Received, From, To, Bcc,
      // Message-ID and hMailServer's own loop counter - and the next header worth
      // protecting would be forgotten. A whitelist fails the other way: a new header
      // is dropped until someone decides to allow it.
      bool IsAllowedMimeReasonHeader(const String &lowerName)
      {
         if (lowerName.StartsWith(_T("content-")))
            return true;

         return lowerName.Compare(_T("mime-version")) == 0;
      }
   }

   SieveVacationResponder::SieveVacationResponder()
   {
   }

   __int64
   SieveVacationResponder::SuppressionWindowSeconds_(const SieveVacationDecision &decision)
   {
      if (decision.secondsGiven)
      {
         // RFC 6131. Zero is legal and means "reply to every qualifying message", so
         // it is passed through as zero rather than being treated as "unset". The
         // validator has already refused a negative value.
         //
         // There is deliberately no site-wide minimum applied on top. An
         // administrator floor was considered and rejected: a script that says
         // ":seconds 30" and is silently given a longer window is carrying out a
         // different instruction from the one written, RFC 6131 does not permit that,
         // and the loop guards that actually matter here (Auto-Submitted, the null
         // return path, the robot local parts, the generated-mail loop counter) do
         // not depend on the window at all. A short window costs repeated replies to
         // one correspondent who keeps writing; it cannot start a loop, because the
         // reply itself carries Auto-Submitted: auto-replied and a null return path.
         return decision.seconds;
      }

      // RFC 5230 4.1: ":days" defaults to 7, which is what the decision carries when
      // the script gave no ":days".
      __int64 days = decision.days > MaxWindowDays ? MaxWindowDays : decision.days;
      return days * SecondsPerDay;
   }

   String
   SieveVacationResponder::ResolveFromAddress_(std::shared_ptr<const Account> account, const String &requested)
   {
      String accountAddress = account->GetAddress();

      if (requested.IsEmpty())
         return accountAddress;

      String candidate = requested;
      candidate.Trim();

      if (candidate.CompareNoCase(accountAddress) == 0)
         return accountAddress;

      // RFC 5230 4.4 allows an implementation to restrict what ":from" may say, and
      // this one does: the reply may only claim to come from an address that actually
      // reaches this account. Without the check a script - which an ordinary user can
      // upload over ManageSieve - would be a way to send mail that appears to come
      // from anyone at all, using the server's own reputation to do it. That is a
      // spoofing primitive, not a convenience.
      std::shared_ptr<const Account> other = CacheContainer::Instance()->GetAccount(candidate);
      if (other && other->GetID() == account->GetID())
         return candidate;

      std::shared_ptr<const Alias> alias = CacheContainer::Instance()->GetAlias(candidate);
      if (alias && alias->GetIsActive() && alias->GetValue().CompareNoCase(accountAddress) == 0)
         return candidate;

      // Logged rather than reported: a user writing a script with somebody else's
      // address in it is a mistake an ordinary installation can make, and the
      // administrator is the one who wants to know about it. An ErrorManager report
      // here would fail every fixture's clean-error-log assertion for a script the
      // server handled correctly.
      String message;
      message.Format(_T("Sieve vacation: the script for %s asked for ':from \"%s\"', which is not an address that reaches ")
                     _T("that account. The reply was sent from %s instead."),
                     accountAddress.c_str(), candidate.c_str(), accountAddress.c_str());
      LOG_APPLICATION(message);

      return accountAddress;
   }

   String
   SieveVacationResponder::DecodeHeaderValue_(const String &rawValue)
   {
      if (rawValue.IsEmpty())
         return rawValue;

      // The value arrives as it appeared in the message, so an RFC 2047 encoded word
      // is still encoded. Quoting that into the reply's subject verbatim and letting
      // MessageData encode the result again would produce nested encoded words that
      // no client displays. Decoding first and re-encoding once is the only way round
      // that.
      //
      // A raw 8-bit subject (no encoded words) has already been through the platform
      // code page on its way from the message file into the evaluator's String, so
      // converting it back to bytes here is the same conversion in reverse and is
      // lossless for anything that survived the first one. Widening that to true byte
      // fidelity is a change to how the whole Sieve path reads a message, not
      // something to bolt on here.
      String decoded = MimeHeader::GetUnicodeFieldValue("Subject", AnsiString(rawValue));

      // A decoder that cannot make sense of the value must not silently turn the
      // subject into nothing: the reply would then say "Automatic reply" and give the
      // recipient no way to tell which of their messages it answers. The undecoded
      // value is the better of the two wrong answers.
      if (decoded.IsEmpty())
         return rawValue;

      return decoded;
   }

   String
   SieveVacationResponder::BuildSubject_(const SieveVacationDecision &decision)
   {
      if (!decision.subject.IsEmpty())
         return decision.subject;

      String original = DecodeHeaderValue_(decision.originalSubject);
      original.Trim();

      if (original.IsEmpty())
      {
         // RFC 5230 4.2 allows a different subject when the original is absent, which
         // it must, because "Auto: " on its own says nothing.
         return _T("Automatic reply");
      }

      if (original.GetLength() > MaxQuotedSubjectLength)
         original = original.Mid(0, MaxQuotedSubjectLength) + _T("...");

      // "Auto: " and not "Re: ", which is what the account-level auto-reply uses.
      // RFC 5230 4.2 names "Auto: " specifically, and the difference matters to a
      // recipient's own filters: "Re:" claims to be part of a conversation and some
      // clients thread it into one, whereas this is a notice about the mailbox.
      return _T("Auto: ") + original;
   }

   void
   SieveVacationResponder::SplitMimeReason_(const String &reason,
                                            std::vector<std::pair<String, String>> &headers,
                                            String &body)
   {
      // Find the header/body separator, accepting CRLF or LF. The same two-form
      // tolerance SieveMessage applies, and for the same reason: a reason string
      // comes out of a Sieve script, where a multi-line string may have been written
      // with either line ending.
      int boundary = reason.Find(_T("\r\n\r\n"));
      int separatorLength = 4;
      if (boundary < 0)
      {
         boundary = reason.Find(_T("\n\n"));
         separatorLength = 2;
      }

      if (boundary < 0)
      {
         // No blank line, so there is no header block. Treating the whole reason as
         // the body is the reading that still produces the message the author
         // obviously meant; treating it as headers would produce a message with no
         // body at all.
         LOG_DEBUG("Sieve vacation: a ':mime' reason had no blank line, so all of it was taken as the body.");
         body = reason;
         return;
      }

      String headerBlock = reason.Mid(0, boundary);
      body = reason.Mid(boundary + separatorLength);

      headerBlock.Replace(_T("\r\n"), _T("\n"));

      std::vector<String> lines;
      int start = 0;
      while (start <= headerBlock.GetLength())
      {
         int newline = headerBlock.Find(_T("\n"), start);
         if (newline < 0)
         {
            lines.push_back(headerBlock.Mid(start));
            break;
         }

         lines.push_back(headerBlock.Mid(start, newline - start));
         start = newline + 1;
      }

      String currentName;
      String currentValue;
      bool haveCurrent = false;

      for (const String &line : lines)
      {
         if (line.GetLength() > 0 && (line[0] == L' ' || line[0] == L'\t'))
         {
            if (haveCurrent)
            {
               String continuation = line;
               continuation.TrimLeft();
               currentValue += _T(" ");
               currentValue += continuation;
            }

            continue;
         }

         if (haveCurrent)
         {
            headers.push_back(std::make_pair(currentName, currentValue));
            haveCurrent = false;
         }

         int colon = line.Find(_T(":"));
         if (colon <= 0)
            continue;

         currentName = line.Mid(0, colon);
         currentName.TrimRight();
         currentValue = line.Mid(colon + 1);
         currentValue.TrimLeft();
         haveCurrent = true;
      }

      if (haveCurrent)
         headers.push_back(std::make_pair(currentName, currentValue));
   }

   void
   SieveVacationResponder::ApplyMimeReason_(MessageData &data, const String &reason)
   {
      std::vector<std::pair<String, String>> headers;
      String body;
      SplitMimeReason_(reason, headers, body);

      bool transferEncodingGiven = false;

      for (const std::pair<String, String> &header : headers)
      {
         String lowerName = header.first;
         lowerName.ToLower();

         if (!IsAllowedMimeReasonHeader(lowerName))
         {
            LOG_DEBUG(_T("Sieve vacation: a ':mime' reason tried to set the header '") + header.first +
                      _T("', which is not one a reason may set. It was ignored."));
            continue;
         }

         data.SetFieldValue(header.first, header.second);

         if (lowerName.Compare(_T("content-transfer-encoding")) == 0)
            transferEncodingGiven = true;
      }

      std::shared_ptr<MimeBody> entity = data.GetMimeMessage();

      String mainType = entity->GetMainType().c_str();

      // Two cases, and the difference is who owns the encoding.
      //
      // If the entity declared a Content-Transfer-Encoding, the script has already
      // encoded the body and our job is to pass the bytes through untouched -
      // re-encoding base64 as quoted-printable would corrupt it. The same applies to
      // a multipart entity: its "body" is a sequence of parts with boundary lines,
      // and applying a transfer encoding across the whole thing is not legal MIME.
      //
      // Otherwise we own the encoding, and SetUnicodeText is the right tool: it
      // honours whatever charset the entity's Content-Type named (defaulting to
      // UTF-8), encodes to it, and sets the matching Content-Transfer-Encoding.
      if (transferEncodingGiven || mainType.CompareNoCase(_T("multipart")) == 0)
      {
         AnsiString charsetName = entity->GetCharset().c_str();
         if (charsetName.IsEmpty())
            charsetName = "utf-8";

         entity->SetRawText(Charset::ToMultiByte(body, charsetName));
         return;
      }

      entity->SetUnicodeText(body);
   }

   bool
   SieveVacationResponder::ComposeAndSubmit_(std::shared_ptr<const Account> account,
                                             const SieveVacationDecision &decision,
                                             const String &fromAddress)
   {
      std::shared_ptr<Message> reply = std::shared_ptr<Message>(new Message());
      reply->SetState(Message::Delivering);

      // RFC 3834 4: an automatic response is sent with a null envelope return path,
      // so that a bounce of the response cannot start a second exchange. Message's
      // from address is already empty; setting it explicitly is what makes that a
      // decision rather than an accident, because TraceHeaderWriter turns it into
      // "Return-Path: <>" and that header is what the far end's own auto-responder
      // looks at before deciding whether to answer this message.
      reply->SetFromAddress(_T(""));

      const String fileName = PersistentMessage::GetFileName(reply);

      MessageData data;
      if (!data.LoadFromMessage(fileName, reply))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5896, "SieveVacationResponder::ComposeAndSubmit_",
            "Could not initialise the message data for a Sieve vacation auto-reply.");
         return false;
      }

      data.GenerateMessageID();
      data.SetSentTime(Time::GetCurrentMimeDate());
      data.SetFrom(fromAddress);

      // The reply goes to the envelope sender, which is what the decision carries,
      // and never to the original From. They are different addresses whenever the
      // message was sent through a list, forwarded, or forged, and it is precisely in
      // those cases that answering the From address delivers a holiday notice to
      // someone who never wrote to this user.
      data.SetTo(decision.to);
      data.SetSubject(BuildSubject_(decision));

      // RFC 3834 5 / RFC 5230 4.6. This is the header that tells the next
      // auto-responder in the chain not to answer, and SieveEvaluator's
      // SuppressVacation_ is the side of the same contract that honours it in the
      // other direction.
      data.SetAutoReplied();

      // The Microsoft convention for the same thing, which Exchange and Outlook
      // honour and Auto-Submitted alone does not reach.
      data.SetFieldValue(_T("X-Auto-Response-Suppress"), _T("All"));

      if (!decision.originalMessageId.IsEmpty())
      {
         // RFC 3834 3.1.4: an automatic response should identify the message it
         // answers, which is also what lets the recipient's client thread it.
         data.SetFieldValue(_T("In-Reply-To"), decision.originalMessageId);
         data.SetFieldValue(_T("References"), decision.originalMessageId);
      }

      // Carry hMailServer's generated-mail loop counter forward. RuleApplier and
      // SMTPForwarding do the same, and the point is that every cycle through any of
      // them increments it, so IsGeneratedResponseAllowed eventually stops a cycle
      // that this responder is only one participant in.
      data.SetRuleLoopCount(decision.loopCount + 1);

      if (decision.mime)
      {
         ApplyMimeReason_(data, decision.reason);
      }
      else
      {
         data.SetFieldValue(_T("Content-Type"), _T("text/plain; charset=\"utf-8\""));
         data.SetBody(decision.reason);
      }

      if (!data.Write(fileName))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5896, "SieveVacationResponder::ComposeAndSubmit_",
            "Could not write the message file for a Sieve vacation auto-reply.");
         return false;
      }

      bool recipientOK = false;
      RecipientParser recipientParser;
      recipientParser.CreateMessageRecipientList(decision.to, reply->GetRecipients(), recipientOK);

      if (reply->GetRecipients()->GetCount() == 0)
      {
         // Nothing to deliver to. Leaving the file behind would leak a message file
         // per attempt into the queue directory, where nothing would ever collect it
         // because no database row refers to it.
         FileUtilities::DeleteFile(fileName);

         String message;
         message.Format(_T("Sieve vacation: no auto-reply for %s, no deliverable recipient could be built from the ")
                        _T("envelope sender '%s'."),
                        String(account->GetAddress()).c_str(), decision.to.c_str());
         LOG_APPLICATION(message);
         return false;
      }

      if (!PersistentMessage::SaveObject(reply))
      {
         FileUtilities::DeleteFile(fileName);
         return false;
      }

      Application::Instance()->SubmitPendingEmail();

      return true;
   }

   void
   SieveVacationResponder::Respond(std::shared_ptr<const Account> account, const SieveVacationDecision &decision)
   {
      if (!decision.send)
         return;

      if (!account)
         return;

      try
      {
         String recipient = decision.to;
         recipient.Trim();

         if (recipient.IsEmpty())
         {
            // The evaluator refuses to set "send" without a sender to reply to, so
            // arriving here means the decision was built inconsistently - a defect in
            // this feature rather than anything an installation did.
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5899, "SieveVacationResponder::Respond",
               "A Sieve vacation decision asked for an auto-reply but carried no recipient. No reply was sent.");
            return;
         }

         // Defence in depth. SieveEvaluator::SuppressVacation_ has already refused to
         // answer the user's own address, but this is the last point before a message
         // is actually queued, and a self-reply is the one that loops fastest: every
         // reply is a new delivery to the same account, which evaluates the same
         // script again.
         if (recipient.CompareNoCase(account->GetAddress()) == 0)
         {
            LOG_DEBUG("Sieve vacation: no auto-reply, the envelope sender is the account's own address.");
            return;
         }

         int loopLimit = Configuration::Instance()->GetSMTPConfiguration()->GetRuleLoopLimit();
         if (decision.loopCount >= loopLimit)
         {
            // The same limit RuleApplier::IsGeneratedResponseAllowed enforces for
            // rule-generated mail. A message that has already been through that many
            // generated hops is in a cycle, whatever the individual guards think, and
            // this is the point at which we stop adding to it.
            String message;
            message.Format(_T("Sieve vacation: no auto-reply for %s, the message has already passed through %d ")
                           _T("generated messages (the rule loop limit)."),
                           String(account->GetAddress()).c_str(), decision.loopCount);
            LOG_APPLICATION(message);
            return;
         }

         // RFC 5230 4.4: the ":handle" the script gave, or one derived from what the
         // response actually says. Deriving it means that editing the reason text
         // starts a fresh suppression window, which is what a user who has just
         // corrected their holiday dates expects.
         String handle = decision.handle;
         if (handle.IsEmpty())
            handle = SieveVacationTracker::DeriveHandle(decision.reason, decision.subject, decision.from, decision.mime);

         __int64 window = SuppressionWindowSeconds_(decision);

         // Claim before composing, not after sending. If this process dies between
         // the claim and the send, the cost is one auto-reply that never went out.
         // The other order costs an auto-reply that went out and was not recorded,
         // and therefore goes out again for the next message from the same sender,
         // and the one after that.
         if (!SieveVacationTracker::Instance()->ClaimResponse(account->GetAddress(), recipient, handle, window))
         {
            LOG_DEBUG(_T("Sieve vacation: no auto-reply to ") + recipient +
                      _T(", the response-tracking store did not permit one."));
            return;
         }

         String fromAddress = ResolveFromAddress_(account, decision.from);

         if (ComposeAndSubmit_(account, decision, fromAddress))
         {
            String message;
            message.Format(_T("Sieve vacation: an auto-reply for %s was queued to %s."),
                           String(account->GetAddress()).c_str(), recipient.c_str());
            LOG_DEBUG(message);
         }
      }
      catch (...)
      {
         // An exception here must not propagate into the delivery path: the message
         // the script was filtering has already been accepted, and failing its
         // delivery because the auto-reply could not be built would turn a cosmetic
         // problem into lost mail.
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5899, "SieveVacationResponder::Respond",
            "An error occurred while sending a Sieve vacation auto-reply. The message being delivered was not affected.");
      }
   }
}
