// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "NotificationClientSubscription.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   NotificationClientSubscription::NotificationClientSubscription(__int64 subscriptionKey, std::weak_ptr<NotificationClient> client) :
      subscription_key_(subscriptionKey),
      client_(client)
   {

   }

   NotificationClientSubscription::~NotificationClientSubscription()
   {

   }

   __int64 
   NotificationClientSubscription::GetSubscriptionKey()
   {
      return subscription_key_;
   }

   std::weak_ptr<NotificationClient> 
   NotificationClientSubscription::GetSubscribedClient()
   {
      return client_;
   }

}
