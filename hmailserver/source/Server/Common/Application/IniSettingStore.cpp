// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "IniSettingStore.h"

#include "IniFileSettings.h"
#include "../SQL/SQLCommand.h"
#include "../SQL/SQLStatement.h"
#include "../SQL/DALRecordset.h"
#include "../Util/XMLite.h"

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
   IniSettingStore::ReadIniSection_(std::map<String, String> &values)
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
         copied = GetPrivateProfileSection(_T("Settings"), &buffer[0], size, iniFile.c_str());

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
               Formatter::Format("The hMailServer.INI setting '{0}' has a name longer than 100 characters, so it cannot be stored in the database and will not be included in backups. Shorten the name or remove the setting.", name));
            continue;
         }

         if (value.GetLength() > 4000)
         {
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5801, "IniSettingStore::ReadIniSection_",
               Formatter::Format("The hMailServer.INI setting '{0}' has a value longer than 4000 characters, so it cannot be stored in the database and will not be included in backups.", name));
            continue;
         }

         values[name] = value;
      }
   }

   bool
   IniSettingStore::ReadAllRows(std::map<String, std::pair<String, String> > &rows)
   {
      rows.clear();

      SQLCommand command("select inisettingname, inisettingvalue, inisettingfilevalue from hm_inisettings");

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);

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
      SQLStatement statement(SQLStatement::STInsert, "hm_inisettings");
      statement.AddColumn("inisettingname", name);
      statement.AddColumn("inisettingvalue", value);
      statement.AddColumn("inisettingfilevalue", fileValue);

      return Application::Instance()->GetDBManager()->Execute(statement);
   }

   bool
   IniSettingStore::UpdateRow_(const String &name, const String &value, const String &fileValue)
   {
      SQLCommand command("update hm_inisettings set inisettingvalue = @VALUE, inisettingfilevalue = @FILEVALUE where inisettingname = @NAME");
      command.AddParameter("@VALUE", value);
      command.AddParameter("@FILEVALUE", fileValue);
      command.AddParameter("@NAME", name);

      return Application::Instance()->GetDBManager()->Execute(command);
   }

   bool
   IniSettingStore::DeleteRow_(const String &name)
   {
      SQLCommand command("delete from hm_inisettings where inisettingname = @NAME");
      command.AddParameter("@NAME", name);

      return Application::Instance()->GetDBManager()->Execute(command);
   }

   bool
   IniSettingStore::WriteIniValue_(const String &name, const String &value)
   {
      String iniFile = IniFileSettings::GetInitializationFile();
      if (iniFile.IsEmpty())
         return false;

      if (WritePrivateProfileString(_T("Settings"), name.c_str(), value.c_str(), iniFile.c_str()) == FALSE)
         return false;

      // WritePrivateProfileString caches; flush so that anything reading the file
      // afterwards - including this process's own GetPrivateProfileString calls -
      // sees the value that was just written.
      WritePrivateProfileString(nullptr, nullptr, nullptr, iniFile.c_str());

      return true;
   }

   bool
   IniSettingStore::Synchronize(std::map<String, String> &resolvedValues)
   {
      resolvedValues.clear();

      std::map<String, std::pair<String, String> > rows;
      if (!ReadAllRows(rows))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5802, "IniSettingStore::Synchronize",
            "The hm_inisettings table could not be read. The server is running on the settings in hMailServer.INI alone, and those settings are not being included in backups until this is fixed.");
         return false;
      }

      std::map<String, String> iniValues;
      ReadIniSection_(iniValues);

      std::vector<String> conflicts;
      std::vector<String> adopted;
      std::vector<String> pushedToFile;
      std::vector<String> removedWithFile;

      // 1. Everything the file has.
      //
      //    Matched case-sensitively, while Windows treats ini keys case-insensitively.
      //    The one thing that makes visible is an operator who RENAMES a key's case -
      //    AcmeEnabled to acmeenabled - which is seen here as a new key and in step 2
      //    as a removed one. On a case-insensitive collation (the default on SQL
      //    Server and CE, and what MySQL uses) the insert below then collides with the
      //    row step 2 has not deleted yet, fails, and is logged; the server still runs
      //    on the right value, because resolvedValues is set from the file either way,
      //    and the next start inserts cleanly because the old row has gone by then.
      //
      //    Left alone deliberately. Matching case-insensitively here would mean
      //    UPDATE and DELETE statements whose WHERE clause names a different casing
      //    from the stored row, and that is backend-dependent - PostgreSQL compares
      //    case-sensitively where the others do not - so the fix would work on three
      //    backends and silently fail on the fourth. A self-healing collision on a
      //    rare manual edit is the better of the two.
      for (auto iter = iniValues.begin(); iter != iniValues.end(); iter++)
      {
         const String &name = (*iter).first;
         const String &fileNow = (*iter).second;

         auto row = rows.find(name);

         if (row == rows.end())
         {
            // No row yet. This is the ordinary path on the first start after the
            // upgrade, and for every setting added to the file since.
            if (InsertRow_(name, fileNow, fileNow))
               adopted.push_back(name);

            resolvedValues[name] = fileNow;
            continue;
         }

         const String &storedValue = (*row).second.first;
         const String &storedFileValue = (*row).second.second;

         bool fileChanged = fileNow.Compare(storedFileValue) != 0;
         bool rowChanged = storedValue.Compare(storedFileValue) != 0;

         if (fileChanged)
         {
            // The file wins. It is the copy the operator can see and the one every
            // direct reader still uses.
            if (rowChanged)
               conflicts.push_back(name);

            UpdateRow_(name, fileNow, fileNow);
            resolvedValues[name] = fileNow;
         }
         else if (rowChanged)
         {
            // The row was changed while the file was not - a remote change, or a
            // restore. Push it into the file, and only record the two as agreed if
            // that write actually happened.
            if (WriteIniValue_(name, storedValue))
            {
               UpdateRow_(name, storedValue, storedValue);
               pushedToFile.push_back(name);
               resolvedValues[name] = storedValue;
            }
            else
            {
               ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5803, "IniSettingStore::Synchronize",
                  Formatter::Format("The setting '{0}' was changed in the database but could not be written back to hMailServer.INI, so the change has not been applied. The account the service runs as needs write access to that file.", name));

               // The file is still the truth for this run, because it is what every
               // direct reader will see.
               resolvedValues[name] = fileNow;
            }
         }
         else
         {
            resolvedValues[name] = storedValue;
         }
      }

      // 2. Rows whose key is no longer in the file. Two very different things look
      //    identical here, and filevalue is what tells them apart.
      //
      //    This resurrected the row unconditionally to begin with, on the reasoning
      //    that a row is the last copy and losing it would lose it from the backup
      //    too. That was wrong, and the regression suite proved it in the loudest
      //    way available: a test that switched ACME on, restarted, then cleaned up
      //    by DELETING its keys from the file had them written straight back, so
      //    AcmeEnabled=1 survived into every following test and 394 of them failed.
      //
      //    Removing a key is a normal way to return a setting to its default -
      //    for the harness and for an administrator with a text editor - and a
      //    mirror that will not let go of a value is worse than no mirror.
      for (auto iter = rows.begin(); iter != rows.end(); iter++)
      {
         const String &name = (*iter).first;

         if (iniValues.find(name) != iniValues.end())
            continue;

         const String &storedValue = (*iter).second.first;
         const String &storedFileValue = (*iter).second.second;

         if (storedValue.Compare(storedFileValue) == 0)
         {
            // The row still agrees with what the file said last time, so nothing
            // has changed on the database side and the file's deletion is the newer
            // fact. Let it go.
            DeleteRow_(name);
            removedWithFile.push_back(name);
            continue;
         }

         // The row was changed since the two last agreed, so this is a database-side
         // addition or edit rather than a file-side deletion - which is exactly what
         // a remote administrator or a restore produces. Put it in the file.
         if (WriteIniValue_(name, storedValue))
         {
            UpdateRow_(name, storedValue, storedValue);
            pushedToFile.push_back(name);
         }

         resolvedValues[name] = storedValue;
      }

      if (!conflicts.empty())
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5804, "IniSettingStore::Synchronize",
            Formatter::Format("These settings were changed both in hMailServer.INI and in the database since the last start, so the value in the file has been used and the database updated to match: {0}. If the database value was the one you wanted, set it again.",
               StringParser::JoinVector(conflicts, _T(", "))));
      }

      if (!adopted.empty())
      {
         LOG_APPLICATION(Formatter::Format("IniSettingStore: {0} setting(s) from hMailServer.INI are now stored in the database and included in backups.",
            StringParser::IntToString((int) adopted.size())));
      }

      if (!pushedToFile.empty())
      {
         LOG_APPLICATION(Formatter::Format("IniSettingStore: these settings were changed in the database and have been written into hMailServer.INI: {0}.",
            StringParser::JoinVector(pushedToFile, _T(", "))));
      }

      if (!removedWithFile.empty())
      {
         LOG_APPLICATION(Formatter::Format("IniSettingStore: these settings were removed from hMailServer.INI and have been dropped from the database to match, so they are back at their defaults: {0}.",
            StringParser::JoinVector(removedWithFile, _T(", "))));
      }

      return true;
   }

   bool
   IniSettingStore::Save(const String &key, const String &value)
   {
      std::map<String, std::pair<String, String> > rows;

      if (!ReadAllRows(rows))
         return false;

      if (rows.find(key) == rows.end())
         return InsertRow_(key, value, value);

      return UpdateRow_(key, value, value);
   }

   void
   IniSettingStore::ReadSettingNames(std::vector<String> &names)
   {
      names.clear();

      std::map<String, String> values;
      ReadIniSection_(values);

      for (auto iter = values.begin(); iter != values.end(); iter++)
         names.push_back((*iter).first);
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

      // File first. See the header for why the order is not an implementation
      // detail.
      if (!WriteIniValue_(name, value))
         return false;

      std::map<String, std::pair<String, String> > rows;

      if (!ReadAllRows(rows))
      {
         // The file now holds the new value and the mirror does not know. Not
         // failed - the server is running on what the file says, which is the copy
         // that decides behaviour - but it is out of step until the next start,
         // where Synchronize will adopt it. Said out loud rather than swallowed,
         // because "it saved but is not in the backup" is precisely the kind of
         // half-success this whole feature exists to remove.
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5806, "IniSettingStore::WriteSetting",
            Formatter::Format("The setting '{0}' was written to hMailServer.INI but the hm_inisettings table could not be read, so the change is not mirrored yet. It will be picked up on the next server start.", name));

         return true;
      }

      if (rows.find(name) == rows.end())
         InsertRow_(name, value, value);
      else
         UpdateRow_(name, value, value);

      return true;
   }

   bool
   IniSettingStore::RemoveSetting(const String &name)
   {
      if (!IsStorableName(name))
         return false;

      // Passing a null value is what DELETES the line rather than leaving "Key="
      // behind - and "Key=" is not the same thing at all, because
      // GetPrivateProfileInt reads an empty value as 0 rather than falling back to
      // the caller's default. That distinction has bitten this project twice.
      String iniFile = IniFileSettings::GetInitializationFile();

      if (iniFile.IsEmpty())
         return false;

      if (WritePrivateProfileString(_T("Settings"), name.c_str(), nullptr, iniFile.c_str()) == FALSE)
         return false;

      WritePrivateProfileString(nullptr, nullptr, nullptr, iniFile.c_str());

      DeleteRow_(name);

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
         bool written = WriteIniValue_(name, value);

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
