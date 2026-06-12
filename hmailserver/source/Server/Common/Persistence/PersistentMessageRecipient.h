// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class MessageRecipient;

   class PersistentMessageRecipient 
   {
   public:
      PersistentMessageRecipient(void);
      ~PersistentMessageRecipient(void);

      static bool DeleteObject(std::shared_ptr<MessageRecipient> pRecipient);
   };
}
