// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

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

      static String GetScriptExtension_(const String &language);
      // Returns the file extension used by the given script language, or an empty
      // string if the language is not one hMailServer can run.

      bool DoesFunctionExist_(const String &language, const String &contents, const String &sProcedure);
      // Runs the given script text and reports whether it declares the named
      // procedure. Takes the text as an argument rather than reading the loaded
      // script, so that LoadScripts can search a candidate script without having
      // installed it.

      bool IsHandlerRegistered_(Event e, String &event_name) const;
      // Returns whether a handler for e was found in the loaded script, and names
      // the event. Must be called with state_mutex_ held, since it reads the
      // handler flags.

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

      String CompileContents_(const String &sLanguage, const String &sFilename, const String &sContents);
      // As Compile_, but compiles text which the caller has already read. sFilename
      // is used only to name the file in the error message. This is what lets a
      // load check exactly the text it is about to install, rather than reading the
      // file a second time and checking whatever is on disk by then.

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

      // Whether a script has ever been loaded successfully. Used only to tell an
      // administrator whether a rejected load left a working script in force or
      // left the server with no event handlers at all.
      bool scripts_loaded_;

      String script_contents_;
      String script_extension_;
      String script_language_;

      // Guards the handler flags and the script text against a reload which runs
      // while events are being fired. The two must be read together: a flag that
      // came from the new script paired with the text of the old one is exactly the
      // incoherence this lock exists to prevent. It is held only long enough to
      // read or to replace them - never while a script runs, since a script is
      // allowed to call Scripting.Reload.
      boost::recursive_mutex state_mutex_;

   };
}