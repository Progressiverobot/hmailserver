// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "IMAPCommandRangeAction.h"

namespace HM
{

   class IMAPStore : public IMAPCommandRangeAction
   {
   public:
	   IMAPStore();
	   virtual ~IMAPStore();

      IMAPResult DoAction(std::shared_ptr<IMAPConnection> pConnection, int messageIndex, std::shared_ptr<Message> pMessage, const std::shared_ptr<IMAPCommandArgument> pArgument);
      static String GetMessageFlags(std::shared_ptr<Message> pMessage, int messageIndex, bool includeModSeq);

      // RFC 7162 (CONDSTORE): "[MODIFIED <set>] " prefix listing the messages skipped because
      // their mod-sequence exceeded the UNCHANGEDSINCE value, or "" when none were skipped.
      virtual String GetConditionalStoreResponseCode();

   private:

      void ParseModifier_(const String &sCommand);

      bool modifier_parsed_ = false;
      bool has_unchangedsince_ = false;
      __int64 unchangedsince_ = 0;
      std::vector<unsigned int> modified_;
   };

}
