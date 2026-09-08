// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "DataProtector.h"

#include "Encoding/Base64.h"

#ifdef HM_PLATFORM_POSIX

// DPAPI - CryptProtectData and the machine key it derives - is a Windows facility
// with no POSIX counterpart, so the three headers below are not read on this
// platform and the two entry points refuse rather than pretend. What would replace
// them is the roadmap row "Stored secrets on a machine with no DPAPI"; until that is
// written, this build must not be able to WRITE a secret that nothing here could
// ever read back, which is exactly what a silently succeeding Protect would arrange.

#else
#include <windows.h>
#include <wincrypt.h>
#include <dpapi.h>
#endif

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   bool
   DataProtector::Protect(const AnsiString &plainText, AnsiString &protectedBase64)
   {
      protectedBase64 = "";
#ifdef HM_PLATFORM_POSIX

      ErrorManager::Instance()->ReportError(ErrorManager::High, 6400, "DataProtector::Protect",
         "This build has no secret store: DPAPI is a Windows facility and nothing has been "
         "written to replace it yet, so the value was NOT protected and has NOT been stored. "
         "See the roadmap row \"Stored secrets on a machine with no DPAPI\".");

      return false;

#else

      DATA_BLOB input;
      input.pbData = reinterpret_cast<BYTE*>(const_cast<char*>(plainText.c_str()));
      input.cbData = static_cast<DWORD>(plainText.GetLength());

      DATA_BLOB output;
      output.pbData = nullptr;
      output.cbData = 0;

      // Machine-scope so the secret stays readable across the (potentially
      // different) accounts used by the service and the administration tools,
      // while remaining bound to this machine.
      if (!CryptProtectData(&input, L"hMailServer secret", nullptr, nullptr, nullptr, CRYPTPROTECT_LOCAL_MACHINE, &output))
         return false;

      protectedBase64 = Base64::Encode(reinterpret_cast<const char*>(output.pbData), static_cast<int>(output.cbData));

      if (output.pbData)
         LocalFree(output.pbData);

      return true;
#endif
   }

   bool
   DataProtector::Unprotect(const AnsiString &protectedBase64, AnsiString &plainText)
   {
      plainText = "";
#ifdef HM_PLATFORM_POSIX

      ErrorManager::Instance()->ReportError(ErrorManager::High, 6401, "DataProtector::Unprotect",
         "A protected secret was found but this build cannot read it: DPAPI is a Windows "
         "facility and nothing has been written to replace it yet. The secret is intact on "
         "disk; it is this build that cannot open it.");

      return false;

#else

      AnsiString raw = Base64::Decode(protectedBase64.c_str(), protectedBase64.GetLength());
      if (raw.GetLength() == 0)
         return false;

      DATA_BLOB input;
      input.pbData = reinterpret_cast<BYTE*>(const_cast<char*>(raw.c_str()));
      input.cbData = static_cast<DWORD>(raw.GetLength());

      DATA_BLOB output;
      output.pbData = nullptr;
      output.cbData = 0;

      if (!CryptUnprotectData(&input, nullptr, nullptr, nullptr, nullptr, 0, &output))
         return false;

      plainText.assign(reinterpret_cast<const char*>(output.pbData), output.cbData);

      if (output.pbData)
         LocalFree(output.pbData);

      return true;
#endif
   }

   void
   DataProtectorTester::Test()
   {
#ifdef HM_PLATFORM_POSIX

      // There is nothing to round-trip: Protect refuses on this platform, so a test
      // that ran would only re-prove that. It is reported rather than skipped in
      // silence, because a self-test that quietly does nothing is indistinguishable
      // from one that passed.
      ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6402, "DataProtectorTester::Test",
         "The DPAPI round-trip self-test did not run: this build has no secret store.");

      return;

#else
      // Round-trip a secret and prove the protected form is neither the plaintext
      // nor trivially recoverable without DPAPI.
      const AnsiString secret = "S3cr3t-DB-p@ssw0rd \xC3\xA4\xC3\xB6"; // includes UTF-8 bytes

      AnsiString protectedBlob;
      if (!DataProtector::Protect(secret, protectedBlob))
         throw 0;

      if (protectedBlob.IsEmpty())
         throw 0;

      // The protected blob must not contain the plaintext.
      if (protectedBlob.Find(secret) != -1)
         throw 0;

      AnsiString recovered;
      if (!DataProtector::Unprotect(protectedBlob, recovered))
         throw 0;

      if (recovered != secret)
         throw 0;

      // A corrupted blob must fail to unprotect rather than return garbage.
      if (protectedBlob.GetLength() > 4)
      {
         std::string t(protectedBlob.c_str(), protectedBlob.GetLength());
         size_t pos = t.size() - 2;
         t[pos] = (t[pos] == 'A') ? 'B' : 'A';
         AnsiString tampered = t.c_str();
         AnsiString shouldFail;
         DataProtector::Unprotect(tampered, shouldFail);
         // We don't assert failure here (DPAPI may tolerate some base64 noise);
         // we only assert it never silently returns the original secret.
         if (shouldFail == secret)
            throw 0;
      }
#endif
   }
}
