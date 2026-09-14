// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// An account's resources under its address, administered: what Account.AppPasswords does over COM. See RestApiServer.h.
//
// The /api/v1/me routes let an account holder manage their own mailbox. An
// administrator - the Control Panel, a provisioning script, the Linux
// regression suite driving the fixtures' COM calls over HTTP - reaches the
// same things through the account's address, exactly as the fetch accounts
// are reached, so that a key restricted to named domains is scoped by the
// address in the path (Authorize_) and nothing here is reachable by an id
// alone:
//
//    GET    /api/v1/accounts/{address}/app-passwords         the account's app passwords
//    POST   /api/v1/accounts/{address}/app-passwords         make one; the clear text is in the answer and nowhere else
//    DELETE /api/v1/accounts/{address}/app-passwords/{id}    remove one
//
// Each handler does what the COM member does with the same persistence call
// and the same checks. A create is InterfaceAppPasswords::Add, then
// InterfaceAppPassword::Generate (or SetPassword, when the body chooses the
// secret: at least twelve characters, and acceptable to the password policy
// exactly as an account password must be), then Save, which is
// PersistentAppPassword::SaveObject - where the name, the hash, the created
// time and the ceiling of twenty per account are judged. A delete goes
// through the account's collection as InterfaceAppPasswords::DeleteByDBID
// does. The entry every route answers with is the one the account's own
// GET /api/v1/me/app-passwords shows, so the two views never disagree.
//
// The administrator is not asked for the account's password, as the account
// holder is on the /me route: that proof exists because a phone's password
// must not mint its own replacement, and the administrator credential is a
// different authority altogether. What it does share with the /me route is
// the log line, so an issued credential is always traceable to a request.

#include "StdAfx.h"
#include "RestApiServer.h"
#include "JsonDocument.h"
#include "Unicode.h"
#include "PasswordPolicy.h"
#include "../Application/Logger.h"
#include "../BO/Account.h"
#include "../BO/AppPassword.h"
#include "../BO/AppPasswords.h"
#include "../Persistence/PersistentAccount.h"
#include "../Persistence/PersistentAppPassword.h"

#include <cmath>
#include <string>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace
{
   using namespace HM;

   // The private helpers of RestApiServer these functions need, handed in by
   // the member function that owns the right to name them.
   struct Bridge
   {
      AnsiString (*escape)(const AnsiString &value);
      HttpResponse (*respond)(int statusCode, const AnsiString &body, const AnsiString &extraHeaders);
      AnsiString (*appPasswordJson)(std::shared_ptr<AppPassword> password, const String &clearText);
   };

   AnsiString Utf8(const String &value)
   {
      AnsiString utf8;
      Unicode::WideToMultiByte(value, utf8);
      return utf8;
   }

   String Utf8ToString(const std::string &utf8)
   {
      String value;
      Unicode::MultiByteToWide(AnsiString(utf8.c_str()), value);
      return value;
   }

   HttpResponse Refusal(const Bridge &bridge, int status, const String &sentence)
   {
      return bridge.respond(status, "{\"error\":\"" + bridge.escape(Utf8(sentence)) + "\"}", "");
   }

   bool ParseObjectBody(const AnsiString &requestBody, JsonValue &body)
   {
      std::string parseError;
      std::string text(requestBody.c_str(), (size_t) requestBody.GetLength());
      return JsonValue::Parse(text, body, parseError) && body.IsObject();
   }

   // The typed reads: untouched when the member is absent or null; false,
   // with error naming the member, when it is present with another type.
   bool ReadString(const JsonValue &object, const char *key, String &out, AnsiString &error)
   {
      const JsonValue *member = object.Get(key);
      if (!member || member->IsNull())
         return true;

      if (!member->IsString())
      {
         error.Format("%hs must be a string", key);
         return false;
      }

      out = Utf8ToString(member->AsString());
      return true;
   }

   bool ReadBool(const JsonValue &object, const char *key, bool &out, AnsiString &error)
   {
      const JsonValue *member = object.Get(key);
      if (!member || member->IsNull())
         return true;

      if (!member->IsBool())
      {
         error.Format("%hs must be true or false", key);
         return false;
      }

      out = member->AsBool();
      return true;
   }

   bool UnknownKey(const JsonValue &object, const char *const *known, size_t knownCount, AnsiString &error)
   {
      for (const std::pair<std::string, JsonValue> &member : object.Members())
      {
         bool found = false;
         for (size_t i = 0; i < knownCount; i++)
         {
            if (member.first == known[i])
            {
               found = true;
               break;
            }
         }
         if (!found)
         {
            error = AnsiString("unknown field: ") + AnsiString(member.first.substr(0, 48).c_str());
            return true;
         }
      }
      return false;
   }

   // A path segment as a database id: digits only, and one.
   bool ParseId(const AnsiString &text, __int64 &id)
   {
      if (text.IsEmpty() || text.GetLength() > 18)
         return false;

      for (int i = 0; i < text.GetLength(); i++)
      {
         if (text[i] < '0' || text[i] > '9')
            return false;
      }

      id = _atoi64(text.c_str());
      return id > 0;
   }

   // The account the address names, read from the store as HandleGetAccount_
   // reads it; null when there is none.
   std::shared_ptr<Account> AccountAt(const String &address)
   {
      std::shared_ptr<Account> account = std::shared_ptr<Account>(new Account());
      if (!PersistentAccount::ReadObject(account, address) || account->GetID() == 0)
         return std::shared_ptr<Account>();

      return account;
   }

   // ------------------------------------------------------------------------
   // App passwords.
   // ------------------------------------------------------------------------

   HttpResponse ListAppPasswords(const Bridge &bridge, std::shared_ptr<Account> account)
   {
      AppPasswords list;
      list.Refresh(account->GetID());

      AnsiString json = "{\"app_passwords\":[";
      int count = 0;
      for (std::shared_ptr<AppPassword> password : list.GetSnapshot())
      {
         if (!password)
            continue;
         if (count > 0)
            json += ",";
         json += bridge.appPasswordJson(password, String());
         count++;
      }
      json += "]}";
      return bridge.respond(200, json, "");
   }

   HttpResponse CreateAppPassword(const Bridge &bridge, std::shared_ptr<Account> account, const AnsiString &requestBody)
   {
      JsonValue body;
      if (!ParseObjectBody(requestBody, body))
         return Refusal(bridge, 400, "the body must be a JSON object");

      static const char *const keys[] = { "name", "password", "active" };
      AnsiString error;
      if (UnknownKey(body, keys, sizeof(keys) / sizeof(keys[0]), error))
         return Refusal(bridge, 400, String(error));

      String name, chosen;
      bool active = true;
      if (!ReadString(body, "name", name, error) ||
          !ReadString(body, "password", chosen, error) ||
          !ReadBool(body, "active", active, error))
         return Refusal(bridge, 400, String(error));

      name.TrimLeft();
      name.TrimRight();
      if (name.IsEmpty())
         return Refusal(bridge, 400, "name is required: what the password is for");
      if (name.GetLength() > 255)
         return Refusal(bridge, 400, "name is at most 255 characters");

      // As InterfaceAppPasswords::Add makes one, owned by this account.
      std::shared_ptr<AppPassword> password = std::shared_ptr<AppPassword>(new AppPassword());
      password->SetAccountID(account->GetID());
      password->SetName(name);
      password->SetActive(active);

      String clearText;
      if (body.Get("password") && !body.Get("password")->IsNull())
      {
         // InterfaceAppPassword::SetPassword's two checks, in its order and
         // its words: the credential's own floor, then the policy every
         // account password is held to - a second credential that opens the
         // same mailbox must not be the way round it.
         if (chosen.GetLength() < 12)
            return Refusal(bridge, 400, "An app password must be at least 12 characters. It is typed into a client once and then lives for years, and it opens the mailbox exactly as the account password does; leave password out to have one generated.");

         String policyFailure;
         if (!PasswordPolicy::IsAcceptable(_T(""), chosen, policyFailure))
            return Refusal(bridge, 400, policyFailure);

         clearText = chosen;
      }
      else
      {
         // InterfaceAppPassword::Generate.
         clearText = AppPassword::GenerateSecret();
         if (clearText.IsEmpty())
            return Refusal(bridge, 500, "the random number generator failed, so no password was made");
      }

      password->SetPassword(clearText);

      // InterfaceAppPassword::Save: the name, the hash and the ceiling of
      // twenty are the store's to judge, and its sentence is the answer.
      String result;
      if (!PersistentAppPassword::SaveObject(password, result, PersistenceModeNormal))
      {
         if (!result.IsEmpty())
            return Refusal(bridge, 400, result);
         return Refusal(bridge, 500, "the app password could not be saved; see the error log");
      }
      PersistentAppPassword::InvalidateExistenceCache();

      LOG_APPLICATION("REST API: app password \"" + name + "\" made for " + account->GetAddress() + " by the administrator.");

      return bridge.respond(201, bridge.appPasswordJson(password, clearText), "");
   }

   HttpResponse DeleteAppPassword(const Bridge &bridge, std::shared_ptr<Account> account, __int64 id)
   {
      AppPasswords list;
      list.Refresh(account->GetID());

      std::shared_ptr<AppPassword> password = list.GetItemByDBID((unsigned __int64) id);
      if (!password || password->GetAccountID() != account->GetID())
         return Refusal(bridge, 404, "no such app password");

      String name = password->GetName();

      // Through the collection, as InterfaceAppPasswords::DeleteByDBID goes.
      if (!list.DeleteItemByDBID(id))
         return Refusal(bridge, 500, "the app password could not be removed");
      PersistentAppPassword::InvalidateExistenceCache();

      LOG_APPLICATION("REST API: app password \"" + name + "\" removed for " + account->GetAddress() + " by the administrator.");

      return bridge.respond(200, "{\"deleted\":true}", "");
   }

   // ------------------------------------------------------------------------
   // The OpenAPI entries, each beginning with a comma as HandleOpenApi_ asks.
   // ------------------------------------------------------------------------

   const char *AccountResourcesPaths =
      ",\"/api/v1/accounts/{address}/app-passwords\":{"
      "\"get\":{\"summary\":\"An account's app passwords (administrator)\",\"description\":\"What Account.AppPasswords lists over COM: id, name, created, last_used, active - never the password. The same entries the account's own GET /api/v1/me/app-passwords shows. A key restricted to named domains reaches the accounts of those domains only.\",\"responses\":{\"200\":{\"description\":\"app_passwords\"},\"404\":{\"description\":\"No such account\"}}},"
      "\"post\":{\"summary\":\"Make an app password for an account (administrator)\",\"description\":\"Body: name (required: what the password is for), password (optional: a chosen secret of at least 12 characters that the password policy accepts, as InterfaceAppPassword.SetPassword requires; left out, one is generated as Generate does) and active (default true). Saved as InterfaceAppPassword.Save saves it: the store judges the name, the hash and the ceiling of twenty per account, and its sentence is the 400. The answer carries the password in clear text, the only time it exists outside the caller. No account password is asked for - the administrator credential is the proof - and the issue is logged with the account's address.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"name\"],\"properties\":{\"name\":{\"type\":\"string\"},\"password\":{\"type\":\"string\",\"writeOnly\":true},\"active\":{\"type\":\"boolean\"}}}}}},\"responses\":{\"201\":{\"description\":\"id, name, created, last_used, active, password\"},\"400\":{\"description\":\"No name, a chosen password the policy or the floor of twelve refuses, an unknown field, or twenty already\"},\"404\":{\"description\":\"No such account\"}}}},"
      "\"/api/v1/accounts/{address}/app-passwords/{id}\":{\"delete\":{\"summary\":\"Remove an account's app password (administrator)\",\"description\":\"Through the account's collection, as InterfaceAppPasswords.DeleteByDBID goes; an app password of another account is not found here. Logged with the account's address.\",\"responses\":{\"200\":{\"description\":\"deleted true\"},\"404\":{\"description\":\"No such account, or no app password with that id in it\"}}}}";
}

namespace HM
{
   // The tail of /api/v1/accounts/<address> - what follows the address - as
   // one of this unit's routes, or false. The caller has split the address
   // off and puts it in route.identifier when this answers true.
   bool
   RestApiServer::ParseAccountResourceRoute_(const AnsiString &method, const AnsiString &tail, Route &route)
   {
      const AnsiString appPasswords = "/app-passwords";

      if (tail == appPasswords)
      {
         if (method == "GET")
            route.kind = RouteAccountAppPasswordList;
         else if (method == "POST")
            route.kind = RouteAccountAppPasswordCreate;

         return route.kind != RouteUnknown;
      }

      if (tail.StartsWith(appPasswords + "/"))
      {
         AnsiString idPart = tail.Mid(appPasswords.GetLength() + 1);
         __int64 id = 0;
         if (method == "DELETE" && idPart.Find("/") < 0 && ParseId(idPart, id))
         {
            route.kind = RouteAccountAppPasswordDelete;
            route.record_id = id;
         }

         return route.kind != RouteUnknown;
      }

      return false;
   }

   AnsiString
   RestApiServer::OpenApiAccountResourcesPaths_()
   {
      return AnsiString(AccountResourcesPaths);
   }

   // The one entry point the dispatcher reaches for this unit's routes: the
   // kind says which resource and which verb, the identifier the account.
   HttpResponse
   RestApiServer::HandleAccountResources_(const Route &route, const AnsiString &requestBody)
   {
      Bridge bridge = { &RestApiServer::JsonEscape_, &RestApiServer::BuildResponse_, &RestApiServer::AppPasswordJson_ };

      std::shared_ptr<Account> account = AccountAt(String(route.identifier));
      if (!account)
         return Refusal(bridge, 404, "account not found");

      switch (route.kind)
      {
      case RouteAccountAppPasswordList:
         return ListAppPasswords(bridge, account);
      case RouteAccountAppPasswordCreate:
         return CreateAppPassword(bridge, account, requestBody);
      case RouteAccountAppPasswordDelete:
         return DeleteAppPassword(bridge, account, route.record_id);
      default:
         break;
      }

      return Refusal(bridge, 404, "not found");
   }
}
