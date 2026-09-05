// Copyright (c) 2010 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "IMAPCommand.h"
#include "IMAPFolderView.h"

namespace HM
{
   // What to do when a message set names a message another session has expunged and
   // this one has not been told about yet.
   enum class MissingMessagePolicy
   {
      // Skip it. Required for the UID variants (RFC 3501 6.4.8).
      Ignore,
      // Act on the messages which do exist, then fail the command (RFC 2180 4.1.3).
      ReportAfterActing,
      // Fail without acting on any of the messages.
      FailBeforeActing,
   };

   class IMAPCommandRangeAction : public IMAPCommand
   {
   public:
      IMAPCommandRangeAction();
      virtual ~IMAPCommandRangeAction();

      void SetIsUID(bool bIsUID);

      IMAPResult ExecuteCommand(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument) {return IMAPResult();}
      IMAPResult DoForMails(std::shared_ptr<IMAPConnection> pConnection, const String &sMailNos, const std::shared_ptr<IMAPCommandArgument> pArgument);

      // RFC 4315 (UIDPLUS): the "[COPYUID <validity> <src-set> <dst-set>] " response-code
      // prefix accumulated during COPY/MOVE, or an empty string when not applicable.
      String GetUIDPlusResponseCode();

      // RFC 7162 (CONDSTORE): the "[MODIFIED <set>] " response-code prefix produced by a
      // conditional STORE (UNCHANGEDSINCE), or an empty string when not applicable.
      virtual String GetConditionalStoreResponseCode() { return _T(""); }

   protected:

      bool GetIsUID();
      virtual IMAPResult DoAction(std::shared_ptr<IMAPConnection> pConnection, int messageIndex, std::shared_ptr<Message> pMessage, const std::shared_ptr<IMAPCommandArgument> pArgument) = 0;

      // Override and return true when DoAction updates the message. Such commands are
      // given the objects the folder's collection holds rather than copies of them.
      virtual bool UsesLiveMessages() const { return false; }

      virtual MissingMessagePolicy GetMissingMessagePolicy() const { return MissingMessagePolicy::Ignore; }

      // Records a source->destination UID mapping for the UIDPLUS COPYUID response.
      void RecordCopyUid(unsigned int sourceUid, unsigned int destUid, unsigned int destUidValidity);

   private:

      // Translates a message set into this session's messages, as (sequence number,
      // entry) pairs, against the session's own view of the folder.
      std::vector<std::pair<int, IMAPViewEntry> > ResolveTargets_(std::shared_ptr<IMAPFolderView> view, const String &sMailNos);

      String JoinUids_(const std::vector<unsigned int> &uids);

      bool is_uid_;

      std::vector<unsigned int> uidplus_source_uids_;
      std::vector<unsigned int> uidplus_dest_uids_;
      unsigned int uidplus_dest_uidvalidity_;

   };

}
