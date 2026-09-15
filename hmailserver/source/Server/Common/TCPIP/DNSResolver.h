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

      // The values of one record type, as the bytes they arrived as, following a
      // CNAME where the name holds none of that type. False means the query could
      // not be answered; true with nothing in values means the name holds no record
      // of that type. resourceType is a DNS_TYPE_ constant, as DNSResolverWinApi
      // takes.
      //
      // For SPF, which cannot use the typed methods above because each of them adds
      // something its own callers want and RFC 7208 forbids. GetIpAddresses merges A
      // and AAAA into one list and leaves AAAA out unless IPv6 is available on this
      // server, where sections 5.3 and 5.4 select by the family the CLIENT connected
      // over; GetPTRRecords builds a reverse-mapping name out of an address, which an
      // evaluation has already built for the i and v macros, and a name built twice
      // can be built two ways; GetMXRecords reports the null MX of RFC 7505 the way it
      // reports a failed query, which would make it a temperror, where section 5.4
      // reads it as a domain with no exchangers.
      //
      // Bytes rather than String, because a TXT record may hold a byte the wide
      // conversion has no business interpreting, and section 3.1 needs the evaluator
      // to see that byte in order to reject the record. The DNSSEC handling of
      // GetTXTRecords applies here too, for the same record type and the same reason.
      bool GetRecordsOfType(const AnsiString &query, int resourceType, std::vector<AnsiString> &values);

   private:

      bool GetEmailServersRecursive_(const String &sDomainName, std::vector<HostNameAndIpAddress> &saFoundNames, int recursionLevel);
      bool GetIpAddressesRecursive_(const String &hostName, std::vector<String> &addresses, int recursionLevel, bool followCnameRecords);
      bool GetTXTRecordsRecursive_(const String &sDomain, std::vector<String> &foundResult, int recursionLevel);

      // The records of one type, following a CNAME where the name holds none of that
      // type. GetTXTRecordsRecursive_ and GetRecordsOfType differ only in what they do
      // with what comes back, so this is where both look.
      bool GetRecordsOfTypeRecursive_(const String &query, int resourceType, std::vector<DNSRecord> &records, int recursionLevel);

      // The DNSSEC gate GetTXTRecords applies to a TXT lookup: Bogus is a forged
      // answer and fails the lookup, Secure answers from the validating resolver, and
      // Insecure falls through to the system one. answered says which of the three
      // happened - false means "fall through and ask the way you would have".
      bool TryValidatedTxtRecords_(const String &sDomain, std::vector<AnsiString> &texts, bool &answered, bool &bogus);
      bool GetMXRecordsRecursive_(const String &sDomain, std::vector<String> &vecFoundNames, int recursionLevel);

      std::vector<String> GetDnsRecordsValues_(std::vector<DNSRecord> records);
   };


}
