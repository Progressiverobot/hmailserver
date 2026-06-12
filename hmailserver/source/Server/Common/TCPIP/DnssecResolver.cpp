// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Validating DNSSEC stub resolver. See DnssecResolver.h.
//
// Failure policy (chosen for safe mail flow):
//   - Transport failures (no resolver reachable, SERVFAIL) degrade to
//     Insecure: DANE is simply not applied, mail still flows.
//   - Received data that fails cryptographic validation is Bogus: the
//     host is not used (RFC 7672 section 2.1.3).

#include "StdAfx.h"

#include "DnssecResolver.h"

#include <ws2tcpip.h>
#include <iphlpapi.h>

#include <openssl/evp.h>
#include <openssl/bn.h>
#include <openssl/ec.h>
#include <openssl/param_build.h>
#include <openssl/core_names.h>
#include <openssl/rand.h>
#include <openssl/sha.h>

#include <algorithm>
#include <ctime>

#pragma comment(lib, "iphlpapi.lib")
#pragma comment(lib, "ws2_32.lib")

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      const unsigned short DnsTypeCname = 5;
      const unsigned short DnsTypeTxt = 16;
      const unsigned short DnsTypeDs = 43;
      const unsigned short DnsTypeRrsig = 46;
      const unsigned short DnsTypeDnskey = 48;
      const unsigned short DnsTypeTlsa = 52;

      const DWORD DnsTimeoutMilliseconds = 5000;
      const int MaxChainDepth = 16;
      const int MaxCnameHops = 3;

      enum class InternalStatus
      {
         Secure,
         Insecure,
         Bogus
      };

      struct ParsedRr
      {
         AnsiString owner;             // lowercase, dotted, "" = root
         unsigned short type = 0;
         unsigned short rr_class = 0;
         unsigned long ttl = 0;
         std::vector<unsigned char> rdata;
         size_t rdata_offset = 0;      // offset of rdata within the packet
      };

      struct DnsResponse
      {
         std::vector<unsigned char> packet;
         int rcode = -1;
         std::vector<ParsedRr> answers;
      };

      struct RrsigInfo
      {
         unsigned short type_covered = 0;
         unsigned char algorithm = 0;
         unsigned char labels = 0;
         unsigned long original_ttl = 0;
         unsigned long expiration = 0;
         unsigned long inception = 0;
         unsigned short key_tag = 0;
         AnsiString signer;            // lowercase dotted
         std::vector<unsigned char> signature;
      };

      struct DsInfo
      {
         unsigned short key_tag = 0;
         unsigned char algorithm = 0;
         unsigned char digest_type = 0;
         std::vector<unsigned char> digest;
      };

      struct ZoneEntry
      {
         InternalStatus status = InternalStatus::Bogus;
         std::vector<std::vector<unsigned char>> dnskey_rdatas;
         time_t expires = 0;
      };

      boost::recursive_mutex zone_cache_mutex;
      std::map<AnsiString, ZoneEntry> zone_cache;

      // ----------------------------------------------------------------
      // Small helpers
      // ----------------------------------------------------------------

      void Push16(std::vector<unsigned char> &out, unsigned short value)
      {
         out.push_back(static_cast<unsigned char>(value >> 8));
         out.push_back(static_cast<unsigned char>(value & 0xFF));
      }

      void Push32(std::vector<unsigned char> &out, unsigned long value)
      {
         out.push_back(static_cast<unsigned char>((value >> 24) & 0xFF));
         out.push_back(static_cast<unsigned char>((value >> 16) & 0xFF));
         out.push_back(static_cast<unsigned char>((value >> 8) & 0xFF));
         out.push_back(static_cast<unsigned char>(value & 0xFF));
      }

      unsigned long Read32(const unsigned char *data)
      {
         return (static_cast<unsigned long>(data[0]) << 24) |
                (static_cast<unsigned long>(data[1]) << 16) |
                (static_cast<unsigned long>(data[2]) << 8) |
                 static_cast<unsigned long>(data[3]);
      }

      int CountLabels(const AnsiString &name)
      {
         if (name.IsEmpty())
            return 0;

         AnsiString copy = name;
         std::vector<AnsiString> labels = StringParser::SplitString(copy, ".");
         return static_cast<int>(labels.size());
      }

      AnsiString RightmostLabels(const AnsiString &name, int count)
      {
         AnsiString copy = name;
         std::vector<AnsiString> labels = StringParser::SplitString(copy, ".");

         AnsiString result;
         for (size_t i = labels.size() - static_cast<size_t>(count); i < labels.size(); i++)
         {
            if (!result.IsEmpty())
               result += ".";
            result += labels[i];
         }

         return result;
      }

      bool IsSubdomainOrEqual(const AnsiString &name, const AnsiString &zone)
      {
         if (zone.IsEmpty())
            return true;

         if (name == zone)
            return true;

         AnsiString suffix = ".";
         suffix += zone;

         if (name.GetLength() <= suffix.GetLength())
            return false;

         return name.Mid(name.GetLength() - suffix.GetLength()) == suffix;
      }

      // RFC 1982 serial-number time comparison.
      bool SignatureTimeValid(unsigned long inception, unsigned long expiration)
      {
         unsigned long now = static_cast<unsigned long>(time(nullptr) & 0xFFFFFFFF);

         if (static_cast<long>(now - inception) < 0)
            return false;

         if (static_cast<long>(expiration - now) < 0)
            return false;

         return true;
      }

      bool HexToBytes(const AnsiString &hex, std::vector<unsigned char> &out)
      {
         out.clear();

         if (hex.GetLength() % 2 != 0)
            return false;

         for (int i = 0; i < hex.GetLength(); i += 2)
         {
            int value = 0;

            for (int j = 0; j < 2; j++)
            {
               char c = hex[i + j];
               value <<= 4;

               if (c >= '0' && c <= '9')
                  value |= c - '0';
               else if (c >= 'a' && c <= 'f')
                  value |= c - 'a' + 10;
               else if (c >= 'A' && c <= 'F')
                  value |= c - 'A' + 10;
               else
                  return false;
            }

            out.push_back(static_cast<unsigned char>(value));
         }

         return true;
      }

      // ----------------------------------------------------------------
      // Wire format
      // ----------------------------------------------------------------

      bool AppendName(std::vector<unsigned char> &out, const AnsiString &dottedName)
      {
         if (!dottedName.IsEmpty())
         {
            AnsiString copy = dottedName;
            std::vector<AnsiString> labels = StringParser::SplitString(copy, ".");

            for (const AnsiString &label : labels)
            {
               if (label.IsEmpty() || label.GetLength() > 63)
                  return false;

               out.push_back(static_cast<unsigned char>(label.GetLength()));

               for (int i = 0; i < label.GetLength(); i++)
               {
                  char c = label[i];
                  if (c >= 'A' && c <= 'Z')
                     c = static_cast<char>(c - 'A' + 'a');

                  out.push_back(static_cast<unsigned char>(c));
               }
            }
         }

         out.push_back(0);
         return true;
      }

      // Reads a possibly compressed name. offset is advanced past the
      // name's wire representation at its original location.
      bool ReadName(const std::vector<unsigned char> &packet, size_t &offset, AnsiString &name)
      {
         name = "";

         size_t position = offset;
         bool jumped = false;
         int safety = 0;

         for (;;)
         {
            if (++safety > 128 || position >= packet.size())
               return false;

            unsigned char length = packet[position];

            if (length == 0)
            {
               if (!jumped)
                  offset = position + 1;
               return true;
            }

            if ((length & 0xC0) == 0xC0)
            {
               if (position + 1 >= packet.size())
                  return false;

               size_t target = (static_cast<size_t>(length & 0x3F) << 8) | packet[position + 1];

               if (!jumped)
                  offset = position + 2;

               if (target >= position)
                  return false; // pointers must point backwards

               position = target;
               jumped = true;
               continue;
            }

            if (length > 63 || position + 1 + length > packet.size())
               return false;

            if (!name.IsEmpty())
               name += ".";

            for (size_t i = 0; i < length; i++)
            {
               char c = static_cast<char>(packet[position + 1 + i]);
               if (c >= 'A' && c <= 'Z')
                  c = static_cast<char>(c - 'A' + 'a');
               name += c;
            }

            position += 1 + static_cast<size_t>(length);
         }
      }

      bool ParseResponse(const std::vector<unsigned char> &packet, DnsResponse &out)
      {
         if (packet.size() < 12)
            return false;

         if ((packet[2] & 0x80) == 0)
            return false; // not a response

         out.packet = packet;
         out.rcode = packet[3] & 0x0F;
         out.answers.clear();

         size_t questionCount = (static_cast<size_t>(packet[4]) << 8) | packet[5];
         size_t answerCount = (static_cast<size_t>(packet[6]) << 8) | packet[7];

         size_t offset = 12;

         AnsiString scratch;
         for (size_t i = 0; i < questionCount; i++)
         {
            if (!ReadName(packet, offset, scratch))
               return false;

            offset += 4;
            if (offset > packet.size())
               return false;
         }

         for (size_t i = 0; i < answerCount; i++)
         {
            ParsedRr record;

            if (!ReadName(packet, offset, record.owner))
               return false;

            if (offset + 10 > packet.size())
               return false;

            record.type = (static_cast<unsigned short>(packet[offset]) << 8) | packet[offset + 1];
            record.rr_class = (static_cast<unsigned short>(packet[offset + 2]) << 8) | packet[offset + 3];
            record.ttl = Read32(packet.data() + offset + 4);

            size_t rdataLength = (static_cast<size_t>(packet[offset + 8]) << 8) | packet[offset + 9];

            offset += 10;

            if (offset + rdataLength > packet.size())
               return false;

            record.rdata_offset = offset;
            record.rdata.assign(packet.begin() + offset, packet.begin() + offset + rdataLength);

            offset += rdataLength;

            out.answers.push_back(record);
         }

         return true;
      }

      bool ParseRrsig(const DnsResponse &response, const ParsedRr &record, RrsigInfo &out)
      {
         if (record.rdata.size() < 20)
            return false;

         const unsigned char *data = record.rdata.data();

         out.type_covered = (static_cast<unsigned short>(data[0]) << 8) | data[1];
         out.algorithm = data[2];
         out.labels = data[3];
         out.original_ttl = Read32(data + 4);
         out.expiration = Read32(data + 8);
         out.inception = Read32(data + 12);
         out.key_tag = (static_cast<unsigned short>(data[16]) << 8) | data[17];

         size_t nameOffset = record.rdata_offset + 18;
         if (!ReadName(response.packet, nameOffset, out.signer))
            return false;

         size_t signatureStart = nameOffset - record.rdata_offset;
         if (signatureStart >= record.rdata.size())
            return false;

         out.signature.assign(record.rdata.begin() + signatureStart, record.rdata.end());
         return true;
      }

      bool GetCanonicalRdata(const DnsResponse &response, const ParsedRr &record, std::vector<unsigned char> &out)
      {
         if (record.type == DnsTypeCname)
         {
            AnsiString target;
            size_t offset = record.rdata_offset;
            if (!ReadName(response.packet, offset, target))
               return false;

            out.clear();
            return AppendName(out, target);
         }

         out = record.rdata;
         return true;
      }

      // ----------------------------------------------------------------
      // Transport
      // ----------------------------------------------------------------

      bool GetDnsServers(std::vector<sockaddr_in> &servers)
      {
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

      bool BuildQuery(const AnsiString &name, unsigned short queryType, std::vector<unsigned char> &query,
                      unsigned char transactionId[2])
      {
         if (RAND_bytes(transactionId, 2) != 1)
         {
            transactionId[0] = static_cast<unsigned char>(GetTickCount() & 0xFF);
            transactionId[1] = static_cast<unsigned char>((GetTickCount() >> 8) & 0xFF);
         }

         query.clear();
         query.reserve(64 + name.GetLength());

         query.push_back(transactionId[0]);
         query.push_back(transactionId[1]);
         query.push_back(0x01); // RD
         query.push_back(0x10); // CD - we validate ourselves
         query.push_back(0x00); query.push_back(0x01); // QDCOUNT 1
         query.push_back(0x00); query.push_back(0x00); // ANCOUNT 0
         query.push_back(0x00); query.push_back(0x00); // NSCOUNT 0
         query.push_back(0x00); query.push_back(0x01); // ARCOUNT 1 (OPT)

         if (!AppendName(query, name))
            return false;

         Push16(query, queryType);
         Push16(query, 1); // IN

         // EDNS0 OPT with DO bit, UDP payload size 1232.
         query.push_back(0x00);                        // root name
         Push16(query, 41);                            // TYPE OPT
         Push16(query, 1232);                          // CLASS = UDP size
         query.push_back(0x00);                        // extended RCODE
         query.push_back(0x00);                        // version
         query.push_back(0x80); query.push_back(0x00); // flags: DO
         Push16(query, 0);                             // RDLEN

         return true;
      }

      bool RunUdpQuery(const sockaddr_in &server, const std::vector<unsigned char> &query,
                       std::vector<unsigned char> &response, bool &truncated)
      {
         truncated = false;

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
            unsigned char receiveBuffer[2048];

            sockaddr_in fromAddress = {};
            int fromLength = sizeof(fromAddress);

            int bytesReceived = recvfrom(udpSocket, reinterpret_cast<char*>(receiveBuffer), sizeof(receiveBuffer), 0,
                                         reinterpret_cast<sockaddr*>(&fromAddress), &fromLength);

            if (bytesReceived >= 12 &&
                receiveBuffer[0] == query[0] &&
                receiveBuffer[1] == query[1] &&
                fromAddress.sin_addr.s_addr == server.sin_addr.s_addr)
            {
               truncated = (receiveBuffer[2] & 0x02) != 0;
               response.assign(receiveBuffer, receiveBuffer + bytesReceived);
               succeeded = true;
            }
         }

         closesocket(udpSocket);
         return succeeded;
      }

      bool RunTcpQuery(const sockaddr_in &server, const std::vector<unsigned char> &query,
                       std::vector<unsigned char> &response)
      {
         SOCKET tcpSocket = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
         if (tcpSocket == INVALID_SOCKET)
            return false;

         DWORD timeout = DnsTimeoutMilliseconds;
         setsockopt(tcpSocket, SOL_SOCKET, SO_RCVTIMEO, (const char*) &timeout, sizeof(timeout));
         setsockopt(tcpSocket, SOL_SOCKET, SO_SNDTIMEO, (const char*) &timeout, sizeof(timeout));

         bool succeeded = false;

         if (connect(tcpSocket, reinterpret_cast<const sockaddr*>(&server), sizeof(server)) == 0)
         {
            std::vector<unsigned char> framed;
            framed.reserve(query.size() + 2);
            Push16(framed, static_cast<unsigned short>(query.size()));
            framed.insert(framed.end(), query.begin(), query.end());

            if (send(tcpSocket, reinterpret_cast<const char*>(framed.data()), static_cast<int>(framed.size()), 0) ==
                static_cast<int>(framed.size()))
            {
               unsigned char lengthBytes[2];
               int received = recv(tcpSocket, reinterpret_cast<char*>(lengthBytes), 2, 0);

               if (received == 2)
               {
                  size_t expected = (static_cast<size_t>(lengthBytes[0]) << 8) | lengthBytes[1];

                  if (expected >= 12 && expected <= 65535)
                  {
                     response.resize(expected);

                     size_t total = 0;
                     while (total < expected)
                     {
                        int chunk = recv(tcpSocket, reinterpret_cast<char*>(response.data() + total),
                                         static_cast<int>(expected - total), 0);
                        if (chunk <= 0)
                           break;

                        total += static_cast<size_t>(chunk);
                     }

                     if (total == expected &&
                         response[0] == query[0] &&
                         response[1] == query[1])
                     {
                        succeeded = true;
                     }
                  }
               }
            }
         }

         closesocket(tcpSocket);
         return succeeded;
      }

      // Returns true with a parsed NOERROR/NXDOMAIN response; false on
      // any transport-level failure.
      bool RunQuery(const AnsiString &name, unsigned short queryType, DnsResponse &out)
      {
         std::vector<unsigned char> query;
         unsigned char transactionId[2];

         if (!BuildQuery(name, queryType, query, transactionId))
            return false;

         std::vector<sockaddr_in> servers;
         if (!GetDnsServers(servers))
            return false;

         for (const sockaddr_in &server : servers)
         {
            std::vector<unsigned char> packet;
            bool truncated = false;

            bool received = RunUdpQuery(server, query, packet, truncated);

            if (received && truncated)
               received = RunTcpQuery(server, query, packet);

            if (!received)
               continue;

            DnsResponse response;
            if (!ParseResponse(packet, response))
               continue;

            if (response.rcode != 0 && response.rcode != 3)
               continue;

            out = response;
            return true;
         }

         return false;
      }

      // ----------------------------------------------------------------
      // Cryptography
      // ----------------------------------------------------------------

      unsigned short ComputeKeyTag(const std::vector<unsigned char> &rdata)
      {
         unsigned long accumulator = 0;

         for (size_t i = 0; i < rdata.size(); i++)
            accumulator += (i & 1) ? rdata[i] : (static_cast<unsigned long>(rdata[i]) << 8);

         accumulator += (accumulator >> 16) & 0xFFFF;

         return static_cast<unsigned short>(accumulator & 0xFFFF);
      }

      EVP_PKEY* BuildKeyFromParams(const char *keyType, OSSL_PARAM *params)
      {
         EVP_PKEY_CTX *context = EVP_PKEY_CTX_new_from_name(nullptr, keyType, nullptr);
         if (context == nullptr)
            return nullptr;

         EVP_PKEY *key = nullptr;

         if (EVP_PKEY_fromdata_init(context) != 1 ||
             EVP_PKEY_fromdata(context, &key, EVP_PKEY_PUBLIC_KEY, params) != 1)
         {
            key = nullptr;
         }

         EVP_PKEY_CTX_free(context);
         return key;
      }

      EVP_PKEY* BuildRsaKey(const unsigned char *keyData, size_t keyLength)
      {
         if (keyLength < 3)
            return nullptr;

         size_t exponentLength = keyData[0];
         size_t offset = 1;

         if (exponentLength == 0)
         {
            if (keyLength < 4)
               return nullptr;

            exponentLength = (static_cast<size_t>(keyData[1]) << 8) | keyData[2];
            offset = 3;
         }

         if (exponentLength == 0 || offset + exponentLength >= keyLength)
            return nullptr;

         BIGNUM *exponent = BN_bin2bn(keyData + offset, static_cast<int>(exponentLength), nullptr);
         BIGNUM *modulus = BN_bin2bn(keyData + offset + exponentLength,
                                     static_cast<int>(keyLength - offset - exponentLength), nullptr);

         EVP_PKEY *key = nullptr;

         OSSL_PARAM_BLD *builder = OSSL_PARAM_BLD_new();
         if (builder != nullptr && exponent != nullptr && modulus != nullptr &&
             OSSL_PARAM_BLD_push_BN(builder, OSSL_PKEY_PARAM_RSA_N, modulus) == 1 &&
             OSSL_PARAM_BLD_push_BN(builder, OSSL_PKEY_PARAM_RSA_E, exponent) == 1)
         {
            OSSL_PARAM *params = OSSL_PARAM_BLD_to_param(builder);
            if (params != nullptr)
            {
               key = BuildKeyFromParams("RSA", params);
               OSSL_PARAM_free(params);
            }
         }

         if (builder != nullptr)
            OSSL_PARAM_BLD_free(builder);
         if (modulus != nullptr)
            BN_free(modulus);
         if (exponent != nullptr)
            BN_free(exponent);

         return key;
      }

      EVP_PKEY* BuildEcKey(const unsigned char *keyData, size_t keyLength, const char *groupName, size_t coordinateSize)
      {
         if (keyLength != coordinateSize * 2)
            return nullptr;

         std::vector<unsigned char> point;
         point.reserve(1 + keyLength);
         point.push_back(0x04);
         point.insert(point.end(), keyData, keyData + keyLength);

         EVP_PKEY *key = nullptr;

         OSSL_PARAM_BLD *builder = OSSL_PARAM_BLD_new();
         if (builder != nullptr &&
             OSSL_PARAM_BLD_push_utf8_string(builder, OSSL_PKEY_PARAM_GROUP_NAME, groupName, 0) == 1 &&
             OSSL_PARAM_BLD_push_octet_string(builder, OSSL_PKEY_PARAM_PUB_KEY, point.data(), point.size()) == 1)
         {
            OSSL_PARAM *params = OSSL_PARAM_BLD_to_param(builder);
            if (params != nullptr)
            {
               key = BuildKeyFromParams("EC", params);
               OSSL_PARAM_free(params);
            }
         }

         if (builder != nullptr)
            OSSL_PARAM_BLD_free(builder);

         return key;
      }

      bool EcdsaRawToDer(const std::vector<unsigned char> &raw, std::vector<unsigned char> &der)
      {
         if (raw.empty() || raw.size() % 2 != 0)
            return false;

         size_t half = raw.size() / 2;

         ECDSA_SIG *signature = ECDSA_SIG_new();
         if (signature == nullptr)
            return false;

         BIGNUM *r = BN_bin2bn(raw.data(), static_cast<int>(half), nullptr);
         BIGNUM *s = BN_bin2bn(raw.data() + half, static_cast<int>(half), nullptr);

         bool succeeded = false;

         if (r != nullptr && s != nullptr && ECDSA_SIG_set0(signature, r, s) == 1)
         {
            // signature owns r and s now.
            int derLength = i2d_ECDSA_SIG(signature, nullptr);
            if (derLength > 0)
            {
               der.resize(static_cast<size_t>(derLength));
               unsigned char *writePointer = der.data();
               i2d_ECDSA_SIG(signature, &writePointer);
               succeeded = true;
            }
         }
         else
         {
            if (r != nullptr)
               BN_free(r);
            if (s != nullptr)
               BN_free(s);
         }

         ECDSA_SIG_free(signature);
         return succeeded;
      }

      bool VerifySignature(unsigned char algorithm, const unsigned char *keyData, size_t keyLength,
                           const std::vector<unsigned char> &signedData,
                           const std::vector<unsigned char> &signature)
      {
         EVP_PKEY *key = nullptr;
         const EVP_MD *digest = nullptr;
         std::vector<unsigned char> derSignature;
         const unsigned char *signatureData = signature.data();
         size_t signatureLength = signature.size();

         switch (algorithm)
         {
         case 8: // RSA/SHA-256
            key = BuildRsaKey(keyData, keyLength);
            digest = EVP_sha256();
            break;

         case 10: // RSA/SHA-512
            key = BuildRsaKey(keyData, keyLength);
            digest = EVP_sha512();
            break;

         case 13: // ECDSA P-256 / SHA-256
            key = BuildEcKey(keyData, keyLength, "prime256v1", 32);
            digest = EVP_sha256();
            if (!EcdsaRawToDer(signature, derSignature))
               key = nullptr;
            signatureData = derSignature.data();
            signatureLength = derSignature.size();
            break;

         case 14: // ECDSA P-384 / SHA-384
            key = BuildEcKey(keyData, keyLength, "secp384r1", 48);
            digest = EVP_sha384();
            if (!EcdsaRawToDer(signature, derSignature))
               key = nullptr;
            signatureData = derSignature.data();
            signatureLength = derSignature.size();
            break;

         case 15: // Ed25519
            if (keyLength != 32 || signature.size() != 64)
               return false;
            key = EVP_PKEY_new_raw_public_key(EVP_PKEY_ED25519, nullptr, keyData, keyLength);
            digest = nullptr;
            break;

         default:
            return false; // unsupported algorithm
         }

         if (key == nullptr)
            return false;

         bool verified = false;

         EVP_MD_CTX *context = EVP_MD_CTX_new();
         if (context != nullptr)
         {
            if (EVP_DigestVerifyInit(context, nullptr, digest, nullptr, key) == 1)
            {
               verified = EVP_DigestVerify(context, signatureData, signatureLength,
                                           signedData.data(), signedData.size()) == 1;
            }

            EVP_MD_CTX_free(context);
         }

         EVP_PKEY_free(key);
         return verified;
      }

      // Verifies one RRSIG over an RRset (RFC 4034 section 3.1.8.1).
      bool VerifyRrsigOverSet(const DnsResponse &response, const std::vector<ParsedRr> &rrset,
                              const RrsigInfo &sig, const std::vector<unsigned char> &dnskeyRdata)
      {
         if (rrset.empty() || dnskeyRdata.size() < 5)
            return false;

         if (!SignatureTimeValid(sig.inception, sig.expiration))
            return false;

         const AnsiString &owner = rrset[0].owner;
         int ownerLabels = CountLabels(owner);

         if (sig.labels > ownerLabels)
            return false;

         AnsiString canonicalOwner = owner;
         if (sig.labels < ownerLabels)
         {
            canonicalOwner = "*.";
            canonicalOwner += RightmostLabels(owner, sig.labels);
         }

         // Signed data: RRSIG RDATA (sans signature, signer canonical)
         // followed by the canonical, sorted RRs.
         std::vector<unsigned char> signedData;
         signedData.reserve(512);

         Push16(signedData, sig.type_covered);
         signedData.push_back(sig.algorithm);
         signedData.push_back(sig.labels);
         Push32(signedData, sig.original_ttl);
         Push32(signedData, sig.expiration);
         Push32(signedData, sig.inception);
         Push16(signedData, sig.key_tag);

         if (!AppendName(signedData, sig.signer))
            return false;

         std::vector<std::vector<unsigned char>> canonicalRdatas;
         for (const ParsedRr &record : rrset)
         {
            std::vector<unsigned char> rdata;
            if (!GetCanonicalRdata(response, record, rdata))
               return false;

            canonicalRdatas.push_back(rdata);
         }

         std::sort(canonicalRdatas.begin(), canonicalRdatas.end());

         for (const std::vector<unsigned char> &rdata : canonicalRdatas)
         {
            if (!AppendName(signedData, canonicalOwner))
               return false;

            Push16(signedData, rrset[0].type);
            Push16(signedData, rrset[0].rr_class);
            Push32(signedData, sig.original_ttl);
            Push16(signedData, static_cast<unsigned short>(rdata.size()));
            signedData.insert(signedData.end(), rdata.begin(), rdata.end());
         }

         return VerifySignature(sig.algorithm, dnskeyRdata.data() + 4, dnskeyRdata.size() - 4,
                                signedData, sig.signature);
      }

      bool DnskeyMatchesDs(const AnsiString &zone, const std::vector<unsigned char> &dnskeyRdata, const DsInfo &ds)
      {
         std::vector<unsigned char> input;
         input.reserve(zone.GetLength() + 2 + dnskeyRdata.size());

         if (!AppendName(input, zone))
            return false;

         input.insert(input.end(), dnskeyRdata.begin(), dnskeyRdata.end());

         unsigned char computed[SHA512_DIGEST_LENGTH];
         size_t digestLength = 0;

         if (ds.digest_type == 2)
         {
            SHA256(input.data(), input.size(), computed);
            digestLength = SHA256_DIGEST_LENGTH;
         }
         else if (ds.digest_type == 4)
         {
            SHA384(input.data(), input.size(), computed);
            digestLength = SHA384_DIGEST_LENGTH;
         }
         else
         {
            return false; // unsupported digest (including SHA-1)
         }

         if (ds.digest.size() != digestLength)
            return false;

         return memcmp(ds.digest.data(), computed, digestLength) == 0;
      }

      // ----------------------------------------------------------------
      // Trust anchors
      // ----------------------------------------------------------------

      std::vector<DsInfo> GetRootTrustAnchors()
      {
         std::vector<DsInfo> anchors;

         // Optional override / extension from hMailServer.ini, format:
         // DnssecTrustAnchors=20326 8 2 E06D...;38696 8 2 683D...
         AnsiString configured = IniFileSettings::Instance()->GetDnssecTrustAnchors();
         if (!configured.IsEmpty())
         {
            std::vector<AnsiString> entries = StringParser::SplitString(configured, ";");
            for (AnsiString entry : entries)
            {
               entry.Trim();
               if (entry.IsEmpty())
                  continue;

               std::vector<AnsiString> fields = StringParser::SplitString(entry, " ");

               std::vector<AnsiString> cleanFields;
               for (AnsiString field : fields)
               {
                  field.Trim();
                  if (!field.IsEmpty())
                     cleanFields.push_back(field);
               }

               if (cleanFields.size() != 4)
                  continue;

               DsInfo anchor;
               anchor.key_tag = static_cast<unsigned short>(atoi(cleanFields[0].c_str()));
               anchor.algorithm = static_cast<unsigned char>(atoi(cleanFields[1].c_str()));
               anchor.digest_type = static_cast<unsigned char>(atoi(cleanFields[2].c_str()));

               if (HexToBytes(cleanFields[3], anchor.digest))
                  anchors.push_back(anchor);
            }
         }

         if (!anchors.empty())
            return anchors;

         // Built-in IANA root key signing keys (root-anchors.xml).
         struct BuiltinAnchor { unsigned short tag; const char *digest; };
         const BuiltinAnchor builtin[] =
         {
            { 20326, "E06D44B80B8F1D39A95C0B0D7C65D08458E880409BBC683457104237C7F8EC8D" }, // KSK-2017
            { 38696, "683D2D0ACB8C9B712A1948B27F741219298D0A450D612C483AF444A4C0FB2B16" }  // KSK-2024
         };

         for (const BuiltinAnchor &entry : builtin)
         {
            DsInfo anchor;
            anchor.key_tag = entry.tag;
            anchor.algorithm = 8;     // RSA/SHA-256
            anchor.digest_type = 2;   // SHA-256

            if (HexToBytes(AnsiString(entry.digest), anchor.digest))
               anchors.push_back(anchor);
         }

         return anchors;
      }

      // ----------------------------------------------------------------
      // Chain validation
      // ----------------------------------------------------------------

      InternalStatus ValidateZoneKeys(const AnsiString &zone, int depth, std::vector<std::vector<unsigned char>> &keys);

      // Fetches and validates the DS RRset for a zone (served by the
      // parent). Secure fills dsRecords; Insecure means an unsigned
      // delegation was found.
      InternalStatus FetchDsRecords(const AnsiString &zone, int depth, std::vector<DsInfo> &dsRecords)
      {
         DnsResponse response;
         if (!RunQuery(zone, DnsTypeDs, response))
            return InternalStatus::Insecure; // transport failure - degrade

         std::vector<ParsedRr> dsRrset;
         std::vector<RrsigInfo> signatures;

         for (const ParsedRr &record : response.answers)
         {
            if (record.owner != zone)
               continue;

            if (record.type == DnsTypeDs)
            {
               dsRrset.push_back(record);
            }
            else if (record.type == DnsTypeRrsig)
            {
               RrsigInfo info;
               if (ParseRrsig(response, record, info) && info.type_covered == DnsTypeDs)
                  signatures.push_back(info);
            }
         }

         if (dsRrset.empty())
            return InternalStatus::Insecure; // no DS - unsigned delegation

         bool sawInsecureParent = false;

         for (const RrsigInfo &sig : signatures)
         {
            // The DS RRset is signed by the parent zone, which must be a
            // proper ancestor of the child zone.
            if (sig.signer == zone || !IsSubdomainOrEqual(zone, sig.signer))
               continue;

            std::vector<std::vector<unsigned char>> parentKeys;
            InternalStatus parentStatus = ValidateZoneKeys(sig.signer, depth + 1, parentKeys);

            if (parentStatus == InternalStatus::Insecure)
            {
               sawInsecureParent = true;
               continue;
            }

            if (parentStatus == InternalStatus::Bogus)
               continue;

            for (const std::vector<unsigned char> &keyRdata : parentKeys)
            {
               if (keyRdata.size() < 5)
                  continue;

               if (keyRdata[3] != sig.algorithm || ComputeKeyTag(keyRdata) != sig.key_tag)
                  continue;

               if (VerifyRrsigOverSet(response, dsRrset, sig, keyRdata))
               {
                  for (const ParsedRr &record : dsRrset)
                  {
                     if (record.rdata.size() < 5)
                        continue;

                     DsInfo info;
                     info.key_tag = (static_cast<unsigned short>(record.rdata[0]) << 8) | record.rdata[1];
                     info.algorithm = record.rdata[2];
                     info.digest_type = record.rdata[3];
                     info.digest.assign(record.rdata.begin() + 4, record.rdata.end());

                     dsRecords.push_back(info);
                  }

                  return dsRecords.empty() ? InternalStatus::Insecure : InternalStatus::Secure;
               }
            }
         }

         return sawInsecureParent ? InternalStatus::Insecure : InternalStatus::Bogus;
      }

      // Returns the validated DNSKEY RRset of a zone, walking up to the
      // root trust anchors.
      InternalStatus ValidateZoneKeys(const AnsiString &zone, int depth, std::vector<std::vector<unsigned char>> &keys)
      {
         if (depth > MaxChainDepth)
            return InternalStatus::Bogus;

         {
            boost::lock_guard<boost::recursive_mutex> guard(zone_cache_mutex);

            auto cached = zone_cache.find(zone);
            if (cached != zone_cache.end() && cached->second.expires > time(nullptr))
            {
               keys = cached->second.dnskey_rdatas;
               return cached->second.status;
            }
         }

         InternalStatus result = InternalStatus::Bogus;
         keys.clear();

         // Establish the trust anchors for this zone: the root uses the
         // built-in anchors, everything else a validated DS RRset.
         std::vector<DsInfo> anchors;

         if (zone.IsEmpty())
         {
            anchors = GetRootTrustAnchors();
         }
         else
         {
            InternalStatus dsStatus = FetchDsRecords(zone, depth, anchors);
            if (dsStatus != InternalStatus::Secure)
               result = dsStatus;
         }

         if (!anchors.empty())
         {
            DnsResponse response;
            if (RunQuery(zone, DnsTypeDnskey, response))
            {
               std::vector<ParsedRr> dnskeyRrset;
               std::vector<RrsigInfo> signatures;

               for (const ParsedRr &record : response.answers)
               {
                  if (record.owner != zone)
                     continue;

                  if (record.type == DnsTypeDnskey)
                  {
                     dnskeyRrset.push_back(record);
                  }
                  else if (record.type == DnsTypeRrsig)
                  {
                     RrsigInfo info;
                     if (ParseRrsig(response, record, info) &&
                         info.type_covered == DnsTypeDnskey && info.signer == zone)
                     {
                        signatures.push_back(info);
                     }
                  }
               }

               // Find a key that matches a trust anchor and signs the
               // DNSKEY RRset; that proves the whole RRset.
               bool trusted = false;

               for (const DsInfo &anchor : anchors)
               {
                  if (trusted)
                     break;

                  for (const ParsedRr &keyRecord : dnskeyRrset)
                  {
                     if (trusted)
                        break;

                     const std::vector<unsigned char> &rdata = keyRecord.rdata;
                     if (rdata.size() < 5)
                        continue;

                     unsigned short keyFlags = (static_cast<unsigned short>(rdata[0]) << 8) | rdata[1];

                     if ((keyFlags & 0x0100) == 0)  // ZONE flag required
                        continue;
                     if ((keyFlags & 0x0080) != 0)  // revoked (RFC 5011)
                        continue;
                     if (rdata[2] != 3)             // protocol
                        continue;
                     if (rdata[3] != anchor.algorithm)
                        continue;
                     if (ComputeKeyTag(rdata) != anchor.key_tag)
                        continue;
                     if (!DnskeyMatchesDs(zone, rdata, anchor))
                        continue;

                     for (const RrsigInfo &sig : signatures)
                     {
                        if (sig.key_tag != anchor.key_tag || sig.algorithm != anchor.algorithm)
                           continue;

                        if (VerifyRrsigOverSet(response, dnskeyRrset, sig, rdata))
                        {
                           trusted = true;
                           break;
                        }
                     }
                  }
               }

               if (trusted)
               {
                  for (const ParsedRr &keyRecord : dnskeyRrset)
                  {
                     const std::vector<unsigned char> &rdata = keyRecord.rdata;
                     if (rdata.size() < 5)
                        continue;

                     unsigned short keyFlags = (static_cast<unsigned short>(rdata[0]) << 8) | rdata[1];

                     if ((keyFlags & 0x0100) == 0 || (keyFlags & 0x0080) != 0 || rdata[2] != 3)
                        continue;

                     keys.push_back(rdata);
                  }

                  result = keys.empty() ? InternalStatus::Bogus : InternalStatus::Secure;
               }
               else if (result != InternalStatus::Insecure)
               {
                  result = InternalStatus::Bogus;
               }
            }
            else if (result != InternalStatus::Insecure)
            {
               result = InternalStatus::Insecure; // transport failure - degrade
            }
         }

         // Cache the outcome.
         int cacheSeconds = 60;
         if (result == InternalStatus::Secure)
            cacheSeconds = 3600;
         else if (result == InternalStatus::Insecure)
            cacheSeconds = 300;

         {
            boost::lock_guard<boost::recursive_mutex> guard(zone_cache_mutex);

            ZoneEntry entry;
            entry.status = result;
            entry.dnskey_rdatas = keys;
            entry.expires = time(nullptr) + cacheSeconds;

            zone_cache[zone] = entry;

            // Keep the cache bounded.
            if (zone_cache.size() > 512)
               zone_cache.clear();
         }

         return result;
      }

      // Validates an answer RRset against its RRSIGs.
      InternalStatus ValidateRrset(const DnsResponse &response, const std::vector<ParsedRr> &rrset,
                                   const std::vector<RrsigInfo> &signatures)
      {
         if (rrset.empty())
            return InternalStatus::Insecure;

         bool sawInsecureZone = false;
         bool sawApplicableSignature = false;

         for (const RrsigInfo &sig : signatures)
         {
            if (sig.type_covered != rrset[0].type)
               continue;

            if (!IsSubdomainOrEqual(rrset[0].owner, sig.signer))
               continue;

            sawApplicableSignature = true;

            std::vector<std::vector<unsigned char>> zoneKeys;
            InternalStatus zoneStatus = ValidateZoneKeys(sig.signer, 0, zoneKeys);

            if (zoneStatus == InternalStatus::Insecure)
            {
               sawInsecureZone = true;
               continue;
            }

            if (zoneStatus == InternalStatus::Bogus)
               continue;

            for (const std::vector<unsigned char> &keyRdata : zoneKeys)
            {
               if (keyRdata.size() < 5)
                  continue;

               if (keyRdata[3] != sig.algorithm || ComputeKeyTag(keyRdata) != sig.key_tag)
                  continue;

               if (VerifyRrsigOverSet(response, rrset, sig, keyRdata))
                  return InternalStatus::Secure;
            }
         }

         if (!sawApplicableSignature || sawInsecureZone)
            return InternalStatus::Insecure;

         return InternalStatus::Bogus;
      }
   }

   DnssecResolver::ChainStatus
   DnssecResolver::QueryValidatedRrset_(const AnsiString &query_name, unsigned short query_type,
                                        std::vector<std::vector<unsigned char>> &rdatas)
   {
      rdatas.clear();

      AnsiString queryName = query_name;
      queryName.MakeLower();

      // The final answer can only be Secure if every CNAME link in the
      // chain was validated as well (RFC 4035 section 5).
      bool aliasChainSecure = true;

      for (int hop = 0; hop <= MaxCnameHops; hop++)
      {
         DnsResponse response;
         if (!RunQuery(queryName, query_type, response))
         {
            LOG_DEBUG("DNSSEC: Lookup for " + String(queryName) + " failed at the transport level.");
            return ChainStatus::Insecure;
         }

         std::vector<ParsedRr> typedRrset;
         std::vector<ParsedRr> cnameRrset;
         std::vector<RrsigInfo> signatures;

         for (const ParsedRr &record : response.answers)
         {
            if (record.owner != queryName)
               continue;

            if (record.type == query_type)
            {
               typedRrset.push_back(record);
            }
            else if (record.type == DnsTypeCname)
            {
               cnameRrset.push_back(record);
            }
            else if (record.type == DnsTypeRrsig)
            {
               RrsigInfo info;
               if (ParseRrsig(response, record, info))
                  signatures.push_back(info);
            }
         }

         if (!typedRrset.empty())
         {
            InternalStatus status = ValidateRrset(response, typedRrset, signatures);

            if (status == InternalStatus::Bogus)
               return ChainStatus::Bogus;

            for (const ParsedRr &record : typedRrset)
               rdatas.push_back(record.rdata);

            return (status == InternalStatus::Secure && aliasChainSecure)
               ? ChainStatus::Secure : ChainStatus::Insecure;
         }

         if (!cnameRrset.empty())
         {
            // Follow the alias, but only across validated links.
            InternalStatus status = ValidateRrset(response, cnameRrset, signatures);

            if (status == InternalStatus::Bogus)
               return ChainStatus::Bogus;

            if (status == InternalStatus::Insecure)
               aliasChainSecure = false;

            AnsiString target;
            size_t offset = cnameRrset[0].rdata_offset;
            if (!ReadName(response.packet, offset, target) || target.IsEmpty())
               return ChainStatus::Insecure;

            queryName = target;
            continue;
         }

         // NOERROR/NXDOMAIN without records: nothing published.
         return ChainStatus::Insecure;
      }

      return ChainStatus::Insecure; // too many CNAME hops
   }

   DnssecResolver::ChainStatus
   DnssecResolver::QueryTlsa(const String &host_name, int port, std::vector<TlsaRecord> &records)
   {
      records.clear();

      AnsiString narrowHost = host_name;
      narrowHost.MakeLower();

      AnsiString queryName;
      queryName.Format("_%d._tcp.%hs", port, narrowHost.c_str());

      std::vector<std::vector<unsigned char>> rdatas;
      ChainStatus status = QueryValidatedRrset_(queryName, DnsTypeTlsa, rdatas);

      if (status != ChainStatus::Secure)
      {
         if (status == ChainStatus::Insecure && !rdatas.empty())
            LOG_DEBUG("DNSSEC: TLSA records for " + String(queryName) + " are not covered by a validated chain (unsigned zone or non-DNSSEC resolver). Proceeding without DANE.");

         return status;
      }

      for (const std::vector<unsigned char> &rdata : rdatas)
      {
         if (rdata.size() < 4)
            continue;

         TlsaRecord tlsa;
         tlsa.usage = rdata[0];
         tlsa.selector = rdata[1];
         tlsa.matching_type = rdata[2];
         tlsa.data.assign(reinterpret_cast<const char*>(rdata.data() + 3), rdata.size() - 3);

         records.push_back(tlsa);
      }

      return records.empty() ? ChainStatus::Insecure : ChainStatus::Secure;
   }

   DnssecResolver::ChainStatus
   DnssecResolver::QueryTxt(const String &name, std::vector<AnsiString> &texts)
   {
      texts.clear();

      AnsiString queryName = name;
      queryName.MakeLower();

      std::vector<std::vector<unsigned char>> rdatas;
      ChainStatus status = QueryValidatedRrset_(queryName, DnsTypeTxt, rdatas);

      if (status == ChainStatus::Bogus)
         return status;

      // TXT rdata is a sequence of length-prefixed character strings
      // which are concatenated to form the record value.
      for (const std::vector<unsigned char> &rdata : rdatas)
      {
         AnsiString text;
         size_t offset = 0;

         while (offset < rdata.size())
         {
            size_t segmentLength = rdata[offset];
            offset++;

            if (offset + segmentLength > rdata.size())
               break;

            text.append(reinterpret_cast<const char*>(rdata.data() + offset), segmentLength);
            offset += segmentLength;
         }

         texts.push_back(text);
      }

      return status;
   }

   // Bridge for the vendored SPF implementation (RMSPF.cpp), which uses
   // the Windows system resolver. A bogus DNSSEC chain for a TXT name
   // means the data is forged and the lookup must be treated as failed.
   bool
   DnssecTxtLookupIsBogus(const char *name)
   {
      if (!IniFileSettings::Instance()->GetDnssecValidationEnabled())
         return false;

      DnssecResolver resolver;
      std::vector<AnsiString> texts;

      return resolver.QueryTxt(String(name), texts) == DnssecResolver::ChainStatus::Bogus;
   }
}
