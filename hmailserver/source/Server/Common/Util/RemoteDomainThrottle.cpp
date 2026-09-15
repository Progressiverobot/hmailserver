// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "RemoteDomainThrottle.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   RemoteDomainThrottle::RemoteDomainThrottle()
   {

   }

   RemoteDomainThrottle::~RemoteDomainThrottle()
   {

   }

   void
   RemoteDomainThrottle::Acquire(Slot &slot, const String &destination, int maxConnections)
   {
      // A slot object is used once. Releasing first means a caller that reuses
      // one cannot hold two counts on the same destination.
      slot.Release();

      if (maxConnections <= 0 || destination.IsEmpty())
      {
         // No ceiling. The empty key is what makes Acquired() answer true
         // without anything having been counted.
         slot.key_ = _T("");
         slot.acquired_ = false;
         return;
      }

      String key = destination;
      key.ToLower();

      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      auto existing = in_use_.find(key);
      int current = existing == in_use_.end() ? 0 : existing->second;

      if (current >= maxConnections)
      {
         // At the ceiling. The key is left set so Acquired() answers false -
         // an empty key means "no ceiling applies", which is the opposite.
         slot.key_ = key;
         slot.acquired_ = false;
         return;
      }

      in_use_[key] = current + 1;

      slot.key_ = key;
      slot.acquired_ = true;
   }

   int
   RemoteDomainThrottle::GetInUse(const String &destination) const
   {
      String key = destination;
      key.ToLower();

      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      auto existing = in_use_.find(key);
      return existing == in_use_.end() ? 0 : existing->second;
   }

   void
   RemoteDomainThrottle::Clear()
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);
      in_use_.clear();
   }

   void
   RemoteDomainThrottle::Release_(const String &key)
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      auto existing = in_use_.find(key);
      if (existing == in_use_.end())
         return;

      if (existing->second <= 1)
      {
         // The last one out removes the entry rather than leaving a zero, so the
         // map holds one entry per destination currently being delivered to and
         // not one per destination ever delivered to.
         in_use_.erase(existing);
         return;
      }

      existing->second = existing->second - 1;
   }

   void
   RemoteDomainThrottle::Slot::Release()
   {
      if (!acquired_)
      {
         key_ = _T("");
         return;
      }

      acquired_ = false;

      String key = key_;
      key_ = _T("");

      RemoteDomainThrottle::Instance()->Release_(key);
   }
}
