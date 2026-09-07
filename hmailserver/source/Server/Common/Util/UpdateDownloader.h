// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   // The second of the four parts of the roadmap's live update: the verify.
   //
   // Fetches the installer the last check found, and its Sigstore bundle, into the
   // data directory's Updates folder, and keeps the installer only when the bundle
   // proves it is the one this repository's release workflow signed
   // (SigstoreVerifier). A file that fails is deleted and the failure logged; it is
   // never left where anything could run it. Nothing runs here either way - that
   // is the third part, gated on this one.
   class UpdateDownloader
   {
   public:
      // On the calling thread. True when the installer is in place and verified;
      // false with error, the files gone and the verdict recorded as a failure.
      static bool DownloadAndVerify(String &error);

      // <DataFolder>\Updates
      static String UpdatesDirectory();

      // Authenticode, when UpdateRequireAuthenticode=1: WinVerifyTrust on the file.
      static bool AuthenticodeTrusted(const String &path, String &why);
   };
}
