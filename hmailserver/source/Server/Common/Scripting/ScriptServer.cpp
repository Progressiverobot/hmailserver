// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

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
      has_on_too_many_invalid_comands_(false)
   {
      
   }

   ScriptServer::~ScriptServer(void)
   {
      
   }

   String 
   ScriptServer::GetCurrentScriptFile() const
   {
      String sEventsDir = IniFileSettings::Instance()->GetEventDirectory();

      String sScriptLanguage = Configuration::Instance()->GetScriptLanguage();

      String sExtension;
      if (sScriptLanguage == _T("VBScript"))
         sExtension = "vbs";
      else if (sScriptLanguage == _T("JScript"))
         sExtension = "js";
      else
         sExtension = "";

      String sFileName = sEventsDir + "\\EventHandlers." + sExtension;

      return sFileName;
   }

   void
   ScriptServer::LoadScripts()
   {
      try
      {
         script_language_ = Configuration::Instance()->GetScriptLanguage();

         if (script_language_ == _T("VBScript"))
            script_extension_ = "vbs";
         else if (script_language_ == _T("JScript"))
            script_extension_ = "js";
         else
            script_extension_ = "";

         String sCurrentScriptFile = GetCurrentScriptFile();

         // Load the script file from disk
         script_contents_ = FileUtilities::ReadCompleteTextFile(sCurrentScriptFile);

         // Do a syntax check before loading it.
         String sMessage = ScriptServer::CheckSyntax();
         if (!sMessage.IsEmpty())
         {
            ErrorManager::Instance()->ReportError(ErrorManager::High, 5016, "ScriptServer::LoadScripts", sMessage);
            return;
         }
         
         // Determine which functions are available.
         has_on_client_connect_ = DoesFunctionExist_("OnClientConnect");
         has_on_accept_message_ = DoesFunctionExist_("OnAcceptMessage");
         has_on_deliver_message_ = DoesFunctionExist_("OnDeliverMessage");
         has_on_backup_completed_ = DoesFunctionExist_("OnBackupCompleted");
         has_on_backup_failed_ = DoesFunctionExist_("OnBackupFailed");
         has_on_delivery_start_ = DoesFunctionExist_("OnDeliveryStart");
         has_on_error_ = DoesFunctionExist_("OnError");
         has_on_delivery_failed_ = DoesFunctionExist_("OnDeliveryFailed");
         has_on_external_account_download_ = DoesFunctionExist_("OnExternalAccountDownload");
         has_on_smtpdata_ = DoesFunctionExist_("OnSMTPData");
         has_on_helo_ = DoesFunctionExist_("OnHELO");
         has_on_client_logon_ = DoesFunctionExist_("OnClientLogon");
         has_on_client_validate_password_ = DoesFunctionExist_("OnClientValidatePassword");
         has_on_recipient_unknown_ = DoesFunctionExist_("OnRecipientUnknown");
         has_on_too_many_invalid_comands_ = DoesFunctionExist_("OnTooManyInvalidCommands");
      }
      catch (...)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 5017, "ScriptServer::LoadScripts", "An exception was thrown when loading scripts.");
      }
   }


   String
   ScriptServer::CheckSyntax()
   {
      String sEventsDir = IniFileSettings::Instance()->GetEventDirectory();      

      String sScriptLanguage = Configuration::Instance()->GetScriptLanguage();
      String sScriptExtension;
      if (sScriptLanguage == _T("VBScript"))
         sScriptExtension = "vbs";
      else if (sScriptLanguage == _T("JScript"))
         sScriptExtension = "js";
      else
         sScriptExtension = "";

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
   ScriptServer::DoesFunctionExist_(const String &sProcedure)
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
      pBasic->Initiate(script_language_, NULL);
      pBasic->AddScript(script_contents_);
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
      String sContents = FileUtilities::ReadCompleteTextFile(sFilename);

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
         // syntax error, but the caller treats any message the same way - it stops
         // loading, so every event handler stays unregistered until the scripts are
         // reloaded. That includes OnClientLogon and OnClientValidatePassword, so an
         // administrator has to be told plainly rather than left reading "compile
         // error". Loading deliberately does not continue: discovering the handlers
         // runs the top-level script once per handler, so a script that hangs here
         // would hang another fifteen times.
         String sTimeoutMessage;
         sTimeoutMessage.Format(_T("The script file %s did not finish executing within %d seconds and was interrupted. Code outside a function runs when the file is loaded, so it must return promptly. No event handlers are registered until this is fixed and the scripts are reloaded."),
            sFilename.c_str(), IniFileSettings::Instance()->GetScriptTimeout());

         ErrorManager::Instance()->ReportError(ErrorManager::High, 5021, "ScriptServer::Compile_", sTimeoutMessage);

         if (!sErrorMessage.IsEmpty())
            sErrorMessage += "\r\n";

         sErrorMessage += sTimeoutMessage;
      }

      if (!sErrorMessage.IsEmpty())
         sErrorMessage = "File: " + sFilename + "\r\n" + sErrorMessage;

      return sErrorMessage;

   }

   void 
   ScriptServer::FireEvent(Event e,  const String &sEventCaller, std::shared_ptr<ScriptObjectContainer> pObjects)
   {
      if (!Configuration::Instance()->GetUseScriptServer())
         return;

	  // JDR: stores the name of the method that is fired in the script. http://www.hmailserver.com/forum/viewtopic.php?f=2&t=25497
	  String event_name = _T("Unknown");

      switch (e)
      {
      case EventOnClientConnect:
         if (!has_on_client_connect_)
            return;
         event_name = _T("OnClientConnect");
         break;
      case EventOnAcceptMessage:
         if (!has_on_accept_message_)
            return;
         event_name = _T("OnAcceptMessage");
         break;
      case EventOnDeliverMessage:
         if (!has_on_deliver_message_)
            return;
         event_name = _T("OnDeliverMessage");
         break;
      case EventOnBackupCompleted:
         if (!has_on_backup_completed_)
            return;
         event_name = _T("OnBackupCompleted");
         break;
      case EventOnBackupFailed:
         if (!has_on_backup_failed_)
            return;
         event_name = _T("OnBackupFailed");
         break;
      case EventOnError:
         if (!has_on_error_)
            return;
         event_name = _T("OnError");
         break;
      case EventOnDeliveryStart:
         if (!has_on_delivery_start_)
            return;
         event_name = _T("OnDeliveryStart");
         break;
      case EventOnDeliveryFailed:
         if (!has_on_delivery_failed_)
            return;
         event_name = _T("OnDeliveryFailed");
         break;
      case EventOnExternalAccountDownload:
         if (!has_on_external_account_download_)
            return;
         event_name = _T("OnExternalAccountDownload");
         break;
      case EventOnSMTPData:
         if (!has_on_smtpdata_)
            return;
         event_name = _T("OnSMTPData");
         break;
      case EventOnHELO:
         if (!has_on_helo_)
            return;
         event_name = _T("OnHELO");
         break;
      case EventOnClientLogon:
         if (!has_on_client_logon_)
            return;
         event_name = _T("OnClientLogon");
         break;
      case EventOnClientValidatePassword:
         if (!has_on_client_validate_password_)
            return;
         event_name = _T("OnClientValidatePassword");
         break;
      case EventOnRecipientUnknown:
         if (!has_on_recipient_unknown_)
            return;
         event_name = _T("OnRecipientUnknown");
         break;
      case EventOnTooManyInvalidCommands:
         if (!has_on_too_many_invalid_comands_)
            return;
         event_name = _T("OnTooManyInvalidCommands");
         break;
      case EventCustom:
         break;
      default:
         {
            return;
         }
      }

	   LOG_DEBUG("Executing event " + event_name);

      String sScript;

      // Build the script.
      if (script_language_ == _T("VBScript"))
         sScript = script_contents_ + "\r\n\r\n" + "Call " + sEventCaller + "\r\n";
      else if (script_language_ == _T("JScript"))
         sScript = script_contents_ + "\r\n\r\n" + sEventCaller + ";\r\n";

      CComObject<CScriptSiteBasic>* pBasic;
      CComObject<CScriptSiteBasic>::CreateInstance(&pBasic);
      CComQIPtr<IActiveScriptSite> spUnk;
      
      if (!pBasic)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 5018, "ScriptServer::FireEvent", "Failed to create instance of script site.");
         return;
      }
      
      spUnk = pBasic; // let CComQIPtr tidy up for us
      pBasic->Initiate(script_language_, NULL);
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
