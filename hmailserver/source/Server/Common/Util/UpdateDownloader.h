// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "SigstoreVerifier.h"

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

      // Fetches one installer and its bundle to installerPath (the bundle beside it as
      // .cosign.bundle) and verifies it; false with why, and nothing left, when it
      // does not verify. expectedSize and expectedDigest are the feed's hints, checked
      // when given. What DownloadAndVerify and the rollback image both do.
      static bool FetchVerified(const String &installerUrl, const String &bundleUrl, const String &installerPath, __int64 expectedSize,
                                const String &expectedDigest, const SigstoreTrust &trust, SigstoreVerdict &verdict, String &why);

      // Authenticode, when UpdateRequireAuthenticode=1: WinVerifyTrust on the file.
      static bool AuthenticodeTrusted(const String &path, String &why);
   };
}
