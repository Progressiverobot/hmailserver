// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class Unicode  
   {
   public:
      Unicode();
      virtual ~Unicode();

      static AnsiString ToANSI(const String &sString);
      static bool WideToMultiByte(const String &sInput, AnsiString &sOutput);
      static bool MultiByteToWide(const AnsiString &sInput, String &sOutput);

      static unsigned char* CharMoveNext(unsigned char*, bool utf8);

      // The on-disk form of a String, and the way back.
      //
      // Every text file this server writes with a byte order mark - the backup
      // and event logs, the backup index, a Sieve script, the rate-limiter state
      // - is UTF-16LE, because on Windows that is what a String's own bytes are
      // and File::Write used to write those bytes as they lay. On Linux a
      // wchar_t is four bytes, so the same code wrote UTF-32 and read UTF-16 as
      // UTF-32, and a file written by either could not be read by the other.
      // These two are the conversion that both platforms now go through: the
      // bytes are UTF-16LE on either, without a mark, and a String read from
      // them holds the same characters on either.
      //
      // Where wchar_t is two bytes the conversion is a copy, so a file written
      // on Windows is byte for byte the file it was before. Where it is four, a
      // character above U+FFFF becomes a surrogate pair on the way out and a
      // pair becomes one character on the way in. An unpaired surrogate is kept
      // as the unit it is in both directions, so a file this program did not
      // write survives a read and a write unchanged.
      static std::string ToUtf16Le(const std::wstring &text);
      static std::wstring FromUtf16Le(const unsigned char *bytes, size_t byteCount);

#ifdef HM_PLATFORM_POSIX
      // The two conversions Windows spells MultiByteToWideChar and
      // WideCharToMultiByte, over iconv - which is what a POSIX system has for the
      // job, and which knows every code page this server names.
      //
      // They are HERE rather than in the platform compatibility header because
      // they are NOT an exact equivalent of the Win32 pair, and that header's rule
      // is that everything in it is. iconv has no counterpart for lpDefaultChar or
      // lpUsedDefaultChar and nothing that matches MB_ERR_INVALID_CHARS, so what
      // these offer is the part of the pair the server actually uses: a code page,
      // a length, and either a buffer to fill or a request for the size one would
      // need.
      //
      // codePage is the Windows code page identifier, which is what the tree
      // already stores in CodePages and passes about; 0 means the machine's own
      // character set, as CP_ACP does on Windows. The length rules are the Win32
      // ones exactly, because the callers were written against them:
      //
      //   sourceLength < 0        the source is NUL-terminated, and the terminator
      //                           is converted and counted with it.
      //   destinationLength == 0  do not convert; answer the length that would be
      //                           needed, in wide characters or in bytes.
      //   the answer 0            the conversion could not be done - an unknown
      //                           code page, a byte sequence that is not valid in
      //                           it, or a destination too small - which is the
      //                           same 0 the Win32 pair returns and which every
      //                           caller here already tests for.
      static int FromCodePage(unsigned int codePage, const char *source, int sourceLength, wchar_t *destination, int destinationLength);
      static int ToCodePage(unsigned int codePage, const wchar_t *source, int sourceLength, char *destination, int destinationLength);
#endif

   private:

   };


}