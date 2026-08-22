// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class GreyListTriplet;

   class PersistentGreyList
   {
   public:
      PersistentGreyList(void);
      ~PersistentGreyList(void);

      static std::shared_ptr<GreyListTriplet> GetRecord(const String &sSenderAddress, const String &sRecipientAddress, const IPAddress &remoteIP);
      static bool AddObject(std::shared_ptr<GreyListTriplet> pTriplet);

      static bool ResetDeletionTime(std::shared_ptr<GreyListTriplet> pTriplet);

      static void IncreaseBlocked(__int64 iTripletID);
      static void ClearExpiredRecords();
      static void ClearAllRecords();
   private:

   };
}