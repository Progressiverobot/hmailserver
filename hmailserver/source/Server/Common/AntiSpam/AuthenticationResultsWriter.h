// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#pragma once

namespace HM
{
   class Message;
   class AuthenticationResults;

   class AuthenticationResultsWriter
   {
   public:

      // Prepends the RFC 8601 Authentication-Results field, and the RFC 7208 9.1
      // Received-SPF field when one is enabled and SPF ran, to the accepted message,
      // first removing any Authentication-Results field already there that carries our
      // own authserv-id.
      //
      // Returns false when the message was left exactly as it was found, which is
      // always a safe outcome: the sender has already been told 250 by the time this
      // runs, so a message that ends up missing or truncated is permanently lost mail
      // the sender believes was delivered. A message without the header is not.
      static bool Write(const String &messageFileName,
                        std::shared_ptr<Message> message,
                        std::shared_ptr<AuthenticationResults> results);
   };
}
