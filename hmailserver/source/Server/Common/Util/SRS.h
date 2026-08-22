// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   // Sender Rewriting Scheme (SRS0) for forwarded mail.
   //
   // When the server forwards a message from an external sender to another
   // external host, the envelope MAIL FROM is rewritten to a reversible,
   // HMAC-signed local-part at one of the server's own (local) domains:
   //
   //    SRS0=<hash>=<tt>=<source-domain>=<source-local>@<forwarder-domain>
   //
   // This keeps SPF aligned (the forwarder, not the original sender, owns the
   // envelope domain) while still allowing any bounce sent back to the rewritten
   // address to be decoded and relayed to the original sender.
   class SRS
   {
   public:
      // Rewrites originalMailFrom into an SRS0 address at forwarderDomain.
      // Returns an empty string if originalMailFrom is empty/invalid or no
      // secret is configured.
      static String Forward(const String &originalMailFrom, const String &forwarderDomain, const String &secret);

      // Returns true if the local-part of address is an SRS0 address.
      static bool IsSRS0(const String &address);

      // Reverses an SRS0 address back to the original MAIL FROM. Returns true
      // only when the HMAC signature is valid and the timestamp is in range.
      static bool Reverse(const String &address, const String &secret, String &originalMailFrom);

   private:
      static int CurrentTimeSlot_();
      static String EncodeTimeSlot_(int slot);
      static bool DecodeTimeSlot_(const String &encoded, int &slot);
      static bool TimeSlotInRange_(int slot);
      static String ComputeHash_(const String &secret, const String &timeSlot, const String &sourceDomain, const String &sourceLocal);
   };
}
