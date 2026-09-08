// Copyright (c) 2012 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"

#include "ADOInt64Helper.h"
#include "../Util/SystemInformation.h"

using namespace std;

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

// ADO and SQL Server Compact are COM, and the roadmap section "Linux and
// AArch64" - the row "Database backends that survive" - leaves both out of the
// POSIX build, where MySQL and PostgreSQL are the two backends that work. Every
// member below is written in terms of the ADO smart pointers the Windows
// precompiled header #imports (_ConnectionPtr, _RecordsetPtr, _CommandPtr),
// which have no POSIX shape at all, so the file is Windows-only in its entirety
// rather than a set of stubs that would look like a database backend.
//
// A POSIX build asked for either backend is refused by name in
// DALConnectionFactory::CreateConnection, which reports HM6390 saying which
// backend was refused and why - so nothing here is reached silently, and a link
// error rather than a stub is what a mistaken caller would get.
#ifndef HM_PLATFORM_POSIX

namespace HM
{
   ADO64Helper ::ADO64Helper ()
   {

   }

   void 
   ADO64Helper ::AddInt64Parameter(_CommandPtr &command, const String& parameterName, __int64 value)
   {
      // int64 variants are not supported in Windows 2000.
      if (SystemInformation::GetOperatingSystem() == SystemInformation::Windows2000)
      {
         String val = StringParser::IntToString(value);

         VARIANT stringType;
         stringType.vt = VT_BSTR;
         stringType.bstrVal  = _bstr_t(val);

         int length = 8000;

         command->Parameters->Append(command->CreateParameter(_bstr_t(parameterName),adWChar,adParamInput, length, stringType));
      }
      else
      {
         VARIANT int64Variant;
         int64Variant.vt = VT_I8;
         int64Variant.llVal = value;

         command->Parameters->Append(command->CreateParameter(_bstr_t(parameterName), adBigInt,adParamInput, sizeof(__int64), int64Variant));
      }
   }
}


#endif
