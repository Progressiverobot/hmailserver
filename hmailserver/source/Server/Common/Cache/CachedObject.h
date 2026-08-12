// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{

   template <typename T>
   struct CachedObject
   {
      CachedObject() {}

      CachedObject(std::shared_ptr<T> object) :
         object_(object),
         id_(object->GetID()),
         name_(object->GetName()),
         creation_time_(static_cast<int>(GetTickCount64() / 1000))
      {
         SetEstimatedSize();
      }

      int
      CachedObject::SecondsOld()
      {
         int iCurrentTime = static_cast<int>(GetTickCount64() / 1000);

         int iAge = iCurrentTime - creation_time_;

         // Only reachable for a default-constructed object, whose creation time
         // is indeterminate. Treat it as far too old to use.
         if (iAge < 0)
            iAge = 60 * 60 * 24;

         return iAge;
      }

      void
      CachedObject::SetEstimatedSize()
      {
         estimated_size_ = object_->GetEstimatedCachingSize();
      }

      size_t
      CachedObject::GetEstimatedSize()
      {
         return estimated_size_;
      }

      __int64 id_;
      std::wstring name_;

      // Whole seconds of uptime rather than milliseconds. This member doubles as
      // the key of the cache's timestamp-ordered index (Cache.h), so it has to be
      // an int that only ever increases; a millisecond tick held in an int changes
      // sign every 24.9 days, which would sort newly cached objects ahead of the
      // oldest ones and make eviction discard the wrong entries. Seconds keep an
      // int monotonic for longer than any machine stays up.
      int creation_time_;
      size_t estimated_size_;

      std::shared_ptr<T> object_;
   };

}
