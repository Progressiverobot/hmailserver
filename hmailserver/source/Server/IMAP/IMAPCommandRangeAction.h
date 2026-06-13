// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "IMAPCommand.h"

namespace HM
{
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

      // Records a source->destination UID mapping for the UIDPLUS COPYUID response.
      void RecordCopyUid(unsigned int sourceUid, unsigned int destUid, unsigned int destUidValidity);

   private:

      String JoinUids_(const std::vector<unsigned int> &uids);

      bool is_uid_;

      std::vector<unsigned int> uidplus_source_uids_;
      std::vector<unsigned int> uidplus_dest_uids_;
      unsigned int uidplus_dest_uidvalidity_;
     
   };

}
