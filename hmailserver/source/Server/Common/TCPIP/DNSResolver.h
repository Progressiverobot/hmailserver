// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class HostNameAndIpAddress;
   class DNSRecord;

   class DNSResolver
   {
   private:

   public:
      DNSResolver();
      virtual ~DNSResolver();

      bool GetEmailServers(const String &sDomainName, std::vector<HostNameAndIpAddress> &saFoundNames);
      bool GetMXRecords(const String &sDomain, std::vector<String> &vecFoundNames);
      bool GetIpAddresses(const String &sDomain, std::vector<String> &saFoundNames, bool followCnameRecords);
      bool GetTXTRecords(const String &sDomain, std::vector<String> &foundResult);
      bool GetPTRRecords(const String &sIP, std::vector<String> &vecFoundNames);

      // Whether the name exists in the DNS at all. NXDOMAIN is the only answer
      // that means no; a name with no records of the type asked for still exists.
      bool DomainExists(const String &sDomain, bool &dnsError);
   private:

      bool GetEmailServersRecursive_(const String &sDomainName, std::vector<HostNameAndIpAddress> &saFoundNames, int recursionLevel);
      bool GetIpAddressesRecursive_(const String &hostName, std::vector<String> &addresses, int recursionLevel, bool followCnameRecords);
      bool GetTXTRecordsRecursive_(const String &sDomain, std::vector<String> &foundResult, int recursionLevel);
      bool GetMXRecordsRecursive_(const String &sDomain, std::vector<String> &vecFoundNames, int recursionLevel);

      std::vector<String> GetDnsRecordsValues_(std::vector<DNSRecord> records);
   };


}
