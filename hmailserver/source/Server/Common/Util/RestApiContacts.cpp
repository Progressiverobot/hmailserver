// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// The signed-in account's address book: GET and POST /api/v1/me/contacts, PUT
// and DELETE /api/v1/me/contacts/{id}, and the collection of recipients from
// what the account sends through the API. Phase A of the roadmap's address-book
// row - the per-account store; Phase B, the CardDAV address book over the same
// rows, is CardDavServer.cpp.
//
// The store is hm_contacts (schema 6032), read and written through ContactStore
// so that this surface and CardDAV cannot disagree about what a contact is.
// The filtering the completion popup asks for (q=) is done here in memory,
// since a case-insensitive substring match is not spelled the same on the four
// database backends and an address book is small.

#include "StdAfx.h"
#include "RestApiServer.h"
#include "HttpServer.h"
#include "ContactStore.h"
#include "../BO/Account.h"

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
   }

   AnsiString
   RestApiServer::ContactJson_(__int64 id, const String &name, const String &address, int source, const String &created)
   {
      AnsiString json = "{\"id\":" + Int64Text(id) +
         ",\"name\":\"" + JsonEscape_(Utf8_(name)) +
         "\",\"address\":\"" + JsonEscape_(Utf8_(address)) +
         "\",\"source\":\"" + (source == ContactStore::SourceCollected ? AnsiString("collected") : AnsiString("manual")) +
         "\",\"created\":\"" + JsonEscape_(Utf8_(created)) + "\"}";
      return json;
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

      std::vector<ContactRecord> contacts;
      if (!ContactStore::List(account->GetID(), contacts))
         return BuildResponse_(500, "{\"error\":\"the contacts could not be read\"}");

      AnsiString json = "{\"contacts\":[";
      int count = 0;
      int total = 0;
      for (const ContactRecord &contact : contacts)
      {
         bool matches = needle.IsEmpty();
         if (!matches)
         {
            String lowerName = contact.name;
            lowerName.ToLower();
            String lowerAddress = contact.address;
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
               json += ContactJson_(contact.id, contact.name, contact.address, contact.source, contact.created);
               count++;
            }
         }
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
      ContactStore::SplitEntry(JsonUtf8Value_(requestBody, "address"), name, address);
      String givenName = JsonUtf8Value_(requestBody, "name");
      givenName.TrimLeft();
      givenName.TrimRight();
      if (!givenName.IsEmpty())
         name = givenName;
      if (name.GetLength() > ContactStore::MaximumNameLength)
         return BuildResponse_(400, "{\"error\":\"name is at most 255 characters\"}");
      if (!ContactStore::IsValidAddress(address))
         return BuildResponse_(400, "{\"error\":\"address must be one e-mail address\"}");

      __int64 existing = 0;
      if (ContactStore::FindByAddress(account->GetID(), address, existing))
         return BuildResponse_(409, "{\"error\":\"a contact with that address exists\",\"id\":" + Int64Text(existing) + "}");

      ContactRecord inserted;
      if (!ContactStore::Insert(account->GetID(), name, address, ContactStore::SourceManual, inserted))
         return BuildResponse_(500, "{\"error\":\"the contact could not be saved\"}");

      return BuildResponse_(201, ContactJson_(inserted.id, inserted.name, inserted.address, inserted.source, inserted.created));
   }

   HttpResponse
   RestApiServer::HandleMeContactUpdate_(const Caller &caller, __int64 id, const AnsiString &requestBody)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      // The row must be this account's; another account's id is 404, not 403,
      // so that the ids of one address book say nothing about another.
      ContactRecord contact;
      if (!ContactStore::Get(account->GetID(), id, contact))
         return BuildResponse_(404, "{\"error\":\"no such contact\"}");

      String name = contact.name;
      String address = contact.address;

      if (requestBody.Find("\"name\"") >= 0)
      {
         name = JsonUtf8Value_(requestBody, "name");
         name.TrimLeft();
         name.TrimRight();
         if (name.GetLength() > ContactStore::MaximumNameLength)
            return BuildResponse_(400, "{\"error\":\"name is at most 255 characters\"}");
      }

      if (requestBody.Find("\"address\"") >= 0)
      {
         String ignored;
         ContactStore::SplitEntry(JsonUtf8Value_(requestBody, "address"), ignored, address);
         if (!ContactStore::IsValidAddress(address))
            return BuildResponse_(400, "{\"error\":\"address must be one e-mail address\"}");

         __int64 other = 0;
         if (ContactStore::FindByAddress(account->GetID(), address, other) && other != id)
            return BuildResponse_(409, "{\"error\":\"a contact with that address exists\",\"id\":" + Int64Text(other) + "}");
      }

      if (!ContactStore::Update(account->GetID(), id, name, address))
         return BuildResponse_(500, "{\"error\":\"the contact could not be saved\"}");

      return BuildResponse_(200, ContactJson_(id, name, address, contact.source, contact.created));
   }

   HttpResponse
   RestApiServer::HandleMeContactDelete_(const Caller &caller, __int64 id)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      ContactRecord contact;
      if (!ContactStore::Get(account->GetID(), id, contact))
         return BuildResponse_(404, "{\"error\":\"no such contact\"}");

      if (!ContactStore::Delete(account->GetID(), id))
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
         ContactStore::SplitEntry(entry, name, address);
         if (!ContactStore::IsValidAddress(address) || address == own)
            continue;

         __int64 existing = 0;
         if (ContactStore::FindByAddress(account->GetID(), address, existing))
            continue;

         ContactRecord inserted;
         ContactStore::Insert(account->GetID(), name, address, ContactStore::SourceCollected, inserted);
      }
   }
}
