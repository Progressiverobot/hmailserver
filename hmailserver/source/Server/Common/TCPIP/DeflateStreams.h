// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include <string>

namespace HM
{
   // The two halves of a DEFLATE-compressed connection (RFC 4978 for IMAP, the
   // same shape for anything else): what the peer sends is inflated as it arrives,
   // what this server sends is deflated with a sync flush at the end of every
   // write, so the peer can act on each response the moment it lands rather than
   // when a block happens to fill. Raw DEFLATE (RFC 1951), no zlib or gzip
   // wrapper, as the RFC requires. zlib does the work; this keeps zlib's types out
   // of the connection's header and its lifetime tied to the connection's.
   class DeflateStreams
   {
   public:
      DeflateStreams();
      ~DeflateStreams();

      // Prepares both directions. False when zlib refuses (which it does not, short
      // of memory).
      bool Start();
      bool IsStarted() const { return started_; }

      // Inflates what the peer sent, appending the plain bytes to output. False on
      // a corrupt stream, with error saying what zlib said; the connection is then
      // not one this server can go on reading.
      bool Inflate(const char *compressed, size_t length, std::string &output, std::string &error);

      // Deflates what this server is about to send, flushed so that everything given
      // here is fully represented in output.
      bool Deflate(const char *plain, size_t length, std::string &output, std::string &error);

   private:
      DeflateStreams(const DeflateStreams &);
      DeflateStreams &operator=(const DeflateStreams &);

      struct State;
      State *state_;
      bool started_;
   };
}
