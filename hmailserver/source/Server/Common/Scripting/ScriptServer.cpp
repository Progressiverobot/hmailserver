// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"


#include "../../hMailServer/hMailServer.h"


#include "ScriptServer.h"
#include "ScriptSite.h"
#include "ScriptObjectContainer.h"


#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // Aborts a runaway script by interrupting the engine from a timer queue
      // thread.
      //
      // The watchdog holds its own reference to the engine, so the engine object
      // stays alive for as long as the watchdog exists - even after Terminate has
      // released the script site's reference. Disarm blocks until a callback which
      // has already started has returned and guarantees that no further callback
      // will start, and it always runs before the reference is dropped. A callback
      // therefore always executes while the engine is still alive; interrupting an
      // engine which has already been closed simply fails with an HRESULT.
      //
      // InterruptScriptThread is the only IActiveScript method documented as
      // callable from a thread other than the one which created the engine, so it
      // is the only thing the callback is allowed to touch. In particular the
      // callback must not report errors: reporting fires the OnError script event,
      // which would run a script engine on a pool thread that has not initialized
      // COM. The calling thread reports instead, once Disarm has returned.
      class ScriptWatchdog
      {
      public:
         ScriptWatchdog(const CComPtr<IActiveScript> &engine, int timeout_seconds) :
            engine_(engine),
            timer_(0),
            fired_(0)
         {
            // Zero means the administrator has turned the limit off.
            if (timeout_seconds <= 0 || !engine_)
               return;

            DWORD due_time = static_cast<DWORD>(timeout_seconds) * 1000UL;

            if (!CreateTimerQueueTimer(&timer_, NULL, OnTimeout_, this, due_time, 0, WT_EXECUTEONLYONCE | WT_EXECUTELONGFUNCTION))
            {
               DWORD last_error = ::GetLastError();

               timer_ = 0;

               // Logged rather than reported, since reporting fires the OnError
               // event which would arm another watchdog and fail again.
               String sMessage;
               sMessage.Format(_T("ScriptServer: Failed to create the script execution watchdog timer. Windows error code: %d"), last_error);
               Logger::Instance()->LogError(sMessage);
            }
         }

         ~ScriptWatchdog()
         {
            Disarm();
         }

         void Disarm()
         {
            if (timer_ == 0)
               return;

            HANDLE timer = timer_;
            timer_ = 0;

            // INVALID_HANDLE_VALUE makes this wait for a callback which is already
            // running, so once this succeeds the callback can no longer reach this
            // object. It must never be called from the callback itself.
            //
            // The result is checked because a failure is the one path that leaves a
            // callback able to run against a watchdog that is about to be destroyed
            // - a stack object here - so it must not pass silently.
            if (!DeleteTimerQueueTimer(NULL, timer, INVALID_HANDLE_VALUE))
            {
               DWORD deleteError = GetLastError();

               String message;
               message.Format(_T("Could not cancel the script execution watchdog. Windows error code: %d."), (int) deleteError);
               ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5022, "ScriptServer::ScriptWatchdog::Disarm", message);
            }
         }

         bool Fired()
         {
            return InterlockedCompareExchange(&fired_, 0, 0) != 0;
         }

      private:

         static VOID CALLBACK OnTimeout_(PVOID parameter, BOOLEAN /*timer_or_wait_fired*/)
         {
            ScriptWatchdog *watchdog = reinterpret_cast<ScriptWatchdog*>(parameter);

            InterlockedExchange(&watchdog->fired_, 1);

            watchdog->engine_->InterruptScriptThread(SCRIPTTHREADID_ALL, NULL, 0);
         }

         CComPtr<IActiveScript> engine_;
         HANDLE timer_;
         volatile LONG fired_;
      };
   }

   ScriptServer::ScriptServer(void) :
      has_on_client_connect_(false),
      has_on_accept_message_(false),
      has_on_deliver_message_(false),
      has_on_backup_completed_(false),
      has_on_backup_failed_(false),
      has_on_delivery_start_(false),
      has_on_error_(false),
      has_on_delivery_failed_(false),
      has_on_external_account_download_(false),
      has_on_smtpdata_(false),
      has_on_helo_(false),
      has_on_client_logon_(false),
      has_on_client_validate_password_(false),
      has_on_recipient_unknown_(false),
      has_on_too_many_invalid_comands_(false),
      scripts_loaded_(false)
   {
      
   }

   ScriptServer::~ScriptServer(void)
   {
      
   }

   String
   ScriptServer::GetScriptExtension_(const String &language)
   {
      if (language == _T("VBScript"))
         return "vbs";

      if (language == _T("JScript"))
         return "js";

      // An unrecognized language deliberately gives an empty extension, which
      // yields a file name that will not be found and so a script which is empty.
      return "";
   }

   String
   ScriptServer::GetCurrentScriptFile() const
   {
      String sEventsDir = IniFileSettings::Instance()->GetEventDirectory();

      String sScriptLanguage = Configuration::Instance()->GetScriptLanguage();

      String sFileName = sEventsDir + "\\EventHandlers." + GetScriptExtension_(sScriptLanguage);

      return sFileName;
   }

   void
   ScriptServer::LoadScripts()
   {
      // A load is all-or-nothing, and it never partly succeeds.
      //
      // The handler flags gate security-relevant events - OnClientLogon and
      // OnClientValidatePassword among them - so the server must never advertise a
      // handler which the script text it is holding cannot actually run. Nothing is
      // installed here until the candidate script has both compiled and been
      // searched for handlers, so a load which fails leaves the previously loaded
      // script in force in its entirety: contents, language and flags together.
      //
      // Keeping the last known-good script is the conservative choice for a mail
      // server. The alternative - clearing everything when a load fails - would let
      // a typo in a hot reload silently switch off an anti-spam or a logon handler
      // on a running server, which is worse than continuing to run the script that
      // was working a moment earlier. On a cold start there is no known-good script
      // to keep, so the same code path registers nothing at all, which is what
      // fail-closed means there.
      try
      {
         String new_language = Configuration::Instance()->GetScriptLanguage();
         String new_extension = GetScriptExtension_(new_language);

         String script_file = GetCurrentScriptFile();

         // Read once, then compile and search that exact text. Reading the file
         // again for the syntax check would allow a file which changed in between to
         // be checked and a different one installed.
         String new_contents = FileUtilities::ReadCompleteTextFile(script_file);

         String sMessage = CompileContents_(new_language, script_file, new_contents);
         if (!sMessage.IsEmpty())
         {
            // Reported before anything has been installed, so firing OnError from
            // here runs the previous script, which is still the one in force.
            String report;

            if (scripts_loaded_)
            {
               report = "The scripts in the event directory could not be compiled and have not been loaded. The previously loaded scripts remain in force, so the server is still running the event handlers it had before this reload. ";
               report += sMessage;

               ErrorManager::Instance()->ReportError(ErrorManager::High, 5710, "ScriptServer::LoadScripts", report);
            }
            else
            {
               report = "The scripts in the event directory could not be compiled and have not been loaded. No event handlers are registered until this is fixed and the scripts are reloaded. ";
               report += sMessage;

               ErrorManager::Instance()->ReportError(ErrorManager::High, 5016, "ScriptServer::LoadScripts", report);
            }

            return;
         }

         // Determine which functions are available. Detected into locals: each of
         // these runs the script, so any of them can fail, and committing them as
         // they are found would leave some flags describing the new script and the
         // rest the old one.
         bool on_client_connect = DoesFunctionExist_(new_language, new_contents, "OnClientConnect");
         bool on_accept_message = DoesFunctionExist_(new_language, new_contents, "OnAcceptMessage");
         bool on_deliver_message = DoesFunctionExist_(new_language, new_contents, "OnDeliverMessage");
         bool on_backup_completed = DoesFunctionExist_(new_language, new_contents, "OnBackupCompleted");
         bool on_backup_failed = DoesFunctionExist_(new_language, new_contents, "OnBackupFailed");
         bool on_delivery_start = DoesFunctionExist_(new_language, new_contents, "OnDeliveryStart");
         bool on_error = DoesFunctionExist_(new_language, new_contents, "OnError");
         bool on_delivery_failed = DoesFunctionExist_(new_language, new_contents, "OnDeliveryFailed");
         bool on_external_account_download = DoesFunctionExist_(new_language, new_contents, "OnExternalAccountDownload");
         bool on_smtpdata = DoesFunctionExist_(new_language, new_contents, "OnSMTPData");
         bool on_helo = DoesFunctionExist_(new_language, new_contents, "OnHELO");
         bool on_client_logon = DoesFunctionExist_(new_language, new_contents, "OnClientLogon");
         bool on_client_validate_password = DoesFunctionExist_(new_language, new_contents, "OnClientValidatePassword");
         bool on_recipient_unknown = DoesFunctionExist_(new_language, new_contents, "OnRecipientUnknown");
         bool on_too_many_invalid_commands = DoesFunctionExist_(new_language, new_contents, "OnTooManyInvalidCommands");

         // Commit. Nothing in this block runs a script, reports an error or takes
         // another lock, so an event being fired on another thread sees either the
         // whole of the old script or the whole of the new one.
         boost::lock_guard<boost::recursive_mutex> guard(state_mutex_);

         script_language_ = new_language;
         script_extension_ = new_extension;
         script_contents_ = new_contents;

         has_on_client_connect_ = on_client_connect;
         has_on_accept_message_ = on_accept_message;
         has_on_deliver_message_ = on_deliver_message;
         has_on_backup_completed_ = on_backup_completed;
         has_on_backup_failed_ = on_backup_failed;
         has_on_delivery_start_ = on_delivery_start;
         has_on_error_ = on_error;
         has_on_delivery_failed_ = on_delivery_failed;
         has_on_external_account_download_ = on_external_account_download;
         has_on_smtpdata_ = on_smtpdata;
         has_on_helo_ = on_helo;
         has_on_client_logon_ = on_client_logon;
         has_on_client_validate_password_ = on_client_validate_password;
         has_on_recipient_unknown_ = on_recipient_unknown;
         has_on_too_many_invalid_comands_ = on_too_many_invalid_commands;

         scripts_loaded_ = true;
      }
      catch (...)
      {
         // Nothing is installed before the commit above, and the commit itself only
         // assigns, so there is nothing to roll back here - whatever was loaded
         // before is untouched and still coherent.
         ErrorManager::Instance()->ReportError(ErrorManager::High, 5017, "ScriptServer::LoadScripts", "An exception was thrown when loading scripts. The scripts have not been loaded; any scripts loaded before this attempt remain in force.");
      }
   }


   String
   ScriptServer::CheckSyntax()
   {
      String sEventsDir = IniFileSettings::Instance()->GetEventDirectory();      

      String sScriptLanguage = Configuration::Instance()->GetScriptLanguage();
      String sScriptExtension = GetScriptExtension_(sScriptLanguage);

      String sErrorMessage;

      // Compile the scripts.
      sErrorMessage = Compile_(sScriptLanguage, sEventsDir + "\\EventHandlers." + sScriptExtension);
      if (!sErrorMessage.IsEmpty())
         return sErrorMessage;

      return String("");
   }

   bool
   ScriptServer::RunInterruptible_(CComObject<CScriptSiteBasic> *pBasic)
   {
      ScriptWatchdog watchdog(pBasic->GetEngine(), IniFileSettings::Instance()->GetScriptTimeout());

      pBasic->Run();

      // Disarm before reading the flag, so that the callback cannot still be in
      // flight while the caller decides what to do, and so that it can never run
      // concurrently with the Terminate which follows.
      watchdog.Disarm();

      return watchdog.Fired();
   }

   void
   ScriptServer::ReportInterruption_(const String &sContext, bool bReportError)
   {
      String sMessage;
      sMessage.Format(_T("The script %s did not complete within %d seconds and was interrupted. An interrupt aborts script execution, but cannot release a handler which is blocked inside a COM call."),
         sContext.c_str(), IniFileSettings::Instance()->GetScriptTimeout());

      if (bReportError)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 5019, "ScriptServer::ReportInterruption_", sMessage);
      }
      else
      {
         // Reporting an error fires the OnError event. When it is the OnError
         // handler itself which had to be killed, firing it again would recurse
         // until the stack is exhausted.
         Logger::Instance()->LogError(sMessage);
      }
   }

   bool
   ScriptServer::DoesFunctionExist_(const String &language, const String &contents, const String &sProcedure)
   {
      // Create an instance of the script engine and execute the script.
      CComObject<CScriptSiteBasic>* pBasic;
      CComObject<CScriptSiteBasic>::CreateInstance(&pBasic);

      CComQIPtr<IActiveScriptSite> spUnk;

      if (!pBasic)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 5017, "ScriptServer::FireEvent", "Failed to create instance of script site.");
         return false;
      }

      spUnk = pBasic;
      pBasic->Initiate(language, NULL);
      pBasic->AddScript(contents);
      bool interrupted = RunInterruptible_(pBasic);
      bool bExists = pBasic->ProcedureExists(sProcedure);
      pBasic->Terminate();

      if (interrupted)
      {
         ReportInterruption_("EventHandlers (function detection)", true);
      }

      return bExists;
   }


   String
   ScriptServer::Compile_(const String &sLanguage, const String &sFilename)
   {
      return CompileContents_(sLanguage, sFilename, FileUtilities::ReadCompleteTextFile(sFilename));
   }

   String
   ScriptServer::CompileContents_(const String &sLanguage, const String &sFilename, const String &sContents)
   {
      if (sContents.IsEmpty())
         return "";

      // Create an instance of the script engine and execute the script.
      CComObject<CScriptSiteBasic>* pBasic;
      CComObject<CScriptSiteBasic>::CreateInstance(&pBasic);

      CComQIPtr<IActiveScriptSite> spUnk;

      if (!pBasic)
         return "ScriptServer:: Failed to create instance of script site.";

      spUnk = pBasic; // let CComQIPtr tidy up for us
      pBasic->Initiate(sLanguage, NULL);
      // pBasic->SetObjectContainer(pObjects);
      pBasic->AddScript(sContents);
      bool interrupted = RunInterruptible_(pBasic);
      pBasic->Terminate();

      String sErrorMessage = pBasic->GetLastError();

      if (interrupted)
      {
         // Reported separately, and with the consequence spelled out. This is not a
         // syntax error, but the caller treats any message the same way - it abandons
         // the load, so the new script does not take effect at all. An administrator
         // has to be told that plainly rather than left reading "compile error".
         // Loading deliberately does not continue: discovering the handlers runs the
         // top-level script once per handler, so a script that hangs here would hang
         // another fifteen times. LoadScripts adds which state the server was left
         // in - the previous script, or nothing at all on a cold start.
         String sTimeoutMessage;
         sTimeoutMessage.Format(_T("The script file %s did not finish executing within %d seconds and was interrupted. Code outside a function runs when the file is loaded, so it must return promptly. The script has not been loaded."),
            sFilename.c_str(), IniFileSettings::Instance()->GetScriptTimeout());

         ErrorManager::Instance()->ReportError(ErrorManager::High, 5021, "ScriptServer::CompileContents_", sTimeoutMessage);

         if (!sErrorMessage.IsEmpty())
            sErrorMessage += "\r\n";

         sErrorMessage += sTimeoutMessage;
      }

      if (!sErrorMessage.IsEmpty())
         sErrorMessage = "File: " + sFilename + "\r\n" + sErrorMessage;

      return sErrorMessage;

   }

   bool
   ScriptServer::IsHandlerRegistered_(Event e, String &event_name) const
   {
      switch (e)
      {
      case EventOnClientConnect:
         if (!has_on_client_connect_)
            return false;
         event_name = _T("OnClientConnect");
         break;
      case EventOnAcceptMessage:
         if (!has_on_accept_message_)
            return false;
         event_name = _T("OnAcceptMessage");
         break;
      case EventOnDeliverMessage:
         if (!has_on_deliver_message_)
            return false;
         event_name = _T("OnDeliverMessage");
         break;
      case EventOnBackupCompleted:
         if (!has_on_backup_completed_)
            return false;
         event_name = _T("OnBackupCompleted");
         break;
      case EventOnBackupFailed:
         if (!has_on_backup_failed_)
            return false;
         event_name = _T("OnBackupFailed");
         break;
      case EventOnError:
         if (!has_on_error_)
            return false;
         event_name = _T("OnError");
         break;
      case EventOnDeliveryStart:
         if (!has_on_delivery_start_)
            return false;
         event_name = _T("OnDeliveryStart");
         break;
      case EventOnDeliveryFailed:
         if (!has_on_delivery_failed_)
            return false;
         event_name = _T("OnDeliveryFailed");
         break;
      case EventOnExternalAccountDownload:
         if (!has_on_external_account_download_)
            return false;
         event_name = _T("OnExternalAccountDownload");
         break;
      case EventOnSMTPData:
         if (!has_on_smtpdata_)
            return false;
         event_name = _T("OnSMTPData");
         break;
      case EventOnHELO:
         if (!has_on_helo_)
            return false;
         event_name = _T("OnHELO");
         break;
      case EventOnClientLogon:
         if (!has_on_client_logon_)
            return false;
         event_name = _T("OnClientLogon");
         break;
      case EventOnClientValidatePassword:
         if (!has_on_client_validate_password_)
            return false;
         event_name = _T("OnClientValidatePassword");
         break;
      case EventOnRecipientUnknown:
         if (!has_on_recipient_unknown_)
            return false;
         event_name = _T("OnRecipientUnknown");
         break;
      case EventOnTooManyInvalidCommands:
         if (!has_on_too_many_invalid_comands_)
            return false;
         event_name = _T("OnTooManyInvalidCommands");
         break;
      case EventCustom:
         // Not gated by a flag: the caller names the procedure to run.
         break;
      default:
         {
            return false;
         }
      }

      return true;
   }

   void
   ScriptServer::FireEvent(Event e,  const String &sEventCaller, std::shared_ptr<ScriptObjectContainer> pObjects)
   {
      if (!Configuration::Instance()->GetUseScriptServer())
         return;

     // JDR: stores the name of the method that is fired in the script. https://www.progressiverobot.com/forum/viewtopic.php?f=2&t=25497
     String event_name = _T("Unknown");

      String script_language;
      String script_contents;

      {
         // The handler flags and the script text are taken together, under the lock,
         // so that a reload running at the same time cannot show this event a flag
         // that came from the new script and the text of the old one - and so that
         // the text is never copied while it is being replaced.
         //
         // The lock is released before the script runs. A script is allowed to call
         // Scripting.Reload, and holding the lock across execution would deadlock
         // the server on the first script that did.
         boost::lock_guard<boost::recursive_mutex> guard(state_mutex_);

         if (!IsHandlerRegistered_(e, event_name))
            return;

         script_language = script_language_;
         script_contents = script_contents_;
      }

      LOG_DEBUG("Executing event " + event_name);

      String sScript;

      // Build the script.
      if (script_language == _T("VBScript"))
         sScript = script_contents + "\r\n\r\n" + "Call " + sEventCaller + "\r\n";
      else if (script_language == _T("JScript"))
         sScript = script_contents + "\r\n\r\n" + sEventCaller + ";\r\n";

      CComObject<CScriptSiteBasic>* pBasic;
      CComObject<CScriptSiteBasic>::CreateInstance(&pBasic);
      CComQIPtr<IActiveScriptSite> spUnk;
      
      if (!pBasic)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 5018, "ScriptServer::FireEvent", "Failed to create instance of script site.");
         return;
      }
      
      spUnk = pBasic; // let CComQIPtr tidy up for us
      pBasic->Initiate(script_language, NULL);
      pBasic->SetObjectContainer(pObjects);
      pBasic->AddScript(sScript);
      bool interrupted = RunInterruptible_(pBasic);
      pBasic->Terminate();

      if (interrupted)
      {
         ReportInterruption_(event_name, e != EventOnError);
      }

      LOG_DEBUG("Event completed");
   }

}
