// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include <boost/multi_index_container.hpp>
#include <boost/multi_index/member.hpp>
#include <boost/multi_index/ordered_index.hpp>
#include <boost/multi_index/hashed_index.hpp>

using boost::multi_index_container;
using namespace boost::multi_index;

#include "../Util/VariantDateTime.h"

#include "CachedObject.h"

#pragma once

namespace HM
{

   struct id {};
   struct name {};
   struct timestamp {};

   template <typename T>
   class Cache : public Singleton<Cache<T>>
   {
   public:
      Cache();

      std::shared_ptr<T> GetObject(const String &sName);
      // Retrieves an object using the object name.

      std::shared_ptr<T> GetObject(__int64 iID);
      // Retrieves an object using the ID

      void RemoveObject(std::shared_ptr<T> pObject);
      void RemoveObject(const String &sName);
      void RemoveObject(__int64 iID);

      void SetTTL(int iNewVal);
      int GetHitRate();
      void SetEnabled(bool bEnabled);
      void Clear();

      void AdjustEstimatedSize(__int64 size_delta);

      void SetMaxSize(size_t max_size);
      size_t GetMaxSize();
      size_t GetSize();

      void Add(std::shared_ptr<T> pObject);

   private:

      void ResetEstimatedSizeIfEmpty_();

      // Every removal decrements by the object's LIVE size
      // (GetEstimatedCachingSize()), never the snapshot CachedObject took at
      // construction. The two can differ by the whole object: the message cache
      // inserts a CachedMessages BEFORE its first database refresh, so its snapshot
      // is permanently zero and everything it later grows by arrives through
      // AdjustEstimatedSize. Decrementing the stale snapshot on removal - or, on
      // the TTL-expiry path, decrementing nothing at all - left those grown bytes
      // in the total forever, a ratchet that only Clear() reset. A counter that
      // only ever grows eventually exceeds any cap, at which point every Add pays
      // the eviction walk for nothing.
      void DecreaseEstimatedSize_(size_t amount)
      {
         if (current_estimated_size_ >= amount)
            current_estimated_size_ -= amount;
         else
            current_estimated_size_ = 0;
      }

      template<typename Tag, typename MultiIndexContainer, typename TagValue>
      std::shared_ptr<T> GetItemBy_(const MultiIndexContainer& s, TagValue value)
      {
         typedef index<container_type, Tag>::type items_by_tag;
         items_by_tag& items = get<Tag>(objects_);
         auto item = items.find(value);

         if (item != items.end())
         {
            CachedObject<T> cached_object = (*item);
            if (GetObjectIsWithinTTL_(cached_object))
            {
               return cached_object.object_;
            }

            DecreaseEstimatedSize_(cached_object.object_->GetEstimatedCachingSize());
            items.erase(item);
            ResetEstimatedSizeIfEmpty_();
         }

         std::shared_ptr<T> empty;
         return empty;
      }

      template<typename Tag, typename MultiIndexContainer, typename TagValue>
      void RemoveBy_(const MultiIndexContainer& s, TagValue value)
      {
         typedef index<container_type, Tag>::type items_by_tag;
         items_by_tag& items = get<Tag>(objects_);

         auto item_iter = items.find(value);

         if (item_iter != items.end())
         {
            auto item = (*item_iter);

            DecreaseEstimatedSize_(item.object_->GetEstimatedCachingSize());

            items.erase(item_iter);
         }

         ResetEstimatedSizeIfEmpty_();

      }
      
      bool GetObjectIsWithinTTL_(CachedObject<T> pObject);
      

      boost::recursive_mutex _mutex;
      
      typedef multi_index_container<
         CachedObject<typename T>,
         indexed_by<
            hashed_unique<
            tag<id>, BOOST_MULTI_INDEX_MEMBER(CachedObject<typename T>, __int64, id_)>,
            hashed_non_unique<
            tag<name>, BOOST_MULTI_INDEX_MEMBER(CachedObject<typename T>, std::wstring, name_)>,
            ordered_non_unique<
            tag<timestamp>, BOOST_MULTI_INDEX_MEMBER(CachedObject<typename T>, int, creation_time_)> >
      > container_type;

      container_type objects_;

      int no_of_misses_;
      int no_of_hits_;
      int ttl_;
      bool enabled_;
      size_t max_size_;
      size_t current_estimated_size_;
   };

   template <class T> 
   Cache<T>::Cache() :
      max_size_(0),
      current_estimated_size_(0),
      no_of_misses_(0),
      no_of_hits_(0),
      ttl_(0),
      enabled_(false)
   {
      
      
   }

   template <class T> 
   void
   Cache<T>::Clear()
   {
      boost::lock_guard<boost::recursive_mutex> guard(_mutex);

      objects_.clear();
      no_of_misses_ = 0;
      no_of_hits_ = 0;

      // The accumulated size belongs to the objects that were just discarded.
      current_estimated_size_ = 0;
   }

   template <class T>
   void
   Cache<T>::SetTTL(int iNewVal)
   {
      // These two setters were the only members of this class that touched shared
      // state without taking _mutex, while every reader takes it. Both are reached
      // from the COM API when an administrator changes a cache setting, so they run
      // on an administration thread while delivery and protocol threads are reading
      // the same fields.
      //
      // The mutex is recursive, so SetEnabled below can hold it across its call to
      // Clear(), which takes it again.
      boost::lock_guard<boost::recursive_mutex> guard(_mutex);

      ttl_ = iNewVal;

      no_of_misses_ = 0;
      no_of_hits_ = 0;
   }


   template <class T>
   void
   Cache<T>::SetEnabled(bool bEnabled)
   {
      boost::lock_guard<boost::recursive_mutex> guard(_mutex);

      enabled_ = bEnabled;

      if (!enabled_)
         Clear();
   }

   template <class T>
   void
   Cache<T>::SetMaxSize(size_t max_size)
   {
      // Reached from the COM API when an administrator changes the cache size, so it
      // runs on an administration thread against a value protocol threads read under
      // the lock. The last of the public mutators that did not take it.
      boost::lock_guard<boost::recursive_mutex> guard(_mutex);

      max_size_ = max_size;
   }

   template <class T>
   size_t
   Cache<T>::GetMaxSize()
   {
      return max_size_;
   }


   template <class T>
   size_t
   Cache<T>::GetSize()
   {
      return current_estimated_size_;
   }


   template <class T> 
   int
   Cache<T>::GetHitRate()
   {
      boost::lock_guard<boost::recursive_mutex> guard(_mutex);

      if (no_of_hits_ == 0)
         return 0;

      int iHitRate = (int) (((float) no_of_hits_ / (float) (no_of_hits_ + no_of_misses_)) * 100);

      return iHitRate;
   }

   template <class T> 
   void 
   Cache<T>::RemoveObject(std::shared_ptr<T> pObject)
   {
      boost::lock_guard<boost::recursive_mutex> guard(_mutex);

      RemoveBy_<name>(objects_, pObject->GetName());
   }

   template <class T> 
   void 
   Cache<T>::RemoveObject(const String &sName)
   {
      boost::lock_guard<boost::recursive_mutex> guard(_mutex);

      RemoveBy_<name>(objects_, sName);


   }

   template <class T> 
   void 
   Cache<T>::RemoveObject(__int64 iID)
   {
      boost::lock_guard<boost::recursive_mutex> guard(_mutex);

      RemoveBy_<id>(objects_, iID);
   }

   template <class T> 
   std::shared_ptr<T> 
   Cache<T>::GetObject(const String &sName)
   {
      boost::lock_guard<boost::recursive_mutex> guard(_mutex);

      if (enabled_)
      {
         return GetItemBy_<name>(objects_, sName);
      }
      return nullptr;
   }

   template <class T> 
   std::shared_ptr<T> 
   Cache<T>::GetObject(__int64 iID)
   {
      boost::lock_guard<boost::recursive_mutex> guard(_mutex);

      if (enabled_)
      {
         return GetItemBy_<id>(objects_, iID);
      }

      return nullptr;
   }

   template <class T> 
   void 
   Cache<T>::Add(std::shared_ptr<T> pObject)
   {
      boost::lock_guard<boost::recursive_mutex> guard(_mutex);

      if (!enabled_)
         return;

      // Object must be saved before it can be cached.
#ifdef DEBUG
      if (pObject->GetID() == 0)
      {
         assert(0);
      }
#endif

      typedef index<container_type, timestamp>::type items_by_timestamp;
      items_by_timestamp& items = get<timestamp>(objects_);

      CachedObject<T> object(pObject);

      if (max_size_ > 0 && current_estimated_size_ + object.GetEstimatedSize() > max_size_)
      {
         // We've reached the cache max size. Remove items until we're 10% free.
         size_t target_size = (size_t) (max_size_ * 0.9);

         while (current_estimated_size_ > target_size)
         {
            auto item_iter = items.begin();
            if (item_iter == items.end())
               break;

            auto item = (*item_iter);

            // Evicted by LIVE size. The previous version read the construction-time
            // snapshot and BROKE on a zero - which, in the message cache, is every
            // entry (they are inserted before their first refresh), so the 512 MB
            // cap never evicted anything at all. With live sizes a zero means an
            // actually-empty entry (an empty folder, say): it is erased and the walk
            // continues, because letting it sit at the front of the timestamp index
            // would block eviction of everything younger for as long as it lives.
            // The walk still terminates - each pass erases exactly one entry - and
            // it can no longer purge a healthy cache by mistake, because when the
            // accounting is truthful, a total above the target implies entries with
            // real size ahead.
            DecreaseEstimatedSize_(item.object_->GetEstimatedCachingSize());

            items.erase(item_iter);
         }

         ResetEstimatedSizeIfEmpty_();
      }

      no_of_misses_++;

      // Counted only when the insert actually happened: the id index is
      // hashed_unique, so a racing second Add of the same object is a silent no-op
      // insert - and counting its bytes anyway leaks phantom size into the total.
      if (items.insert(object).second)
         current_estimated_size_ += object.GetEstimatedSize();
   }

   template <class T> 
   bool 
   Cache<T>::GetObjectIsWithinTTL_(CachedObject<T> pObject)
   {
      if (pObject.SecondsOld() < ttl_)
      {
         // A fresh object was found in the cache.
         no_of_hits_++;
         return true;
      }

      return false;
   }


   template <class T>
   void
   Cache<T>::ResetEstimatedSizeIfEmpty_()
   {
      if (objects_.size() == 0)
      {
         current_estimated_size_ = 0;
      }

   }

   // Deterministic tests for the size accounting. Wired into ClassTester::DoTests,
   // reachable via hMailServer.exe /Test. They pin the invariant the eviction cap
   // depends on: the counter equals the sum of the entries' LIVE sizes, through
   // grow-after-insert, removal, TTL expiry, duplicate Add and eviction.
   class CacheAccountingTester
   {
   private:
      // The shape of CachedMessages, reduced: inserted at one size (zero, like an
      // unrefreshed folder), grown afterwards, with the growth reported through
      // AdjustEstimatedSize.
      class Dummy
      {
      public:
         Dummy(__int64 id, const String &name, size_t size) : id_(id), name_(name), size_(size) {}
         __int64 GetID() { return id_; }
         String GetName() { return name_; }
         size_t GetEstimatedCachingSize() { return size_; }
         void SetEstimatedCachingSize(size_t size) { size_ = size; }
      private:
         __int64 id_;
         String name_;
         size_t size_;
      };

   public:
      void Test()
      {
         // Grow-after-insert, then removal: the counter must come back to zero.
         // With the old snapshot-based removal it stayed at 1000 forever - the
         // ratchet that eventually put every cache permanently "over" its cap.
         {
            Cache<Dummy> cache;
            cache.SetEnabled(true);
            cache.SetTTL(3600);

            auto grown = std::make_shared<Dummy>(1, _T("grown"), 0);
            cache.Add(grown);
            if (cache.GetSize() != 0)
               throw 0;

            grown->SetEstimatedCachingSize(1000);
            cache.AdjustEstimatedSize(1000);
            if (cache.GetSize() != 1000)
               throw 0;

            cache.RemoveObject((__int64) 1);
            if (cache.GetSize() != 0)
               throw 0;

            // The decrease guard: an oversized negative delta clamps at zero
            // rather than wrapping the unsigned total.
            cache.AdjustEstimatedSize(-500);
            if (cache.GetSize() != 0)
               throw 0;
         }

         // A duplicate Add of the same id is a silent no-op insert and must not
         // count its bytes twice.
         {
            Cache<Dummy> cache;
            cache.SetEnabled(true);
            cache.SetTTL(3600);

            auto once = std::make_shared<Dummy>(6, _T("once"), 100);
            cache.Add(once);
            cache.Add(once);
            if (cache.GetSize() != 100)
               throw 0;
         }

         // TTL expiry erases through the lookup path, which previously removed the
         // entry without touching the counter at all.
         {
            Cache<Dummy> cache;
            cache.SetEnabled(true);
            cache.SetTTL(0); // everything is already expired

            cache.Add(std::make_shared<Dummy>(7, _T("expired"), 200));
            if (cache.GetSize() != 200)
               throw 0;

            if (cache.GetObject((__int64) 7) != nullptr)
               throw 0;
            if (cache.GetSize() != 0)
               throw 0;
         }

         // Eviction: a zero-size entry at the front of the timestamp index is
         // erased and walked past - the old code stopped dead on it, which in the
         // message cache meant the cap never evicted anything - and eviction
         // proceeds oldest-first until the total is under target.
         {
            Cache<Dummy> cache;
            cache.SetEnabled(true);
            cache.SetTTL(3600);
            cache.SetMaxSize(3000);

            cache.Add(std::make_shared<Dummy>(2, _T("empty-folder"), 0));
            cache.Add(std::make_shared<Dummy>(3, _T("a"), 1500));
            cache.Add(std::make_shared<Dummy>(4, _T("b"), 1500));
            if (cache.GetSize() != 3000)
               throw 0;

            // Pushes past the cap: the zero-size entry and then the oldest real
            // one must go; the two younger entries stay.
            cache.Add(std::make_shared<Dummy>(5, _T("c"), 1500));

            if (cache.GetObject((__int64) 2) != nullptr)
               throw 0;
            if (cache.GetObject((__int64) 3) != nullptr)
               throw 0;
            if (cache.GetObject((__int64) 4) == nullptr)
               throw 0;
            if (cache.GetObject((__int64) 5) == nullptr)
               throw 0;
            if (cache.GetSize() != 3000)
               throw 0;
         }
      }
   };

   template <class T>
   void
   Cache<T>::AdjustEstimatedSize(__int64 size_delta)
   {
      // Public, and called from outside this class - MessagesContainer adjusts the
      // message cache from the IMAP path - so it runs on protocol threads while
      // other protocol threads are reading and writing the same total under the
      // lock.
      //
      // A signed delta rather than the previous (bool increase, size_t change)
      // pair, whose one caller computed direction and magnitude in a mirrored
      // if/else - the shape that produced the inverted-branch bug this function
      // has already carried once. The caller now subtracts two sizes and passes
      // the result; the sign logic lives in exactly one place.
      boost::lock_guard<boost::recursive_mutex> guard(_mutex);

      if (size_delta >= 0)
      {
         current_estimated_size_ += (size_t) size_delta;
      }
      else
      {
         DecreaseEstimatedSize_((size_t) (-size_delta));
      }
   }
}
