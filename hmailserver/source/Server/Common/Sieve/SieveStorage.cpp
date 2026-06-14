// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#include "StdAfx.h"

#include "SieveStorage.h"

#include "../Application/IniFileSettings.h"
#include "../Util/FileUtilities.h"
#include "../Util/Parsing/StringParser.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   String
   SieveStorage::SanitizeComponent_(const String &component)
   {
      // Keep a conservative set of filesystem-safe characters; map anything else
      // to '_' so unusual local-parts/domains cannot escape the Sieve directory.
      String lower = component;
      lower.ToLower();

      String result;
      for (int i = 0; i < lower.GetLength(); i++)
      {
         wchar_t ch = lower[i];
         bool safe =
            (ch >= L'a' && ch <= L'z') ||
            (ch >= L'0' && ch <= L'9') ||
            ch == L'.' || ch == L'-' || ch == L'_' || ch == L'+';

         result += safe ? ch : L'_';
      }

      if (result.IsEmpty())
         result = _T("_");

      return result;
   }

   String
   SieveStorage::GetAccountDirectory_(const String &accountAddress)
   {
      String dataDirectory = IniFileSettings::Instance()->GetDataDirectory();

      String domain = SanitizeComponent_(StringParser::ExtractDomain(accountAddress));
      String localPart = SanitizeComponent_(StringParser::ExtractAddress(accountAddress));

      String sieveRoot = FileUtilities::Combine(dataDirectory, _T("Sieve"));
      String domainDirectory = FileUtilities::Combine(sieveRoot, domain);
      return FileUtilities::Combine(domainDirectory, localPart);
   }

   String
   SieveStorage::GetActiveScriptPath_(const String &accountAddress)
   {
      return FileUtilities::Combine(GetAccountDirectory_(accountAddress), _T("active.sieve"));
   }

   String
   SieveStorage::GetActiveScript(const String &accountAddress)
   {
      String path = GetActiveScriptPath_(accountAddress);

      if (!FileUtilities::Exists(path))
         return _T("");

      return FileUtilities::ReadCompleteTextFile(path);
   }

   bool
   SieveStorage::SetActiveScript(const String &accountAddress, const String &script)
   {
      String path = GetActiveScriptPath_(accountAddress);

      if (script.IsEmpty())
      {
         if (FileUtilities::Exists(path))
            FileUtilities::DeleteFile(path);

         return true;
      }

      String directory = GetAccountDirectory_(accountAddress);
      if (!FileUtilities::Exists(directory))
         FileUtilities::CreateDirectory(directory);

      return FileUtilities::WriteToFile(path, script, true);
   }
}
