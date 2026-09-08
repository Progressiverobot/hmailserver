// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "./SystemInformation.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   SystemInformation::OperatingSystem SystemInformation::operating_system_ = Unknown;

   SystemInformation::SystemInformation(void)
   {

   }

   SystemInformation::~SystemInformation(void)
   {
   }

   SystemInformation::OperatingSystem 
   SystemInformation::GetOperatingSystem()
   {
      // Initialize operating system version once:
      if (operating_system_ == Unknown)
      {
#ifdef HM_PLATFORM_POSIX
         // Every value of this enumeration names a version of Windows, and all
         // three callers ask the same kind of question - "is this one of the old
         // ones?" - so that they can apply a work-around for a defect in it. Here
         // the answer is that it is none of them and there is no work-around to
         // apply, so the classification stays Unknown. That is the truth on this
         // platform rather than a stub: every caller already reads Unknown as
         // "not one of the versions I am asking about" and takes the modern path.
         //
         // Nothing in the POSIX build reaches here today - the callers are
         // ADOInt64Helper, which is ADO, and hMailServer.exe - but the function is
         // kept so that the class means the same thing on both platforms.
#else
         OSVERSIONINFO OSversion;

         OSversion.dwOSVersionInfoSize=sizeof(OSVERSIONINFO);

         // Disabling warning: "Deprecated. Use VerifyVersionInfo* or IsWindows* macros from VersionHelpers."
         #pragma warning(push) 
         #pragma warning(disable:4996)
         ::GetVersionEx(&OSversion);
         #pragma warning (pop)

         switch(OSversion.dwPlatformId)
         {
         case VER_PLATFORM_WIN32s: 
            operating_system_ = Windows3;
         case VER_PLATFORM_WIN32_WINDOWS:
            if(OSversion.dwMinorVersion==0)
               operating_system_ = Windows95;
            else if(OSversion.dwMinorVersion==10)  
               operating_system_ = Windows98;
            else if(OSversion.dwMinorVersion==90)  
               operating_system_ = Windows98;
            break;
         case VER_PLATFORM_WIN32_NT:
            if(OSversion.dwMajorVersion==5 && OSversion.dwMinorVersion==0)
               operating_system_ = Windows2000;
            else if(OSversion.dwMajorVersion==5 &&   OSversion.dwMinorVersion==1)
               operating_system_ = WindowsXP;
            else if(OSversion.dwMajorVersion<=4)
               operating_system_ = WindowsNT;
            else  
               //for unknown windows/newest windows version
               operating_system_ = Windows2003;
         }      
#endif
      }

      return operating_system_;
   }
}