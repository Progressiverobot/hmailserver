// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "ScriptObjectPolicy.h"

#include "../Application/IniFileSettings.h"
#include "../Util/Parsing/StringParser.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   bool
   ScriptObjectPolicy::IsUnrestricted()
   {
      String setting = IniFileSettings::Instance()->GetScriptAllowedObjects();
      setting.Trim();
      return setting == _T("*");
   }

   bool
   ScriptObjectPolicy::IsAllowed(REFCLSID clsid, String &objectName)
   {
      objectName = DescribeClass_(clsid);

      if (IsUnrestricted())
         return true;

      // The ProgID the class registers, lower-cased, so an entry written the way
      // a script writes it ("Scripting.FileSystemObject") matches whatever CLSID
      // the engine resolved it to - including a versioned ProgID's CLSID when the
      // administrator listed the version-independent name, because both names
      // register the same class.
      String progId;
      LPOLESTR registeredProgId = NULL;
      if (SUCCEEDED(ProgIDFromCLSID(clsid, &registeredProgId)) && registeredProgId)
      {
         progId = registeredProgId;
         CoTaskMemFree(registeredProgId);
         progId.ToLower();
      }

      std::vector<String> entries = StringParser::SplitString(IniFileSettings::Instance()->GetScriptAllowedObjects(), ",");
      for (size_t i = 0; i < entries.size(); i++)
      {
         String entry = entries[i];
         entry.Trim();
         if (entry.IsEmpty())
            continue;

         if (EntryMatches_(entry, clsid, progId))
            return true;
      }

      return false;
   }

   bool
   ScriptObjectPolicy::EntryMatches_(const String &entry, REFCLSID clsid, const String &progId)
   {
      // A CLSID in braces compares as a CLSID; anything else is a ProgID, compared
      // both by name and by the class it resolves to.
      CLSID entryClass = {0};
      if (entry.StartsWith(_T("{")))
      {
         if (SUCCEEDED(CLSIDFromString(const_cast<LPOLESTR>(entry.c_str()), &entryClass)))
            return IsEqualCLSID(entryClass, clsid) != FALSE;
         return false;
      }

      String lowered = entry;
      lowered.ToLower();
      if (!progId.IsEmpty() && lowered == progId)
         return true;

      if (SUCCEEDED(CLSIDFromProgID(entry.c_str(), &entryClass)))
         return IsEqualCLSID(entryClass, clsid) != FALSE;

      return false;
   }

   String
   ScriptObjectPolicy::DescribeClass_(REFCLSID clsid)
   {
      LPOLESTR registeredProgId = NULL;
      if (SUCCEEDED(ProgIDFromCLSID(clsid, &registeredProgId)) && registeredProgId)
      {
         String name = registeredProgId;
         CoTaskMemFree(registeredProgId);
         return name;
      }

      OLECHAR text[64] = {0};
      if (StringFromGUID2(clsid, text, sizeof(text) / sizeof(text[0])) > 0)
         return String(text);

      return _T("(unknown class)");
   }
}
