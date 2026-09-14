// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
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
//   1. end whatever is running from under the installation - a Control Panel
//      left open - because the installer replaces those files and, silent, aborts
//      at the first one it cannot; then run the installer silently and wait for it;
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
#include <tlhelp32.h>

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
      std::wstring app;
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
         else if (name == L"--app") arguments.app = value;
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

namespace
{
   // The installation, {app}: what --app names, or else the directory above the
   // service's own binary (...\Bin\hMailServer.exe), read from the service.
   std::wstring InstallationRoot(const Arguments &arguments)
   {
      if (!arguments.app.empty())
         return arguments.app;
      std::wstring root;
      SC_HANDLE manager = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT);
      if (!manager)
         return root;
      SC_HANDLE handle = OpenServiceW(manager, arguments.service.c_str(), SERVICE_QUERY_CONFIG);
      if (handle)
      {
         DWORD needed = 0;
         QueryServiceConfigW(handle, nullptr, 0, &needed);
         if (needed)
         {
            std::vector<BYTE> buffer(needed);
            QUERY_SERVICE_CONFIGW *config = (QUERY_SERVICE_CONFIGW *) buffer.data();
            if (QueryServiceConfigW(handle, config, needed, &needed) && config->lpBinaryPathName)
            {
               // "C:\...\Bin\hMailServer.exe" RunAsService: the quoted path, or the
               // first word when it is not quoted.
               std::wstring binary = config->lpBinaryPathName;
               if (!binary.empty() && binary[0] == L'"')
               {
                  size_t close = binary.find(L'"', 1);
                  binary = close == std::wstring::npos ? binary.substr(1) : binary.substr(1, close - 1);
               }
               else
               {
                  size_t space = binary.find(L' ');
                  if (space != std::wstring::npos)
                     binary = binary.substr(0, space);
               }
               root = Directory(Directory(binary));
            }
         }
         CloseServiceHandle(handle);
      }
      CloseServiceHandle(manager);
      return root;
   }

   DWORD ServiceProcessId(const std::wstring &service)
   {
      DWORD processId = 0;
      SC_HANDLE manager = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT);
      if (!manager)
         return 0;
      SC_HANDLE handle = OpenServiceW(manager, service.c_str(), SERVICE_QUERY_STATUS);
      if (handle)
      {
         SERVICE_STATUS_PROCESS status;
         DWORD needed = 0;
         if (QueryServiceStatusEx(handle, SC_STATUS_PROCESS_INFO, (LPBYTE) &status, sizeof(status), &needed))
            processId = status.dwProcessId;
         CloseServiceHandle(handle);
      }
      CloseServiceHandle(manager);
      return processId;
   }

   bool Under(const std::wstring &path, const std::wstring &root)
   {
      if (root.empty() || path.size() <= root.size())
         return false;
      if (_wcsnicmp(path.c_str(), root.c_str(), root.size()) != 0)
         return false;
      wchar_t next = path[root.size()];
      return next == L'\\' || next == L'/';
   }

   // Every program running from under the installation is ended before the
   // installer runs - the Control Panel above all. The installer replaces those
   // files and, silent, aborts at the first one it cannot replace: on 14 September
   // 2026 a Control Panel left open in the operator's session, which the Restart
   // Manager called from session 0 could not close, failed an install and its
   // rollback alike with exit code 5. The service itself is left to the installer,
   // which stops it in order, with its drain period; this program, which runs from
   // the data directory under the installation, is itself.
   int EndProcessesUnder(const std::wstring &root, const std::wstring &service)
   {
      if (root.empty())
         return 0;
      const DWORD self = GetCurrentProcessId();
      const DWORD serviceProcess = ServiceProcessId(service);
      HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
      if (snapshot == INVALID_HANDLE_VALUE)
         return 0;
      int ended = 0;
      PROCESSENTRY32W entry;
      entry.dwSize = sizeof(entry);
      if (Process32FirstW(snapshot, &entry))
      {
         do
         {
            if (entry.th32ProcessID == 0 || entry.th32ProcessID == self || entry.th32ProcessID == serviceProcess)
               continue;
            if (_wcsicmp(entry.szExeFile, L"hMailServer.exe") == 0)
               continue;
            HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE | SYNCHRONIZE, FALSE, entry.th32ProcessID);
            if (!process)
               continue;
            wchar_t image[2 * MAX_PATH];
            DWORD length = 2 * MAX_PATH;
            if (QueryFullProcessImageNameW(process, 0, image, &length) && Under(image, root))
            {
               const std::wstring name = entry.szExeFile;
               const std::wstring id = std::to_wstring(entry.th32ProcessID);
               if (TerminateProcess(process, 1))
               {
                  WaitForSingleObject(process, 10000);
                  ended++;
                  Log(L"Ended " + name + L" (process " + id + L"), running from " + image +
                      L": the installer replaces those files, and cannot while they are in use.");
               }
               else
               {
                  Log(L"Could not end " + name + L" (process " + id + L"), running from " + image +
                      L": Windows error " + std::to_wstring(GetLastError()) + L"; the installer may fail on its files.");
               }
            }
            CloseHandle(process);
         } while (Process32NextW(snapshot, &entry));
      }
      CloseHandle(snapshot);
      return ended;
   }

   std::wstring Decoded(const std::vector<char> &bytes)
   {
      if (bytes.size() >= 2 && (BYTE) bytes[0] == 0xFF && (BYTE) bytes[1] == 0xFE)
         return std::wstring((const wchar_t *) (bytes.data() + 2), (bytes.size() - 2) / 2);
      size_t skip = bytes.size() >= 3 && (BYTE) bytes[0] == 0xEF && (BYTE) bytes[1] == 0xBB && (BYTE) bytes[2] == 0xBF ? 3 : 0;
      const char *text = bytes.data() + skip;
      const int size = (int) (bytes.size() - skip);
      if (size <= 0)
         return L"";
      UINT page = CP_UTF8;
      int needed = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text, size, nullptr, 0);
      if (!needed)
      {
         page = CP_ACP;
         needed = MultiByteToWideChar(page, 0, text, size, nullptr, 0);
      }
      std::wstring out((size_t) needed, L'\0');
      if (needed)
         MultiByteToWideChar(page, 0, text, size, &out[0], needed);
      return out;
   }

   // What the installer's own log says went wrong, for the outcome, so that the
   // status page says why rather than only where to look: the last few lines that
   // name an error, and the file they were about. Inno Setup writes the log with
   // a timestamp on each line, in UTF-8, or in the system code page from older
   // versions; both are read.
   std::wstring InstallerLogSays(const std::wstring &path)
   {
      HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
      if (file == INVALID_HANDLE_VALUE)
         return L"";
      LARGE_INTEGER size;
      size.QuadPart = 0;
      GetFileSizeEx(file, &size);
      const LONGLONG most = 512 * 1024;
      if (size.QuadPart > most)
      {
         LARGE_INTEGER from;
         from.QuadPart = size.QuadPart - most;
         SetFilePointerEx(file, from, nullptr, FILE_BEGIN);
         size.QuadPart = most;
      }
      std::vector<char> bytes((size_t) size.QuadPart);
      DWORD readCount = 0;
      if (!bytes.empty())
         ReadFile(file, bytes.data(), (DWORD) bytes.size(), &readCount, nullptr);
      CloseHandle(file);
      bytes.resize(readCount);
      const std::wstring text = Decoded(bytes);
      std::vector<std::wstring> said;
      std::wstring about;
      std::wstring subject;
      size_t start = 0;
      while (start < text.size())
      {
         size_t end = text.find(L'\n', start);
         if (end == std::wstring::npos)
            end = text.size();
         std::wstring line = text.substr(start, end - start);
         start = end + 1;
         while (!line.empty() && (line.back() == L'\r' || line.back() == L' '))
            line.pop_back();
         // "2026-09-14 01:38:20.123   text": the stamp goes.
         if (line.size() > 24 && iswdigit(line[0]) && line[4] == L'-' && line[10] == L' ')
            line = line.substr(23);
         while (!line.empty() && line.front() == L' ')
            line.erase(line.begin());
         if (line.empty())
            continue;
         std::wstring lower = line;
         for (size_t i = 0; i < lower.size(); i++)
            lower[i] = towlower(lower[i]);
         if (lower.find(L"dest filename:") == 0)
         {
            about = line;
            continue;
         }
         if (lower.find(L"error") == std::wstring::npos && lower.find(L"abort") == std::wstring::npos && lower.find(L"failed") == std::wstring::npos &&
             lower.find(L"cannot") == std::wstring::npos && lower.find(L"in use") == std::wstring::npos && lower.find(L"denied") == std::wstring::npos)
            continue;
         // The file the first error was about is kept apart, so that it is
         // never one of the lines trimmed below.
         if (subject.empty() && !about.empty())
            subject = about;
         said.push_back(line);
      }
      if (said.empty())
         return L"";
      if (said.size() > 4)
         said.erase(said.begin(), said.end() - 4);
      std::wstring out = subject;
      for (size_t i = 0; i < said.size(); i++)
         out += (out.empty() ? L"" : L" | ") + said[i];
      if (out.size() > 600)
         out = out.substr(0, 600) + L"...";
      return L"; the installer's log says: " + out;
   }
}

int wmain(int argc, wchar_t *argv[])
{
   Arguments arguments;
   if (!Parse(argc, argv, arguments))
   {
      wprintf(L"Usage: hMailServer.Updater --installer <path> --version <v> --outcome <path> [--rollback <path> --rollback-version <v>]\n"
              L"                           [--service <name>] [--token <token>] [--log <path>] [--wait <seconds>] [--app <root>]\n");
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
   const std::wstring root = InstallationRoot(arguments);
   if (root.empty())
      Log(L"The installation's directory is not known, so nothing running from it can be ended before the installer runs.");
   else
      Log(L"Installation: " + root + L"; " + std::to_wstring(EndProcessesUnder(root, arguments.service)) + L" program(s) running from it ended before the installer runs.");
   int code = RunInstaller(arguments.installer, arguments.token, installLog);
   const std::wstring says = code == 0 ? L"" : InstallerLogSays(installLog);
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
         L" but the service is running" + says + L"; see " + installLog);
      return 1;
   }

   std::wstring reason = code == 0
      ? L"the installer succeeded but the service was not running after " + std::to_wstring(arguments.wait_seconds) + L" seconds (" + StateName(ServiceState(arguments.service)) + L")"
      : L"the installer exited with " + std::wstring(codeText) + L" and the service was not running (" + StateName(ServiceState(arguments.service)) + L")" + says;

   if (arguments.rollback.empty())
   {
      WriteOutcome(arguments.outcome, L"no-rollback " + arguments.version + L" " + reason + L"; no rollback image was available; see " + installLog);
      return 1;
   }

   Log(L"Rolling back to hMailServer " + arguments.rollback_version + L": " + reason);
   std::wstring rollbackLog = Directory(arguments.outcome) + L"\\rollback-" + arguments.rollback_version + L".log";
   if (!root.empty())
      EndProcessesUnder(root, arguments.service);
   int rollbackCode = RunInstaller(arguments.rollback, L"", rollbackLog);
   const std::wstring rollbackSays = rollbackCode == 0 ? L"" : InstallerLogSays(rollbackLog);
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
      L" exited with " + rollbackText + L" and the service is " + StateName(ServiceState(arguments.service)) + rollbackSays +
      L"; see " + installLog + L" and " + rollbackLog);
   return 1;
}
