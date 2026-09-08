// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "DALConnectionFactory.h"
// ADO and SQL Server Compact are COM, and the roadmap section "Linux and
// AArch64" - the row "Database backends that survive" - leaves both out of the
// POSIX build, where MySQL and PostgreSQL are the two backends that work. A
// POSIX build asked for either of them says so and hands back nothing, which the
// connection manager turns into a fatal error naming the backend; it must not
// quietly return an empty connection the caller would dereference.
#ifdef _MSC_VER
#include "ADOConnection.h"
#endif
#include "MySQLConnection.h"
#include "PGConnection.h"
#ifdef _MSC_VER
#include "SQLCEConnection.h"
#endif
#include "DatabaseSettings.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{

   DALConnectionFactory::DALConnectionFactory()
   {

   }

   DALConnectionFactory::~DALConnectionFactory()
   {

   }

   std::shared_ptr<DALConnection>
   DALConnectionFactory::CreateConnection(std::shared_ptr<DatabaseSettings> pSettings)
   {
      std::shared_ptr<DALConnection> pConn;
      
      HM::DatabaseSettings::SQLDBType t = pSettings->GetType();

     switch (t)
      {
#ifdef _MSC_VER
      case HM::DatabaseSettings::TypeMSSQLServer:
         pConn = std::shared_ptr<ADOConnection>(new ADOConnection(pSettings));
         break;
#endif
      case HM::DatabaseSettings::TypeMYSQLServer:
         pConn = std::shared_ptr<MySQLConnection>(new MySQLConnection(pSettings));
         break;
      case HM::DatabaseSettings::TypePGServer:
         pConn = std::shared_ptr<PGConnection>(new PGConnection(pSettings));
         break;
#ifdef _MSC_VER
      case HM::DatabaseSettings::TypeMSSQLCompactEdition:
         pConn = std::shared_ptr<SQLCEConnection>(new SQLCEConnection(pSettings));
         break;
#endif
#ifdef HM_PLATFORM_POSIX
      // The two cases the Windows build handles above and this one cannot. Saying
      // so here is the whole point: an administrator who moved a configuration
      // across from Windows without repointing the database has to be told which
      // backend was refused and why, not left with a connection attempt that
      // never happened.
      case HM::DatabaseSettings::TypeMSSQLServer:
      case HM::DatabaseSettings::TypeMSSQLCompactEdition:
         ErrorManager::Instance()->ReportError(ErrorManager::Critical, 6390, "DALConnectionFactory::CreateConnection",
            t == HM::DatabaseSettings::TypeMSSQLServer ?
               "The database type in hMailServer.INI is SQL Server, which this build cannot use. SQL Server is reached through ADO, which exists only on Windows. Use MySQL or PostgreSQL." :
               "The database type in hMailServer.INI is SQL Server Compact, which this build cannot use. SQL Server Compact exists only on Windows. Use MySQL or PostgreSQL.");
         break;
#endif
      }
   
      return pConn;
   }
}