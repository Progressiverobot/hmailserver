// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Constant-time comparison of a presented secret against an expected one.

#pragma once

#include <string>

#include <openssl/crypto.h>

namespace HM
{
   // One implementation of "did the caller present the right secret", for every
   // listener that has to answer that question.
   //
   // Why it is a shared header rather than a copy per listener. This tree already
   // hand-writes the same comparison in three places - the anonymous-namespace
   // ConstantTimeEquals in RestApiServer.cpp, OAuth2TokenValidator's private
   // ConstantTimeEquals_, and inline in HashCreator::ValidateHash - and they do
   // not agree. OAuth2TokenValidator's checks only that the two lengths match, so
   // it answers "equal" when both sides are empty; the other two refuse a
   // zero-length secret outright. That divergence is the argument for a shared
   // primitive: the copy that is subtly wrong is exactly the copy nobody
   // re-reads, and a fourth copy would make the divergence harder to see rather
   // than easier. So it lives here once, with its argument written down, and new
   // call sites take it from here instead of pasting it.
   //
   // Why constant time at all. A comparison that returns on the first differing
   // byte tells whoever is guessing how much of the guess was right, which turns
   // one search over the whole secret into a cheap search per byte. That matters
   // most on interfaces machines talk to - a metrics endpoint, an API - because a
   // machine client can take as many timing samples as it likes and nobody will
   // notice the traffic.
   //
   // What it deliberately does NOT hide: the length of the expected secret. The
   // length check happens before CRYPTO_memcmp because CRYPTO_memcmp requires two
   // buffers of the same size, so a wrong-length guess returns sooner than a
   // right-length one. That is accepted - the length of a random token is not the
   // token - and it is the same trade every implementation of this makes.
   inline bool SecureEquals(const void *left, size_t left_length, const void *right, size_t right_length)
   {
      // A zero-length expected secret must never match. Callers are supposed to
      // treat "no secret configured" as a separate case decided before they get
      // here, but if one ever forgets, the failure mode has to be "nothing is
      // accepted" and not "an empty credential is accepted".
      if (left_length != right_length || left_length == 0)
         return false;

      return CRYPTO_memcmp(left, right, left_length) == 0;
   }

   // The overload every caller in this tree actually wants. AnsiString derives
   // from std::basic_string<char>, so an AnsiString binds to this directly, and
   // so does a plain std::string holding bytes off the wire - which means a
   // comparison between the two needs no conversion and therefore no extra copy
   // of the secret to forget about.
   //
   // size() rather than a null-terminated length on purpose: a decoded HTTP Basic
   // credential is arbitrary bytes and may legitimately contain a NUL, and
   // strlen() on that would silently compare a prefix.
   inline bool SecureEquals(const std::string &left, const std::string &right)
   {
      return SecureEquals(left.data(), left.size(), right.data(), right.size());
   }
}
