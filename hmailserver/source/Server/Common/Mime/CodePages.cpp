// Copyright (c) 2007 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
//
// Contains mapping between character sets and code pages
// http://www.iana.org/assignments/character-sets
// http://msdn.microsoft.com/library/default.asp?url=/library/en-us/intl/unicode_81rn.asp
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "CodePages.h"


#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   CodePages::CodePages()
   {
      Initialize();
   }

   CodePages::~CodePages()
   {

   }

   void
   CodePages::Initialize()
   {
      // Complete this list.
      AddCodePage_("US-ASCII", 20127);
      
      AddCodePage_("BIG5", 950);
      AddCodePage_("csBig5", 950);

      AddCodePage_("iso-2022-jp", 50221);
      AddCodePage_("csISO2022JP", 50221);

      AddCodePage_("windows-1250", 1250);
      AddCodePage_("windows-1251", 1251);
      AddCodePage_("windows-1252", 1252);
      AddCodePage_("windows-1253", 1253);
      AddCodePage_("windows-1254", 1254);
      AddCodePage_("windows-1255", 1255);
      AddCodePage_("windows-1256", 1256);
      AddCodePage_("windows-1257", 1257);
      AddCodePage_("windows-1258", 1258);

#ifdef HM_PLATFORM_POSIX
      // The numbers themselves. CP_UTF8 and CP_UTF7 are Windows code page
      // identifiers, and an identifier is a number rather than a behaviour: 65001
      // and 65000 are what <winnls.h> defines them as, and what this table stores
      // and hands to the conversion routines either way. Written out here rather
      // than added to the platform header because these two are the only Windows
      // code page names the tree uses by name; every other entry above is already
      // a literal for exactly the same reason.
      AddCodePage_("utf-8", 65001);
      AddCodePage_("utf-7", 65000);
#else
      AddCodePage_("utf-8", CP_UTF8);
      AddCodePage_("utf-7", CP_UTF7);
#endif
   }

   void 
   CodePages::AddCodePage_(const AnsiString &sName, int iCodePage)
   {
      AnsiString sTmp = sName;
      sTmp.ToLower();

      code_pages_[sTmp] = iCodePage;
   }

   int 
   CodePages::GetCodePage(const AnsiString &sName) const
   {
      AnsiString lowerCaseCharSet = sName;
      lowerCaseCharSet.ToLower();

      std::map<AnsiString, int>::const_iterator iter = code_pages_.find(lowerCaseCharSet);

      if (iter == code_pages_.end())
         return 0;

      return (*iter).second;
   }
      
}
