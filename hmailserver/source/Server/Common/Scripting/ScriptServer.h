// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "ScriptSite.h"

namespace HM
{
   class ScriptObjectContainer;

   class ScriptServer : public Singleton<ScriptServer>
   {
   public:
      
      enum Event
      {
         EventOnClientConnect = 1001,
         EventOnAcceptMessage = 1002,
         EventOnDeliverMessage = 1003,
         EventOnBackupCompleted = 1004,
         EventOnBackupFailed = 1005,
         EventOnDeliveryStart = 1006,
         EventOnError = 1007,
         EventOnDeliveryFailed = 1008,
         
         EventCustom = 1010,
         
         EventOnExternalAccountDownload = 1011,
         EventOnSMTPData = 1012,
         EventOnHELO = 1013,
         EventOnClientLogon = 1014,
         EventOnClientValidatePassword = 1015,
         EventOnRecipientUnknown = 1016,
         EventOnTooManyInvalidCommands = 1017
      };

      ScriptServer(void);
      ~ScriptServer(void);

      // Fires an event (if the script engine has been turned
      // on in hMailAdmin. 
      void FireEvent(Event e, const String &sEventCaller, std::shared_ptr<ScriptObjectContainer> pObjects);

      // Checks the syntax of the scripts in the
      // event directory and return the result.
      String CheckSyntax();

      // Refreshes the scripts in the event directory
      void LoadScripts();
      
      String GetCurrentScriptFile() const;   
   
   private:

      bool DoesFunctionExist_(const String &sProcedure);

      bool RunInterruptible_(CComObject<CScriptSiteBasic> *pBasic);
      // Runs the script which has been added to pBasic, under a watchdog which
      // aborts execution once the configured script timeout has elapsed. A
      // timeout of zero means the administrator has disabled the limit.
      // Returns true if the watchdog had to interrupt the script.

      void ReportInterruption_(const String &sContext, bool bReportError);
      // Reports that the script named by sContext was killed by the watchdog.
      // bReportError must be false when the killed script is the OnError
      // handler itself, since reporting an error fires OnError again.

      String Compile_(const String &sLanguage, const String &sFilename);
      // Compiels the script in sFileName and returns the result. If
      // no compilation errors exists, the function returns an emtpy string.
 
      bool has_on_client_connect_;
      bool has_on_accept_message_;
      bool has_on_deliver_message_;
      bool has_on_backup_completed_;
      bool has_on_backup_failed_;
      bool has_on_delivery_start_;
      bool has_on_error_;
      bool has_on_delivery_failed_;
      bool has_on_external_account_download_;
      bool has_on_smtpdata_;
      bool has_on_helo_;
      bool has_on_client_logon_;
      bool has_on_client_validate_password_;
      bool has_on_recipient_unknown_;
      bool has_on_too_many_invalid_comands_;

      String script_contents_;
      String script_extension_;
      String script_language_;

   };
}