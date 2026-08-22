// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later
// A queryable per-message delivery trace.
//
// The question this exists for is "what happened to the message Jane sent at
// 14:20", and until now the answer was to grep several log files and hope the
// relevant one had not rotated. The events themselves were never missing: the
// AWStats journal has been called from every interesting site for years -
// accepted, refused, delivered, failed - it simply wrote them to a text file in
// a format designed for a web-statistics tool, with no way to ask it anything.
//
// So this is not a new instrumentation pass. It is the same events, given a
// correlation key and somewhere to be asked questions.
//
// THE CORRELATION KEY IS THE QUEUE ID, and what that does and does not buy is
// worth being exact about. Every event for one queue id is one message
// instance, which is what answers the question above. It does NOT follow a
// message across a fork: a forward, a distribution list or an account-level
// rule that re-sends produces a NEW queue entry with a new id, and this trace
// will show that as a separate instance rather than a continuation. Following a
// fork needs the RFC Message-ID, which is in the message file rather than in
// the objects these call sites hold, and reading it would put a file parse on
// every delivery. That is a real limitation and it is stated in the roadmap
// rather than implied away.
//
// OFF BY DEFAULT, and that is a privacy decision rather than a performance one:
// this table stores sender and recipient addresses, so it is a record of who
// corresponds with whom, retained. An administrator should switch that on
// deliberately and choose how long it is kept.

#pragma once

namespace HM
{
   class DALRecordset;

   struct MessageTraceEvent
   {
      __int64 id = 0;
      __int64 queue_id = 0;
      String occurred;
      String event_name;
      String sender;
      String recipient;
      String source_ip;
      int status_code = 0;
      String detail;
   };

   class MessageTrace
   {
   public:
      // The event names, kept as constants because they are queried on and a typo
      // in one of sixteen call sites would produce a row nobody can find.
      static const wchar_t *EventAccepted;
      static const wchar_t *EventDelivered;
      static const wchar_t *EventFailed;
      static const wchar_t *EventQuarantined;

      static bool GetEnabled();

      // Records one event. Never throws and never blocks a delivery: a trace that
      // can fail a delivery is worse than no trace, because the failure would be
      // invisible in exactly the tool built to make deliveries visible.
      static void Record(__int64 queueID, const String &eventName, const String &sender,
                         const String &recipient, const String &sourceIP, int statusCode,
                         const String &detail);

      // Events matching an address (sender OR recipient, substring), newest first.
      // An empty address returns the most recent events for any address.
      static std::vector<MessageTraceEvent> Search(const String &address, int maxCount);

      // Every event for one queue id, oldest first - the story of one message.
      static std::vector<MessageTraceEvent> GetByQueueID(__int64 queueID);

      static int GetCount();

      // Removes everything older than MessageTraceRetentionDays. 0 means never,
      // which for a record of who corresponds with whom is a decision rather than
      // a default.
      static int DeleteExpired();

   private:
      static bool ReadRow_(std::shared_ptr<DALRecordset> recordset, MessageTraceEvent &out_event);
   };
}
