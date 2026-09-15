// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// RFC 5424 rendering. See SyslogFormatter.h for the shape and for the two
// decisions - the enterprise number and the byte-order mark - that look
// arbitrary without the reasoning.

#include "StdAfx.h"

#include "SyslogFormatter.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   // The in-class initialisers above are declarations; these are the definitions,
   // so that a caller taking a reference to one still links.
   const size_t SyslogFormatter::UdpByteLimit;
   const size_t SyslogFormatter::TcpByteLimit;

   namespace
   {
      // The private SD-ID. See the header for why this enterprise number.
      const char *SdId = "hmailserver@32473";

      // RFC 5424 section 6: every field but MSG is PRINTUSASCII, and a field the
      // sender cannot supply is "-". A value that would break the grammar is
      // replaced by the NILVALUE rather than sent mangled.
      AnsiString AsciiField_(const AnsiString &value, size_t max_length)
      {
         AnsiString result;

         int length = value.GetLength();
         for (int i = 0; i < length && result.GetLength() < (int) max_length; i++)
         {
            unsigned char c = (unsigned char) value.GetAt(i);

            // 33..126 is PRINTUSASCII: printable, and no space, which is the
            // field separator.
            if (c >= 33 && c <= 126)
               result += (char) c;
         }

         if (result.IsEmpty())
            return "-";

         return result;
      }

      bool AllDigits_(const String &value, int offset, int count)
      {
         if (offset + count > value.GetLength())
            return false;

         for (int i = offset; i < offset + count; i++)
         {
            if (value[i] < _T('0') || value[i] > _T('9'))
               return false;
         }

         return true;
      }

      // "Severity: 3 (Medium), Code: HM5015, ..." -> 3. Returns 0 when the
      // message is not an ErrorManager line, which every caller reads as "no
      // opinion" rather than as a severity.
      int ErrorManagerSeverity_(const String &message)
      {
         if (message.Find(_T("Severity: ")) != 0)
            return 0;

         const int at = 10;   // the length of "Severity: "
         if (!AllDigits_(message, at, 1))
            return 0;

         return message[at] - _T('0');
      }

      // ", Code: HM5015," -> 5015. Returns 0 when there is none.
      int ErrorManagerCode_(const String &message)
      {
         int at = message.Find(_T("Code: HM"));
         if (at < 0)
            return 0;

         at += 8;   // the length of "Code: HM"

         int digits = 0;
         while (AllDigits_(message, at + digits, 1))
            digits++;

         if (digits == 0)
            return 0;

         return _ttoi(message.Mid(at, digits).c_str());
      }
   }

   int
   SyslogFormatter::Priority(int facility, int severity)
   {
      if (!IsValidFacility(facility))
         facility = SyslogFacilityMail;

      if (severity < SyslogEmergency || severity > SyslogDebug)
         severity = SyslogInformational;

      return facility * 8 + severity;
   }

   bool
   SyslogFormatter::IsValidFacility(int facility)
   {
      return facility >= 0 && facility <= 23;
   }

   AnsiString
   SyslogFormatter::Timestamp(const String &logger_time, const String &utc_relation)
   {
      // The Logger hands out "yyyy-MM-dd HH:mm:ss.fff". Validated rather than
      // trusted, for the reason NcsaLogFormatter gives: a timestamp that does not
      // parse defeats the point of emitting a standard format, and this function
      // is reachable from a caller outside the Logger.
      if (logger_time.GetLength() < 23)
         return "-";

      if (logger_time[4] != _T('-') || logger_time[7] != _T('-') || logger_time[10] != _T(' ') ||
          logger_time[13] != _T(':') || logger_time[16] != _T(':') || logger_time[19] != _T('.'))
         return "-";

      if (!AllDigits_(logger_time, 0, 4) || !AllDigits_(logger_time, 5, 2) || !AllDigits_(logger_time, 8, 2) ||
          !AllDigits_(logger_time, 11, 2) || !AllDigits_(logger_time, 14, 2) || !AllDigits_(logger_time, 17, 2) ||
          !AllDigits_(logger_time, 20, 3))
         return "-";

      // "+hhmm" is what Time::GetUTCRelation answers; RFC 3339 wants "+hh:mm".
      // An offset that is not that shape is reported as Z rather than guessed at.
      AnsiString offset = "Z";
      if (utc_relation.GetLength() == 5 &&
          (utc_relation[0] == _T('+') || utc_relation[0] == _T('-')) &&
          AllDigits_(utc_relation, 1, 4))
      {
         offset.Format("%c%c%c:%c%c",
            (char) utc_relation[0], (char) utc_relation[1], (char) utc_relation[2],
            (char) utc_relation[3], (char) utc_relation[4]);
      }

      AnsiString date = AnsiString(logger_time.Mid(0, 10));
      AnsiString time = AnsiString(logger_time.Mid(11, 12));

      AnsiString result;
      result.Format("%sT%s%s", date.c_str(), time.c_str(), offset.c_str());
      return result;
   }

   int
   SyslogFormatter::Severity(const String &category, const String &message)
   {
      if (category == _T("ERROR"))
      {
         // ErrorManager's Critical(1), High(2), Medium(3) and Low(4) are syslog's
         // crit(2), err(3), warning(4) and notice(5) - the same four levels, one
         // apart. A line that does not carry one is err, which is what an error
         // log line means when nothing finer is known.
         int reported = ErrorManagerSeverity_(message);
         if (reported >= 1 && reported <= 4)
            return reported + 1;

         return SyslogError;
      }

      if (category == _T("DEBUG") || category == _T("TCPIP"))
         return SyslogDebug;

      return SyslogInformational;
   }

   AnsiString
   SyslogFormatter::MessageId(const String &category, const String &message)
   {
      if (category == _T("ERROR"))
      {
         int code = ErrorManagerCode_(message);
         if (code > 0)
         {
            AnsiString result;
            result.Format("HM%d", code);
            return result;
         }
      }

      // 32 is the RFC's ceiling for MSGID. Every category this server produces is
      // well under it; the limit is here so that a future one cannot break the
      // grammar by being long.
      return AsciiField_(AnsiString(category), 32);
   }

   AnsiString
   SyslogFormatter::EscapeParamValue(const AnsiString &value)
   {
      AnsiString result;

      int length = value.GetLength();
      for (int i = 0; i < length; i++)
      {
         char c = value.GetAt(i);

         switch (c)
         {
         case '"':
         case '\\':
         case ']':
            result += '\\';
            result += c;
            break;
         default:
            // A control character has no escape in the RFC's grammar, and a raw
            // CR or LF would end the message early at a receiver that reads
            // lines. Dropped, not escaped.
            if ((unsigned char) c >= 0x20 && (unsigned char) c != 0x7f)
               result += c;
            break;
         }
      }

      return result;
   }

   AnsiString
   SyslogFormatter::TruncateUtf8(const AnsiString &value, size_t limit)
   {
      if ((size_t) value.GetLength() <= limit)
         return value;

      size_t at = limit;

      // Back off any continuation bytes (10xxxxxx), then decide whether the lead
      // byte that precedes them still has all of its sequence.
      while (at > 0 && ((unsigned char) value.GetAt((int) at) & 0xC0) == 0x80)
         at--;

      if (at > 0)
      {
         unsigned char lead = (unsigned char) value.GetAt((int) at - 1);
         size_t needed = 0;

         if ((lead & 0xE0) == 0xC0)
            needed = 2;
         else if ((lead & 0xF0) == 0xE0)
            needed = 3;
         else if ((lead & 0xF8) == 0xF0)
            needed = 4;

         // The loop above stopped at the first byte that is not a continuation,
         // so whatever is kept before it is a complete character - UNLESS the
         // input is malformed and the last kept byte is itself the lead of a
         // sequence with no continuation bytes after it. Drop that one too
         // rather than send a half character.
         if (needed > 1)
            at--;
      }

      return value.Mid(0, (int) at);
   }

   AnsiString
   SyslogFormatter::Render(const SyslogRecord &record, int facility, size_t max_bytes)
   {
      AnsiString header;
      header.Format("<%d>1 %s %s %s %d %s ",
         Priority(facility, record.severity),
         AsciiField_(record.timestamp, 64).c_str(),
         AsciiField_(record.hostname, 255).c_str(),
         AsciiField_(record.app_name, 48).c_str(),
         record.process_id,
         AsciiField_(record.message_id, 32).c_str());

      // Structured data. Only the parameters that say something are emitted: a
      // session id on a line that belongs to no session, or a client address on
      // one with no peer, is noise a collector then has to filter.
      AnsiString sd;

      if (record.session >= 0 || !record.remote_host.IsEmpty() || record.thread != 0)
      {
         sd.Format("[%s thread=\"%ld\"", SdId, record.thread);

         if (record.session >= 0)
         {
            AnsiString param;
            param.Format(" session=\"%d\"", record.session);
            sd += param;
         }

         if (!record.remote_host.IsEmpty())
         {
            sd += " client=\"";
            sd += EscapeParamValue(record.remote_host);
            sd += "\"";
         }

         sd += "]";
      }
      else
      {
         sd = "-";
      }

      AnsiString prefix = header;
      prefix += sd;
      prefix += " ";

      // RFC 5424 section 6.4: a UTF-8 MSG should be introduced by the byte-order
      // mark, which is how a receiver tells UTF-8 from an unknown encoding.
      AnsiString bom;
      bom += (char) 0xEF;
      bom += (char) 0xBB;
      bom += (char) 0xBF;

      // A raw newline inside MSG is legal in RFC 5424 and illegal in the older
      // line-delimited practice most collectors still fall back to, so it is
      // folded to a space. The Logger's own messages are single lines; a script's
      // or a peer's banner is not always.
      AnsiString message;
      int length = record.message.GetLength();
      for (int i = 0; i < length; i++)
      {
         char c = record.message.GetAt(i);
         if (c == '\r' || c == '\n')
         {
            if (!message.IsEmpty() && message.GetAt(message.GetLength() - 1) != ' ')
               message += ' ';
         }
         else
         {
            message += c;
         }
      }

      if (max_bytes > 0)
      {
         size_t used = (size_t) prefix.GetLength() + (size_t) bom.GetLength();

         // A header alone over the limit means the limit is smaller than a syslog
         // message can be. The header still goes, whole: a receiver can file a
         // message with no text, and can do nothing at all with half a header.
         if (used >= max_bytes)
            return prefix;

         message = TruncateUtf8(message, max_bytes - used);
      }

      AnsiString result = prefix;
      result += bom;
      result += message;
      return result;
   }

   AnsiString
   SyslogFormatter::OctetCounted(const AnsiString &message)
   {
      AnsiString result;
      result.Format("%d ", message.GetLength());
      result += message;
      return result;
   }
}
