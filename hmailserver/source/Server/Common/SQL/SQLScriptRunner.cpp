// Copyright (c) 2009 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"

#include "SQLScriptRunner.h"
#include "SQLScriptParser.h"
#include "Macros/MacroParser.h"
#include "Macros/IMacroExpander.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   SQLScriptRunner::SQLScriptRunner() 
   {
      
   }

   bool
   SQLScriptRunner::ExecuteScript(std::shared_ptr<DALConnection> connectionObject, const String &sFile, String &sErrorMessage)
   {
      SQLScriptParser oParser(connectionObject->GetSettings(), sFile);
      if (!oParser.Parse(sErrorMessage))
      {
         sErrorMessage = "Parsing of SQL script failed: " + sFile + "\r\nError:" + sErrorMessage;
         return false;
      }

      if (oParser.GetNoOfCommands() == 0)
      {
         sErrorMessage = "Found no SQL commands in file : " + sFile;
         return false;
      }

      /*
         30 minute timeout per statement. Should hopefully never be needed.

         Restored on the way out whatever happens. The connection this runs on is
         usually one borrowed from the pool (DatabaseConnectionManager::
         ExecuteScript), and every early return below used to hand it back still
         carrying the half-hour timeout - so the next unrelated statement to be
         given that connection, on a live server, would sit for up to thirty
         minutes on a lock instead of thirty seconds. A script failing is exactly
         when that happens.
      */
      class TimeoutScope
      {
      public:
         TimeoutScope(std::shared_ptr<DALConnection> connection) :
            connection_(connection)
         {
            connection_->SetTimeout(60 * 30);
         }

         ~TimeoutScope()
         {
            try
            {
               connection_->SetTimeout(30);
            }
            catch (...)
            {
               // SetTimeout reaches COM on two of the four backends. Never let
               // it throw out of a destructor.
            }
         }

      private:
         std::shared_ptr<DALConnection> connection_;
      };

      TimeoutScope timeoutScope(connectionObject);

      for (int i = 0; i < oParser.GetNoOfCommands(); i++)
      {
         String sCommand = oParser.GetCommand(i);

         if (sCommand.StartsWith(_T("@@@")))
         {
            // Remove leading @@@.
            sCommand = sCommand.Mid(3);

            // Remove trailing @@@
            sCommand = sCommand.Mid(0, sCommand.Find(_T("@@@")));

            MacroParser parser(sCommand);
            Macro macro = parser.Parse();

            if (macro.GetType() == Macro::Unknown)
            {
               sErrorMessage = "Parsing of SQL script failed. Unknown macro. " + sFile + "\r\nMacro:" + sCommand;
               return false;
            }

            std::shared_ptr<IMacroExpander> macroExpander = connectionObject->CreateMacroExpander();
            if (!macroExpander->ProcessMacro(connectionObject, macro, sErrorMessage))
               return false;
         }
         else
         {
            if (connectionObject->TryExecute(SQLCommand(sCommand), sErrorMessage, 0, 0) != DALConnection::DALSuccess)
            {
               return false;
            }
         }
      }

      return true;
   }


}
