// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "UpdateApplyToken.h"
#include "UpdateDownloader.h"
#include "FileUtilities.h"

#include <openssl/rand.h>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      const int TOKEN_BYTES = 32;
      const __int64 TOKEN_LIFETIME_SECONDS = 3600;

      bool FileAgeSeconds_(const String &path, __int64 &seconds)
      {
         WIN32_FILE_ATTRIBUTE_DATA attributes;
         if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &attributes))
            return false;

         FILETIME now;
         GetSystemTimeAsFileTime(&now);

         ULARGE_INTEGER written, current;
         written.LowPart = attributes.ftLastWriteTime.dwLowDateTime;
         written.HighPart = attributes.ftLastWriteTime.dwHighDateTime;
         current.LowPart = now.dwLowDateTime;
         current.HighPart = now.dwHighDateTime;

         // 100-nanosecond units. A file written in the future, by a clock that was
         // then corrected, counts as fresh.
         seconds = current.QuadPart <= written.QuadPart ? 0 : (__int64) ((current.QuadPart - written.QuadPart) / 10000000ULL);
         return true;
      }
   }

   String
   UpdateApplyToken::Path()
   {
      String directory = UpdateDownloader::UpdatesDirectory();
      return directory.IsEmpty() ? String() : directory + _T("\\apply-token");
   }

   bool
   UpdateApplyToken::Issue(AnsiString &token, String &error)
   {
      String path = Path();
      if (path.IsEmpty())
      {
         error = _T("The data directory is not configured.");
         return false;
      }

      unsigned char bytes[TOKEN_BYTES];
      if (RAND_bytes(bytes, TOKEN_BYTES) != 1)
      {
         error = _T("No random bytes for the token.");
         return false;
      }

      static const char *digits = "0123456789abcdef";
      token = "";
      for (int i = 0; i < TOKEN_BYTES; i++)
      {
         token += digits[bytes[i] >> 4];
         token += digits[bytes[i] & 0x0F];
      }

      String directory = UpdateDownloader::UpdatesDirectory();
      if (!FileUtilities::DirectoryExists(directory) && !FileUtilities::CreateDirectory(directory))
      {
         error = Formatter::Format(_T("{0} could not be created."), directory);
         return false;
      }

      if (!FileUtilities::WriteToFile(path, token))
      {
         error = Formatter::Format(_T("{0} could not be written."), path);
         return false;
      }

      return true;
   }

   bool
   UpdateApplyToken::Redeem(const String &presented)
   {
      const String prefix = _T("token:");
      if (presented.GetLength() != (int) prefix.GetLength() + TOKEN_BYTES * 2 || presented.Left(prefix.GetLength()) != prefix)
         return false;

      String path = Path();
      if (path.IsEmpty() || !FileUtilities::Exists(path))
         return false;

      __int64 age = 0;
      if (!FileAgeSeconds_(path, age) || age > TOKEN_LIFETIME_SECONDS)
      {
         // Stale: a token the helper never used, from an apply that did not happen.
         FileUtilities::DeleteFile(path);
         LOG_APPLICATION("Update apply token refused: it had expired.");
         return false;
      }

      String stored = FileUtilities::ReadCompleteTextFile(path);
      stored.TrimRight();
      stored.TrimLeft();

      String offered = presented.Mid(prefix.GetLength());
      if (stored.GetLength() != offered.GetLength())
         return false;

      // Constant time over the whole length: a comparison that stops at the first
      // difference would let a guesser measure how much of the token it has.
      int difference = 0;
      for (int i = 0; i < stored.GetLength(); i++)
         difference |= stored[i] ^ offered[i];
      if (difference != 0)
         return false;

      FileUtilities::DeleteFile(path);
      LOG_APPLICATION("Update apply token redeemed: the database upgrade authenticated as the administrator with the token issued for this update.");
      return true;
   }

   void
   UpdateApplyToken::Revoke()
   {
      String path = Path();
      if (!path.IsEmpty() && FileUtilities::Exists(path))
         FileUtilities::DeleteFile(path);
   }
}
