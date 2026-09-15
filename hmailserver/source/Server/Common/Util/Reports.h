// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// What this server can honestly say about itself, per domain and per day.
//
// A report is only as true as what was kept, so the first thing this file does
// is name its three sources and what each one cannot answer. Nothing here
// counts anything new at delivery time: every number below is an aggregate of
// something the server was already storing, computed in the database rather
// than by reading rows into memory, because a report that scans the message
// store on every request is a denial of service against the server it reports
// on.
//
// THE THREE SOURCES
//
//   hm_messagetrace (schema 6020). One row per recipient per delivery attempt:
//   when, the event, the sender, the recipient, the source address and the
//   status code. This is the only source with a DOMAIN in it, because it is
//   the only one holding addresses - so messages in and out per domain, the
//   failures by reason, and the top senders and recipients all come from here
//   and from nowhere else.
//
//   Two things about it decide what a report may claim. It is OFF BY DEFAULT
//   (MessageTraceEnabled), because it records who corresponds with whom; a
//   report drawn while it was off is empty, and must say "off" rather than
//   "nothing happened". And only two of its four event names are ever
//   recorded today - "delivered" and "failed", both from AWStats.cpp - so a
//   message refused at RCPT appears as a failure, and one accepted and still
//   in the queue appears not at all. There is no spam or virus event in it.
//
//   hm_metricsamples (schema 6028). One row per metric per minute, SERVER-WIDE
//   and with no domain in it at all. This is where the spam and virus counts
//   come from, and the delivered/deferred/bounced totals, and - from 15
//   September 2026 - the size of the message store. A counter's value for a
//   day is the increase across that day with a restart (a decrease) read as a
//   restart rather than as a negative rate; a gauge's value for a day is its
//   last sample of the day.
//
//   hm_messages joined to hm_accounts. The size of every mailbox as it is now.
//   A point in time, not a history: nothing has ever recorded what a mailbox
//   held yesterday, so "largest mailboxes" is answerable and "this mailbox
//   grew by 40 MB last week" is not.
//
// WHAT IS NOT ANSWERABLE, AND IS SAID SO RATHER THAN GUESSED
//
//   Spam and virus counts per domain. The counters are server-wide and the
//   trace has no spam event, so there is no honest way to attribute either to
//   a domain. The report says the server-wide figure and names it as such.
//
//   Storage growth per domain. The store-size gauge is server-wide. The
//   per-domain figure is the size now. Making the growth per-domain needs a
//   per-domain sample taken daily, which is a schema step and a new table; it
//   is named in the roadmap rather than faked here by extrapolating from the
//   size now.
//
//   Anything before the feature was switched on. The trace keeps
//   MessageTraceRetentionDays and the samples keep MetricsHistoryDays; a range
//   that reaches past either is answered with what exists and a note saying
//   how far back the data goes.
//
// THE COST, stated because the brief for this work asked for it. One report
// makes at most three grouped queries: one over hm_messagetrace in the window,
// grouped by (day, event, status, sender, recipient), so the rows returned are
// distinct tuples rather than deliveries; one over hm_metricsamples per metric
// asked for, grouped by (day, hour), so at most 24 rows a day; and one over
// hm_messages grouped by account, which is one pass of the index and returns
// one row per mailbox. The trace query is capped at MaxTraceRows and the
// answer says so when the cap was reached, because a partial answer that
// admits it is better than an unbounded read.

#pragma once

#include <memory>
#include <vector>

namespace HM
{
   class DALRecordset;

   class Reports
   {
   public:
      // A half-open window, as the database stores a timestamp:
      // "YYYY-MM-DD HH:MM:SS", from inclusive, to exclusive.
      struct Range
      {
         String from;
         String to;
      };

      // One grouped row of the trace: how many events of this kind, between
      // this sender and this recipient, with this status, on this day.
      struct TraceRow
      {
         TraceRow() : year(0), month(0), day(0), status(0), count(0) { }

         int year;
         int month;
         int day;
         String event;
         int status;
         String sender;
         String recipient;
         __int64 count;
      };

      // One mailbox as it is now.
      struct MailboxRow
      {
         MailboxRow() : bytes(0), messages(0) { }

         String address;
         String domain;
         __int64 bytes;
         __int64 messages;
      };

      // One day of one metric. day is "YYYY-MM-DD".
      struct DayValue
      {
         DayValue() : value(0) { }

         String day;
         double value;
      };

      // The ceiling on the grouped trace rows one report will read. Reached
      // only by a window holding more than this many distinct
      // (day, event, status, sender, recipient) tuples, which is a very busy
      // server over a very long range; the answer then says truncated.
      static const int MaxTraceRows = 100000;

      // The grouped trace over a window. False when the query could not be
      // run at all (which is not the same as an empty window).
      static bool ReadTrace(const Range &range, std::vector<TraceRow> &rows, bool &truncated);

      // Every mailbox that holds at least one message, with its size and its
      // count. A mailbox holding nothing does not appear - there is no row in
      // hm_messages to group - which is right for "largest mailboxes" and is
      // why the per-domain roll-up counts the domains that have mail rather
      // than the domains that exist.
      static bool ReadMailboxes(std::vector<MailboxRow> &rows);

      // A gauge per day: its last sample of each day.
      static bool ReadGaugeByDay(const String &metric, const Range &range, std::vector<DayValue> &days);

      // A counter's increase per day, computed from per-hour minima and
      // maxima so that a restart inside a day is read as a restart. A day
      // with no sample does not appear.
      static bool ReadCounterIncreaseByDay(const String &metric, const Range &range, std::vector<DayValue> &days);

      // The oldest sample this server still holds of any metric, as
      // "YYYY-MM-DD HH:MM:SS", or empty when there are none. What a report
      // uses to say how far back it can see.
      static String OldestSample();

      // The oldest trace row, the same way.
      static String OldestTraceRow();

      // The size of the message store, for the metric sampler and the
      // Prometheus exporter. A full aggregate over hm_messages, so it is
      // computed at most once an hour and every caller in between is handed
      // the cached figure; a report never calls this, it reads the samples.
      static void CachedStoreTotals(__int64 &bytes, __int64 &messages);

      // The same aggregate, now, whatever the cache holds. For a test that
      // needs the truth rather than an hour-old truth.
      static bool ReadStoreTotalsNow(__int64 &bytes, __int64 &messages);

      // The domain part of an address, lower-cased, or empty when there is
      // no "@" in it. Kept here because a report folds addresses into domains
      // in a dozen places and a report that folds them two different ways
      // would put the same message in two domains.
      static String DomainOf(const String &address);

   private:
      // "year", "month", "day" or "hour" of a column, in the dialect of the
      // configured backend. MySQL and PostgreSQL take the standard
      // EXTRACT(part FROM column) - PostgreSQL's answers numeric, so it is
      // cast - and SQL Server and its Compact Edition take DATEPART, which is
      // the one spelling both of those understand.
      static String DatePart_(const String &part, const String &column);

      // sum() of a size column read back in the type the backend hands it
      // over in: MySQL and PostgreSQL as a double, SQL Server as a 64-bit
      // integer. The same distinction PersistentAccount::GetMessageBoxSize
      // makes, for the same reason.
      static __int64 ReadSum_(std::shared_ptr<DALRecordset> recordset, const AnsiString &column);
   };
}
