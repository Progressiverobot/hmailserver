// Copyright (c) 2005 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "..\Common\Threading\Task.h"

namespace HM
{
   class Message;

   class DeliveryTask : public Task
   {
   public:
      DeliveryTask(std::shared_ptr<Message> pMessage);
      ~DeliveryTask(void);

      virtual void DoWork();

   private:
      
      std::shared_ptr<Message> message_;
   };
}