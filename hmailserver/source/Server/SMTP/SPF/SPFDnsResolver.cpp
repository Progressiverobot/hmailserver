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
// than by the DNS_TYPE_ macros of windns.h - this file is compiled on Linux too.

#include "StdAfx.h"

#include "SPFDnsResolver.h"

#include "../../Common/TCPIP/DNSResolver.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // The resource-record type numbers of RFC 1035 and RFC 3596. windns.h spells
      // them DNS_TYPE_A and so on, and DNSResolver.cpp writes the same five numbers
      // out for the POSIX build for the same reason: they are the numbers on the wire
      // and are the same on every platform, while the header that names them is not.
      const int RESOURCE_TYPE_A = 0x0001;
      const int RESOURCE_TYPE_PTR = 0x000c;
      const int RESOURCE_TYPE_MX = 0x000f;
      const int RESOURCE_TYPE_TEXT = 0x0010;
      const int RESOURCE_TYPE_AAAA = 0x001c;
   }

   bool
   SPFDnsResolver::GetTXTRecords(const AnsiString &domain, std::vector<AnsiString> &records)
   {
      // The character-strings of one TXT record arrive already joined, which is
      // what RFC 7208 section 3.3 asks for.
      return Query_(domain, RESOURCE_TYPE_TEXT, records);
   }

   bool
   SPFDnsResolver::GetARecords(const AnsiString &host, std::vector<AnsiString> &addresses)
   {
      return Query_(host, RESOURCE_TYPE_A, addresses);
   }

   bool
   SPFDnsResolver::GetAAAARecords(const AnsiString &host, std::vector<AnsiString> &addresses)
   {
      return Query_(host, RESOURCE_TYPE_AAAA, addresses);
   }

   bool
   SPFDnsResolver::GetMXRecords(const AnsiString &domain, std::vector<AnsiString> &hostNames)
   {
      if (!Query_(domain, RESOURCE_TYPE_MX, hostNames))
         return false;

      // The null MX of RFC 7505: a domain saying it accepts no mail at all. To an
      // mx mechanism that is a domain with no exchangers to match, section 5.4,
      // and the root is not a host name to go looking for addresses of.
      for (int i = (int) hostNames.size(); i > 0; i--)
      {
         if (hostNames[i - 1] == "." || hostNames[i - 1].IsEmpty())
            hostNames.erase(hostNames.begin() + (i - 1));
      }

      return true;
   }

   bool
   SPFDnsResolver::GetPTRRecords(const AnsiString &reverseName, std::vector<AnsiString> &hostNames)
   {
      // The reverse-mapping name is built by the evaluation rather than here: it
      // is what %{ir}.%{v}.arpa expands to, and SPFAddress is the one place that
      // spells it.
      return Query_(reverseName, RESOURCE_TYPE_PTR, hostNames);
   }

   bool
   SPFDnsResolver::Query_(const AnsiString &query, int resourceType, std::vector<AnsiString> &values)
   {
      values.clear();

      // There is no query to make, so the answer is that the lookup did not happen -
      // which is what SPFTestLookup models and what DNSResolver would report anyway.
      // It is refused HERE because DNSResolver reports an empty name through
      // ErrorManager (HM5516), and this is the one caller that can reach it without a
      // mistake having been made: section 6.2's exp modifier is expanded and then
      // looked up without being re-checked, because a name that comes out unusable is
      // a name with no explanation behind it rather than an error. One such record
      // would otherwise write a line to the error log for every message it failed.
      if (query.IsEmpty())
         return false;

      DNSResolver resolver;

      return resolver.GetRecordsOfType(query, resourceType, values);
   }
}
