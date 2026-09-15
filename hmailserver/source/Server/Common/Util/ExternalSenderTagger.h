// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// What a domain does to a message arriving from outside: the [EXTERNAL] tag
// and the first-contact note.
//
// It runs at LOCAL DELIVERY, against the recipient account's own copy of the
// message, for two reasons. The switch belongs to the RECIPIENT's domain, and
// that is not known until the message is split between its recipients; and a
// message with both a local and an external recipient must not have the tag
// relayed onward to the outside world, which is exactly what tagging the
// queued file would do.
//
// The place it runs is the one where a per-account rewrite of a delivered copy
// already happens - beside LocalDelivery::RemoveSpamClassification_, which
// rewrites the same file for the same kind of reason.

#pragma once

namespace HM
{
   class Account;
   class Domain;
   class Message;

   class ExternalSenderTagger
   {
   public:

      // Applies whatever the recipient's domain asks for to this account's copy
      // of the message, and returns true when the file was rewritten.
      //
      // Costs nothing at all when the domain asks for nothing, which is the
      // shipped state: the header block is not even read.
      static bool Apply(std::shared_ptr<const Account> account,
                        std::shared_ptr<const Domain> domain,
                        std::shared_ptr<Message> message,
                        const String &sendersIP);

      // Stamped on a message from outside. Prepending a field is
      // signature-safe: DKIM selects duplicate fields from the bottom up, so a
      // sender's signature over their own From or Subject is untouched by a
      // field appearing above it.
      static const char *ExternalHeader() { return "X-hMailServer-External"; }

      // Stamped when this account has had mail from other people but never from
      // this sender.
      static const char *FirstContactHeader() { return "X-hMailServer-First-Contact"; }

      // The shipped tag text, used when the domain has set none.
      static String DefaultTagText() { return _T("[EXTERNAL]"); }

      // The subject with the tag in front of it, or the subject unchanged when
      // it already begins with the tag - a reply to a tagged message, arriving
      // back from outside, must not collect a second one.
      static String TagSubject(const String &subject, const String &tagText);
   };

   // Run from ClassTester::DoTests.
   class ExternalSenderTaggerTester
   {
   public:
      void Test();
   };
}
