// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// One log entry rendered as an RFC 5424 syslog message.
//
// Separated from SyslogSink because the rendering is the part that has to be
// exactly right and the part a reader can check against the RFC without reading
// anything about threads, sockets or settings. The sink decides whether to send
// and where; this decides what is sent.
//
// The shape, from RFC 5424 section 6:
//
//    <PRI>1 TIMESTAMP HOSTNAME APP-NAME PROCID MSGID [SD-ID ...] BOM MSG
//
// PRI is facility * 8 + severity. The version digit is 1 and is not the version
// of this server. Any field the server cannot supply is the NILVALUE "-" rather
// than an empty one, because an empty field makes the message unparseable.
//
// Two decisions worth stating, because both look arbitrary otherwise:
//
//   The structured-data id is "hmailserver@32473". RFC 5424 section 7.2.2 says a
//   private SD-ID must be "name@<private enterprise number>", and this project
//   has no enterprise number registered with IANA. 32473 is the number IANA
//   reserves for documentation and examples (RFC 5612), so it claims nothing
//   that belongs to somebody else and a collector keys on the whole SD-ID
//   anyway. If the project ever registers a number, change the one constant.
//
//   The MSG is UTF-8 and carries the byte-order mark RFC 5424 section 6.4 asks
//   for. rsyslog, syslog-ng and journald all strip it; a receiver that does not
//   shows three extra bytes and nothing worse.

#pragma once

namespace HM
{
   // The RFC 5424 severities, by their numbers. Named here rather than as bare
   // integers at the call sites: "3" reads as a magic number and SyslogError
   // does not.
   enum SyslogSeverity
   {
      SyslogEmergency = 0,
      SyslogAlert = 1,
      SyslogCritical = 2,
      SyslogError = 3,
      SyslogWarning = 4,
      SyslogNotice = 5,
      SyslogInformational = 6,
      SyslogDebug = 7
   };

   // The facilities an administrator would choose between. mail is the default
   // and the only one that is right by default: this is a mail server, and a
   // collector filing its lines under mail.* is what every Linux administrator
   // expects. The rest are offered because a site that already files its own
   // applications under local0..local7 has a filing system of its own.
   enum SyslogFacility
   {
      SyslogFacilityKernel = 0,
      SyslogFacilityUser = 1,
      SyslogFacilityMail = 2,
      SyslogFacilityDaemon = 3,
      SyslogFacilityAuth = 4,
      SyslogFacilitySyslog = 5,
      SyslogFacilityLocal0 = 16,
      SyslogFacilityLocal1 = 17,
      SyslogFacilityLocal2 = 18,
      SyslogFacilityLocal3 = 19,
      SyslogFacilityLocal4 = 20,
      SyslogFacilityLocal5 = 21,
      SyslogFacilityLocal6 = 22,
      SyslogFacilityLocal7 = 23
   };

   // One entry as the Logger produced it, already converted to the bytes that go
   // on the wire. Built by SyslogSink on the logging thread - deliberately, so
   // that nothing but a copy of finished bytes crosses to the worker.
   struct SyslogRecord
   {
      SyslogRecord() :
         severity(SyslogInformational),
         process_id(0),
         thread(0),
         session(-1)
      {
      }

      int severity;
      AnsiString timestamp;      // RFC 3339, with an offset
      AnsiString hostname;
      AnsiString app_name;
      int process_id;
      AnsiString message_id;     // MSGID: the log category, or HM<code> for an error
      long thread;
      int session;               // -1 when the entry belongs to no session
      AnsiString remote_host;    // empty when the entry has no peer
      AnsiString message;        // UTF-8, no BOM: Render adds it
   };

   class SyslogFormatter
   {
   public:

      // RFC 5426 section 3.2 puts no ceiling on a UDP syslog datagram beyond the
      // IP one, and says a receiver must take 480 octets and should take 2048.
      // 1024 is the number this server truncates at: it clears the must, it is
      // under every path MTU that matters once the IP and UDP headers are added,
      // and it is the figure the documentation quotes.
      static const size_t UdpByteLimit = 1024;

      // The whole message over TCP, which RFC 5425 section 4.3 frames by length
      // rather than by a delimiter. No truncation there - the frame says how long
      // it is - but a bound is still needed, because a single log line is not
      // allowed to become an unbounded allocation on the worker.
      static const size_t TcpByteLimit = 65000;

      static int Priority(int facility, int severity);

      // The Logger's own "yyyy-MM-dd HH:mm:ss.fff" and Time::GetUTCRelation()'s
      // "+hhmm", as RFC 3339's "yyyy-MM-ddTHH:mm:ss.fff+hh:mm". The Logger's
      // string is used rather than a second clock reading so that the line in the
      // file and the line at the collector carry the same instant. Returns the
      // NILVALUE "-" when the input is not that shape, which is what the RFC asks
      // for and is better than an invented timestamp.
      static AnsiString Timestamp(const String &logger_time, const String &utc_relation);

      // The Logger's category as a severity. ERROR lines are read more closely:
      // ErrorManager writes "Severity: <1..4> (" at the front of every one, and
      // its four levels map onto syslog's crit, err, warning and notice - so an
      // administrator filtering at err sees the ones that matter and not the
      // "Low" ones, which is the whole point of having a severity.
      static int Severity(const String &category, const String &message);

      // The MSGID field: the log category, unless the message carries an
      // ErrorManager code, in which case that. RFC 5424 allows 32 printable
      // ASCII characters and no space, so anything longer or stranger than that
      // is cut or replaced.
      static AnsiString MessageId(const String &category, const String &message);

      // PARAM-VALUE escaping, RFC 5424 section 6.3.3: '"', '\' and ']' are
      // escaped with a backslash and nothing else is. Control characters are
      // dropped rather than escaped, because the RFC gives no escape for them
      // and a raw one would end the message at a receiver reading lines.
      static AnsiString EscapeParamValue(const AnsiString &value);

      // The finished message. max_bytes bounds the whole thing including the
      // header; 0 means no bound. When the bound bites it is the MSG that is cut,
      // never the header or the structured data - a message with half a header is
      // not a syslog message at all, while a message with half a sentence is still
      // filed, timestamped and findable.
      static AnsiString Render(const SyslogRecord &record, int facility, size_t max_bytes);

      // Cuts to at most limit bytes on a UTF-8 character boundary. Exposed for
      // the same reason Render's bound is: both are asserted by the regression
      // fixture, and a truncation that splits a multi-byte character produces a
      // line a collector may reject whole.
      static AnsiString TruncateUtf8(const AnsiString &value, size_t limit);

      // RFC 5425 section 4.3 framing: the octet count, a space, then the message.
      static AnsiString OctetCounted(const AnsiString &message);

      static bool IsValidFacility(int facility);
   };
}
