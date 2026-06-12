// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class Message;

   class DKIMSigner
   {
   public:
      DKIMSigner();

      void Sign(std::shared_ptr<Message> message);
   };

}