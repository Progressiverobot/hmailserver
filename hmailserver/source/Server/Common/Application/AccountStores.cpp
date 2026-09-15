// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "AccountStores.h"

#include "../BO/Account.h"
#include "../Util/Encoding/Base64.h"
#include "../Util/Time.h"
#include "../Util/Unicode.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   // THE DECLARATION. Everything in this file is driven by the two arrays below,
   // and build/check-account-stores.py reads them against the CREATE TABLE scripts.
   //
   // To add a store: add its table to stores_, add EVERY one of its columns to
   // columns_ with the role each plays, and add a test to
   // RegressionTests/Infrastructure/BackupAccountStores.cs that makes a row, takes a
   // backup, deletes the domain, restores and finds the row again. The check refuses
   // a table with a foreign key to hm_accounts - or any table with an account-id
   // column - that is named in neither stores_ nor elsewhere_, and refuses a column
   // of a declared store that is missing from columns_.
   //
   // To decide NOT to carry a store, put it in elsewhere_ with the reason. That is
   // the honest form of the decision; leaving it out altogether is how this went
   // wrong the first time.

   const AccountStores::Store AccountStores::stores_[] =
   {
      // The address book (schema 6032, extended for CardDAV at 6040). Read and
      // written by the webmail's /api/v1/me/contacts and by the CardDAV address
      // book; ContactStore owns the SQL.
      { _T("hm_contacts"), 0 },

      // The webmail's remembered choices (schema 6033). The server attaches no
      // meaning to a key, which is exactly why it must be carried verbatim.
      { _T("hm_accountprefs"), 0 },

      // What the account has put off: a draft to be sent at a time, a message
      // snoozed until one (schema 6034). Its two references are carried as a
      // folder path and a UID - see RoleMessage.
      { _T("hm_scheduled"), 0 },

      // Large attachments sent as links (schema 6035). The bytes live under
      // <data directory>/Files/<token> and are already inside the archive's
      // message store; this is the record that gives them a name, an expiry and
      // an owner. Without it the bytes come back and nothing can reach them.
      { _T("hm_files"), 0 },

      // S/MIME certificates and keys (schema 6039). The private key is stored as
      // the page wrapped it - AES-256-GCM under a key derived from the account
      // password, which this server cannot open - so losing it in a restore means
      // the account can never read its own encrypted mail again.
      { _T("hm_smimekeys"), 0 },

      // The password reuse history (schema 6019). Hashes, like the account
      // password beside them. A restore that dropped these would silently let
      // every account go back to a password it had been made to change.
      { _T("hm_passwordhistory"), 0 },

      // The calendar (schema 6041), and its objects under it. The objects are a
      // child store because their row points at the calendar rather than at the
      // account, and the calendar's identity is reassigned by the restore.
      { _T("hm_calendars"), 0 },
      { _T("hm_calendarobjects"), _T("hm_calendars") },

      // Who has written to the account before (schema 6044): the memory the
      // first-contact note is decided against. Losing it loses no mail, which is
      // what makes it easy to leave out - but a restore that dropped it would put
      // every account back to the cold start DomainTransforms.md describes, and
      // announce every correspondent it has as a first contact until each of them
      // had written again.
      { _T("hm_knownsenders"), 0 },
   };

   const AccountStores::Column AccountStores::columns_[] =
   {
      { _T("hm_contacts"), _T("contactid"),          AccountStores::RoleIdentity },
      { _T("hm_contacts"), _T("contactaccountid"),   AccountStores::RoleAccount },
      { _T("hm_contacts"), _T("contactname"),        AccountStores::RoleText },
      { _T("hm_contacts"), _T("contactaddress"),     AccountStores::RoleText },
      { _T("hm_contacts"), _T("contactsource"),      AccountStores::RoleNumber },
      { _T("hm_contacts"), _T("contactcreated"),     AccountStores::RoleTimestamp },
      { _T("hm_contacts"), _T("contacturi"),         AccountStores::RoleText },
      { _T("hm_contacts"), _T("contactuid"),         AccountStores::RoleText },
      { _T("hm_contacts"), _T("contactvcard"),       AccountStores::RoleText },

      { _T("hm_accountprefs"), _T("prefid"),         AccountStores::RoleIdentity },
      { _T("hm_accountprefs"), _T("prefaccountid"),  AccountStores::RoleAccount },
      { _T("hm_accountprefs"), _T("prefname"),       AccountStores::RoleText },
      { _T("hm_accountprefs"), _T("prefvalue"),      AccountStores::RoleText },

      { _T("hm_scheduled"), _T("schedid"),           AccountStores::RoleIdentity },
      { _T("hm_scheduled"), _T("schedaccountid"),    AccountStores::RoleAccount },
      { _T("hm_scheduled"), _T("schedmessageid"),    AccountStores::RoleMessage },
      { _T("hm_scheduled"), _T("schedaction"),       AccountStores::RoleNumber },
      { _T("hm_scheduled"), _T("schedat"),           AccountStores::RoleTimestamp },
      { _T("hm_scheduled"), _T("schedfolderid"),     AccountStores::RoleFolder },
      { _T("hm_scheduled"), _T("schedcreated"),      AccountStores::RoleTimestamp },

      { _T("hm_files"), _T("fileid"),                AccountStores::RoleIdentity },
      { _T("hm_files"), _T("fileaccountid"),         AccountStores::RoleAccount },
      { _T("hm_files"), _T("filetoken"),             AccountStores::RoleText },
      { _T("hm_files"), _T("filename"),              AccountStores::RoleText },
      { _T("hm_files"), _T("filetype"),              AccountStores::RoleText },
      { _T("hm_files"), _T("filesize"),              AccountStores::RoleNumber },
      { _T("hm_files"), _T("filestored"),            AccountStores::RoleNumber },
      { _T("hm_files"), _T("filecomplete"),          AccountStores::RoleNumber },
      { _T("hm_files"), _T("filecreated"),           AccountStores::RoleNumber },
      { _T("hm_files"), _T("fileexpires"),           AccountStores::RoleNumber },
      { _T("hm_files"), _T("filepasswordhash"),      AccountStores::RoleText },
      { _T("hm_files"), _T("filedownloads"),         AccountStores::RoleNumber },

      { _T("hm_smimekeys"), _T("smimeid"),           AccountStores::RoleIdentity },
      { _T("hm_smimekeys"), _T("smimeaccountid"),    AccountStores::RoleAccount },
      { _T("hm_smimekeys"), _T("smimekind"),         AccountStores::RoleNumber },
      { _T("hm_smimekeys"), _T("smimeaddress"),      AccountStores::RoleText },
      { _T("hm_smimekeys"), _T("smimename"),         AccountStores::RoleText },
      { _T("hm_smimekeys"), _T("smimefingerprint"),  AccountStores::RoleText },
      { _T("hm_smimekeys"), _T("smimecertificate"),  AccountStores::RoleText },
      { _T("hm_smimekeys"), _T("smimechain"),        AccountStores::RoleText },
      { _T("hm_smimekeys"), _T("smimekey"),          AccountStores::RoleText },
      { _T("hm_smimekeys"), _T("smimenotafter"),     AccountStores::RoleNumber },
      { _T("hm_smimekeys"), _T("smimecreated"),      AccountStores::RoleNumber },

      { _T("hm_passwordhistory"), _T("phid"),        AccountStores::RoleIdentity },
      { _T("hm_passwordhistory"), _T("phaccountid"), AccountStores::RoleAccount },
      { _T("hm_passwordhistory"), _T("phhash"),      AccountStores::RoleText },
      { _T("hm_passwordhistory"), _T("phencryption"),AccountStores::RoleNumber },
      { _T("hm_passwordhistory"), _T("phchanged"),   AccountStores::RoleTimestamp },

      { _T("hm_calendars"), _T("calendarid"),          AccountStores::RoleIdentity },
      { _T("hm_calendars"), _T("calendaraccountid"),   AccountStores::RoleAccount },
      { _T("hm_calendars"), _T("calendarname"),        AccountStores::RoleText },
      { _T("hm_calendars"), _T("calendardisplayname"), AccountStores::RoleText },
      { _T("hm_calendars"), _T("calendarsynctoken"),   AccountStores::RoleNumber },
      { _T("hm_calendars"), _T("calendarcreated"),     AccountStores::RoleNumber },

      { _T("hm_calendarobjects"), _T("objectid"),         AccountStores::RoleIdentity },
      { _T("hm_calendarobjects"), _T("objectaccountid"),  AccountStores::RoleAccount },
      { _T("hm_calendarobjects"), _T("objectcalendarid"), AccountStores::RoleParent },
      { _T("hm_calendarobjects"), _T("objecturi"),        AccountStores::RoleText },
      { _T("hm_calendarobjects"), _T("objectuid"),        AccountStores::RoleText },
      { _T("hm_calendarobjects"), _T("objectcomponent"),  AccountStores::RoleText },
      { _T("hm_calendarobjects"), _T("objectdata"),       AccountStores::RoleText },
      { _T("hm_calendarobjects"), _T("objectetag"),       AccountStores::RoleText },
      { _T("hm_calendarobjects"), _T("objectsynctoken"),  AccountStores::RoleNumber },
      { _T("hm_calendarobjects"), _T("objectstart"),      AccountStores::RoleNumber },
      { _T("hm_calendarobjects"), _T("objectend"),        AccountStores::RoleNumber },
      { _T("hm_calendarobjects"), _T("objectfirst"),      AccountStores::RoleNumber },
      { _T("hm_calendarobjects"), _T("objectlast"),       AccountStores::RoleNumber },
      { _T("hm_calendarobjects"), _T("objectdeleted"),    AccountStores::RoleNumber },
      { _T("hm_calendarobjects"), _T("objectmodified"),   AccountStores::RoleNumber },

      // The two times are RoleText, not RoleTimestamp, because they are not
      // datetime columns: nvarchar(32) on SQL Server and SQL Server Compact,
      // varchar(32) on MySQL and PostgreSQL, holding the string
      // Time::GetCurrentDateTime() produced when PersistentKnownSender wrote the
      // row. RoleText carries that string exactly as the database holds it.
      // RoleTimestamp is for a column each backend renders its own way: it would
      // parse the value, write it back re-formatted, and put the time of the
      // restore in place of any value it could not parse.
      { _T("hm_knownsenders"), _T("ksid"),           AccountStores::RoleIdentity },
      { _T("hm_knownsenders"), _T("ksaccountid"),    AccountStores::RoleAccount },
      { _T("hm_knownsenders"), _T("ksaddress"),      AccountStores::RoleText },
      { _T("hm_knownsenders"), _T("kscount"),        AccountStores::RoleNumber },
      { _T("hm_knownsenders"), _T("ksfirstseen"),    AccountStores::RoleText },
      { _T("hm_knownsenders"), _T("kslastseen"),     AccountStores::RoleText },
   };

   // Per-account tables this file does NOT carry, and why. Every one of them is
   // still in the backup or is still correct to leave out of it; what is not
   // allowed is a table that is in neither list, because that is silence.
   const AccountStores::Elsewhere AccountStores::elsewhere_[] =
   {
      { _T("hm_fetchaccounts"),
        _T("FetchAccount::XMLStore writes it, under the account.") },
      { _T("hm_fetchaccounts_uids"),
        _T("FetchAccountUID::XMLStore writes it, under its fetch account.") },
      { _T("hm_apppasswords"),
        _T("AppPassword::XMLStore writes it, under the account.") },
      { _T("hm_rules"),
        _T("Rule::XMLStore writes it, under the account; its criteria and actions with it.") },
      { _T("hm_rule_criterias"),
        _T("RuleCriteria::XMLStore writes it, under its rule.") },
      { _T("hm_rule_actions"),
        _T("RuleAction::XMLStore writes it, under its rule.") },
      { _T("hm_imapfolders"),
        _T("IMAPFolder::XMLStore writes it, under the account, when messages are backed up.") },
      { _T("hm_messages"),
        _T("Message::XMLStore writes it, under its folder, when messages are backed up.") },
      { _T("hm_messagerecipients"),
        _T("The recipients of a message still in the delivery queue. A queued message is in flight, not in a mailbox; the backup carries mailboxes.") },
      { _T("hm_message_metadata"),
        _T("Per-message annotations (RFC 5257), keyed on a message id the restore reassigns. Not carried: the roadmap row is what would carry them, with the message.") },
      { _T("hm_imapexpunged"),
        _T("The expunge log a QRESYNC client is answered from. Restoring it would tell a client that messages the restore has just put back are gone.") },
      { _T("hm_imap_metadata"),
        _T("Per-mailbox annotations (RFC 5464), keyed on a folder id the restore reassigns. As above.") },
      { _T("hm_acl"),
        _T("ACLPermission::XMLStore writes it, under its public folder.") },
      { _T("hm_group_members"),
        _T("GroupMember::XMLStore writes it, under its group, in the settings section.") },
      { _T("hm_messageindexterms"),
        _T("The full-text index: derived from the messages themselves and rebuilt by the indexer, so carrying it would double the archive to save work a background task does.") },
      { _T("hm_messageindexstate"),
        _T("How far the indexer has read. Derived, as above; a restored server indexes from the beginning.") },
   };

   const int AccountStores::store_count_ = (int) (sizeof(stores_) / sizeof(stores_[0]));
   const int AccountStores::column_count_ = (int) (sizeof(columns_) / sizeof(columns_[0]));
   const int AccountStores::elsewhere_count_ = (int) (sizeof(elsewhere_) / sizeof(elsewhere_[0]));

   namespace
   {
      // The element the stores hang under, the element one store is, and the
      // element one row is. Named once so the writer and the reader cannot
      // disagree.
      const wchar_t *StoresElement = _T("AccountStores");
      const wchar_t *StoreElement = _T("Store");
      const wchar_t *RowElement = _T("Row");
      const wchar_t *ReferenceElement = _T("Ref");
      const wchar_t *PathElement = _T("Path");
      const wchar_t *NameAttribute = _T("Name");
      const wchar_t *ColumnAttribute = _T("Column");
      const wchar_t *UidAttribute = _T("UID");

      // Appended to a column's name when the value could not be an attribute as it
      // stands. See AccountStores.h.
      const wchar_t *EncodedSuffix = _T(".base64");

      // A folder tree is at most IMAPFolder::MaxFolderDepth deep, and the walk up
      // from a folder to the root is bounded by the same number - not because a
      // deeper tree is expected but because a parent chain that pointed at itself
      // would otherwise be an infinite loop inside a backup.
      const int MaximumPathDepth = 25;

      AnsiString AnsiOf_(const String &value)
      {
         // The column and table names here are ASCII literals from the arrays
         // above, so this is a widen-to-narrow of known-safe characters rather
         // than a character set conversion.
         AnsiString result;
         result.reserve((size_t) value.GetLength());

         for (int index = 0; index < value.GetLength(); index++)
            result += (char) value.SafeGetAt((unsigned int) index);

         return result;
      }

      bool NeedsEncoding_(const String &value)
      //---------------------------------------------------------------------------()
      // DESCRIPTION:
      // Whether this value has to be base64 to cross an XML attribute intact.
      //
      // Two different reasons, both of which produce an archive that cannot be read
      // back rather than a value that comes back wrong:
      //
      //  * XML 1.0 has no representation at all for most control characters. A
      //    single 0x0B in a vCard - and a vCard is whatever a client sent - makes
      //    the whole index unparseable, which loses every account after it.
      //  * A conforming XML parser replaces a literal tab, carriage return or line
      //    feed in an attribute value with a space. The parser here does not, but
      //    an archive whose fidelity depends on that is one nobody may ever read
      //    with anything else, and an iCalendar object is all line breaks.
      //---------------------------------------------------------------------------()
      {
         for (int index = 0; index < value.GetLength(); index++)
         {
            wchar_t character = value.SafeGetAt((unsigned int) index);

            if (character < 0x20 || character == 0x7f)
               return true;
         }

         return false;
      }

      void WriteValue_(XNode *row, const String &column, const String &value)
      {
         if (!NeedsEncoding_(value))
         {
            row->AppendAttr(column.c_str(), value.c_str());
            return;
         }

         AnsiString utf8;
         Unicode::WideToMultiByte(value, utf8);

         String encoded = Base64::Encode(utf8.c_str(), (int) utf8.GetLength());
         String name = column + EncodedSuffix;

         row->AppendAttr(name.c_str(), encoded.c_str());
      }

      bool ReadValue_(XNode *row, const String &column, String &value)
      {
         LPXAttr plain = row->GetAttr(column.c_str());

         if (plain)
         {
            value = plain->value;
            return true;
         }

         String name = column + EncodedSuffix;
         LPXAttr encoded = row->GetAttr(name.c_str());

         if (!encoded)
            return false;

         AnsiString bytes = Base64::Decode(AnsiOf_(encoded->value).c_str(), (int) encoded->value.GetLength());

         value.Empty();
         Unicode::MultiByteToWide(bytes, value);

         return true;
      }

      __int64 ReadNumber_(XNode *row, const String &column)
      {
         String value;

         if (!ReadValue_(row, column, value))
            return 0;

         return _ttoi64(value.c_str());
      }

      // A folder's path, root first, read from the database rather than from any
      // in-memory tree: this runs during a backup, where the account's folders may
      // not have been loaded at all (BackupMessages off), and a folder the tree
      // does not happen to hold is not the same as a folder that does not exist.
      bool FolderPath_(__int64 accountId, __int64 folderId, std::vector<String> &path)
      {
         __int64 current = folderId;

         for (int depth = 0; depth < MaximumPathDepth; depth++)
         {
            if (current <= 0)
               return false;

            SQLCommand command("select foldername, folderparentid from hm_imapfolders where folderid = @FOLDERID and folderaccountid = @ACCOUNTID");
            command.AddParameter("@FOLDERID", current);
            command.AddParameter("@ACCOUNTID", accountId);

            std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);

            if (!recordset || recordset->IsEOF())
               return false;

            path.insert(path.begin(), recordset->GetStringValue("foldername"));

            __int64 parent = recordset->GetInt64Value("folderparentid");

            // A top-level folder's parent is recorded as -1.
            if (parent <= 0)
               return true;

            current = parent;
         }

         return false;
      }

      // The folder and UID of one of the account's messages. False when the
      // message is no longer there, which on a running server is mail flow rather
      // than an error.
      bool MessageReference_(__int64 accountId, __int64 messageId, std::vector<String> &path, unsigned int &uid)
      {
         SQLCommand command("select messagefolderid, messageuid from hm_messages where messageid = @MESSAGEID and messageaccountid = @ACCOUNTID");
         command.AddParameter("@MESSAGEID", messageId);
         command.AddParameter("@ACCOUNTID", accountId);

         std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);

         if (!recordset || recordset->IsEOF())
            return false;

         __int64 folderId = recordset->GetInt64Value("messagefolderid");
         uid = (unsigned int) recordset->GetInt64Value("messageuid");

         if (folderId <= 0 || uid == 0)
            return false;

         return FolderPath_(accountId, folderId, path);
      }

      void WritePath_(XNode *reference, const std::vector<String> &path)
      {
         for (size_t segment = 0; segment < path.size(); segment++)
         {
            XNode *node = reference->AppendChild(PathElement);
            WriteValue_(node, NameAttribute, path[segment]);
         }
      }

      bool ReadPath_(XNode *reference, std::vector<String> &path)
      {
         for (int index = 0; index < reference->GetChildCount(); index++)
         {
            XNode *child = reference->GetChild((unsigned int) index);

            if (child->name != PathElement)
               continue;

            String segment;

            if (!ReadValue_(child, NameAttribute, segment))
               return false;

            path.push_back(segment);
         }

         return !path.empty();
      }

      XNode *FindReference_(XNode *row, const String &column)
      {
         for (int index = 0; index < row->GetChildCount(); index++)
         {
            XNode *child = row->GetChild((unsigned int) index);

            if (child->name != ReferenceElement)
               continue;

            if (child->GetAttrValue(ColumnAttribute) == column)
               return child;
         }

         return 0;
      }

      // The account's folder at this path, or 0 when there is none.
      //
      // Read from the database rather than from the account's in-memory folder tree
      // or the shared message cache, for the same reason the writer above does: the
      // restore has just inserted these rows, the comparison is against the exact
      // string the backup read out of this column, and a lookup that went through
      // the caches would depend on whether a folder had been loaded into one.
      __int64 FolderAtPath_(__int64 accountId, const std::vector<String> &path)
      {
         // Every top-level folder's parent is recorded as -1, not 0.
         __int64 parent = -1;
         __int64 current = 0;

         for (size_t index = 0; index < path.size(); index++)
         {
            SQLCommand command("select folderid from hm_imapfolders where folderaccountid = @ACCOUNTID and folderparentid = @PARENTID and foldername = @NAME");
            command.AddParameter("@ACCOUNTID", accountId);
            command.AddParameter("@PARENTID", parent);
            command.AddParameter("@NAME", path[index]);

            std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);

            if (!recordset || recordset->IsEOF())
               return 0;

            current = recordset->GetInt64Value("folderid");

            if (current <= 0)
               return 0;

            parent = current;
         }

         return current;
      }

      // The account's message with this UID in this folder, or 0.
      __int64 MessageWithUid_(__int64 accountId, __int64 folderId, unsigned int uid)
      {
         SQLCommand command("select messageid from hm_messages where messageaccountid = @ACCOUNTID and messagefolderid = @FOLDERID and messageuid = @UID");
         command.AddParameter("@ACCOUNTID", accountId);
         command.AddParameter("@FOLDERID", folderId);
         command.AddParameter("@UID", (__int64) uid);

         std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);

         if (!recordset || recordset->IsEOF())
            return 0;

         return recordset->GetInt64Value("messageid");
      }
   }

   // One table, flattened out of the two arrays above.
   struct AccountStoreTable
   {
      AccountStoreTable() :
         columns(0),
         column_count(0)
      {
      }

      String table;
      String parent;

      String identity_column;
      String account_column;
      String parent_column;

      // The columns of this table, in declaration order, which is the order the
      // SELECT names them in.
      const AccountStores::Column *columns;
      int column_count;

      // The SELECT list, built once.
      String select_columns;
   };

   // One row, read out of the database before any of it is written, so that no
   // recordset is open while the XML is built or while a reference is resolved.
   struct AccountStoreRow
   {
      AccountStoreRow() :
         identity(0)
      {
      }

      __int64 identity;

      // Aligned with the table's columns: text holds the value of a text or
      // timestamp column, numbers the value of every other kind.
      std::vector<String> text;
      std::vector<__int64> numbers;
   };

   namespace
   {
      std::vector<AccountStoreTable> BuildTables_()
      {
         std::vector<AccountStoreTable> tables;

         int storeCount = 0;
         const AccountStores::Store *stores = AccountStores::Stores(storeCount);

         int columnCount = 0;
         const AccountStores::Column *columns = AccountStores::Columns(columnCount);

         for (int storeIndex = 0; storeIndex < storeCount; storeIndex++)
         {
            AccountStoreTable table;
            table.table = stores[storeIndex].table;
            table.parent = stores[storeIndex].parent == 0 ? String() : String(stores[storeIndex].parent);

            for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
               if (table.table != columns[columnIndex].table)
                  continue;

               if (table.columns == 0)
                  table.columns = &columns[columnIndex];

               table.column_count++;

               if (!table.select_columns.IsEmpty())
                  table.select_columns += _T(", ");

               table.select_columns += columns[columnIndex].name;

               switch (columns[columnIndex].role)
               {
               case AccountStores::RoleIdentity:
                  table.identity_column = columns[columnIndex].name;
                  break;
               case AccountStores::RoleAccount:
                  table.account_column = columns[columnIndex].name;
                  break;
               case AccountStores::RoleParent:
                  table.parent_column = columns[columnIndex].name;
                  break;
               default:
                  break;
               }
            }

            tables.push_back(table);
         }

         return tables;
      }

      const std::vector<AccountStoreTable> &Tables_()
      {
         // Built once, on first use, by the one-time initialisation the language
         // guarantees for a function-local static - rather than a "fill it if it is
         // empty" that two threads could enter at once.
         static const std::vector<AccountStoreTable> tables = BuildTables_();

         return tables;
      }

      const AccountStoreTable *TableNamed_(const String &name)
      {
         const std::vector<AccountStoreTable> &tables = Tables_();

         for (size_t index = 0; index < tables.size(); index++)
         {
            if (tables[index].table == name)
               return &tables[index];
         }

         return 0;
      }

      bool ReadRows_(const AccountStoreTable &table, const String &keyColumn, __int64 keyValue,
                     std::vector<AccountStoreRow> &rows, String &failure)
      //---------------------------------------------------------------------------()
      // DESCRIPTION:
      // Every row of one store belonging to one key, read out in a single query and
      // into memory. Nothing is held open afterwards: the caller may then query
      // again (a child store, a message reference) without a recordset of this one
      // still being alive, and a row deleted after this returns is simply a row this
      // backup was taken before.
      //
      // A recordset that could not be opened is a FAILURE and says so. That
      // distinction is the whole reason this is not written the way the collection
      // Refresh methods are: those return void, so a query that did not run is
      // indistinguishable from a store that is empty, and an account whose address
      // book "is empty" is exactly the archive this change exists to stop.
      //---------------------------------------------------------------------------()
      {
         String query = _T("select ") + table.select_columns + _T(" from ") + table.table +
                        _T(" where ") + keyColumn + _T(" = @KEY order by ") + table.identity_column;

         SQLCommand command(query);
         command.AddParameter("@KEY", keyValue);

         std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);

         if (!recordset)
         {
            failure = table.table + _T(" could not be read.");
            return false;
         }

         while (!recordset->IsEOF())
         {
            AccountStoreRow row;
            row.text.resize((size_t) table.column_count);
            row.numbers.resize((size_t) table.column_count);

            for (int index = 0; index < table.column_count; index++)
            {
               AnsiString name = AnsiOf_(table.columns[index].name);

               switch (table.columns[index].role)
               {
               case AccountStores::RoleText:
               case AccountStores::RoleTimestamp:
                  row.text[(size_t) index] = recordset->GetStringValue(name);
                  break;
               default:
                  row.numbers[(size_t) index] = recordset->GetInt64Value(name);
                  break;
               }

               if (table.columns[index].role == AccountStores::RoleIdentity)
                  row.identity = row.numbers[(size_t) index];
            }

            rows.push_back(row);

            recordset->MoveNext();
         }

         return true;
      }

      bool StoreTable_(__int64 accountId, const AccountStoreTable &table, const String &keyColumn, __int64 keyValue,
                       XNode *parentNode, unsigned int &dropped, String &failure)
      {
         std::vector<AccountStoreRow> rows;

         if (!ReadRows_(table, keyColumn, keyValue, rows, failure))
            return false;

         if (rows.empty())
            return true;

         XNode *storeNode = parentNode->AppendChild(StoreElement);
         storeNode->AppendAttr(NameAttribute, table.table.c_str());

         // The store whose rows hang under each of these, if there is one.
         const AccountStoreTable *child = 0;
         const std::vector<AccountStoreTable> &tables = Tables_();

         for (size_t index = 0; index < tables.size(); index++)
         {
            if (tables[index].parent == table.table)
               child = &tables[index];
         }

         for (size_t rowIndex = 0; rowIndex < rows.size(); rowIndex++)
         {
            const AccountStoreRow &row = rows[rowIndex];

            // Appended first and removed again if a reference in it does not
            // resolve, rather than built detached: AppendChild is the only thing
            // that stamps a node with its document, and RemoveChild is what deletes
            // one - so a row that is dropped leaves nothing half-written behind and
            // a row that is kept is indistinguishable from any other.
            XNode *rowNode = storeNode->AppendChild(RowElement);
            bool complete = true;

            for (int index = 0; complete && index < table.column_count; index++)
            {
               const AccountStores::Column &column = table.columns[index];
               String name = column.name;

               switch (column.role)
               {
               case AccountStores::RoleIdentity:
               case AccountStores::RoleAccount:
               case AccountStores::RoleParent:
                  break;

               case AccountStores::RoleText:
               case AccountStores::RoleTimestamp:
                  WriteValue_(rowNode, name, row.text[(size_t) index]);
                  break;

               case AccountStores::RoleNumber:
                  rowNode->AppendAttr(name.c_str(), StringParser::IntToString(row.numbers[(size_t) index]).c_str());
                  break;

               case AccountStores::RoleMessage:
                  {
                     std::vector<String> path;
                     unsigned int uid = 0;

                     if (!MessageReference_(accountId, row.numbers[(size_t) index], path, uid))
                     {
                        complete = false;
                        break;
                     }

                     XNode *reference = rowNode->AppendChild(ReferenceElement);
                     reference->AppendAttr(ColumnAttribute, name.c_str());
                     reference->AppendAttr(UidAttribute, StringParser::IntToString(uid).c_str());
                     WritePath_(reference, path);
                  }
                  break;

               case AccountStores::RoleFolder:
                  {
                     std::vector<String> path;

                     if (!FolderPath_(accountId, row.numbers[(size_t) index], path))
                     {
                        complete = false;
                        break;
                     }

                     XNode *reference = rowNode->AppendChild(ReferenceElement);
                     reference->AppendAttr(ColumnAttribute, name.c_str());
                     WritePath_(reference, path);
                  }
                  break;
               }
            }

            if (!complete)
            {
               storeNode->RemoveChild(rowNode);
               dropped++;
               continue;
            }

            if (child && !StoreTable_(accountId, *child, child->parent_column, row.identity, rowNode, dropped, failure))
               return false;
         }

         return true;
      }

      bool LoadTable_(Account &account, const AccountStoreTable &table, __int64 parentId, XNode *storeNode,
                      unsigned int &dropped)
      {
         for (int rowIndex = 0; rowIndex < storeNode->GetChildCount(); rowIndex++)
         {
            XNode *rowNode = storeNode->GetChild((unsigned int) rowIndex);

            if (rowNode->name != RowElement)
               continue;

            SQLStatement statement;
            statement.SetTable(table.table);
            statement.SetStatementType(SQLStatement::STInsert);
            statement.SetIdentityColumn(table.identity_column);

            bool complete = true;

            for (int index = 0; complete && index < table.column_count; index++)
            {
               const AccountStores::Column &column = table.columns[index];
               String name = column.name;

               switch (column.role)
               {
               case AccountStores::RoleIdentity:
                  break;

               case AccountStores::RoleAccount:
                  // From the account being restored, never from the archive: an
                  // archive cannot be made to write a row against somebody else.
                  statement.AddColumnInt64(name, account.GetID());
                  break;

               case AccountStores::RoleParent:
                  statement.AddColumnInt64(name, parentId);
                  break;

               case AccountStores::RoleText:
                  {
                     String value;
                     ReadValue_(rowNode, name, value);
                     statement.AddColumn(name, value);
                  }
                  break;

               case AccountStores::RoleNumber:
                  statement.AddColumnInt64(name, ReadNumber_(rowNode, name));
                  break;

               case AccountStores::RoleTimestamp:
                  {
                     String value;
                     ReadValue_(rowNode, name, value);

                     // Every one of these columns is NOT NULL, and AddColumnDate
                     // writes NULL for a date it cannot parse - so an archive with
                     // an empty or unreadable timestamp would fail the insert on a
                     // constraint rather than say anything useful. The row's own
                     // time is lost either way; the row is not.
                     if (!Time::IsValidSystemDate(value))
                        value = Time::GetCurrentDateTime();

                     statement.AddColumnDate(name, Time::GetDateFromSystemDate(value));
                  }
                  break;

               case AccountStores::RoleMessage:
                  {
                     XNode *reference = FindReference_(rowNode, name);
                     std::vector<String> path;

                     if (!reference || !ReadPath_(reference, path))
                     {
                        complete = false;
                        break;
                     }

                     __int64 folderId = FolderAtPath_(account.GetID(), path);

                     if (folderId <= 0)
                     {
                        complete = false;
                        break;
                     }

                     unsigned int uid = (unsigned int) _ttoi64(reference->GetAttrValue(UidAttribute).c_str());
                     __int64 messageId = MessageWithUid_(account.GetID(), folderId, uid);

                     if (messageId <= 0)
                     {
                        complete = false;
                        break;
                     }

                     statement.AddColumnInt64(name, messageId);
                  }
                  break;

               case AccountStores::RoleFolder:
                  {
                     XNode *reference = FindReference_(rowNode, name);
                     std::vector<String> path;

                     if (!reference || !ReadPath_(reference, path))
                     {
                        complete = false;
                        break;
                     }

                     __int64 folderId = FolderAtPath_(account.GetID(), path);

                     if (folderId <= 0)
                     {
                        complete = false;
                        break;
                     }

                     statement.AddColumnInt64(name, folderId);
                  }
                  break;
               }
            }

            if (!complete)
            {
               // A row naming a message or a folder this restore has not put back.
               // Writing it anyway would point a scheduled send at whatever message
               // is given that id next, which is the one outcome worse than losing
               // the row.
               dropped++;
               continue;
            }

            __int64 identity = 0;

            if (!Application::Instance()->GetDBManager()->Execute(statement, &identity))
            {
               ErrorManager::Instance()->ReportError(ErrorManager::High, 6128, "AccountStores::XMLLoad",
                  Formatter::Format("A row of {0} from the backup could not be written for account {1}.",
                     table.table, account.GetAddress()));

               return false;
            }

            // Its children, under the identity this insert was given.
            for (int childIndex = 0; childIndex < rowNode->GetChildCount(); childIndex++)
            {
               XNode *childNode = rowNode->GetChild((unsigned int) childIndex);

               if (childNode->name != StoreElement)
                  continue;

               const AccountStoreTable *child = TableNamed_(childNode->GetAttrValue(NameAttribute));

               if (!child)
               {
                  Logger::Instance()->LogBackup(Formatter::Format(
                     "The backup carries rows for {0} under {1}, which this version of hMailServer has no table for. They have not been restored.",
                     childNode->GetAttrValue(NameAttribute), table.table));
                  continue;
               }

               if (!LoadTable_(account, *child, identity, childNode, dropped))
                  return false;
            }
         }

         return true;
      }
   }

   const AccountStores::Store *
   AccountStores::Stores(int &count)
   {
      count = store_count_;
      return stores_;
   }

   const AccountStores::Column *
   AccountStores::Columns(int &count)
   {
      count = column_count_;
      return columns_;
   }

   const AccountStores::Elsewhere *
   AccountStores::Handled(int &count)
   {
      count = elsewhere_count_;
      return elsewhere_;
   }

   bool
   AccountStores::XMLStore(Account &account, XNode *accountNode)
   {
      const std::vector<AccountStoreTable> &tables = Tables_();

      // Removed again if no store had a row, so that the archive an account with
      // none of these produces is the one it produced before this change - which is
      // what keeps two backups of the same server comparable across it.
      XNode *storesNode = accountNode->AppendChild(StoresElement);
      unsigned int dropped = 0;

      for (size_t index = 0; index < tables.size(); index++)
      {
         // A child store is read inside its parent's rows, never on its own.
         if (!tables[index].parent.IsEmpty())
            continue;

         String failure;

         if (!StoreTable_(account.GetID(), tables[index], tables[index].account_column, account.GetID(),
                          storesNode, dropped, failure))
         {
            accountNode->RemoveChild(storesNode);

            Logger::Instance()->LogBackup("Backing up " + account.GetAddress() + " failed: " + failure +
               " No archive will be written; the previous backups have not been touched.");

            return false;
         }
      }

      if (storesNode->GetChildCount() == 0)
         accountNode->RemoveChild(storesNode);

      if (dropped > 0)
      {
         Logger::Instance()->LogBackup(Formatter::Format(
            "{0} row(s) of {1}'s scheduled mail name a message or folder that is no longer there and are not in this backup. They were deleted from the mailbox before or while the backup ran.",
            dropped, account.GetAddress()));
      }

      return true;
   }

   bool
   AccountStores::XMLLoad(Account &account, XNode *accountNode)
   {
      XNode *storesNode = accountNode->GetChild(StoresElement);

      // An archive written before this existed. Nothing to restore, and nothing to
      // say about it: that is what restoring one has always done.
      if (!storesNode)
         return true;

      unsigned int dropped = 0;

      for (int index = 0; index < storesNode->GetChildCount(); index++)
      {
         XNode *storeNode = storesNode->GetChild((unsigned int) index);

         if (storeNode->name != StoreElement)
            continue;

         String name = storeNode->GetAttrValue(NameAttribute);
         const AccountStoreTable *table = TableNamed_(name);

         if (!table)
         {
            // The version gate in BackupRestorer has already refused an archive
            // from a newer hMailServer, so this is a hand-edited archive or one
            // whose version could not be parsed. Said plainly and left alone:
            // refusing the whole restore would cost the administrator their mail
            // to protect rows nothing here could use.
            Logger::Instance()->LogBackup(Formatter::Format(
               "The backup carries rows for {0}, which this version of hMailServer has no table for. They have not been restored.", name));
            continue;
         }

         if (!table->parent.IsEmpty())
         {
            // A child store belongs inside its parent's row, where the identity it
            // points at is known. This writer never puts one here, so an archive
            // that does is one somebody has edited - and writing the rows anyway
            // would mean writing a parent id of zero, which the foreign key would
            // refuse and which would fail the whole restore over a hand-edit.
            Logger::Instance()->LogBackup(Formatter::Format(
               "The backup carries rows for {0} outside the {1} they belong to, which this server cannot place. They have not been restored.",
               name, table->parent));
            continue;
         }

         if (!LoadTable_(account, *table, 0, storeNode, dropped))
            return false;
      }

      if (dropped > 0)
      {
         Logger::Instance()->LogBackup(Formatter::Format(
            "{0} scheduled item(s) of {1} named a message or folder this restore did not put back and have not been restored. Nothing else in the account is affected.",
            dropped, account.GetAddress()));
      }

      return true;
   }

   void
   AccountStores::CountRows(XNode *node, std::map<String, __int64> &counts)
   {
      if (!node)
         return;

      if (node->name == StoreElement)
      {
         String name = node->GetAttrValue(NameAttribute);
         __int64 rows = 0;

         for (int index = 0; index < node->GetChildCount(); index++)
         {
            if (node->GetChild((unsigned int) index)->name == RowElement)
               rows++;
         }

         counts[name] += rows;
      }

      for (size_t index = 0; index < node->childs.size(); index++)
         CountRows(node->childs[index], counts);
   }
}
