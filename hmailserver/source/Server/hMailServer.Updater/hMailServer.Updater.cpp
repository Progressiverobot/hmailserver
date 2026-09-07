// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

// hMailServer.Updater: the one-shot helper that applies a verified update.
//
// The service cannot run its own installer - the installer stops the service - so
// the server copies this program out of Bin (which the installer replaces) into the
// data directory's Updates folder and starts it there, detached, with the verified
// installer's path, a rollback image where one could be had, and a single-use token
// the new installer's database tool presents to authenticate the schema upgrade.
//
// Then, alone:
//   1. run the installer silently and wait for it;
//   2. wait for the service to be running again, up to --wait seconds;
//   3. if it is not, run the rollback image and wait again;
//   4. write one line to --outcome saying what happened, which the server reads and
//      reports the next time it starts.
//
// No dependencies beyond Win32 and the static C runtime, deliberately: everything
// else on the machine is what is being replaced while this runs.

#include "stdafx.h"

#include <string>
#include <vector>

namespace
{
   struct Arguments
   {
      std::wstring installer;
      std::wstring version;
      std::wstring rollback;
      std::wstring rollback_version;
      std::wstring service;
      std::wstring token;
      std::wstring outcome;
      std::wstring log;
      int wait_seconds;

      Arguments() : service(L"hMailServer"), wait_seconds(180) {}
   };

   std::wstring g_logPath;

   std::wstring Now()
   {
      SYSTEMTIME t;
      GetLocalTime(&t);
      wchar_t buffer[32];
      swprintf_s(buffer, L"%04d-%02d-%02d %02d:%02d:%02d", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond);
      return buffer;
   }

   void Log(const std::wstring &line)
   {
      std::wstring text = Now() + L"  " + line + L"\r\n";
      wprintf(L"%s", text.c_str());
      if (g_logPath.empty())
         return;
      HANDLE file = CreateFileW(g_logPath.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
      if (file == INVALID_HANDLE_VALUE)
         return;
      int needed = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), (int) text.size(), nullptr, 0, nullptr, nullptr);
      std::vector<char> utf8((size_t) needed);
      WideCharToMultiByte(CP_UTF8, 0, text.c_str(), (int) text.size(), utf8.data(), needed, nullptr, nullptr);
      DWORD written = 0;
      WriteFile(file, utf8.data(), (DWORD) utf8.size(), &written, nullptr);
      CloseHandle(file);
   }

   bool Parse(int argc, wchar_t *argv[], Arguments &arguments)
   {
      for (int i = 1; i + 1 < argc; i += 2)
      {
         std::wstring name = argv[i];
         std::wstring value = argv[i + 1];
         if (name == L"--installer") arguments.installer = value;
         else if (name == L"--version") arguments.version = value;
         else if (name == L"--rollback") arguments.rollback = value;
         else if (name == L"--rollback-version") arguments.rollback_version = value;
         else if (name == L"--service") arguments.service = value;
         else if (name == L"--token") arguments.token = value;
         else if (name == L"--outcome") arguments.outcome = value;
         else if (name == L"--log") arguments.log = value;
         else if (name == L"--wait") arguments.wait_seconds = _wtoi(value.c_str());
         else return false;
      }
      return !arguments.installer.empty() && !arguments.outcome.empty();
   }

   DWORD ServiceState(const std::wstring &service)
   {
      SC_HANDLE manager = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT);
      if (!manager)
         return 0;
      DWORD state = 0;
      SC_HANDLE handle = OpenServiceW(manager, service.c_str(), SERVICE_QUERY_STATUS);
      if (handle)
      {
         SERVICE_STATUS_PROCESS status;
         DWORD needed = 0;
         if (QueryServiceStatusEx(handle, SC_STATUS_PROCESS_INFO, (LPBYTE) &status, sizeof(status), &needed))
            state = status.dwCurrentState;
         CloseServiceHandle(handle);
      }
      CloseServiceHandle(manager);
      return state;
   }

   const wchar_t *StateName(DWORD state)
   {
      switch (state)
      {
      case SERVICE_RUNNING: return L"running";
      case SERVICE_STOPPED: return L"stopped";
      case SERVICE_START_PENDING: return L"starting";
      case SERVICE_STOP_PENDING: return L"stopping";
      case SERVICE_PAUSED: return L"paused";
      case 0: return L"not installed";
      default: return L"in transition";
      }
   }

   bool WaitForRunning(const std::wstring &service, int seconds)
   {
      for (int elapsed = 0; elapsed <= seconds; elapsed += 2)
      {
         if (ServiceState(service) == SERVICE_RUNNING)
            return true;
         Sleep(2000);
      }
      return ServiceState(service) == SERVICE_RUNNING;
   }

   // Runs an installer silently and returns its exit code; -1 when it could not be
   // started, -2 when it ran for longer than an hour.
   int RunInstaller(const std::wstring &installer, const std::wstring &token, const std::wstring &installLog)
   {
      std::wstring command = L"\"" + installer + L"\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS";
      if (!token.empty())
         command += L" /upgradetoken=" + token;
      if (!installLog.empty())
         command += L" /LOG=\"" + installLog + L"\"";

      Log(L"Running: " + command);

      std::vector<wchar_t> mutableCommand(command.begin(), command.end());
      mutableCommand.push_back(L'\0');

      STARTUPINFOW startup;
      memset(&startup, 0, sizeof(startup));
      startup.cb = sizeof(startup);
      PROCESS_INFORMATION process;
      memset(&process, 0, sizeof(process));

      if (!CreateProcessW(nullptr, mutableCommand.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &startup, &process))
      {
         wchar_t message[64];
         swprintf_s(message, L"CreateProcess failed with %lu", GetLastError());
         Log(message);
         return -1;
      }

      DWORD waited = WaitForSingleObject(process.hProcess, 60 * 60 * 1000);
      DWORD exitCode = 0;
      if (waited == WAIT_OBJECT_0)
         GetExitCodeProcess(process.hProcess, &exitCode);
      CloseHandle(process.hThread);
      CloseHandle(process.hProcess);

      if (waited != WAIT_OBJECT_0)
      {
         Log(L"The installer did not finish within an hour.");
         return -2;
      }

      wchar_t message[64];
      swprintf_s(message, L"The installer exited with %lu", exitCode);
      Log(message);
      return (int) exitCode;
   }

   void WriteOutcome(const std::wstring &path, const std::wstring &line)
   {
      Log(L"Outcome: " + line);
      HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
      if (file == INVALID_HANDLE_VALUE)
         return;
      int needed = WideCharToMultiByte(CP_UTF8, 0, line.c_str(), (int) line.size(), nullptr, 0, nullptr, nullptr);
      std::vector<char> utf8((size_t) needed);
      WideCharToMultiByte(CP_UTF8, 0, line.c_str(), (int) line.size(), utf8.data(), needed, nullptr, nullptr);
      DWORD written = 0;
      WriteFile(file, utf8.data(), (DWORD) utf8.size(), &written, nullptr);
      CloseHandle(file);
   }

   std::wstring Directory(const std::wstring &path)
   {
      size_t slash = path.find_last_of(L"\\/");
      return slash == std::wstring::npos ? L"." : path.substr(0, slash);
   }
}

int wmain(int argc, wchar_t *argv[])
{
   Arguments arguments;
   if (!Parse(argc, argv, arguments))
   {
      wprintf(L"Usage: hMailServer.Updater --installer <path> --version <v> --outcome <path> [--rollback <path> --rollback-version <v>]\n"
              L"                           [--service <name>] [--token <token>] [--log <path>] [--wait <seconds>]\n");
      return 2;
   }

   g_logPath = arguments.log;
   Log(L"hMailServer.Updater: applying hMailServer " + arguments.version + L" from " + arguments.installer +
       (arguments.rollback.empty() ? L"; no rollback image" : L"; rollback image " + arguments.rollback + L" (" + arguments.rollback_version + L")"));
   Log(L"Service " + arguments.service + L" is " + StateName(ServiceState(arguments.service)) + L" before the installer runs.");

   // The service that started this process is about to be stopped by the installer.
   // A moment for it to finish returning from the call that started us.
   Sleep(2000);

   std::wstring installLog = Directory(arguments.outcome) + L"\\install-" + arguments.version + L".log";
   int code = RunInstaller(arguments.installer, arguments.token, installLog);
   bool running = WaitForRunning(arguments.service, arguments.wait_seconds);

   wchar_t codeText[32];
   swprintf_s(codeText, L"%d", code);

   if (code == 0 && running)
   {
      WriteOutcome(arguments.outcome, L"ok " + arguments.version + L" the installer succeeded and the service is running");
      return 0;
   }

   if (running)
   {
      WriteOutcome(arguments.outcome, L"failed " + arguments.version + L" the installer exited with " + codeText +
         L" but the service is running; see " + installLog);
      return 1;
   }

   std::wstring reason = code == 0
      ? L"the installer succeeded but the service was not running after " + std::to_wstring(arguments.wait_seconds) + L" seconds (" + StateName(ServiceState(arguments.service)) + L")"
      : L"the installer exited with " + std::wstring(codeText) + L" and the service was not running (" + StateName(ServiceState(arguments.service)) + L")";

   if (arguments.rollback.empty())
   {
      WriteOutcome(arguments.outcome, L"no-rollback " + arguments.version + L" " + reason + L"; no rollback image was available; see " + installLog);
      return 1;
   }

   Log(L"Rolling back to hMailServer " + arguments.rollback_version + L": " + reason);
   std::wstring rollbackLog = Directory(arguments.outcome) + L"\\rollback-" + arguments.rollback_version + L".log";
   int rollbackCode = RunInstaller(arguments.rollback, L"", rollbackLog);
   bool rolledBack = WaitForRunning(arguments.service, arguments.wait_seconds);

   wchar_t rollbackText[32];
   swprintf_s(rollbackText, L"%d", rollbackCode);

   if (rolledBack)
   {
      WriteOutcome(arguments.outcome, L"rolled-back " + arguments.version + L" " + reason + L"; hMailServer " + arguments.rollback_version +
         L" was reinstalled and the service is running; see " + installLog);
      return 1;
   }

   WriteOutcome(arguments.outcome, L"rollback-failed " + arguments.version + L" " + reason + L"; the rollback to " + arguments.rollback_version +
      L" exited with " + rollbackText + L" and the service is " + StateName(ServiceState(arguments.service)) +
      L"; see " + installLog + L" and " + rollbackLog);
   return 1;
}
