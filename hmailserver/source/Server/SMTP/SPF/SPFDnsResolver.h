// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// Ported from hMailServer upstream, https://github.com/hmailserver/hmailserver,
// commit 1beaeab9 ("Replace SPF evaluator", #645) of 13 September 2026, where
// this file carries the notice "Copyright (c) 2010 Martin Knafve /
// hMailServer.com". Both trees are AGPL-3.0-or-later, so the port is
// licence-clean and the attribution stands here.
//
// Changed in this tree: it asks DNSResolver::GetRecordsOfType, which was added
// for it and answers in bytes, and the record types are named by number rather
// than by the DNS_TYPE_ macros of windns.h - this file is compiled on Linux too,
// where there is no such header. The null-MX filter is here for the same reason
// it is upstream: our GetMXRecords reports RFC 7505's null MX the way it reports
// a failed lookup, and section 5.4 reads it as a domain with no exchangers.

#pragma once

#include "SPFDnsLookup.h"

namespace HM
{
   // The SPFDnsLookup the server uses: real DNS, through this server's own
   // DNSResolver - so an SPF check honours the configured DNS server, can be put
   // behind the regression suite's zone, and works on every platform the resolver
   // does. It goes through GetRecordsOfType rather than the typed methods beside
   // it; README.md says why, and so does that method.
   class SPFDnsResolver : public SPFDnsLookup
   {
   public:

      virtual bool GetTXTRecords(const AnsiString &domain, std::vector<AnsiString> &records);
      virtual bool GetARecords(const AnsiString &host, std::vector<AnsiString> &addresses);
      virtual bool GetAAAARecords(const AnsiString &host, std::vector<AnsiString> &addresses);
      virtual bool GetMXRecords(const AnsiString &domain, std::vector<AnsiString> &hostNames);
      virtual bool GetPTRRecords(const AnsiString &reverseName, std::vector<AnsiString> &hostNames);

   private:

      static bool Query_(const AnsiString &query, int resourceType, std::vector<AnsiString> &values);
   };
}
