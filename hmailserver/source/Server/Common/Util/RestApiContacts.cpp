// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// The signed-in account's address book: GET and POST /api/v1/me/contacts, PUT
// and DELETE /api/v1/me/contacts/{id}, and the collection of recipients from
// what the account sends through the API. Phase A of the roadmap's address-book
// row - a per-account store, no CardDAV yet; Phase B is CardDAV over this table.
//
// The store is hm_contacts (schema 6032): one row per account and address, with
// a name, a source (0 = added by the user, 1 = collected from a message the
// account sent) and a creation time. Reads and writes go through the same
// SQLCommand/SQLStatement path as every other table, parameterised; the
// filtering the completion popup asks for (q=) is done here in memory, since a
// case-insensitive substring match is not spelled the same on the four database
// backends and an address book is small.

#include "StdAfx.h"
#include "RestApiServer.h"
#include "HttpServer.h"
#include "Time.h"
#include "../BO/Account.h"
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
   namespace
   {
      const int ContactListDefaultLimit = 200;
      const int ContactListMaximumLimit = 1000;

      AnsiString Int64Text(__int64 value)
      {
         AnsiString text;
         text.Format("%I64d", value);
         return text;
      }

      // "Name <address>", "<address>" or "address" -> the name (may be empty) and
      // the address, lower-cased, without the brackets.
      void SplitContactEntry(const String &entry, String &name, String &address)
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

      // One address, with a local part and a domain, no whitespace or line
      // breaks, and short enough for the column.
      bool ValidContactAddress(const String &address)
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
   }

   AnsiString
   RestApiServer::ContactJson_(__int64 id, const String &name, const String &address, int source, const String &created)
   {
      AnsiString json = "{\"id\":" + Int64Text(id) +
         ",\"name\":\"" + JsonEscape_(Utf8_(name)) +
         "\",\"address\":\"" + JsonEscape_(Utf8_(address)) +
         "\",\"source\":\"" + (source == 1 ? AnsiString("collected") : AnsiString("manual")) +
         "\",\"created\":\"" + JsonEscape_(Utf8_(created)) + "\"}";
      return json;
   }

   // Whether the account already has this address, and its row id if so.
   bool
   RestApiServer::FindContact_(__int64 accountId, const String &address, __int64 &contactId)
   {
      contactId = 0;

      SQLCommand command("select contactid from hm_contacts where contactaccountid = @ACCOUNTID and contactaddress = @ADDRESS");
      command.AddParameter("@ACCOUNTID", accountId);
      command.AddParameter("@ADDRESS", address);

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!recordset || recordset->IsEOF())
         return false;

      contactId = recordset->GetInt64Value("contactid");
      return contactId > 0;
   }

   bool
   RestApiServer::InsertContact_(__int64 accountId, const String &name, const String &address, int source, __int64 &contactId, String &created)
   {
      created = Time::GetCurrentDateTime();

      SQLStatement statement;
      statement.SetTable("hm_contacts");
      statement.SetStatementType(SQLStatement::STInsert);
      statement.SetIdentityColumn("contactid");
      statement.AddColumnInt64("contactaccountid", accountId);
      statement.AddColumn("contactname", name);
      statement.AddColumn("contactaddress", address);
      statement.AddColumnInt64("contactsource", source);
      statement.AddColumnDate("contactcreated", Time::GetDateFromSystemDate(created));

      contactId = 0;
      return Application::Instance()->GetDBManager()->Execute(statement, &contactId) && contactId > 0;
   }

   HttpResponse
   RestApiServer::HandleMeContacts_(const Caller &caller, const AnsiString &query)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      // q= narrows to names and addresses containing the text, case-insensitively
      // (the completion popup's use); limit= caps the answer, 200 by default.
      String needle = String(QueryParameter_(query, "q"));
      needle.ToLower();
      int limit = ContactListDefaultLimit;
      AnsiString limitText = QueryParameter_(query, "limit");
      if (!limitText.IsEmpty())
      {
         limit = atoi(limitText.c_str());
         if (limit < 1)
            limit = 1;
         if (limit > ContactListMaximumLimit)
            limit = ContactListMaximumLimit;
      }

      SQLCommand command("select contactid, contactname, contactaddress, contactsource, contactcreated from hm_contacts where contactaccountid = @ACCOUNTID order by contactname asc, contactaddress asc");
      command.AddParameter("@ACCOUNTID", account->GetID());

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!recordset)
         return BuildResponse_(500, "{\"error\":\"the contacts could not be read\"}");

      AnsiString json = "{\"contacts\":[";
      int count = 0;
      int total = 0;
      while (!recordset->IsEOF())
      {
         String name = recordset->GetStringValue("contactname");
         String address = recordset->GetStringValue("contactaddress");
         bool matches = needle.IsEmpty();
         if (!matches)
         {
            String lowerName = name;
            lowerName.ToLower();
            String lowerAddress = address;
            lowerAddress.ToLower();
            matches = lowerName.Find(needle) >= 0 || lowerAddress.Find(needle) >= 0;
         }

         if (matches)
         {
            total++;
            if (count < limit)
            {
               if (count > 0)
                  json += ",";
               json += ContactJson_(recordset->GetInt64Value("contactid"), name, address,
                                    (int) recordset->GetLongValue("contactsource"),
                                    recordset->GetStringValue("contactcreated"));
               count++;
            }
         }

         recordset->MoveNext();
      }

      json += "],\"count\":" + Int64Text(count) + ",\"total\":" + Int64Text(total) + "}";
      return BuildResponse_(200, json);
   }

   HttpResponse
   RestApiServer::HandleMeContactCreate_(const Caller &caller, const AnsiString &requestBody)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      String name;
      String address;
      SplitContactEntry(JsonUtf8Value_(requestBody, "address"), name, address);
      String givenName = JsonUtf8Value_(requestBody, "name");
      givenName.TrimLeft();
      givenName.TrimRight();
      if (!givenName.IsEmpty())
         name = givenName;
      if (name.GetLength() > 255)
         return BuildResponse_(400, "{\"error\":\"name is at most 255 characters\"}");
      if (!ValidContactAddress(address))
         return BuildResponse_(400, "{\"error\":\"address must be one e-mail address\"}");

      __int64 existing = 0;
      if (FindContact_(account->GetID(), address, existing))
         return BuildResponse_(409, "{\"error\":\"a contact with that address exists\",\"id\":" + Int64Text(existing) + "}");

      __int64 id = 0;
      String created;
      if (!InsertContact_(account->GetID(), name, address, 0, id, created))
         return BuildResponse_(500, "{\"error\":\"the contact could not be saved\"}");

      return BuildResponse_(201, ContactJson_(id, name, address, 0, created));
   }

   HttpResponse
   RestApiServer::HandleMeContactUpdate_(const Caller &caller, __int64 id, const AnsiString &requestBody)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      // The row must be this account's; another account's id is 404, not 403,
      // so that the ids of one address book say nothing about another.
      SQLCommand select("select contactname, contactaddress, contactsource, contactcreated from hm_contacts where contactid = @ID and contactaccountid = @ACCOUNTID");
      select.AddParameter("@ID", id);
      select.AddParameter("@ACCOUNTID", account->GetID());
      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(select);
      if (!recordset || recordset->IsEOF())
         return BuildResponse_(404, "{\"error\":\"no such contact\"}");

      String name = recordset->GetStringValue("contactname");
      String address = recordset->GetStringValue("contactaddress");
      int source = (int) recordset->GetLongValue("contactsource");
      String created = recordset->GetStringValue("contactcreated");

      if (requestBody.Find("\"name\"") >= 0)
      {
         name = JsonUtf8Value_(requestBody, "name");
         name.TrimLeft();
         name.TrimRight();
         if (name.GetLength() > 255)
            return BuildResponse_(400, "{\"error\":\"name is at most 255 characters\"}");
      }

      if (requestBody.Find("\"address\"") >= 0)
      {
         String ignored;
         SplitContactEntry(JsonUtf8Value_(requestBody, "address"), ignored, address);
         if (!ValidContactAddress(address))
            return BuildResponse_(400, "{\"error\":\"address must be one e-mail address\"}");

         __int64 other = 0;
         if (FindContact_(account->GetID(), address, other) && other != id)
            return BuildResponse_(409, "{\"error\":\"a contact with that address exists\",\"id\":" + Int64Text(other) + "}");
      }

      SQLStatement statement;
      statement.SetTable("hm_contacts");
      statement.SetStatementType(SQLStatement::STUpdate);
      statement.AddColumn("contactname", name);
      statement.AddColumn("contactaddress", address);
      statement.SetWhereClause("contactid = " + Int64Text(id) + " and contactaccountid = " + Int64Text(account->GetID()));
      if (!Application::Instance()->GetDBManager()->Execute(statement))
         return BuildResponse_(500, "{\"error\":\"the contact could not be saved\"}");

      return BuildResponse_(200, ContactJson_(id, name, address, source, created));
   }

   HttpResponse
   RestApiServer::HandleMeContactDelete_(const Caller &caller, __int64 id)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      SQLCommand select("select contactid from hm_contacts where contactid = @ID and contactaccountid = @ACCOUNTID");
      select.AddParameter("@ID", id);
      select.AddParameter("@ACCOUNTID", account->GetID());
      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(select);
      if (!recordset || recordset->IsEOF())
         return BuildResponse_(404, "{\"error\":\"no such contact\"}");

      SQLCommand command("delete from hm_contacts where contactid = @ID and contactaccountid = @ACCOUNTID");
      command.AddParameter("@ID", id);
      command.AddParameter("@ACCOUNTID", account->GetID());
      if (!Application::Instance()->GetDBManager()->Execute(command))
         return BuildResponse_(500, "{\"error\":\"the contact could not be deleted\"}");

      return BuildResponse_(200, "{\"deleted\":true}");
   }

   // Every recipient of a message the account sent becomes a contact, once:
   // the address the user typed, with the display name if there was one. The
   // account's own address is not collected, and nothing here can fail the
   // send that called it.
   void
   RestApiServer::CollectContacts_(std::shared_ptr<const Account> account, const std::vector<String> &entries)
   {
      if (!account)
         return;

      String own = account->GetAddress();
      own.ToLower();

      for (const String &entry : entries)
      {
         String name;
         String address;
         SplitContactEntry(entry, name, address);
         if (!ValidContactAddress(address) || address == own)
            continue;

         __int64 existing = 0;
         if (FindContact_(account->GetID(), address, existing))
            continue;

         __int64 id = 0;
         String created;
         InsertContact_(account->GetID(), name, address, 1, id, created);
      }
   }
}
