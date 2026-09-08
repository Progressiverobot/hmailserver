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