// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"

#include "DALConnection.h"
#include "DatabaseSettings.h"
#include "../Cache/CacheContainer.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   DALConnection::DALConnection(std::shared_ptr<DatabaseSettings> pDatabaseSettings) :
      try_count_(6),
      database_settings_(pDatabaseSettings)
   {

   }

   DALConnection::~DALConnection()
   {

   }

   bool 
   DALConnection::Execute(const SQLCommand &command, String &sErrorMessage, __int64 *iInsertID, int iIgnoreErrors) 
   {
      SQLCommand commandCopy = command;

      if (!GetSupportsCommandParameters() && command.GetParameters().size() > 0)
      {
         SQLStatement statement;
         String nonParameterQueryString = statement.GenerateFromCommand(command);

         commandCopy.ClearParameters();
         commandCopy.SetQueryString(nonParameterQueryString);
      }

      int iNoOfTries = 6;
      for (int i = 1 ; i <= try_count_; i++)
      {
         if (i == 2 || i == 4)
         {
            // Try to reconnect
            Reconnect(sErrorMessage);
         }

         DALConnection::ExecutionResult result = TryExecute(commandCopy, sErrorMessage, iInsertID, iIgnoreErrors);
         if (result == DALConnection::DALSuccess)
         {
            sErrorMessage = "";           
            return true;
         }

         if (result != DALConnection::DALConnectionProblem)
            break;

         // There's been a connection problem. Try to reconnect.
         Sleep(1000);
         Reconnect(sErrorMessage);
      }

      // We've reached the maximum number of 
      // reties without success. Time to give up.
      ErrorManager::Instance()->ReportError(ErrorManager::High, 5032, "DALConnection::Execute", sErrorMessage);

      return false;
   }

   bool
   DALConnection::HasIgnoreErrorsMarker(const String &sql)
   {
      // See the comment on the declaration in DALConnection.h for why this is a
      // scan rather than a Find().
      const String marker = _T("[IGNORE-ERRORS]");
      const int markerLength = marker.GetLength();
      const int length = sql.GetLength();

      bool inLiteral = false;
      bool inLineComment = false;
      bool inBlockComment = false;

      for (int i = 0; i < length; i++)
      {
         TCHAR c = sql[i];

         if (inLiteral)
         {
            if (c == _T('\\') && i + 1 < length)
            {
               // Backslash escape (MySQL default sql_mode): the next character
               // cannot close the literal.
               i++;
               continue;
            }

            if (c == _T('\''))
            {
               if (i + 1 < length && sql[i + 1] == _T('\''))
               {
                  // Doubled '' - still inside the literal.
                  i++;
                  continue;
               }

               inLiteral = false;
            }

            continue;
         }

         if (inLineComment)
         {
            if (c == _T('\n'))
            {
               inLineComment = false;
               continue;
            }
         }
         else if (inBlockComment)
         {
            if (c == _T('*') && i + 1 < length && sql[i + 1] == _T('/'))
            {
               inBlockComment = false;
               i++;
               continue;
            }
         }
         else
         {
            if (c == _T('\''))
            {
               inLiteral = true;
               continue;
            }

            if (c == _T('-') && i + 1 < length && sql[i + 1] == _T('-'))
            {
               inLineComment = true;
               i++;
               continue;
            }

            if (c == _T('/') && i + 1 < length && sql[i + 1] == _T('*'))
            {
               inBlockComment = true;
               i++;
               continue;
            }

            // Ordinary statement text. The marker does not count here: it is a
            // directive to the DAL, not SQL, so it can only legitimately live in
            // a comment.
            continue;
         }

         // Inside a comment.
         if (c == _T('[') && i + markerLength <= length &&
             sql.Mid(i, markerLength).Compare(marker.c_str()) == 0)
            return true;
      }

      return false;
   }

   bool
   DALConnection::Reconnect(String &sErrorMessage)
   {
#ifndef _ERROR_LOGGING_IN_MESSAGE_BOXES
      LOG_APPLICATION("DALConnection - Reconnecting to the database");
#endif

      // Disconnect first...
      Disconnect();
      
      // ... and now try to reconnect.
      if (Connect(sErrorMessage) != Connected)
         return false;   

      CacheContainer::Instance()->Clear();

      return true;
   }



}