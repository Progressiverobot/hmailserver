// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "Registry.h"



#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   Registry::Registry(void)
   {
   }

   Registry::~Registry(void)
   {
   }

   bool
   Registry::GetStringValue(HKEY hive, String key, String valueName, String &value)
   {
#ifdef HM_PLATFORM_POSIX
      // There is no registry here, and nothing on this platform is the registry
      // under another name: the configuration this class reaches for on Windows -
      // an installation path written by the installer, a value another product
      // left behind - lives in files on a POSIX machine and is found by a path
      // rather than by a hive and a key. So the answer is a refusal rather than an
      // empty string, because an empty string out of this function is
      // indistinguishable from a value that is genuinely empty, and a caller
      // reading the second would carry on with a setting it never obtained.
      //
      // Reported as well as refused. Nothing in the tree calls this today, which
      // is why the refusal is written rather than the whole file guarded away: the
      // day something does, the log says which key was asked for and that this
      // build could not go and look.
      // The hive is a Windows HKEY and there is nothing here to name it against,
      // so it is not part of the message; referenced so that a build with warnings
      // as errors does not fail on it.
      (void) hive;

      value.Empty();

      ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6407, "Registry::GetStringValue",
         Formatter::Format(_T("A registry value was asked for and this build has no registry to read it from: ")
            _T("key '{0}', value '{1}'. The Windows registry is a Windows facility and there is no POSIX ")
            _T("equivalent to read instead. Whatever needed this setting has NOT been given one."), key, valueName));

      return false;
#else
     /* HKEY   hkey; 
      DWORD  dwDisposition; 
      LONG result = RegCreateKeyEx(HKEY_CURRENT_USER, TEXT("Software"),  
         0, NULL, 0, KEY_READ, NULL, &hkey, &dwDisposition); 
*/

      DWORD maxSize = 8096;

      HKEY   hkey;

      if (RegCreateKeyExW(hive, key, 0, NULL, 0, KEY_READ | KEY_WOW64_32KEY, NULL, &hkey, 0) != ERROR_SUCCESS)
      {
         int err = GetLastError();
         return false;
      }

      DWORD dwType = REG_SZ;
      bool success = 
         RegQueryValueEx(hkey, valueName.GetBuffer(), NULL, &dwType, (PBYTE) value.GetBuffer(maxSize), &maxSize) == ERROR_SUCCESS;

      RegCloseKey(hkey);

      value.ReleaseBuffer();

      if (dwType != REG_SZ || success == false)
         return false;

      return true;
#endif
   }



}