// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// See ContactStore.h. Reads and writes go through the same SQLCommand and
// SQLStatement path as every other table, parameterised.

#include "StdAfx.h"
#include "ContactStore.h"
#include "VCard.h"
#include "Unicode.h"
#include "Time.h"
#include "../SQL/SQLCommand.h"
#include "../SQL/SQLStatement.h"
#include "../SQL/DALRecordset.h"
#include "../Application/Application.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   const int ContactStore::SourceManual;
   const int ContactStore::SourceCollected;
   const int ContactStore::MaximumNameLength;

   namespace
   {
      AnsiString Int64Text(__int64 value)
      {
         AnsiString text;
         text.Format("%I64d", value);
         return text;
      }

      void ReadRecord(std::shared_ptr<DALRecordset> recordset, ContactRecord &contact)
      {
         contact.id = recordset->GetInt64Value("contactid");
         contact.name = recordset->GetStringValue("contactname");
         contact.address = recordset->GetStringValue("contactaddress");
         contact.source = (int) recordset->GetLongValue("contactsource");
         contact.created = recordset->GetStringValue("contactcreated");
         contact.uri = recordset->GetStringValue("contacturi");
         contact.uid = recordset->GetStringValue("contactuid");
         contact.vcard = recordset->GetStringValue("contactvcard");
      }
   }

   bool
   ContactStore::List(__int64 accountId, std::vector<ContactRecord> &contacts)
   {
      contacts.clear();

      SQLCommand command("select contactid, contactname, contactaddress, contactsource, contactcreated, contacturi, contactuid, contactvcard from hm_contacts where contactaccountid = @ACCOUNTID order by contactname asc, contactaddress asc");
      command.AddParameter("@ACCOUNTID", accountId);

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!recordset)
         return false;

      while (!recordset->IsEOF())
      {
         ContactRecord contact;
         ReadRecord(recordset, contact);
         contacts.push_back(contact);
         recordset->MoveNext();
      }

      return true;
   }

   bool
   ContactStore::Get(__int64 accountId, __int64 id, ContactRecord &contact)
   {
      SQLCommand command("select contactid, contactname, contactaddress, contactsource, contactcreated, contacturi, contactuid, contactvcard from hm_contacts where contactid = @ID and contactaccountid = @ACCOUNTID");
      command.AddParameter("@ID", id);
      command.AddParameter("@ACCOUNTID", accountId);

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!recordset || recordset->IsEOF())
         return false;

      ReadRecord(recordset, contact);
      return true;
   }

   bool
   ContactStore::FindByAddress(__int64 accountId, const String &address, __int64 &id)
   {
      id = 0;

      SQLCommand command("select contactid from hm_contacts where contactaccountid = @ACCOUNTID and contactaddress = @ADDRESS");
      command.AddParameter("@ACCOUNTID", accountId);
      command.AddParameter("@ADDRESS", address);

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!recordset || recordset->IsEOF())
         return false;

      id = recordset->GetInt64Value("contactid");
      return id > 0;
   }

   bool
   ContactStore::Insert(__int64 accountId, const String &name, const String &address, int source, ContactRecord &inserted)
   {
      inserted = ContactRecord();
      inserted.name = name;
      inserted.address = address;
      inserted.source = source;
      inserted.created = Time::GetCurrentDateTime();

      SQLStatement statement;
      statement.SetTable("hm_contacts");
      statement.SetStatementType(SQLStatement::STInsert);
      statement.SetIdentityColumn("contactid");
      statement.AddColumnInt64("contactaccountid", accountId);
      statement.AddColumn("contactname", name);
      statement.AddColumn("contactaddress", address);
      statement.AddColumnInt64("contactsource", source);
      statement.AddColumnDate("contactcreated", Time::GetDateFromSystemDate(inserted.created));
      statement.AddColumn("contacturi", String());
      statement.AddColumn("contactuid", String());
      statement.AddColumn("contactvcard", String());

      __int64 id = 0;
      if (!Application::Instance()->GetDBManager()->Execute(statement, &id) || id <= 0)
         return false;

      inserted.id = id;
      return true;
   }

   bool
   ContactStore::Update(__int64 accountId, __int64 id, const String &name, const String &address)
   {
      SQLStatement statement;
      statement.SetTable("hm_contacts");
      statement.SetStatementType(SQLStatement::STUpdate);
      statement.AddColumn("contactname", name);
      statement.AddColumn("contactaddress", address);

      // A card a client stored for this contact follows the change: the name
      // and address the webmail wrote become its FN, N and preferred EMAIL, and
      // everything else in it stays. Left as it was, the next sync would hand
      // the client a card that contradicts the row.
      ContactRecord current;
      if (Get(accountId, id, current) && !current.vcard.IsEmpty())
      {
         AnsiString card, uid, nameUtf8, addressUtf8;
         Unicode::WideToMultiByte(current.vcard, card);
         Unicode::WideToMultiByte(current.uid, uid);
         Unicode::WideToMultiByte(name, nameUtf8);
         Unicode::WideToMultiByte(address, addressUtf8);

         String rewritten;
         if (!Unicode::MultiByteToWide(VCard::WithNameAndAddress(card, uid, nameUtf8, addressUtf8), rewritten))
            rewritten = String();
         statement.AddColumn("contactvcard", rewritten);
      }

      statement.SetWhereClause("contactid = " + Int64Text(id) + " and contactaccountid = " + Int64Text(accountId));

      return Application::Instance()->GetDBManager()->Execute(statement);
   }

   bool
   ContactStore::FindByUri(__int64 accountId, const String &uri, ContactRecord &contact)
   {
      // contacturi is nvarchar(255): a longer name from a request path cannot exist.
      if (uri.IsEmpty() || uri.GetLength() > 255)
         return false;

      SQLCommand command("select contactid, contactname, contactaddress, contactsource, contactcreated, contacturi, contactuid, contactvcard from hm_contacts where contactaccountid = @ACCOUNTID and contacturi = @URI");
      command.AddParameter("@ACCOUNTID", accountId);
      command.AddParameter("@URI", uri);

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!recordset || recordset->IsEOF())
         return false;

      ReadRecord(recordset, contact);
      return true;
   }

   bool
   ContactStore::InsertCard(__int64 accountId, const String &name, const String &address, const String &uri,
                            const String &uid, const String &vcard, ContactRecord &inserted)
   {
      inserted = ContactRecord();
      inserted.name = name;
      inserted.address = address;
      inserted.source = SourceManual;
      inserted.created = Time::GetCurrentDateTime();
      inserted.uri = uri;
      inserted.uid = uid;
      inserted.vcard = vcard;

      SQLStatement statement;
      statement.SetTable("hm_contacts");
      statement.SetStatementType(SQLStatement::STInsert);
      statement.SetIdentityColumn("contactid");
      statement.AddColumnInt64("contactaccountid", accountId);
      statement.AddColumn("contactname", name);
      statement.AddColumn("contactaddress", address);
      statement.AddColumnInt64("contactsource", SourceManual);
      statement.AddColumnDate("contactcreated", Time::GetDateFromSystemDate(inserted.created));
      statement.AddColumn("contacturi", uri);
      statement.AddColumn("contactuid", uid);
      statement.AddColumn("contactvcard", vcard);

      __int64 id = 0;
      if (!Application::Instance()->GetDBManager()->Execute(statement, &id) || id <= 0)
         return false;

      inserted.id = id;
      return true;
   }

   bool
   ContactStore::UpdateCard(__int64 accountId, __int64 id, const String &name, const String &address,
                            const String &uid, const String &vcard)
   {
      SQLStatement statement;
      statement.SetTable("hm_contacts");
      statement.SetStatementType(SQLStatement::STUpdate);
      statement.AddColumn("contactname", name);
      statement.AddColumn("contactaddress", address);
      statement.AddColumn("contactuid", uid);
      statement.AddColumn("contactvcard", vcard);
      statement.SetWhereClause("contactid = " + Int64Text(id) + " and contactaccountid = " + Int64Text(accountId));

      return Application::Instance()->GetDBManager()->Execute(statement);
   }

   bool
   ContactStore::Delete(__int64 accountId, __int64 id)
   {
      SQLCommand select("select contactid from hm_contacts where contactid = @ID and contactaccountid = @ACCOUNTID");
      select.AddParameter("@ID", id);
      select.AddParameter("@ACCOUNTID", accountId);
      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(select);
      if (!recordset || recordset->IsEOF())
         return false;

      SQLCommand command("delete from hm_contacts where contactid = @ID and contactaccountid = @ACCOUNTID");
      command.AddParameter("@ID", id);
      command.AddParameter("@ACCOUNTID", accountId);
      return Application::Instance()->GetDBManager()->Execute(command);
   }

   bool
   ContactStore::IsValidAddress(const String &address)
   {
      if (address.IsEmpty() || address.GetLength() > 255)
         return false;

      int at = address.Find(_T("@"));
      if (at <= 0 || at == address.GetLength() - 1)
         return false;

      if (address.Find(_T("@"), at + 1) >= 0)
         return false;

      for (int i = 0; i < address.GetLength(); i++)
      {
         wchar_t c = address[i];
         if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '<' || c == '>' || c == ',' || c == ';' || c == '"')
            return false;
      }

      return true;
   }

   void
   ContactStore::SplitEntry(const String &entry, String &name, String &address)
   {
      String text = entry;
      text.TrimLeft();
      text.TrimRight();
      name = _T("");
      address = _T("");

      int open = text.Find(_T("<"));
      int close = text.Find(_T(">"));
      if (open >= 0 && close > open)
      {
         address = text.Mid(open + 1, close - open - 1);
         name = text.Mid(0, open);
         name.TrimLeft();
         name.TrimRight();
         if (name.GetLength() >= 2 && name.StartsWith(_T("\"")) && name.EndsWith(_T("\"")))
            name = name.Mid(1, name.GetLength() - 2);
      }
      else
      {
         address = text;
      }

      address.TrimLeft();
      address.TrimRight();
      address.ToLower();
   }
}
