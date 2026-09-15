// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// CalDAV (RFC 4791) over the account's calendar, served by the web services
// listener under /dav/ beside CardDAV, so that a phone or a desktop client
// syncs events and tasks with this server. One calendar per account,
// "Calendar", made on first use; its members are the rows of
// hm_calendarobjects (CalendarStore), each one iCalendar object as the
// client sent it.
//
// The tree a client walks, from the RFC 6764 well-known redirect:
//
//    /dav/                                     the context path (CardDAV's
//                                              PROPFIND answers it, with
//                                              calendar-home-set too)
//    /dav/principals/<address>/                the principal: calendar-home-set
//    /dav/calendars/<address>/                 the home: one child collection
//    /dav/calendars/<address>/calendar/        the calendar: getctag,
//                                              sync-token, the reports
//    /dav/calendars/<address>/calendar/<name>  one object, VEVENT or VTODO
//
// Every request is HTTP Basic as the account through AccountLogon - the IMAP
// logon's lockout and auto-ban - and served over HTTPS only, as CardDAV is
// and for the same reason. An object is kept byte for byte as the client
// sent it and served back so, with the strong ETag of those bytes; what the
// store cannot answer for is refused with the reason and nothing stored: an
// object that is not one VCALENDAR, that has no UID, whose UID is already at
// another name in the calendar, that is a VJOURNAL or VFREEBUSY, or whose
// recurrence or time zone the iCalendar module cannot expand.
//
// The reports: calendar-query with comp-filter, time-range (recurrences
// expanded through ICalendar, bounded) and prop-filter with text-match on
// the master's properties; calendar-multiget, an href answered once and
// the whole answer bounded; sync-collection with a real change log - the
// collection's counter and the rows stamped after the token the client
// presents, deletions as 404 responses - so a poll is answered with only
// what changed.

#pragma once

#include "HttpServer.h"

namespace HM
{
   class CalDavServer
   {
   public:
      // The path under /dav/ where the calendar homes live, with the
      // trailing slash: "/dav/calendars/".
      static const char *CalendarsPath;

      // Whether a request-target is under CalendarsPath (or is it, without
      // the slash). Everything else under /dav/ is CardDAV's, which answers
      // the principal for both.
      static bool IsCalendarTarget(const AnsiString &target);

      // Answers one request under CalendarsPath. over_https says whether the
      // request reached the listener over TLS, or a trusted proxy says it did.
      static HttpResponse Handle(const HttpRequest &request, bool over_https);

      // Whether a request under CalendarsPath may be as large as the
      // listener's large cap: a PUT (an object with a long description or many
      // overrides) and a REPORT (a multiget names many hrefs).
      static bool IsLargeRequest(const AnsiString &method, const AnsiString &target);

      // The href of the account's calendar home, for CardDAV's principal to
      // answer calendar-home-set with: "/dav/calendars/<address>/".
      static AnsiString HomeHref(const AnsiString &addressUtf8);
   };
}
