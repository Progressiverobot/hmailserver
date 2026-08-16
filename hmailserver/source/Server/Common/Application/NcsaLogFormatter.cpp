// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// See NcsaLogFormatter.h for the field order and the reasoning behind it.

#include "stdafx.h"

#include "NcsaLogFormatter.h"

#include "../Util/Time.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   String
   NcsaLogFormatter::Format(const String &category, int session, const String &remoteHost,
                            const String &time, const String &message)
   {
      String host = BuildToken_(remoteHost);

      // CLF has no "no user" value other than the hyphen, and a session id is
      // the only identity the Logger is given. Prefixed rather than bare so a
      // reader is never left wondering whether a lone number is a user name.
      String authenticatedUser(_T("-"));
      if (session >= 0)
         authenticatedUser.Format(_T("session-%d"), session);

      String request = EscapeQuotedField(category + _T(" ") + message);

      String status = ExtractStatusCode(message);
      if (status.IsEmpty())
         status = _T("-");

      // host, ident, authuser, [date], "request", status, bytes - in that order.
      // The two bare hyphens are ident and bytes; see the header for why neither
      // can be filled in.
      String result;
      result.Format(_T("%s - %s %s \"%s\" %s -\r\n"),
         host.c_str(),
         authenticatedUser.c_str(),
         BuildClfTimestamp(time).c_str(),
         request.c_str(),
         status.c_str());

      return result;
   }

   String
   NcsaLogFormatter::BuildClfTimestamp(const String &time)
   {
      // The Logger always hands us Time::GetCurrentDateTimeWithMilliseconds(),
      // which is "yyyy-MM-dd HH:mm:ss.fff". Everything below validates that
      // shape rather than trusting it, because producing an unparseable date
      // field would defeat the whole point of emitting a standard format, and a
      // caller outside the Logger could reasonably pass something else.
      if (time.GetLength() < 19)
         return _T("[-]");

      if (time[4] != _T('-') || time[7] != _T('-') || time[10] != _T(' ') ||
          time[13] != _T(':') || time[16] != _T(':'))
         return _T("[-]");

      if (!AllDigits_(time, 0, 4) || !AllDigits_(time, 5, 2) || !AllDigits_(time, 8, 2) ||
          !AllDigits_(time, 11, 2) || !AllDigits_(time, 14, 2) || !AllDigits_(time, 17, 2))
         return _T("[-]");

      int year = _ttoi(time.Mid(0, 4).c_str());
      int month = _ttoi(time.Mid(5, 2).c_str());
      int day = _ttoi(time.Mid(8, 2).c_str());

      if (month < 1 || month > 12)
         return _T("[-]");

      // GetUTCRelation() reports the offset in force now rather than the one in
      // force when the entry was created. The two can only differ for entries
      // rendered across a daylight-saving transition, which is a second or two
      // of mislabelling twice a year; carrying a per-entry offset would mean
      // capturing the time zone at every log call site for no practical gain.
      //
      // The time of day is copied out of the input rather than reformatted from
      // parsed integers: it has already been validated as six digits in two
      // separators, and re-printing it could only introduce a difference between
      // this rendering and the default one.
      String result;
      result.Format(_T("[%.02d/%s/%d:%s %s]"),
         day,
         Time::GetMonthShortName(month).c_str(),
         year,
         time.Mid(11, 8).c_str(),
         Time::GetUTCRelation().c_str());

      return result;
   }

   String
   NcsaLogFormatter::EscapeQuotedField(const String &value)
   {
      String result;
      result.reserve(value.GetLength() + 16);

      for (int i = 0; i < value.GetLength(); i++)
      {
         wchar_t character = value[i];

         switch (character)
         {
         case '\"':
            result += _T("\\\"");
            break;
         case '\\':
            result += _T("\\\\");
            break;
         case '\r':
            result += _T("\\r");
            break;
         case '\n':
            result += _T("\\n");
            break;
         case '\t':
            result += _T("\\t");
            break;
         default:
            if (character < 0x20)
            {
               // Apache's \xHH form. Anything reaching here came off a socket, so
               // it has to be neutralised rather than passed through: a raw
               // control byte in the middle of a quoted field is exactly the sort
               // of thing that makes a downstream parser reject the whole file.
               String encoded;
               encoded.Format(_T("\\x%02X"), static_cast<int>(character));
               result += encoded;
            }
            else
            {
               result += character;
            }
            break;
         }
      }

      return result;
   }

   String
   NcsaLogFormatter::ExtractStatusCode(const String &message)
   {
      // Conversation entries are logged as "SENT: 250 OK" or
      // "RECEIVED: EHLO host"; application-level entries have no reply code at
      // all. Only a three-digit group at the start of the protocol text counts as
      // a status, so "Size: 512 KB" does not become status 512.
      int offset = 0;

      if (message.StartsWith(_T("SENT: ")))
         offset = 6;
      else if (message.StartsWith(_T("RECEIVED: ")))
         offset = 10;

      if (message.GetLength() < offset + 3)
         return _T("");

      if (!AllDigits_(message, offset, 3))
         return _T("");

      // A longer digit run is a number in prose, not a reply code. SMTP writes
      // "250-" for a continued multi-line reply, so the hyphen counts as a
      // terminator alongside the space.
      if (message.GetLength() > offset + 3)
      {
         wchar_t next = message[offset + 3];
         if (next != _T(' ') && next != _T('-'))
            return _T("");
      }

      return message.Mid(offset, 3);
   }

   String
   NcsaLogFormatter::BuildToken_(const String &value)
   {
      String result;
      result.reserve(value.GetLength());

      for (int i = 0; i < value.GetLength(); i++)
      {
         wchar_t character = value[i];

         if (character <= 0x20 || character == _T('\"'))
            continue;

         result += character;
      }

      if (result.IsEmpty())
         return _T("-");

      return result;
   }

   bool
   NcsaLogFormatter::AllDigits_(const String &value, int offset, int count)
   {
      if (offset < 0 || count < 0 || offset + count > value.GetLength())
         return false;

      for (int i = offset; i < offset + count; i++)
      {
         if (value[i] < _T('0') || value[i] > _T('9'))
            return false;
      }

      return true;
   }
}
