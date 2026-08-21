// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   // The Windows event log sink.
   //
   // hMailServer's own records - the ERROR log, hmailserver_events.log, the OnError
   // script event - are all invisible to the tools a Windows administrator actually
   // watches with: Event Viewer, a monitoring agent, a SIEM collector, a scheduled
   // task triggered on an event id. This class is the bridge, and it is deliberately
   // narrow: it forwards ERRORS, as ErrorManager classifies them, and nothing else.
   // The Windows event log is a signal channel, not a second copy of the SMTP
   // conversation; every event written here also has its full record in the ERROR
   // log, which remains the record of note.
   //
   // What deliberately does NOT come from here:
   //
   //   * Service start and stop. The Service Control Manager already records every
   //     transition of the hMailServer service in the System log (event 7036, and
   //     7034 when the process dies), unconditionally, with no code of ours run at
   //     all - which makes it the one record of a start or stop that survives every
   //     failure mode ours could have. Duplicating it here would add a second id
   //     for the same fact, one that vanishes exactly when the process is too
   //     broken to write it.
   //
   //   * "A log line could not be written to disk" - Logger::ReportWriteFailure_,
   //     event id 0. It predates this sink, fires once per process, and is
   //     deliberately independent of it: that path exists for the moment the disk
   //     has failed, and must not acquire a dependency on settings, throttles or
   //     registry state. Id 0 is reserved for it and nothing here may use it.
   //
   //   * Protocol sessions, delivery attempts, spam scores - anything that happens
   //     many times an hour on a healthy server. A mail server that chatters into
   //     the Application log gets its event source filtered out or itself
   //     uninstalled, which costs the administrator the events that mattered.
   //
   // ============================ WINDOWS EVENT IDS =============================
   //
   // THIS TABLE IS THE AUTHORITATIVE LIST. The ids are a contract: an
   // administrator alerts on "source hMailServer, id 2010", and an id that changes
   // meaning between versions is worse than no id. Ids may be ADDED here; an id
   // may never be renumbered or reused. The block 2000-2099 is reserved for this
   // sink.
   //
   // These are NOT hMailServer error codes. The "HMnnnn" codes that ErrorManager
   // writes to the ERROR log (and passes to OnError) are a separate, much larger
   // namespace; every event written by this sink carries the HM code of the error
   // behind it in its message text, so the two can always be correlated. The
   // catch-all ids exist so that an HM code this table has never heard of still
   // reaches the event log at its severity rather than being dropped.
   //
   //   0     (reserved - Logger::ReportWriteFailure_, predates this sink)
   //   2000  A critical error not covered by a specific id below.        Error
   //   2001  A high-severity error not covered by a specific id below.   Error
   //   2002  A medium-severity error (only at WindowsEventLogLevel>=3).  Warning
   //   2003  A low-severity error (only at WindowsEventLogLevel>=4).     Information
   //   2010  The database is unavailable: a connection could not be      Error
   //         established, was lost and could not be re-established, or
   //         the connection pool was exhausted.
   //   2011  A listener could not be started: a protocol port could not  Error
   //         be opened, bound or listened on.
   //   2012  A crash or unhandled fault: the process is terminating on   Error
   //         an unhandled exception, an exception was contained at the
   //         top of a worker or session, or the crash reporter itself
   //         failed.
   //   2013  A backup or restore failed.                                 Error
   //   2014  Brute-force protection is impaired: an auto-ban could not   Error
   //         be recorded or applied, so repeated logon failures may not
   //         be blocked.
   //   2015  Free disk space has fallen below the configured floor and   Error
   //         new mail is being refused with a temporary failure.
   //
   // (The Error/Warning/Information column is the EVENTLOG_* type the entry is
   // written with; it follows the reported severity - Critical and High are
   // Error, Medium is Warning, Low is Information - so a specific id keeps the
   // type of the severity that produced it.)
   //
   // Which HM codes map to which specific id lives in one table at the top of
   // WindowsEventLog.cpp. A new HM code lands in the right catch-all until it is
   // added there, so an uncurated error degrades to a less specific id, never to
   // silence.
   //
   // ============================== CONFIGURATION ===============================
   //
   // [Settings] in hMailServer.ini:
   //
   //   WindowsEventLogEnabled  (default 1)  - the sink as a whole.
   //   WindowsEventLogLevel    (default 2)  - forward errors with ErrorManager
   //     severity <= this value: 1 Critical only, 2 Critical+High, 3 +Medium,
   //     4 +Low. Values outside 1..4 are clamped.
   //
   // ON by default, and that choice is load-bearing in the opposite direction
   // from most defaults: a healthy server reports no Critical or High errors at
   // all, so the default costs a quiet server nothing - while an administrator
   // who never finds the setting is exactly the administrator whose only
   // monitoring is the Windows event log. The flood risk that usually argues for
   // off-by-default is bounded instead: per event id, at most 5 events are
   // written per 10-minute window, and the first event written after a window in
   // which any were suppressed says how many. The ERROR log always has every one.
   //
   // ============================== RENDERING ===================================
   //
   // A classic event source renders through a message DLL named in the registry;
   // without one, Event Viewer shows "The description for Event ID ... cannot be
   // found" above the raw strings. hMailServer does not ship a message DLL.
   // Instead, on first use the sink best-effort registers the source (under
   // HKLM\SYSTEM\CurrentControlSet\Services\EventLog\Application\hMailServer)
   // with EventMessageFile pointing at the .NET Framework's EventLogMessages.dll,
   // whose message table maps every id 0-65535 to "%1" - which is precisely how
   // .NET applications render arbitrary ids cleanly, and the framework ships as a
   // Windows component on every supported OS. Each event therefore carries its
   // entire text as ONE insertion string. If registration is not possible (no
   // rights, no DLL), events are still written and carry the same text; only the
   // rendering degrades. An existing EventMessageFile value is never overwritten.
   class WindowsEventLog : public Singleton<WindowsEventLog>
   {
   public:

      WindowsEventLog();
      ~WindowsEventLog();

      // Windows event ids. The comment block above is the contract; this enum is
      // the code's copy of it.
      enum EventId
      {
         EventCriticalError = 2000,
         EventHighError = 2001,
         EventMediumError = 2002,
         EventLowError = 2003,
         EventDatabaseUnavailable = 2010,
         EventListenerFailed = 2011,
         EventCrash = 2012,
         EventBackupFailed = 2013,
         EventAutoBanImpaired = 2014,
         EventDiskSpaceFloor = 2015
      };

      // Forwards one reported error. Called by ErrorManager::ReportError_ for
      // every error, after the ERROR log line is written; everything that makes
      // this a signal channel rather than a firehose - the enabled switch, the
      // severity gate, the per-id throttle - is decided in here, so the caller
      // stays a single unconditional line.
      //
      // Must not throw (the caller is often inside a catch block; its outer
      // wrapper would eat the throw, but at the cost of the ERROR log entry it is
      // in the middle of producing), and must not call ErrorManager or Logger's
      // error path (this is called FROM there; the loop would be immediate).
      void OnError(int severity, int hmErrorId, const String &source, const String &description);

   private:

      // Maps an HM error code to its specific event id, or 0 when only the
      // severity catch-all applies.
      static int GetCuratedEventId_(int hmErrorId);

      // EVENTLOG_ERROR_TYPE / EVENTLOG_WARNING_TYPE / EVENTLOG_INFORMATION_TYPE
      // for an ErrorManager severity.
      static unsigned short GetEventType_(int severity);

      static String GetSeverityName_(int severity);

      // Best-effort, once per process. See the RENDERING section above.
      void EnsureSourceRegistered_();

      // Admits or suppresses one event for the given id, and reports how many
      // earlier events for that id were suppressed in the window that just
      // closed (0 almost always). Returns false when this event must not be
      // written.
      bool AdmitThroughThrottle_(int eventId, int &suppressedBefore);

      struct ThrottleBucket
      {
         ThrottleBucket() : window_start_ms(0), written(0), suppressed(0) {}

         unsigned __int64 window_start_ms;
         int written;
         int suppressed;
      };

      std::map<int, ThrottleBucket> throttle_;
      boost::mutex throttle_mutex_;
   };
}
