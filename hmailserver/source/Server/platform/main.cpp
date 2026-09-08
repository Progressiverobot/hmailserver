// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// The POSIX service host.
//
// On Windows the server is an ATL out-of-process COM server that registers
// itself with the Service Control Manager, and hMailServer.cpp is 500 lines of
// registration, COM plumbing and SCM callbacks. None of that has a meaning here:
// systemd starts a process, expects it to keep running, and asks it to stop with
// a signal. So this file is what is left when all of that is taken away - start
// the application, wait, stop it - and it is deliberately short enough to read in
// one go, because everything that could go wrong in it takes the mail server
// with it.
//
// It is not built on Windows and is not in hMailServer.vcxproj.
//
// WHAT THIS DELIBERATELY DOES NOT DO
//   - It does not daemonise. systemd's Type=simple wants the process in the
//     foreground; forking would hide the exit code and confuse the supervisor.
//   - It does not write a pid file. systemd tracks the process itself.
//   - It does not serve COM. There is none here: administration on this platform
//     is the REST API and the configuration file, which is what the roadmap's
//     "Administration without COM" row is about.
#include "StdAfx.h"

#include <csignal>
#include <termios.h>
#include <unistd.h>
#include <iostream>
#include <algorithm>
#include <cstdio>
#include <cstring>
#include <atomic>
#include <condition_variable>
#include <mutex>

#include "../Common/Application/Application.h"
#include "../Common/Application/Version.h"
#include "../Common/Application/IniFileSettings.h"
#include "../Common/Application/Logger.h"
#include "../Common/Util/Utilities.h"
#include "../Common/Application/Constants.h"
#include "../Common/Util/FileUtilities.h"
#include "../Common/SQL/DatabaseSettings.h"
#include "../Common/SQL/DALConnectionFactory.h"
#include "../Common/SQL/DALConnection.h"
#include "../Common/SQL/DALRecordset.h"
#include "../Common/SQL/SQLStatement.h"
#include "../Common/SQL/SQLCommand.h"
#include "../Common/SQL/SQLScriptRunner.h"
#include "../Common/Util/CrashOracle.h"
#include <dirent.h>

namespace
{
   // Set by the signal handler and read by the main thread. sig_atomic_t is the
   // only type a handler may touch, and the condition variable it is paired with
   // is notified from the main loop's own wake-up rather than from the handler:
   // pthread_cond_signal is not async-signal-safe, and a mail server that
   // deadlocks in its own shutdown is worse than one that takes a second longer.
   volatile sig_atomic_t stop_requested = 0;
   volatile sig_atomic_t reload_requested = 0;

   extern "C" void HandleSignal(int number)
   {
      switch (number)
      {
      case SIGTERM:
      case SIGINT:
         stop_requested = 1;
         break;
      case SIGHUP:
         reload_requested = 1;
         break;
      default:
         break;
      }
   }

   void InstallHandlers()
   {
      struct sigaction action;
      std::memset(&action, 0, sizeof(action));
      action.sa_handler = HandleSignal;
      sigemptyset(&action.sa_mask);
      // No SA_RESTART: a blocking read in a worker should come back with EINTR
      // when we are stopping rather than hold the shutdown open.
      action.sa_flags = 0;

      sigaction(SIGTERM, &action, nullptr);
      sigaction(SIGINT, &action, nullptr);
      sigaction(SIGHUP, &action, nullptr);

      // A peer that closes a socket mid-write must not kill the process. The
      // Windows build never sees this because Winsock reports it as an error
      // code; here the default disposition is death.
      struct sigaction ignore;
      std::memset(&ignore, 0, sizeof(ignore));
      ignore.sa_handler = SIG_IGN;
      sigaction(SIGPIPE, &ignore, nullptr);
   }

   void PrintUsage(const char *program)
   {
      std::printf(
         "hMailServer %s\n"
         "\n"
         "Usage: %s [options]\n"
         "\n"
         "  --config <file>   the configuration file to read\n"
         "                    (default: hMailServer.ini beside this executable,\n"
         "                    then /etc/hmailserver/hMailServer.ini)\n"
         "  --foreground      run in the foreground and log to standard error as\n"
         "                    well as to the log directory. This is the default;\n"
         "                    the option exists so that a unit file can say so.\n"
         "  --check-config    read the configuration, report what it says, and\n"
         "                    exit without opening a listener or the database\n"
         "  --set-admin-password\n"
         "                    read a new administrator password from standard\n"
         "                    input, hash it with PBKDF2 and write it to the\n"
         "                    configuration file, then exit. This is how the\n"
         "                    password is set where there is no Control Panel.\n"
         "  --create-database create the database [Database] names, on the server it\n"
         "                    names, and run the create script for its type. What\n"
         "                    DBSetupQuick does on Windows; there is no COM here.\n"
         "  --upgrade-database\n"
         "                    run every upgrade script from the database's version\n"
         "                    to this build's, in order. What DBUpdater does on\n"
         "                    Windows. The installer's post-install step runs it.\n"
         "  --version         print the version and exit\n"
         "  --help            print this and exit\n"
         "\n"
         "Signals: SIGTERM and SIGINT stop the server; SIGHUP re-reads the\n"
         "configuration the way the Control Panel's Reinitialize does.\n",
         HMAILSERVER_VERSION, program);
   }
}


namespace
{
   // The two database commands. Both read [Database] from the configuration the
   // way the server does, and both refuse the two backends this platform does
   // not have by name rather than by a connection error.

   const char *DescribeType(HM::DatabaseSettings::SQLDBType type)
   {
      switch (type)
      {
      case HM::DatabaseSettings::TypeMYSQLServer:            return "MySQL";
      case HM::DatabaseSettings::TypePGServer:               return "PostgreSQL";
      case HM::DatabaseSettings::TypeMSSQLServer:            return "SQL Server";
      case HM::DatabaseSettings::TypeMSSQLCompactEdition:    return "SQL Server Compact";
      default:                                               return "unknown";
      }
   }

   // The suffix the scripts carry for each backend: Upgrade6030to6031PGSQL.sql.
   const char *ScriptSuffix(HM::DatabaseSettings::SQLDBType type)
   {
      switch (type)
      {
      case HM::DatabaseSettings::TypeMYSQLServer: return "MySQL";
      case HM::DatabaseSettings::TypePGServer:    return "PGSQL";
      default:                                    return "";
      }
   }

   bool ReadDatabaseSettings(std::shared_ptr<HM::DatabaseSettings> &withDatabase,
                             std::shared_ptr<HM::DatabaseSettings> &serverOnly)
   {
      HM::IniFileSettings *ini = HM::IniFileSettings::Instance();
      ini->LoadSettings();

      const HM::DatabaseSettings::SQLDBType type = ini->GetDatabaseType();
      const HM::String name = ini->GetDatabaseName();

      if (type == HM::DatabaseSettings::TypeUnknown || name.IsEmpty())
      {
         std::fprintf(stderr,
            "[Database] in %s does not name a Type and a Database. Set Type=PostgreSQL or\n"
            "Type=MySQL, the Server, Port, Username, Password and Database, and try again.\n",
            HM::AnsiString(HM::IniFileSettings::GetInitializationFile()).c_str());
         return false;
      }

      if (type == HM::DatabaseSettings::TypeMSSQLServer || type == HM::DatabaseSettings::TypeMSSQLCompactEdition)
      {
         std::fprintf(stderr,
            "[Database] Type is %s, which this build does not have: ADO and SQL Server Compact are\n"
            "Windows. PostgreSQL and MySQL are the backends here.\n", DescribeType(type));
         return false;
      }

      const HM::String empty;
      withDatabase = std::make_shared<HM::DatabaseSettings>(
         ini->GetDatabaseProvider(), ini->GetDatabaseServer(), name, ini->GetUsername(), ini->GetPassword(),
         empty, empty, type, ini->GetDatabasePort());
      serverOnly = std::make_shared<HM::DatabaseSettings>(
         ini->GetDatabaseProvider(), ini->GetDatabaseServer(), empty, ini->GetUsername(), ini->GetPassword(),
         empty, empty, type, ini->GetDatabasePort());
      return true;
   }

   std::shared_ptr<HM::DALConnection> Connect(std::shared_ptr<HM::DatabaseSettings> settings, const char *what, bool quietly = false)
   {
      std::shared_ptr<HM::DALConnection> connection = HM::DALConnectionFactory::CreateConnection(settings);
      HM::String error;
      if (!connection || connection->Connect(error) != HM::DALConnection::Connected)
      {
         if (!quietly)
            std::fprintf(stderr, "Could not connect to %s: %s\n", what, HM::AnsiString(error).c_str());
         return std::shared_ptr<HM::DALConnection>();
      }
      return connection;
   }

   int ReadSchemaVersion(std::shared_ptr<HM::DALConnection> connection)
   {
      std::shared_ptr<HM::DALRecordset> recordset = connection->CreateRecordset();
      if (!recordset || !recordset->Open(connection, HM::SQLCommand("select * from hm_dbversion")))
         return 0;
      if (recordset->IsEOF())
         return 0;
      return (int) recordset->GetLongValue("value");
   }

   int CreateDatabase()
   {
      std::shared_ptr<HM::DatabaseSettings> withDatabase, serverOnly;
      if (!ReadDatabaseSettings(withDatabase, serverOnly))
         return 2;

      // Same sequence as the COM CreateExternalDatabase: connect to the server
      // with no database named, CREATE DATABASE in the backend's own dialect,
      // reconnect to the new database, run the create script - which produces
      // the current schema outright, so a fresh database needs no upgrade.
      //
      // Unless the database is already there. An administrator who ran createdb
      // themselves - the usual shape when the service role is not allowed to
      // create databases - has an empty database that this should fill rather
      // than a CREATE DATABASE failure to read; and one that already holds an
      // hMailServer schema must not have the create script run over it, which
      // is a refusal with the number and the command that applies.
      HM::String error;
      std::shared_ptr<HM::DALConnection> database = Connect(withDatabase, "the database", true);
      if (database)
      {
         const int existing = ReadSchemaVersion(database);
         if (existing > 0)
         {
            std::fprintf(stderr, "Database %s already exists and is an hMailServer database at schema version %d. Nothing was changed; --upgrade-database is the command that brings it forward.\n",
               HM::AnsiString(withDatabase->GetDatabaseName()).c_str(), existing);
            return 1;
         }
         std::printf("Database %s already exists and holds no hMailServer schema; creating the schema in it.\n",
            HM::AnsiString(withDatabase->GetDatabaseName()).c_str());
      }
      else
      {
         std::shared_ptr<HM::DALConnection> server = Connect(serverOnly, "the database server");
         if (!server)
            return 1;

         const HM::String create = HM::SQLStatement::GetCreateDatabase(serverOnly, withDatabase->GetDatabaseName());
         if (!server->Execute(HM::SQLCommand(create), error, 0, 0))
         {
            std::fprintf(stderr, "CREATE DATABASE failed: %s\n", HM::AnsiString(error).c_str());
            return 1;
         }

         database = Connect(withDatabase, "the new database");
         if (!database)
            return 1;
      }

      const HM::String script = withDatabase->GetDefaultScript();
      if (!HM::FileUtilities::Exists(script))
      {
         std::fprintf(stderr, "The create script is not where the configuration says: %s\n"
            "ProgramFolder in [Directories] should be the directory that holds DBScripts.\n",
            HM::AnsiString(script).c_str());
         return 1;
      }

      HM::SQLScriptRunner runner;
      if (!runner.ExecuteScript(database, script, error))
      {
         std::fprintf(stderr, "The create script failed: %s\n", HM::AnsiString(error).c_str());
         return 1;
      }

      std::printf("Database %s created on %s at schema version %d, from %s.\n",
         HM::AnsiString(withDatabase->GetDatabaseName()).c_str(), DescribeType(withDatabase->GetType()),
         ReadSchemaVersion(database), HM::AnsiString(script).c_str());
      return 0;
   }

   // The next script in the chain: Upgrade<from>to<something><suffix>.sql. The
   // directory is scanned rather than the number guessed, because the steps are
   // not consecutive - the chain goes 5001, 5002, ... and jumps where a release
   // skipped a number - and DBUpdater builds its path the same way.
   bool FindUpgradeScript(const HM::String &directory, int from, const char *suffix, HM::String &file, int &to)
   {
      const HM::AnsiString narrowDirectory(directory);
      DIR *entries = ::opendir(narrowDirectory.c_str());
      if (!entries)
         return false;

      char prefix[64];
      std::snprintf(prefix, sizeof(prefix), "Upgrade%dto", from);
      const std::string wantedSuffix = std::string(suffix) + ".sql";

      bool found = false;
      struct dirent *entry;
      while ((entry = ::readdir(entries)) != nullptr)
      {
         const std::string name = entry->d_name;
         if (name.compare(0, std::strlen(prefix), prefix) != 0)
            continue;
         if (name.size() <= std::strlen(prefix) + wantedSuffix.size())
            continue;
         if (name.compare(name.size() - wantedSuffix.size(), wantedSuffix.size(), wantedSuffix) != 0)
            continue;
         const std::string middle = name.substr(std::strlen(prefix), name.size() - std::strlen(prefix) - wantedSuffix.size());
         if (middle.empty() || middle.find_first_not_of("0123456789") != std::string::npos)
            continue;
         to = std::atoi(middle.c_str());
         file = directory + "/" + HM::String(name.c_str());
         found = true;
         break;
      }
      ::closedir(entries);
      return found;
   }

   int UpgradeDatabase()
   {
      std::shared_ptr<HM::DatabaseSettings> withDatabase, serverOnly;
      if (!ReadDatabaseSettings(withDatabase, serverOnly))
         return 2;

      std::shared_ptr<HM::DALConnection> database = Connect(withDatabase, "the database");
      if (!database)
         return 1;

      int current = ReadSchemaVersion(database);
      if (current == 0)
      {
         std::fprintf(stderr, "The database has no hm_dbversion row, so it is not an hMailServer database or was never created. --create-database makes one.\n");
         return 1;
      }

      const int required = REQUIRED_DB_VERSION;
      if (current == required)
      {
         std::printf("The database is at schema version %d, which is what this build needs. Nothing to do.\n", current);
         return 0;
      }
      if (current > required)
      {
         std::fprintf(stderr, "The database is at schema version %d and this build needs %d: it was created by a NEWER hMailServer. This build will not run against it.\n", current, required);
         return 1;
      }

      const HM::String directory = HM::IniFileSettings::Instance()->GetDBScriptDirectory();
      const char *suffix = ScriptSuffix(withDatabase->GetType());
      HM::SQLScriptRunner runner;
      int steps = 0;

      while (current < required)
      {
         HM::String script;
         int next = 0;
         if (!FindUpgradeScript(directory, current, suffix, script, next))
         {
            std::fprintf(stderr, "No upgrade script leads from schema version %d in %s. The chain is broken, or ProgramFolder does not point at the DBScripts this build shipped with.\n",
               current, HM::AnsiString(directory).c_str());
            return 1;
         }

         HM::String error;
         std::printf("%d -> %d: %s\n", current, next, HM::AnsiString(script).c_str());
         if (!runner.ExecuteScript(database, script, error))
         {
            std::fprintf(stderr, "The upgrade from %d to %d failed: %s\nThe database is at %d; the scripts before this one have been applied.\n",
               current, next, HM::AnsiString(error).c_str(), ReadSchemaVersion(database));
            return 1;
         }

         const int after = ReadSchemaVersion(database);
         if (after <= current)
         {
            std::fprintf(stderr, "The script for %d -> %d ran but the version row still says %d. Stopping rather than looping.\n", current, next, after);
            return 1;
         }
         current = after;
         steps++;
      }

      std::printf("Upgraded in %d step%s; the database is at schema version %d.\n", steps, steps == 1 ? "" : "s", current);
      return 0;
   }
}

int main(int argc, char *argv[])
{
   std::string configuration;
   bool check_only = false;
   bool set_password = false;
   bool create_database = false;
   bool upgrade_database = false;

   for (int index = 1; index < argc; index++)
   {
      const std::string argument = argv[index];
      if (argument == "--help" || argument == "-h")
      {
         PrintUsage(argv[0]);
         return 0;
      }
      if (argument == "--version")
      {
         std::printf("hMailServer %s build %d\n", HMAILSERVER_VERSION, HMAILSERVER_BUILD);
         return 0;
      }
      if (argument == "--foreground")
         continue;
      if (argument == "--check-config")
      {
         check_only = true;
         continue;
      }
      if (argument == "--set-admin-password")
      {
         set_password = true;
         continue;
      }
      if (argument == "--create-database")
      {
         create_database = true;
         continue;
      }
      if (argument == "--upgrade-database")
      {
         upgrade_database = true;
         continue;
      }
      if (argument == "--config")
      {
         if (index + 1 >= argc)
         {
            std::fprintf(stderr, "--config needs a file name.\n");
            return 2;
         }
         configuration = argv[++index];
         continue;
      }
      std::fprintf(stderr, "Unknown option: %s\nTry --help.\n", argument.c_str());
      return 2;
   }

   InstallHandlers();

   if (!configuration.empty())
      HM::IniFileSettings::Instance()->SetInitializationFile(HM::String(configuration.c_str()));

   HM::String error;

   if (set_password)
   {
      // The administrator password is the credential the REST API and the
      // Control Panel authenticate against, and until this existed there was no
      // way to set it on a machine with no COM: a packaged installation would
      // have had to carry one in plain text, which is exactly what the packaged
      // configuration's own comment tells an administrator not to do. The hash
      // written here is the same PBKDF2 the Control Panel writes, produced by
      // the same code.
      //
      // The password is read from standard input rather than from an argument,
      // so it is not in the process list or in the shell history, and a
      // terminal's echo is turned off while it is typed.
      HM::IniFileSettings::Instance()->LoadSettings();

      const bool interactive = ::isatty(STDIN_FILENO) != 0;
      struct termios original;
      bool echo_disabled = false;
      if (interactive)
      {
         std::printf("New administrator password: ");
         std::fflush(stdout);
         if (::tcgetattr(STDIN_FILENO, &original) == 0)
         {
            struct termios quiet = original;
            quiet.c_lflag &= ~(tcflag_t) ECHO;
            echo_disabled = ::tcsetattr(STDIN_FILENO, TCSAFLUSH, &quiet) == 0;
         }
      }

      std::string password;
      std::getline(std::cin, password);

      if (echo_disabled)
         ::tcsetattr(STDIN_FILENO, TCSAFLUSH, &original);
      if (interactive)
         std::printf("\n");

      while (!password.empty() && (password.back() == '\r' || password.back() == '\n'))
         password.pop_back();

      if (password.empty())
      {
         std::fprintf(stderr, "No password was given; nothing was changed.\n");
         return 2;
      }

      HM::IniFileSettings::Instance()->SetAdministratorPassword(HM::String(password.c_str()));

      // Overwrite this process's copy. It costs nothing, and the alternative is
      // a password sitting in a heap block until the process exits.
      std::fill(password.begin(), password.end(), '\0');

      std::printf("The administrator password was written to %s as a PBKDF2 hash.\n",
         HM::AnsiString(HM::IniFileSettings::Instance()->GetInitializationFile()).c_str());
      return 0;
   }

   if (create_database)
      return CreateDatabase();

   if (upgrade_database)
      return UpgradeDatabase();

   if (check_only)
   {
      // Reads the file and reports what it found, without opening the database
      // or a listener. This is what a package's post-install step runs, and what
      // an administrator runs after editing the file, so it must never be the
      // thing that starts a half-configured server.
      HM::IniFileSettings::Instance()->LoadSettings();
      std::printf("Configuration file: %s\n",
         HM::AnsiString(HM::IniFileSettings::Instance()->GetInitializationFile()).c_str());
      std::printf("Program directory:  %s\n",
         HM::AnsiString(HM::IniFileSettings::Instance()->GetProgramDirectory()).c_str());
      std::printf("Data directory:     %s\n",
         HM::AnsiString(HM::IniFileSettings::Instance()->GetDataDirectory()).c_str());
      std::printf("Log directory:      %s\n",
         HM::AnsiString(HM::IniFileSettings::Instance()->GetLogDirectory()).c_str());
      std::printf("Database type:      %d\n",
         (int) HM::IniFileSettings::Instance()->GetDatabaseType());
      return 0;
   }

   // The crash oracle first, before the application touches the heap in anger:
   // a fault during start-up is exactly the kind that otherwise disappears. It
   // installs signal handlers for the memory-safety signals and SIGABRT, and
   // says where its records go once the log is up (below).
   HM::CrashOracle::Install();

   if (!HM::Application::Instance()->InitInstance(error))
   {
      // Nothing is listening and nothing has been accepted, so this is the one
      // place a failure can be reported plainly and exited on. It goes to
      // standard error as well as the log because at this point the log
      // directory may be exactly what is wrong.
      std::fprintf(stderr, "hMailServer could not start: %s\n", HM::AnsiString(error).c_str());
      return 1;
   }

   if (!HM::Application::Instance()->StartServers())
   {
      std::fprintf(stderr, "hMailServer started but could not open its listeners; see the error log.\n");
      HM::Application::Instance()->ExitInstance();
      return 1;
   }

   HM::CrashOracle::LogInstallationStatus();
   HM::Logger::Instance()->LogApplication(HM::String(_T("hMailServer ")) + HMAILSERVER_VERSION + _T(" is running. Send SIGTERM to stop it."));

   // The wait loop. The application's work happens on its own threads; this
   // thread exists to hold the process open and to notice a signal. A second of
   // latency on a stop is not worth a self-pipe here: systemd's default
   // TimeoutStopSec is 90 seconds and the drain below is what actually takes the
   // time.
   while (!stop_requested)
   {
      if (reload_requested)
      {
         reload_requested = 0;
         HM::Logger::Instance()->LogApplication(_T("SIGHUP: re-reading the configuration."));
         const HM::String result = HM::Application::Instance()->Reinitialize();
         if (!result.IsEmpty())
            HM::Logger::Instance()->LogApplication(HM::String(_T("SIGHUP: the configuration was not re-read: ")) + result);
      }

      struct timespec pause;
      pause.tv_sec = 1;
      pause.tv_nsec = 0;
      nanosleep(&pause, nullptr);
   }

   HM::Logger::Instance()->LogApplication(_T("Stopping: a signal was received."));

   // The same order the Windows service uses, and for the same reason: stop
   // accepting first so nothing new arrives, then let the application finish what
   // it holds. ShutdownDrainSeconds decides how long that is allowed to take.
   // A fault from here on is recorded as one during shutdown, which is a
   // different finding from one in service.
   HM::CrashOracle::NotifyShutdownStarted();
   HM::Application::Instance()->StopServers();
   HM::Application::Instance()->ExitInstance();

   HM::Logger::Instance()->LogApplication(_T("Stopped."));
   return 0;
}
