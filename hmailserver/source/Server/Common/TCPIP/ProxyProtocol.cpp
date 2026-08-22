// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "ProxyProtocol.h"

#include <boost/atomic.hpp>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // The exact 12-byte binary signature that opens every PROXY protocol v2
      // header, straight from the specification (section 2.2):
      // \x0D \x0A \x0D \x0A \x00 \x0D \x0A \x51 \x55 \x49 \x54 \x0A
      // ("\r\n\r\n\0\r\nQUIT\n").
      const unsigned char PROXY_V2_SIGNATURE[12] =
         { 0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A };

      // Specification section 2.1: a v1 line is at most 107 bytes including its
      // terminating CRLF. Anything longer without a LF is not a valid header.
      const size_t PROXY_V1_MAX_LINE_LENGTH = 107;

      bool ParsePortNumber(const String &value, unsigned int &port)
      {
         if (value.IsEmpty() || value.GetLength() > 5)
            return false;

         unsigned int result = 0;
         for (int i = 0; i < value.GetLength(); i++)
         {
            wchar_t c = value.GetAt(i);
            if (c < '0' || c > '9')
               return false;

            result = result * 10 + (c - '0');
         }

         if (result > 65535)
            return false;

         port = result;
         return true;
      }
   }

   ProxyProtocolParser::Status
   ProxyProtocolParser::Parse(const unsigned char *buffer, size_t available, size_t &bytes_needed, Result &result)
   {
      if (available == 0)
      {
         bytes_needed = 1;
         return StatusNeedMore;
      }

      // The first byte separates the two versions: a v1 header starts with the
      // ASCII text "PROXY", a v2 header with the binary signature whose first
      // byte is 0x0D.
      if (buffer[0] == 'P')
         return ParseVersion1_(buffer, available, bytes_needed, result);

      if (buffer[0] == 0x0D)
         return ParseVersion2_(buffer, available, bytes_needed, result);

      result.error_message = "The data does not begin with a PROXY protocol v1 or v2 header";
      return StatusInvalid;
   }

   ProxyProtocolParser::Status
   ProxyProtocolParser::ParseVersion1_(const unsigned char *buffer, size_t available, size_t &bytes_needed, Result &result)
   {
      // Find the terminating LF. Until it arrives the line length is unknown,
      // so the caller is asked for ONE byte at a time - the only request size
      // that can never consume anything beyond the header.
      size_t lineFeedIndex = 0;
      bool foundLineFeed = false;

      for (size_t i = 0; i < available; i++)
      {
         if (buffer[i] == 0x0A)
         {
            lineFeedIndex = i;
            foundLineFeed = true;
            break;
         }
      }

      if (!foundLineFeed)
      {
         if (available >= PROXY_V1_MAX_LINE_LENGTH)
         {
            result.error_message = "No CRLF within the maximum 107 bytes of a PROXY protocol v1 line";
            return StatusInvalid;
         }

         bytes_needed = 1;
         return StatusNeedMore;
      }

      if (lineFeedIndex == 0 || buffer[lineFeedIndex - 1] != 0x0D)
      {
         result.error_message = "PROXY protocol v1 line terminated by a bare LF instead of CRLF";
         return StatusInvalid;
      }

      AnsiString line((const char*) buffer, lineFeedIndex - 1);

      // The specification requires exactly one space between tokens, so empty
      // tokens (from doubled spaces, or a trailing space) are rejected.
      std::vector<AnsiString> tokens = StringParser::SplitString(line, " ");
      for (const AnsiString &token : tokens)
      {
         if (token.GetLength() == 0)
         {
            result.error_message = "Malformed spacing in PROXY protocol v1 line";
            return StatusInvalid;
         }
      }

      if (tokens.size() < 2 || tokens[0] != "PROXY")
      {
         result.error_message = "Malformed PROXY protocol v1 line";
         return StatusInvalid;
      }

      AnsiString protocolFamily = tokens[1];

      if (protocolFamily == "UNKNOWN")
      {
         // Specification: for UNKNOWN, the receiver must ignore anything
         // presented before the CRLF and must NOT use an address from it. The
         // connection continues with the real (proxy's own) endpoints.
         result.has_source_address = false;
         return StatusComplete;
      }

      if (protocolFamily != "TCP4" && protocolFamily != "TCP6")
      {
         result.error_message = "Unsupported protocol family in PROXY protocol v1 line: " + String(protocolFamily);
         return StatusInvalid;
      }

      if (tokens.size() != 6)
      {
         result.error_message = "A PROXY protocol v1 TCP line must have exactly 6 tokens";
         return StatusInvalid;
      }

      IPAddress sourceAddress;
      IPAddress destinationAddress;

      if (!sourceAddress.TryParse(tokens[2], false) ||
          !destinationAddress.TryParse(tokens[3], false))
      {
         result.error_message = "Unparsable address in PROXY protocol v1 line";
         return StatusInvalid;
      }

      IPAddress::Type expectedType = protocolFamily == "TCP4" ? IPAddress::IPV4 : IPAddress::IPV6;
      if (sourceAddress.GetType() != expectedType || destinationAddress.GetType() != expectedType)
      {
         result.error_message = "Address family mismatch in PROXY protocol v1 line";
         return StatusInvalid;
      }

      unsigned int sourcePort = 0;
      unsigned int destinationPort = 0;
      if (!ParsePortNumber(String(tokens[4]), sourcePort) ||
          !ParsePortNumber(String(tokens[5]), destinationPort))
      {
         result.error_message = "Unparsable port in PROXY protocol v1 line";
         return StatusInvalid;
      }

      result.has_source_address = true;
      result.source_address = sourceAddress;
      result.source_port = sourcePort;

      return StatusComplete;
   }

   ProxyProtocolParser::Status
   ProxyProtocolParser::ParseVersion2_(const unsigned char *buffer, size_t available, size_t &bytes_needed, Result &result)
   {
      // The fixed part of a v2 header is 16 bytes: the 12-byte signature, one
      // version/command byte, one family/transport byte and a 2-byte length.
      if (available < 16)
      {
         bytes_needed = 16 - available;
         return StatusNeedMore;
      }

      if (memcmp(buffer, PROXY_V2_SIGNATURE, sizeof(PROXY_V2_SIGNATURE)) != 0)
      {
         result.error_message = "Invalid PROXY protocol v2 signature";
         return StatusInvalid;
      }

      unsigned char versionAndCommand = buffer[12];

      if ((versionAndCommand & 0xF0) != 0x20)
      {
         result.error_message = "Unsupported PROXY protocol version";
         return StatusInvalid;
      }

      unsigned char command = versionAndCommand & 0x0F;

      // \x0 = LOCAL, \x1 = PROXY. The specification requires receivers to drop
      // connections presenting any other command.
      if (command != 0x00 && command != 0x01)
      {
         result.error_message = "Unsupported PROXY protocol v2 command";
         return StatusInvalid;
      }

      unsigned char familyAndTransport = buffer[13];
      size_t addressBlockLength = (size_t) ((buffer[14] << 8) | buffer[15]);

      // The remainder of the header (address block plus any TLV extensions,
      // e.g. HAProxy's PP2_TYPE_SSL) is 'addressBlockLength' bytes and belongs
      // to the header whatever it contains, so it is safe to request exactly.
      if (available < 16 + addressBlockLength)
      {
         bytes_needed = 16 + addressBlockLength - available;
         return StatusNeedMore;
      }

      if (command == 0x00)
      {
         // LOCAL: a connection the proxy established on its own behalf (health
         // check). The specification says the receiver must use the real
         // connection endpoints and ignore the address block entirely.
         result.has_source_address = false;
         return StatusComplete;
      }

      switch (familyAndTransport)
      {
      case 0x11: // TCP over IPv4
         {
            if (addressBlockLength < 12)
            {
               result.error_message = "PROXY protocol v2 TCP4 address block is too short";
               return StatusInvalid;
            }

            boost::asio::ip::address_v4::bytes_type addressBytes;
            memcpy(addressBytes.data(), buffer + 16, 4);

            result.has_source_address = true;
            result.source_address = IPAddress(boost::asio::ip::address(boost::asio::ip::address_v4(addressBytes)));
            result.source_port = (unsigned int) ((buffer[24] << 8) | buffer[25]);

            return StatusComplete;
         }
      case 0x21: // TCP over IPv6
         {
            if (addressBlockLength < 36)
            {
               result.error_message = "PROXY protocol v2 TCP6 address block is too short";
               return StatusInvalid;
            }

            boost::asio::ip::address_v6::bytes_type addressBytes;
            memcpy(addressBytes.data(), buffer + 16, 16);

            result.has_source_address = true;
            result.source_address = IPAddress(boost::asio::ip::address(boost::asio::ip::address_v6(addressBytes)));
            result.source_port = (unsigned int) ((buffer[48] << 8) | buffer[49]);

            return StatusComplete;
         }
      default:
         {
            // AF_UNSPEC (\x00), UDP, unix sockets, or a family this receiver
            // does not know. The specification requires \x00 to be tolerated
            // with the address block ignored; none of the others can name a
            // usable TCP client either. In every case the connection continues
            // with the real (trusted proxy's) endpoints rather than an address
            // nobody usable vouched for.
            result.has_source_address = false;
            return StatusComplete;
         }
      }
   }

   bool
   TrustedProxyList::Matches(const String &configuredList, const IPAddress &address)
   {
      if (configuredList.IsEmpty())
         return false;

      std::vector<String> entries = StringParser::SplitString(configuredList, ",");

      for (String entry : entries)
      {
         entry = entry.Trim();

         if (entry.IsEmpty())
            continue;

         if (EntryMatches_(entry, address))
            return true;
      }

      return false;
   }

   bool
   TrustedProxyList::EntryMatches_(const String &entry, const IPAddress &address)
   {
      String addressPart = entry;
      int prefixLength = -1;

      int slashIndex = entry.Find(_T("/"));
      if (slashIndex >= 0)
      {
         addressPart = entry.Left(slashIndex);

         String prefixPart = entry.Mid(slashIndex + 1);
         if (prefixPart.IsEmpty())
         {
            ReportInvalidEntryOnce_(entry);
            return false;
         }

         unsigned int parsedPrefix = 0;
         if (!ParsePortNumber(prefixPart, parsedPrefix)) // digits-only parse; range-checked below
         {
            ReportInvalidEntryOnce_(entry);
            return false;
         }

         prefixLength = (int) parsedPrefix;
      }

      IPAddress entryAddress;
      if (!entryAddress.TryParse(addressPart, false))
      {
         ReportInvalidEntryOnce_(entry);
         return false;
      }

      // Families never match across: 10.0.0.0/8 says nothing about any IPv6
      // peer, and comparing the raw numeric forms across families would let it.
      if (entryAddress.GetType() != address.GetType())
         return false;

      if (prefixLength < 0)
         return entryAddress.GetAddress1() == address.GetAddress1() &&
                entryAddress.GetAddress2() == address.GetAddress2();

      if (address.GetType() == IPAddress::IPV4)
      {
         if (prefixLength > 32)
         {
            ReportInvalidEntryOnce_(entry);
            return false;
         }

         unsigned __int64 mask = prefixLength == 0 ? 0 : (0xFFFFFFFFui64 << (32 - prefixLength)) & 0xFFFFFFFFui64;

         return (entryAddress.GetAddress1() & mask) == (address.GetAddress1() & mask);
      }

      // IPv6: the address is the 128-bit pair (GetAddress1 = high 64 bits,
      // GetAddress2 = low 64 bits).
      if (prefixLength > 128)
      {
         ReportInvalidEntryOnce_(entry);
         return false;
      }

      int highBits = prefixLength > 64 ? 64 : prefixLength;
      int lowBits = prefixLength > 64 ? prefixLength - 64 : 0;

      unsigned __int64 highMask = highBits == 0 ? 0 : (highBits == 64 ? 0xFFFFFFFFFFFFFFFFui64 : (0xFFFFFFFFFFFFFFFFui64 << (64 - highBits)));
      unsigned __int64 lowMask = lowBits == 0 ? 0 : (lowBits == 64 ? 0xFFFFFFFFFFFFFFFFui64 : (0xFFFFFFFFFFFFFFFFui64 << (64 - lowBits)));

      return (entryAddress.GetAddress1() & highMask) == (address.GetAddress1() & highMask) &&
             (entryAddress.GetAddress2() & lowMask) == (address.GetAddress2() & lowMask);
   }

   void
   TrustedProxyList::ReportInvalidEntryOnce_(const String &entry)
   {
      // The list is consulted on every connection, so a configuration typo must
      // not write one error per connection - but it must also not be silent,
      // because a malformed entry FAILS CLOSED (it trusts nothing), which from
      // the administrator's chair looks like the feature not working.
      static boost::atomic<bool> already_reported(false);

      bool expected = false;
      if (!already_reported.compare_exchange_strong(expected, true))
         return;

      ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6281, "TrustedProxyList::EntryMatches_",
         Formatter::Format(_T("The trusted proxy list contains an entry that is not a valid IP address or CIDR range: {0}. The entry is ignored (it matches nothing). Further occurrences of this error will not be reported until the service is restarted."), entry));
   }
}
