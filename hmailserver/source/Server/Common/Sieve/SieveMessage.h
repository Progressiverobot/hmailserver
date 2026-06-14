// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#pragma once

#include <vector>

namespace HM
{
   // A lightweight read model of a message for Sieve test evaluation: the
   // unfolded header set plus the total octet size. Address extraction for the
   // "address" test is provided on top of the raw header values.
   class SieveMessage
   {
   public:
      explicit SieveMessage(const String &rawMessage);

      // All values for the named header (case-insensitive), in order.
      std::vector<String> GetHeaderValues(const String &name) const;

      // True when at least one header with the given name exists.
      bool HasHeader(const String &name) const;

      // The total size of the message in octets.
      __int64 GetSize() const { return size_; }

      // Extracts addresses from a header value according to the Sieve address
      // part: "all" (the whole address), "localpart" or "domain".
      static std::vector<String> ExtractAddresses(const String &headerValue, const String &addressPart);

   private:
      struct Header
      {
         String name;   // lowercased
         String value;
      };

      void Parse_(const String &rawMessage);

      std::vector<Header> headers_;
      __int64 size_;
   };
}
