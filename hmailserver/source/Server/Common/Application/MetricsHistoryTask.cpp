// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "MetricsHistoryTask.h"

#include "IniFileSettings.h"
#include "../SQL/SQLCommand.h"
#include "../SQL/SQLStatement.h"
#include "../SQL/DALRecordset.h"
#include "../SQL/DatabaseConnectionManager.h"
#include "../Util/ServerStatus.h"
#include "../TCPIP/SocketConstants.h"

#include <time.h>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // The names are the exporter's, without the hmailserver_ prefix and with a
      // label folded into the name where the exporter uses one. They are the
      // contract with the readers, so a rename here is a change to the API.
      const char *METRIC_NAMES[] =
      {
         "sessions_smtp",
         "sessions_imap",
         "sessions_pop3",
         "processed_messages_total",
         "messages_delivered_total",
         "messages_deferred_total",
         "messages_bounced_total",
         "spam_messages_total",
         "viruses_removed_total",
         "auth_success_total",
         "auth_failures_total",
         "tls_handshakes_total",
         "tls_handshake_failures_total",
         "messagestore_missing_files"
      };

      // Prune on the first run and then once an hour: the delete is indexed and
      // cheap, and once an hour keeps the table within a minute-resolution day of
      // its retention, which is as exact as anyone needs.
      int runs_since_prune = 60;
   }

   MetricsHistoryTask::MetricsHistoryTask()
   {
   }

   MetricsHistoryTask::~MetricsHistoryTask()
   {
   }

   void
   MetricsHistoryTask::DoWork()
   {
      if (IniFileSettings::Instance()->GetMetricsHistoryDays() <= 0)
         return;

      SampleNow();

      if (++runs_since_prune >= 60)
      {
         runs_since_prune = 0;
         Prune();
      }
   }

   const std::vector<AnsiString> &
   MetricsHistoryTask::MetricNames()
   {
      static std::vector<AnsiString> names(METRIC_NAMES, METRIC_NAMES + sizeof(METRIC_NAMES) / sizeof(METRIC_NAMES[0]));
      return names;
   }

   bool
   MetricsHistoryTask::IsMetricName(const String &name)
   {
      AnsiString candidate = name;

      for (const AnsiString &known : MetricNames())
      {
         if (known.CompareNoCase(candidate) == 0)
            return true;
      }

      return false;
   }

   void
   MetricsHistoryTask::Collect_(std::vector<std::pair<AnsiString, double> > &values)
   {
      ServerStatus *status = ServerStatus::Instance();

      values.push_back(std::make_pair(AnsiString("sessions_smtp"), (double) status->GetNumberOfSessions(STSMTP)));
      values.push_back(std::make_pair(AnsiString("sessions_imap"), (double) status->GetNumberOfSessions(STIMAP)));
      values.push_back(std::make_pair(AnsiString("sessions_pop3"), (double) status->GetNumberOfSessions(STPOP3)));
      values.push_back(std::make_pair(AnsiString("processed_messages_total"), (double) status->GetNumberOfProcessedMessages()));
      values.push_back(std::make_pair(AnsiString("messages_delivered_total"), (double) status->GetNumberOfMessagesDelivered()));
      values.push_back(std::make_pair(AnsiString("messages_deferred_total"), (double) status->GetNumberOfMessagesDeferred()));
      values.push_back(std::make_pair(AnsiString("messages_bounced_total"), (double) status->GetNumberOfMessagesBounced()));
      values.push_back(std::make_pair(AnsiString("spam_messages_total"), (double) status->GetNumberOfDetectedSpamMessages()));
      values.push_back(std::make_pair(AnsiString("viruses_removed_total"), (double) status->GetNumberOfRemovedViruses()));
      values.push_back(std::make_pair(AnsiString("auth_success_total"), (double) status->GetNumberOfAuthenticationsSucceeded()));
      values.push_back(std::make_pair(AnsiString("auth_failures_total"), (double) status->GetNumberOfAuthenticationFailures()));
      values.push_back(std::make_pair(AnsiString("tls_handshakes_total"), (double) status->GetNumberOfTlsHandshakesCompleted()));
      values.push_back(std::make_pair(AnsiString("tls_handshake_failures_total"), (double) status->GetNumberOfTlsHandshakeFailures()));
      values.push_back(std::make_pair(AnsiString("messagestore_missing_files"), (double) status->GetMessageStoreMissingFiles()));
   }

   int
   MetricsHistoryTask::SampleNow()
   {
      if (IniFileSettings::Instance()->GetMetricsHistoryDays() <= 0)
         return -1;

      std::vector<std::pair<AnsiString, double> > values;
      Collect_(values);

      int written = 0;

      for (const std::pair<AnsiString, double> &value : values)
      {
         // The value goes in as a numeric literal: it is a double of our own
         // making, and the statement builder has no double column. The time is
         // the database's own clock, the same clock Prune and Query measure
         // against, so a server and a database on different clocks cannot
         // disagree about what is a day old.
         AnsiString literal;
         literal.Format("%.6f", value.second);

         SQLStatement statement;
         statement.SetStatementType(SQLStatement::STInsert);
         statement.SetTable("hm_metricsamples");
         statement.AddColumnCommand("metricsampletime", SQLStatement::GetCurrentTimestamp());
         statement.AddColumn("metricsamplename", String(value.first));
         statement.AddColumnCommand("metricsamplevalue", String(literal));

         if (Application::Instance()->GetDBManager()->Execute(statement))
            written++;
      }

      return written;
   }

   bool
   MetricsHistoryTask::Prune()
   {
      int days = IniFileSettings::Instance()->GetMetricsHistoryDays();

      if (days <= 0)
         return false;

      SQLStatement statement;
      statement.SetStatementType(SQLStatement::STDelete);
      statement.SetTable("hm_metricsamples");
      statement.SetWhereClause("metricsampletime < " + SQLStatement::GetCurrentTimestampPlusMinutes(-days * 24 * 60));

      return Application::Instance()->GetDBManager()->Execute(statement);
   }

   bool
   MetricsHistoryTask::ParseTimestamp_(const String &text, __int64 &seconds)
   {
      // "YYYY-MM-DD HH:MM:SS", as the database hands it back. Treated as a plain
      // calendar value with no zone: buckets are relative and the output is
      // formatted back the same way, so no conversion happens in either direction.
      AnsiString value = text;
      int year = 0, month = 0, day = 0, hour = 0, minute = 0, second = 0;

      if (sscanf_s(value.c_str(), "%d-%d-%d %d:%d:%d", &year, &month, &day, &hour, &minute, &second) < 3)
         return false;

      struct tm parts;
      memset(&parts, 0, sizeof(parts));
      parts.tm_year = year - 1900;
      parts.tm_mon = month - 1;
      parts.tm_mday = day;
      parts.tm_hour = hour;
      parts.tm_min = minute;
      parts.tm_sec = second;

      __int64 result = _mkgmtime64(&parts);

      if (result < 0)
         return false;

      seconds = result;
      return true;
   }

   String
   MetricsHistoryTask::FormatTimestamp_(__int64 seconds)
   {
      struct tm parts;
      __time64_t value = seconds;

      if (_gmtime64_s(&parts, &value) != 0)
         return _T("");

      String text;
      text.Format(_T("%04d-%02d-%02d %02d:%02d:%02d"),
         parts.tm_year + 1900, parts.tm_mon + 1, parts.tm_mday, parts.tm_hour, parts.tm_min, parts.tm_sec);

      return text;
   }

   bool
   MetricsHistoryTask::Query(const String &metric, int minutesBack, int bucketMinutes, std::vector<Sample> &samples)
   {
      samples.clear();

      if (!IsMetricName(metric))
         return false;

      if (minutesBack <= 0)
         minutesBack = 24 * 60;

      String lowered = metric;
      lowered.MakeLower();

      SQLCommand command("select metricsampletime, metricsamplevalue from hm_metricsamples where metricsamplename = @NAME and metricsampletime >= " +
         SQLStatement::GetCurrentTimestampPlusMinutes(-minutesBack) + " order by metricsampletime asc");
      command.AddParameter("@NAME", lowered);

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);

      if (!recordset)
         return true;

      // Averaged per bucket, keyed by the bucket's start. Rows arrive in time
      // order, so a bucket is complete when the next row falls outside it.
      const __int64 bucketSeconds = bucketMinutes > 0 ? (__int64) bucketMinutes * 60 : 0;

      __int64 currentBucket = -1;
      double sum = 0;
      int count = 0;

      while (!recordset->IsEOF())
      {
         String time = recordset->GetStringValue("metricsampletime");
         double value = recordset->GetDoubleValue("metricsamplevalue");

         __int64 seconds = 0;

         if (!ParseTimestamp_(time, seconds))
         {
            recordset->MoveNext();
            continue;
         }

         if (bucketSeconds == 0)
         {
            Sample sample;
            sample.time = time;
            sample.value = value;
            samples.push_back(sample);
            recordset->MoveNext();
            continue;
         }

         __int64 bucket = (seconds / bucketSeconds) * bucketSeconds;

         if (bucket != currentBucket)
         {
            if (count > 0)
            {
               Sample sample;
               sample.time = FormatTimestamp_(currentBucket);
               sample.value = sum / count;
               samples.push_back(sample);
            }

            currentBucket = bucket;
            sum = 0;
            count = 0;
         }

         sum += value;
         count++;

         recordset->MoveNext();
      }

      if (bucketSeconds != 0 && count > 0)
      {
         Sample sample;
         sample.time = FormatTimestamp_(currentBucket);
         sample.value = sum / count;
         samples.push_back(sample);
      }

      return true;
   }

   AnsiString
   MetricsHistoryTask::QueryAsJson(const String &metric, int minutesBack, int bucketMinutes)
   {
      std::vector<Sample> samples;
      bool known = Query(metric, minutesBack, bucketMinutes, samples);

      AnsiString name = metric;
      name.MakeLower();

      AnsiString items;

      for (const Sample &sample : samples)
      {
         AnsiString item;
         item.Format("%s{\"time\":\"%s\",\"value\":%.10g}", items.IsEmpty() ? "" : ",", AnsiString(sample.time).c_str(), sample.value);
         items += item;
      }

      AnsiString json;
      json.Format("{\"metric\":\"%s\",\"known\":%s,\"enabled\":%s,\"retention_days\":%d,\"minutes_back\":%d,\"bucket_minutes\":%d,\"samples\":[%s]}",
         name.c_str(),
         known ? "true" : "false",
         IniFileSettings::Instance()->GetMetricsHistoryDays() > 0 ? "true" : "false",
         IniFileSettings::Instance()->GetMetricsHistoryDays(),
         minutesBack <= 0 ? 24 * 60 : minutesBack,
         bucketMinutes < 0 ? 0 : bucketMinutes,
         items.c_str());

      return json;
   }
}
