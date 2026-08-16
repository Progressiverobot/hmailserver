// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#include "StdAfx.h"

#include "SieveStorage.h"

#include "SieveVacationTracker.h"

#include "../Application/IniFileSettings.h"
#include "../Util/FileUtilities.h"
#include "../Util/Parsing/StringParser.h"

#include <boost/filesystem.hpp>

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
   SieveStorage::GetAccountDirectory(const String &accountAddress)
   {
      return GetAccountDirectory_(accountAddress);
   }

   String
   SieveStorage::GetDomainDirectory(const String &domainName)
   {
      String dataDirectory = IniFileSettings::Instance()->GetDataDirectory();
      String sieveRoot = FileUtilities::Combine(dataDirectory, _T("Sieve"));

      return FileUtilities::Combine(sieveRoot, SanitizeComponent_(domainName));
   }

   String
   SieveStorage::GetActiveScriptPath_(const String &accountAddress)
   {
      return FileUtilities::Combine(GetAccountDirectory_(accountAddress), _T("active.sieve"));
   }

   String
   SieveStorage::GetVacationStorePath(const String &accountAddress)
   {
      return FileUtilities::Combine(GetAccountDirectory_(accountAddress), _T("vacation.responses"));
   }

   String
   SieveStorage::GetDuplicateStorePath(const String &accountAddress)
   {
      return FileUtilities::Combine(GetAccountDirectory_(accountAddress), _T("duplicate.seen"));
   }

   String
   SieveStorage::GetIncludedScript(const String &accountAddress, const String &name, bool global)
   {
      if (!IsValidScriptName(name))
         return _T("");

      // Personal scripts are the account's own named ManageSieve scripts; global
      // ones live in a shared directory beside the domain directories, named with
      // a leading underscore no sanitised domain can produce, and are the
      // administrator's to place - nothing uploads into it.
      String path;
      if (global)
      {
         String dataDirectory = IniFileSettings::Instance()->GetDataDirectory();
         String sieveRoot = FileUtilities::Combine(dataDirectory, _T("Sieve"));
         String globalDirectory = FileUtilities::Combine(sieveRoot, _T("_global"));
         path = FileUtilities::Combine(globalDirectory, name + _T(".sieve"));
      }
      else
      {
         path = GetScriptPath_(accountAddress, name);
      }

      if (!FileUtilities::Exists(path))
         return _T("");

      return FileUtilities::ReadCompleteTextFile(path);
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

         // Switching Sieve filtering off also clears the vacation response records,
         // mirroring SMTPVacationMessageCreator::VacationMessageTurnedOff for the
         // account-level auto-reply. Without this, a user who ends their holiday,
         // deletes the script, then goes away again a week later would find that the
         // people who wrote to them in between hear nothing, because their
         // suppression windows are still running. It also stops an abandoned account
         // leaving a tracking file behind indefinitely: the file prunes itself only
         // when an auto-reply is next considered, which for a deactivated script is
         // never.
         //
         // This is the only place anything is allowed to forget a recorded response,
         // and it is safe precisely because it needs a deliberate act by the account
         // owner: a script that is off sends no replies, so there is no window in
         // which forgetting here can produce a second one.
         SieveVacationTracker::Instance()->Forget(accountAddress);

         return true;
      }

      String directory = GetAccountDirectory_(accountAddress);
      if (!FileUtilities::Exists(directory))
         FileUtilities::CreateDirectory(directory);

      return FileUtilities::WriteToFile(path, script, true);
   }

   bool
   SieveStorage::IsValidScriptName(const String &name)
   {
      if (name.IsEmpty() || name.GetLength() > 128)
         return false;

      if (name == _T(".") || name == _T(".."))
         return false;

      for (int i = 0; i < name.GetLength(); i++)
      {
         wchar_t ch = name[i];
         bool ok =
            (ch >= L'A' && ch <= L'Z') ||
            (ch >= L'a' && ch <= L'z') ||
            (ch >= L'0' && ch <= L'9') ||
            ch == L'.' || ch == L'-' || ch == L'_' || ch == L'+' || ch == L' ';

         if (!ok)
            return false;
      }

      return true;
   }

   String
   SieveStorage::GetScriptsDirectory_(const String &accountAddress)
   {
      return FileUtilities::Combine(GetAccountDirectory_(accountAddress), _T("scripts"));
   }

   String
   SieveStorage::GetScriptPath_(const String &accountAddress, const String &name)
   {
      return FileUtilities::Combine(GetScriptsDirectory_(accountAddress), name + _T(".sieve"));
   }

   String
   SieveStorage::GetActiveNamePath_(const String &accountAddress)
   {
      return FileUtilities::Combine(GetAccountDirectory_(accountAddress), _T("active.name"));
   }

   std::vector<String>
   SieveStorage::ListScriptNames(const String &accountAddress)
   {
      std::vector<String> names;

      String directory = GetScriptsDirectory_(accountAddress);

      boost::system::error_code error_code;
      boost::filesystem::path path(directory.begin(), directory.end());
      if (!boost::filesystem::is_directory(path, error_code))
         return names;

      for (boost::filesystem::directory_iterator file(path, error_code);
           file != boost::filesystem::directory_iterator(); file.increment(error_code))
      {
         boost::filesystem::path current = file->path();
         if (!boost::filesystem::is_regular_file(current, error_code))
            continue;

         String fileName = current.filename().wstring().c_str();

         String lower = fileName;
         lower.ToLower();
         if (lower.EndsWith(_T(".sieve")))
            names.push_back(fileName.Mid(0, fileName.GetLength() - 6));
      }

      return names;
   }

   bool
   SieveStorage::ScriptExists(const String &accountAddress, const String &name)
   {
      return FileUtilities::Exists(GetScriptPath_(accountAddress, name));
   }

   String
   SieveStorage::GetScript(const String &accountAddress, const String &name)
   {
      String path = GetScriptPath_(accountAddress, name);
      if (!FileUtilities::Exists(path))
         return _T("");

      return FileUtilities::ReadCompleteTextFile(path);
   }

   bool
   SieveStorage::PutScript(const String &accountAddress, const String &name, const String &content)
   {
      if (!IsValidScriptName(name))
         return false;

      String directory = GetScriptsDirectory_(accountAddress);
      if (!FileUtilities::Exists(directory))
         FileUtilities::CreateDirectory(directory);

      String path = GetScriptPath_(accountAddress, name);
      if (!FileUtilities::WriteToFile(path, content, true))
         return false;

      // If the active script was overwritten, refresh its live copy.
      if (GetActiveScriptName(accountAddress).CompareNoCase(name) == 0)
         SetActiveScript(accountAddress, content);

      return true;
   }

   bool
   SieveStorage::DeleteScript(const String &accountAddress, const String &name)
   {
      String path = GetScriptPath_(accountAddress, name);
      if (!FileUtilities::Exists(path))
         return false;

      // RFC 5804: the active script cannot be deleted.
      if (GetActiveScriptName(accountAddress).CompareNoCase(name) == 0)
         return false;

      return FileUtilities::DeleteFile(path);
   }

   bool
   SieveStorage::RenameScript(const String &accountAddress, const String &oldName, const String &newName)
   {
      if (!IsValidScriptName(oldName) || !IsValidScriptName(newName))
         return false;

      String oldPath = GetScriptPath_(accountAddress, oldName);
      if (!FileUtilities::Exists(oldPath))
         return false;

      String newPath = GetScriptPath_(accountAddress, newName);
      if (FileUtilities::Exists(newPath))
         return false;

      // Read the active name BEFORE the move: after it, an active-name file
      // naming the old script refers to nothing, and that window should be as
      // small as the code can make it.
      bool wasActive = GetActiveScriptName(accountAddress).CompareNoCase(oldName) == 0;

      if (!FileUtilities::Move(oldPath, newPath))
         return false;

      if (wasActive)
         SetActiveScriptName(accountAddress, newName);

      return true;
   }

   String
   SieveStorage::GetActiveScriptName(const String &accountAddress)
   {
      String path = GetActiveNamePath_(accountAddress);
      if (!FileUtilities::Exists(path))
         return _T("");

      String name = FileUtilities::ReadCompleteTextFile(path);
      name.Trim();
      return name;
   }

   bool
   SieveStorage::SetActiveScriptName(const String &accountAddress, const String &name)
   {
      if (name.IsEmpty())
      {
         // Deactivate: clear the active marker and the live script.
         String namePath = GetActiveNamePath_(accountAddress);
         if (FileUtilities::Exists(namePath))
            FileUtilities::DeleteFile(namePath);

         SetActiveScript(accountAddress, _T(""));
         return true;
      }

      if (!ScriptExists(accountAddress, name))
         return false;

      String content = GetScript(accountAddress, name);
      SetActiveScript(accountAddress, content);

      String directory = GetAccountDirectory_(accountAddress);
      if (!FileUtilities::Exists(directory))
         FileUtilities::CreateDirectory(directory);

      return FileUtilities::WriteToFile(GetActiveNamePath_(accountAddress), name, true);
   }
}
