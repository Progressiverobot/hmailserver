// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class FetchAccount;
   class FetchAccountUIDs;
   enum PersistenceMode;

   class PersistentFetchAccount
   {
   public:
      PersistentFetchAccount(void);
      ~PersistentFetchAccount(void);

      static void Lock(__int64 ID);
      static void Unlock(__int64 ID);
      static void UnlockAll();
      static bool IsLocked(__int64 ID);
      static bool ReadObject(std::shared_ptr<FetchAccount> pFA, const SQLCommand& command);
      static bool ReadObject(std::shared_ptr<FetchAccount> oFA, std::shared_ptr<DALRecordset> pRS);
      static bool SaveObject(std::shared_ptr<FetchAccount> oFA, String &errorMessage,PersistenceMode mode);
      static bool SaveObject(std::shared_ptr<FetchAccount> oFA);
      static bool DeleteObject(std::shared_ptr<FetchAccount> pFA);
      static void DeleteByAccountID(__int64 ID);

      static void SetRetryNow(__int64 iFAID);
      static void SetNextTryTime(std::shared_ptr<FetchAccount> pFA);
   };
}