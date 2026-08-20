// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// See DmarcRptStore.h.

#include "StdAfx.h"

#include "DmarcRptStore.h"

#include <ctime>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   // A row exists per unique (source, verdict, identities) combination, and
   // source IPs are attacker-chosen on a server that accepts mail from the
   // internet, so a day's rows must be bounded. 1000 unique combinations per
   // policy domain per day is far beyond what a small server sees and cheap
   // to hold; past it, further unique combinations are counted as dropped
   // and the reporter says so.
   static const size_t kMaxRowsPerDomain = 1000;

   // Domains are bounded too, though more softly: recording only happens for
   // domains that actually publish a DMARC policy, so growing this requires
   // real DNS records. A cap all the same, because "requires real domains"
   // is not the same as "bounded".
   static const size_t kMaxDomainsPerDay = 10000;

   AnsiString
   DmarcRptStore::GetCurrentDayKey()
   {
      time_t now = time(nullptr);

      tm utcTime = {};
      gmtime_s(&utcTime, &now);

      AnsiString key;
      key.Format("%04d-%02d-%02d", utcTime.tm_year + 1900, utcTime.tm_mon + 1, utcTime.tm_mday);
      return key;
   }

   void
   DmarcRptStore::Record(const AnsiString &policyDomain,
                         const AnsiString &adkim, const AnsiString &aspf,
                         const AnsiString &p, const AnsiString &sp, const AnsiString &np,
                         const AnsiString &testing, int pct, const AnsiString &discoveryMethod,
                         const AnsiString &sourceIp, const AnsiString &disposition,
                         bool dkimAligned, bool spfAligned,
                         const AnsiString &headerFrom,
                         const AnsiString &envelopeFromDomain, bool spfPassed,
                         const std::vector<AnsiString> &dkimPassingDomains,
                         const std::vector<AnsiString> &dkimPassingSelectors)
   {
      AnsiString domainKey = policyDomain;
      domainKey.MakeLower();
      if (domainKey.IsEmpty())
         return;

      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      std::map<AnsiString, DomainBucket> &day = days_[GetCurrentDayKey()];

      auto bucketIter = day.find(domainKey);
      if (bucketIter == day.end())
      {
         if (day.size() >= kMaxDomainsPerDay)
            return;

         bucketIter = day.insert(std::make_pair(domainKey, DomainBucket())).first;

         // The policy published when the domain's first message of the day
         // arrived. A policy edited mid-day is reported as it was first seen,
         // which RFC 7489 6.2 explicitly allows a reporter to do.
         DomainBucket &fresh = bucketIter->second;
         fresh.policy_domain = domainKey;
         fresh.adkim = adkim;
         fresh.aspf = aspf;
         fresh.p = p;
         fresh.sp = sp;
         fresh.np = np;
         fresh.testing = testing;
         fresh.discovery_method = discoveryMethod;
         fresh.pct = pct;
      }

      DomainBucket &bucket = bucketIter->second;

      AnsiString ip = sourceIp;
      ip.MakeLower();

      AnsiString from = headerFrom;
      from.MakeLower();

      AnsiString envelopeDomain = envelopeFromDomain;
      envelopeDomain.MakeLower();

      const AnsiString dkimVerdict = dkimAligned ? "pass" : "fail";
      const AnsiString spfVerdict = spfAligned ? "pass" : "fail";

      for (Row &row : bucket.rows)
      {
         if (row.source_ip == ip &&
             row.disposition == disposition &&
             row.dkim == dkimVerdict &&
             row.spf == spfVerdict &&
             row.header_from == from &&
             row.envelope_from_domain == envelopeDomain &&
             row.spf_passed == spfPassed &&
             row.dkim_passing_domains == dkimPassingDomains &&
             row.dkim_passing_selectors == dkimPassingSelectors)
         {
            row.count++;
            return;
         }
      }

      if (bucket.rows.size() >= kMaxRowsPerDomain)
      {
         bucket.dropped_rows++;
         return;
      }

      Row row;
      row.source_ip = ip;
      row.disposition = disposition;
      row.dkim = dkimVerdict;
      row.spf = spfVerdict;
      row.header_from = from;
      row.envelope_from_domain = envelopeDomain;
      row.spf_passed = spfPassed;
      row.dkim_passing_domains = dkimPassingDomains;
      row.dkim_passing_selectors = dkimPassingSelectors;
      row.count = 1;

      bucket.rows.push_back(row);
   }

   std::vector<AnsiString>
   DmarcRptStore::GetCompletedDays()
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

   std::map<AnsiString, DmarcRptStore::DomainBucket>
   DmarcRptStore::PopDay(const AnsiString &dayKey)
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      std::map<AnsiString, DomainBucket> result;

      auto iter = days_.find(dayKey);
      if (iter != days_.end())
      {
         result = iter->second;
         days_.erase(iter);
      }

      return result;
   }
}
