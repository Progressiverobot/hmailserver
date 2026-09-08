// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"

#include "WindowsEventLog.h"

#include "ErrorManager.h"
#include "IniFileSettings.h"
#include "Logger.h"

#ifdef HM_PLATFORM_POSIX
// syslog(3) is what this class writes to here. It is the same kind of channel the
// Windows event log is - the place a machine's own monitoring already looks, kept
// for signals rather than for a second copy of the mail log - so everything above
// this line about what is forwarded, at what severity and under what throttle is
// unchanged, and only the three calls that hand the text to the system differ.
//
// One collision has to be dealt with rather than ignored. <syslog.h> defines
// LOG_DEBUG as a priority number and Logger.h, included just above, defines
// LOG_DEBUG as this server's debug-logging macro; whichever header is second
// wins. So the priorities this file needs are taken into constants here and the
// name is then put out of the way entirely. Leaving it defined as a number would
// mean that the next person to write LOG_DEBUG("...") in this file got a silent
// nonsense expression instead of a log line; leaving it undefined means they get
// a compiler error, which is the right answer.
#include <syslog.h>

namespace
{
   const int SYSLOG_PRIORITY_ERROR = LOG_ERR;
   const int SYSLOG_PRIORITY_WARNING = LOG_WARNING;
   const int SYSLOG_PRIORITY_INFORMATION = LOG_INFO;

   // LOG_MAIL is the facility a mail server's records belong in: it is what puts
   // them in the file an administrator's mail log already is, beside the MTA's.
   const int SYSLOG_FACILITY = LOG_MAIL;
   const int SYSLOG_OPTIONS = LOG_PID;
}

#undef LOG_DEBUG
#endif

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // The curated map: which HM error codes are, to an operator, one of the
      // conditions with a name. Everything else falls through to the severity
      // catch-alls, so this table can only make an event MORE specific - going
      // stale degrades an event to a less specific id, never to silence.
      //
      // The event ids these map to are a published contract (see the table in
      // WindowsEventLog.h). HM codes may be added to a category as they are
      // found; moving a code between categories changes what an existing alert
      // matches and needs the same caution as renumbering.
      struct CuratedMapping
      {
         int hm_code;
         int event_id;
      };

      const CuratedMapping curated_mappings[] =
      {
         // The database is unavailable: could not connect at startup or on
         // reconnect (per backend), schema checks could not run, a statement
         // gave up after its reconnect attempts, or the pool timed out.
         { 4354, WindowsEventLog::EventDatabaseUnavailable },
         { 5008, WindowsEventLog::EventDatabaseUnavailable },
         { 5010, WindowsEventLog::EventDatabaseUnavailable },
         { 5011, WindowsEventLog::EventDatabaseUnavailable },
         { 5027, WindowsEventLog::EventDatabaseUnavailable },
         { 5028, WindowsEventLog::EventDatabaseUnavailable },
         { 5029, WindowsEventLog::EventDatabaseUnavailable },
         { 5032, WindowsEventLog::EventDatabaseUnavailable },
         { 5085, WindowsEventLog::EventDatabaseUnavailable },
         { 5097, WindowsEventLog::EventDatabaseUnavailable },
         { 5098, WindowsEventLog::EventDatabaseUnavailable },
         { 5099, WindowsEventLog::EventDatabaseUnavailable },
         { 5180, WindowsEventLog::EventDatabaseUnavailable },

         // A listener could not open, bind or listen on its port. The service is
         // running and the SCM is content, which is exactly why this needs its
         // own event: to the Service Control Manager this failure is invisible.
         { 4316, WindowsEventLog::EventListenerFailed },
         { 4317, WindowsEventLog::EventListenerFailed },

         // A crash, a contained fault, or the crash reporter failing at its one
         // job. 5820 is the process terminating; 4208 and 6075 are exceptions
         // contained at the top of a session or worker; the rest are the
         // minidump machinery reporting that no dump could be produced.
         { 4208, WindowsEventLog::EventCrash },
         { 5519, WindowsEventLog::EventCrash },
         { 5521, WindowsEventLog::EventCrash },
         { 5820, WindowsEventLog::EventCrash },
         { 6015, WindowsEventLog::EventCrash },
         { 6016, WindowsEventLog::EventCrash },
         { 6075, WindowsEventLog::EventCrash },

         // A backup or restore failed.
         { 5014, WindowsEventLog::EventBackupFailed },

         // Brute-force protection is impaired: a ban range could not be saved, a
         // failure could not be counted, or spent failures could not be cleared.
         // Security controls that fail open belong in the event log precisely
         // because nothing visible to users has broken.
         { 6099, WindowsEventLog::EventAutoBanImpaired },
         { 6113, WindowsEventLog::EventAutoBanImpaired },
         { 6114, WindowsEventLog::EventAutoBanImpaired },

         // Free space fell below the floor; new mail is being refused with a
         // temporary failure. Reported once per band transition at the source,
         // so the throttle is a formality for this one.
         { 6230, WindowsEventLog::EventDiskSpaceFloor },
      };

      // The throttle: per event id, at most this many events per window. The
      // numbers are deliberately not settings - they exist to keep the worst
      // case bounded (a failing disk producing one High error per delivery, say)
      // and an administrator who wants a different volume of errors has the
      // severity level for it.
      const unsigned __int64 THROTTLE_WINDOW_MS = 10 * 60 * 1000;
      const int THROTTLE_MAX_PER_WINDOW = 5;

#ifndef HM_PLATFORM_POSIX
      // The name RegisterEventSource is called with. There is no counterpart on
      // POSIX - openlog is told the identity instead, and an unused constant here
      // would only draw a warning - so it is declared where it is used.
      const wchar_t *EVENT_SOURCE_NAME = L"hMailServer";
#endif
   }

   WindowsEventLog::WindowsEventLog()
   {
   }

   WindowsEventLog::~WindowsEventLog()
   {
   }

   int
   WindowsEventLog::GetCuratedEventId_(int hmErrorId)
   {
      for (const CuratedMapping &mapping : curated_mappings)
      {
         if (mapping.hm_code == hmErrorId)
            return mapping.event_id;
      }

      return 0;
   }

   unsigned short
   WindowsEventLog::GetEventType_(int severity)
   {
#ifdef HM_PLATFORM_POSIX
      // The syslog priority for the same three bands. LOG_ERR, LOG_WARNING and
      // LOG_INFO are what EVENTLOG_ERROR_TYPE, EVENTLOG_WARNING_TYPE and
      // EVENTLOG_INFORMATION_TYPE are on the other side - the same decision, in
      // the vocabulary the reader's tools filter on - so the mapping below is
      // line for line the Windows one.
      switch (severity)
      {
      case ErrorManager::Critical:
      case ErrorManager::High:
         return (unsigned short) SYSLOG_PRIORITY_ERROR;
      case ErrorManager::Medium:
         return (unsigned short) SYSLOG_PRIORITY_WARNING;
      default:
         return (unsigned short) SYSLOG_PRIORITY_INFORMATION;
      }
#else
      switch (severity)
      {
      case ErrorManager::Critical:
      case ErrorManager::High:
         // Both are faults in this codebase's own taxonomy - a bind failure or a
         // dead delivery path is High - and an operator triages on the type
         // column first. The two remain distinguishable by event id.
         return EVENTLOG_ERROR_TYPE;
      case ErrorManager::Medium:
         return EVENTLOG_WARNING_TYPE;
      default:
         return EVENTLOG_INFORMATION_TYPE;
      }
#endif
   }

   String
   WindowsEventLog::GetSeverityName_(int severity)
   {
      switch (severity)
      {
      case ErrorManager::Critical:
         return "Critical";
      case ErrorManager::High:
         return "High";
      case ErrorManager::Medium:
         return "Medium";
      case ErrorManager::Low:
         return "Low";
      default:
         return "Unknown";
      }
   }

   void
   WindowsEventLog::EnsureSourceRegistered_()
   //---------------------------------------------------------------------------
   // DESCRIPTION:
   // Best-effort source registration, once per process.
   //
   // ReportEvent works without any of this - an unregistered source still lands
   // in the Application log - but Event Viewer then renders "The description for
   // Event ID ... cannot be found" above the raw string. A registered
   // EventMessageFile whose message table maps the id to "%1" renders the string
   // as the entire description. The .NET Framework's EventLogMessages.dll is
   // such a table for every id 0-65535 (it is how .NET applications render
   // arbitrary ids), and the framework is a Windows component on every supported
   // OS, so it is used when present.
   //
   // Best-effort throughout: the service normally runs as LocalSystem and the
   // HKLM write succeeds; run as a restricted user it fails, the events are
   // written all the same, and only the rendering degrades. Failure must not be
   // REPORTED - this runs under ErrorManager::ReportError_, so reporting would
   // re-enter it - which is why the one note it leaves goes to the application
   // log. An installer could create this key at install time instead (and remove
   // it at uninstall), which would also cover servers run under restricted
   // accounts; this code then finds the value present and touches nothing.
   //---------------------------------------------------------------------------
   {
#ifdef HM_PLATFORM_POSIX
      // There is nothing to register. syslog has no per-source registry entry and
      // no message table: the text of an entry IS the entry, so the readable
      // rendering the Windows arm below goes to such lengths to arrange comes for
      // free here. All this has to do is open the connection once, with the
      // identity and the facility that every later syslog call inherits.
      //
      // openlog cannot fail and has nothing to report, so the failure path below -
      // which exists because the HKLM write can be refused - has no counterpart
      // and needs none.
      static boost::once_flag opened = BOOST_ONCE_INIT;

      boost::call_once(opened, []()
      {
         ::openlog("hmailserver", SYSLOG_OPTIONS, SYSLOG_FACILITY);
      });

      return;
#else
      static boost::once_flag registered = BOOST_ONCE_INIT;

      // Written inside the call_once, logged after it. Logging can reach
      // ErrorManager (the SQL log device reports its own degradation), and
      // ErrorManager comes back here: after the call_once that re-entry falls
      // straight through a completed flag, but from inside the lambda it would
      // re-enter a call_once still in progress on the same thread.
      static LONG registration_failure = ERROR_SUCCESS;

      boost::call_once(registered, []()
      {
         HKEY key = nullptr;

         LONG result = RegCreateKeyEx(HKEY_LOCAL_MACHINE,
            _T("SYSTEM\\CurrentControlSet\\Services\\EventLog\\Application\\hMailServer"),
            0, nullptr, REG_OPTION_NON_VOLATILE, KEY_QUERY_VALUE | KEY_SET_VALUE, nullptr, &key, nullptr);

         if (result != ERROR_SUCCESS)
         {
            registration_failure = result;
            return;
         }

         // Never overwrite an existing registration - an installer, or an
         // administrator pointing the source at a message file of their own,
         // outranks this fallback.
         DWORD valueType = 0;
         if (RegQueryValueEx(key, _T("EventMessageFile"), nullptr, &valueType, nullptr, nullptr) != ERROR_SUCCESS)
         {
            // The %SystemRoot% form is what goes into the registry (the value is
            // REG_EXPAND_SZ and survives relocation); the expanded form is only
            // for checking the file is actually there.
            TCHAR windowsDirectory[MAX_PATH] = { 0 };

            if (GetWindowsDirectory(windowsDirectory, MAX_PATH) > 0)
            {
               const String relativePath = _T("\\Microsoft.NET\\Framework64\\v4.0.30319\\EventLogMessages.dll");
               String expandedPath = String(windowsDirectory) + relativePath;

               if (FileUtilities::Exists(expandedPath))
               {
                  String storedPath = String(_T("%SystemRoot%")) + relativePath;

                  RegSetValueEx(key, _T("EventMessageFile"), 0, REG_EXPAND_SZ,
                     (const BYTE*) storedPath.c_str(),
                     (DWORD) ((storedPath.GetLength() + 1) * sizeof(TCHAR)));
               }
            }

            DWORD typesSupported = EVENTLOG_ERROR_TYPE | EVENTLOG_WARNING_TYPE | EVENTLOG_INFORMATION_TYPE;

            RegSetValueEx(key, _T("TypesSupported"), 0, REG_DWORD,
               (const BYTE*) &typesSupported, sizeof(typesSupported));
         }

         RegCloseKey(key);
      });

      if (registration_failure != ERROR_SUCCESS)
      {
         // Once: the note is only worth a line, and only the first time. Cleared
         // before logging so a re-entry from the logging itself finds it settled.
         LONG failure = registration_failure;
         registration_failure = ERROR_SUCCESS;

         LOG_APPLICATION(Formatter::Format(
            "The hMailServer event source could not be registered with Windows (error {0}). "
            "Events are still written to the Application log, but Event Viewer will show "
            "\"the description for the event id cannot be found\" above each one.", (int) failure));
      }
#endif
   }

   bool
   WindowsEventLog::AdmitThroughThrottle_(int eventId, int &suppressedBefore)
   {
      suppressedBefore = 0;

      unsigned __int64 now = GetTickCount64();

      boost::lock_guard<boost::mutex> guard(throttle_mutex_);

      ThrottleBucket &bucket = throttle_[eventId];

      if (bucket.window_start_ms == 0 || now - bucket.window_start_ms >= THROTTLE_WINDOW_MS)
      {
         // A new window. Whatever the old one suppressed is announced on this,
         // the first event that gets through, so a suppression is never
         // permanently silent.
         suppressedBefore = bucket.suppressed;

         bucket.window_start_ms = now;
         bucket.written = 0;
         bucket.suppressed = 0;
      }

      if (bucket.written >= THROTTLE_MAX_PER_WINDOW)
      {
         bucket.suppressed++;
         return false;
      }

      bucket.written++;
      return true;
   }

   void
   WindowsEventLog::OnError(int severity, int hmErrorId, const String &source, const String &description)
   {
      // Nothing may escape: the caller is ErrorManager mid-report, frequently
      // called from a catch block. A failure to mirror an error into the event
      // log is a silent failure by design - the ERROR log entry, already
      // written, is the record; this is the signal.
      try
      {
         auto ini_file_settings = IniFileSettings::Instance();

         if (!ini_file_settings->GetWindowsEventLogEnabled())
            return;

         // Read live on every event, like the log settings are, so a
         // Reinitialize picks up a change without a service restart.
         int level = ini_file_settings->GetWindowsEventLogLevel();

         if (level < ErrorManager::Critical)
            level = ErrorManager::Critical;
         if (level > ErrorManager::Low)
            level = ErrorManager::Low;

         if (severity > level)
            return;

         int eventId = GetCuratedEventId_(hmErrorId);

         if (eventId == 0)
         {
            switch (severity)
            {
            case ErrorManager::Critical:
               eventId = EventCriticalError;
               break;
            case ErrorManager::High:
               eventId = EventHighError;
               break;
            case ErrorManager::Medium:
               eventId = EventMediumError;
               break;
            default:
               eventId = EventLowError;
               break;
            }
         }

         int suppressedBefore = 0;
         if (!AdmitThroughThrottle_(eventId, suppressedBefore))
            return;

         EnsureSourceRegistered_();

         // One insertion string carrying everything, because the registered
         // message table renders exactly "%1" - a second string would be
         // invisible in the rendered view. The HM code makes the event
         // correlatable with its full record in the ERROR log.
         String message = Formatter::Format("hMailServer error HM{0}. Severity: {1}. Source: {2}. {3}",
            hmErrorId, GetSeverityName_(severity), source, description);

         if (suppressedBefore > 0)
         {
            message += Formatter::Format(" [{0} earlier event(s) with this event id were not written to the "
               "Windows event log in the previous ten minutes; the hMailServer ERROR log has every one.]",
               suppressedBefore);
         }

#ifdef HM_PLATFORM_POSIX
         // One call, because openlog has already said who this is and which
         // facility the entry belongs to. The format is "%s" and not the message
         // itself: the text carries an administrator's own strings - a file name,
         // a database error - and a stray per-cent in one of those would otherwise
         // be read as a conversion and print whatever was next on the stack.
         //
         // The event id goes into the text. syslog has no separate id field, and
         // the ids are a published contract that an administrator alerts on, so
         // they are written where they can still be matched: a Windows operator
         // filters on id 2010, and here the same event says "[2010]".
         const AnsiString narrow_message = message;

         ::syslog(GetEventType_(severity), "[%d] %s", eventId, narrow_message.c_str());
#else
         HANDLE eventSource = RegisterEventSource(NULL, EVENT_SOURCE_NAME);

         if (eventSource != NULL)
         {
            LPCTSTR strings[1] = { message.c_str() };
            ReportEvent(eventSource, GetEventType_(severity), 0, (DWORD) eventId, NULL, 1, 0, strings, NULL);
            DeregisterEventSource(eventSource);
         }
#endif
      }
      catch (...)
      {
         // See above. There is nowhere to report a failure to report.
      }
   }
}
