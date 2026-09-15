// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "Reports.h"

#include "../Application/Application.h"
#include "../Application/IniFileSettings.h"
#include "../SQL/DALRecordset.h"
#include "../SQL/SQLCommand.h"
#include "../SQL/SQLStatement.h"
#include "../SQL/DatabaseConnectionManager.h"

#include <algorithm>
#include <atomic>
#include <map>
#include <time.h>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // The store-size cache. Two aligned 64-bit values and a stamp, read and
      // written by the metric sampler and by a Prometheus scrape. No lock:
      // the worst a race can do is run the aggregate twice in the same
      // second, which costs a query and cannot produce a wrong answer, and an
      // atomic keeps the three values from being read torn.
      std::atomic<long long> cached_store_bytes(-1);
      std::atomic<long long> cached_store_messages(-1);
      std::atomic<long long> cached_store_taken(0);

      const long long StoreCacheSeconds = 3600;

      // "YYYY-MM-DD" from three numbers, which is how every day in a report is
      // keyed and sorted: the text sorts in the same order as the date.
      String DayKey(int year, int month, int day)
      {
         String text;
         text.Format(_T("%04d-%02d-%02d"), year, month, day);
         return text;
      }
   }

   String
   Reports::DatePart_(const String &part, const String &column)
   {
      switch (IniFileSettings::Instance()->GetDatabaseType())
      {
      case DatabaseSettings::TypeMSSQLServer:
      case DatabaseSettings::TypeMSSQLCompactEdition:
         return "datepart(" + part + ", " + column + ")";
      case DatabaseSettings::TypePGServer:
         // EXTRACT answers numeric here, and the DAL reads a numeric column
         // as text unless it is told otherwise; the cast makes it an integer
         // in every backend alike.
         return "cast(extract(" + part + " from " + column + ") as integer)";
      case DatabaseSettings::TypeMYSQLServer:
         return "extract(" + part + " from " + column + ")";
      default:
         break;
      }

      return "";
   }

   __int64
   Reports::ReadSum_(std::shared_ptr<DALRecordset> recordset, const AnsiString &column)
   {
      if (!recordset || recordset->IsEOF())
         return 0;

      // A sum over no rows is null, which every backend answers differently
      // once it has been through the DAL. Asked first, so that nothing below
      // has to guess what 0 meant.
      if (recordset->GetIsNull(column))
         return 0;

      switch (IniFileSettings::Instance()->GetDatabaseType())
      {
      case DatabaseSettings::TypeMSSQLServer:
      case DatabaseSettings::TypeMSSQLCompactEdition:
         return recordset->GetInt64Value(column);
      default:
         return (__int64) recordset->GetDoubleValue(column);
      }
   }

   String
   Reports::DomainOf(const String &address)
   {
      int at = address.ReverseFind('@');

      if (at < 0)
         return _T("");

      String domain = address.Mid(at + 1);
      domain.MakeLower();
      domain.Trim();

      return domain;
   }

   bool
   Reports::ReadTrace(const Range &range, std::vector<TraceRow> &rows, bool &truncated)
   {
      rows.clear();
      truncated = false;

      String year = DatePart_("year", "mtoccurred");
      String month = DatePart_("month", "mtoccurred");
      String day = DatePart_("day", "mtoccurred");

      if (year.IsEmpty())
         return false;

      // Grouped in the database. What comes back is one row per distinct
      // (day, event, status, sender, recipient), which is what every section
      // built from the trace needs and is very much smaller than the
      // deliveries it summarises. No ORDER BY: the ordering a report wants
      // differs per section and sorting a bounded vector in memory is free,
      // whereas an ORDER BY over an aggregate is the kind of expression the
      // four backends spell differently.
      SQLCommand command(
         "select " + year + " as ryear, " + month + " as rmonth, " + day + " as rday, "
         "mtevent, mtstatuscode, mtsender, mtrecipient, count(*) as rcount "
         "from hm_messagetrace "
         "where mtoccurred >= @FROM and mtoccurred < @TO "
         "group by " + year + ", " + month + ", " + day + ", mtevent, mtstatuscode, mtsender, mtrecipient");

      command.AddParameter("@FROM", range.from);
      command.AddParameter("@TO", range.to);

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);

      if (!recordset)
         return false;

      while (!recordset->IsEOF())
      {
         if ((int) rows.size() >= MaxTraceRows)
         {
            truncated = true;
            break;
         }

         TraceRow row;
         row.year = (int) recordset->GetLongValue("ryear");
         row.month = (int) recordset->GetLongValue("rmonth");
         row.day = (int) recordset->GetLongValue("rday");
         row.event = recordset->GetStringValue("mtevent");
         row.status = (int) recordset->GetLongValue("mtstatuscode");
         row.sender = recordset->GetStringValue("mtsender");
         row.recipient = recordset->GetStringValue("mtrecipient");
         row.count = (__int64) recordset->GetLongValue("rcount");

         rows.push_back(row);

         recordset->MoveNext();
      }

      return true;
   }

   bool
   Reports::ReadMailboxes(std::vector<MailboxRow> &rows)
   {
      rows.clear();

      // One grouped pass over hm_messages by its own index on
      // messageaccountid, joined to the accounts for the address. The domain
      // is taken from the address rather than by a second join to hm_domains:
      // an account address is user@domain by construction, and one join fewer
      // is one fewer thing for a backend to disagree about.
      SQLCommand command(
         "select hm_accounts.accountaddress as raddress, "
         "sum(hm_messages.messagesize) as rbytes, count(*) as rcount "
         "from hm_messages inner join hm_accounts "
         "on hm_messages.messageaccountid = hm_accounts.accountid "
         "group by hm_accounts.accountaddress");

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);

      if (!recordset)
         return false;

      while (!recordset->IsEOF())
      {
         MailboxRow row;
         row.address = recordset->GetStringValue("raddress");
         row.domain = DomainOf(row.address);
         row.bytes = ReadSum_(recordset, "rbytes");
         row.messages = (__int64) recordset->GetLongValue("rcount");

         rows.push_back(row);

         recordset->MoveNext();
      }

      return true;
   }

   namespace
   {
      // One hour of one metric, as the grouped query hands it over.
      struct HourSample
      {
         HourSample() : year(0), month(0), day(0), hour(0), lowest(0), highest(0) { }

         int year;
         int month;
         int day;
         int hour;
         double lowest;
         double highest;

         bool operator<(const HourSample &other) const
         {
            if (year != other.year) return year < other.year;
            if (month != other.month) return month < other.month;
            if (day != other.day) return day < other.day;
            return hour < other.hour;
         }
      };
   }

   bool
   Reports::ReadGaugeByDay(const String &metric, const Range &range, std::vector<DayValue> &days)
   {
      days.clear();

      std::vector<HourSample> hours;

      String year = DatePart_("year", "metricsampletime");
      String month = DatePart_("month", "metricsampletime");
      String day = DatePart_("day", "metricsampletime");
      String hour = DatePart_("hour", "metricsampletime");

      if (year.IsEmpty())
         return false;

      SQLCommand command(
         "select " + year + " as ryear, " + month + " as rmonth, " + day + " as rday, " + hour + " as rhour, "
         "min(metricsamplevalue) as rmin, max(metricsamplevalue) as rmax "
         "from hm_metricsamples "
         "where metricsamplename = @NAME and metricsampletime >= @FROM and metricsampletime < @TO "
         "group by " + year + ", " + month + ", " + day + ", " + hour);

      String lowered = metric;
      lowered.MakeLower();

      command.AddParameter("@NAME", lowered);
      command.AddParameter("@FROM", range.from);
      command.AddParameter("@TO", range.to);

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);

      if (!recordset)
         return false;

      while (!recordset->IsEOF())
      {
         HourSample sample;
         sample.year = (int) recordset->GetLongValue("ryear");
         sample.month = (int) recordset->GetLongValue("rmonth");
         sample.day = (int) recordset->GetLongValue("rday");
         sample.hour = (int) recordset->GetLongValue("rhour");
         sample.lowest = recordset->GetDoubleValue("rmin");
         sample.highest = recordset->GetDoubleValue("rmax");

         hours.push_back(sample);

         recordset->MoveNext();
      }

      std::sort(hours.begin(), hours.end());

      // A gauge's figure for a day is its last sample of that day, which is
      // the highest hour's maximum. Written as "the last hour wins" rather
      // than "the biggest value wins" because a store that shrank on the 3rd
      // must show as smaller on the 3rd.
      for (const HourSample &sample : hours)
      {
         String key = DayKey(sample.year, sample.month, sample.day);

         if (!days.empty() && days.back().day == key)
         {
            days.back().value = sample.highest;
            continue;
         }

         DayValue value;
         value.day = key;
         value.value = sample.highest;
         days.push_back(value);
      }

      return true;
   }

   bool
   Reports::ReadCounterIncreaseByDay(const String &metric, const Range &range, std::vector<DayValue> &days)
   {
      days.clear();

      std::vector<HourSample> hours;

      String year = DatePart_("year", "metricsampletime");
      String month = DatePart_("month", "metricsampletime");
      String day = DatePart_("day", "metricsampletime");
      String hour = DatePart_("hour", "metricsampletime");

      if (year.IsEmpty())
         return false;

      SQLCommand command(
         "select " + year + " as ryear, " + month + " as rmonth, " + day + " as rday, " + hour + " as rhour, "
         "min(metricsamplevalue) as rmin, max(metricsamplevalue) as rmax "
         "from hm_metricsamples "
         "where metricsamplename = @NAME and metricsampletime >= @FROM and metricsampletime < @TO "
         "group by " + year + ", " + month + ", " + day + ", " + hour);

      String lowered = metric;
      lowered.MakeLower();

      command.AddParameter("@NAME", lowered);
      command.AddParameter("@FROM", range.from);
      command.AddParameter("@TO", range.to);

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);

      if (!recordset)
         return false;

      while (!recordset->IsEOF())
      {
         HourSample sample;
         sample.year = (int) recordset->GetLongValue("ryear");
         sample.month = (int) recordset->GetLongValue("rmonth");
         sample.day = (int) recordset->GetLongValue("rday");
         sample.hour = (int) recordset->GetLongValue("rhour");
         sample.lowest = recordset->GetDoubleValue("rmin");
         sample.highest = recordset->GetDoubleValue("rmax");

         hours.push_back(sample);

         recordset->MoveNext();
      }

      std::sort(hours.begin(), hours.end());

      // The arithmetic, and the one thing it has to get right. A counter only
      // ever climbs until the server restarts, when it goes back to zero. So
      // the increase inside an hour is its maximum less its minimum, and the
      // increase BETWEEN two hours is the next minimum less the previous
      // maximum - unless that is negative, which is a restart, and then the
      // whole of the next minimum was earned since the restart. Read any
      // other way a restart shows up as a large negative number, which is how
      // a report ends up claiming that minus nine thousand messages were
      // delivered on Tuesday.
      std::map<String, double> byDay;
      std::vector<String> order;

      bool havePrevious = false;
      double previousHighest = 0;

      for (const HourSample &sample : hours)
      {
         String key = DayKey(sample.year, sample.month, sample.day);

         if (byDay.find(key) == byDay.end())
         {
            byDay[key] = 0;
            order.push_back(key);
         }

         if (havePrevious)
         {
            double step = sample.lowest >= previousHighest ? sample.lowest - previousHighest : sample.lowest;
            byDay[key] += step;
         }

         byDay[key] += sample.highest - sample.lowest;

         previousHighest = sample.highest;
         havePrevious = true;
      }

      for (const String &key : order)
      {
         DayValue value;
         value.day = key;
         value.value = byDay[key];
         days.push_back(value);
      }

      return true;
   }

   String
   Reports::OldestSample()
   {
      SQLCommand command("select min(metricsampletime) as roldest from hm_metricsamples");

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);

      if (!recordset || recordset->IsEOF() || recordset->GetIsNull("roldest"))
         return _T("");

      return recordset->GetStringValue("roldest");
   }

   String
   Reports::OldestTraceRow()
   {
      SQLCommand command("select min(mtoccurred) as roldest from hm_messagetrace");

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);

      if (!recordset || recordset->IsEOF() || recordset->GetIsNull("roldest"))
         return _T("");

      return recordset->GetStringValue("roldest");
   }

   bool
   Reports::ReadStoreTotalsNow(__int64 &bytes, __int64 &messages)
   {
      bytes = 0;
      messages = 0;

      SQLCommand command("select sum(messagesize) as rbytes, count(*) as rcount from hm_messages");

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);

      if (!recordset || recordset->IsEOF())
         return false;

      bytes = ReadSum_(recordset, "rbytes");
      messages = (__int64) recordset->GetLongValue("rcount");

      cached_store_bytes = bytes;
      cached_store_messages = messages;
      cached_store_taken = (long long) time(0);

      return true;
   }

   void
   Reports::CachedStoreTotals(__int64 &bytes, __int64 &messages)
   {
      long long now = (long long) time(0);
      long long taken = cached_store_taken.load();

      if (cached_store_bytes.load() >= 0 && taken > 0 && now - taken < StoreCacheSeconds && now >= taken)
      {
         bytes = (__int64) cached_store_bytes.load();
         messages = (__int64) cached_store_messages.load();
         return;
      }

      if (!ReadStoreTotalsNow(bytes, messages))
      {
         // The query failed. The last figure is better than a zero, which
         // would look in a growth chart exactly like a store that had been
         // emptied; a cache that has never been filled answers zero because
         // there is nothing else to answer.
         bytes = (__int64) std::max<long long>(0, cached_store_bytes.load());
         messages = (__int64) std::max<long long>(0, cached_store_messages.load());
      }
   }
}
