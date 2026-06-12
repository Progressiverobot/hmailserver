// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// In-memory store of outbound TLS session results, aggregated per UTC day
// and recipient domain. Used to generate SMTP TLS reports (RFC 8460).

#pragma once

namespace HM
{
   class TlsRptStore : public Singleton<TlsRptStore>
   {
   public:

      struct FailureDetail
      {
         AnsiString result_type;
         AnsiString receiving_mx;
         int count = 0;
      };

      struct DomainBucket
      {
         AnsiString policy_type;      // "sts", "tlsa" or "no-policy-found"
         AnsiString policy_string;
         int successful_sessions = 0;
         std::vector<FailureDetail> failures;
      };

      void RecordSuccess(const String &domain, const AnsiString &policyType, const AnsiString &policyString);
      void RecordFailure(const String &domain, const AnsiString &policyType, const AnsiString &policyString,
                         const AnsiString &resultType, const String &receivingMx);

      // Returns the day keys (yyyy-mm-dd, UTC) of all days before the
      // current one which still hold data.
      std::vector<AnsiString> GetCompletedDays();

      // Removes and returns the data for one day.
      std::map<String, DomainBucket> PopDay(const AnsiString &dayKey);

      static AnsiString GetCurrentDayKey();

   private:

      DomainBucket& GetBucket_(const String &domain, const AnsiString &policyType, const AnsiString &policyString);

      boost::recursive_mutex mutex_;
      std::map<AnsiString, std::map<String, DomainBucket> > days_;
   };
}
