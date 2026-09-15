// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// The syslog sink: every log entry the Logger produces, also sent to a syslog
// collector as RFC 5424, or - on Linux under systemd, with no collector
// configured - to the journal.
//
// It sits BESIDE the file sink, never instead of it. Nothing here can stop a
// line reaching hmailserver_<date>.log, and nothing here is consulted before one
// is written: the hook is the same chokepoint the OTLP log exporter uses
// (Logger::Write_), after the entry has been rendered and before the device
// routing that decides between the file and the database.
//
// Settings live in hm_settings, not in hMailServer.ini, per the rule in
// .github/CONTRIBUTING.md from 15 September 2026. They are read at Start, which
// means at service start and at Reinitialize; a change made in the Control Panel
// is written to the database immediately and applied at the next of those, which
// is what the editor says on the page.
//
// THREE THINGS THIS MUST NEVER DO, because it runs on the path every message
// takes:
//
//   Take a lock the Logger holds. OnLogEntry is called from Logger::Write_,
//   which is outside Logger::mtx_, and it takes only its own queue mutex. It
//   must never call back into the Logger - which is why the failure lines are
//   written by the worker thread, which excludes itself.
//
//   Allocate without bound. The queue is capped at QueueLimit entries and drops
//   the OLDEST when it is full, so a collector that has stopped reading costs a
//   fixed amount of memory and the newest lines - the ones describing what is
//   happening now - are the ones kept.
//
//   Block on a socket. Nothing in OnLogEntry touches a socket. Every connect,
//   handshake, send and reconnect happens on the worker thread, with a send
//   deadline, and a failure there costs dropped log lines and never a delayed
//   message.

#pragma once

#include "SyslogFormatter.h"

#include <thread>
#include <deque>
#include <mutex>
#include <condition_variable>
#include <memory>
#include <chrono>

namespace HM
{
   class SyslogSink : public Singleton<SyslogSink>
   {
   public:

      // The stored values of the SyslogTransport setting. UDP is the default
      // because it is what an unconfigured rsyslog on the same machine listens
      // for and what costs the sender nothing; TLS is what a collector across a
      // network deserves.
      enum Transport
      {
         TransportUdp = 0,
         TransportTcp = 1,
         TransportTls = 2
      };

      // Where the entries are actually going, once Start has decided. Journal is
      // not a value of the setting: it is what "enabled, on Linux, under systemd,
      // with no collector host" resolves to.
      enum Destination
      {
         DestinationNone = 0,
         DestinationCollector = 1,
         DestinationJournal = 2
      };

      SyslogSink();
      ~SyslogSink();

      // Reads the settings and, when the feature is on and has somewhere to send
      // to, starts the worker. Safe to call repeatedly: Reinitialize runs the
      // whole start sequence again.
      void Start();
      void Stop();

      bool IsEnabled() const { return enabled_; }
      Destination GetDestination() const { return destination_; }

      // Queues one entry. A no-op when disabled, when the entry's category is not
      // one of the selected log types, when its severity is below the configured
      // minimum, and on this sink's own worker thread - the line a failed send
      // produces must not be queued for the next send.
      void OnLogEntry(const String &category, long thread, int session,
                      const String &remote_host, const String &time, const String &message);

      // What the sink has done since it started. Read by the regression fixture
      // and by the summary the worker writes when a collector comes back.
      unsigned int GetSentCount() const;
      unsigned int GetDroppedCount() const;

   private:

      // Deliberately the whole of the Logger's entry rather than a rendered line:
      // the rendering costs more than the copy and belongs on the worker, and the
      // severity and message id are decided from the message text.
      struct Entry
      {
         Entry() : thread(0), session(-1), severity(SyslogInformational) {}

         String category;
         long thread;
         int session;
         String remote_host;
         String time;
         String message;
         int severity;
      };

      // Everything the transports need that would drag boost::asio and OpenSSL
      // into this header. Defined in the .cpp.
      struct Connection;

      void Run_();
      void ApplySettings_();
      bool SelectedCategory_(const String &category) const;

      // Sends one already-rendered message, reconnecting if it has to. False when
      // it could not be delivered; the caller counts that and backs off.
      //
      // allow_connect is false while the server is stopping: what is already
      // connected is flushed, and nothing new is dialled, so a shutdown cannot
      // wait on a collector that is not there.
      bool Deliver_(const AnsiString &message, bool allow_connect);

      bool DeliverUdp_(const AnsiString &message, bool allow_connect);
      bool DeliverStream_(const AnsiString &message, bool allow_connect);
      bool OpenStream_();
      void CloseStream_();

      // The journal's native protocol, on Linux only. False everywhere else.
      bool DeliverJournal_(const Entry &entry);

      // True when this process is a systemd service with a journal socket to
      // write to. False on Windows, always.
      static bool RunningUnderSystemd_();

      AnsiString RenderForCollector_(const Entry &entry) const;

      // Reports, once, that entries are being dropped, and later that the
      // collector came back. Called only from the worker thread, whose own log
      // lines OnLogEntry ignores.
      void ReportDrops_();

      // 4096 entries, matching the OTLP log exporter's queue. At the average
      // length of a protocol log line that is a few megabytes at the very worst,
      // which is the price of a collector that has stopped answering.
      static const size_t QueueLimit = 4096;

      // The worker sends at most this many messages before going back to the
      // queue lock, so a burst cannot hold the mutex for an unbounded time.
      static const size_t BatchLimit = 512;

      bool enabled_;
      Destination destination_;

      AnsiString host_;
      int port_;
      int transport_;
      int facility_;
      int minimum_severity_;
      int categories_;            // a mask of Logger::LogSource bits

      AnsiString hostname_;       // the HOSTNAME field, decided once at Start
      AnsiString app_name_;
      int process_id_;

      std::unique_ptr<Connection> connection_;

      // Back-off between reconnection attempts, in seconds: 1, 2, 4 ... capped at
      // a minute. Only ever touched by the worker thread.
      int backoff_seconds_;
      unsigned int consecutive_failures_;
      std::chrono::steady_clock::time_point next_attempt_;
      std::chrono::steady_clock::time_point next_drop_report_;

      std::thread worker_;
      std::thread::id worker_thread_id_;
      bool running_;

      mutable std::mutex queue_mutex_;
      std::condition_variable queue_cv_;
      std::deque<Entry> queue_;

      unsigned int sent_count_;
      unsigned int dropped_count_;
      unsigned int reported_drop_count_;
   };
}
