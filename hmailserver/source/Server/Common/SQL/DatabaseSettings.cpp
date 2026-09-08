// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"

#include "DatabaseSettings.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   DatabaseSettings::DatabaseSettings(const String &sDatabaseProvider, const String &sDatabaseServer, const String &sDatabaseName, const String &sUsername, const String &sPassword,
                                      const String &sDatabaseDirectory, const String &sDatabaseServerFailoverPartner, HM::DatabaseSettings::SQLDBType dbType, long lDBPort) :
      database_server_(sDatabaseServer),
      database_name_(sDatabaseName),
      username_(sUsername),
      password_(sPassword),
      database_directory_(sDatabaseDirectory),
      sqldbtype_(dbType),
      database_server_failover_partner_(sDatabaseServerFailoverPartner),
      dbport_(lDBPort),
      database_provider_(sDatabaseProvider)
   {

   }

   DatabaseSettings::~DatabaseSettings()
   {

   }

   String 
   DatabaseSettings::GetDefaultScript()
   {
      String sFolder = IniFileSettings::Instance()->GetDBScriptDirectory();
      
      String sFile;
      switch (sqldbtype_)
      {
      case TypeMSSQLServer:
      case TypeMSSQLCompactEdition:
         sFile = "CreateTablesMSSQL.sql";
         break;
      case TypeMYSQLServer:
         // Spelled as the file on disk is spelled. It read "MYSQL" for years and
         // worked, because NTFS does not care; on a case-sensitive filesystem it
         // is a database that cannot be created and an error that names a file
         // the administrator can see is there.
         sFile = "CreateTablesMySQL.sql";
         break;
      case TypePGServer:
         sFile = "CreateTablesPGSQL.sql";
         break;

      }

      // Joined with the separator this platform uses. A backslash here is not a
      // separator on POSIX; it is a character in a file name that does not exist.
      String sFullPath = FileUtilities::Combine(sFolder, sFile);

      return sFullPath;

   }

   String 
   DatabaseSettings::GetDatabaseTypeName(HM::DatabaseSettings::SQLDBType type)
   {
      switch (type)
      {
      case TypeMYSQLServer:
         return "MySQL";
      case TypeMSSQLServer:
         return "MSSQL";
      case TypePGServer:
         return "PostgreSQL";
      case TypeMSSQLCompactEdition:
         return "MSSQL Compact";
      default:
         return "Unknown";
      }
   }

}