// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#include "stdafx.h"
#include "IMAPMetadataStore.h"

#include "../Common/Application/Application.h"
#include "../Common/SQL/SQLCommand.h"
#include "../Common/SQL/DALRecordset.h"
#include "../Common/SQL/DatabaseConnectionManager.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   bool
   IMAPMetadataStore::Get(__int64 accountId, __int64 folderId, const String &entryName, String &value, bool &found)
   {
      found = false;

      SQLCommand command("select metadatavalue from hm_imap_metadata "
                         "where metadataaccountid = @ACCOUNTID and metadatafolderid = @FOLDERID and metadataentryname = @ENTRYNAME");
      command.AddParameter("@ACCOUNTID", accountId);
      command.AddParameter("@FOLDERID", folderId);
      command.AddParameter("@ENTRYNAME", entryName);

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!recordset)
         return false;

      if (!recordset->IsEOF())
      {
         value = recordset->GetStringValue("metadatavalue");
         found = true;
      }

      return true;
   }

   std::vector<IMAPMetadataStore::Entry>
   IMAPMetadataStore::List(__int64 accountId, __int64 folderId, const String &entryName, int depth)
   {
      std::vector<Entry> result;

      // The entry itself plus, for depth 1 or infinity, everything under it.
      // Fetched by prefix and depth-filtered in code: SQL LIKE has no notion of
      // "one path segment", and the table is small.
      SQLCommand command("select metadataentryname, metadatavalue from hm_imap_metadata "
                         "where metadataaccountid = @ACCOUNTID and metadatafolderid = @FOLDERID "
                         "order by metadataentryname asc");
      command.AddParameter("@ACCOUNTID", accountId);
      command.AddParameter("@FOLDERID", folderId);

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!recordset)
         return result;

      String prefix = entryName + _T("/");

      while (!recordset->IsEOF())
      {
         String name = recordset->GetStringValue("metadataentryname");

         bool matches = false;

         if (name.CompareNoCase(entryName) == 0)
            matches = true;
         else if (depth != 0 && name.GetLength() > prefix.GetLength() &&
                  name.Mid(0, prefix.GetLength()).CompareNoCase(prefix) == 0)
         {
            if (depth < 0)
               matches = true;   // infinity
            else
            {
               // Depth 1: no further '/' after the prefix.
               String remainder = name.Mid(prefix.GetLength());
               matches = remainder.Find(_T("/")) < 0;
            }
         }

         if (matches)
         {
            Entry entry;
            entry.name = name;
            entry.value = recordset->GetStringValue("metadatavalue");
            result.push_back(entry);
         }

         recordset->MoveNext();
      }

      return result;
   }

   bool
   IMAPMetadataStore::Set(__int64 accountId, __int64 folderId, const String &entryName, const String &value)
   {
      // Delete-then-insert rather than dialect-specific upsert syntax: the same
      // two statements run on all four backends, and the DAL's Execute does not
      // report affected rows, which an update-then-insert would need. The
      // uniqueness constraint makes the race harmless - a concurrent second
      // writer's insert fails, which reports as a failed SETMETADATA: honest,
      // if unlucky.
      if (!Remove(accountId, folderId, entryName))
         return false;

      SQLCommand insert("insert into hm_imap_metadata (metadataaccountid, metadatafolderid, metadataentryname, metadatavalue) "
                        "values (@ACCOUNTID, @FOLDERID, @ENTRYNAME, @VALUE)");
      insert.AddParameter("@ACCOUNTID", accountId);
      insert.AddParameter("@FOLDERID", folderId);
      insert.AddParameter("@ENTRYNAME", entryName);
      insert.AddParameter("@VALUE", value);

      return Application::Instance()->GetDBManager()->Execute(insert);
   }

   bool
   IMAPMetadataStore::Remove(__int64 accountId, __int64 folderId, const String &entryName)
   {
      SQLCommand command("delete from hm_imap_metadata "
                         "where metadataaccountid = @ACCOUNTID and metadatafolderid = @FOLDERID and metadataentryname = @ENTRYNAME");
      command.AddParameter("@ACCOUNTID", accountId);
      command.AddParameter("@FOLDERID", folderId);
      command.AddParameter("@ENTRYNAME", entryName);

      return Application::Instance()->GetDBManager()->Execute(command);
   }
}
