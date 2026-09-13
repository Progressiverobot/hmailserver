// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// The REST API's external (fetch) accounts: what InterfaceFetchAccounts and InterfaceFetchAccount do over COM. See RestApiServer.h.
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// An external account is a remote POP3 or IMAP mailbox the server collects
// into one of its own accounts, on a schedule. Over COM it is
// Account.FetchAccounts: Add, the setters, Save, Delete and DownloadNow. Here
// it is a sub-resource of the account that owns it, so that a key restricted
// to named domains is scoped by the address in the path exactly as the
// account routes are, and a fetch account is never reachable by its id alone:
//
//    GET    /api/v1/accounts/{address}/fetch-accounts               the account's fetch accounts
//    POST   /api/v1/accounts/{address}/fetch-accounts               create one
//    GET    /api/v1/accounts/{address}/fetch-accounts/{id}          one of them
//    PUT    /api/v1/accounts/{address}/fetch-accounts/{id}          change any subset of its fields
//    DELETE /api/v1/accounts/{address}/fetch-accounts/{id}          delete it, with the UIDs it remembers
//    POST   /api/v1/accounts/{address}/fetch-accounts/{id}/download collect now
//
// Each handler does what the COM member does with the same persistence call:
// a create is InterfaceFetchAccounts::Add + the setters + InterfaceFetchAccount::Save,
// which is PersistentFetchAccount::SaveObject; a delete goes through the
// account's FetchAccounts collection as InterfaceFetchAccount::Delete does, so
// the row and its UID list go together; DownloadNow is
// PersistentFetchAccount::SetRetryNow and ExternalFetchManager::SetCheckNow,
// and answers 202 because the collection happens on the fetcher's thread - a
// caller watches locked go false, as the fixtures do. The remote password is
// write-only, as it is over COM.

#include "StdAfx.h"
#include "RestApiServer.h"
#include "JsonDocument.h"
#include "Unicode.h"
#include "../BO/Account.h"
#include "../BO/FetchAccount.h"
#include "../BO/FetchAccounts.h"
#include "../Persistence/PersistentAccount.h"
#include "../Persistence/PersistentFetchAccount.h"
#include "../../ExternalFetcher/ExternalFetchManager.h"

#include <cmath>
#include <functional>
#include <string>
#include <vector>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      typedef std::function<AnsiString(const String &)> Quote;

      const char *ServerTypeWord(FetchAccount::ServerType type)
      {
         return type == FetchAccount::IMAP ? "imap" : "pop3";
      }

      const char *SecurityWord(ConnectionSecurity security)
      {
         switch (security)
         {
         case CSSSL:
            return "tls";
         case CSSTARTTLSOptional:
            return "starttls_optional";
         case CSSTARTTLSRequired:
            return "starttls_required";
         case CSNone:
         default:
            return "none";
         }
      }

      bool ParseSecurityWord(const std::string &word, ConnectionSecurity &security)
      {
         AnsiString value = word.c_str();

         if (value.CompareNoCase("none") == 0)
            security = CSNone;
         else if (value.CompareNoCase("tls") == 0)
            security = CSSSL;
         else if (value.CompareNoCase("starttls_optional") == 0)
            security = CSSTARTTLSOptional;
         else if (value.CompareNoCase("starttls_required") == 0)
            security = CSSTARTTLSRequired;
         else
            return false;

         return true;
      }

      AnsiString ErrorBody(const Quote &quote, const String &sentence)
      {
         AnsiString body;
         body.Format("{\"error\":\"%hs\"}", quote(sentence).c_str());
         return body;
      }

      String Utf8ToString(const std::string &utf8)
      {
         String value;
         Unicode::MultiByteToWide(AnsiString(utf8.c_str()), value);
         return value;
      }

      // A fetch account as every route emits it. The password is the one
      // column left out.
      AnsiString FetchAccountJson(std::shared_ptr<FetchAccount> fetchAccount, const Quote &quote)
      {
         AnsiString entry;
         entry.Format("{\"id\":%I64d,\"name\":\"%hs\",\"server_address\":\"%hs\",\"port\":%d,\"server_type\":\"%hs\"",
            fetchAccount->GetID(),
            quote(fetchAccount->GetName()).c_str(),
            quote(fetchAccount->GetServerAddress()).c_str(),
            fetchAccount->GetPort(),
            ServerTypeWord(fetchAccount->GetServerType()));

         AnsiString numbers;
         numbers.Format(",\"username\":\"%hs\",\"minutes_between_fetch\":%d,\"days_to_keep_messages\":%d,\"connection_security\":\"%hs\",\"mime_recipient_headers\":\"%hs\"",
            quote(fetchAccount->GetUsername()).c_str(),
            fetchAccount->GetMinutesBetweenTry(),
            fetchAccount->GetDaysToKeep(),
            SecurityWord(fetchAccount->GetConnectionSecurity()),
            quote(fetchAccount->GetMIMERecipientHeaders()).c_str());
         entry += numbers;

         auto flag = [&entry](const char *name, bool value)
         {
            entry += ",\"";
            entry += name;
            entry += value ? "\":true" : "\":false";
         };

         flag("enabled", fetchAccount->GetActive());
         flag("process_mime_recipients", fetchAccount->GetProcessMIMERecipients());
         flag("process_mime_date", fetchAccount->GetProcessMIMEDate());
         flag("use_antispam", fetchAccount->GetUseAntiSpam());
         flag("use_antivirus", fetchAccount->GetUseAntiVirus());
         flag("enable_route_recipients", fetchAccount->GetEnableRouteRecipients());
         flag("mirror_folders", fetchAccount->GetMirrorFolders());
         flag("locked", PersistentFetchAccount::IsLocked(fetchAccount->GetID()));

         entry += ",\"next_download_time\":\"" + quote(fetchAccount->GetNextTry()) + "\"}";
         return entry;
      }

      // The fields a body may carry, for both the create and the update.
      const char *const KnownKeys[] =
      {
         "name", "server_address", "port", "server_type", "username", "password", "enabled",
         "minutes_between_fetch", "days_to_keep_messages", "connection_security",
         "process_mime_recipients", "process_mime_date", "use_antispam", "use_antivirus",
         "enable_route_recipients", "mime_recipient_headers", "mirror_folders"
      };

      bool ReadString(const JsonValue &object, const char *key, String &out, bool &given, AnsiString &error)
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
         given = true;
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

      bool ReadInteger(const JsonValue &object, const char *key, long minimum, long maximum, long &out, AnsiString &error)
      {
         const JsonValue *member = object.Get(key);
         if (!member || member->IsNull())
            return true;

         double number = member->IsNumber() ? member->AsNumber() : 0.0;

         if (!member->IsNumber() || std::floor(number) != number ||
             number < (double) minimum || number > (double) maximum)
         {
            error.Format("%hs must be a whole number between %ld and %ld", key, minimum, maximum);
            return false;
         }

         out = (long) number;
         return true;
      }

      // Applies a body to a fetch account, new or existing. Every member is read
      // and checked before any is applied, so a refused body changes nothing;
      // the sentence names the member. A string longer than its column is
      // refused rather than cut, because a name that came back shorter than it
      // was written would be a different fetch account.
      bool ApplyBody(const JsonValue &body, std::shared_ptr<FetchAccount> fetchAccount, AnsiString &error)
      {
         for (const std::pair<std::string, JsonValue> &member : body.Members())
         {
            bool known = false;
            for (size_t i = 0; i < sizeof(KnownKeys) / sizeof(KnownKeys[0]); i++)
            {
               if (member.first == KnownKeys[i])
               {
                  known = true;
                  break;
               }
            }
            if (!known)
            {
               error = AnsiString("unknown field: ") + AnsiString(member.first.substr(0, 48).c_str());
               return false;
            }
         }

         String name = fetchAccount->GetName(), serverAddress = fetchAccount->GetServerAddress();
         String username = fetchAccount->GetUsername(), password = fetchAccount->GetPassword();
         String headers = fetchAccount->GetMIMERecipientHeaders(), serverType, security;
         bool nameGiven = false, serverGiven = false, usernameGiven = false, passwordGiven = false;
         bool headersGiven = false, typeGiven = false, securityGiven = false;

         if (!ReadString(body, "name", name, nameGiven, error) ||
             !ReadString(body, "server_address", serverAddress, serverGiven, error) ||
             !ReadString(body, "username", username, usernameGiven, error) ||
             !ReadString(body, "password", password, passwordGiven, error) ||
             !ReadString(body, "mime_recipient_headers", headers, headersGiven, error) ||
             !ReadString(body, "server_type", serverType, typeGiven, error) ||
             !ReadString(body, "connection_security", security, securityGiven, error))
            return false;

         long port = fetchAccount->GetPort();
         long minutes = fetchAccount->GetMinutesBetweenTry();
         long days = fetchAccount->GetDaysToKeep();

         if (!ReadInteger(body, "port", 1, 65535, port, error) ||
             !ReadInteger(body, "minutes_between_fetch", 1, 100000, minutes, error) ||
             !ReadInteger(body, "days_to_keep_messages", -1, 100000, days, error))
            return false;

         bool enabled = fetchAccount->GetActive();
         bool processRecipients = fetchAccount->GetProcessMIMERecipients();
         bool processDate = fetchAccount->GetProcessMIMEDate();
         bool antiSpam = fetchAccount->GetUseAntiSpam();
         bool antiVirus = fetchAccount->GetUseAntiVirus();
         bool routeRecipients = fetchAccount->GetEnableRouteRecipients();
         bool mirror = fetchAccount->GetMirrorFolders();

         if (!ReadBool(body, "enabled", enabled, error) ||
             !ReadBool(body, "process_mime_recipients", processRecipients, error) ||
             !ReadBool(body, "process_mime_date", processDate, error) ||
             !ReadBool(body, "use_antispam", antiSpam, error) ||
             !ReadBool(body, "use_antivirus", antiVirus, error) ||
             !ReadBool(body, "enable_route_recipients", routeRecipients, error) ||
             !ReadBool(body, "mirror_folders", mirror, error))
            return false;

         FetchAccount::ServerType type = fetchAccount->GetServerType();
         if (typeGiven)
         {
            AnsiString word = serverType;
            if (word.CompareNoCase("pop3") == 0)
               type = FetchAccount::POP3;
            else if (word.CompareNoCase("imap") == 0)
               type = FetchAccount::IMAP;
            else
            {
               error = "server_type must be pop3 or imap";
               return false;
            }
         }

         ConnectionSecurity connectionSecurity = fetchAccount->GetConnectionSecurity();
         if (securityGiven && !ParseSecurityWord(std::string(AnsiString(security).c_str()), connectionSecurity))
         {
            error = "connection_security must be one of none, starttls_optional, starttls_required, tls";
            return false;
         }

         name.Trim();
         serverAddress.Trim();

         if (name.IsEmpty())
         {
            error = "name is required";
            return false;
         }
         if (name.GetLength() > 255 || serverAddress.GetLength() > 255 || username.GetLength() > 255 || headers.GetLength() > 255)
         {
            error = "name, server_address, username and mime_recipient_headers are at most 255 characters";
            return false;
         }
         if (serverAddress.IsEmpty())
         {
            error = "server_address is required";
            return false;
         }
         if (port == 0)
         {
            error = "port is required";
            return false;
         }

         fetchAccount->SetName(name);
         fetchAccount->SetServerAddress(serverAddress);
         fetchAccount->SetPort((int) port);
         fetchAccount->SetServerType(type);
         fetchAccount->SetUsername(username);
         fetchAccount->SetPassword(password);
         fetchAccount->SetActive(enabled);
         fetchAccount->SetMinutesBetweenTry((int) minutes);
         fetchAccount->SetDaysToKeep((int) days);
         fetchAccount->SetConnectionSecurity(connectionSecurity);
         fetchAccount->SetProcessMIMERecipients(processRecipients);
         fetchAccount->SetProcessMIMEDate(processDate);
         fetchAccount->SetUseAntiSpam(antiSpam);
         fetchAccount->SetUseAntiVirus(antiVirus);
         fetchAccount->SetEnableRouteRecipients(routeRecipients);
         fetchAccount->SetMIMERecipientHeaders(headers);
         fetchAccount->SetMirrorFolders(mirror);
         return true;
      }

      // The account named in the path, or null. Read as HandleUpdateAccount_
      // reads it: by address, through PersistentAccount.
      std::shared_ptr<Account> OwningAccount(const String &address)
      {
         std::shared_ptr<Account> account = std::shared_ptr<Account>(new Account());
         if (!PersistentAccount::ReadObject(account, address) || account->GetID() == 0)
            return std::shared_ptr<Account>();
         return account;
      }

      bool ParseBody(const AnsiString &requestBody, JsonValue &body)
      {
         std::string parseError;
         std::string text(requestBody.c_str(), (size_t) requestBody.GetLength());
         return JsonValue::Parse(text, body, parseError) && body.IsObject();
      }
   }

   AnsiString
   RestApiServer::OpenApiFetchAccountsPaths_()
   {
      static const char *paths =
         ",\"/api/v1/accounts/{address}/fetch-accounts\":{"
         "\"get\":{\"summary\":\"The account's external (fetch) accounts\",\"description\":\"The remote POP3 or IMAP mailboxes the server collects into this account - Account.FetchAccounts over COM. Each entry: id, name, server_address, port, server_type (pop3 or imap), username, enabled, minutes_between_fetch, days_to_keep_messages, connection_security, process_mime_recipients, process_mime_date, use_antispam, use_antivirus, enable_route_recipients, mime_recipient_headers, mirror_folders, locked (a collection is running now) and next_download_time. The remote password is never emitted. A key restricted to named domains reaches the accounts of those domains only.\",\"responses\":{\"200\":{\"description\":\"Array of fetch accounts\"},\"404\":{\"description\":\"No such account\"}}},"
         "\"post\":{\"summary\":\"Create an external (fetch) account\",\"description\":\"Body: name, server_address and port (required); server_type (pop3, the default, or imap), username, password (write-only), enabled (default true), minutes_between_fetch (default 30), days_to_keep_messages (default 0: delete after collecting; -1 keeps every message; for IMAP the remote INBOX is collected once by UID and left intact when this says so), connection_security (none, starttls_optional, starttls_required, tls; default none), process_mime_recipients, process_mime_date, use_antispam, use_antivirus, enable_route_recipients, mime_recipient_headers, mirror_folders. What InterfaceFetchAccounts.Add and Save do; the first collection is scheduled at once. Everything is checked before anything is saved: an unknown field, a value of the wrong type or a value out of range is a 400 naming it.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"name\",\"server_address\",\"port\"],\"properties\":{\"name\":{\"type\":\"string\"},\"server_address\":{\"type\":\"string\"},\"port\":{\"type\":\"integer\"},\"server_type\":{\"type\":\"string\",\"enum\":[\"pop3\",\"imap\"]},\"username\":{\"type\":\"string\"},\"password\":{\"type\":\"string\",\"writeOnly\":true},\"enabled\":{\"type\":\"boolean\"},\"minutes_between_fetch\":{\"type\":\"integer\"},\"days_to_keep_messages\":{\"type\":\"integer\"},\"connection_security\":{\"type\":\"string\",\"enum\":[\"none\",\"starttls_optional\",\"starttls_required\",\"tls\"]},\"process_mime_recipients\":{\"type\":\"boolean\"},\"process_mime_date\":{\"type\":\"boolean\"},\"use_antispam\":{\"type\":\"boolean\"},\"use_antivirus\":{\"type\":\"boolean\"},\"enable_route_recipients\":{\"type\":\"boolean\"},\"mime_recipient_headers\":{\"type\":\"string\"},\"mirror_folders\":{\"type\":\"boolean\"}}}}}},\"responses\":{\"201\":{\"description\":\"Created: the fetch account as the listing shows it, with its id\"},\"400\":{\"description\":\"A required field missing, an unknown field, or a value refused (error names it)\"},\"404\":{\"description\":\"No such account\"}}}},"
         "\"/api/v1/accounts/{address}/fetch-accounts/{id}\":{"
         "\"get\":{\"summary\":\"One external (fetch) account\",\"description\":\"As the listing shows it. A fetch account that belongs to another account is not found here.\",\"responses\":{\"200\":{\"description\":\"The fetch account\"},\"404\":{\"description\":\"No such account, or no fetch account with that id in it\"}}},"
         "\"put\":{\"summary\":\"Change an external (fetch) account\",\"description\":\"Body: any subset of the fields POST takes; a field left out keeps its value, and a password left out is kept. The same checks as POST, and nothing changes when one fails. What the setters and Save do over COM.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\"}}}},\"responses\":{\"200\":{\"description\":\"The fetch account as saved\"},\"400\":{\"description\":\"A field refused; nothing changed\"},\"404\":{\"description\":\"No such account, or no fetch account with that id in it\"}}},"
         "\"delete\":{\"summary\":\"Delete an external (fetch) account\",\"description\":\"Through the account's collection as InterfaceFetchAccount.Delete goes: the row and the UIDs it remembered go together. Mail already collected stays.\",\"responses\":{\"200\":{\"description\":\"Deleted\"},\"404\":{\"description\":\"No such account, or no fetch account with that id in it\"}}}},"
         "\"/api/v1/accounts/{address}/fetch-accounts/{id}/download\":{\"post\":{\"summary\":\"Collect from the remote mailbox now\",\"description\":\"What DownloadNow does over COM: the next collection is set to now and the fetcher woken. Answers before the collection runs, because it runs on the fetcher's thread: watch locked in GET until it is false again.\",\"responses\":{\"202\":{\"description\":\"Queued\"},\"404\":{\"description\":\"No such account, or no fetch account with that id in it\"},\"503\":{\"description\":\"The external fetcher is not running\"}}}}";

      return AnsiString(paths);
   }

   HttpResponse
   RestApiServer::HandleListFetchAccounts_(const String &address)
   {
      Quote quote = [](const String &value) { return JsonEscape_(Utf8_(value)); };

      std::shared_ptr<Account> account = OwningAccount(address);
      if (!account)
         return BuildResponse_(404, "{\"error\":\"account not found\"}");

      FetchAccounts fetchAccounts(account->GetID());
      fetchAccounts.Refresh();

      AnsiString body = "[";
      int count = 0;
      for (std::shared_ptr<FetchAccount> fetchAccount : fetchAccounts.GetSnapshot())
      {
         if (!fetchAccount)
            continue;
         if (count > 0)
            body += ",";
         body += FetchAccountJson(fetchAccount, quote);
         count++;
      }
      body += "]";

      return BuildResponse_(200, body);
   }

   HttpResponse
   RestApiServer::HandleCreateFetchAccount_(const String &address, const AnsiString &requestBody)
   {
      Quote quote = [](const String &value) { return JsonEscape_(Utf8_(value)); };

      std::shared_ptr<Account> account = OwningAccount(address);
      if (!account)
         return BuildResponse_(404, "{\"error\":\"account not found\"}");

      JsonValue body;
      if (!ParseBody(requestBody, body))
         return BuildResponse_(400, "{\"error\":\"the body must be a JSON object\"}");

      // As InterfaceFetchAccounts::Add makes one: the constructor's defaults,
      // owned by this account.
      std::shared_ptr<FetchAccount> fetchAccount = std::shared_ptr<FetchAccount>(new FetchAccount());
      fetchAccount->SetAccountID(account->GetID());

      AnsiString error;
      if (!ApplyBody(body, fetchAccount, error))
         return BuildResponse_(400, ErrorBody(quote, String(error)));

      if (!PersistentFetchAccount::SaveObject(fetchAccount))
         return BuildResponse_(500, "{\"error\":\"failed to save the fetch account; see the error log\"}");

      LOG_APPLICATION("RestApi: Fetch account " + fetchAccount->GetName() + " created for " + address + ".");

      return BuildResponse_(201, FetchAccountJson(fetchAccount, quote));
   }

   HttpResponse
   RestApiServer::HandleGetFetchAccount_(const String &address, __int64 fetchAccountId)
   {
      Quote quote = [](const String &value) { return JsonEscape_(Utf8_(value)); };

      std::shared_ptr<Account> account = OwningAccount(address);
      if (!account)
         return BuildResponse_(404, "{\"error\":\"account not found\"}");

      FetchAccounts fetchAccounts(account->GetID());
      fetchAccounts.Refresh();

      std::shared_ptr<FetchAccount> fetchAccount = fetchAccounts.GetItemByDBID(fetchAccountId);
      if (!fetchAccount)
         return BuildResponse_(404, "{\"error\":\"fetch account not found\"}");

      return BuildResponse_(200, FetchAccountJson(fetchAccount, quote));
   }

   HttpResponse
   RestApiServer::HandleUpdateFetchAccount_(const String &address, __int64 fetchAccountId, const AnsiString &requestBody)
   {
      Quote quote = [](const String &value) { return JsonEscape_(Utf8_(value)); };

      std::shared_ptr<Account> account = OwningAccount(address);
      if (!account)
         return BuildResponse_(404, "{\"error\":\"account not found\"}");

      JsonValue body;
      if (!ParseBody(requestBody, body))
         return BuildResponse_(400, "{\"error\":\"the body must be a JSON object\"}");

      FetchAccounts fetchAccounts(account->GetID());
      fetchAccounts.Refresh();

      std::shared_ptr<FetchAccount> fetchAccount = fetchAccounts.GetItemByDBID(fetchAccountId);
      if (!fetchAccount)
         return BuildResponse_(404, "{\"error\":\"fetch account not found\"}");

      AnsiString error;
      if (!ApplyBody(body, fetchAccount, error))
         return BuildResponse_(400, ErrorBody(quote, String(error)));

      if (!PersistentFetchAccount::SaveObject(fetchAccount))
         return BuildResponse_(500, "{\"error\":\"failed to save the fetch account; see the error log\"}");

      LOG_APPLICATION("RestApi: Fetch account " + fetchAccount->GetName() + " of " + address + " updated.");

      return BuildResponse_(200, FetchAccountJson(fetchAccount, quote));
   }

   HttpResponse
   RestApiServer::HandleDeleteFetchAccount_(const String &address, __int64 fetchAccountId)
   {
      std::shared_ptr<Account> account = OwningAccount(address);
      if (!account)
         return BuildResponse_(404, "{\"error\":\"account not found\"}");

      FetchAccounts fetchAccounts(account->GetID());
      fetchAccounts.Refresh();

      std::shared_ptr<FetchAccount> fetchAccount = fetchAccounts.GetItemByDBID(fetchAccountId);
      if (!fetchAccount)
         return BuildResponse_(404, "{\"error\":\"fetch account not found\"}");

      String name = fetchAccount->GetName();

      if (!fetchAccounts.DeleteItemByDBID(fetchAccountId))
         return BuildResponse_(500, "{\"error\":\"failed to delete the fetch account\"}");

      LOG_APPLICATION("RestApi: Fetch account " + name + " of " + address + " deleted.");

      return BuildResponse_(200, "{\"deleted\":true}");
   }

   HttpResponse
   RestApiServer::HandleDownloadFetchAccount_(const String &address, __int64 fetchAccountId)
   {
      std::shared_ptr<Account> account = OwningAccount(address);
      if (!account)
         return BuildResponse_(404, "{\"error\":\"account not found\"}");

      FetchAccounts fetchAccounts(account->GetID());
      fetchAccounts.Refresh();

      std::shared_ptr<FetchAccount> fetchAccount = fetchAccounts.GetItemByDBID(fetchAccountId);
      if (!fetchAccount)
         return BuildResponse_(404, "{\"error\":\"fetch account not found\"}");

      std::shared_ptr<ExternalFetchManager> fetcher = Application::Instance()->GetExternalFetchManager();
      if (!fetcher)
         return BuildResponse_(503, "{\"error\":\"the external fetcher is not running\"}");

      PersistentFetchAccount::SetRetryNow(fetchAccount->GetID());
      fetcher->SetCheckNow();

      LOG_APPLICATION("RestApi: Fetch account " + fetchAccount->GetName() + " of " + address + " asked to collect now.");

      return BuildResponse_(202, "{\"queued\":true}");
   }
}
