// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// See CalendarStore.h. Reads and writes go through the same SQLCommand and
// SQLStatement path as every other table, parameterised. Every write that
// changes a collection steps its token and stamps the row with the new value
// under one process-wide mutex, so two writers cannot each read the same
// token and leave a row the sync report never answers.

#include "StdAfx.h"
#include "CalendarStore.h"
#include "../SQL/SQLCommand.h"
#include "../SQL/SQLStatement.h"
#include "../SQL/DALRecordset.h"
#include "../Application/Application.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   const char *CalendarStore::DefaultName = "calendar";
   const char *CalendarStore::DefaultDisplayName = "Calendar";
   const int CalendarStore::MaximumUriLength;

   namespace
   {
      boost::recursive_mutex write_mutex;

      const char *ObjectColumns =
         "objectid, objectcalendarid, objecturi, objectuid, objectcomponent, objectdata, objectetag, objectsynctoken, "
         "objectstart, objectend, objectfirst, objectlast, objectdeleted, objectmodified";

      AnsiString Int64Text(__int64 value)
      {
         AnsiString text;
         text.Format("%I64d", value);
         return text;
      }

      __int64 Now()
      {
         return static_cast<__int64>(time(nullptr));
      }

      void ReadCalendar(std::shared_ptr<DALRecordset> recordset, CalendarRecord &calendar)
      {
         calendar.id = recordset->GetInt64Value("calendarid");
         calendar.accountId = recordset->GetInt64Value("calendaraccountid");
         calendar.name = recordset->GetStringValue("calendarname");
         calendar.displayName = recordset->GetStringValue("calendardisplayname");
         calendar.syncToken = recordset->GetInt64Value("calendarsynctoken");
         calendar.created = recordset->GetInt64Value("calendarcreated");
      }

      void ReadObject(std::shared_ptr<DALRecordset> recordset, CalendarObjectRecord &object)
      {
         object.id = recordset->GetInt64Value("objectid");
         object.calendarId = recordset->GetInt64Value("objectcalendarid");
         object.uri = recordset->GetStringValue("objecturi");
         object.uid = recordset->GetStringValue("objectuid");
         object.component = recordset->GetStringValue("objectcomponent");
         object.data = recordset->GetStringValue("objectdata");
         object.etag = recordset->GetStringValue("objectetag");
         object.syncToken = recordset->GetInt64Value("objectsynctoken");
         object.start = recordset->GetInt64Value("objectstart");
         object.end = recordset->GetInt64Value("objectend");
         object.first = recordset->GetInt64Value("objectfirst");
         object.last = recordset->GetInt64Value("objectlast");
         object.deleted = recordset->GetLongValue("objectdeleted") != 0;
         object.modified = recordset->GetInt64Value("objectmodified");
      }

      bool ReadObjects(const SQLCommand &command, std::vector<CalendarObjectRecord> &objects)
      {
         objects.clear();

         std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
         if (!recordset)
            return false;

         while (!recordset->IsEOF())
         {
            CalendarObjectRecord object;
            ReadObject(recordset, object);
            objects.push_back(object);
            recordset->MoveNext();
         }

         return true;
      }

      bool ReadOneObject(const SQLCommand &command, CalendarObjectRecord &object)
      {
         std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
         if (!recordset || recordset->IsEOF())
            return false;

         ReadObject(recordset, object);
         return true;
      }

      // The row whose resource name, or UID, is exactly the one asked for. The WHERE
      // clause compares under the column's collation, which ignores case on SQL
      // Server and Compact and case, accents and trailing spaces on MySQL, while a
      // CalDAV resource name (an RFC 3986 path segment) and an iCalendar UID are
      // case-sensitive. So the database narrows the candidates and this decides:
      // "Event.ics" and "event.ics" are two objects, and a PUT to one must never
      // overwrite the other. Every row is walked, because no unique index covers
      // UIDs.
      bool ReadExactObject(const SQLCommand &command, bool byUid, const String &value, CalendarObjectRecord &object)
      {
         std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
         if (!recordset)
            return false;

         while (!recordset->IsEOF())
         {
            CalendarObjectRecord candidate;
            ReadObject(recordset, candidate);

            if ((byUid ? candidate.uid : candidate.uri).Compare(value) == 0)
            {
               object = candidate;
               return true;
            }

            recordset->MoveNext();
         }

         return false;
      }

      // The collection's counter stepped by one, and the new value read
      // back. Called with write_mutex held.
      bool StepToken(__int64 accountId, __int64 calendarId, __int64 &token)
      {
         SQLCommand step("update hm_calendars set calendarsynctoken = calendarsynctoken + 1 where calendarid = @ID and calendaraccountid = @ACCOUNTID");
         step.AddParameter("@ID", calendarId);
         step.AddParameter("@ACCOUNTID", accountId);
         if (!Application::Instance()->GetDBManager()->Execute(step))
            return false;

         CalendarRecord calendar;
         if (!CalendarStore::Get(accountId, calendarId, calendar))
            return false;

         token = calendar.syncToken;
         return true;
      }

      void AddObjectColumns(SQLStatement &statement, const CalendarObjectRecord &object, __int64 token)
      {
         statement.AddColumn("objectuid", object.uid);
         statement.AddColumn("objectcomponent", object.component);
         statement.AddColumn("objectdata", object.data);
         statement.AddColumn("objectetag", object.etag);
         statement.AddColumnInt64("objectsynctoken", token);
         statement.AddColumnInt64("objectstart", object.start);
         statement.AddColumnInt64("objectend", object.end);
         statement.AddColumnInt64("objectfirst", object.first);
         statement.AddColumnInt64("objectlast", object.last);
         statement.AddColumnInt64("objectdeleted", 0);
         statement.AddColumnInt64("objectmodified", Now());
      }
   }

   bool
   CalendarStore::EnsureDefault(__int64 accountId, CalendarRecord &calendar)
   {
      String name = DefaultName;

      SQLCommand select("select calendarid, calendaraccountid, calendarname, calendardisplayname, calendarsynctoken, calendarcreated from hm_calendars where calendaraccountid = @ACCOUNTID and calendarname = @NAME");
      select.AddParameter("@ACCOUNTID", accountId);
      select.AddParameter("@NAME", name);

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(select);
      if (!recordset)
         return false;

      if (!recordset->IsEOF())
      {
         ReadCalendar(recordset, calendar);
         return true;
      }

      // Made on first use, once: a second request for the same account at the
      // same moment waits here and then finds the row.
      boost::lock_guard<boost::recursive_mutex> guard(write_mutex);

      recordset = Application::Instance()->GetDBManager()->OpenRecordset(select);
      if (!recordset)
         return false;
      if (!recordset->IsEOF())
      {
         ReadCalendar(recordset, calendar);
         return true;
      }

      calendar = CalendarRecord();
      calendar.accountId = accountId;
      calendar.name = name;
      calendar.displayName = DefaultDisplayName;
      calendar.syncToken = 1;
      calendar.created = Now();

      SQLStatement statement;
      statement.SetTable("hm_calendars");
      statement.SetStatementType(SQLStatement::STInsert);
      statement.SetIdentityColumn("calendarid");
      statement.AddColumnInt64("calendaraccountid", accountId);
      statement.AddColumn("calendarname", calendar.name);
      statement.AddColumn("calendardisplayname", calendar.displayName);
      statement.AddColumnInt64("calendarsynctoken", calendar.syncToken);
      statement.AddColumnInt64("calendarcreated", calendar.created);

      __int64 id = 0;
      if (!Application::Instance()->GetDBManager()->Execute(statement, &id) || id <= 0)
         return false;

      calendar.id = id;
      return true;
   }

   bool
   CalendarStore::Get(__int64 accountId, __int64 calendarId, CalendarRecord &calendar)
   {
      SQLCommand command("select calendarid, calendaraccountid, calendarname, calendardisplayname, calendarsynctoken, calendarcreated from hm_calendars where calendarid = @ID and calendaraccountid = @ACCOUNTID");
      command.AddParameter("@ID", calendarId);
      command.AddParameter("@ACCOUNTID", accountId);

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!recordset || recordset->IsEOF())
         return false;

      ReadCalendar(recordset, calendar);
      return true;
   }

   bool
   CalendarStore::List(__int64 accountId, __int64 calendarId, std::vector<CalendarObjectRecord> &objects)
   {
      SQLCommand command(String(AnsiString("select ") + ObjectColumns + " from hm_calendarobjects where objectaccountid = @ACCOUNTID and objectcalendarid = @CALENDARID and objectdeleted = 0 order by objecturi asc"));
      command.AddParameter("@ACCOUNTID", accountId);
      command.AddParameter("@CALENDARID", calendarId);
      return ReadObjects(command, objects);
   }

   bool
   CalendarStore::ListInRange(__int64 accountId, __int64 calendarId, __int64 rangeStart, __int64 rangeEnd, std::vector<CalendarObjectRecord> &objects)
   {
      SQLCommand command(String(AnsiString("select ") + ObjectColumns + " from hm_calendarobjects where objectaccountid = @ACCOUNTID and objectcalendarid = @CALENDARID and objectdeleted = 0 and objectlast > @RANGESTART and objectfirst < @RANGEEND order by objecturi asc"));
      command.AddParameter("@ACCOUNTID", accountId);
      command.AddParameter("@CALENDARID", calendarId);
      command.AddParameter("@RANGESTART", rangeStart);
      command.AddParameter("@RANGEEND", rangeEnd);
      return ReadObjects(command, objects);
   }

   bool
   CalendarStore::ListChangedSince(__int64 accountId, __int64 calendarId, __int64 sinceToken, std::vector<CalendarObjectRecord> &objects)
   {
      SQLCommand command(String(AnsiString("select ") + ObjectColumns + " from hm_calendarobjects where objectaccountid = @ACCOUNTID and objectcalendarid = @CALENDARID and objectsynctoken > @SINCE order by objecturi asc"));
      command.AddParameter("@ACCOUNTID", accountId);
      command.AddParameter("@CALENDARID", calendarId);
      command.AddParameter("@SINCE", sinceToken);
      return ReadObjects(command, objects);
   }

   bool
   CalendarStore::FindByUri(__int64 accountId, __int64 calendarId, const String &uri, CalendarObjectRecord &object)
   {
      // objecturi is nvarchar(255): a longer name from a request path cannot exist.
      if (uri.IsEmpty() || uri.GetLength() > 255)
         return false;

      SQLCommand command(String(AnsiString("select ") + ObjectColumns + " from hm_calendarobjects where objectaccountid = @ACCOUNTID and objectcalendarid = @CALENDARID and objecturi = @URI and objectdeleted = 0"));
      command.AddParameter("@ACCOUNTID", accountId);
      command.AddParameter("@CALENDARID", calendarId);
      command.AddParameter("@URI", uri);
      return ReadExactObject(command, false, uri, object);
   }

   bool
   CalendarStore::FindByUid(__int64 accountId, __int64 calendarId, const String &uid, CalendarObjectRecord &object)
   {
      if (uid.IsEmpty() || uid.GetLength() > 255)
         return false;

      SQLCommand command(String(AnsiString("select ") + ObjectColumns + " from hm_calendarobjects where objectaccountid = @ACCOUNTID and objectcalendarid = @CALENDARID and objectuid = @UID and objectdeleted = 0"));
      command.AddParameter("@ACCOUNTID", accountId);
      command.AddParameter("@CALENDARID", calendarId);
      command.AddParameter("@UID", uid);
      return ReadExactObject(command, true, uid, object);
   }

   bool
   CalendarStore::FindCollationTwin(__int64 accountId, __int64 calendarId, const String &uri, CalendarObjectRecord &object)
   {
      if (uri.IsEmpty() || uri.GetLength() > 255)
         return false;

      // Live objects only, as the header says: a deleted twin is revived by Insert
      // under the new spelling rather than blocking the name.
      SQLCommand command(String(AnsiString("select ") + ObjectColumns + " from hm_calendarobjects where objectaccountid = @ACCOUNTID and objectcalendarid = @CALENDARID and objecturi = @URI and objectdeleted = 0"));
      command.AddParameter("@ACCOUNTID", accountId);
      command.AddParameter("@CALENDARID", calendarId);
      command.AddParameter("@URI", uri);

      CalendarObjectRecord candidate;
      if (!ReadOneObject(command, candidate) || candidate.uri.Compare(uri) == 0)
         return false;

      object = candidate;
      return true;
   }

   bool
   CalendarStore::Insert(__int64 accountId, __int64 calendarId, const CalendarObjectRecord &object, CalendarObjectRecord &inserted, __int64 &token)
   {
      boost::lock_guard<boost::recursive_mutex> guard(write_mutex);

      if (!StepToken(accountId, calendarId, token))
         return false;

      // A tombstone under this name: the row comes back rather than a second
      // row, since the unique index on the name is what keeps a sync honest.
      SQLCommand find(String(AnsiString("select ") + ObjectColumns + " from hm_calendarobjects where objectaccountid = @ACCOUNTID and objectcalendarid = @CALENDARID and objecturi = @URI"));
      find.AddParameter("@ACCOUNTID", accountId);
      find.AddParameter("@CALENDARID", calendarId);
      find.AddParameter("@URI", object.uri);
      CalendarObjectRecord existing;
      if (ReadOneObject(find, existing))
      {
         // Found under the collation. A LIVE row that is not exactly this name - a
         // case or accent twin - is a different object the unique index will not let
         // a second row sit beside, so the insert is refused and the caller answers
         // 409. A deleted one is only a tombstone holding the name: it is revived
         // under the spelling asked for, which is what reviving a tombstone of the
         // exact name has always done; a client whose sync token predates the delete
         // then sees the object under its new name rather than the old one gone.
         if (!existing.deleted)
            return false;

         SQLStatement statement;
         statement.SetTable("hm_calendarobjects");
         statement.SetStatementType(SQLStatement::STUpdate);
         AddObjectColumns(statement, object, token);
         if (existing.uri.Compare(object.uri) != 0)
            statement.AddColumn("objecturi", object.uri);
         statement.SetWhereClause("objectid = " + Int64Text(existing.id) + " and objectaccountid = " + Int64Text(accountId));
         if (!Application::Instance()->GetDBManager()->Execute(statement))
            return false;

         inserted = object;
         inserted.id = existing.id;
         inserted.calendarId = calendarId;
         inserted.syncToken = token;
         inserted.deleted = false;
         inserted.modified = Now();
         return true;
      }

      SQLStatement statement;
      statement.SetTable("hm_calendarobjects");
      statement.SetStatementType(SQLStatement::STInsert);
      statement.SetIdentityColumn("objectid");
      statement.AddColumnInt64("objectaccountid", accountId);
      statement.AddColumnInt64("objectcalendarid", calendarId);
      statement.AddColumn("objecturi", object.uri);
      AddObjectColumns(statement, object, token);

      __int64 id = 0;
      if (!Application::Instance()->GetDBManager()->Execute(statement, &id) || id <= 0)
         return false;

      inserted = object;
      inserted.id = id;
      inserted.calendarId = calendarId;
      inserted.syncToken = token;
      inserted.deleted = false;
      inserted.modified = Now();
      return true;
   }

   bool
   CalendarStore::Update(__int64 accountId, __int64 calendarId, __int64 objectId, const CalendarObjectRecord &object, __int64 &token)
   {
      boost::lock_guard<boost::recursive_mutex> guard(write_mutex);

      if (!StepToken(accountId, calendarId, token))
         return false;

      SQLStatement statement;
      statement.SetTable("hm_calendarobjects");
      statement.SetStatementType(SQLStatement::STUpdate);
      AddObjectColumns(statement, object, token);
      statement.SetWhereClause("objectid = " + Int64Text(objectId) + " and objectaccountid = " + Int64Text(accountId) + " and objectcalendarid = " + Int64Text(calendarId));

      return Application::Instance()->GetDBManager()->Execute(statement);
   }

   bool
   CalendarStore::Delete(__int64 accountId, __int64 calendarId, __int64 objectId, __int64 &token)
   {
      boost::lock_guard<boost::recursive_mutex> guard(write_mutex);

      SQLCommand select("select objectid from hm_calendarobjects where objectid = @ID and objectaccountid = @ACCOUNTID and objectcalendarid = @CALENDARID and objectdeleted = 0");
      select.AddParameter("@ID", objectId);
      select.AddParameter("@ACCOUNTID", accountId);
      select.AddParameter("@CALENDARID", calendarId);
      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(select);
      if (!recordset || recordset->IsEOF())
         return false;

      if (!StepToken(accountId, calendarId, token))
         return false;

      // The tombstone keeps its name and UID and nothing else: the text goes,
      // and the span is emptied so no time-range query reads the row.
      SQLStatement statement;
      statement.SetTable("hm_calendarobjects");
      statement.SetStatementType(SQLStatement::STUpdate);
      statement.AddColumn("objectdata", String());
      statement.AddColumn("objectetag", String());
      statement.AddColumnInt64("objectsynctoken", token);
      statement.AddColumnInt64("objectstart", 0);
      statement.AddColumnInt64("objectend", 0);
      statement.AddColumnInt64("objectfirst", 0);
      statement.AddColumnInt64("objectlast", 0);
      statement.AddColumnInt64("objectdeleted", 1);
      statement.AddColumnInt64("objectmodified", Now());
      statement.SetWhereClause("objectid = " + Int64Text(objectId) + " and objectaccountid = " + Int64Text(accountId));

      return Application::Instance()->GetDBManager()->Execute(statement);
   }
}
