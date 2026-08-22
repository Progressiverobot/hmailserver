// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "../Common/Util/Time.h"

namespace HM
{
   /*
      One failed recipient, in the terms a machine can act on.

      Until this existed a delivery failure was a paragraph of English pushed
      into a std::vector<String>, and by the time the bounce was assembled there
      was nothing left in it to say WHICH recipient had failed, whether the
      failure was final, or why. So the notification this server sent could not
      be parsed by anything: a list manager could not tell a dead address from a
      full mailbox, and monitoring could not classify a failure at all.

      The prose is still here and its wording is unchanged - it is what a person
      reads, and for the surrounding template it comes from a translatable server
      message. What is added is the RFC 3464 record beside it.

      The constructor takes the recipient, the RFC 3463 enhanced status code and
      the prose TOGETHER, and there is deliberately no default constructor and no
      setter for any of the three. A failure site knows all three at the moment
      it knows any of them, and a type that could be constructed without a status
      code would eventually be constructed without one - which is how every
      recipient ends up bounced with "5.0.0" and the whole point is lost. A
      failure site that forgets is a compile error, not a wrong status code.

      The two optional fields have setters, because "not known here" is a real
      and correct answer for them. RFC 3464 2.3: an omitted optional field says
      nothing; a filled-in one asserts something. Diagnostic-Code is only ever
      set from what a remote server actually replied, and Remote-MTA only from a
      host this server actually spoke to.
   */
   class DeliveryFailure
   {
   public:
      DeliveryFailure(const String &recipient, const String &enhancedStatusCode, const String &humanReadableText) :
         recipient_(recipient),
         enhanced_status_code_(enhancedStatusCode),
         human_readable_text_(humanReadableText),
         // Stamped here rather than where the report is written, because this is
         // the moment the attempt ended. The two are within the same delivery
         // pass, but only one of them is the thing Last-Attempt-Date names.
         last_attempt_date_(Time::GetCurrentMimeDate())
      {
      }

      const String &GetRecipient() const { return recipient_; }
      const String &GetEnhancedStatusCode() const { return enhanced_status_code_; }
      const String &GetText() const { return human_readable_text_; }
      const String &GetLastAttemptDate() const { return last_attempt_date_; }

      // What the remote server said, verbatim, including its three-digit reply
      // code. Empty unless a remote server really did answer - our own reasons
      // for giving up are not attributed to it.
      const String &GetRemoteSmtpReply() const { return remote_smtp_reply_; }
      void SetRemoteSmtpReply(const String &value) { remote_smtp_reply_ = value; }

      // The host this server was talking to when the failure happened.
      const String &GetRemoteMta() const { return remote_mta_; }
      void SetRemoteMta(const String &value) { remote_mta_ = value; }

      //------------------------------------------------------------------------
      // Turning an SMTP reply into an RFC 3463 enhanced status code.
      //------------------------------------------------------------------------

      // The remote server's own enhanced code, if it sent one.
      //
      // RFC 3463 2: a server that supports enhanced codes puts one at the start
      // of the reply text, and its first digit agrees with the first digit of
      // the three-digit reply code. Both tests are needed - "550 5 users were
      // unknown" is prose that happens to begin with a digit, and a reply of
      // "250 4.0.0 ..." is not an enhanced code for a failure.
      //
      // Returns an empty string when the reply carries none, which is not a
      // failure: most servers still do not send them.
      static String ExtractRemoteEnhancedStatus(int replyCode, const AnsiString &reply)
      {
         if (replyCode < 200 || replyCode > 599)
            return _T("");

         // A multi-line reply repeats the enhanced code on every line; the last
         // line carries the verdict, so that is the one read.
         AnsiString lastLine = reply;
         lastLine.TrimRight("\r\n");

         int lastBreak = lastLine.ReverseFind("\r\n");
         if (lastBreak >= 0)
            lastLine = lastLine.Mid(lastBreak + 2);

         lastLine.TrimLeft();

         // "550 5.1.1 ..." - three digits, one separator, then the text.
         if (lastLine.GetLength() < 6)
            return _T("");

         if (lastLine.GetAt(3) != ' ' && lastLine.GetAt(3) != '-')
            return _T("");

         AnsiString text = lastLine.Mid(4);
         text.TrimLeft();

         int space = text.Find(' ');
         AnsiString token = space < 0 ? text : text.Mid(0, space);

         if (!IsEnhancedStatusCode_(token))
            return _T("");

         // The class digit must agree with the reply. A server that answers
         // "550 4.2.2 ..." is contradicting itself, and the three-digit code is
         // the one the protocol acted on.
         if (token.GetAt(0) != (char)('0' + (replyCode / 100)))
            return _T("");

         return String(token);
      }

      // What the three-digit reply code alone is worth.
      //
      // Only at the RCPT TO stage does RFC 5321 4.2.3 give these codes a
      // recipient-specific meaning, and that meaning is what the enhanced codes
      // below restate. Anywhere else in the session the same digits mean
      // something about the session rather than about this address, so the
      // honest answer there is the undefined code for the class - the remote's
      // own text travels in Diagnostic-Code, where the reader can see it.
      static String EnhancedStatusFromReplyCodeOnly(int replyCode, bool atRecipientStage)
      {
         const bool permanent = replyCode >= 500 && replyCode <= 599;
         const bool transient = replyCode >= 400 && replyCode <= 499;

         if (!permanent && !transient)
            return _T("");

         if (atRecipientStage)
         {
            switch (replyCode)
            {
            // RFC 5321 4.2.3 "mailbox unavailable" -> RFC 3463 X.2.0, other or
            // undefined mailbox status. Not X.2.1 (disabled) or X.2.2 (full):
            // the reply does not say which.
            case 450: return _T("4.2.0");
            // "local error in processing" at the far end -> X.3.0.
            case 451: return _T("4.3.0");
            // "insufficient system storage" for one recipient -> mailbox full.
            case 452: return _T("4.2.2");
            // "mailbox unavailable", permanently -> bad destination mailbox.
            case 550: return _T("5.1.1");
            // "user not local" -> RFC 3463 X.1.6, mailbox moved.
            case 551: return _T("5.1.6");
            // "exceeded storage allocation" -> mailbox full.
            case 552: return _T("5.2.2");
            // "mailbox name not allowed" -> bad mailbox address syntax.
            case 553: return _T("5.1.3");
            }
         }

         return permanent ? _T("5.0.0") : _T("4.0.0");
      }

      // The remote's own code when it sent one, otherwise the best the reply
      // code alone supports.
      static String EnhancedStatusFromSmtpReply(int replyCode, const AnsiString &reply, bool atRecipientStage)
      {
         String remote = ExtractRemoteEnhancedStatus(replyCode, reply);

         if (!remote.IsEmpty())
            return remote;

         return EnhancedStatusFromReplyCodeOnly(replyCode, atRecipientStage);
      }

   private:

      // class "." subject "." detail, RFC 3463 2, with the class restricted to
      // the three values that RFC defines (2 success, 4 persistent transient,
      // 5 permanent) and each of the other two at most three digits.
      static bool IsEnhancedStatusCode_(const AnsiString &token)
      {
         if (token.GetLength() < 5)
            return false;

         const char classDigit = token.GetAt(0);
         if (classDigit != '2' && classDigit != '4' && classDigit != '5')
            return false;

         if (token.GetAt(1) != '.')
            return false;

         int position = 2;
         int digits = 0;
         while (position < token.GetLength() && token.GetAt(position) >= '0' && token.GetAt(position) <= '9')
         {
            position++;
            digits++;
         }

         if (digits == 0 || digits > 3)
            return false;

         if (position >= token.GetLength() || token.GetAt(position) != '.')
            return false;

         position++;
         digits = 0;
         while (position < token.GetLength() && token.GetAt(position) >= '0' && token.GetAt(position) <= '9')
         {
            position++;
            digits++;
         }

         if (digits == 0 || digits > 3)
            return false;

         // Nothing may follow. A token like "5.1.1:" is not a status code, and
         // reporting it as one produces a DSN no parser will accept.
         return position == token.GetLength();
      }

      String recipient_;
      String enhanced_status_code_;
      String human_readable_text_;
      String last_attempt_date_;

      String remote_smtp_reply_;
      String remote_mta_;
   };
}
