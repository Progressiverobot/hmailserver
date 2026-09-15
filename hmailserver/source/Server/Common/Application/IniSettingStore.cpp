// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "IniSettingStore.h"

#include "IniFileSettings.h"
#include "../SQL/SQLCommand.h"
#include "../SQL/SQLStatement.h"
#include "../SQL/DALRecordset.h"
#include "../Util/XMLite.h"

// The Win32 profile API - GetPrivateProfileString and the three calls beside
// it - lives in kernel32 and has no POSIX equivalent, so the POSIX build
// declares those four functions here and implements them over the INI file
// itself in Common/Util/IniFile.cpp. The Windows build never reaches this line
// and binds the same calls to <windows.h> as it always has.
#ifdef HM_PLATFORM_POSIX
#include "../Util/IniFile.h"
#endif

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   IniSettingStore::IniSettingStore()
   {
   }

   IniSettingStore::~IniSettingStore()
   {
   }

   void
   IniSettingStore::ReadIniSection_(const String &section, std::map<String, String> &values)
   {
      values.clear();

      String iniFile = IniFileSettings::GetInitializationFile();
      if (iniFile.IsEmpty())
         return;

      // Read the whole section in one call, growing the buffer while the result
      // looks truncated. Same shape as RateLimiter::LoadSettings_, which reads its
      // own overrides section: GetPrivateProfileSection reports "copied" one or two
      // short of the buffer when it has run out of room rather than failing, so the
      // only reliable test is that it came back short of the size that was offered.
      std::vector<TCHAR> buffer;
      DWORD size = 16384;
      DWORD copied = 0;

      for (int attempt = 0; attempt < 6; attempt++)
      {
         buffer.assign(size, _T('\0'));
         copied = GetPrivateProfileSection(section.c_str(), &buffer[0], size, iniFile.c_str());

         if (copied < size - 2)
            break;

         size *= 4;
      }

      if (copied == 0)
         return;

      const TCHAR *entry = &buffer[0];
      while (*entry != 0)
      {
         String line = entry;
         entry += line.GetLength() + 1;

         // A comment line has no place in the table: it is not a setting, and
         // copying it in would create a row whose name is not a key.
         String trimmed = line;
         trimmed.Trim();
         if (trimmed.IsEmpty() || trimmed.StartsWith(_T(";")) || trimmed.StartsWith(_T("#")))
            continue;

         int separator = trimmed.Find(_T("="));
         if (separator <= 0)
            continue;

         String name = trimmed.Mid(0, separator);
         String value = trimmed.Mid(separator + 1);

         name.Trim();

         // The value is NOT trimmed of trailing space here beyond what the section
         // read already did. GetPrivateProfileString, which is what every reader
         // uses, returns the value with surrounding whitespace as the file has it,
         // and a mirror that trims would disagree with the reader it mirrors.
         if (name.IsEmpty())
            continue;

         // The column is 100 characters. A longer key cannot be stored, and
         // truncating it would create a row that matches the wrong setting - so it
         // is skipped and named, and that setting simply stays ini-only.
         if (name.GetLength() > 100)
         {
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5800, "IniSettingStore::ReadIniSection_",
               Formatter::Format("The hMailServer.INI setting '{0}' in [{1}] has a name longer than 100 characters, so it cannot be stored in the database, which is the settings store. Shorten the name or remove the setting.", name, section));
            continue;
         }

         if (value.GetLength() > 4000)
         {
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5801, "IniSettingStore::ReadIniSection_",
               Formatter::Format("The hMailServer.INI setting '{0}' in [{1}] has a value longer than 4000 characters, so it cannot be stored in the database, which is the settings store.", name, section));
            continue;
         }

         values[name] = value;
      }
   }

   /// <summary>
   /// The database manager, or an empty pointer when there is none.
   ///
   /// There is none on a database this build refused - a schema version it does not
   /// know, a connection that could not be made - and the COM layer keeps answering
   /// on such a server, with the refusal, rather than disappearing. Settings are the
   /// first thing anything asks it for, so every statement below goes through this
   /// and a null one is a false return rather than an access violation. HM5011 has a
   /// fixture of its own and this is the path it walks.
   /// </summary>
   static std::shared_ptr<DatabaseConnectionManager> StoreDatabase_()
   {
      return Application::Instance()->GetDBManager();
   }

   bool
   IniSettingStore::ReadAllRows(std::map<String, std::pair<String, String> > &rows)
   {
      rows.clear();

      std::shared_ptr<DatabaseConnectionManager> database = StoreDatabase_();

      if (!database)
         return false;

      SQLCommand command("select inisettingname, inisettingvalue, inisettingfilevalue from hm_inisettings");

      std::shared_ptr<DALRecordset> recordset = database->OpenRecordset(command);

      if (!recordset)
         return false;

      while (!recordset->IsEOF())
      {
         String name = recordset->GetStringValue("inisettingname");
         String value = recordset->GetStringValue("inisettingvalue");
         String fileValue = recordset->GetStringValue("inisettingfilevalue");

         if (!name.IsEmpty())
            rows[name] = std::make_pair(value, fileValue);

         recordset->MoveNext();
      }

      return true;
   }

   bool
   IniSettingStore::InsertRow_(const String &name, const String &value, const String &fileValue)
   {
      // Parameters rather than a formatted statement: these names and values come
      // from a file an administrator edits, so they are not trusted input even
      // though they are local.
      std::shared_ptr<DatabaseConnectionManager> database = StoreDatabase_();

      if (!database)
         return false;

      SQLStatement statement(SQLStatement::STInsert, "hm_inisettings");
      statement.AddColumn("inisettingname", name);
      statement.AddColumn("inisettingvalue", value);
      statement.AddColumn("inisettingfilevalue", fileValue);

      return database->Execute(statement);
   }

   bool
   IniSettingStore::UpdateRow_(const String &name, const String &value, const String &fileValue)
   {
      std::shared_ptr<DatabaseConnectionManager> database = StoreDatabase_();

      if (!database)
         return false;

      SQLCommand command("update hm_inisettings set inisettingvalue = @VALUE, inisettingfilevalue = @FILEVALUE where inisettingname = @NAME");
      command.AddParameter("@VALUE", value);
      command.AddParameter("@FILEVALUE", fileValue);
      command.AddParameter("@NAME", name);

      return database->Execute(command);
   }

   bool
   IniSettingStore::DeleteRow_(const String &name)
   {
      std::shared_ptr<DatabaseConnectionManager> database = StoreDatabase_();

      if (!database)
         return false;

      SQLCommand command("delete from hm_inisettings where inisettingname = @NAME");
      command.AddParameter("@NAME", name);

      return database->Execute(command);
   }

   void
   IniSettingStore::ReadOverrides(std::map<String, String> &values)
   {
      ReadIniSection_(_T("SettingsOverride"), values);
   }

   bool
   IniSettingStore::WriteIniValue_(const String &section, const String &name, const String &value)
   {
      String iniFile = IniFileSettings::GetInitializationFile();
      if (iniFile.IsEmpty())
         return false;

      if (WritePrivateProfileString(section.c_str(), name.c_str(), value.c_str(), iniFile.c_str()) == FALSE)
         return false;

      // WritePrivateProfileString caches; flush so that anything reading the file
      // afterwards - including this process's own GetPrivateProfileString calls -
      // sees the value that was just written.
      WritePrivateProfileString(nullptr, nullptr, nullptr, iniFile.c_str());

      return true;
   }

   void
   IniSettingStore::WriteFileNotice_()
   {
      // Two keys, because the profile API writes keys and not comment lines, and it
      // is the only writer the POSIX build has. Store= is the machine-readable fact;
      // ReadMe= is the sentence an administrator opening the file needs to read
      // before they edit a value and wonder why nothing happened.
      static const String kStore = _T("database");
      static const String kReadMe =
         _T("The [Settings] section below is a CACHE of the hm_inisettings table, which is the settings store from schema 6042. ")
         _T("Editing a value here does not change it: the stored value is used, the edit is named in hMailServer_ERROR.log and the line is put back at the next start. ")
         _T("Settings are changed in the Control Panel, over the REST API at /api/v1/settings/ini, with hmctl, or from the Control Deck. ")
         _T("To force a value from this file - when the database is unreachable or wrong - put it in a [SettingsOverride] section, which is applied over the store and announced in the error log at every start. ")
         _T("[Directories], [Database] and [Security] are NOT settings: they are how the database is reached and how an administrator is let in without one, and they stay here.");

      String iniFile = IniFileSettings::GetInitializationFile();
      if (iniFile.IsEmpty())
         return;

      const DWORD bufferSize = 4096;
      TCHAR current[bufferSize];

      GetPrivateProfileString(_T("SettingsStore"), _T("Store"), _T(""), current, bufferSize, iniFile.c_str());
      if (kStore.Compare(String(current)) != 0)
         WriteIniValue_(_T("SettingsStore"), _T("Store"), kStore);

      GetPrivateProfileString(_T("SettingsStore"), _T("ReadMe"), _T(""), current, bufferSize, iniFile.c_str());
      if (kReadMe.Compare(String(current)) != 0)
         WriteIniValue_(_T("SettingsStore"), _T("ReadMe"), kReadMe);
   }

   bool
   IniSettingStore::Synchronize(std::map<String, String> &resolvedValues)
   {
      resolvedValues.clear();

      std::map<String, std::pair<String, String> > rows;
      if (!ReadAllRows(rows))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5802, "IniSettingStore::Synchronize",
            "The hm_inisettings table could not be read, so the server is running on the copy of those settings in hMailServer.INI. That copy is written by the server and is normally correct, but the database is the settings store: a change made since the file was last written will not be in it, and nothing can be changed until the table can be read again.");
         return false;
      }

      std::map<String, String> iniValues;
      ReadIniSection_(_T("Settings"), iniValues);

      std::vector<String> edited;
      std::vector<String> adopted;
      std::vector<String> writtenToFile;
      std::vector<String> restoredToFile;
      std::vector<String> unwritable;

      // 1. Everything the file has.
      //
      //    Matched case-sensitively, while Windows treats ini keys case-insensitively.
      //    The one thing that makes visible is an operator who RENAMES a key's case -
      //    AcmeEnabled to acmeenabled - which is seen here as a new key. On a
      //    case-insensitive collation (the default on SQL Server and CE, and what
      //    MySQL uses) the insert below then collides with the row that still holds
      //    the other casing, fails, and is logged; the server runs on the stored
      //    value either way.
      //
      //    Left alone deliberately. Matching case-insensitively here would mean
      //    UPDATE and DELETE statements whose WHERE clause names a different casing
      //    from the stored row, and that is backend-dependent - PostgreSQL compares
      //    case-sensitively where the others do not - so the fix would work on three
      //    backends and silently fail on the fourth.
      for (auto iter = iniValues.begin(); iter != iniValues.end(); iter++)
      {
         const String &name = (*iter).first;
         const String &fileNow = (*iter).second;

         auto row = rows.find(name);

         if (row == rows.end())
         {
            // No row yet. This is THE MIGRATION: on the first start at schema 6042
            // every key the file has that the table does not is taken into the store,
            // which is what moves 238 settings without moving them one at a time. It
            // is also the ordinary path for a fresh install, and for a key an
            // administrator has put in the file for a server that has never stored it.
            if (InsertRow_(name, fileNow, fileNow))
               adopted.push_back(name);

            resolvedValues[name] = fileNow;
            continue;
         }

         const String &storedValue = (*row).second.first;
         const String &storedFileValue = (*row).second.second;

         // The stored value is the answer in every branch below. What the file says
         // decides only what has to be SAID about it, and what has to be written back.
         resolvedValues[name] = storedValue;

         bool fileEdited = fileNow.Compare(storedFileValue) != 0;
         bool fileAgrees = fileNow.Compare(storedValue) == 0;

         if (fileEdited && !fileAgrees)
         {
            // Somebody changed the file and meant something by it. They are told, by
            // name, and the line is put back - once, because after this the file
            // agrees with the store again and the next start says nothing.
            edited.push_back(name);
         }

         if (fileAgrees)
         {
            // The file already holds the stored value; only the bookkeeping can be
            // behind, which is what a hand edit that happened to match leaves.
            if (fileEdited)
               UpdateRow_(name, storedValue, storedValue);

            continue;
         }

         if (WriteIniValue_(_T("Settings"), name, storedValue))
         {
            UpdateRow_(name, storedValue, storedValue);

            if (!fileEdited)
               writtenToFile.push_back(name);
         }
         else
         {
            unwritable.push_back(name);
         }
      }

      // 2. Rows whose key is not in the file.
      //
      //    This used to DELETE the row, because while the file was the store,
      //    removing a key was how a setting was returned to its default and a mirror
      //    that would not let go of a value was worse than no mirror. With the store
      //    in the database that reasoning inverts exactly: the row IS the setting, a
      //    missing line is a cache that has lost an entry, and the line is written
      //    back. Returning a setting to its default is now DeleteIniSetting over COM,
      //    DELETE /api/v1/settings/ini/{name}, or the Control Panel - each of which
      //    drops the row and the line together.
      for (auto iter = rows.begin(); iter != rows.end(); iter++)
      {
         const String &name = (*iter).first;

         if (iniValues.find(name) != iniValues.end())
            continue;

         const String &storedValue = (*iter).second.first;

         if (WriteIniValue_(_T("Settings"), name, storedValue))
         {
            UpdateRow_(name, storedValue, storedValue);
            restoredToFile.push_back(name);
         }
         else
         {
            unwritable.push_back(name);
         }

         resolvedValues[name] = storedValue;
      }

      WriteFileNotice_();

      if (!edited.empty())
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5804, "IniSettingStore::Synchronize",
            Formatter::Format("These settings were edited in hMailServer.INI, but the database is the settings store and the stored value has been used instead: {0}. The file has been put back to what is stored. Change a setting in the Control Panel, over the REST API or with hmctl; if this file has to win - because the database is unreachable or wrong - put the key in a [SettingsOverride] section, which is applied at every start and says so in this log.",
               StringParser::JoinVector(edited, _T(", "))));
      }

      if (!unwritable.empty())
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5803, "IniSettingStore::Synchronize",
            Formatter::Format("These settings could not be written into hMailServer.INI: {0}. The server is using the stored values, so the settings themselves are right, but hMailServer.exe /Register and anything else that reads the file directly will see the old ones until the account the service runs as has write access to it.",
               StringParser::JoinVector(unwritable, _T(", "))));
      }

      if (!adopted.empty())
      {
         LOG_APPLICATION(Formatter::Format("IniSettingStore: {0} setting(s) from the [Settings] section of hMailServer.INI are now stored in the database, which is the settings store.",
            StringParser::IntToString((int) adopted.size())));
      }

      if (!writtenToFile.empty())
      {
         LOG_APPLICATION(Formatter::Format("IniSettingStore: these settings were changed in the database and have been written into hMailServer.INI: {0}.",
            StringParser::JoinVector(writtenToFile, _T(", "))));
      }

      if (!restoredToFile.empty())
      {
         LOG_APPLICATION(Formatter::Format("IniSettingStore: these settings are stored in the database but were missing from hMailServer.INI, and have been written back into it: {0}. Removing a line from the file no longer returns a setting to its default - delete the setting in the Control Panel, or with DELETE /api/v1/settings/ini/(name).",
            StringParser::JoinVector(restoredToFile, _T(", "))));
      }

      return true;
   }

   void
   IniSettingStore::ReadSettingNames(std::vector<String> &names)
   {
      names.clear();

      // A std::set rather than the two maps' keys concatenated: after a start the
      // table and the file hold the same names, and the caller would otherwise be
      // handed every one of them twice.
      std::set<String> united;

      std::map<String, String> values;
      ReadIniSection_(_T("Settings"), values);

      for (auto iter = values.begin(); iter != values.end(); iter++)
         united.insert((*iter).first);

      std::map<String, std::pair<String, String> > rows;

      if (ReadAllRows(rows))
      {
         for (auto iter = rows.begin(); iter != rows.end(); iter++)
            united.insert((*iter).first);
      }

      for (auto iter = united.begin(); iter != united.end(); iter++)
         names.push_back(*iter);
   }

   bool
   IniSettingStore::IsStorableName(const String &name)
   {
      if (name.IsEmpty() || name.GetLength() > 100)
         return false;

      // A name carrying any of these would not come back as the same key. '=' ends
      // the name in an ini file, '[' and ']' would make it look like a section
      // header, and a newline would split it into two lines - each of which is a
      // way to write one setting and silently create another.
      if (name.Find(_T("=")) >= 0 || name.Find(_T("[")) >= 0 || name.Find(_T("]")) >= 0)
         return false;

      if (name.Find(_T("\r")) >= 0 || name.Find(_T("\n")) >= 0)
         return false;

      // Leading or trailing whitespace does not survive a round trip through
      // GetPrivateProfileSection, so a name carrying it would never match again.
      String trimmed = name;
      trimmed.Trim();

      return trimmed.Compare(name) == 0;
   }

   bool
   IniSettingStore::IsStorableValue(const String &value)
   {
      if (value.GetLength() > 4000)
         return false;

      // Same reasoning as the name: a newline in a value writes a second line into
      // the section, which on the next read is an entirely separate setting.
      return value.Find(_T("\r")) < 0 && value.Find(_T("\n")) < 0;
   }

   bool
   IniSettingStore::WriteSetting(const String &name, const String &value)
   {
      if (!IsStorableName(name) || !IsStorableValue(value))
         return false;

      // The store first. See the header for why the order is not an implementation
      // detail, and why it is the opposite of what it was.
      std::map<String, std::pair<String, String> > rows;

      if (!ReadAllRows(rows))
         return false;

      auto existing = rows.find(name);

      // What the file is BELIEVED to hold right now, kept for the failure path
      // below. Empty for a key that has no row, which is also what the file holds
      // for one - no line at all.
      String previousFileValue = existing == rows.end() ? String() : (*existing).second.second;

      bool stored = existing == rows.end()
         ? InsertRow_(name, value, value)
         : UpdateRow_(name, value, value);

      if (!stored)
         return false;

      // The cache second, and a failure here does not fail the write: the value is
      // stored and is what the server will use. Said out loud rather than swallowed,
      // because "it saved, but /Register still sees the old service account" is
      // precisely the half-success this area keeps producing when nobody says
      // anything.
      if (!WriteIniValue_(_T("Settings"), name, value))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5806, "IniSettingStore::WriteSetting",
            Formatter::Format("The setting '{0}' was stored in the database, which is the settings store, but hMailServer.INI could not be written. The setting itself is correct and will be used; hMailServer.exe /Register and anything else reading the file directly will see the old value until the account the service runs as has write access to it.", name));

         // filevalue must go back to what the file really holds, NOT stay at the
         // value the insert or update above optimistically put there. It is the one
         // thing that tells Synchronize an edit from a stale copy, and leaving it
         // claiming this value would have the next start read the file, find the old
         // one, and accuse an administrator of editing a file nobody touched - while
         // doing exactly the right thing about it. With the previous value here the
         // next start sees no edit, writes the line again, and says so quietly.
         UpdateRow_(name, value, previousFileValue);
      }

      return true;
   }

   bool
   IniSettingStore::RemoveSetting(const String &name)
   {
      if (!IsStorableName(name))
         return false;

      // THE LINE FIRST, and this is the one place where the order is the opposite of
      // WriteSetting's - for the same reason the order is what it is there, which is
      // "leave behind whichever residue the next start repairs, never the one it
      // mistakes for an instruction".
      //
      // A leftover ROW is repaired: Synchronize finds a row whose line is missing and
      // writes the line back. A leftover LINE is not - it is a key the table has never
      // seen, which is adopted as a new setting, so a delete that dropped the row and
      // failed to remove the line would put the setting straight back at the next
      // start and report success in the meantime. So the file goes first and a failure
      // there changes nothing at all.
      //
      // Passing a null value is what DELETES the line rather than leaving "Key="
      // behind - and "Key=" is not the same thing at all, because
      // GetPrivateProfileInt reads an empty value as 0 rather than falling back to
      // the caller's default. That distinction has bitten this project twice.
      String iniFile = IniFileSettings::GetInitializationFile();

      if (!iniFile.IsEmpty())
      {
         if (WritePrivateProfileString(_T("Settings"), name.c_str(), nullptr, iniFile.c_str()) == FALSE)
            return false;

         WritePrivateProfileString(nullptr, nullptr, nullptr, iniFile.c_str());
      }

      if (!DeleteRow_(name))
      {
         // The line has gone and the row has not. The setting is NOT back at its
         // default and the caller is told so; the next start finds a row with no line
         // and writes the line back, which is the state this started in.
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5806, "IniSettingStore::RemoveSetting",
            Formatter::Format("The setting '{0}' could not be removed from the database, which is the settings store, so it has not been returned to its default. Its line in hMailServer.INI was removed and will be written back from the stored value at the next start.", name));

         return false;
      }

      return true;
   }

   bool
   IniSettingStore::XMLStore(XNode *pBackupNode)
   {
      // Read the table rather than serialising the in-memory resolved values: a row
      // this build does not recognise - one written by a newer build, or a setting
      // since removed from the code - must still survive a backup and restore. The
      // resolved map only contains keys this build asked for.
      std::map<String, std::pair<String, String> > rows;

      if (!ReadAllRows(rows))
         return false;

      XNode *pNode = pBackupNode->AppendChild(_T("IniSettings"));

      for (auto iter = rows.begin(); iter != rows.end(); iter++)
      {
         // The name goes in an ATTRIBUTE, not in the element name. XMLite escapes
         // attribute values and writes element names raw, and these names come from
         // a file an administrator edits - so a name that is not a valid XML element
         // name would produce an archive that cannot be parsed back.
         XNode *pSetting = pNode->AppendChild(_T("Setting"));
         pSetting->AppendAttr(_T("Name"), (*iter).first);
         pSetting->AppendAttr(_T("Value"), (*iter).second.first);
      }

      return true;
   }

   bool
   IniSettingStore::XMLLoad(XNode *pBackupNode)
   {
      XNode *pNode = pBackupNode->GetChild(_T("IniSettings"));

      // Absent means the archive was taken before this feature existed. Doing
      // nothing is the only safe reading: there is no version stamp on the element
      // to distinguish "old archive" from "no settings", and clearing the table on
      // an old archive would discard the configuration of the server being restored
      // onto.
      if (!pNode)
         return true;

      // Read once and kept up to date as rows are added, rather than re-read inside
      // the loop. A restore of a server with sixty ini settings issued sixty full
      // table reads to answer a question the first one had already answered.
      std::map<String, std::pair<String, String> > rows;

      if (!ReadAllRows(rows))
         return false;

      std::vector<String> writeFailures;

      for (int i = 0; i < pNode->GetChildCount(); i++)
      {
         XNode *pSetting = pNode->GetChild(i);

         String name = pSetting->GetAttrValue(_T("Name"));
         String value = pSetting->GetAttrValue(_T("Value"));

         if (name.IsEmpty())
            continue;

         // Restore is authoritative: the point of restoring settings is to get the
         // backed-up settings. Both the row and the file are written, and the two
         // are recorded as agreed, so the merge on the next start sees no change and
         // does not hand the restored value back to whatever the local file said.
         //
         // Only when the file write SUCCEEDED, though. filevalue means "what the file
         // is known to hold"; recording the restored value there after a write that
         // did not happen would tell the next Synchronize that the two agree, and the
         // restored setting would sit in the table for ever without ever reaching the
         // file that every direct reader - and /Register - actually reads.
         bool written = WriteIniValue_(_T("Settings"), name, value);

         if (!written)
            writeFailures.push_back(name);

         String fileValue = written ? value : String();

         if (rows.find(name) == rows.end())
         {
            if (InsertRow_(name, value, fileValue))
               rows[name] = std::make_pair(value, fileValue);
         }
         else
         {
            if (UpdateRow_(name, value, fileValue))
               rows[name] = std::make_pair(value, fileValue);
         }
      }

      if (!writeFailures.empty())
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5805, "IniSettingStore::XMLLoad",
            Formatter::Format("These settings were restored into the database but could not be written to hMailServer.INI: {0}. The account the service runs as needs write access to that file; until it has, the restored values will be written on a later start rather than lost.",
               StringParser::JoinVector(writeFailures, _T(", "))));
      }

      return true;
   }
}
