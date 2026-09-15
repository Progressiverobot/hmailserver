// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// The account's calendars, hm_calendars and hm_calendarobjects (schema 6041):
// one row per calendar collection - one per account for now, "Calendar",
// made on first use - and one row per calendar object under it, holding the
// iCalendar text as the client sent it beside what the queries need without
// reading it: the UID, the component kind, the master's start and end and
// the first and last instant of any instance, all as UTC seconds, the ETag,
// and the sync token the collection was at when the row last changed. A
// deleted object stays as a tombstone (objectdeleted = 1, its text emptied)
// so that a sync-collection report can say it was removed; a later PUT to
// the same name takes the row back. The collection's token is a counter,
// stepped on every write under one mutex, so that a client holding token N
// is answered exactly the rows stamped after N. The one surface that reads
// and writes this today is CalDavServer.cpp.

#pragma once

#include <vector>

namespace HM
{
   struct CalendarRecord
   {
      CalendarRecord() : id(0), accountId(0), syncToken(0), created(0) { }

      __int64 id;
      __int64 accountId;
      String name;          // the URI segment: "calendar"
      String displayName;   // "Calendar"
      __int64 syncToken;    // the collection's counter; also its ctag
      __int64 created;      // UTC seconds
   };

   struct CalendarObjectRecord
   {
      CalendarObjectRecord() : id(0), calendarId(0), syncToken(0), start(0), end(0), first(0), last(0), deleted(false), modified(0) { }

      __int64 id;
      __int64 calendarId;
      String uri;           // the resource name the client chose
      String uid;
      String component;     // "VEVENT" or "VTODO"
      String data;          // the iCalendar text as sent; empty for a tombstone
      String etag;          // quoted, as served
      __int64 syncToken;    // the collection's counter when this row last changed
      __int64 start;        // the master's DTSTART, UTC seconds
      __int64 end;          // the master's end
      __int64 first;        // the earliest instance start
      __int64 last;         // the latest instance end; ICalendar::Forever for a recurrence with no end
      bool deleted;
      __int64 modified;     // UTC seconds
   };

   class CalendarStore
   {
   public:
      static const char *DefaultName;          // "calendar"
      static const char *DefaultDisplayName;   // "Calendar"

      // The account's one calendar, created the first time it is asked for.
      // False when the table could not be read or the row not made.
      static bool EnsureDefault(__int64 accountId, CalendarRecord &calendar);

      // The calendar again, for its current token.
      static bool Get(__int64 accountId, __int64 calendarId, CalendarRecord &calendar);

      // The live objects of the calendar (tombstones left out), by uri.
      static bool List(__int64 accountId, __int64 calendarId, std::vector<CalendarObjectRecord> &objects);

      // The live objects whose span touches [rangeStart, rangeEnd): last >
      // rangeStart and first < rangeEnd. What a time-range query narrows to
      // before expanding each.
      static bool ListInRange(__int64 accountId, __int64 calendarId, __int64 rangeStart, __int64 rangeEnd, std::vector<CalendarObjectRecord> &objects);

      // Every row, tombstones included, stamped after the token.
      static bool ListChangedSince(__int64 accountId, __int64 calendarId, __int64 sinceToken, std::vector<CalendarObjectRecord> &objects);

      // One live object by its resource name, or by its UID.
      static bool FindByUri(__int64 accountId, __int64 calendarId, const String &uri, CalendarObjectRecord &object);
      static bool FindByUid(__int64 accountId, __int64 calendarId, const String &uid, CalendarObjectRecord &object);

      // A new object under the client's name, reviving a tombstone of that
      // name if there is one. The collection's token is stepped and answered.
      static bool Insert(__int64 accountId, __int64 calendarId, const CalendarObjectRecord &object, CalendarObjectRecord &inserted, __int64 &token);

      // The object replaced in place: text, UID, kind, span and ETag. The
      // collection's token is stepped and answered.
      static bool Update(__int64 accountId, __int64 calendarId, __int64 objectId, const CalendarObjectRecord &object, __int64 &token);

      // The object turned into a tombstone. False when there was no such live
      // object of this calendar.
      static bool Delete(__int64 accountId, __int64 calendarId, __int64 objectId, __int64 &token);

      static const int MaximumUriLength = 255;
   };
}
