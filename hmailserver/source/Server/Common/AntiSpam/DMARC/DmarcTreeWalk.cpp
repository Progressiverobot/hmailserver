// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "DmarcTreeWalk.h"

#include "../../TCPIP/DNSResolver.h"
#include "../../Util/Parsing/StringParser.h"

#include <ctime>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // RFC 9989 4.10 step 4: an eight-or-more-label domain jumps straight to its
      // seven-label suffix, so the walk is bounded at eight queries however long
      // the name is. Without it a deliberately deep name is a DNS amplifier
      // pointed at the receiver.
      const size_t max_labels_walked = 7;

      // Reads one tag out of a DMARC record. Deliberately local rather than
      // reusing DMARC::ParseTagValue_: the walk has to read psd= from records it
      // has NOT yet decided to trust as policy, so it must not depend on the
      // parse that assumes a policy record.
      bool GetTag(const String &record, const String &tag, String &value)
      {
         std::vector<String> parts = StringParser::SplitString(record, ";");

         for (String part : parts)
         {
            part.Trim();

            int equals = part.Find(_T("="));
            if (equals <= 0)
               continue;

            String name = part.Mid(0, equals);
            name.Trim();
            name.ToLower();

            if (name.Compare(tag) != 0)
               continue;

            value = part.Mid(equals + 1);
            value.Trim();
            value.ToLower();

            return true;
         }

         return false;
      }

      // Step 2 and step 6, which are the same rule written twice in the RFC:
      // discard anything that is not a current-version DMARC record, and discard
      // ALL of them if more than one survives. Two records at one name is not a
      // tie to be broken - it is an ambiguity the domain owner has to fix, and
      // guessing would let somebody publish a second record to weaken the first.
      bool SelectSingleRecord(const std::vector<String> &txtRecords, String &out_record)
      {
         int found = 0;

         for (String record : txtRecords)
         {
            String trimmed = record;
            trimmed.Trim();

            if (!trimmed.StartsWith(_T("v=DMARC1")))
               continue;

            found++;

            if (found > 1)
               return false;

            out_record = trimmed;
         }

         return found == 1;
      }
   }

   std::vector<DmarcTreeWalkRecord>
   DmarcTreeWalk::Walk(const String &domain, bool &dnsError)
   {
      dnsError = false;

      std::vector<DmarcTreeWalkRecord> results;

      String lowerDomain = domain;
      lowerDomain.MakeLower();
      lowerDomain.TrimRight(_T("."));

      if (lowerDomain.IsEmpty())
         return results;

      std::vector<String> labels = StringParser::SplitString(lowerDomain, ".");

      if (labels.empty())
         return results;

      // The sequence of names to query: the domain itself, then successively
      // shorter suffixes, with the eight-label shortcut applied to the second
      // one. Built up front so the walk itself is a plain loop and the shortcut
      // is visible in one place rather than as an index adjustment inside it.
      std::vector<String> targets;
      targets.push_back(lowerDomain);

      size_t startIndex = 1;

      if (labels.size() >= 8)
         startIndex = labels.size() - max_labels_walked;

      for (size_t i = startIndex; i < labels.size(); i++)
      {
         String target;

         for (size_t j = i; j < labels.size(); j++)
         {
            if (!target.IsEmpty())
               target += _T(".");

            target += labels[j];
         }

         targets.push_back(target);
      }

      for (const String &target : targets)
      {
         DNSResolver resolver;
         std::vector<String> txtRecords;

         if (!resolver.GetTXTRecords(_T("_dmarc.") + target, txtRecords))
         {
            // A transient resolver failure is NOT "no record". Reported so the
            // caller can answer temperror rather than silently deciding a domain
            // has no DMARC policy because the network hiccuped - which for a
            // p=reject domain is the difference between honouring the policy and
            // ignoring it.
            dnsError = true;
            return results;
         }

         String record;

         if (!SelectSingleRecord(txtRecords, record))
            continue;

         DmarcTreeWalkRecord found;
         found.domain = target;
         found.record = record;

         String psd;

         if (GetTag(record, _T("psd"), psd))
         {
            found.psd_yes = psd.Compare(_T("y")) == 0;
            found.psd_no = psd.Compare(_T("n")) == 0;
         }

         results.push_back(found);

         // Steps 2 and 6: an explicit psd= answer ends the walk. The domain owner
         // has stated where the boundary is, so there is nothing further up worth
         // asking about.
         if (found.psd_yes || found.psd_no)
            break;
      }

      return results;
   }

   String
   DmarcTreeWalk::SelectOrganizationalDomain(const String &startDomain,
                                             const std::vector<DmarcTreeWalkRecord> &records)
   {
      String lowerStart = startDomain;
      lowerStart.MakeLower();
      lowerStart.TrimRight(_T("."));

      // RFC 9989 4.10.2 examines the retrieved records from the longest domain
      // name to the shortest. Walk() collects them in exactly that order, so the
      // rules below read in the order the RFC states them.

      // Rule 1: psd=n IS the Organizational Domain.
      for (const DmarcTreeWalkRecord &record : records)
      {
         if (record.psd_no)
            return record.domain;
      }

      // Rule 2: psd=y anywhere other than the starting domain means the
      // Organizational Domain is one label BELOW it. The exclusion of the start
      // is what stops a domain declaring itself a public suffix and thereby
      // claiming its own parent as its organizational domain.
      for (const DmarcTreeWalkRecord &record : records)
      {
         if (!record.psd_yes || record.domain.Compare(lowerStart) == 0)
            continue;

         std::vector<String> startLabels = StringParser::SplitString(lowerStart, ".");
         std::vector<String> psdLabels = StringParser::SplitString(record.domain, ".");

         // One label below the public suffix, taken from the STARTING domain -
         // the suffix itself cannot supply that label, and the answer has to be
         // a name the message's domain actually sits under.
         if (psdLabels.size() >= startLabels.size())
            continue;

         size_t take = psdLabels.size() + 1;
         String organizationalDomain;

         for (size_t i = startLabels.size() - take; i < startLabels.size(); i++)
         {
            if (!organizationalDomain.IsEmpty())
               organizationalDomain += _T(".");

            organizationalDomain += startLabels[i];
         }

         return organizationalDomain;
      }

      // Rule 3: the record found at the name with the fewest labels.
      if (!records.empty())
      {
         String fewest = records[0].domain;
         size_t fewestCount = StringParser::SplitString(fewest, ".").size();

         for (const DmarcTreeWalkRecord &record : records)
         {
            size_t count = StringParser::SplitString(record.domain, ".").size();

            if (count < fewestCount)
            {
               fewest = record.domain;
               fewestCount = count;
            }
         }

         return fewest;
      }

      // "If this process does not determine the Organizational Domain, then the
      // initial target domain is the Organizational Domain."
      return lowerStart;
   }

   String
   DmarcTreeWalk::GetOrganizationalDomain(const String &domain, bool &dnsError)
   {
      dnsError = false;

      String lowerDomain = domain;
      lowerDomain.MakeLower();
      lowerDomain.TrimRight(_T("."));

      if (lowerDomain.IsEmpty())
         return lowerDomain;

      const time_t now = time(nullptr);

      {
         boost::lock_guard<boost::mutex> guard(mutex_);

         auto cached = cache_.find(lowerDomain);

         if (cached != cache_.end())
         {
            // A cache entry stamped ahead of the clock is a backwards step, not an
            // answer valid for hours.
            if (cached->second.expires > now && cached->second.expires <= now + cache_seconds_)
               return cached->second.organizational_domain;

            cache_.erase(cached);
         }
      }

      std::vector<DmarcTreeWalkRecord> records = Walk(lowerDomain, dnsError);

      if (dnsError)
      {
         // Not cached. Caching a temporary failure would turn one bad second into
         // five minutes of ignoring a domain's policy.
         return lowerDomain;
      }

      String organizationalDomain = SelectOrganizationalDomain(lowerDomain, records);

      {
         boost::lock_guard<boost::mutex> guard(mutex_);

         // Bounded rather than allowed to grow: the keys are attacker-chosen, and
         // a relay taking mail for arbitrary sender domains would otherwise let
         // anybody fill this map. Cleared wholesale when full, because a cache
         // that has just been flooded has nothing worth keeping and the walk that
         // refills it is the same walk that filled it.
         if (cache_.size() >= max_cache_entries_)
            cache_.clear();

         CacheEntry entry;
         entry.organizational_domain = organizationalDomain;
         entry.expires = now + cache_seconds_;

         cache_[lowerDomain] = entry;
      }

      return organizationalDomain;
   }

   void
   DmarcTreeWalk::ClearCache()
   {
      boost::lock_guard<boost::mutex> guard(mutex_);
      cache_.clear();
   }
   namespace
   {
      DmarcTreeWalkRecord MakeRecord(const String &domain, const String &psd)
      {
         DmarcTreeWalkRecord record;
         record.domain = domain;
         record.record = String(_T("v=DMARC1; p=none")) + (psd.IsEmpty() ? String(_T("")) : String(_T("; psd=")) + psd);
         record.psd_yes = psd.Compare(_T("y")) == 0;
         record.psd_no = psd.Compare(_T("n")) == 0;
         return record;
      }
   }

   void
   DmarcTreeWalkTester::Test()
   {
      // Nothing found: the RFC's own fallback - the domain the walk started from
      // is its own organizational domain. This is the common case for the entire
      // internet that publishes no DMARC at all, so getting it wrong would be
      // felt everywhere.
      {
         std::vector<DmarcTreeWalkRecord> none;
         if (DmarcTreeWalk::SelectOrganizationalDomain(_T("mail.example.com"), none).Compare(_T("mail.example.com")) != 0)
            throw 0;
      }

      // Rule 3: the record at the name with the fewest labels. The ordinary case -
      // a policy published at the registered domain, mail sent from a subdomain.
      {
         std::vector<DmarcTreeWalkRecord> records;
         records.push_back(MakeRecord(_T("a.b.example.com"), _T("")));
         records.push_back(MakeRecord(_T("example.com"), _T("")));

         if (DmarcTreeWalk::SelectOrganizationalDomain(_T("a.b.example.com"), records).Compare(_T("example.com")) != 0)
            throw 0;
      }

      // Rule 1 beats rule 3: psd=n IS the organizational domain even though a
      // shorter name also carries a record. This is a domain owner saying "the
      // boundary is here", and it has to win over label arithmetic.
      {
         std::vector<DmarcTreeWalkRecord> records;
         records.push_back(MakeRecord(_T("division.example.co.uk"), _T("n")));
         records.push_back(MakeRecord(_T("co.uk"), _T("")));

         if (DmarcTreeWalk::SelectOrganizationalDomain(_T("mail.division.example.co.uk"), records).Compare(_T("division.example.co.uk")) != 0)
            throw 0;
      }

      // Rule 2: psd=y at a public suffix means the organizational domain is one
      // label BELOW it, taken from the starting domain. This is the case the
      // Public Suffix List existed to answer, now answered by the suffix itself.
      {
         std::vector<DmarcTreeWalkRecord> records;
         records.push_back(MakeRecord(_T("co.uk"), _T("y")));

         if (DmarcTreeWalk::SelectOrganizationalDomain(_T("mail.example.co.uk"), records).Compare(_T("example.co.uk")) != 0)
            throw 0;
      }

      // ...and rule 2 explicitly does NOT apply to a psd=y record at the domain
      // the walk started from. Without that exclusion, a domain could declare
      // itself a public suffix and thereby claim its own PARENT as its
      // organizational domain - aligning it with every sibling under that parent.
      {
         std::vector<DmarcTreeWalkRecord> records;
         records.push_back(MakeRecord(_T("evil.example.com"), _T("y")));

         if (DmarcTreeWalk::SelectOrganizationalDomain(_T("evil.example.com"), records).Compare(_T("evil.example.com")) != 0)
            throw 0;
      }

      // Rule 1 beats rule 2 as well: the RFC states them in order, and a psd=n
      // lower down is a more specific statement than a psd=y above it.
      {
         std::vector<DmarcTreeWalkRecord> records;
         records.push_back(MakeRecord(_T("example.co.uk"), _T("n")));
         records.push_back(MakeRecord(_T("co.uk"), _T("y")));

         if (DmarcTreeWalk::SelectOrganizationalDomain(_T("mail.example.co.uk"), records).Compare(_T("example.co.uk")) != 0)
            throw 0;
      }

      // A single-label domain has nowhere to walk to.
      {
         std::vector<DmarcTreeWalkRecord> none;
         if (DmarcTreeWalk::SelectOrganizationalDomain(_T("localhost"), none).Compare(_T("localhost")) != 0)
            throw 0;
      }

      // Case and a trailing root dot are normalised, because a resolver will hand
      // back either and an organizational domain that differs by a dot compares
      // unequal in alignment - which is a failed DMARC check for a correctly
      // signed message.
      {
         std::vector<DmarcTreeWalkRecord> records;
         records.push_back(MakeRecord(_T("example.com"), _T("")));

         if (DmarcTreeWalk::SelectOrganizationalDomain(_T("Mail.Example.COM."), records).Compare(_T("example.com")) != 0)
            throw 0;
      }
   }
}
