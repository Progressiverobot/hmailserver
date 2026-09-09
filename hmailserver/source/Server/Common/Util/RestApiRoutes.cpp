// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// The REST API's SMTP routes, alias writes and the account update. See RestApiServer.h.
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Each handler here does what the Control Panel does through COM, with the same
// persistence call, the same limitation check and the same cache refresh:
//
//   - a route is InterfaceRoutes::Add + the setters + InterfaceRoute::Save, which
//     is PersistentRoute::SaveObject and then AddToParentCollection on the ONE
//     Routes collection SMTPConfiguration owns - the collection RecipientParser
//     and ServerTargetResolver read for every recipient, so a route added to it
//     is in effect for the next message without a restart; its addresses go
//     through PersistentRouteAddress::SaveObject and the route's own
//     RouteAddresses collection, as InterfaceRouteAddress::Save does; a delete
//     is Routes::DeleteItemByDBID, which deletes the addresses with the row;
//   - an alias is InterfaceAliases::Add + InterfaceAlias::Save, which is
//     PersistentAlias::SaveObject - the limitation check that refuses a name an
//     account or a list already has, and the Cache<Alias> eviction that makes
//     the next RCPT TO read the row - and a delete is PersistentAlias::DeleteObject
//     through the domain's collection;
//   - the account update is the InterfaceAccount setters + InterfaceAccount::Save:
//     put_Password's policy and reuse checks and hashing, then
//     PersistentAccount::SaveObject with the same limitation check and the
//     Cache<Account> eviction that makes the next IMAP logon read the new row.
//
// Bodies are parsed as JSON documents (JsonDocument.h) rather than searched by
// key, because a route carries an array and the account update has to name a
// key it does not know. A member that is present with the wrong type is a 400
// naming the member; one that is absent takes the default the Control Panel
// gives it, or - for the account update - leaves the stored value alone.

#include "StdAfx.h"
#include "RestApiServer.h"
#include "JsonDocument.h"
#include "Crypt.h"
#include "PasswordPolicy.h"
#include "PasswordHistory.h"
#include "Time.h"
#include "Unicode.h"
#include "../Application/IniFileSettings.h"
#include "../BO/Domains.h"
#include "../BO/Domain.h"
#include "../BO/Aliases.h"
#include "../BO/Alias.h"
#include "../BO/Account.h"
#include "../BO/Routes.h"
#include "../BO/Route.h"
#include "../BO/RouteAddresses.h"
#include "../BO/RouteAddress.h"
#include "../Persistence/PersistentAlias.h"
#include "../Persistence/PersistentAccount.h"
#include "../Persistence/PersistentRoute.h"
#include "../Persistence/PersistentRouteAddress.h"
#include "../Persistence/PersistenceMode.h"
#include "../TCPIP/SocketConstants.h"
#include "../../SMTP/SMTPConfiguration.h"

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
      // The escape the member functions hand down: a String as the escaped UTF-8
      // that goes between the quotes of a JSON string. JsonEscape_ and Utf8_ are
      // private to RestApiServer, so the builders here take them as a functor
      // rather than naming them.
      typedef std::function<AnsiString(const String &)> Quote;

      // The words the API uses for ConnectionSecurity, in both directions. The
      // COM enum's numbers are not an interface anyone should have to know.
      const char *ConnectionSecurityWord(ConnectionSecurity security)
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

      bool ParseConnectionSecurityWord(const std::string &word, ConnectionSecurity &security)
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

      const char *AdminLevelWord(Account::AdminLevel level)
      {
         switch (level)
         {
         case Account::ServerAdmin:
            return "server";
         case Account::DomainAdmin:
            return "domain";
         case Account::NormalUser:
         default:
            return "user";
         }
      }

      // The three words and nothing else: InterfaceAccount::put_AdminLevel
      // refuses a number that matches no level, because a value stored without
      // being one of these is how its guard was once walked around.
      bool ParseAdminLevelWord(const std::string &word, Account::AdminLevel &level)
      {
         AnsiString value = word.c_str();

         if (value.CompareNoCase("user") == 0)
            level = Account::NormalUser;
         else if (value.CompareNoCase("domain") == 0)
            level = Account::DomainAdmin;
         else if (value.CompareNoCase("server") == 0)
            level = Account::ServerAdmin;
         else
            return false;

         return true;
      }

      // The request body as a JSON object. An empty body, a body that is not
      // JSON, or a JSON value that is not an object are all the same refusal.
      bool ParseObjectBody(const AnsiString &requestBody, JsonValue &body, AnsiString &error)
      {
         std::string parseError;
         std::string text(requestBody.c_str(), (size_t) requestBody.GetLength());

         if (!JsonValue::Parse(text, body, parseError) || !body.IsObject())
         {
            error = "the body must be a JSON object";
            return false;
         }

         return true;
      }

      // The first member whose key is not in the list, or the empty string. A
      // misspelt key is caught rather than silently ignored, which is the one
      // thing that makes "any subset of these fields" honest.
      std::string FirstUnknownKey(const JsonValue &object, const char *const *known, size_t knownCount)
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
               return member.first;
         }

         return "";
      }

      bool HasMember(const JsonValue &object, const char *key)
      {
         const JsonValue *member = object.Get(key);
         return member != nullptr && !member->IsNull();
      }

      String Utf8ToString(const std::string &utf8)
      {
         String value;
         Unicode::MultiByteToWide(AnsiString(utf8.c_str()), value);
         return value;
      }

      // The typed reads. Each leaves out untouched when the member is absent (or
      // null), and answers false with error naming the member when it is present
      // with another type.
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

      // A flat array of e-mail addresses, trimmed and de-duplicated without
      // regard to case, as RouteAddresses::GetItemByName matches them.
      bool ReadAddressArray(const JsonValue &object, const char *key, std::vector<String> &out, AnsiString &error)
      {
         const JsonValue *member = object.Get(key);
         if (!member || member->IsNull())
            return true;

         if (!member->IsArray())
         {
            error.Format("%hs must be an array of e-mail addresses", key);
            return false;
         }

         for (const JsonValue &item : member->Items())
         {
            if (!item.IsString())
            {
               error.Format("%hs must be an array of e-mail addresses", key);
               return false;
            }

            String address = Utf8ToString(item.AsString());
            address.Trim();

            if (address.IsEmpty() || !StringParser::IsValidEmailAddress(address))
            {
               error.Format("%hs must be an array of e-mail addresses", key);
               return false;
            }

            bool duplicate = false;
            for (const String &existing : out)
            {
               if (existing.CompareNoCase(address) == 0)
               {
                  duplicate = true;
                  break;
               }
            }

            if (!duplicate)
               out.push_back(address);
         }

         return true;
      }

      AnsiString ErrorBody(const Quote &quote, const String &sentence)
      {
         AnsiString body;
         body.Format("{\"error\":\"%hs\"}", quote(sentence).c_str());
         return body;
      }

      // A route as the listing renders it, so that what a create or an update
      // answers is exactly what the next listing will show. The relay password
      // is the one column left out: it is write-only here, as it is over COM,
      // where SetRelayerAuthPassword has no getter.
      AnsiString RouteEntryJson(std::shared_ptr<HM::Route> route, const Quote &quote)
      {
         AnsiString entry;
         entry.Format("{\"id\":%I64d,\"domain_name\":\"%hs\",\"description\":\"%hs\",\"target_smtp_host\":\"%hs\","
                      "\"target_smtp_port\":%d,\"number_of_tries\":%d,\"minutes_between_try\":%d",
            route->GetID(),
            quote(route->DomainName()).c_str(),
            quote(route->GetDescription()).c_str(),
            quote(route->TargetSMTPHost()).c_str(),
            (int) route->TargetSMTPPort(),
            (int) route->NumberOfTries(),
            (int) route->MinutesBetweenTry());

         auto flag = [&entry](const char *name, bool value)
         {
            entry += ",\"";
            entry += name;
            entry += value ? "\":true" : "\":false";
         };

         flag("relayer_requires_authentication", route->GetRelayerRequiresAuth());
         entry += ",\"relayer_auth_username\":\"" + quote(route->GetRelayerAuthUsername()) + "\"";

         // treat_security_as_local_domain is the COM property's older name for
         // treat_recipient_as_local_domain (InterfaceRoute maps both to the same
         // flag); both are emitted so a caller written against either reads the
         // value it expects.
         flag("treat_security_as_local_domain", route->GetTreatRecipientAsLocalDomain());
         flag("treat_recipient_as_local_domain", route->GetTreatRecipientAsLocalDomain());
         flag("treat_sender_as_local_domain", route->GetTreatSenderAsLocalDomain());
         flag("all_addresses", route->ToAllAddresses());

         entry += ",\"connection_security\":\"";
         entry += ConnectionSecurityWord(route->GetConnectionSecurity());
         entry += "\",\"addresses\":[";

         int count = 0;
         std::shared_ptr<RouteAddresses> addresses = route->GetAddresses();
         if (addresses)
         {
            for (std::shared_ptr<RouteAddress> address : addresses->GetSnapshot())
            {
               if (!address)
                  continue;

               if (count > 0)
                  entry += ",";

               entry += "\"" + quote(address->GetAddress()) + "\"";
               count++;
            }
         }

         entry += "]}";

         return entry;
      }

      // Every scalar of a route, id included. Used to work on a copy while the
      // running route stays as it was until the save has succeeded.
      void CopyRouteScalars(const HM::Route &from, HM::Route &to)
      {
         to.SetID(from.GetID());
         to.DomainName(from.DomainName());
         to.SetDescription(from.GetDescription());
         to.TargetSMTPHost(from.TargetSMTPHost());
         to.TargetSMTPPort(from.TargetSMTPPort());
         to.NumberOfTries(from.NumberOfTries());
         to.MinutesBetweenTry(from.MinutesBetweenTry());
         to.ToAllAddresses(from.ToAllAddresses());
         to.SetRelayerRequiresAuth(from.GetRelayerRequiresAuth());
         to.SetRelayerAuthUsername(from.GetRelayerAuthUsername());
         to.SetRelayerAuthPassword(from.GetRelayerAuthPassword());
         to.SetTreatRecipientAsLocalDomain(from.GetTreatRecipientAsLocalDomain());
         to.SetTreatSenderAsLocalDomain(from.GetTreatSenderAsLocalDomain());
         to.SetConnectionSecurity(from.GetConnectionSecurity());
      }

      // The whole record from the body onto route: what a POST creates and what
      // a PUT replaces. Every member the body does not name takes the default
      // the Control Panel gives a new route (RoutesView: port 25, three tries
      // ten minutes apart) or the Route constructor's (every address, no
      // authentication, no TLS), except the relay password, which stays as it
      // is when the body does not name it - a caller can never read it back to
      // send it again, so "absent" cannot mean "blank".
      bool ApplyRouteBody(const JsonValue &body, std::shared_ptr<HM::Route> route, std::vector<String> &addresses, AnsiString &error)
      {
         static const char *const knownKeys[] =
         {
            "id", "domain_name", "description", "target_smtp_host", "target_smtp_port",
            "number_of_tries", "minutes_between_try", "relayer_requires_authentication",
            "relayer_auth_username", "relayer_auth_password", "treat_security_as_local_domain",
            "treat_recipient_as_local_domain", "treat_sender_as_local_domain", "all_addresses",
            "addresses", "connection_security"
         };

         std::string unknown = FirstUnknownKey(body, knownKeys, sizeof(knownKeys) / sizeof(knownKeys[0]));
         if (!unknown.empty())
         {
            error = "unknown field: " + AnsiString(unknown.substr(0, 48).c_str());
            return false;
         }

         String domainName;
         String description;
         String targetHost;
         String relayerUsername;
         String relayerPassword;
         long targetPort = 25;
         long numberOfTries = 3;
         long minutesBetweenTry = 10;
         bool relayerRequiresAuth = false;
         bool treatRecipientAsLocal = false;
         bool treatSenderAsLocal = false;
         bool allAddresses = true;
         ConnectionSecurity security = CSNone;

         if (!ReadString(body, "domain_name", domainName, error) ||
             !ReadString(body, "description", description, error) ||
             !ReadString(body, "target_smtp_host", targetHost, error) ||
             !ReadInteger(body, "target_smtp_port", 1, 65535, targetPort, error) ||
             !ReadInteger(body, "number_of_tries", 0, 1000000, numberOfTries, error) ||
             !ReadInteger(body, "minutes_between_try", 0, 1000000, minutesBetweenTry, error) ||
             !ReadBool(body, "relayer_requires_authentication", relayerRequiresAuth, error) ||
             !ReadString(body, "relayer_auth_username", relayerUsername, error) ||
             !ReadString(body, "relayer_auth_password", relayerPassword, error) ||
             !ReadBool(body, "treat_security_as_local_domain", treatRecipientAsLocal, error) ||
             !ReadBool(body, "treat_recipient_as_local_domain", treatRecipientAsLocal, error) ||
             !ReadBool(body, "treat_sender_as_local_domain", treatSenderAsLocal, error) ||
             !ReadBool(body, "all_addresses", allAddresses, error) ||
             !ReadAddressArray(body, "addresses", addresses, error))
         {
            return false;
         }

         if (HasMember(body, "connection_security"))
         {
            const JsonValue *member = body.Get("connection_security");
            if (!member->IsString() || !ParseConnectionSecurityWord(member->AsString(), security))
            {
               error = "connection_security must be one of none, starttls_optional, starttls_required, tls";
               return false;
            }
         }

         domainName.Trim();
         targetHost.Trim();
         relayerUsername.Trim();

         if (domainName.IsEmpty())
         {
            error = "domain_name is required";
            return false;
         }

         if (targetHost.IsEmpty())
         {
            error = "target_smtp_host is required";
            return false;
         }

         if (relayerRequiresAuth && relayerUsername.IsEmpty())
         {
            error = "relayer_auth_username is required when relayer_requires_authentication is true";
            return false;
         }

         route->DomainName(domainName);
         route->SetDescription(description);
         route->TargetSMTPHost(targetHost);
         route->TargetSMTPPort(targetPort);
         route->NumberOfTries(numberOfTries);
         route->MinutesBetweenTry(minutesBetweenTry);
         route->SetRelayerRequiresAuth(relayerRequiresAuth);
         route->SetRelayerAuthUsername(relayerUsername);
         route->SetTreatRecipientAsLocalDomain(treatRecipientAsLocal);
         route->SetTreatSenderAsLocalDomain(treatSenderAsLocal);
         route->ToAllAddresses(allAddresses);
         route->SetConnectionSecurity(security);

         if (HasMember(body, "relayer_auth_password"))
            route->SetRelayerAuthPassword(relayerPassword);

         return true;
      }

      // One address row for a route, saved and added to the route's own
      // collection - InterfaceRouteAddresses::Add followed by
      // InterfaceRouteAddress::Save.
      bool AddRouteAddress(std::shared_ptr<HM::Route> route, std::shared_ptr<RouteAddresses> collection, const String &address)
      {
         std::shared_ptr<RouteAddress> routeAddress = std::shared_ptr<RouteAddress>(new RouteAddress());
         routeAddress->SetRouteID(route->GetID());
         routeAddress->SetAddress(address);

         if (!PersistentRouteAddress::SaveObject(routeAddress))
            return false;

         collection->AddItem(routeAddress);
         return true;
      }

      AnsiString AliasEntryJson(std::shared_ptr<Alias> alias, const Quote &quote)
      {
         // The same three fields, in the same order, as HandleListAliases_.
         AnsiString entry;
         entry.Format("{\"name\":\"%hs\",\"value\":\"%hs\",\"active\":%hs}",
            quote(alias->GetName()).c_str(),
            quote(alias->GetValue()).c_str(),
            alias->GetIsActive() ? "true" : "false");

         return entry;
      }

      // The account as the update answers it: the listing's two fields first
      // and in its order, then every field the update accepts, so a client
      // can PUT back what it was given. No password, no hash, no TOTP secret.
      AnsiString AccountEntryJson(std::shared_ptr<Account> account, const Quote &quote)
      {
         AnsiString entry;
         entry.Format("{\"address\":\"%hs\",\"active\":%hs,\"max_size_mb\":%ld,\"first_name\":\"%hs\",\"last_name\":\"%hs\"",
            quote(account->GetAddress()).c_str(),
            account->GetActive() ? "true" : "false",
            (long) account->GetAccountMaxSize(),
            quote(account->GetPersonFirstName()).c_str(),
            quote(account->GetPersonLastName()).c_str());

         auto flag = [&entry](const char *name, bool value)
         {
            entry += ",\"";
            entry += name;
            entry += value ? "\":true" : "\":false";
         };

         flag("forward_enabled", account->GetForwardEnabled());
         entry += ",\"forward_address\":\"" + quote(account->GetForwardAddress()) + "\"";
         flag("forward_keep_original", account->GetForwardKeepOriginal());
         flag("signature_enabled", account->GetEnableSignature());
         entry += ",\"signature_plain_text\":\"" + quote(account->GetSignaturePlainText()) + "\"";
         entry += ",\"signature_html\":\"" + quote(account->GetSignatureHTML()) + "\"";
         entry += ",\"admin_level\":\"";
         entry += AdminLevelWord(account->GetAdminLevel());
         entry += "\"}";

         return entry;
      }
   }

   AnsiString
   RestApiServer::OpenApiRoutesPaths_()
   {
      // The alias and account paths already have an entry in HandleOpenApi_'s
      // head for their read and delete; the entries here repeat those verbs
      // beside the new one, so that a reader keeping the last of two equal keys
      // still sees the whole path.
      return
         ",\"/api/v1/routes\":{"
         "\"get\":{\"summary\":\"List the SMTP routes\",\"description\":\"The routes the running server delivers by, as the Control Panel lists them. Each entry: id, domain_name (may carry a wildcard), description, target_smtp_host, target_smtp_port, number_of_tries, minutes_between_try, relayer_requires_authentication, relayer_auth_username, treat_security_as_local_domain (the COM name for treat_recipient_as_local_domain; both are given), treat_recipient_as_local_domain, treat_sender_as_local_domain, all_addresses, connection_security (none, starttls_optional, starttls_required or tls) and addresses (the recipients the route accepts when all_addresses is false). The relay password is never returned. Server-wide; refused for domain-restricted keys.\",\"responses\":{\"200\":{\"description\":\"Array of routes\"}}},"
         "\"post\":{\"summary\":\"Create an SMTP route\",\"description\":\"Body: domain_name and target_smtp_host (required); target_smtp_port (default 25), number_of_tries (default 3), minutes_between_try (default 10), relayer_requires_authentication (default false, and then relayer_auth_username is required), relayer_auth_username, relayer_auth_password (write-only), treat_recipient_as_local_domain or treat_security_as_local_domain (default false), treat_sender_as_local_domain (default false), all_addresses (default true), addresses (an array of e-mail addresses) and connection_security (default none). Persisted and put into effect exactly as a route saved in the Control Panel is: the next message to the domain uses it. Server-wide; refused for domain-restricted keys.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"domain_name\",\"target_smtp_host\"],\"properties\":{\"domain_name\":{\"type\":\"string\"},\"description\":{\"type\":\"string\"},\"target_smtp_host\":{\"type\":\"string\"},\"target_smtp_port\":{\"type\":\"integer\"},\"number_of_tries\":{\"type\":\"integer\"},\"minutes_between_try\":{\"type\":\"integer\"},\"relayer_requires_authentication\":{\"type\":\"boolean\"},\"relayer_auth_username\":{\"type\":\"string\"},\"relayer_auth_password\":{\"type\":\"string\"},\"treat_recipient_as_local_domain\":{\"type\":\"boolean\"},\"treat_security_as_local_domain\":{\"type\":\"boolean\"},\"treat_sender_as_local_domain\":{\"type\":\"boolean\"},\"all_addresses\":{\"type\":\"boolean\"},\"addresses\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"connection_security\":{\"type\":\"string\",\"enum\":[\"none\",\"starttls_optional\",\"starttls_required\",\"tls\"]}}}}}},\"responses\":{\"201\":{\"description\":\"Created: the route as the listing shows it\"},\"400\":{\"description\":\"A required field missing, a field of the wrong type or out of range, an unknown field, or the save refused (the reason is in error)\"},\"409\":{\"description\":\"A route for that domain name exists\"}}}}"
         ",\"/api/v1/routes/{id}\":{"
         "\"put\":{\"summary\":\"Replace an SMTP route\",\"description\":\"The whole record, with the same fields, defaults and checks as the create; the address list is replaced by addresses (absent means none). The one exception is relayer_auth_password, which is kept when the body does not name it. The running route is left as it was when the body is refused. Server-wide; refused for domain-restricted keys.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"domain_name\",\"target_smtp_host\"]}}}},\"responses\":{\"200\":{\"description\":\"The route as saved\"},\"400\":{\"description\":\"As for the create\"},\"404\":{\"description\":\"Unknown id\"},\"409\":{\"description\":\"Another route has that domain name\"}}},"
         "\"delete\":{\"summary\":\"Delete an SMTP route\",\"description\":\"The route and its addresses. Server-wide; refused for domain-restricted keys.\",\"responses\":{\"200\":{\"description\":\"Deleted\"},\"404\":{\"description\":\"Unknown id\"}}}}"
         ",\"/api/v1/domains/{domain}/aliases\":{"
         "\"get\":{\"summary\":\"List aliases in a domain\",\"responses\":{\"200\":{\"description\":\"Array of aliases\"},\"404\":{\"description\":\"Unknown domain\"}}},"
         "\"post\":{\"summary\":\"Create an alias\",\"description\":\"Body: name (the alias address, in this domain), value (the e-mail address it delivers to) and active (default true). Judged as the Control Panel judges an alias - a name an account or a distribution list already has, or a domain at its alias limit, is refused with the same sentence - and in effect for the next message to the name. Scoped to the domain.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"name\",\"value\"],\"properties\":{\"name\":{\"type\":\"string\"},\"value\":{\"type\":\"string\"},\"active\":{\"type\":\"boolean\"}}}}}},\"responses\":{\"201\":{\"description\":\"Created: name, value, active\"},\"400\":{\"description\":\"name or value missing or not an address, name outside the domain, or the save refused (the reason is in error)\"},\"404\":{\"description\":\"Unknown domain\"},\"409\":{\"description\":\"An alias with that name exists\"}}}}"
         ",\"/api/v1/aliases/{address}\":{\"delete\":{\"summary\":\"Delete an alias\",\"description\":\"Scoped to the address's domain.\",\"responses\":{\"200\":{\"description\":\"Deleted\"},\"404\":{\"description\":\"Unknown alias\"}}}}"
         ",\"/api/v1/accounts/{address}\":{"
         "\"delete\":{\"summary\":\"Delete an account\",\"responses\":{\"200\":{\"description\":\"Deleted\"},\"404\":{\"description\":\"Unknown account\"}}},"
         "\"put\":{\"summary\":\"Update an account\",\"description\":\"Any subset of active, password, max_size_mb, first_name, last_name, forward_enabled, forward_address, forward_keep_original, signature_enabled, signature_plain_text, signature_html and admin_level (user, domain or server); a field the body does not name is left as it is, and an unknown field is refused by name. A password goes through the password policy, the reuse history and the configured hash exactly as when the Control Panel sets one, and the next logon uses it. Who is asking decides what admin_level may become, as over COM: the administrator password or a key issued for every domain may set all three levels and may update an account that is a server administrator; a key restricted to named domains may set user or domain only, and may not touch an account that is a server administrator. Scoped to the address's domain.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"properties\":{\"active\":{\"type\":\"boolean\"},\"password\":{\"type\":\"string\"},\"max_size_mb\":{\"type\":\"integer\"},\"first_name\":{\"type\":\"string\"},\"last_name\":{\"type\":\"string\"},\"forward_enabled\":{\"type\":\"boolean\"},\"forward_address\":{\"type\":\"string\"},\"forward_keep_original\":{\"type\":\"boolean\"},\"signature_enabled\":{\"type\":\"boolean\"},\"signature_plain_text\":{\"type\":\"string\"},\"signature_html\":{\"type\":\"string\"},\"admin_level\":{\"type\":\"string\",\"enum\":[\"user\",\"domain\",\"server\"]}}}}}},\"responses\":{\"200\":{\"description\":\"The account as saved: address, active, max_size_mb, first_name, last_name, forward_enabled, forward_address, forward_keep_original, signature_enabled, signature_plain_text, signature_html, admin_level\"},\"400\":{\"description\":\"Nothing to update, an unknown field, a field of the wrong type, an admin_level that is not user, domain or server, a password the policy refuses, or the save refused (the reason is in error)\"},\"403\":{\"description\":\"A domain-restricted key naming admin_level server, or updating an account that is a server administrator\"},\"404\":{\"description\":\"Unknown account\"},\"409\":{\"description\":\"The password was used recently on this account\"}}}}";
   }

   // ------------------------------------------------------------------------
   // Routes. The collection every handler works on is the one the SMTP code
   // reads - SMTPConfiguration's - and never a fresh load, so what a listing
   // shows is what delivery will do, and what a create adds is in effect at
   // once. That is also what InterfaceRoutes::LoadSettings hands the Control
   // Panel.

   HttpResponse
   RestApiServer::HandleListRoutes_()
   {
      Quote quote = [](const String &value) { return JsonEscape_(Utf8_(value)); };

      std::shared_ptr<Routes> routes = Configuration::Instance()->GetSMTPConfiguration()->GetRoutes();
      if (!routes)
         return BuildResponse_(200, "[]");

      AnsiString body = "[";
      int count = 0;

      for (std::shared_ptr<HM::Route> route : routes->GetSnapshot())
      {
         if (!route)
            continue;

         if (count > 0)
            body += ",";

         body += RouteEntryJson(route, quote);
         count++;
      }

      body += "]";

      return BuildResponse_(200, body);
   }

   HttpResponse
   RestApiServer::HandleCreateRoute_(const AnsiString &requestBody)
   {
      Quote quote = [](const String &value) { return JsonEscape_(Utf8_(value)); };

      JsonValue body;
      AnsiString error;

      if (!ParseObjectBody(requestBody, body, error))
         return BuildResponse_(400, ErrorBody(quote, String(error)));

      std::shared_ptr<Routes> routes = Configuration::Instance()->GetSMTPConfiguration()->GetRoutes();
      if (!routes)
         return BuildResponse_(500, "{\"error\":\"the routes are not loaded\"}");

      // A fresh Route, as InterfaceRoutes::Add makes one.
      std::shared_ptr<HM::Route> route = std::shared_ptr<HM::Route>(new HM::Route());
      std::vector<String> addresses;

      if (!ApplyRouteBody(body, route, addresses, error))
         return BuildResponse_(400, ErrorBody(quote, String(error)));

      // A duplicate is refused before the save rather than by it, so that it is
      // a 409 and not a 400 carrying the limitation check's sentence - the
      // check itself still runs inside SaveObject, against the same collection.
      if (routes->GetItemByName(route->DomainName()))
         return BuildResponse_(409, "{\"error\":\"a route for that domain name already exists\"}");

      String saveError;

      if (!PersistentRoute::SaveObject(route, saveError, PersistenceModeNormal))
      {
         if (!saveError.IsEmpty())
         {
            LOG_APPLICATION("RestApi: Refused to create route " + route->DomainName() + ": " + saveError);
            return BuildResponse_(400, ErrorBody(quote, saveError));
         }

         return BuildResponse_(500, "{\"error\":\"failed to save route\"}");
      }

      // The addresses, each as InterfaceRouteAddress::Save writes one. The
      // route's collection is fresh (the route has just been given its id), so
      // there is nothing to refresh.
      std::shared_ptr<RouteAddresses> routeAddresses = route->GetAddresses();

      for (const String &address : addresses)
      {
         if (AddRouteAddress(route, routeAddresses, address))
            continue;

         // Half a route - a domain with some of its recipients - would refuse
         // mail the caller asked to be relayed, so the row goes with the
         // addresses that did save. Reported either way: an INSERT into
         // hm_routeaddresses failing is the database's news, not the caller's.
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6490, "RestApiServer::HandleCreateRoute_",
            "The route for " + route->DomainName() + " was saved but its address " + address +
            " could not be, so the route has been removed again.");

         if (!PersistentRoute::DeleteObject(route))
         {
            ErrorManager::Instance()->ReportError(ErrorManager::High, 6491, "RestApiServer::HandleCreateRoute_",
               "The route for " + route->DomainName() + " could not be removed after one of its addresses failed to save. "
               "It exists with an incomplete address list and should be deleted by hand.");
         }

         return BuildResponse_(500, "{\"error\":\"failed to save the route's addresses\"}");
      }

      // AddToParentCollection: from here the running delivery sees the route.
      routes->AddItem(route);

      LOG_APPLICATION("RestApi: Route " + route->DomainName() + " -> " + route->TargetSMTPHost() + ":" +
         StringParser::IntToString((int) route->TargetSMTPPort()) + " created.");

      return BuildResponse_(201, RouteEntryJson(route, quote));
   }

   HttpResponse
   RestApiServer::HandleUpdateRoute_(__int64 routeId, const AnsiString &requestBody)
   {
      Quote quote = [](const String &value) { return JsonEscape_(Utf8_(value)); };

      std::shared_ptr<Routes> routes = Configuration::Instance()->GetSMTPConfiguration()->GetRoutes();
      if (!routes)
         return BuildResponse_(500, "{\"error\":\"the routes are not loaded\"}");

      std::shared_ptr<HM::Route> existing = routes->GetItemByDBID(routeId);
      if (!existing)
         return BuildResponse_(404, "{\"error\":\"route not found\"}");

      JsonValue body;
      AnsiString error;

      if (!ParseObjectBody(requestBody, body, error))
         return BuildResponse_(400, ErrorBody(quote, String(error)));

      // The body is applied to a copy and the copy is what is saved. COM's
      // setters write straight into the running route and Save() can then
      // refuse it, leaving delivery using values the database never accepted;
      // here a refused body leaves the running route untouched.
      std::shared_ptr<HM::Route> updated = std::shared_ptr<HM::Route>(new HM::Route());
      CopyRouteScalars(*existing, *updated);

      std::vector<String> addresses;

      if (!ApplyRouteBody(body, updated, addresses, error))
         return BuildResponse_(400, ErrorBody(quote, String(error)));

      std::shared_ptr<HM::Route> other = routes->GetItemByName(updated->DomainName());
      if (other && other->GetID() != routeId)
         return BuildResponse_(409, "{\"error\":\"another route has that domain name\"}");

      String saveError;

      if (!PersistentRoute::SaveObject(updated, saveError, PersistenceModeNormal))
      {
         if (!saveError.IsEmpty())
         {
            LOG_APPLICATION("RestApi: Refused to update route " + existing->DomainName() + ": " + saveError);
            return BuildResponse_(400, ErrorBody(quote, saveError));
         }

         return BuildResponse_(500, "{\"error\":\"failed to save route\"}");
      }

      // The scalars into the running route, then its address list brought to
      // the body's: rows the body no longer names are deleted through the
      // route's own collection (InterfaceRouteAddresses::DeleteByDBID), rows it
      // adds are saved into it (Add + Save). Unchanged rows keep their ids, and
      // there is no moment at which the route has no addresses at all.
      CopyRouteScalars(*updated, *existing);

      std::shared_ptr<RouteAddresses> routeAddresses = existing->GetAddresses();
      int failures = 0;

      for (std::shared_ptr<RouteAddress> current : routeAddresses->GetSnapshot())
      {
         if (!current)
            continue;

         bool kept = false;
         for (const String &address : addresses)
         {
            if (address.CompareNoCase(current->GetAddress()) == 0)
            {
               kept = true;
               break;
            }
         }

         if (!kept && !routeAddresses->DeleteItemByDBID(current->GetID()))
            failures++;
      }

      for (const String &address : addresses)
      {
         if (routeAddresses->GetItemByName(address))
            continue;

         if (!AddRouteAddress(existing, routeAddresses, address))
         {
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6492, "RestApiServer::HandleUpdateRoute_",
               "The address " + address + " could not be added to the route for " + existing->DomainName() +
               ". The route's other fields were saved.");
            failures++;
         }
      }

      if (failures > 0)
         return BuildResponse_(500, "{\"error\":\"the route was saved but its address list could not be brought up to date\"}");

      LOG_APPLICATION("RestApi: Route " + existing->DomainName() + " -> " + existing->TargetSMTPHost() + ":" +
         StringParser::IntToString((int) existing->TargetSMTPPort()) + " updated.");

      return BuildResponse_(200, RouteEntryJson(existing, quote));
   }

   HttpResponse
   RestApiServer::HandleDeleteRoute_(__int64 routeId)
   {
      std::shared_ptr<Routes> routes = Configuration::Instance()->GetSMTPConfiguration()->GetRoutes();
      if (!routes)
         return BuildResponse_(500, "{\"error\":\"the routes are not loaded\"}");

      std::shared_ptr<HM::Route> route = routes->GetItemByDBID(routeId);
      if (!route)
         return BuildResponse_(404, "{\"error\":\"route not found\"}");

      String domainName = route->DomainName();

      // Through the collection, as InterfaceRoute::Delete goes through its
      // parent: PersistentRoute::DeleteObject removes the addresses and the row,
      // and the collection drops the route only when that succeeded.
      if (!routes->DeleteItemByDBID(routeId))
         return BuildResponse_(500, "{\"error\":\"failed to delete route\"}");

      LOG_APPLICATION("RestApi: Route " + domainName + " deleted.");

      return BuildResponse_(200, "{\"deleted\":true}");
   }

   // ------------------------------------------------------------------------
   // Aliases.

   HttpResponse
   RestApiServer::HandleCreateAlias_(const String &domainName, const AnsiString &requestBody)
   {
      Quote quote = [](const String &value) { return JsonEscape_(Utf8_(value)); };

      JsonValue body;
      AnsiString error;

      if (!ParseObjectBody(requestBody, body, error))
         return BuildResponse_(400, ErrorBody(quote, String(error)));

      static const char *const knownKeys[] = { "name", "value", "active" };

      std::string unknown = FirstUnknownKey(body, knownKeys, sizeof(knownKeys) / sizeof(knownKeys[0]));
      if (!unknown.empty())
         return BuildResponse_(400, ErrorBody(quote, Utf8ToString("unknown field: " + unknown.substr(0, 48))));

      String name;
      String value;
      bool active = true;

      if (!ReadString(body, "name", name, error) ||
          !ReadString(body, "value", value, error) ||
          !ReadBool(body, "active", active, error))
      {
         return BuildResponse_(400, ErrorBody(quote, String(error)));
      }

      name.Trim();
      value.Trim();

      if (name.IsEmpty())
         return BuildResponse_(400, "{\"error\":\"name is required\"}");

      if (value.IsEmpty())
         return BuildResponse_(400, "{\"error\":\"value is required\"}");

      // The name has to be an address in the domain the path names - which is
      // what the domain restriction on the key was checked against - and the
      // value an address for RecipientParser to follow.
      if (!StringParser::IsValidEmailAddress(name) || StringParser::ExtractDomain(name).CompareNoCase(domainName) != 0)
         return BuildResponse_(400, "{\"error\":\"name must be an e-mail address in the domain\"}");

      if (!StringParser::IsValidEmailAddress(value))
         return BuildResponse_(400, "{\"error\":\"value must be an e-mail address\"}");

      Domains domains;
      domains.Refresh();

      std::shared_ptr<Domain> domain = domains.GetItemByName(domainName);
      if (!domain)
         return BuildResponse_(404, "{\"error\":\"domain not found\"}");

      std::shared_ptr<Aliases> aliases = domain->GetAliases();
      if (!aliases)
         return BuildResponse_(500, "{\"error\":\"the domain's aliases are not loaded\"}");

      aliases->Refresh();

      if (aliases->GetItemByName(name))
         return BuildResponse_(409, "{\"error\":\"alias already exists\"}");

      // A fresh Alias, as InterfaceAliases::Add makes one.
      std::shared_ptr<Alias> alias = std::shared_ptr<Alias>(new Alias());
      alias->SetDomainID(domain->GetID());
      alias->SetName(name);
      alias->SetValue(value);
      alias->SetIsActive(active);

      String saveError;

      if (!PersistentAlias::SaveObject(alias, saveError, PersistenceModeNormal))
      {
         // The limitation check's own sentences: a name an account or a list
         // already has, a domain at its alias limit. Fixed text, written for an
         // administrator, which is what makes passing it through safe.
         if (!saveError.IsEmpty())
         {
            LOG_APPLICATION("RestApi: Refused to create alias " + name + ": " + saveError);
            return BuildResponse_(400, ErrorBody(quote, saveError));
         }

         return BuildResponse_(500, "{\"error\":\"failed to save alias\"}");
      }

      // SaveObject has already dropped the name from Cache<Alias>, so the next
      // RCPT TO for it reads the row. AddToParentCollection, for symmetry.
      aliases->AddItem(alias);

      LOG_APPLICATION("RestApi: Alias " + name + " -> " + value + " created.");

      return BuildResponse_(201, AliasEntryJson(alias, quote));
   }

   HttpResponse
   RestApiServer::HandleDeleteAlias_(const String &address)
   {
      String domainName = StringParser::ExtractDomain(address);

      Domains domains;
      domains.Refresh();

      std::shared_ptr<Domain> domain = domains.GetItemByName(domainName);
      if (!domain)
         return BuildResponse_(404, "{\"error\":\"alias not found\"}");

      std::shared_ptr<Aliases> aliases = domain->GetAliases();
      if (!aliases)
         return BuildResponse_(404, "{\"error\":\"alias not found\"}");

      aliases->Refresh();

      std::shared_ptr<Alias> alias = aliases->GetItemByName(address);
      if (!alias)
         return BuildResponse_(404, "{\"error\":\"alias not found\"}");

      // Through the collection, as InterfaceAliases::DeleteByDBID: it is
      // PersistentAlias::DeleteObject, which drops the cache entry too, and the
      // collection reports a refused delete instead of forgetting the object.
      if (!aliases->DeleteItemByDBID(alias->GetID()))
         return BuildResponse_(500, "{\"error\":\"failed to delete alias\"}");

      LOG_APPLICATION("RestApi: Alias " + alias->GetName() + " deleted.");

      return BuildResponse_(200, "{\"deleted\":true}");
   }

   // ------------------------------------------------------------------------
   // The account update.

   HttpResponse
   RestApiServer::HandleUpdateAccount_(const Caller &caller, const String &address, const AnsiString &requestBody)
   {
      Quote quote = [](const String &value) { return JsonEscape_(Utf8_(value)); };

      JsonValue body;
      AnsiString error;

      if (!ParseObjectBody(requestBody, body, error))
         return BuildResponse_(400, ErrorBody(quote, String(error)));

      static const char *const knownKeys[] =
      {
         "active", "password", "max_size_mb", "first_name", "last_name", "forward_enabled",
         "forward_address", "forward_keep_original", "signature_enabled", "signature_plain_text",
         "signature_html", "admin_level"
      };

      std::string unknown = FirstUnknownKey(body, knownKeys, sizeof(knownKeys) / sizeof(knownKeys[0]));
      if (!unknown.empty())
         return BuildResponse_(400, ErrorBody(quote, Utf8ToString("unknown field: " + unknown.substr(0, 48))));

      if (body.Members().empty())
         return BuildResponse_(400, "{\"error\":\"nothing to update\"}");

      std::shared_ptr<Account> account = std::shared_ptr<Account>(new Account());

      if (!PersistentAccount::ReadObject(account, address) || account->GetID() == 0)
         return BuildResponse_(404, "{\"error\":\"account not found\"}");

      // Who is asking decides what may be done to a server administrator's
      // account and whether one may be made, exactly as it does over COM. An
      // empty domain list is the administrator password or a key issued for
      // every domain: the server administrator's authority. A key restricted
      // to named domains carries the domain administrator's, and
      // InterfaceAccount::put_AdminLevel and Save refuse that authority two
      // things: promoting an account to server administrator, and saving an
      // account that already is one - because a server administrator's mailbox
      // can sit in a domain somebody else administers, and its password is a
      // COM logon with the server's whole authority.
      const bool unrestricted = caller.domains.empty();

      if (account->GetAdminLevel() == Account::ServerAdmin && !unrestricted)
      {
         LOG_APPLICATION("RestApi: Refused to update account " + address + " for " + String(caller.identity) +
            ": it is a server administrator and the key is restricted to named domains.");
         return BuildResponse_(403, "{\"error\":\"this api key is restricted to named domains, and a server administrator's account may only be updated by the administrator password or a key issued for every domain\"}");
      }

      bool adminLevelGiven = false;
      Account::AdminLevel adminLevel = account->GetAdminLevel();

      if (HasMember(body, "admin_level"))
      {
         const JsonValue *member = body.Get("admin_level");
         if (!member->IsString() || !ParseAdminLevelWord(member->AsString(), adminLevel))
            return BuildResponse_(400, "{\"error\":\"admin_level must be user, domain or server\"}");

         if (adminLevel == Account::ServerAdmin && !unrestricted)
         {
            LOG_APPLICATION("RestApi: Refused to make account " + address + " a server administrator for " +
               String(caller.identity) + ": the key is restricted to named domains.");
            return BuildResponse_(403, "{\"error\":\"this api key is restricted to named domains and cannot make an account a server administrator; only the administrator password or a key issued for every domain can\"}");
         }

         adminLevelGiven = true;
      }

      bool active = account->GetActive();
      long maxSize = account->GetAccountMaxSize();
      String firstName = account->GetPersonFirstName();
      String lastName = account->GetPersonLastName();
      bool forwardEnabled = account->GetForwardEnabled();
      String forwardAddress = account->GetForwardAddress();
      bool forwardKeepOriginal = account->GetForwardKeepOriginal();
      bool signatureEnabled = account->GetEnableSignature();
      String signaturePlainText = account->GetSignaturePlainText();
      String signatureHtml = account->GetSignatureHTML();
      String password;

      if (!ReadBool(body, "active", active, error) ||
          !ReadInteger(body, "max_size_mb", 0, 2147483647L, maxSize, error) ||
          !ReadString(body, "first_name", firstName, error) ||
          !ReadString(body, "last_name", lastName, error) ||
          !ReadBool(body, "forward_enabled", forwardEnabled, error) ||
          !ReadString(body, "forward_address", forwardAddress, error) ||
          !ReadBool(body, "forward_keep_original", forwardKeepOriginal, error) ||
          !ReadBool(body, "signature_enabled", signatureEnabled, error) ||
          !ReadString(body, "signature_plain_text", signaturePlainText, error) ||
          !ReadString(body, "signature_html", signatureHtml, error) ||
          !ReadString(body, "password", password, error))
      {
         return BuildResponse_(400, ErrorBody(quote, String(error)));
      }

      forwardAddress.Trim();

      // Only when the body touches forwarding, so that an account whose
      // forwarding was left half set over COM can still have its name changed.
      if (HasMember(body, "forward_enabled") || HasMember(body, "forward_address"))
      {
         if (forwardEnabled && forwardAddress.IsEmpty())
            return BuildResponse_(400, "{\"error\":\"forwarding needs an e-mail address\"}");

         if (forwardEnabled && forwardAddress.CompareNoCase(account->GetAddress()) == 0)
            return BuildResponse_(400, "{\"error\":\"forwarding to the account itself would loop\"}");
      }

      // The password, exactly as InterfaceAccount::put_Password: the policy,
      // then the reuse history, then the old hash recorded before the new one
      // replaces it, then the configured hash and the age clock.
      if (HasMember(body, "password"))
      {
         if (password.IsEmpty())
            return BuildResponse_(400, "{\"error\":\"password must not be empty\"}");

         String policyFailure;

         if (!PasswordPolicy::IsAcceptable(account->GetAddress(), password, policyFailure))
         {
            LOG_APPLICATION("RestApi: Refused a password for account " + address + ": " + policyFailure);
            return BuildResponse_(400, ErrorBody(quote, policyFailure));
         }

         if (PasswordHistory::IsReuse(account, password))
            return BuildResponse_(409, "{\"error\":\"This password has been used recently on this account. Choose one that has not.\"}");

         PasswordHistory::Record(account);

         int preferredHashAlgorithm = IniFileSettings::Instance()->GetPreferredHashAlgorithm();
         account->SetPassword(Crypt::Instance()->EnCrypt(password, (Crypt::EncryptionType) preferredHashAlgorithm));
         account->SetPasswordEncryption(preferredHashAlgorithm);
         account->SetPasswordChanged(Time::GetCurrentDateTime());
      }

      if (adminLevelGiven)
         account->SetAdminLevel(adminLevel);

      account->SetActive(active);
      account->SetAccountMaxSize(maxSize);
      account->SetPersonFirstName(firstName);
      account->SetPersonLastName(lastName);
      account->SetForwardEnabled(forwardEnabled);
      account->SetForwardAddress(forwardAddress);
      account->SetForwardKeepOriginal(forwardKeepOriginal);
      account->SetEnableSignature(signatureEnabled);
      account->SetSignaturePlainText(signaturePlainText);
      account->SetSignatureHTML(signatureHtml);

      String saveError;

      // createInbox true, as InterfaceAccount::Save passes it; it only matters
      // for a new row, and this is not one. SaveObject drops the account from
      // Cache<Account>, so the next logon - IMAP, POP3, SMTP AUTH, the portal -
      // reads the row just written.
      if (!PersistentAccount::SaveObject(account, saveError, true, PersistenceModeNormal))
      {
         if (!saveError.IsEmpty())
         {
            LOG_APPLICATION("RestApi: Refused to update account " + address + ": " + saveError);
            return BuildResponse_(400, ErrorBody(quote, saveError));
         }

         return BuildResponse_(500, "{\"error\":\"failed to save account\"}");
      }

      // The field names, never the values: the log is not the place for a
      // signature, and certainly not for a password.
      String changed;
      for (const std::pair<std::string, JsonValue> &member : body.Members())
      {
         if (!changed.IsEmpty())
            changed += ", ";
         changed += String(member.first.c_str());
      }

      LOG_APPLICATION("RestApi: Account " + address + " updated (" + changed + ").");

      return BuildResponse_(200, AccountEntryJson(account, quote));
   }
}
