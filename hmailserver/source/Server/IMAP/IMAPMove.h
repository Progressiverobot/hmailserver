// Copyright (c) 2010 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "IMAPCommandRangeAction.h"

namespace HM
{
   // Implements the message-move action used by the MOVE and UID MOVE
   // commands (RFC 6851). Each message is copied to the target folder and
   // recorded so that it can be expunged from the source folder afterwards.
   class IMAPMove : public IMAPCommandRangeAction
   {
   public:
      IMAPMove();

      virtual IMAPResult DoAction(std::shared_ptr<IMAPConnection> pConnection, int messageIndex, std::shared_ptr<Message> pOldMessage, const std::shared_ptr<IMAPCommandArgument> pArgument);

      // MOVE is atomic (RFC 6851 3.3), so nothing moves unless every message exists.
      virtual MissingMessagePolicy GetMissingMessagePolicy() const { return MissingMessagePolicy::FailBeforeActing; }

      // Removes the successfully copied messages from the source folder and
      // sends the untagged EXPUNGE responses required by RFC 6851.
      void ExpungeMovedMessages(std::shared_ptr<IMAPConnection> pConnection);

   private:

      std::vector<unsigned int> moved_message_uids_;
   };
}
