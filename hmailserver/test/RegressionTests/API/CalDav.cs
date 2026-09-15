// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.API
{
   /// <summary>
   ///    CalDAV (RFC 4791) on the web services listener: the account's
   ///    calendar under /dav/calendars/, beside the CardDAV address book, on
   ///    the hm_calendars and hm_calendarobjects rows of schema 6041. The
   ///    shape is CardDav.cs's: raw HTTP/1.0 against the listener, the
   ///    multistatus answers read as XML, and every write checked by reading
   ///    it back, byte for byte where the point is fidelity.
   ///
   ///    The listener is plain HTTP on this bench, and CalDAV refuses Basic
   ///    over plain HTTP, so the requests say what a TLS-terminating proxy in
   ///    front of the listener would say: X-Forwarded-Proto: https. One test
   ///    leaves it out, to pin the refusal.
   ///
   ///    The recurrence expansion is tested twice: through the calendar-query
   ///    time-range, where an instance of a recurring event must be hit and a
   ///    window outside it missed, and through POST /api/v1/calendar/expand
   ///    on the REST listener, which answers the instants the module computes
   ///    - across the March daylight-saving change in two zones, the last
   ///    Friday of a month, BYSETPOS, WKST, EXDATE, a RECURRENCE-ID override,
   ///    a leap-day yearly rule and an all-day event.
   /// </summary>
   [TestFixture]
   public class CalDav : TestFixtureBase
   {
      private const int WebServicesPort = 9105;
      private static int RestPort = 9535;
      private const string UserPassword = "Original-Passw0rd!";
      private const string AdminPassword = "testar";

      private const string NsDav = "DAV:";
      private const string NsCalDav = "urn:ietf:params:xml:ns:caldav";
      private const string NsCalendarServer = "http://calendarserver.org/ns/";

      private Account _account;

      private string Address
      {
         get { return "self@" + _domain.Name; }
      }

      private string Principal
      {
         get { return "/dav/principals/" + Address + "/"; }
      }

      private string Home
      {
         get { return "/dav/calendars/" + Address + "/"; }
      }

      private string Calendar
      {
         get { return Home + "calendar/"; }
      }

      [SetUp]
      public void StartListeners()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, Address, UserPassword);
         _settings.SetAdministratorPassword(AdminPassword);

         IniFileSetting.Write("CalDavRedirectUrl", "");
         IniFileSetting.Write("CardDavRedirectUrl", "");
         IniFileSetting.Write("WebServicesBindAddress", "127.0.0.1");
         IniFileSetting.Write("WebServicesHttpPort", WebServicesPort.ToString());
         IniFileSetting.Write("WebServicesHttpsPort", "0");

         RestPort = RestListener.Start(RestPort);
         IniFileSetting.Write("RestApiCertificateFile", "");
         IniFileSetting.Write("RestApiPrivateKeyFile", "");

         _application.Reinitialize();

         Response probe = Dav("OPTIONS", "/dav/", Auth(Address, UserPassword));
         Assert.AreEqual(200, probe.Status, "The DAV endpoint did not answer OPTIONS. Body: " + probe.Body);
      }

      [TearDown]
      public void StopListeners()
      {
         IniFileSetting.Write("WebServicesHttpPort", "0");
         RestListener.Stop();
         _application.Reinitialize();
      }

      [Test]
      [Description("OPTIONS on the calendar advertises DAV class 1, 3, addressbook and calendar-access, and the methods the calendar answers")]
      public void OptionsAdvertisesCalendarAccess()
      {
         Response options = Dav("OPTIONS", Calendar, Auth(Address, UserPassword));

         Assert.AreEqual(200, options.Status, "Body: " + options.Body);

         string dav = options.Header("DAV");
         Assert.IsNotNull(dav, "No DAV header. Headers: " + options.HeaderText);
         StringAssert.Contains("calendar-access", dav);
         StringAssert.Contains("addressbook", dav);
         StringAssert.Contains("1", dav);

         string allow = options.Header("Allow");
         Assert.IsNotNull(allow, "No Allow header. Headers: " + options.HeaderText);
         StringAssert.Contains("PROPFIND", allow);
         StringAssert.Contains("REPORT", allow);

         // The principal, served by the CardDAV side of the tree, carries the
         // same DAV header: that is where Apple's clients look for it.
         Response principal = Dav("OPTIONS", Principal, Auth(Address, UserPassword));
         Assert.AreEqual(200, principal.Status, "Body: " + principal.Body);
         StringAssert.Contains("calendar-access", principal.Header("DAV") ?? "");
      }

      [Test]
      [Description("A client walks from the context path to the calendar: current-user-principal, calendar-home-set on the principal, then the collection with its CalDAV properties")]
      public void DiscoveryWalksFromTheContextPathToTheCalendar()
      {
         Response root = Propfind("/dav/", "0", "<D:current-user-principal/><C:calendar-home-set/>");
         Assert.AreEqual(207, root.Status, "Body: " + root.Body);
         Assert.AreEqual(Principal, PropText(root, "/dav/", "D:current-user-principal/D:href"), "Body: " + root.Body);
         Assert.AreEqual(Home, PropText(root, "/dav/", "C:calendar-home-set/D:href"), "Body: " + root.Body);

         Response principal = Propfind(Principal, "0", "<C:calendar-home-set/><C:calendar-user-address-set/><D:resourcetype/>");
         Assert.AreEqual(207, principal.Status, "Body: " + principal.Body);
         Assert.AreEqual(Home, PropText(principal, Principal, "C:calendar-home-set/D:href"), "Body: " + principal.Body);
         Assert.AreEqual("mailto:" + Address, PropText(principal, Principal, "C:calendar-user-address-set/D:href"), "Body: " + principal.Body);

         Response home = Propfind(Home, "1", "<D:resourcetype/><D:displayname/>");
         Assert.AreEqual(207, home.Status, "Body: " + home.Body);
         CollectionAssert.Contains(Hrefs(home), Calendar, "The home must list the calendar. Body: " + home.Body);
         Assert.IsNotNull(Prop(home, Calendar, "D:resourcetype/C:calendar"), "The collection must be a CALDAV:calendar. Body: " + home.Body);
         Assert.IsNotNull(Prop(home, Calendar, "D:resourcetype/D:collection"), "Body: " + home.Body);
         Assert.AreEqual("Calendar", PropText(home, Calendar, "D:displayname"), "Body: " + home.Body);

         Response calendar = Propfind(Calendar, "0",
            "<C:supported-calendar-component-set/><C:supported-calendar-data/><CS:getctag/><D:sync-token/><D:supported-report-set/><D:current-user-privilege-set/><C:max-resource-size/>");
         Assert.AreEqual(207, calendar.Status, "Body: " + calendar.Body);
         Assert.IsNotNull(Prop(calendar, Calendar, "C:supported-calendar-component-set/C:comp[@name='VEVENT']"), "Body: " + calendar.Body);
         Assert.IsNotNull(Prop(calendar, Calendar, "C:supported-calendar-component-set/C:comp[@name='VTODO']"), "Body: " + calendar.Body);
         Assert.IsNull(Prop(calendar, Calendar, "C:supported-calendar-component-set/C:comp[@name='VJOURNAL']"), "VJOURNAL is not stored, so not advertised. Body: " + calendar.Body);

         XmlNode data = Prop(calendar, Calendar, "C:supported-calendar-data/C:calendar-data");
         Assert.IsNotNull(data, "Body: " + calendar.Body);
         Assert.AreEqual("text/calendar", data.Attributes["content-type"].Value);
         Assert.AreEqual("2.0", data.Attributes["version"].Value);

         Assert.IsFalse(string.IsNullOrEmpty(PropText(calendar, Calendar, "CS:getctag")), "No getctag. Body: " + calendar.Body);
         Assert.IsFalse(string.IsNullOrEmpty(PropText(calendar, Calendar, "D:sync-token")), "No sync-token. Body: " + calendar.Body);
         Assert.AreEqual("1048576", PropText(calendar, Calendar, "C:max-resource-size"));
         Assert.IsNotNull(Prop(calendar, Calendar, "D:supported-report-set/D:supported-report/D:report/C:calendar-multiget"), "Body: " + calendar.Body);
         Assert.IsNotNull(Prop(calendar, Calendar, "D:supported-report-set/D:supported-report/D:report/C:calendar-query"), "Body: " + calendar.Body);
         Assert.IsNotNull(Prop(calendar, Calendar, "D:supported-report-set/D:supported-report/D:report/D:sync-collection"), "Body: " + calendar.Body);
         Assert.IsNotNull(Prop(calendar, Calendar, "D:current-user-privilege-set/D:privilege/D:write"), "Body: " + calendar.Body);

         // A property this server does not keep is answered in the 404
         // propstat, not invented; calendar-timezone is one.
         Response unknown = Propfind(Calendar, "0", "<C:calendar-timezone/><D:quota-available-bytes/>");
         Assert.AreEqual(207, unknown.Status, "Body: " + unknown.Body);
         Assert.IsNull(Prop(unknown, Calendar, "C:calendar-timezone"), "Body: " + unknown.Body);
         StringAssert.Contains("404 Not Found", unknown.Body);

         // The root lists the calendars collection beside the address books.
         Response listing = Propfind("/dav/", "1", "<D:resourcetype/><D:displayname/>");
         Assert.AreEqual(207, listing.Status, "Body: " + listing.Body);
         CollectionAssert.Contains(Hrefs(listing), "/dav/calendars/", "Body: " + listing.Body);
         Assert.AreEqual("Calendars", PropText(listing, "/dav/calendars/", "D:displayname"));
      }

      [Test]
      [Description("PUT with If-None-Match: * creates an event; GET returns it byte for byte with the same strong ETag; a stale If-Match is 412, the current one updates, and If-None-Match: * on an existing object is 412")]
      public void PutCreatesAnEventReadBackByteForByte()
      {
         string href = Calendar + "6f2c1e2a-3b0c-4d1e-9f7a-0123456789ab.ics";
         string sent = Event("6f2c1e2a-3b0c-4d1e-9f7a-0123456789ab", "20260601T100000Z", "20260601T110000Z", "Planning");
         Response created = Put(href, sent, "If-None-Match: *\r\n");

         Assert.AreEqual(201, created.Status, "Body: " + created.Body);

         string etag = created.Header("ETag");
         Assert.IsNotNull(etag, "A PUT must answer with the new ETag. Headers: " + created.HeaderText);
         Assert.IsTrue(etag.StartsWith("\"") && etag.EndsWith("\"") && etag.Length > 8, "A strong, quoted ETag: " + etag);
         Assert.AreEqual(href, created.Header("Location"), "Headers: " + created.HeaderText);

         Response read = Dav("GET", href, Auth(Address, UserPassword));
         Assert.AreEqual(200, read.Status, "Body: " + read.Body);
         Assert.AreEqual(etag, read.Header("ETag"), "Headers: " + read.HeaderText);
         StringAssert.StartsWith("text/calendar", read.Header("Content-Type"));
         StringAssert.Contains("component=VEVENT", read.Header("Content-Type"));
         Assert.AreEqual(sent, read.Body, "The object comes back as it was sent, the client's PRODID and DTSTAMP included.");

         Response unchanged = Dav("GET", href, Auth(Address, UserPassword), null, "If-None-Match: " + etag + "\r\n");
         Assert.AreEqual(304, unchanged.Status, "Body: " + unchanged.Body);

         Response listing = Propfind(Calendar, "1", "<D:getetag/><D:getcontenttype/>");
         Assert.AreEqual(207, listing.Status, "Body: " + listing.Body);
         CollectionAssert.Contains(Hrefs(listing), href, "Body: " + listing.Body);
         Assert.AreEqual(etag, PropText(listing, href, "D:getetag"), "Body: " + listing.Body);
         StringAssert.StartsWith("text/calendar", PropText(listing, href, "D:getcontenttype"));

         string changed = sent.Replace("SUMMARY:Planning", "SUMMARY:Planning (moved)");
         Response stale = Put(href, changed, "If-Match: \"not-the-etag\"\r\n");
         Assert.AreEqual(412, stale.Status, "Body: " + stale.Body);
         Assert.AreEqual(sent, Dav("GET", href, Auth(Address, UserPassword)).Body, "A refused PUT changes nothing.");

         Response updated = Put(href, changed, "If-Match: " + etag + "\r\n");
         Assert.AreEqual(204, updated.Status, "Body: " + updated.Body);
         string newEtag = updated.Header("ETag");
         Assert.IsNotNull(newEtag, "Headers: " + updated.HeaderText);
         Assert.AreNotEqual(etag, newEtag, "A write that changed the object must change the ETag.");

         Response reread = Dav("GET", href, Auth(Address, UserPassword));
         Assert.AreEqual(changed, reread.Body);
         Assert.AreEqual(newEtag, reread.Header("ETag"));

         Response create = Put(href, changed, "If-None-Match: *\r\n");
         Assert.AreEqual(412, create.Status, "If-None-Match: * on an existing resource must be 412. Body: " + create.Body);
      }

      [Test]
      [Description("DELETE removes the object: GET is then 404, the listing no longer carries it, and the collections are not deletable")]
      public void DeleteRemovesTheObject()
      {
         string href = Calendar + "to-delete.ics";
         Response created = Put(href, Event("to-delete-0001", "20260602T100000Z", "20260602T110000Z", "Gone soon"), "If-None-Match: *\r\n");
         Assert.AreEqual(201, created.Status, "Body: " + created.Body);

         Response deleted = Dav("DELETE", href, Auth(Address, UserPassword));
         Assert.AreEqual(204, deleted.Status, "Body: " + deleted.Body);

         Response gone = Dav("GET", href, Auth(Address, UserPassword));
         Assert.AreEqual(404, gone.Status, "Body: " + gone.Body);

         Response listing = Propfind(Calendar, "1", "<D:getetag/>");
         Assert.AreEqual(207, listing.Status, "Body: " + listing.Body);
         CollectionAssert.DoesNotContain(Hrefs(listing), href, "Body: " + listing.Body);

         Response again = Dav("DELETE", href, Auth(Address, UserPassword));
         Assert.AreEqual(404, again.Status, "Body: " + again.Body);

         Response calendar = Dav("DELETE", Calendar, Auth(Address, UserPassword));
         Assert.AreEqual(403, calendar.Status, "Body: " + calendar.Body);

         // The name can be used again: the tombstone comes back as a live object.
         Response revived = Put(href, Event("to-delete-0002", "20260603T100000Z", "20260603T110000Z", "Back"), "If-None-Match: *\r\n");
         Assert.AreEqual(201, revived.Status, "Body: " + revived.Body);
         Assert.AreEqual(200, Dav("GET", href, Auth(Address, UserPassword)).Status);
      }

      [Test]
      [Description("calendar-query with a time-range hits an expanded instance of a weekly event in Europe/London and misses windows before it starts and after its COUNT runs out; a comp-filter for VTODO does not return the event")]
      public void CalendarQueryByTimeRangeHitsAnExpandedInstance()
      {
         // Ten Mondays at 09:00 London time from 5 January 2026, all in GMT.
         string weekly = Calendar + "weekly.ics";
         string weeklyText = Event("weekly-0001", "20260105T090000", "20260105T100000", "Stand-up",
            "RRULE:FREQ=WEEKLY;COUNT=10", "Europe/London");
         Assert.AreEqual(201, Put(weekly, weeklyText, "If-None-Match: *\r\n").Status);

         string single = Calendar + "single.ics";
         Assert.AreEqual(201, Put(single, Event("single-0001", "20260701T120000Z", "20260701T130000Z", "Lunch"), "If-None-Match: *\r\n").Status);

         // The third Monday, 19 January: hit, and the July event is not in it.
         Response hit = Report(Calendar, Query("VEVENT", "20260119T000000Z", "20260120T000000Z"));
         Assert.AreEqual(207, hit.Status, "Body: " + hit.Body);
         CollectionAssert.AreEquivalent(new[] { weekly }, Hrefs(hit), "Body: " + hit.Body);
         StringAssert.Contains("RRULE:FREQ=WEEKLY;COUNT=10", PropText(hit, weekly, "C:calendar-data"), "The object is served whole, as stored.");
         Assert.IsFalse(string.IsNullOrEmpty(PropText(hit, weekly, "D:getetag")));

         // Before the first instance: nothing.
         Response before = Report(Calendar, Query("VEVENT", "20260101T000000Z", "20260104T000000Z"));
         Assert.AreEqual(207, before.Status, "Body: " + before.Body);
         Assert.AreEqual(0, Hrefs(before).Count, "Body: " + before.Body);

         // After the tenth Monday (9 March): nothing.
         Response after = Report(Calendar, Query("VEVENT", "20260316T000000Z", "20260323T000000Z"));
         Assert.AreEqual(207, after.Status, "Body: " + after.Body);
         Assert.AreEqual(0, Hrefs(after).Count, "Body: " + after.Body);

         // A window that holds the July event and the last Monday.
         Response both = Report(Calendar, Query("VEVENT", "20260309T000000Z", "20260801T000000Z"));
         Assert.AreEqual(207, both.Status, "Body: " + both.Body);
         CollectionAssert.AreEquivalent(new[] { weekly, single }, Hrefs(both), "Body: " + both.Body);

         // A range around the instance's exact start: the hour 09:00-10:00 GMT.
         Response exact = Report(Calendar, Query("VEVENT", "20260119T093000Z", "20260119T094500Z"));
         Assert.AreEqual(207, exact.Status, "Body: " + exact.Body);
         CollectionAssert.AreEquivalent(new[] { weekly }, Hrefs(exact), "The instance overlaps the range. Body: " + exact.Body);

         // The event is not a task.
         Response todos = Report(Calendar, Query("VTODO", "20260101T000000Z", "20270101T000000Z"));
         Assert.AreEqual(207, todos.Status, "Body: " + todos.Body);
         Assert.AreEqual(0, Hrefs(todos).Count, "Body: " + todos.Body);

         // No filter: everything.
         Response all = Report(Calendar, Query(null, null, null));
         Assert.AreEqual(207, all.Status, "Body: " + all.Body);
         CollectionAssert.AreEquivalent(new[] { weekly, single }, Hrefs(all), "Body: " + all.Body);

         // prop-filter on UID with a text-match, and one that matches nothing.
         Response byUid = Report(Calendar,
            "<C:calendar-query xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:caldav\"><D:prop><D:getetag/></D:prop>" +
            "<C:filter><C:comp-filter name=\"VCALENDAR\"><C:comp-filter name=\"VEVENT\"><C:prop-filter name=\"UID\"><C:text-match collation=\"i;octet\">single-0001</C:text-match></C:prop-filter></C:comp-filter></C:comp-filter></C:filter>" +
            "</C:calendar-query>");
         Assert.AreEqual(207, byUid.Status, "Body: " + byUid.Body);
         CollectionAssert.AreEquivalent(new[] { single }, Hrefs(byUid), "Body: " + byUid.Body);

         Response bySummary = Report(Calendar,
            "<C:calendar-query xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:caldav\"><D:prop><D:getetag/></D:prop>" +
            "<C:filter><C:comp-filter name=\"VCALENDAR\"><C:comp-filter name=\"VEVENT\"><C:prop-filter name=\"SUMMARY\"><C:text-match>STAND</C:text-match></C:prop-filter></C:comp-filter></C:comp-filter></C:filter>" +
            "</C:calendar-query>");
         Assert.AreEqual(207, bySummary.Status, "Body: " + bySummary.Body);
         CollectionAssert.AreEquivalent(new[] { weekly }, Hrefs(bySummary), "Case-insensitive under the default collation. Body: " + bySummary.Body);

         // A filter that does not start at VCALENDAR is refused as such.
         Response badFilter = Report(Calendar,
            "<C:calendar-query xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:caldav\"><D:prop><D:getetag/></D:prop>" +
            "<C:filter><C:comp-filter name=\"VEVENT\"/></C:filter></C:calendar-query>");
         Assert.AreEqual(403, badFilter.Status, "Body: " + badFilter.Body);
         StringAssert.Contains("valid-filter", badFilter.Body);
      }

      [Test]
      [Description("calendar-multiget answers each href once, whatever the client repeated, and 404 for one that is not there")]
      public void MultigetAnswersEachHrefOnce()
      {
         string a = Calendar + "a.ics";
         string b = Calendar + "b.ics";
         Assert.AreEqual(201, Put(a, Event("multi-a", "20260701T120000Z", "20260701T130000Z", "A"), "If-None-Match: *\r\n").Status);
         Assert.AreEqual(201, Put(b, Event("multi-b", "20260702T120000Z", "20260702T130000Z", "B"), "If-None-Match: *\r\n").Status);
         string bogus = Calendar + "no-such.ics";

         Response multiget = Report(Calendar,
            "<C:calendar-multiget xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:caldav\">" +
            "<D:prop><D:getetag/><C:calendar-data/></D:prop>" +
            "<D:href>" + a + "</D:href><D:href>" + a + "</D:href><D:href>" + bogus + "</D:href><D:href>" + b + "</D:href>" +
            "</C:calendar-multiget>");
         Assert.AreEqual(207, multiget.Status, "Body: " + multiget.Body);

         List<string> hrefs = Hrefs(multiget);
         Assert.AreEqual(3, hrefs.Count, "The repeated href is answered once. Body: " + multiget.Body);
         CollectionAssert.AreEquivalent(new[] { a, bogus, b }, hrefs, "Body: " + multiget.Body);
         StringAssert.Contains("UID:multi-a", PropText(multiget, a, "C:calendar-data"));
         StringAssert.Contains("UID:multi-b", PropText(multiget, b, "C:calendar-data"));
         StringAssert.Contains("404 Not Found", ResponseStatus(multiget, bogus));

         // A report this server does not have is refused as such.
         Response unsupported = Report(Calendar, "<C:free-busy-query xmlns:C=\"urn:ietf:params:xml:ns:caldav\"/>");
         Assert.AreEqual(403, unsupported.Status, "Body: " + unsupported.Body);
         StringAssert.Contains("supported-report", unsupported.Body);
      }

      [Test]
      [Description("sync-collection lists the whole calendar on an empty token, only the changes since a token it issued - a deletion as a 404 response - and refuses a token it never issued; getctag changes with every write")]
      public void SyncCollectionReportsChangesAndDeletions()
      {
         string a = Calendar + "sync-a.ics";
         string b = Calendar + "sync-b.ics";
         Assert.AreEqual(201, Put(a, Event("sync-a", "20260701T120000Z", "20260701T130000Z", "A"), "If-None-Match: *\r\n").Status);

         Response initial = Report(Calendar, Sync(""));
         Assert.AreEqual(207, initial.Status, "Body: " + initial.Body);
         CollectionAssert.AreEquivalent(new[] { a }, Hrefs(initial), "Body: " + initial.Body);
         string token = SyncToken(initial);
         Assert.IsFalse(string.IsNullOrEmpty(token), "No sync-token. Body: " + initial.Body);

         Response nothingNew = Report(Calendar, Sync(token));
         Assert.AreEqual(207, nothingNew.Status, "Body: " + nothingNew.Body);
         Assert.AreEqual(0, Hrefs(nothingNew).Count, "The current token means no changes. Body: " + nothingNew.Body);
         Assert.AreEqual(token, SyncToken(nothingNew));

         string ctagBefore = PropText(Propfind(Calendar, "0", "<CS:getctag/>"), Calendar, "CS:getctag");

         Assert.AreEqual(201, Put(b, Event("sync-b", "20260702T120000Z", "20260702T130000Z", "B"), "If-None-Match: *\r\n").Status);

         string ctagAfter = PropText(Propfind(Calendar, "0", "<CS:getctag/>"), Calendar, "CS:getctag");
         Assert.AreNotEqual(ctagBefore, ctagAfter, "A write must change the ctag.");

         // Only the new object since the old token.
         Response changed = Report(Calendar, Sync(token));
         Assert.AreEqual(207, changed.Status, "Body: " + changed.Body);
         CollectionAssert.AreEquivalent(new[] { b }, Hrefs(changed), "Body: " + changed.Body);
         Assert.IsFalse(string.IsNullOrEmpty(PropText(changed, b, "D:getetag")));
         string token2 = SyncToken(changed);
         Assert.AreNotEqual(token, token2);

         // A deletion is reported as a 404 response for the href.
         Assert.AreEqual(204, Dav("DELETE", a, Auth(Address, UserPassword)).Status);
         Response afterDelete = Report(Calendar, Sync(token2));
         Assert.AreEqual(207, afterDelete.Status, "Body: " + afterDelete.Body);
         CollectionAssert.AreEquivalent(new[] { a }, Hrefs(afterDelete), "Body: " + afterDelete.Body);
         StringAssert.Contains("404 Not Found", ResponseStatus(afterDelete, a));
         string token3 = SyncToken(afterDelete);
         Assert.AreNotEqual(token2, token3);

         // From the very first token, both the change and the deletion.
         Response sinceStart = Report(Calendar, Sync(token));
         Assert.AreEqual(207, sinceStart.Status, "Body: " + sinceStart.Body);
         CollectionAssert.AreEquivalent(new[] { a, b }, Hrefs(sinceStart), "Body: " + sinceStart.Body);
         StringAssert.Contains("404 Not Found", ResponseStatus(sinceStart, a));

         // A token this calendar never issued is refused with the precondition.
         Response stale = Report(Calendar, Sync("urn:x-hmailserver:caldav-sync:999999999"));
         Assert.AreEqual(403, stale.Status, "Body: " + stale.Body);
         StringAssert.Contains("valid-sync-token", stale.Body);

         Response foreign = Report(Calendar, Sync("http://example.test/ns/sync/12"));
         Assert.AreEqual(403, foreign.Status, "Body: " + foreign.Body);
         StringAssert.Contains("valid-sync-token", foreign.Body);
      }

      [Test]
      [Description("What the calendar cannot hold is refused with the CalDAV precondition and the reason, and nothing is stored: not a calendar, no UID, a UID already at another name, a VJOURNAL, the wrong content type, a rule the server does not expand; a wrong password is 401 and plain HTTP is 403")]
      public void RefusalsNameWhatIsWrongAndStoreNothing()
      {
         string kept = Calendar + "kept.ics";
         Assert.AreEqual(201, Put(kept, Event("kept-0001", "20260701T120000Z", "20260701T130000Z", "Kept"), "If-None-Match: *\r\n").Status);

         Response notACalendar = Put(Calendar + "text.ics", "this is not a calendar", "If-None-Match: *\r\n");
         Assert.AreEqual(403, notACalendar.Status, "Body: " + notACalendar.Body);
         StringAssert.Contains("valid-calendar-data", notACalendar.Body);

         string noUid = Event("x", "20260701T120000Z", "20260701T130000Z", "No UID").Replace("UID:x\r\n", "");
         Response withoutUid = Put(Calendar + "no-uid.ics", noUid, "If-None-Match: *\r\n");
         Assert.AreEqual(403, withoutUid.Status, "Body: " + withoutUid.Body);
         StringAssert.Contains("valid-calendar-object-resource", withoutUid.Body);
         StringAssert.Contains("UID", withoutUid.Body);

         Response duplicate = Put(Calendar + "another-name.ics", Event("kept-0001", "20260702T120000Z", "20260702T130000Z", "Copy"), "If-None-Match: *\r\n");
         Assert.AreEqual(409, duplicate.Status, "Body: " + duplicate.Body);
         StringAssert.Contains("no-uid-conflict", duplicate.Body);
         StringAssert.Contains(kept, duplicate.Body);

         string journal = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//A phone//EN\r\nBEGIN:VJOURNAL\r\nUID:journal-0001\r\nDTSTAMP:20260101T000000Z\r\nDTSTART;VALUE=DATE:20260701\r\nSUMMARY:Diary\r\nEND:VJOURNAL\r\nEND:VCALENDAR\r\n";
         Response journalRefused = Put(Calendar + "journal.ics", journal, "If-None-Match: *\r\n");
         Assert.AreEqual(403, journalRefused.Status, "Body: " + journalRefused.Body);
         StringAssert.Contains("supported-calendar-component", journalRefused.Body);

         Response wrongType = Raw(WebServicesPort, "PUT", Calendar + "json.ics", Auth(Address, UserPassword),
            "{\"summary\":\"Not a calendar\"}", "application/json", "X-Forwarded-Proto: https\r\n");
         Assert.AreEqual(415, wrongType.Status, "Body: " + wrongType.Body);

         Response hourly = Put(Calendar + "hourly.ics", Event("hourly-0001", "20260701T120000Z", "20260701T130000Z", "Hourly", "RRULE:FREQ=HOURLY;COUNT=5"), "If-None-Match: *\r\n");
         Assert.AreEqual(403, hourly.Status, "Body: " + hourly.Body);
         StringAssert.Contains("valid-calendar-data", hourly.Body);
         StringAssert.Contains("HOURLY", hourly.Body);

         Response unknownZone = Put(Calendar + "zone.ics", Event("zone-0001", "20260701T120000", "20260701T130000", "Where", null, "Mars/Olympus_Mons"), "If-None-Match: *\r\n");
         Assert.AreEqual(403, unknownZone.Status, "Body: " + unknownZone.Body);
         StringAssert.Contains("Mars/Olympus_Mons", unknownZone.Body);

         Response wrong = Propfind(Calendar, "0", "<D:displayname/>", Auth(Address, "not-the-password"));
         Assert.AreEqual(401, wrong.Status, "Body: " + wrong.Body);
         StringAssert.Contains("Basic", wrong.Header("WWW-Authenticate") ?? "", "Headers: " + wrong.HeaderText);

         Response plain = Raw(WebServicesPort, "PROPFIND", Calendar, Auth(Address, UserPassword),
            "<D:propfind xmlns:D=\"DAV:\"><D:prop><D:displayname/></D:prop></D:propfind>", "application/xml", "Depth: 0\r\n");
         Assert.AreEqual(403, plain.Status, "Body: " + plain.Body);
         StringAssert.Contains("HTTPS", plain.Body);
         StringAssert.Contains("X-Forwarded-Proto", plain.Body);

         // Another account's calendar is not there for them.
         string other = "other@" + _domain.Name;
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, other, UserPassword);
         Response theirs = Propfind(Calendar, "1", "<D:getetag/>", Auth(other, UserPassword));
         Assert.AreEqual(404, theirs.Status, "Body: " + theirs.Body);

         // Only the one object was ever stored.
         Response listing = Propfind(Calendar, "1", "<D:getetag/>");
         Assert.AreEqual(207, listing.Status, "Body: " + listing.Body);
         CollectionAssert.AreEquivalent(new[] { Calendar, kept }, Hrefs(listing), "Body: " + listing.Body);
      }

      [Test]
      [Description("A VTODO is stored, listed with its component in the content type, answered by a VTODO comp-filter and not by a VEVENT one, and matched by its DUE in a time-range")]
      public void AVTodoIsStoredAndListed()
      {
         string href = Calendar + "task.ics";
         string todo = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//A phone//EN\r\nBEGIN:VTODO\r\nUID:task-0001\r\nDTSTAMP:20260101T000000Z\r\nDUE:20260715T170000Z\r\nSUMMARY:File the return\r\nSTATUS:NEEDS-ACTION\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
         Response created = Put(href, todo, "If-None-Match: *\r\n");
         Assert.AreEqual(201, created.Status, "Body: " + created.Body);

         Response read = Dav("GET", href, Auth(Address, UserPassword));
         Assert.AreEqual(todo, read.Body);
         StringAssert.Contains("component=VTODO", read.Header("Content-Type"));

         Response listing = Propfind(Calendar, "1", "<D:getcontenttype/>");
         StringAssert.Contains("component=VTODO", PropText(listing, href, "D:getcontenttype"));

         Response todos = Report(Calendar, Query("VTODO", null, null));
         Assert.AreEqual(207, todos.Status, "Body: " + todos.Body);
         CollectionAssert.AreEquivalent(new[] { href }, Hrefs(todos), "Body: " + todos.Body);

         Response events = Report(Calendar, Query("VEVENT", null, null));
         Assert.AreEqual(207, events.Status, "Body: " + events.Body);
         Assert.AreEqual(0, Hrefs(events).Count, "Body: " + events.Body);

         // The DUE falls in the window (RFC 4791 section 9.9, the DUE-only row).
         Response due = Report(Calendar, Query("VTODO", "20260715T000000Z", "20260716T000000Z"));
         Assert.AreEqual(207, due.Status, "Body: " + due.Body);
         CollectionAssert.AreEquivalent(new[] { href }, Hrefs(due), "Body: " + due.Body);

         Response notDue = Report(Calendar, Query("VTODO", "20260801T000000Z", "20260901T000000Z"));
         Assert.AreEqual(207, notDue.Status, "Body: " + notDue.Body);
         Assert.AreEqual(0, Hrefs(notDue).Count, "Body: " + notDue.Body);
      }

      [Test]
      [Description("Rules written to spin - a daily rule with a hundred million occurrences queried two centuries out, a yearly rule whose parts never agree, a monthly rule with every weekday and BYSETPOS - are stored and answered in bounded time")]
      public void AHostileRruleIsAnsweredInBoundedTime()
      {
         Assert.AreEqual(201, Put(Calendar + "daily.ics", Event("hostile-daily", "20260101T090000Z", "20260101T093000Z", "Daily forever", "RRULE:FREQ=DAILY;COUNT=100000000"), "If-None-Match: *\r\n").Status);
         Assert.AreEqual(201, Put(Calendar + "never.ics", Event("hostile-never", "20260101T090000Z", "20260101T093000Z", "Never", "RRULE:FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=30"), "If-None-Match: *\r\n").Status);
         Assert.AreEqual(201, Put(Calendar + "setpos.ics", Event("hostile-setpos", "20260101T090000Z", "20260101T093000Z", "Every weekday", "RRULE:FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR,SA,SU;BYSETPOS=1,-1;COUNT=99999999"), "If-None-Match: *\r\n").Status);
         Assert.AreEqual(201, Put(Calendar + "sparse.ics", Event("hostile-sparse", "20260101T090000Z", "20260101T093000Z", "Every millennium", "RRULE:FREQ=YEARLY;INTERVAL=1000;COUNT=100000"), "If-None-Match: *\r\n").Status);

         var watch = Stopwatch.StartNew();
         Response far = Report(Calendar, Query("VEVENT", "22000101T000000Z", "22000201T000000Z"));
         watch.Stop();
         Assert.AreEqual(207, far.Status, "Body: " + far.Body);
         Assert.Less(watch.ElapsedMilliseconds, 20000, "A query over four hostile rules must answer in bounded time.");
         CollectionAssert.Contains(Hrefs(far), Calendar + "daily.ics", "The daily rule reaches 2200. Body: " + far.Body);
         CollectionAssert.DoesNotContain(Hrefs(far), Calendar + "never.ics", "Body: " + far.Body);

         watch.Restart();
         Response near = Report(Calendar, Query("VEVENT", "20260301T000000Z", "20260302T000000Z"));
         watch.Stop();
         Assert.AreEqual(207, near.Status, "Body: " + near.Body);
         Assert.Less(watch.ElapsedMilliseconds, 20000);
         CollectionAssert.AreEquivalent(new[] { Calendar + "daily.ics", Calendar + "setpos.ics" }, Hrefs(near), "1 March 2026 is the first day of its month. Body: " + near.Body);
      }

      [Test]
      [Description("POST /api/v1/calendar/expand answers the instants the recurrence module computes: a weekly 09:00 across the London and New York daylight-saving changes (the second through the object's own VTIMEZONE), the last Friday of the month, BYSETPOS, the RFC 5545 WKST example, EXDATE with a RECURRENCE-ID override, a leap-day yearly rule, an all-day event, a limit, and a rule the server refuses")]
      public void ExpansionOverRestPlacesInstancesAcrossDstAndByDay()
      {
         // London: 09:00 on Mondays from 16 March 2026; BST from 29 March.
         List<string> london = Starts(Expand(Event("dst-london", "20260316T090000", "20260316T100000", "Weekly", "RRULE:FREQ=WEEKLY;COUNT=4", "Europe/London"), null, null, null));
         CollectionAssert.AreEqual(new[] { "20260316T090000Z", "20260323T090000Z", "20260330T080000Z", "20260406T080000Z" }, london);

         // New York, with the zone carried in the object: 09:00 on Mondays
         // from 2 March 2026; EDT from 8 March.
         string newYork =
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//A phone//EN\r\n" +
            "BEGIN:VTIMEZONE\r\nTZID:Custom/East\r\n" +
            "BEGIN:STANDARD\r\nDTSTART:20071104T020000\r\nRRULE:FREQ=YEARLY;BYMONTH=11;BYDAY=1SU\r\nTZOFFSETFROM:-0400\r\nTZOFFSETTO:-0500\r\nEND:STANDARD\r\n" +
            "BEGIN:DAYLIGHT\r\nDTSTART:20070311T020000\r\nRRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=2SU\r\nTZOFFSETFROM:-0500\r\nTZOFFSETTO:-0400\r\nEND:DAYLIGHT\r\n" +
            "END:VTIMEZONE\r\n" +
            "BEGIN:VEVENT\r\nUID:dst-east\r\nDTSTAMP:20260101T000000Z\r\nDTSTART;TZID=Custom/East:20260302T090000\r\nDTEND;TZID=Custom/East:20260302T100000\r\nRRULE:FREQ=WEEKLY;COUNT=3\r\nSUMMARY:Weekly\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
         CollectionAssert.AreEqual(new[] { "20260302T140000Z", "20260309T130000Z", "20260316T130000Z" }, Starts(Expand(newYork, null, null, null)));

         // The last Friday of each month.
         CollectionAssert.AreEqual(new[] { "20260130T090000Z", "20260227T090000Z", "20260327T090000Z" },
            Starts(Expand(Event("last-friday", "20260130T090000Z", "20260130T100000Z", "Payday", "RRULE:FREQ=MONTHLY;BYDAY=-1FR;COUNT=3"), null, null, null)));

         // The last weekday of each month, by BYSETPOS.
         CollectionAssert.AreEqual(new[] { "20260130T090000Z", "20260227T090000Z", "20260331T090000Z" },
            Starts(Expand(Event("last-weekday", "20260130T090000Z", "20260130T100000Z", "Close", "RRULE:FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-1;COUNT=3"), null, null, null)));

         // RFC 5545 section 3.3.10: the WKST example, both ways.
         CollectionAssert.AreEqual(new[] { "19970805T090000Z", "19970810T090000Z", "19970819T090000Z", "19970824T090000Z" },
            Starts(Expand(Event("wkst-mo", "19970805T090000Z", "19970805T100000Z", "WKST", "RRULE:FREQ=WEEKLY;INTERVAL=2;COUNT=4;BYDAY=TU,SU;WKST=MO"), null, null, null)));
         CollectionAssert.AreEqual(new[] { "19970805T090000Z", "19970817T090000Z", "19970819T090000Z", "19970831T090000Z" },
            Starts(Expand(Event("wkst-su", "19970805T090000Z", "19970805T100000Z", "WKST", "RRULE:FREQ=WEEKLY;INTERVAL=2;COUNT=4;BYDAY=TU,SU;WKST=SU"), null, null, null)));

         // Five days, the third excluded, the fourth moved to the tenth.
         string moved =
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//A phone//EN\r\n" +
            "BEGIN:VEVENT\r\nUID:moved\r\nDTSTAMP:20260101T000000Z\r\nDTSTART:20260601T100000Z\r\nDTEND:20260601T110000Z\r\nRRULE:FREQ=DAILY;COUNT=5\r\nEXDATE:20260603T100000Z\r\nSUMMARY:Daily\r\nEND:VEVENT\r\n" +
            "BEGIN:VEVENT\r\nUID:moved\r\nDTSTAMP:20260101T000000Z\r\nRECURRENCE-ID:20260604T100000Z\r\nDTSTART:20260610T140000Z\r\nDTEND:20260610T150000Z\r\nSUMMARY:Daily (moved)\r\nEND:VEVENT\r\n" +
            "END:VCALENDAR\r\n";
         string movedBody = Expand(moved, null, null, null);
         CollectionAssert.AreEqual(new[] { "20260601T100000Z", "20260602T100000Z", "20260605T100000Z", "20260610T140000Z" }, Starts(movedBody));
         StringAssert.Contains("\"recurrence_id\":\"20260604T100000Z\"", movedBody);
         StringAssert.Contains("\"summary\":\"Daily (moved)\"", movedBody);

         // 29 February, every four years.
         CollectionAssert.AreEqual(new[] { "20240229T120000Z", "20280229T120000Z", "20320229T120000Z" },
            Starts(Expand(Event("leap", "20240229T120000Z", "20240229T130000Z", "Leap", "RRULE:FREQ=YEARLY;COUNT=3"), null, null, null)));

         // An all-day event: midnight to midnight, flagged.
         string allDay = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//A phone//EN\r\nBEGIN:VEVENT\r\nUID:all-day\r\nDTSTAMP:20260101T000000Z\r\nDTSTART;VALUE=DATE:20260601\r\nSUMMARY:Holiday\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
         string allDayBody = Expand(allDay, null, null, null);
         CollectionAssert.AreEqual(new[] { "20260601T000000Z" }, Starts(allDayBody));
         StringAssert.Contains("\"end\":\"20260602T000000Z\"", allDayBody);
         StringAssert.Contains("\"all_day\":true", allDayBody);

         // The window narrows, and a limit truncates.
         CollectionAssert.AreEqual(new[] { "20260323T090000Z", "20260330T080000Z" },
            Starts(Expand(Event("dst-london", "20260316T090000", "20260316T100000", "Weekly", "RRULE:FREQ=WEEKLY;COUNT=4", "Europe/London"), "20260320T000000Z", "20260401T000000Z", null)));
         string limited = Expand(Event("endless", "20260101T090000Z", "20260101T093000Z", "Daily", "RRULE:FREQ=DAILY"), null, null, 3);
         Assert.AreEqual(3, Starts(limited).Count, limited);
         StringAssert.Contains("\"truncated\":true", limited);
         StringAssert.Contains("\"last\":\"\"", limited);

         // What the module does not expand is refused with the reason.
         (int status, string body) = RestRaw("POST", "/api/v1/calendar/expand", "{\"calendar\":" + JsonString(Event("by-hour", "20260101T090000Z", "20260101T093000Z", "Hourly", "RRULE:FREQ=DAILY;BYHOUR=9,13")) + "}");
         Assert.AreEqual(400, status, body);
         StringAssert.Contains("BYHOUR", body);

         (int missingStatus, string missingBody) = RestRaw("POST", "/api/v1/calendar/expand", "{\"start\":\"20260101T000000Z\"}");
         Assert.AreEqual(400, missingStatus, missingBody);
         StringAssert.Contains("calendar must be a string", missingBody);
      }

      // ---------------------------------------------------------------- helpers

      // One VEVENT in a VCALENDAR. With a zone, DTSTART and DTEND carry TZID
      // and the times are wall-clock; without, they are as given (UTC or
      // floating).
      [Test]
      [Description("An object longer than 4,000 characters is stored, read back byte for byte and updated. SQL Server " +
                   "Compact refused any string parameter over 4,000 characters, even into ntext, so every such PUT was " +
                   "a 500 on the bench while the server advertised a one-megabyte max-resource-size.")]
      public void AnObjectLongerThanFourThousandCharactersIsStoredAndUpdated()
      {
         const string uid = "long-object-0001";
         string href = Calendar + uid + ".ics";

         var filler = new System.Text.StringBuilder();
         for (int line = 0; line < 150; line++)
            filler.Append("X-FILLER-" + line.ToString("D3") + ":" + new string((char) ('a' + line % 26), 60) + "\r\n");

         string sent = Event(uid, "20260701T100000Z", "20260701T110000Z", "Long").Replace("SEQUENCE:0\r\n", "SEQUENCE:0\r\n" + filler);
         Assert.Greater(sent.Length, 9000, "The object must be well past 4,000 characters for this to prove anything.");

         Response created = Put(href, sent, "If-None-Match: *\r\n");
         Assert.AreEqual(201, created.Status, "Body: " + created.Body);

         Response read = Dav("GET", href, Auth(Address, UserPassword));
         Assert.AreEqual(200, read.Status, "Body: " + read.Body);
         Assert.AreEqual(sent, read.Body, "A long object must come back as it was sent.");

         string changed = sent.Replace("SUMMARY:Long", "SUMMARY:Long (moved)");
         Response updated = Put(href, changed, "If-Match: " + created.Header("ETag") + "\r\n");
         Assert.AreEqual(204, updated.Status, "Body: " + updated.Body);
         Assert.AreEqual(changed, Dav("GET", href, Auth(Address, UserPassword)).Body);
      }

      [Test]
      [Description("Two resource names that differ only in case are two objects. The lookup compared under the column's " +
                   "collation, which ignores case on SQL Server and Compact, so a PUT to one found the other and " +
                   "overwrote it. The twin is now its own object where the database allows it, and a 409 where not.")]
      public void NamesThatDifferOnlyInCaseAreTwoObjects()
      {
         string lower = Calendar + "case-twin.ics";
         string upper = Calendar + "CASE-TWIN.ics";

         string first = Event("case-twin-lower", "20260801T100000Z", "20260801T110000Z", "Lower");
         string second = Event("case-twin-upper", "20260801T120000Z", "20260801T130000Z", "Upper");

         Response createdLower = Put(lower, first, "If-None-Match: *\r\n");
         Assert.AreEqual(201, createdLower.Status, "Body: " + createdLower.Body);

         // Without If-None-Match: a client that PUTs a name it believes is new. The
         // lookup used to find the lower-case object for it and overwrite it.
         Response twin = Put(upper, second);

         Assert.AreEqual(first, Dav("GET", lower, Auth(Address, UserPassword)).Body, "The first object was overwritten by its case twin.");

         // The database decides whether two names that differ only in case can both
         // exist: PostgreSQL compares exactly, the others do not. Either the twin is
         // its own object, or it is refused with 409 - never a 500, never the other.
         if (twin.Status == 201)
         {
            Assert.AreEqual(second, Dav("GET", upper, Auth(Address, UserPassword)).Body);
         }
         else
         {
            Assert.AreEqual(409, twin.Status, "Body: " + twin.Body);
            StringAssert.Contains("differs from this one only in case", twin.Body);
            Assert.AreEqual(404, Dav("GET", upper, Auth(Address, UserPassword)).Status,
               "A refused twin must not be readable at its own name through the other object.");
         }
      }

      [Test]
      [Description("A deleted object does not block a name that differs from it only in case: the PUT is a create, " +
                   "on every database, and the object reads back under the name it was created with.")]
      public void ADeletedObjectDoesNotBlockItsCaseTwin()
      {
         string lower = Calendar + "tombstone-twin.ics";
         string upper = Calendar + "TOMBSTONE-TWIN.ics";

         Assert.AreEqual(201, Put(lower, Event("tombstone-twin-1", "20260901T100000Z", "20260901T110000Z", "Gone"), "If-None-Match: *\r\n").Status);
         Assert.AreEqual(204, Dav("DELETE", lower, Auth(Address, UserPassword)).Status);

         string second = Event("tombstone-twin-2", "20260901T120000Z", "20260901T130000Z", "Back");
         Response created = Put(upper, second, "If-None-Match: *\r\n");
         Assert.AreEqual(201, created.Status, "A deleted object's case twin was refused. Body: " + created.Body);

         Assert.AreEqual(second, Dav("GET", upper, Auth(Address, UserPassword)).Body);
      }

      [Test]
      [Description("A request for an object name far longer than the column is a 404, and the server is still answering " +
                   "afterwards. Bound as a long string and compared with the name column, such a value crashed the SQL " +
                   "Server Compact provider inside the service.")]
      public void AnObjectNameLongerThanTheColumnIsNotFoundAndTheServerSurvives()
      {
         string longName = Calendar + new string('n', 20000) + ".ics";

         for (int attempt = 0; attempt < 3; attempt++)
            Assert.AreEqual(404, Dav("GET", longName, Auth(Address, UserPassword)).Status);

         Response listing = Propfind(Calendar, "1", "<D:getetag/>");
         Assert.AreEqual(207, listing.Status, "The server stopped answering after an over-long object name. Body: " + listing.Body);
      }

      private static string Event(string uid, string dtstart, string dtend, string summary, string rrule = null, string tzid = null)
      {
         string zone = tzid == null ? "" : ";TZID=" + tzid;
         return string.Join("\r\n", new[]
         {
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//A phone//EN",
            "BEGIN:VEVENT",
            "UID:" + uid,
            "DTSTAMP:20260101T000000Z",
            "DTSTART" + zone + ":" + dtstart,
            "DTEND" + zone + ":" + dtend,
            "SUMMARY:" + summary,
            rrule ?? "SEQUENCE:0",
            "END:VEVENT",
            "END:VCALENDAR",
            ""
         });
      }

      private static string Query(string component, string start, string end)
      {
         string range = start == null && end == null ? "" :
            "<C:time-range" + (start == null ? "" : " start=\"" + start + "\"") + (end == null ? "" : " end=\"" + end + "\"") + "/>";
         string filter = component == null ? "" :
            "<C:filter><C:comp-filter name=\"VCALENDAR\"><C:comp-filter name=\"" + component + "\">" + range + "</C:comp-filter></C:comp-filter></C:filter>";
         return "<C:calendar-query xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:caldav\">" +
                "<D:prop><D:getetag/><C:calendar-data/></D:prop>" + filter + "</C:calendar-query>";
      }

      private static string Sync(string token)
      {
         return "<D:sync-collection xmlns:D=\"DAV:\"><D:sync-token>" + token + "</D:sync-token><D:sync-level>1</D:sync-level>" +
                "<D:prop><D:getetag/></D:prop></D:sync-collection>";
      }

      private Response Propfind(string path, string depth, string props, string authorization = null)
      {
         return Dav("PROPFIND", path, authorization ?? Auth(Address, UserPassword),
            "<D:propfind xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:caldav\" xmlns:CS=\"http://calendarserver.org/ns/\"><D:prop>" + props + "</D:prop></D:propfind>",
            "Depth: " + depth + "\r\n");
      }

      private Response Report(string path, string body)
      {
         return Dav("REPORT", path, Auth(Address, UserPassword), body, "Depth: 1\r\n");
      }

      private Response Put(string path, string calendar, string extraHeaders = null)
      {
         return Raw(WebServicesPort, "PUT", path, Auth(Address, UserPassword), calendar, "text/calendar; charset=utf-8",
            "X-Forwarded-Proto: https\r\n" + (extraHeaders ?? ""));
      }

      private static Response Dav(string method, string path, string authorization, string body = null, string extraHeaders = null)
      {
         return Raw(WebServicesPort, method, path, authorization, body, "application/xml; charset=utf-8",
            "X-Forwarded-Proto: https\r\n" + (extraHeaders ?? ""));
      }

      // The expansion route on the REST listener, as the administrator.
      private static string Expand(string calendar, string start, string end, int? limit)
      {
         string body = "{\"calendar\":" + JsonString(calendar) +
                       (start == null ? "" : ",\"start\":\"" + start + "\"") +
                       (end == null ? "" : ",\"end\":\"" + end + "\"") +
                       (limit == null ? "" : ",\"limit\":" + limit.Value) + "}";
         (int status, string answer) = RestRaw("POST", "/api/v1/calendar/expand", body);
         Assert.AreEqual(200, status, answer);
         return answer;
      }

      private static (int status, string body) RestRaw(string method, string path, string body)
      {
         string credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes("Administrator:" + AdminPassword));
         Response response = Raw(RestPort, method, path, "Basic " + credentials, body, "application/json", null);
         return (response.Status, response.Body);
      }

      // The instance starts of an expansion answer, in order.
      private static List<string> Starts(string json)
      {
         return Regex.Matches(json, "\"start\":\"([0-9]{8}T[0-9]{6}Z)\"").Cast<Match>().Select(match => match.Groups[1].Value).ToList();
      }

      private static string JsonString(string value)
      {
         return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
      }

      private static string Auth(string user, string password)
      {
         return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
      }

      // ------------------------------------------------------- multistatus XML

      private static XmlDocument Parse(Response response)
      {
         var document = new XmlDocument { XmlResolver = null };
         try
         {
            document.LoadXml(response.Body);
         }
         catch (XmlException ex)
         {
            Assert.Fail("Not well-formed XML (" + ex.Message + "): " + response.Body);
         }
         return document;
      }

      private static XmlNamespaceManager Namespaces(XmlDocument document)
      {
         var namespaces = new XmlNamespaceManager(document.NameTable);
         namespaces.AddNamespace("D", NsDav);
         namespaces.AddNamespace("C", NsCalDav);
         namespaces.AddNamespace("CS", NsCalendarServer);
         return namespaces;
      }

      private static List<string> Hrefs(Response response)
      {
         XmlDocument document = Parse(response);
         return document.SelectNodes("/D:multistatus/D:response/D:href", Namespaces(document))
            .Cast<XmlNode>().Select(node => node.InnerText).ToList();
      }

      private static string SyncToken(Response response)
      {
         XmlDocument document = Parse(response);
         XmlNode token = document.SelectSingleNode("/D:multistatus/D:sync-token", Namespaces(document));
         return token == null ? null : token.InnerText;
      }

      private static XmlNode Prop(Response response, string href, string propertyPath)
      {
         XmlDocument document = Parse(response);
         return document.SelectSingleNode(
            "/D:multistatus/D:response[D:href='" + href + "']/D:propstat[contains(D:status,'200')]/D:prop/" + propertyPath,
            Namespaces(document));
      }

      private static string PropText(Response response, string href, string propertyPath)
      {
         XmlNode node = Prop(response, href, propertyPath);
         Assert.IsNotNull(node, "No " + propertyPath + " for " + href + " in: " + response.Body);
         return node.InnerText;
      }

      private static string ResponseStatus(Response response, string href)
      {
         XmlDocument document = Parse(response);
         XmlNode status = document.SelectSingleNode("/D:multistatus/D:response[D:href='" + href + "']/D:status", Namespaces(document));
         Assert.IsNotNull(status, "No status for " + href + " in: " + response.Body);
         return status.InnerText;
      }

      // ------------------------------------------------------------- raw HTTP

      private sealed class Response
      {
         public int Status;
         public string Body = "";
         public string HeaderText = "";
         public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

         public string Header(string name)
         {
            string value;
            return Headers.TryGetValue(name, out value) ? value : null;
         }
      }

      private static Response Raw(int port, string method, string path, string authorization, string body, string contentType, string extraHeaders)
      {
         using (var client = new TcpClient())
         {
            Exception last = null;
            for (int attempt = 0; attempt < 25; attempt++)
            {
               try
               {
                  client.Connect("127.0.0.1", port);
                  last = null;
                  break;
               }
               catch (SocketException ex)
               {
                  last = ex;
                  Thread.Sleep(200);
               }
            }

            if (last != null)
               throw last;

            using (NetworkStream stream = client.GetStream())
            using (var memory = new MemoryStream())
            {
               var head = new StringBuilder();
               head.Append(method + " " + path + " HTTP/1.0\r\n");
               head.Append("Host: 127.0.0.1:" + port + "\r\n");
               if (authorization != null)
                  head.Append("Authorization: " + authorization + "\r\n");
               if (extraHeaders != null)
                  head.Append(extraHeaders);

               byte[] bodyBytes = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body);
               if (body != null)
               {
                  head.Append("Content-Type: " + contentType + "\r\n");
                  head.Append("Content-Length: " + bodyBytes.Length + "\r\n");
               }

               head.Append("Connection: close\r\n\r\n");

               byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
               stream.Write(headBytes, 0, headBytes.Length);
               if (bodyBytes.Length > 0)
                  stream.Write(bodyBytes, 0, bodyBytes.Length);

               byte[] buffer = new byte[8192];
               int read;
               while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                  memory.Write(buffer, 0, read);

               string raw = Encoding.UTF8.GetString(memory.ToArray());
               var response = new Response();

               int separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
               response.HeaderText = separator >= 0 ? raw.Substring(0, separator) : raw;
               response.Body = separator >= 0 ? raw.Substring(separator + 4) : "";

               string[] lines = response.HeaderText.Split(new[] { "\r\n" }, StringSplitOptions.None);
               if (lines.Length > 0)
               {
                  string[] parts = lines[0].Split(' ');
                  if (parts.Length >= 2)
                     int.TryParse(parts[1], out response.Status);
               }

               for (int i = 1; i < lines.Length; i++)
               {
                  int colon = lines[i].IndexOf(':');
                  if (colon > 0)
                     response.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
               }

               return response;
            }
         }
      }
   }
}
