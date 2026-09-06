// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "../../Threading/Task.h"

namespace HM
{
   class IMAPFolder;
   class Message;
   class Account;

   // SpamAssassinLearnOnMove: a message a user moves into their Junk folder is
   // spam, and one they move out of it is ham, and spamd is told so (the spamc
   // TELL command, "Set: local", the same Bayes store its verdicts come from).
   // This is the feedback loop every other mail system has and this one did not:
   // until now the only way to teach spamd was sa-learn on the server's console.
   //
   // Off by default, like the other spamd extras: it sends a command an existing
   // spamd may be configured to refuse (--allow-tell is spamd's own switch), and an
   // operator turns it on knowing that.
   class SpamAssassinLearner
   {
   public:
      // Called after an IMAP COPY or MOVE has written the copy: decides whether it
      // was a move into or out of a \Junk folder of the destination owner's own
      // tree, and if so queues the lesson. Never blocks the IMAP command: the
      // conversation with spamd runs on the asynchronous work queue.
      static void LearnFromMove(std::shared_ptr<IMAPFolder> source, std::shared_ptr<IMAPFolder> destination,
                                std::shared_ptr<Message> copy, std::shared_ptr<const Account> destinationOwner);

      // The conversation itself, synchronous: connect, TELL, wait for the reply.
      // Returns whether spamd acknowledged (EX_OK). Public for the task below.
      static bool Tell(const String &messageFile, bool spam, const String &user);
   };

   class SpamAssassinLearnTask : public Task
   {
   public:
      SpamAssassinLearnTask(const String &messageFile, bool spam, const String &user);
      virtual void DoWork();

   private:
      String message_file_;
      bool spam_;
      String user_;
   };
}
