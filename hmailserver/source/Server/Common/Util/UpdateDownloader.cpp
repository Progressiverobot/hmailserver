// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "UpdateDownloader.h"
#include "UpdateChecker.h"
#include "SigstoreVerifier.h"
#include "HttpsClient.h"
#include "FileUtilities.h"
#include "Unicode.h"
#include "../Application/IniFileSettings.h"

// Authenticode is a Windows code-signing scheme, and WinVerifyTrust is the only
// implementation of it. See AuthenticodeTrusted below for what the POSIX build
// does instead; everything else in this file - the download, the digest and the
// Sigstore verification - is platform-neutral.
#ifdef _MSC_VER
#include <wintrust.h>
#include <softpub.h>
#pragma comment(lib, "wintrust.lib")
#endif

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // An installer is seventy-odd megabytes; a bundle, eleven kilobytes.
      const size_t MAX_INSTALLER_BYTES = 1024u * 1024u * 1024u;
      const size_t MAX_BUNDLE_BYTES = 1024u * 1024u;

      void Remove_(const String &path)
      {
         if (!path.IsEmpty() && FileUtilities::Exists(path))
            FileUtilities::DeleteFile(path);
      }
   }

   String
   UpdateDownloader::UpdatesDirectory()
   {
      String directory = IniFileSettings::Instance()->GetDataDirectory();
      if (directory.IsEmpty())
         return _T("");
      if (directory.Right(1) != FileUtilities::PathSeparator)
         directory += FileUtilities::PathSeparator;
      return directory + _T("Updates");
   }

   bool
   UpdateDownloader::AuthenticodeTrusted(const String &path, String &why)
   {
#ifndef _MSC_VER
      // There is no Authenticode outside Windows, so there is nothing here to ask.
      // Refusing is the only safe answer: an administrator who set
      // UpdateRequireAuthenticode=1 asked for the signature to be checked, and a
      // build that cannot check it must not report that it passed. The roadmap
      // section "Linux and AArch64" leaves updating a Linux install to its package
      // manager rather than to a signed Windows installer.
      why = _T("Authenticode signatures can only be checked on Windows");
      return false;
#else
      WINTRUST_FILE_INFO fileInfo;
      memset(&fileInfo, 0, sizeof(fileInfo));
      fileInfo.cbStruct = sizeof(fileInfo);
      fileInfo.pcwszFilePath = path.c_str();

      WINTRUST_DATA data;
      memset(&data, 0, sizeof(data));
      data.cbStruct = sizeof(data);
      data.dwUIChoice = WTD_UI_NONE;
      data.fdwRevocationChecks = WTD_REVOKE_NONE;
      data.dwUnionChoice = WTD_CHOICE_FILE;
      data.pFile = &fileInfo;
      data.dwStateAction = WTD_STATEACTION_VERIFY;
      data.dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL;

      GUID policy = WINTRUST_ACTION_GENERIC_VERIFY_V2;
      LONG status = WinVerifyTrust((HWND) INVALID_HANDLE_VALUE, &policy, &data);

      data.dwStateAction = WTD_STATEACTION_CLOSE;
      WinVerifyTrust((HWND) INVALID_HANDLE_VALUE, &policy, &data);

      if (status == ERROR_SUCCESS)
         return true;

      switch (status)
      {
      case TRUST_E_NOSIGNATURE:
         why = _T("the file carries no Authenticode signature");
         break;
      case TRUST_E_SUBJECT_FORM_UNKNOWN:
         why = _T("the file is not a signed Windows binary");
         break;
      case TRUST_E_EXPLICIT_DISTRUST:
         why = _T("the signer is explicitly distrusted on this machine");
         break;
      case TRUST_E_SUBJECT_NOT_TRUSTED:
         why = _T("the signer is not trusted on this machine");
         break;
      case CERT_E_UNTRUSTEDROOT:
         why = _T("the signing certificate chains to a root this machine does not trust");
         break;
      case TRUST_E_BAD_DIGEST:
         why = _T("the file has been modified since it was signed");
         break;
      default:
         why.Format(_T("WinVerifyTrust answered 0x%08X"), (unsigned) status);
         break;
      }
      return false;
#endif
   }

   bool
   UpdateDownloader::FetchVerified(const String &installerUrl, const String &bundleUrl, const String &installerPath, __int64 expectedSize,
                                   const String &expectedDigest, const SigstoreTrust &trust, SigstoreVerdict &verdict, String &why)
   {
      String bundlePath = installerPath + _T(".cosign.bundle");
      String partialPath = installerPath + _T(".partial");
      bool ok = false;
      int status = 0;

      // The bundle first: it is small, and without it the installer is not worth
      // the bandwidth.
      if (!HttpsClient::Download(AnsiString(bundleUrl), bundlePath, MAX_BUNDLE_BYTES, status, why))
         why = Formatter::Format(_T("The Sigstore bundle could not be fetched: {0}"), why);
      else if (!HttpsClient::Download(AnsiString(installerUrl), partialPath, MAX_INSTALLER_BYTES, status, why))
         why = Formatter::Format(_T("The installer could not be fetched: {0}"), why);
      else
      {
         unsigned __int64 size = 0;
         FileUtilities::FileSize64(partialPath, size);
         if (expectedSize > 0 && (__int64) size != expectedSize)
            why = Formatter::Format(_T("The feed says the installer is {0} bytes; the download is {1}."), expectedSize, (__int64) size);
         else
         {
            // The feed's digest is a cheap consistency check, not the proof; the
            // proof is the bundle. A disagreement between the two means one of them
            // is not what it claims, and that is enough to stop.
            std::vector<unsigned char> digest;
            String hashError;
            if (!SigstoreVerifier::Sha256File(partialPath, digest, hashError))
               why = hashError;
            else
            {
               AnsiString stated = AnsiString(expectedDigest);
               stated.MakeLower();
               if (stated.StartsWith("sha256:") && std::string(stated.c_str() + 7) != SigstoreVerifier::Hex(digest))
                  why = Formatter::Format(_T("The feed says the installer's SHA-256 is {0}; the download's is {1}."),
                     String(stated.c_str() + 7), String(SigstoreVerifier::Hex(digest).c_str()));
               else
               {
                  AnsiString bundleJson = AnsiString(Unicode::ToANSI(FileUtilities::ReadCompleteTextFile(bundlePath)));
                  if (!SigstoreVerifier::VerifyFile(partialPath, bundleJson, trust, verdict))
                     why = Formatter::Format(_T("The installer did not verify against its Sigstore bundle: {0}"), verdict.error);
                  else if (IniFileSettings::Instance()->GetUpdateRequireAuthenticode() && !AuthenticodeTrusted(partialPath, why))
                     why = Formatter::Format(_T("The installer's Authenticode signature was refused: {0}."), why);
                  else
                  {
                     Remove_(installerPath);
                     if (!FileUtilities::Move(partialPath, installerPath))
                        why = Formatter::Format(_T("{0} could not be moved into place."), partialPath);
                     else
                        ok = true;
                  }
               }
            }
         }
      }

      if (!ok)
      {
         Remove_(partialPath);
         Remove_(bundlePath);
      }
      return ok;
   }

   bool
   UpdateDownloader::DownloadAndVerify(String &error)
   {
      error.Empty();

      UpdateChecker::Snapshot snapshot = UpdateChecker::Current();
      if (snapshot.available_version.IsEmpty())
      {
         error = _T("No newer release is known; run a check first.");
         return false;
      }
      if (snapshot.installer_url.IsEmpty() || snapshot.installer_name.IsEmpty())
      {
         error = Formatter::Format(_T("Release {0} carries no x64 installer to download."), snapshot.available_version);
         return false;
      }
      if (snapshot.bundle_url.IsEmpty())
      {
         error = Formatter::Format(_T("Release {0} carries no Sigstore bundle for its installer, so it cannot be verified."), snapshot.available_version);
         return false;
      }

      String directory = UpdatesDirectory();
      if (directory.IsEmpty())
      {
         error = _T("The data directory is not configured.");
         return false;
      }
      if (!FileUtilities::DirectoryExists(directory) && !FileUtilities::CreateDirectory(directory))
      {
         error = Formatter::Format(_T("{0} could not be created."), directory);
         return false;
      }

      SigstoreTrust trust;
      String trustError;
      if (!SigstoreVerifier::ConfiguredTrust(trust, trustError))
      {
         UpdateChecker::RecordFailure(trustError);
         LOG_APPLICATION(Formatter::Format(_T("Update download refused: {0}"), trustError));
         error = trustError;
         return false;
      }

      // The name came from the feed. UpdateChecker refuses a tag that is not a
      // version number, so this cannot normally be anything but a file name - and
      // this is the line that would be exploited if that ever stopped being true,
      // so it does not take the checker's word for it.
      if (snapshot.installer_name.Find(_T("\\")) >= 0 || snapshot.installer_name.Find(_T("/")) >= 0 ||
          snapshot.installer_name.Find(_T(":")) >= 0 || snapshot.installer_name.Find(_T("..")) >= 0 ||
          snapshot.installer_name.IsEmpty())
      {
         error = _T("The release names its installer with a file name this server will not write. Nothing was downloaded.");
         UpdateChecker::RecordFailure(error);
         LOG_APPLICATION(Formatter::Format(_T("Update download refused: {0}"), error));
         return false;
      }

      String installerPath = directory + FileUtilities::PathSeparator + snapshot.installer_name;
      SigstoreVerdict verdict;
      String why;
      if (!FetchVerified(snapshot.installer_url, snapshot.bundle_url, installerPath, snapshot.installer_size, snapshot.installer_digest, trust, verdict, why))
      {
         UpdateChecker::RecordFailure(why);
         LOG_APPLICATION(Formatter::Format(_T("Update download failed for hMailServer {0}: {1}"), snapshot.available_version, why));
         error = why;
         return false;
      }

      UpdateChecker::RecordDownloaded(installerPath, verdict.identity, verdict.integrated_time);
      LOG_APPLICATION(Formatter::Format(_T("Update: hMailServer {0} downloaded to {1} and verified: signed by {2}, recorded by the transparency log at {3}."),
         snapshot.available_version, installerPath, String(verdict.identity), UpdateChecker::FormatUnixTime(verdict.integrated_time)));
      return true;
   }
}
