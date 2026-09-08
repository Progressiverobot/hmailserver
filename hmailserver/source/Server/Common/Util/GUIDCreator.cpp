// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "GUIDCreator.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   GUIDCreator::GUIDCreator()
   {

   }

   GUIDCreator::~GUIDCreator()
   {

   }

   String
   GUIDCreator::GetGUID()
   {
#ifdef HM_PLATFORM_POSIX
      // CoCreateGuid is COM's name for UuidCreate, which has returned a version 4
      // (random) UUID since Windows 2000. That is a FORMAT rather than a Windows
      // mechanism, so this generates the same thing from the kernel's random pool
      // and writes it exactly the way StringFromGUID2 does - braces, upper case,
      // 8-4-4-4-12 - because callers put this string into file names, into message
      // identifiers and into the database, and a build must not be identifiable
      // from the shape of its identifiers.
      unsigned char bytes[16];

      // /dev/urandom rather than rand(): these end up in Message-IDs and in
      // minidump file names, and a predictable one of either is a real problem.
      FILE *randomSource = ::fopen("/dev/urandom", "rb");

      if (randomSource == nullptr)
         return "";

      const size_t bytesRead = ::fread(bytes, 1, sizeof(bytes), randomSource);

      ::fclose(randomSource);

      if (bytesRead != sizeof(bytes))
         return "";

      // RFC 4122 fixes four bits of version and two of variant; the other 122 are
      // the random ones. Without these two lines the result is 128 random bits
      // rather than a UUID, and anything that parses one would be entitled to
      // refuse it.
      bytes[6] = (unsigned char) ((bytes[6] & 0x0F) | 0x40);
      bytes[8] = (unsigned char) ((bytes[8] & 0x3F) | 0x80);

      wchar_t szGUID[39] = { 0 };

      ::swprintf(szGUID, sizeof(szGUID) / sizeof(szGUID[0]),
         L"{%02X%02X%02X%02X-%02X%02X-%02X%02X-%02X%02X-%02X%02X%02X%02X%02X%02X}",
         bytes[0], bytes[1], bytes[2], bytes[3],
         bytes[4], bytes[5], bytes[6], bytes[7],
         bytes[8], bytes[9], bytes[10], bytes[11],
         bytes[12], bytes[13], bytes[14], bytes[15]);

      return szGUID;
#else
      GUID uuid = { 0 };
      if (FAILED(CoCreateGuid(&uuid)))
         return "";

      wchar_t szGUID[39] = { 0 };
      if (StringFromGUID2(uuid, szGUID, 39) == 0)
         return "";

      return szGUID;
#endif
   }
}
