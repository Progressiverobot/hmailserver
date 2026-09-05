// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "../BO/ScheduledTask.h"

namespace HM
{
   // The metric history. /metrics is a stateless scrape and the Control Panel
   // remembers three minutes; this is the part that remembers longer. Once a
   // minute it writes one row per metric to hm_metricsamples - the same counters
   // and gauges the exporter serves, under stable names without labels - keeps
   // them MetricsHistoryDays, and reads them back averaged per bucket for the
   // 24 h / 7 d / 30 d views over COM (Utilities.GetMetricHistory) and REST
   // (/api/v1/metrics/history).
   //
   // Counters are stored as the totals they are; a rate is a difference between
   // two samples, and that is the reader's arithmetic, not the sampler's, so that
   // a restart (which resets every counter to zero) shows as a drop rather than
   // as a negative rate.
   class MetricsHistoryTask : public ScheduledTask
   {
   public:
      MetricsHistoryTask();
      ~MetricsHistoryTask();

      virtual void DoWork();

      struct Sample
      {
         String time;
         double value;
      };

      // Records the current value of every metric. Returns the rows written, or
      // -1 when the history is off (MetricsHistoryDays is 0).
      static int SampleNow();

      // Removes rows older than MetricsHistoryDays. Returns whether the delete ran.
      static bool Prune();

      // The samples of one metric over the last minutesBack minutes, averaged per
      // bucketMinutes (0 or less returns the raw samples). Times are the bucket
      // starts as the database stores them. False when the name is not a metric
      // this server records.
      static bool Query(const String &metric, int minutesBack, int bucketMinutes, std::vector<Sample> &samples);

      // Query, as the JSON document both the COM method and the REST route return.
      static AnsiString QueryAsJson(const String &metric, int minutesBack, int bucketMinutes);

      static const std::vector<AnsiString> &MetricNames();
      static bool IsMetricName(const String &name);

   private:
      static void Collect_(std::vector<std::pair<AnsiString, double> > &values);
      static bool ParseTimestamp_(const String &text, __int64 &seconds);
      static String FormatTimestamp_(__int64 seconds);
   };
}
