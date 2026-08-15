// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#pragma once

#include <vector>
#include <memory>

namespace HM
{
   class MimeBody;

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

      // The strings the "body" test (RFC 5173) compares its keys against, for one
      // of the three body transforms:
      //
      //   "raw"     - the message body exactly as it arrived, one value, no MIME
      //               structure and no decoding.
      //   "content" - one value per MIME part whose content type matches an entry
      //               in contentTypes, decoded. An entry may be a full type
      //               ("text/html"), a top-level type ("image", matching every
      //               subtype), or "" which matches every part.
      //   "text"    - the default: one value per text/* part, decoded.
      //
      // An empty result means the test is false for every key, which is what a
      // message with no matching part should produce.
      std::vector<String> GetBodyValues(const String &transform,
                                        const std::vector<String> &contentTypes) const;

   private:
      // Walks the MIME tree, appending the decoded text of every part the filter
      // accepts. Recursive, because a part may itself be multipart.
      static void CollectMatchingParts_(std::shared_ptr<MimeBody> part,
                                        const String &transform,
                                        const std::vector<String> &contentTypes,
                                        int depth,
                                        std::vector<String> &values);

      static bool ContentTypeMatches_(const String &partType, const std::vector<String> &contentTypes);

      struct Header
      {
         String name;   // lowercased
         String value;
      };

      void Parse_(const String &rawMessage);

      std::vector<Header> headers_;
      __int64 size_;

      // The message exactly as it arrived, and where its body starts within it.
      // Kept because the "body" test needs the bytes, not the parsed headers -
      // ":raw" is defined as the undecoded body and ":text"/":content" need the
      // whole message so the MIME parser can see the boundaries in its headers.
      String raw_;
      int bodyOffset_;
   };
}
