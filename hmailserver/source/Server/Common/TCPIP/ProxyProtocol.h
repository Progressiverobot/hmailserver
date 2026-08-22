// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "IPAddress.h"

namespace HM
{
   /*
      Parser for the HAProxy PROXY protocol, versions 1 and 2
      (https://www.haproxy.org/download/1.8/doc/proxy-protocol.txt).

      The header is sent by a trusted load balancer / reverse proxy as the very
      first bytes of the TCP connection - before the TLS handshake on an SSL
      port and before any protocol banner - and carries the address of the real
      client the proxy is forwarding for.

      The parser is deliberately incremental and never asks for more bytes than
      are guaranteed to belong to the header: one byte at a time for the
      human-readable v1 line (its length is unknown until its CRLF), and exact
      block sizes for binary v2. That property is what lets the caller read
      straight from the socket without ever consuming bytes that belong to
      whatever follows the header (the TLS ClientHello, or the first SMTP
      command).
   */
   class ProxyProtocolParser
   {
   public:
      enum Status
      {
         // The buffer does not yet hold a complete header; read exactly
         // 'bytes_needed' further bytes and call again.
         StatusNeedMore = 0,
         // A complete, valid header was parsed.
         StatusComplete = 1,
         // The bytes are not a valid PROXY protocol header. The connection
         // must be dropped; the specification forbids answering.
         StatusInvalid = 2
      };

      struct Result
      {
         // True when the header carried a usable TCP client address. False for
         // a v1 UNKNOWN header, a v2 LOCAL command and a v2 AF_UNSPEC (or other
         // non-TCP) address family - in all of those cases the specification
         // says the receiver must keep using the real connection endpoints,
         // i.e. NOT rewrite the address.
         bool has_source_address = false;

         IPAddress source_address;
         unsigned int source_port = 0;

         // Set when StatusInvalid is returned; describes what was wrong.
         String error_message;
      };

      // Examines 'available' bytes at 'buffer' (always from the start of the
      // connection). Returns StatusNeedMore with bytes_needed set, or a final
      // status. Never inspects more than the header itself.
      static Status Parse(const unsigned char *buffer, size_t available, size_t &bytes_needed, Result &result);

   private:
      static Status ParseVersion1_(const unsigned char *buffer, size_t available, size_t &bytes_needed, Result &result);
      static Status ParseVersion2_(const unsigned char *buffer, size_t available, size_t &bytes_needed, Result &result);
   };

   /*
      Matches an address against an administrator-configured list of trusted
      proxy addresses: comma-separated single IP addresses and/or CIDR ranges
      (e.g. "10.0.0.5, 192.168.1.0/24, 2001:db8::/32").

      An empty list trusts nobody, and a malformed entry never matches (fail
      closed) - a peer that is allowed to rewrite its own source address has
      defeated every IP-based control on the server, so nothing is trusted
      unless an administrator wrote it down correctly.

      Callers must pass the REAL TCP peer address (the socket's remote
      endpoint), never an address that a PROXY header or XCLIENT command
      supplied, so that a chained proxy cannot bootstrap trust from an address
      it asserted itself.
   */
   class TrustedProxyList
   {
   public:
      static bool Matches(const String &configuredList, const IPAddress &address);

   private:
      static bool EntryMatches_(const String &entry, const IPAddress &address);
      static void ReportInvalidEntryOnce_(const String &entry);
   };
}
