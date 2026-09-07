// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "UpdateInstaller.h"
#include "UpdateApplyToken.h"
#include "UpdateChecker.h"
#include "UpdateDownloader.h"
#include "SigstoreVerifier.h"
#include "FileUtilities.h"
#include "Unicode.h"
#include "../Application/IniFileSettings.h"
#include "../Application/ErrorManager.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      const TCHAR *SERVICE_NAME = _T("hMailServer");
      const TCHAR *HELPER_NAME = _T("hMailServer.Updater.exe");
      const int SERVICE_WAIT_SECONDS = 180;

      String Quote_(const String &text)
      {
         return _T("\"") + text + _T("\"");
      }
   }

   String
   UpdateInstaller::HelperSourcePath()
   {
      wchar_t module[MAX_PATH];
      DWORD length = GetModuleFileNameW(nullptr, module, MAX_PATH);
      if (length == 0 || length >= MAX_PATH)
         return _T("");
      String path = module;
      int slash = path.ReverseFind('\\');
      if (slash < 0)
         return _T("");
      return path.Left(slash + 1) + HELPER_NAME;
   }

   String
   UpdateInstaller::OutcomePath()
   {
      String directory = UpdateDownloader::UpdatesDirectory();
      return directory.IsEmpty() ? String() : directory + _T("\\last-apply.txt");
   }

   bool
   UpdateInstaller::EnsureRollbackImage_(const String &runningVersion, String &rollbackPath, String &why)
   {
      rollbackPath.Empty();

      String directory = UpdateDownloader::UpdatesDirectory() + _T("\\rollback");
      if (!FileUtilities::DirectoryExists(directory) && !FileUtilities::CreateDirectory(directory))
      {
         why = Formatter::Format(_T("{0} could not be created."), directory);
         return false;
      }

      String name = _T("hMailServer-") + runningVersion + _T("-x64.exe");
      String path = directory + _T("\\") + name;
      String bundlePath = path + _T(".cosign.bundle");

      SigstoreTrust trust;
      if (!SigstoreVerifier::ConfiguredTrust(trust, why))
         return false;

      // Already fetched by an earlier apply, and still the file it was.
      if (FileUtilities::Exists(path) && FileUtilities::Exists(bundlePath))
      {
         SigstoreVerdict verdict;
         AnsiString bundleJson = AnsiString(Unicode::ToANSI(FileUtilities::ReadCompleteTextFile(bundlePath)));
         if (SigstoreVerifier::VerifyFile(path, bundleJson, trust, verdict))
         {
            rollbackPath = path;
            return true;
         }
         FileUtilities::DeleteFile(path);
         FileUtilities::DeleteFile(bundlePath);
      }

      UpdateChecker::ReleaseAssets assets;
      if (!UpdateChecker::FetchReleaseByTag(runningVersion, assets, why))
         return false;
      if (assets.installer_url.IsEmpty() || assets.bundle_url.IsEmpty())
      {
         why = Formatter::Format(_T("release {0} carries no x64 installer with a Sigstore bundle."), runningVersion);
         return false;
      }

      SigstoreVerdict verdict;
      if (!UpdateDownloader::FetchVerified(assets.installer_url, assets.bundle_url, path, assets.installer_size, assets.installer_digest, trust, verdict, why))
         return false;

      rollbackPath = path;
      return true;
   }

   bool
   UpdateInstaller::Launch_(const String &commandLine, const String &workingDirectory, String &error)
   {
      std::vector<wchar_t> mutableCommand(commandLine.begin(), commandLine.end());
      mutableCommand.push_back(L'\0');

      STARTUPINFOW startup;
      memset(&startup, 0, sizeof(startup));
      startup.cb = sizeof(startup);
      PROCESS_INFORMATION process;
      memset(&process, 0, sizeof(process));

      // Detached from this process in every way Windows offers: its own group, no
      // console, and out of any job the service runs in, so that the service
      // stopping - which is what the installer does first - does not take the
      // helper with it. Breaking away needs the job to allow it; when it does not,
      // the helper is started inside it and the service's own exit is the only risk.
      DWORD flags = CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP | CREATE_BREAKAWAY_FROM_JOB;
      BOOL started = CreateProcessW(nullptr, mutableCommand.data(), nullptr, nullptr, FALSE, flags, nullptr, workingDirectory.c_str(), &startup, &process);
      if (!started && GetLastError() == ERROR_ACCESS_DENIED)
      {
         flags = CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP;
         started = CreateProcessW(nullptr, mutableCommand.data(), nullptr, nullptr, FALSE, flags, nullptr, workingDirectory.c_str(), &startup, &process);
      }
      if (!started)
      {
         error = Formatter::Format(_T("The update helper could not be started: Windows error {0}."), (int) GetLastError());
         return false;
      }

      CloseHandle(process.hThread);
      CloseHandle(process.hProcess);
      return true;
   }

   bool
   UpdateInstaller::Apply(String &error)
   {
      error.Empty();

      UpdateChecker::Snapshot snapshot = UpdateChecker::Current();
      if (snapshot.state != UpdateChecker::StateDownloaded || snapshot.installer_path.IsEmpty() || !FileUtilities::Exists(snapshot.installer_path))
      {
         error = _T("No verified installer is waiting; download one first.");
         return false;
      }

      String helperSource = HelperSourcePath();
      if (helperSource.IsEmpty() || !FileUtilities::Exists(helperSource))
      {
         error = Formatter::Format(_T("The update helper {0} is not beside the server; this installation cannot apply updates."), helperSource);
         return false;
      }

      // Verified once more, now, because the file has been sitting in a directory
      // since it was: what runs is what was checked.
      SigstoreTrust trust;
      String why;
      if (!SigstoreVerifier::ConfiguredTrust(trust, why))
      {
         error = why;
         return false;
      }
      String bundlePath = snapshot.installer_path + _T(".cosign.bundle");
      SigstoreVerdict verdict;
      AnsiString bundleJson = FileUtilities::Exists(bundlePath) ? AnsiString(Unicode::ToANSI(FileUtilities::ReadCompleteTextFile(bundlePath))) : AnsiString();
      if (!SigstoreVerifier::VerifyFile(snapshot.installer_path, bundleJson, trust, verdict))
      {
         FileUtilities::DeleteFile(snapshot.installer_path);
         if (FileUtilities::Exists(bundlePath))
            FileUtilities::DeleteFile(bundlePath);
         why = Formatter::Format(_T("The installer no longer verifies against its bundle and has been deleted: {0}"), verdict.error);
         UpdateChecker::RecordFailure(why);
         LOG_APPLICATION(Formatter::Format(_T("Update apply refused: {0}"), why));
         error = why;
         return false;
      }

      String directory = UpdateDownloader::UpdatesDirectory();
      String running = UpdateChecker::RunningVersion();

      String rollbackPath;
      String rollbackWhy;
      // Braces on both branches: LOG_APPLICATION is itself an if statement.
      if (EnsureRollbackImage_(running, rollbackPath, rollbackWhy))
      {
         LOG_APPLICATION(Formatter::Format(_T("Update: the rollback image for hMailServer {0} is {1}, verified."), running, rollbackPath));
      }
      else
      {
         LOG_APPLICATION(Formatter::Format(_T("Update: no rollback image for hMailServer {0} could be had ({1}); if {2} does not start, it will need a manual reinstall."),
            running, rollbackWhy, snapshot.available_version));
      }

      AnsiString token;
      if (!UpdateApplyToken::Issue(token, why))
      {
         error = why;
         return false;
      }

      String helper = directory + _T("\\") + HELPER_NAME;
      if (FileUtilities::Exists(helper))
         FileUtilities::DeleteFile(helper);
      if (!FileUtilities::Copy(helperSource, helper, false))
      {
         UpdateApplyToken::Revoke();
         error = Formatter::Format(_T("{0} could not be copied to {1}."), helperSource, helper);
         return false;
      }

      String outcome = OutcomePath();
      if (FileUtilities::Exists(outcome))
         FileUtilities::DeleteFile(outcome);

      String command = Quote_(helper);
      command += _T(" --installer ") + Quote_(snapshot.installer_path);
      command += _T(" --version ") + snapshot.available_version;
      if (!rollbackPath.IsEmpty())
      {
         command += _T(" --rollback ") + Quote_(rollbackPath);
         command += _T(" --rollback-version ") + running;
      }
      command += _T(" --service ") + String(SERVICE_NAME);
      command += _T(" --token ") + String(token);
      command += _T(" --outcome ") + Quote_(outcome);
      command += _T(" --log ") + Quote_(directory + _T("\\apply-") + snapshot.available_version + _T(".log"));
      command += Formatter::Format(_T(" --wait {0}"), IniFileSettings::Instance()->GetUpdateServiceWaitSeconds());

      if (!Launch_(command, directory, why))
      {
         UpdateApplyToken::Revoke();
         UpdateChecker::RecordFailure(why);
         LOG_APPLICATION(Formatter::Format(_T("Update apply failed: {0}"), why));
         error = why;
         return false;
      }

      UpdateChecker::RecordInstalling();
      LOG_APPLICATION(Formatter::Format(_T("Update: hMailServer {0} is being applied by {1}; the service will stop and start, and the outcome is reported when it does."),
         snapshot.available_version, helper));
      return true;
   }

   void
   UpdateInstaller::ReportLastOutcome()
   {
      String path = OutcomePath();
      if (path.IsEmpty() || !FileUtilities::Exists(path))
         return;

      String line = FileUtilities::ReadCompleteTextFile(path);
      int newline = line.Find(_T("\n"));
      if (newline >= 0)
         line = line.Left(newline);
      line.TrimRight();
      line.TrimLeft();

      // "<status> <version> <detail>"
      String status, version, detail;
      int first = line.Find(_T(" "));
      if (first > 0)
      {
         status = line.Left(first);
         String rest = line.Mid(first + 1);
         int second = rest.Find(_T(" "));
         if (second > 0)
         {
            version = rest.Left(second);
            detail = rest.Mid(second + 1);
         }
         else
            version = rest;
      }
      else
         status = line;

      String reported = path + _T(".reported");
      if (FileUtilities::Exists(reported))
         FileUtilities::DeleteFile(reported);
      FileUtilities::Move(path, reported);

      UpdateChecker::RecordApplyOutcome(status, version, detail);

      String message;
      if (status == _T("ok"))
      {
         message = Formatter::Format(_T("Update applied: hMailServer {0} is installed and this is it running; {1}."), version, detail);
         LOG_APPLICATION(message);
         return;
      }

      message = Formatter::Format(_T("Update to hMailServer {0} did not succeed ({1}): {2}"), version, status, detail);
      LOG_APPLICATION(message);
      ErrorManager::Instance()->ReportError(ErrorManager::High, 5480, "UpdateInstaller::ReportLastOutcome", message);
   }
}
