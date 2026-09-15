// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// The domain's legal footer, appended to mail leaving the organisation.
//
// It runs where the signature runs - SMTPConnection::DoPreAcceptMessageModifications_,
// against the same MessageData and committed by the same single write - because
// that is the one place on this server where a message's body is modified on
// its way out, and a second one would be a second thing to keep correct. That
// position also puts it before DKIMSigner::Sign, which runs at delivery, so
// this server's own signature covers the footer rather than being broken by it.

#pragma once

namespace HM
{
   class Domain;
   class Message;
   class MessageData;

   class DisclaimerAdder
   {
   public:

      // Appends the sender domain's disclaimer to the message. Returns true
      // when message_data was changed and must be written; the caller owns the
      // write, exactly as it does for SignatureAdder.
      //
      // submission_is_ours is the session's own answer to "did this message
      // come from us": authenticated, or sent by an address that is an account
      // on this server. A message relayed in from outside that merely claims a
      // hosted sender domain gets no footer - attaching a company's legal text
      // to a forgery is worse than attaching it to nothing.
      static bool Append(std::shared_ptr<Message> message,
                         std::shared_ptr<const Domain> sender_domain,
                         bool submission_is_ours,
                         std::shared_ptr<MessageData> &message_data);

      // Stamped on a message the footer was added to, so that a message this
      // server handles twice is not footed twice.
      static const char *AppliedHeader() { return "X-hMailServer-Disclaimer"; }

      // True when the message is signed or encrypted end to end. Appending to
      // one of these is not a degraded result, it is a broken message: a
      // multipart/signed whose first part changed fails verification in every
      // client that checks, and an encrypted part cannot be appended to at all
      // because the server cannot read it. So nothing is appended, the message
      // goes out exactly as the sender wrote it, and the reason is written to
      // the log where an administrator looking for their missing footer will
      // find it.
      static bool IsProtectedContentType(const String &contentType);

      // The reply-chain guard. True when the body already carries the footer -
      // which, on a reply, is the copy quoted from the message being replied
      // to. Compared with quote markers, tags and white space removed, because
      // that is what quoting does to it.
      static bool AlreadyCarries(const String &body, const String &disclaimer, bool bodyIsHtml);
   };

   // Run from ClassTester::DoTests.
   class DisclaimerAdderTester
   {
   public:
      void Test();
   };
}
