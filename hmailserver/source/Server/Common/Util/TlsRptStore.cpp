// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// See TlsRptStore.h.

#include "StdAfx.h"

#include "TlsRptStore.h"

#include <ctime>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   AnsiString
   TlsRptStore::GetCurrentDayKey()
   {
      time_t now = time(nullptr);

      tm utcTime = {};
      gmtime_s(&utcTime, &now);

      AnsiString key;
      key.Format("%04d-%02d-%02d", utcTime.tm_year + 1900, utcTime.tm_mon + 1, utcTime.tm_mday);
      return key;
   }

   TlsRptStore::DomainBucket&
   TlsRptStore::GetBucket_(const String &domain, const AnsiString &policyType, const AnsiString &policyString)
   {
      String domainKey = domain;
      domainKey.MakeLower();

      DomainBucket &bucket = days_[GetCurrentDayKey()][domainKey];

      // Keep the most specific policy observed for the day.
      if (bucket.policy_type.IsEmpty() || bucket.policy_type == "no-policy-found")
      {
         bucket.policy_type = policyType;
         bucket.policy_string = policyString;
      }

      return bucket;
   }

   void
   TlsRptStore::RecordSuccess(const String &domain, const AnsiString &policyType, const AnsiString &policyString)
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      DomainBucket &bucket = GetBucket_(domain, policyType, policyString);
      bucket.successful_sessions++;
   }

   void
   TlsRptStore::RecordFailure(const String &domain, const AnsiString &policyType, const AnsiString &policyString,
                              const AnsiString &resultType, const String &receivingMx)
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      DomainBucket &bucket = GetBucket_(domain, policyType, policyString);

      AnsiString mx = receivingMx;
      mx.MakeLower();

      for (FailureDetail &failure : bucket.failures)
      {
         if (failure.result_type == resultType && failure.receiving_mx == mx)
         {
            failure.count++;
            return;
         }
      }

      FailureDetail detail;
      detail.result_type = resultType;
      detail.receiving_mx = mx;
      detail.count = 1;

      bucket.failures.push_back(detail);
   }

   std::vector<AnsiString>
   TlsRptStore::GetCompletedDays()
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      AnsiString today = GetCurrentDayKey();

      std::vector<AnsiString> result;
      for (auto iter = days_.begin(); iter != days_.end(); ++iter)
      {
         if (iter->first < today)
            result.push_back(iter->first);
      }

      return result;
   }

   std::map<String, TlsRptStore::DomainBucket>
   TlsRptStore::PopDay(const AnsiString &dayKey)
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      std::map<String, DomainBucket> result;

      auto iter = days_.find(dayKey);
      if (iter != days_.end())
      {
         result = iter->second;
         days_.erase(iter);
      }

      return result;
   }
}
