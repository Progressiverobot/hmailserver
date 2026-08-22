// Copyright (c) 2005 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Created 2005-07-21
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "..\Util\Singleton.h"

namespace HM
{
   class Message;

   class MessageCache : public Singleton<MessageCache>
   {
   public:
      MessageCache(void);
      ~MessageCache(void);

      void AddMessage(std::shared_ptr<Message> pMessage);
      std::shared_ptr<Message> GetMessage(__int64 iMessageID);


   private:

      boost::recursive_mutex mutex_;
      std::map<__int64, std::shared_ptr<Message> > message_;

   };
}