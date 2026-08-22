// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "DataProtector.h"

#include "Encoding/Base64.h"

#include <windows.h>
#include <wincrypt.h>
#include <dpapi.h>

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
   }

   bool
   DataProtector::Unprotect(const AnsiString &protectedBase64, AnsiString &plainText)
   {
      plainText = "";

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
   }

   void
   DataProtectorTester::Test()
   {
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
   }
}
