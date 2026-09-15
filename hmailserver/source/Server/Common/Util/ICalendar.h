// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// iCalendar (RFC 5545), as far as a CalDAV server needs it: a reader and a
// writer for the component tree - VCALENDAR holding VEVENT, VTODO, VALARM and
// VTIMEZONE - with the folding, parameter-quoting and escaping rules; dates
// and date-times in UTC, floating, or in a zone (the object's own VTIMEZONE,
// or a built-in table of the common IANA and Windows names when the object
// names a zone it does not carry); and recurrence - RRULE with FREQ daily,
// weekly, monthly and yearly, INTERVAL, COUNT, UNTIL, BYMONTH, BYWEEKNO,
// BYYEARDAY, BYMONTHDAY, BYDAY, BYSETPOS and WKST - expanded in the wall-clock
// time of the DTSTART's zone and converted instance by instance, so a 09:00
// weekly meeting stays at 09:00 across a daylight-saving change, with EXDATE
// removed, RDATE added and a RECURRENCE-ID override put in place of the
// instance it names. Every expansion is bounded: a ceiling on instances, on
// periods scanned, on empty periods in a row, and the year 9999, so that a
// rule written to spin cannot hold a worker thread.
//
// What is deliberately not here, and is refused rather than approximated when
// an object needs it (see Summarize): FREQ=SECONDLY, MINUTELY and HOURLY;
// BYHOUR, BYMINUTE and BYSECOND; RANGE=THISANDFUTURE on a RECURRENCE-ID; a
// TZID that is neither in the object nor in the built-in table. The built-in
// table carries each zone's rules as they stand in 2026, not its history, so
// an old instance in a zone whose rules have changed is placed by today's rule;
// an object that carries its VTIMEZONE is placed by that, always.
//
// Bytes in, bytes out, UTF-8 throughout: nothing here is a String.

#pragma once

#include <utility>
#include <vector>

namespace HM
{
   struct ICalProperty
   {
      AnsiString name;     // upper-cased: "DTSTART", "RRULE", "SUMMARY"
      AnsiString value;    // as written, escapes intact; see ICalendar::Unescape

      // Parameter names upper-cased; values as written, quotes removed.
      std::vector<std::pair<AnsiString, AnsiString>> parameters;

      // The first value of the parameter, or "" when absent.
      AnsiString Parameter(const char *parameterName) const;

      // Whether a parameter carries the value, case-insensitively, inside a
      // comma-separated list as well.
      bool HasParameter(const char *parameterName, const char *parameterValue) const;
   };

   struct ICalComponent
   {
      AnsiString name;     // upper-cased: "VCALENDAR", "VEVENT", "VTIMEZONE", "STANDARD"
      std::vector<ICalProperty> properties;
      std::vector<ICalComponent> children;

      // The first property of the name, or null.
      const ICalProperty *Property(const char *propertyName) const;

      // The first property's value, unescaped and trimmed, or "".
      AnsiString Value(const char *propertyName) const;

      // The first child component of the name, or null.
      const ICalComponent *Child(const char *componentName) const;
   };

   // A DATE or DATE-TIME value, resolved to an instant.
   struct ICalTime
   {
      ICalTime() : isDate(false), floating(false), utc(false), instant(0), year(0), month(0), day(0), hour(0), minute(0), second(0) { }

      bool isDate;         // VALUE=DATE: a day, not an instant
      bool floating;       // a DATE-TIME with neither Z nor TZID
      bool utc;            // written with Z
      AnsiString tzid;     // the zone it was written in, if any

      // Seconds since 1970-01-01T00:00:00Z. A DATE is midnight UTC of its day,
      // and a floating time is read as UTC: this server has no zone of its own.
      __int64 instant;

      // The wall-clock fields as written.
      int year, month, day, hour, minute, second;
   };

   // One occurrence of a calendar object.
   struct ICalInstance
   {
      ICalInstance() : start(0), end(0), allDay(false), recurrenceId(0), overridden(false), component(0) { }

      __int64 start;          // UTC seconds
      __int64 end;            // UTC seconds; == start for an instant with no duration
      bool allDay;
      __int64 recurrenceId;   // the start the recurrence gave this instance, before any override
      bool overridden;        // a RECURRENCE-ID component supplied it
      size_t component;       // index into the VCALENDAR's children of the component it came from
   };

   class ICalendar
   {
   public:
      // The instant past every other: 9999-12-31T23:59:59Z. The "last
      // instance" of a recurrence that never ends, or that outruns the
      // expansion ceiling.
      static const __int64 Forever;

      // The most instances one expansion answers, whatever the caller asks.
      static const size_t MaxInstances;

      // Unfolds and reads one iCalendar text into its component tree. False,
      // with the reason, when it is not one: it must open with BEGIN:VCALENDAR
      // and close with END:VCALENDAR, every BEGIN must meet its END, and every
      // line between must have a name and a colon. Line breaks may be CRLF or
      // bare LF; a line beginning with a space or a tab continues the one
      // before it. Bounded in depth, component count and property count.
      static bool Parse(const AnsiString &text, ICalComponent &root, AnsiString &problem);

      // The tree as text again: CRLF line breaks, every line folded at 75
      // octets, parameter values quoted where RFC 5545 requires it.
      static AnsiString Serialize(const ICalComponent &component);

      // The text-value escapes: \\ \n \, \; (RFC 5545 section 3.3.11).
      static AnsiString Unescape(const AnsiString &value);
      static AnsiString Escape(const AnsiString &value);

      // One content line folded at 75 octets with CRLF and a space, never
      // inside a UTF-8 sequence (RFC 5545 section 3.1).
      static AnsiString Fold(const AnsiString &line);

      // A DATE or DATE-TIME property resolved to an instant: the TZID
      // parameter through the calendar's VTIMEZONE of that name, else the
      // built-in table. False with the reason for a value that is not a date,
      // or a zone this server cannot place.
      static bool ParseTime(const ICalProperty &property, const ICalComponent *calendar, ICalTime &time, AnsiString &problem);

      // "YYYYMMDDTHHMMSSZ" (or "YYYYMMDD", as midnight UTC) to an instant, and
      // back. What the REPORT time-range and the REST route carry.
      static bool ParseUtc(const AnsiString &value, __int64 &instant);
      static AnsiString FormatUtc(__int64 instant);

      // Whether the built-in table knows the zone.
      static bool KnowsTimeZone(const AnsiString &tzid);

      // What the store keeps of a calendar object, and the check that it is
      // one this server can answer for: one VCALENDAR; VEVENT or VTODO
      // components (not both, not neither, no VJOURNAL or VFREEBUSY), every
      // one with the same UID; a VEVENT with a DTSTART; every date placeable;
      // every RRULE within what Expand expands. False with the reason.
      struct Summary
      {
         Summary() : recurring(false), start(0), end(0), first(0), last(0) { }

         AnsiString uid;
         AnsiString component;   // "VEVENT" or "VTODO"
         bool recurring;
         __int64 start;          // the master component's DTSTART (a VTODO without one: its DUE, else 0)
         __int64 end;            // the master's DTEND, DTSTART+DURATION, DUE, or start
         __int64 first;          // the earliest start of any instance
         __int64 last;           // the latest end of any instance; Forever for a recurrence with no end
      };
      static bool Summarize(const ICalComponent &calendar, Summary &summary, AnsiString &problem);

      // The instances of the object that touch [windowStart, windowEnd), in
      // start order, at most maxInstances (capped at MaxInstances); truncated
      // says the ceiling stopped the expansion before the window's end. False
      // with the reason for an object Summarize would refuse.
      static bool Expand(const ICalComponent &calendar, __int64 windowStart, __int64 windowEnd, size_t maxInstances,
                         std::vector<ICalInstance> &instances, bool &truncated, AnsiString &problem);

      // RFC 4791 section 9.9: whether any instance of the object overlaps the
      // range, by the table for its component kind - a VEVENT by its start and
      // end, a VTODO by DTSTART, DUE, DURATION, COMPLETED and CREATED as the
      // table has it. A VALARM's own time-range is not evaluated.
      static bool OverlapsTimeRange(const ICalComponent &calendar, __int64 rangeStart, __int64 rangeEnd);
   };
}
