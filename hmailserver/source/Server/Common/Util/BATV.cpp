// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "BATV.h"

#include "Hashing/HashCreator.h"

#include <ctime>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   // The single key number used for the "K" field. Key rotation is out of scope;
   // a fixed value keeps the prvs tag value a constant 10 characters.
   static const wchar_t kKeyNumber = L'1';

   // How many days a prvs tag remains valid, plus one day of tolerance for clock
   // skew between the bouncing host and this server. Bounces normally return
   // within hours, so a short window keeps the backscatter rejection tight.
   static const int kMaxAgeDays = 7;
   static const int kDayModulus = 1000;

   // Number of hex characters of the HMAC kept in the tag (24 bits).
   static const int kHashHexLength = 6;

   // Fixed length of the tag value: K (1) + DDD (3) + SSSSSS (6).
   static const int kTagValueLength = 1 + 3 + kHashHexLength; // = 10

   int
   BATV::CurrentDayNumber_()
   {
      time_t now = time(0);
      __int64 days = (__int64) now / 86400;
      return (int) (days % kDayModulus);
   }

   bool
   BATV::DayNumberInRange_(int dayNumber)
   {
      int currentDay = CurrentDayNumber_();
      int age = currentDay - dayNumber;
      if (age < 0)
         age += kDayModulus;

      // Accept tags up to kMaxAgeDays old, plus one day in the future to tolerate
      // clock skew (age wraps to kDayModulus-1 when the day is one ahead).
      return (age <= kMaxAgeDays) || (age >= (kDayModulus - 1));
   }

   String
   BATV::ComputeHash_(const String &secret, const String &keyAndDay, const String &mailbox)
   {
      // Canonical signing string: the key+day prefix followed by the lowercased
      // mailbox. Both the sign and verify paths build it identically.
      String lowerMailbox = mailbox;
      lowerMailbox.ToLower();

      AnsiString canonical = AnsiString(keyAndDay) + AnsiString(lowerMailbox);
      AnsiString hex = HashCreator::ComputeHMACSHA256Hex(AnsiString(secret), canonical);
      AnsiString truncated = hex.Mid(0, kHashHexLength);

      // The hex digest is ASCII; widen it to a String for use in the address.
      String result;
      for (int i = 0; i < truncated.GetLength(); i++)
         result += (wchar_t) truncated.GetAt(i);
      return result;
   }

   String
   BATV::Sign(const String &mailbox, const String &secret)
   {
      if (mailbox.IsEmpty() || secret.IsEmpty())
         return "";

      // Never double-sign an already-tagged address.
      if (IsPrvs(mailbox))
         return "";

      int atPos = mailbox.ReverseFind(_T('@'));
      if (atPos <= 0 || atPos == mailbox.GetLength() - 1)
         return "";

      String localPart = mailbox.Mid(0, atPos);
      String domain = mailbox.Mid(atPos + 1);

      String dayDigits;
      dayDigits.Format(_T("%03d"), CurrentDayNumber_());

      String keyAndDay;
      keyAndDay += kKeyNumber;
      keyAndDay += dayDigits;

      String hash = ComputeHash_(secret, keyAndDay, mailbox);

      String result;
      result.Format(_T("prvs=%s%s=%s@%s"),
         keyAndDay.c_str(), hash.c_str(), localPart.c_str(), domain.c_str());
      return result;
   }

   bool
   BATV::IsPrvs(const String &address)
   {
      int atPos = address.ReverseFind(_T('@'));
      if (atPos <= 0)
         return false;

      String localPart = address.Mid(0, atPos);

      // "prvs=" (5) + 10-char tag value + "=" + at least one character.
      if (localPart.GetLength() < 5 + kTagValueLength + 2)
         return false;

      if (localPart.Mid(0, 5).CompareNoCase(_T("prvs=")) != 0)
         return false;

      // The separator after the fixed-length tag value must be '='.
      return localPart.GetAt(5 + kTagValueLength) == _T('=');
   }

   bool
   BATV::Verify(const String &address, const String &secret, String &originalMailbox)
   {
      originalMailbox = "";

      if (secret.IsEmpty() || !IsPrvs(address))
         return false;

      int atPos = address.ReverseFind(_T('@'));
      String localPart = address.Mid(0, atPos);
      String domain = address.Mid(atPos + 1);

      // localPart = "prvs=" <K><DDD><SSSSSS> "=" <original-local-part>
      String payload = localPart.Mid(5);
      String tagValue = payload.Mid(0, kTagValueLength);
      // payload[kTagValueLength] is the '=' separator (validated by IsPrvs).
      String originalLocal = payload.Mid(kTagValueLength + 1);

      if (originalLocal.IsEmpty())
         return false;

      String keyAndDay = tagValue.Mid(0, 4); // K + DDD
      String hash = tagValue.Mid(4);         // SSSSSS

      String dayDigits = keyAndDay.Mid(1, 3);
      for (int i = 0; i < dayDigits.GetLength(); i++)
      {
         if (dayDigits.GetAt(i) < L'0' || dayDigits.GetAt(i) > L'9')
            return false;
      }

      String candidateMailbox = originalLocal + _T("@") + domain;

      // Verify the HMAC signature (case-insensitive hex comparison).
      String expected = ComputeHash_(secret, keyAndDay, candidateMailbox);
      if (expected.CompareNoCase(hash) != 0)
         return false;

      int dayNumber = (dayDigits.GetAt(0) - L'0') * 100 +
                      (dayDigits.GetAt(1) - L'0') * 10 +
                      (dayDigits.GetAt(2) - L'0');
      if (!DayNumberInRange_(dayNumber))
         return false;

      originalMailbox = candidateMailbox;
      return true;
   }
}
