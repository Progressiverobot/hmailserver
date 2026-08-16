// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#pragma once

namespace HM
{
   // Renders one log entry in the NCSA Common Log Format.
   //
   // WHY THIS EXISTS: eLogOutputFormat::hLogFormatCSA has been declared in the
   // IDL, settable through COM (Settings.Logging.LogFormat) and offered in the
   // Control Panel's Logging page for years, and nothing ever read the stored
   // value. An administrator who picked it got no error and no change: the log
   // stayed in hMailServer's own tab-separated format. A setting that appears to
   // work is worse than one that is missing, because nobody goes looking for the
   // reason their log analyser sees nothing it recognises.
   //
   // THE FIELD ORDER is the one NCSA defined for httpd and that every log
   // analyser since has parsed:
   //
   //    host ident authuser [date] "request" status bytes
   //
   // and the mapping onto a mail server's log entry is:
   //
   //    host      The remote peer's IP address; "-" for entries that have no
   //              peer (application, debug, TCP/IP and error entries).
   //    ident     Always the literal "-". This field is RFC 1413 identd, which
   //              hMailServer has never queried and will not start querying
   //              merely to fill in a log column.
   //    authuser  "session-<n>" for an entry that belongs to a protocol
   //              session, "-" otherwise. CLF reserves this field for the
   //              identity determined for the request. The Logger entry points
   //              are handed a session id and never a user name -
   //              LogSMTPConversation and friends simply are not given one - and
   //              resolving a name here would mean a database read per logged
   //              line. The session id is the identity hMailServer actually has,
   //              and it is the thing that ties the lines of one conversation
   //              together, which is the reason anybody greps this file at all.
   //    date      "[dd/Mmm/yyyy:HH:MM:SS +hhmm]" - local time with the numeric
   //              UTC offset, exactly as CLF specifies.
   //    request   "<category> <message>", e.g. "SMTPD SENT: 220 mail ESMTP".
   //              The category is the same token the default format writes, so a
   //              line can still be attributed to a protocol. Quotes,
   //              backslashes and control characters are escaped Apache-style so
   //              that remote input - a HELO string is remote input - can never
   //              terminate the quoted field and shift every later column.
   //    status    The three-digit protocol reply code when the entry text starts
   //              with one (after an optional "SENT: " / "RECEIVED: " prefix),
   //              "-" otherwise. This is the field that makes the format worth
   //              selecting: an analyser can count 5xx rejections without
   //              knowing anything at all about hMailServer.
   //    bytes     Always the literal "-". The Logger is never told transfer
   //              sizes. Per-message byte counts live in the AWStats journal
   //              (hmailserver_awstats.log), which this setting deliberately
   //              does not touch, because an external AWStats installation
   //              parses that file with a fixed field list and reformatting it
   //              would break every existing installation.
   //
   // REJECTED: the "combined" variant, which appends "referer" and "user-agent".
   // hMailServer has nothing to put in either field, and emitting two literal
   // "-" columns to claim a richer format would only make the lines longer. The
   // IDL help string - the closest thing to a specification this setting has -
   // says "NCSA Common log format", so Common is what is emitted. The Control
   // Panel's label currently reads "NCSA / combined (AWStats)"; that label is
   // wrong on both counts and is the thing that should change.
   //
   // ALSO REJECTED: parking hMailServer's thread id in one of the combined
   // fields. Choosing a fixed interchange format means accepting its columns;
   // the thread id has no CLF field and is dropped. An administrator who wants
   // hMailServer's own diagnostic columns leaves the setting on Default, or
   // turns on JsonLogging, which carries every field.
   class NcsaLogFormatter
   {
   public:

      // Returns a complete log line including the trailing CRLF, so it is
      // interchangeable with the strings the default and JSON renderings
      // produce. session < 0 means "no protocol session".
      static String Format(const String &category, int session, const String &remoteHost,
                           const String &time, const String &message);

      // "2026-08-12 14:20:01.123" -> "[12/Aug/2026:14:20:01 +0100]".
      //
      // Takes the timestamp the entry was created with rather than reading the
      // clock: an entry can be rendered a moment after it happened, and the file
      // and database devices must never disagree about when it did. Returns
      // "[-]" for anything that is not the Logger's timestamp shape - every
      // separator in place and every other character in the first 19 a digit -
      // because a plainly malformed date field is far easier to diagnose than a
      // plausible but wrong one.
      static String BuildClfTimestamp(const String &time);

      // Escapes the characters that would otherwise terminate or corrupt a
      // quoted CLF field, using the escapes Apache writes (\" \\ \r \n \t and
      // \xHH for anything else below 0x20) so existing parsers already
      // understand them.
      static String EscapeQuotedField(const String &value);

      // Returns the leading three-digit protocol reply code of a conversation
      // line, or an empty string when the line does not start with one.
      static String ExtractStatusCode(const String &message);

   private:

      // Collapses a value that must occupy a single unquoted CLF column: spaces,
      // quotes and control characters are removed, and an empty result becomes
      // "-". Only ever fed IP address strings, but a column that silently
      // acquired a space would shift every field after it.
      static String BuildToken_(const String &value);

      // True when count characters starting at offset are all ASCII digits.
      // Used instead of trusting _ttoi, which reports 0 for "abcd" just as it
      // does for "0000" and would let a malformed timestamp through as a
      // plausible-looking date.
      static bool AllDigits_(const String &value, int offset, int count);
   };
}
