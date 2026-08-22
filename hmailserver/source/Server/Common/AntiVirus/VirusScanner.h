// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "VirusScanningResult.h"

namespace HM
{
   class Message;

   class VirusScanner  
   {
   public:
   
      // Guard for the MaxRunningScanners cap. It tries to take a scanner slot when
      // it is constructed and gives the slot back when it is destroyed - but only
      // if it actually got one.
      //
      // Owning the "I hold a slot" state here is the whole point. Previously the
      // destructor decremented the counter unconditionally, while the give-up
      // paths in WaitForFreeScanner_ returned without having incremented it. Every
      // give-up therefore drove the counter one lower until it went negative, and
      // once it was negative the cap stopped limiting anything at all - which is
      // exactly the protection that keeps a slow scanner from fanning out across
      // the whole thread pool.
      class VirusScannerAutoCount
      {
      public:
         VirusScannerAutoCount();
         ~VirusScannerAutoCount();

         // True if this object holds one of the MaxRunningScanners slots. False
         // means we gave up waiting and the scan is running over the cap.
         bool GetHoldsSlot() const;

      private:

         // Not copyable - a copy would give back a slot it never took.
         VirusScannerAutoCount(const VirusScannerAutoCount &);
         VirusScannerAutoCount & operator=(const VirusScannerAutoCount &);

         bool holds_slot_;
      };

      enum Constants
      {
         MaxRunningScanners = 10
      };


      static bool GetVirusScanningEnabled();

      // Returns true when a virus was found. scannerFailed is set when an enabled
      // scanner could not complete - it could not be launched, the socket to it
      // failed, it answered nothing usable - which is the difference between "this
      // message is clean" and "nobody looked". Callers that only care about the
      // verdict may pass nullptr, but the delivery path must not: for years a
      // scanner outage was indistinguishable from a clean result, so mail went out
      // unexamined and the only trace was one HM5406 in the error log.
      //
      // Deliberately NOT set for the two partial-scan cases the function reports
      // itself (HM6001, a message that could not be parsed for the per-attachment
      // pass, and HM6002, one attachment that could not be written out): the whole
      // message file was still scanned in both, and treating a transient file lock
      // as a scanner outage would hold ordinary mail. Nor for the max-size skip or
      // the scanner-slot give-up, which are policy and a wait respectively, not
      // failures.
      static bool Scan(std::shared_ptr<Message> pMessage, String &virusName, bool *scannerFailed = nullptr);
      static void BlockAttachments(std::shared_ptr<Message> message);

      static void ResetCounter();

   private:

      static long running_scanners_;

      // Only VirusScannerAutoCount may take and give back slots, so that the
      // increment and the decrement can never get out of step again.
      static bool WaitForFreeScanner_();
      static void DecreaseCounter();
      static void ReportVirusFound(std::shared_ptr<Message> pMessage);
      // scannerError is set when any enabled scanner reported an error for this
      // file, whether or not a later scanner then answered.
      static VirusScanningResult ScanFile_(const String &fileName, bool &scannerError);

      static void ReportScanningError_(const VirusScanningResult &scanningResult);
   };

}
