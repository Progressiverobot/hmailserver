// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "SRS.h"

#include "Hashing/HashCreator.h"

#include <ctime>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   // RFC 4648 base32 alphabet (without padding). 1024 = 32^2, so a day-slot
   // taken modulo 1024 always encodes to exactly two characters.
   static const wchar_t *kBase32Alphabet = L"ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

   // How many days an SRS address remains valid for, plus one day of tolerance
   // for clock skew between the bouncing host and this server.
   static const int kMaxAgeDays = 21;
   static const int kFutureSkewSlot = 1023; // currentSlot + 1 (mod 1024)

   // Number of hex characters of the HMAC kept in the address (32 bits).
   static const int kHashHexLength = 8;

   int
   SRS::CurrentTimeSlot_()
   {
      time_t now = time(0);
      __int64 days = (__int64) now / 86400;
      return (int) (days % 1024);
   }

   String
   SRS::EncodeTimeSlot_(int slot)
   {
      slot = slot % 1024;
      if (slot < 0)
         slot += 1024;

      String result;
      result += kBase32Alphabet[(slot >> 5) & 31];
      result += kBase32Alphabet[slot & 31];
      return result;
   }

   bool
   SRS::DecodeTimeSlot_(const String &encoded, int &slot)
   {
      if (encoded.GetLength() != 2)
         return false;

      int high = -1;
      int low = -1;
      for (int i = 0; i < 32; i++)
      {
         wchar_t c = kBase32Alphabet[i];
         if (encoded.GetAt(0) == c) high = i;
         if (encoded.GetAt(1) == c) low = i;
      }

      if (high < 0 || low < 0)
         return false;

      slot = (high << 5) | low;
      return true;
   }

   bool
   SRS::TimeSlotInRange_(int slot)
   {
      int currentSlot = CurrentTimeSlot_();
      int age = currentSlot - slot;
      if (age < 0)
         age += 1024;

      // Accept timestamps up to kMaxAgeDays old, plus one day in the future to
      // tolerate clock skew (age wraps to 1023 when the slot is one day ahead).
      return (age <= kMaxAgeDays) || (age >= kFutureSkewSlot);
   }

   String
   SRS::ComputeHash_(const String &secret, const String &timeSlot, const String &sourceDomain, const String &sourceLocal)
   {
      // Canonical signing string: timestamp + lowercased source domain + source
      // local-part. Both the forward and reverse paths build it identically.
      String lowerDomain = sourceDomain;
      lowerDomain.ToLower();

      AnsiString canonical = AnsiString(timeSlot) + AnsiString(lowerDomain) + AnsiString(sourceLocal);
      AnsiString hex = HashCreator::ComputeHMACSHA256Hex(AnsiString(secret), canonical);
      AnsiString truncated = hex.Mid(0, kHashHexLength);

      // The hex digest is ASCII; widen it to a String for use in the address.
      String result;
      for (int i = 0; i < truncated.GetLength(); i++)
         result += (wchar_t) truncated.GetAt(i);
      return result;
   }

   String
   SRS::Forward(const String &originalMailFrom, const String &forwarderDomain, const String &secret)
   {
      if (originalMailFrom.IsEmpty() || forwarderDomain.IsEmpty() || secret.IsEmpty())
         return "";

      int atPos = originalMailFrom.ReverseFind(_T('@'));
      if (atPos <= 0 || atPos == originalMailFrom.GetLength() - 1)
         return "";

      String sourceLocal = originalMailFrom.Mid(0, atPos);
      String sourceDomain = originalMailFrom.Mid(atPos + 1);

      String timeSlot = EncodeTimeSlot_(CurrentTimeSlot_());
      String hash = ComputeHash_(secret, timeSlot, sourceDomain, sourceLocal);

      String result;
      result.Format(_T("SRS0=%s=%s=%s=%s@%s"),
         hash.c_str(), timeSlot.c_str(), sourceDomain.c_str(), sourceLocal.c_str(), forwarderDomain.c_str());
      return result;
   }

   bool
   SRS::IsSRS0(const String &address)
   {
      int atPos = address.ReverseFind(_T('@'));
      if (atPos <= 0)
         return false;

      String localPart = address.Mid(0, atPos);

      // The character following "SRS0" is a separator that may be '=', '+' or '-'.
      if (localPart.GetLength() < 5)
         return false;

      if (localPart.Mid(0, 4).CompareNoCase(_T("SRS0")) != 0)
         return false;

      wchar_t sep = localPart.GetAt(4);
      return sep == '=' || sep == '+' || sep == '-';
   }

   bool
   SRS::Reverse(const String &address, const String &secret, String &originalMailFrom)
   {
      originalMailFrom = "";

      if (secret.IsEmpty() || !IsSRS0(address))
      {
         return false;
      }

      int atPos = address.ReverseFind(_T('@'));
      String localPart = address.Mid(0, atPos);

      // Strip the "SRS0" prefix and its separator character.
      String payload = localPart.Mid(5);

      // payload = <hash>=<tt>=<source-domain>=<source-local...>
      // The source local-part may itself contain '=' characters, so only the
      // first three '=' are treated as field separators.
      int sep1 = payload.Find(_T("="));
      if (sep1 <= 0)
      {
         return false;
      }
      int sep2 = payload.Find(_T("="), sep1 + 1);
      if (sep2 <= sep1)
      {
         return false;
      }
      int sep3 = payload.Find(_T("="), sep2 + 1);
      if (sep3 <= sep2)
      {
         return false;
      }

      String hash = payload.Mid(0, sep1);
      String timeSlot = payload.Mid(sep1 + 1, sep2 - sep1 - 1);
      String sourceDomain = payload.Mid(sep2 + 1, sep3 - sep2 - 1);
      String sourceLocal = payload.Mid(sep3 + 1);

      if (hash.IsEmpty() || timeSlot.IsEmpty() || sourceDomain.IsEmpty() || sourceLocal.IsEmpty())
      {
         return false;
      }

      // Verify the HMAC signature (case-insensitive hex comparison).
      String expected = ComputeHash_(secret, timeSlot, sourceDomain, sourceLocal);
      if (expected.CompareNoCase(hash) != 0)
      {
         return false;
      }

      // Verify the timestamp is within the acceptance window.
      int slot = 0;
      if (!DecodeTimeSlot_(timeSlot, slot))
      {
         return false;
      }
      if (!TimeSlotInRange_(slot))
      {
         return false;
      }

      originalMailFrom = sourceLocal + _T("@") + sourceDomain;
      return true;
   }
}
