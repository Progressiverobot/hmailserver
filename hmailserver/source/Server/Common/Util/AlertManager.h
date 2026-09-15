// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// Telling the administrator when something has gone wrong.
//
// Six things in this server already NOTICE - the disk filling, a work queue
// stalling, a certificate a week from expiry, a run of auto-bans, a minidump
// written, a backup that failed - and each of them, until now, wrote a line in
// a log file and told nobody. A log file nobody is reading is not a
// notification. This is the half that was missing.
//
// THE SHAPE
//
//   a condition   a row in hm_alertrules: its name, whether it is on, its
//                 threshold, what to do (mail, a webhook, or both), how long to
//                 wait before saying it again, and whether it goes out at once
//                 or waits for the digest. The name is a free string, so a new
//                 condition is a new ROW and never a schema change.
//
//   an event      a row in hm_alertevents: this condition became true (or
//                 stopped being true) at this time, with this summary. Written
//                 by whatever noticed; NOTHING is sent from there.
//
//   delivery      AlertTask, once a minute. It is the only thing that sends,
//                 which is what keeps a notification about the delivery queue
//                 from being submitted INTO the delivery queue by the thread
//                 that is stuck on it - the same deferral the TLS-RPT reporter
//                 makes for the same reason.
//
// NOTHING MAY FLAP
//
// An alerting system that mails every minute is switched off by its
// administrator on the first day, and then nothing tells them anything. Four
// things stop that, and they are the reason this class is more than an INSERT:
//
//   1. A STATE condition that is already raised is not raised again. The disk
//      is not "low" once a minute for an hour; it became low once, and it will
//      become not-low once.
//   2. An EDGE condition - a backup that failed, a minidump - has no "still
//      true", so it is bounded by the rule's cool-down instead: one inside the
//      cool-down is recorded but held for the digest rather than sent.
//   3. A ceiling per hour across every condition (AlertMaxPerHour). Past it,
//      everything goes to the digest.
//   4. The digest is the quiet default: one message at a set hour listing what
//      fired, rather than one message per event.
//
// A condition that clears says so, once, and only if its raise was sent.

#pragma once

#include <vector>

#include "../BO/ScheduledTask.h"

namespace HM
{
   class DALRecordset;

   class AlertManager : public Singleton<AlertManager>
   {
   public:

      AlertManager();
      ~AlertManager();

      enum Action
      {
         ActionMail = 1,
         ActionWebhook = 2
      };

      enum EventState
      {
         StateRaised = 1,
         StateCleared = 2
      };

      enum HookState
      {
         HookNotRequired = 0,
         HookPending = 1,
         HookDelivered = 2,
         HookDeadLettered = 3
      };

      // The six the roadmap names. A seventh needs a row in hm_alertrules and
      // nothing else - no column, no schema step, no code here.
      static const wchar_t *ConditionBackupFailed;
      static const wchar_t *ConditionCertificateExpiring;
      static const wchar_t *ConditionDiskLow;
      static const wchar_t *ConditionQueueStalled;
      static const wchar_t *ConditionAutoBanStorm;
      static const wchar_t *ConditionMinidumpWritten;

      struct Rule
      {
         Rule() : id(0), enabled(false), threshold(0), actions(ActionMail), cooldown(60), digest(true) {}

         __int64 id;
         String condition;
         bool enabled;
         int threshold;
         int actions;
         String address;   // empty: the server-wide AlertRecipient
         String webhook;
         String secret;
         int cooldown;     // minutes
         bool digest;      // true: hold for the daily digest
      };

      struct Event
      {
         Event() : id(0), severity(3), state(StateRaised), time(0), notified(false), digested(false),
                   hook_state(HookNotRequired), hook_tries(0), hook_next(0) {}

         __int64 id;
         String condition;
         int severity;     // ErrorManager::eSeverity
         int state;
         __int64 time;
         String summary;
         String detail;
         bool notified;
         bool digested;
         int hook_state;
         int hook_tries;
         __int64 hook_next;
      };

      // Records that a condition has become true. Returns true when an event was
      // written - false when the condition is already raised, when its rule is
      // off, or when alerting is off altogether. Never sends anything and never
      // blocks on anything but one INSERT, so it is safe to call from a protocol
      // thread, from a scheduled task, and from the code that noticed.
      bool Raise(const String &condition, int severity, const String &summary, const String &detail);

      // As Raise, for a condition that is an occurrence rather than a state: a
      // backup that failed, a minidump written. Every one is recorded; the rule's
      // cool-down decides whether it is sent now or waits for the digest.
      bool RaiseOccurrence(const String &condition, int severity, const String &summary, const String &detail);

      // Records that a condition has stopped being true. Does nothing unless the
      // condition is currently raised.
      bool Clear(const String &condition, const String &summary);

      // True while the newest event for the condition is a raise.
      bool IsRaised(const String &condition);

      bool GetRules(std::vector<Rule> &rules);
      bool GetRule(const String &condition, Rule &rule);

      // Writes the rule for rule.condition, creating it when the condition is one
      // nothing has shipped. Returns false with a sentence when it is refused.
      bool SaveRule(const Rule &rule, String &refusal);

      bool GetEvents(int limit, std::vector<Event> &events);

      // Everything AlertTask does, in the order it does it. Separated so the
      // tests can run a pass without waiting for a minute to elapse.
      int SendPendingNotifications();
      int SendDigestIfDue();
      int RunWebhookDeliveries();

      // The conditions AlertTask evaluates for itself, because nothing else in
      // the server asks the question on a schedule: how close every configured
      // certificate is to expiry, whether auto-bans have run above the rule's
      // threshold in the last hour, and whether a minidump has been written
      // since the last one that was recorded.
      int EvaluateScheduledConditions();

      // One auto-ban happened. Counted in memory - the ban itself is a row, but
      // "how many in the last hour" is a question about a rate, and a rate does
      // not belong in the database.
      void NoteAutoBan();

      // HMAC-SHA256 over "<timestamp>.<body>" under the rule's secret, lower-case
      // hex. Public because the documentation states the recipe and a test pins
      // it; a receiver reproduces it from the two headers and the raw body.
      static AnsiString SignWebhook(const AnsiString &secret, __int64 timestamp, const AnsiString &body);

      // The JSON one event is delivered as, to a webhook and (as the attachment)
      // to a mailbox.
      static AnsiString BuildEventJson(const Event &event);

      // Minutes to wait before attempt n+1 of a webhook: 1, 5, 15, 60, 60 ...
      static int WebhookBackoffMinutes(int attemptsSoFar);

   private:

      bool WriteEvent_(const Rule &rule, int severity, int state, const String &summary, const String &detail);
      bool ReadNewestEvent_(const String &condition, Event &event);
      bool MarkNotified_(__int64 eventId);
      bool UpdateHook_(__int64 eventId, int state, int tries, __int64 next);
      int CountNotifiedSince_(__int64 since);
      bool SendMail_(const String &subject, const AnsiString &body, const String &recipient);
      bool DeliverWebhook_(const Rule &rule, const Event &event, String &error);
      static Event ReadEvent_(const std::shared_ptr<DALRecordset> &recordset);
      static Rule ReadRule_(const std::shared_ptr<DALRecordset> &recordset);
      int EvaluateCertificates_();
      int EvaluateAutoBans_();
      int EvaluateMinidumps_();

      boost::recursive_mutex mutex_;

      // Timestamps of the auto-bans of the last hour, oldest first.
      std::vector<__int64> auto_bans_;
   };

   class AlertTask : public ScheduledTask
   {
   public:
      AlertTask();
      ~AlertTask();

      virtual void DoWork();
   };
}
