// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "UpdateApplyToken.h"
#include "UpdateDownloader.h"
#include "FileUtilities.h"

#include <openssl/rand.h>
#ifdef HM_PLATFORM_POSIX

// sddl.h has no counterpart here and needs none: the rule it is used to state -
// this file is readable by the account the server runs as and by nobody else - is
// a creation mode on this platform rather than a security descriptor. What the
// POSIX branches below need instead is open(2), stat(2) and unlink(2), and
// boost::filesystem to turn the wide path the server carries into the narrow one
// those take.
#include <boost/filesystem.hpp>

#include <sys/stat.h>
#include <fcntl.h>
#include <unistd.h>

#include <cerrno>
#include <ctime>

#else
#include <sddl.h>
#endif

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
#ifndef HM_PLATFORM_POSIX

      // The SID of the account this process runs as, as a string, or empty when
      // it cannot be read - in which case the DACL simply does not name it, and a
      // service running as something other than SYSTEM fails to write the token
      // rather than writing one it cannot read back.
      String CurrentUserSid_()
      {
         HANDLE token = nullptr;
         if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token))
            return String();

         DWORD needed = 0;
         GetTokenInformation(token, TokenUser, nullptr, 0, &needed);
         if (needed == 0)
         {
            CloseHandle(token);
            return String();
         }

         std::vector<unsigned char> buffer(needed);
         String result;
         if (GetTokenInformation(token, TokenUser, &buffer[0], needed, &needed))
         {
            LPWSTR text = nullptr;
            if (ConvertSidToStringSidW(((TOKEN_USER *) &buffer[0])->User.Sid, &text))
            {
               result = text;
               LocalFree(text);
            }
         }

         CloseHandle(token);
         return result;
      }
#endif

      // The token is the administrator password for one hour and one use, so the
      // file it lives in is written with a DACL of its own rather than inheriting
      // the data directory's. A default install puts Data under {app} in Program
      // Files, where BUILTIN\Users can read - and a local user who can read this
      // file can authenticate to the COM API as the administrator. The DACL is
      // protected (P), so no inherited entry widens it, and it names nobody but
      // SYSTEM, the local Administrators group and the account the service runs as.
      //
      // A failure here fails the issue: a token written where anyone can read it
      // is worse than an upgrade that asks for a password.
      bool WriteProtected_(const String &path, const AnsiString &token, String &error)
      {
#ifdef HM_PLATFORM_POSIX

         // The POSIX statement of exactly the rule the DACL below states: this file
         // is readable by the account the server runs as and by nobody else.
         //
         // 0600 is given to open(2) AT CREATION rather than applied with chmod
         // afterwards, because a file created 0644 and narrowed a moment later is
         // world-readable for that moment, and one moment is all a local user needs
         // to read a token that authenticates as the administrator.
         //
         // The unlink-then-O_EXCL pair is the counterpart of the DeleteFile and
         // CREATE_NEW in the Windows branch, and is there for the same reason:
         // opening an existing file keeps whatever permissions that file already
         // had, which is precisely the case this code exists to prevent.
         const std::string native = boost::filesystem::path(std::wstring(path)).string();

         ::unlink(native.c_str());

         const int descriptor = ::open(native.c_str(), O_WRONLY | O_CREAT | O_EXCL, S_IRUSR | S_IWUSR);

         if (descriptor < 0)
         {
            error = Formatter::Format(_T("{0} could not be written (error {1})."), path, (int) errno);
            return false;
         }

         const ssize_t written = ::write(descriptor, token.c_str(), (size_t) token.GetLength());

         ::close(descriptor);

         // A short write leaves a truncated token on disk, which would be redeemed by
         // nothing and would look to the next Issue like a token already in hand, so
         // the file goes rather than being left behind.
         if (written != (ssize_t) token.GetLength())
         {
            ::unlink(native.c_str());
            error = Formatter::Format(_T("{0} could not be written."), path);
            return false;
         }

         return true;

#else
         // SYSTEM, the local Administrators group, and the account this process
         // runs as. The last one matters: the service does not have to be
         // LocalSystem - there is a setting for running it as a named account -
         // and a DACL naming only SYSTEM would leave the server unable to read
         // back the token it had just written.
         String sddl = _T("D:P(A;;FA;;;SY)(A;;FA;;;BA)");
         String owner = CurrentUserSid_();
         if (!owner.IsEmpty() && owner != _T("S-1-5-18"))
            sddl += Formatter::Format(_T("(A;;FA;;;{0})"), owner);

         PSECURITY_DESCRIPTOR descriptor = nullptr;
         if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl, SDDL_REVISION_1, &descriptor, nullptr))
         {
            error = Formatter::Format(_T("The token file's permissions could not be built (error {0})."), (int) GetLastError());
            return false;
         }

         SECURITY_ATTRIBUTES attributes = {};
         attributes.nLength = sizeof(attributes);
         attributes.lpSecurityDescriptor = descriptor;
         attributes.bInheritHandle = FALSE;

         // CREATE_ALWAYS on an existing file keeps the file's existing DACL, so an
         // earlier token file written before this code - or by anything else - is
         // removed first and the new one created with the DACL above.
         DeleteFile(path);

         HANDLE handle = CreateFileW(path, GENERIC_WRITE, 0, &attributes, CREATE_NEW,
                                     FILE_ATTRIBUTE_NORMAL, nullptr);
         LocalFree(descriptor);

         if (handle == INVALID_HANDLE_VALUE)
         {
            error = Formatter::Format(_T("{0} could not be written (error {1})."), path, (int) GetLastError());
            return false;
         }

         DWORD written = 0;
         bool ok = WriteFile(handle, token.c_str(), (DWORD) token.GetLength(), &written, nullptr) != 0 &&
                   written == (DWORD) token.GetLength();
         CloseHandle(handle);

         if (!ok)
         {
            DeleteFile(path);
            error = Formatter::Format(_T("{0} could not be written."), path);
            return false;
         }

         return true;
#endif
      }

      bool FileAgeSeconds_(const String &path, __int64 &seconds)
      {
#ifdef HM_PLATFORM_POSIX

         // st_mtime is the last modification, which is what the Windows branch reads
         // too (ftLastWriteTime), so both platforms measure the age of the same event.
         struct stat status = {};

         if (::stat(boost::filesystem::path(std::wstring(path)).string().c_str(), &status) != 0)
            return false;

         const time_t now = ::time(nullptr);

         // A file written in the future, by a clock that was then corrected, counts
         // as fresh - the same rule the Windows branch applies below.
         seconds = now <= status.st_mtime ? 0 : (__int64) (now - status.st_mtime);
         return true;

#else
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
#endif
      }
   }

   String
   UpdateApplyToken::Path()
   {
      String directory = UpdateDownloader::UpdatesDirectory();
      return directory.IsEmpty() ? String() : directory + FileUtilities::PathSeparator + _T("apply-token");
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

      if (!WriteProtected_(path, token, error))
         return false;

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
