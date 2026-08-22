// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "../BO/ScheduledTask.h"

namespace HM
{
   /*
      Caps the QRESYNC expunge-record table, hm_imapexpunged.

      One row is written there for every message ever expunged from an IMAP folder -
      folder, UID and the mod-sequence the expunge was given - so that a client
      reconnecting after a disconnect can be told which UIDs vanished while it was
      away. Until this task, the only thing that ever removed a row was deleting the
      whole folder, so on a busy server the table grows for the life of the
      installation and the SELECT that reads it gets slower forever.

      RFC 7162 section 5.3 anticipates exactly this:

         "Note that indefinitely storing information about expunged messages can
         cause storage and related problems for an implementation. In the worst case,
         this could result in almost 64 GB of storage for each IMAP mailbox. ...
         Hence, implementations are encouraged to adopt strategies to protect against
         such storage problems, such as limiting the size of the queue used to store
         mod-sequences for expunged messages and 'expiring' older records when this
         limit is reached."

      So the strategy here is the RFC's own: a per-mailbox cap,
      [Settings] IMAPExpungeRetentionRecords, with the oldest records going first.

      It is a record COUNT rather than an age because the table has no timestamp
      column - it holds nothing but folder, account, UID and mod-sequence - and
      adding one is a coordinated schema change. The count is not a poor substitute:
      it is the quantity the RFC talks about, it bounds the table directly, and a
      mod-sequence is what a resynchronising client actually asks in terms of.

      What makes the pruning safe rather than silent data loss is the other half of
      this change, in PersistentIMAPFolder::RemembersExpungesSince and its three
      callers. RFC 7162 section 3.2.6 requires a server that has forgotten the
      records covering a client's mod-sequence to report every UID in the requested
      set that is no longer in the mailbox, instead of reporting the records it
      happens to have left. Without that rule implemented, pruning would tell a
      client that a message it can no longer see is still there. With it, the cost of
      a small cap is a longer VANISHED response for a client that has been away a
      long time, and nothing else.
   */
   class IMAPExpungeRetentionTask : public ScheduledTask
   {
   public:
      IMAPExpungeRetentionTask(void);
      ~IMAPExpungeRetentionTask(void);

      virtual void DoWork();
   };
}
