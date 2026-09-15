// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// How many connections this server will hold open to one remote at once.
//
// A counting semaphore per destination, and deliberately a NON-BLOCKING one: a
// delivery thread that waited for a slot would be a delivery thread not
// delivering anybody else's mail, and a handful of slow destinations with a
// ceiling of one would stall the whole queue exactly as a slow host does. So the
// caller that cannot get a slot defers its message instead - the queue is the
// waiting room this server already has, and a deferral is a thing an
// administrator can see in the delivery log.
//
// This is not the same control as RateLimiter's per-minute window, which is
// about how MANY messages go out; this is about how many sessions are open at
// the same moment, which is what a remote's own connection limit counts and
// what gets an IP address temporarily refused by a large provider.
//
// The counts live in memory and are per process: there is one delivery queue in
// one process, so there is nothing to share. A restart forgets them, which is
// correct - a restart also closed every connection they were counting.

#pragma once

#include "Singleton.h"

#include <map>

namespace HM
{
   class RemoteDomainThrottle : public Singleton<RemoteDomainThrottle>
   {
   public:
      RemoteDomainThrottle();
      virtual ~RemoteDomainThrottle();

      // Holds one slot for as long as it lives. Movable-free on purpose: the one
      // caller declares it on the stack around the connection attempts and lets
      // scope end it, so no path - an early return, a throw out of the SMTP
      // client, a host that could not be resolved - can leak a slot and leave a
      // destination permanently at its ceiling.
      class Slot
      {
      public:
         Slot() : acquired_(false) {}
         ~Slot() { Release(); }

         // True when a slot was taken, or when no ceiling applies. False means
         // the ceiling is reached and the caller must not connect.
         bool Acquired() const { return acquired_ || key_.IsEmpty(); }

         void Release();

      private:
         friend class RemoteDomainThrottle;

         Slot(const Slot &);
         Slot &operator=(const Slot &);

         String key_;
         bool acquired_;
      };

      // Takes a slot for the named destination. maxConnections of 0 or less means
      // no ceiling: nothing is counted and the slot reports itself acquired, so
      // the caller needs no separate "is it configured" test.
      void Acquire(Slot &slot, const String &destination, int maxConnections);

      // What is open to a destination right now. For the self-tests and for a
      // diagnostic; delivery does not read it.
      int GetInUse(const String &destination) const;

      // Forgets every count. For the self-tests only - calling it while
      // deliveries are running would let every destination past its ceiling
      // until the open sessions closed.
      void Clear();

   private:

      void Release_(const String &key);

      mutable boost::recursive_mutex mutex_;
      std::map<String, int> in_use_;
   };
}
