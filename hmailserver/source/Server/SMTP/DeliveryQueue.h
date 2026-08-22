// Copyright (c) 2005 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Created 2005-08-02
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "..\Common\Threading\Task.h"

namespace HM
{
   class DeliveryQueueClearer : public Task
   {
   public:
      DeliveryQueueClearer(void);
      ~DeliveryQueueClearer(void);

      virtual void DoWork();

   private:

   };

   class DeliveryQueue
   {
   public:
      DeliveryQueue(void);
      ~DeliveryQueue(void);

      static void Clear();
      static void ResetDeliveryTime(__int64 iMessageID);
      static void Remove(__int64 iMessageID);
      static void StartDelivery();

      static void OnDeliveryQueueCleared();
   private:

      static bool is_clearing_;
   };
}