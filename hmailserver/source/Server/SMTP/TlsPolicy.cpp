// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Outbound TLS policy support (MTA-STS, DANE TLSA). See TlsPolicy.h.

#include "stdafx.h"

#include "TlsPolicy.h"

#include "../Common/TCPIP/DNSResolver.h"
#include "../Common/TCPIP/CertificateVerifier.h"
#include "../Common/TCPIP/DnssecResolver.h"
#include "../Common/TCPIP/SocketConstants.h"
#include "../Common/Util/TlsRptStore.h"

#include <ws2tcpip.h>
#include <iphlpapi.h>

#include <openssl/rand.h>
#include <openssl/ssl.h>

#include <ctime>

#pragma comment(lib, "iphlpapi.lib")
#pragma comment(lib, "ws2_32.lib")

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   boost::recursive_mutex TlsPolicy::sts_cache_mutex_;
   std::map<String, TlsPolicy::CachedStsPolicy> TlsPolicy::sts_cache_;

   namespace
   {
      const size_t MaxPolicyBodySize = 64 * 1024;
      const int MaxStsMaxAge = 31557600;     // RFC 8461: max one year.
      const int MinStsCacheSeconds = 300;    // Lower bound for our cache entries.
      const int RevalidateIntervalSeconds = 3600;
      const int NegativeCacheSeconds = 1800; // Cache "no policy" results for 30 minutes.
      const int DnsTimeoutMilliseconds = 5000;
      const int HttpsTimeoutMilliseconds = 15000;
      const unsigned short DnsTypeTlsa = 52;
      const size_t MaxTlsaRecords = 8;
   }

   TlsPolicy::StsPolicy
   TlsPolicy::GetStsPolicy(const String &domain)
   {
      String cacheKey = domain;
      cacheKey.MakeLower();

      time_t now = time(nullptr);

      // Cache access is done under the lock; network operations (DNS and
      // HTTPS) are always performed outside of it so parallel delivery
      // threads are not serialized behind slow lookups.
      bool haveCachedPolicy = false;
      AnsiString cachedId;
      StsPolicy cachedPolicy;

      {
         boost::lock_guard<boost::recursive_mutex> guard(sts_cache_mutex_);

         auto iter = sts_cache_.find(cacheKey);
         if (iter != sts_cache_.end())
         {
            CachedStsPolicy &cached = iter->second;

            if (now >= cached.expires_at)
            {
               sts_cache_.erase(iter);
            }
            else if (now < cached.revalidate_at)
            {
               return cached.policy;
            }
            else
            {
               // Due for revalidation. Bump the revalidation time so that
               // only this thread performs it; remember a snapshot to work
               // with outside the lock.
               cached.revalidate_at = now + RevalidateIntervalSeconds;

               haveCachedPolicy = true;
               cachedId = cached.id;
               cachedPolicy = cached.policy;
            }
         }
      }

      if (haveCachedPolicy)
      {
         // Check whether the policy id in DNS has changed. On DNS failure
         // or unchanged id, keep using the cached policy (RFC 8461 5.1).
         AnsiString currentId;
         if (!LookupStsDnsRecord_(cacheKey, currentId) || currentId == cachedId)
            return cachedPolicy;

         StsPolicy updatedPolicy;
         int maxAge = 0;
         if (!FetchStsPolicy_(cacheKey, updatedPolicy, maxAge))
            return cachedPolicy; // Fetch failed - continue using cached policy.

         CachedStsPolicy updatedEntry;
         updatedEntry.policy = updatedPolicy;
         updatedEntry.id = currentId;
         updatedEntry.expires_at = now + maxAge;
         updatedEntry.revalidate_at = now + RevalidateIntervalSeconds;

         boost::lock_guard<boost::recursive_mutex> guard(sts_cache_mutex_);
         sts_cache_[cacheKey] = updatedEntry;
         return updatedPolicy;
      }

      // No cached policy. Look for a policy advertisement in DNS.
      CachedStsPolicy newEntry;
      newEntry.revalidate_at = now + RevalidateIntervalSeconds;

      AnsiString policyId;
      if (!LookupStsDnsRecord_(cacheKey, policyId))
      {
         // No MTA-STS record published. Negative-cache the result.
         newEntry.expires_at = now + NegativeCacheSeconds;

         boost::lock_guard<boost::recursive_mutex> guard(sts_cache_mutex_);
         sts_cache_[cacheKey] = newEntry;
         return newEntry.policy;
      }

      StsPolicy fetchedPolicy;
      int maxAge = 0;
      if (!FetchStsPolicy_(cacheKey, fetchedPolicy, maxAge))
      {
         // Policy advertised but not retrievable. Per RFC 8461 this is a
         // potential downgrade attack, but with no previously cached policy
         // we have nothing to enforce. Negative-cache briefly and log.
         String message;
         message.Format(_T("MTA-STS: Domain %s advertises a policy but the policy file could not be fetched."), String(cacheKey).c_str());
         LOG_SMTP_CLIENT(0, "", message);

         TlsRptStore::Instance()->RecordFailure(cacheKey, "sts", "", "sts-policy-fetch-error", "");

         newEntry.expires_at = now + MinStsCacheSeconds;

         boost::lock_guard<boost::recursive_mutex> guard(sts_cache_mutex_);
         sts_cache_[cacheKey] = newEntry;
         return newEntry.policy;
      }

      newEntry.policy = fetchedPolicy;
      newEntry.id = policyId;
      newEntry.expires_at = now + maxAge;

      boost::lock_guard<boost::recursive_mutex> guard(sts_cache_mutex_);
      sts_cache_[cacheKey] = newEntry;

      return fetchedPolicy;
   }

   bool
   TlsPolicy::LookupStsDnsRecord_(const String &domain, AnsiString &id)
   {
      DNSResolver resolver;

      std::vector<String> txtRecords;
      if (!resolver.GetTXTRecords("_mta-sts." + domain, txtRecords))
         return false;

      for (const String &record : txtRecords)
      {
         AnsiString narrow = record;
         narrow.Trim();

         if (!narrow.StartsWith("v=STSv1"))
            continue;

         // Extract the id= field.
         std::vector<AnsiString> parts = StringParser::SplitString(narrow, ";");
         for (AnsiString part : parts)
         {
            part.Trim();
            if (part.StartsWith("id="))
            {
               id = part.Mid(3);
               id.Trim();
               return !id.IsEmpty();
            }
         }
      }

      return false;
   }

   bool
   TlsPolicy::FetchStsPolicy_(const String &domain, StsPolicy &policy, int &max_age)
   {
      max_age = 0;

      AnsiString body;
      if (!HttpsGet_("mta-sts." + domain, "/.well-known/mta-sts.txt", body))
         return false;

      if (!ParseStsPolicyBody_(body, policy, max_age))
         return false;

      if (max_age < MinStsCacheSeconds)
         max_age = MinStsCacheSeconds;
      if (max_age > MaxStsMaxAge)
         max_age = MaxStsMaxAge;

      return true;
   }

   bool
   TlsPolicy::ParseStsPolicyBody_(const AnsiString &body, StsPolicy &policy, int &max_age)
   {
      max_age = 0;

      bool versionSeen = false;

      std::vector<AnsiString> lines = StringParser::SplitString(body, "\n");
      for (AnsiString line : lines)
      {
         line.TrimRight("\r");
         line.Trim();

         if (line.IsEmpty())
            continue;

         int separatorPos = line.Find(":");
         if (separatorPos <= 0)
            continue;

         AnsiString key = line.Mid(0, separatorPos);
         AnsiString value = line.Mid(separatorPos + 1);
         key.Trim();
         value.Trim();
         key.MakeLower();

         if (key == "version")
         {
            if (value != "STSv1")
               return false;
            versionSeen = true;
         }
         else if (key == "mode")
         {
            value.MakeLower();
            if (value == "enforce")
               policy.mode = StsEnforce;
            else if (value == "testing")
               policy.mode = StsTesting;
            else
               policy.mode = StsNone;
         }
         else if (key == "mx")
         {
            if (!value.IsEmpty())
               policy.mx_patterns.push_back(String(value));
         }
         else if (key == "max_age")
         {
            if (StringParser::IsNumeric(value))
               max_age = atoi(value);
         }
      }

      if (!versionSeen)
         return false;

      if (policy.mode == StsEnforce && policy.mx_patterns.empty())
      {
         // An enforce policy without mx patterns is invalid - treat as none.
         policy.mode = StsNone;
         return false;
      }

      return true;
   }

   bool
   TlsPolicy::HostMatchesStsPolicy(const String &host_name, const StsPolicy &policy)
   {
      String host = host_name;
      host.MakeLower();

      for (const String &patternConst : policy.mx_patterns)
      {
         String pattern = patternConst;
         pattern.MakeLower();

         if (pattern == host)
            return true;

         if (pattern.StartsWith(_T("*.")))
         {
            // The wildcard matches exactly one leftmost label.
            String suffix = pattern.Mid(1); // ".example.com"

            if (host.GetLength() <= suffix.GetLength())
               continue;

            if (host.Right(suffix.GetLength()) != suffix)
               continue;

            String prefix = host.Mid(0, host.GetLength() - suffix.GetLength());
            if (!prefix.IsEmpty() && prefix.Find(_T(".")) < 0)
               return true;
         }
      }

      return false;
   }

   bool
   TlsPolicy::HttpsGet_(const String &host, const AnsiString &path, AnsiString &response_body)
   {
      try
      {
         AnsiString narrowHost = host;

         boost::asio::io_context ioContext;

         boost::asio::ip::tcp::resolver resolver(ioContext);
         boost::asio::ip::tcp::resolver::results_type endpoints =
            resolver.resolve(std::string(narrowHost.c_str()), "443");

         boost::asio::ssl::context sslContext(boost::asio::ssl::context::tls_client);
         sslContext.set_default_verify_paths();

         boost::asio::ssl::stream<boost::asio::ip::tcp::socket> stream(ioContext, sslContext);

         boost::asio::connect(stream.next_layer(), endpoints);

         // Apply socket-level timeouts so a stalled policy host cannot hang
         // the delivery thread indefinitely.
         DWORD timeout = HttpsTimeoutMilliseconds;
         setsockopt(stream.next_layer().native_handle(), SOL_SOCKET, SO_RCVTIMEO, (const char*) &timeout, sizeof(timeout));
         setsockopt(stream.next_layer().native_handle(), SOL_SOCKET, SO_SNDTIMEO, (const char*) &timeout, sizeof(timeout));

         // RFC 8461 requires certificate validation for the policy fetch.
         stream.set_verify_mode(boost::asio::ssl::verify_peer);
         stream.set_verify_callback(CertificateVerifier(0, CSSSL, host));

         if (!SSL_set_tlsext_host_name(stream.native_handle(), narrowHost.c_str()))
            return false;

         stream.handshake(boost::asio::ssl::stream_base::client);

         AnsiString request;
         request.append("GET ");
         request.append(path);
         request.append(" HTTP/1.0\r\nHost: ");
         request.append(narrowHost);
         request.append("\r\nUser-Agent: hMailServer\r\nConnection: close\r\n\r\n");

         boost::asio::write(stream, boost::asio::buffer(request.c_str(), request.GetLength()));

         std::string response;
         char buffer[4096];
         boost::system::error_code errorCode;

         for (;;)
         {
            size_t bytesRead = stream.read_some(boost::asio::buffer(buffer, sizeof(buffer)), errorCode);

            if (bytesRead > 0)
               response.append(buffer, bytesRead);

            if (errorCode)
               break;

            if (response.size() > MaxPolicyBodySize)
               break;
         }

         // Parse the status line.
         size_t firstLineEnd = response.find("\r\n");
         if (firstLineEnd == std::string::npos)
            return false;

         std::string statusLine = response.substr(0, firstLineEnd);
         if (statusLine.find(" 200") == std::string::npos)
            return false;

         size_t headerEnd = response.find("\r\n\r\n");
         if (headerEnd == std::string::npos)
            return false;

         response_body = response.substr(headerEnd + 4).c_str();
         return true;
      }
      catch (...)
      {
         return false;
      }
   }

   std::vector<TlsaRecord>
   TlsPolicy::GetTlsaRecords(const String &host_name, int port, TlsaLookupStatus &status)
   {
      if (IniFileSettings::Instance()->GetDnssecValidationEnabled())
      {
         DnssecResolver resolver;

         std::vector<TlsaRecord> rawRecords;
         DnssecResolver::ChainStatus chainStatus = resolver.QueryTlsa(host_name, port, rawRecords);

         switch (chainStatus)
         {
         case DnssecResolver::ChainStatus::Secure:
            {
               std::vector<TlsaRecord> usable = FilterUsableTlsaRecords_(rawRecords);
               status = usable.empty() ? TlsaLookupStatus::NoRecords : TlsaLookupStatus::DnssecValidated;
               return usable;
            }

         case DnssecResolver::ChainStatus::Bogus:
            status = TlsaLookupStatus::Bogus;
            return std::vector<TlsaRecord>();

         case DnssecResolver::ChainStatus::Insecure:
         default:
            status = TlsaLookupStatus::NoRecords;
            return std::vector<TlsaRecord>();
         }
      }

      // Legacy opportunistic mode (DnssecValidationEnabled=0).
      std::vector<TlsaRecord> records = LookupTlsaOpportunistic_(host_name, port);
      status = records.empty() ? TlsaLookupStatus::NoRecords : TlsaLookupStatus::Unvalidated;
      return records;
   }

   std::vector<TlsaRecord>
   TlsPolicy::FilterUsableTlsaRecords_(const std::vector<TlsaRecord> &records)
   {
      // Keep only records we can act on: DANE-EE (usage 3) with a
      // supported selector and matching type.
      std::vector<TlsaRecord> result;

      for (const TlsaRecord &record : records)
      {
         if (record.usage != 3)
            continue;
         if (record.selector != 0 && record.selector != 1)
            continue;
         if (record.matching_type < 0 || record.matching_type > 2)
            continue;
         if (record.data.empty())
            continue;

         result.push_back(record);

         if (result.size() >= MaxTlsaRecords)
            break;
      }

      return result;
   }

   std::vector<TlsaRecord>
   TlsPolicy::LookupTlsaOpportunistic_(const String &host_name, int port)
   {
      std::vector<TlsaRecord> result;

      AnsiString queryName;
      AnsiString narrowHost = host_name;
      narrowHost.MakeLower();
      queryName.Format("_%d._tcp.%hs", port, narrowHost.c_str());

      std::vector<sockaddr_in> dnsServers;
      if (!GetDnsServers_(dnsServers))
         return result;

      for (const sockaddr_in &server : dnsServers)
      {
         std::vector<unsigned char> response;
         if (!RunDnsQuery_(server, queryName, DnsTypeTlsa, response))
            continue;

         std::vector<TlsaRecord> records;
         if (!ParseTlsaResponse_(response, records))
            continue;

         result = FilterUsableTlsaRecords_(records);
         return result;
      }

      return result;
   }

   bool
   TlsPolicy::GetDnsServers_(std::vector<sockaddr_in> &servers)
   {
      // If an explicit DNS server is configured in hMailServer.ini, use it.
      String configuredServer = IniFileSettings::Instance()->GetDNSServer();
      if (!configuredServer.IsEmpty())
      {
         AnsiString narrow = configuredServer;

         sockaddr_in address = {};
         address.sin_family = AF_INET;
         address.sin_port = htons(53);

         if (inet_pton(AF_INET, narrow.c_str(), &address.sin_addr) == 1)
         {
            servers.push_back(address);
            return true;
         }
      }

      // Otherwise, pick up the system-configured DNS servers.
      ULONG bufferSize = 16 * 1024;
      std::vector<unsigned char> buffer(bufferSize);

      ULONG flags = GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_FRIENDLY_NAME | GAA_FLAG_SKIP_UNICAST;

      ULONG queryResult = GetAdaptersAddresses(AF_INET, flags, nullptr, reinterpret_cast<IP_ADAPTER_ADDRESSES*>(buffer.data()), &bufferSize);

      if (queryResult == ERROR_BUFFER_OVERFLOW)
      {
         buffer.resize(bufferSize);
         queryResult = GetAdaptersAddresses(AF_INET, flags, nullptr, reinterpret_cast<IP_ADAPTER_ADDRESSES*>(buffer.data()), &bufferSize);
      }

      if (queryResult != NO_ERROR)
         return false;

      for (IP_ADAPTER_ADDRESSES *adapter = reinterpret_cast<IP_ADAPTER_ADDRESSES*>(buffer.data());
           adapter != nullptr;
           adapter = adapter->Next)
      {
         if (adapter->OperStatus != IfOperStatusUp)
            continue;

         for (IP_ADAPTER_DNS_SERVER_ADDRESS *dns = adapter->FirstDnsServerAddress;
              dns != nullptr;
              dns = dns->Next)
         {
            if (dns->Address.lpSockaddr == nullptr || dns->Address.lpSockaddr->sa_family != AF_INET)
               continue;

            sockaddr_in address = {};
            memcpy(&address, dns->Address.lpSockaddr, sizeof(sockaddr_in));
            address.sin_port = htons(53);

            servers.push_back(address);

            if (servers.size() >= 2)
               return true;
         }
      }

      return !servers.empty();
   }

   bool
   TlsPolicy::RunDnsQuery_(const sockaddr_in &server, const AnsiString &name, unsigned short query_type, std::vector<unsigned char> &response)
   {
      // Build the query packet.
      unsigned char transactionId[2];
      if (RAND_bytes(transactionId, sizeof(transactionId)) != 1)
      {
         transactionId[0] = static_cast<unsigned char>(GetTickCount() & 0xFF);
         transactionId[1] = static_cast<unsigned char>((GetTickCount() >> 8) & 0xFF);
      }

      std::vector<unsigned char> query;
      query.reserve(64 + name.GetLength());

      // Header
      query.push_back(transactionId[0]);
      query.push_back(transactionId[1]);
      query.push_back(0x01); // RD
      query.push_back(0x00);
      query.push_back(0x00); query.push_back(0x01); // QDCOUNT 1
      query.push_back(0x00); query.push_back(0x00); // ANCOUNT 0
      query.push_back(0x00); query.push_back(0x00); // NSCOUNT 0
      query.push_back(0x00); query.push_back(0x01); // ARCOUNT 1 (EDNS0 OPT)

      // Question name
      std::vector<AnsiString> labels = StringParser::SplitString(name, ".");
      for (const AnsiString &label : labels)
      {
         if (label.IsEmpty() || label.GetLength() > 63)
            return false;

         query.push_back(static_cast<unsigned char>(label.GetLength()));
         for (int i = 0; i < label.GetLength(); i++)
            query.push_back(static_cast<unsigned char>(label[i]));
      }
      query.push_back(0x00);

      // QTYPE / QCLASS
      query.push_back(static_cast<unsigned char>(query_type >> 8));
      query.push_back(static_cast<unsigned char>(query_type & 0xFF));
      query.push_back(0x00); query.push_back(0x01); // IN

      // EDNS0 OPT pseudo-record: root name, type 41, class = UDP payload size 1232.
      query.push_back(0x00);                        // root
      query.push_back(0x00); query.push_back(41);   // TYPE OPT
      query.push_back(0x04); query.push_back(0xD0); // CLASS 1232
      query.push_back(0x00); query.push_back(0x00); query.push_back(0x00); query.push_back(0x00); // TTL
      query.push_back(0x00); query.push_back(0x00); // RDLEN 0

      SOCKET udpSocket = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
      if (udpSocket == INVALID_SOCKET)
         return false;

      DWORD timeout = DnsTimeoutMilliseconds;
      setsockopt(udpSocket, SOL_SOCKET, SO_RCVTIMEO, (const char*) &timeout, sizeof(timeout));
      setsockopt(udpSocket, SOL_SOCKET, SO_SNDTIMEO, (const char*) &timeout, sizeof(timeout));

      bool succeeded = false;

      if (sendto(udpSocket, reinterpret_cast<const char*>(query.data()), static_cast<int>(query.size()), 0,
                 reinterpret_cast<const sockaddr*>(&server), sizeof(server)) == static_cast<int>(query.size()))
      {
         unsigned char receiveBuffer[4096];

         sockaddr_in fromAddress = {};
         int fromLength = sizeof(fromAddress);

         int bytesReceived = recvfrom(udpSocket, reinterpret_cast<char*>(receiveBuffer), sizeof(receiveBuffer), 0,
                                      reinterpret_cast<sockaddr*>(&fromAddress), &fromLength);

         if (bytesReceived >= 12 &&
             receiveBuffer[0] == transactionId[0] &&
             receiveBuffer[1] == transactionId[1] &&
             fromAddress.sin_addr.s_addr == server.sin_addr.s_addr)
         {
            response.assign(receiveBuffer, receiveBuffer + bytesReceived);
            succeeded = true;
         }
      }

      closesocket(udpSocket);
      return succeeded;
   }

   bool
   TlsPolicy::SkipDnsName_(const std::vector<unsigned char> &data, size_t &offset)
   {
      size_t safetyCounter = 0;

      while (offset < data.size())
      {
         if (++safetyCounter > 128)
            return false;

         unsigned char length = data[offset];

         if (length == 0)
         {
            offset += 1;
            return true;
         }

         if ((length & 0xC0) == 0xC0)
         {
            // Compression pointer: two bytes, then the name ends here.
            offset += 2;
            return offset <= data.size();
         }

         if (length > 63)
            return false;

         offset += static_cast<size_t>(length) + 1;
      }

      return false;
   }

   bool
   TlsPolicy::ParseTlsaResponse_(const std::vector<unsigned char> &response, std::vector<TlsaRecord> &records)
   {
      if (response.size() < 12)
         return false;

      unsigned char flags1 = response[2];
      unsigned char flags2 = response[3];

      bool isResponse = (flags1 & 0x80) != 0;
      bool truncated = (flags1 & 0x02) != 0;
      int responseCode = flags2 & 0x0F;

      if (!isResponse || responseCode != 0)
         return false;

      if (truncated)
      {
         LOG_DEBUG("DANE: TLSA response truncated; skipping DANE for this host.");
         return false;
      }

      size_t questionCount = (static_cast<size_t>(response[4]) << 8) | response[5];
      size_t answerCount = (static_cast<size_t>(response[6]) << 8) | response[7];

      size_t offset = 12;

      // Skip questions.
      for (size_t i = 0; i < questionCount; i++)
      {
         if (!SkipDnsName_(response, offset))
            return false;

         offset += 4; // QTYPE + QCLASS
         if (offset > response.size())
            return false;
      }

      // Parse answers.
      for (size_t i = 0; i < answerCount; i++)
      {
         if (!SkipDnsName_(response, offset))
            return false;

         if (offset + 10 > response.size())
            return false;

         unsigned short recordType = (static_cast<unsigned short>(response[offset]) << 8) | response[offset + 1];
         size_t rdataLength = (static_cast<size_t>(response[offset + 8]) << 8) | response[offset + 9];

         offset += 10;

         if (offset + rdataLength > response.size())
            return false;

         if (recordType == DnsTypeTlsa && rdataLength >= 4)
         {
            TlsaRecord record;
            record.usage = response[offset];
            record.selector = response[offset + 1];
            record.matching_type = response[offset + 2];
            record.data.assign(reinterpret_cast<const char*>(response.data() + offset + 3), rdataLength - 3);

            records.push_back(record);
         }

         offset += rdataLength;
      }

      return true;
   }
}
