// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// The REST API's five small collections - DNS black lists, SURBL servers, white-list addresses, blocked senders and incoming relays - as resources. See RestApiServer.h.
//
// Five collections the Control Panel edits through COM that had no REST
// resource at all, each in the IP range resource's shape - a listing and a
// create at the collection's path, an update and a delete at /{id}, one
// entry that every route answers with, and a body read whole before anything
// is applied, so that a wrong type, an unknown field or a value the owner
// refuses is a 400 naming it and nothing changes:
//
//    /api/v1/dns-blacklists       AntiSpam.DNSBlackLists   (InterfaceDNSBlackList)
//    /api/v1/surbl-servers        AntiSpam.SURBLServers    (InterfaceSURBLServer)
//    /api/v1/whitelist-addresses  AntiSpam.WhiteListAddresses (InterfaceWhiteListAddress)
//    /api/v1/blocked-senders      AntiSpam.BlockedSenders  (InterfaceBlockedSender)
//    /api/v1/incoming-relays      Settings.IncomingRelays  (InterfaceIncomingRelay)
//
// Each write reaches the same setters and the same Persistent*::SaveObject or
// DeleteObject the COM item's Save and Delete reach, and takes effect the way
// a change from the Control Panel does. The DNS black lists, the SURBL servers
// and the incoming relays are the live collections AntiSpamConfiguration and
// SMTPConfiguration hold, which the spam tests and the SMTP session read for
// every message: a created entry is added to that collection as
// AddToParentCollection adds it, an updated one is the collection's own
// object, and a deleted one goes through the collection's DeleteItemByDBID.
// The white list and the blocked senders are read by the server through
// WhiteListCache and BlockedSenderCache, which the persistence layer marks
// for reload on every save and delete, so those two work on a fresh
// collection, as InterfaceAntiSpam hands one out.

#include "StdAfx.h"
#include "RestApiServer.h"
#include "JsonDocument.h"
#include "Unicode.h"
#include "../BO/DNSBlackLists.h"
#include "../BO/DNSBlackList.h"
#include "../BO/SURBLServers.h"
#include "../BO/SURBLServer.h"
#include "../BO/WhiteListAddresses.h"
#include "../BO/WhiteListAddress.h"
#include "../BO/BlockedSenders.h"
#include "../BO/BlockedSender.h"
#include "../BO/IncomingRelays.h"
#include "../BO/IncomingRelay.h"
#include "../Persistence/PersistentDNSBlackList.h"
#include "../Persistence/PersistentSURBLServer.h"
#include "../Persistence/PersistentWhiteListAddress.h"
#include "../Persistence/PersistentBlockedSender.h"
#include "../Persistence/PersistentIncomingRelay.h"
#include "../TCPIP/IPAddress.h"
#include "../../SMTP/SMTPConfiguration.h"

#include <cmath>
#include <string>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace
{
   using namespace HM;

   // The two private helpers of RestApiServer these functions need, handed
   // in by the member function that owns the right to name them.
   struct Bridge
   {
      AnsiString (*escape)(const AnsiString &value);
      HttpResponse (*respond)(int statusCode, const AnsiString &body, const AnsiString &extraHeaders);
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

   // A String as the escaped UTF-8 that goes between the quotes of a JSON string.
   AnsiString Quoted(const Bridge &bridge, const String &value)
   {
      return bridge.escape(Utf8(value));
   }

   HttpResponse Refusal(const Bridge &bridge, int status, const AnsiString &sentence)
   {
      return bridge.respond(status, "{\"error\":\"" + bridge.escape(sentence) + "\"}", "");
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

   // An address text as an IP address, or false. The two-argument form: a
   // caller's typo is answered with a 400, not written to the ERROR log by
   // the parser as the one-argument form does.
   bool ParseAddress(const String &text, IPAddress &address)
   {
      return address.TryParse(AnsiString(text), false);
   }

   AntiSpamConfiguration &AntiSpam()
   {
      return Configuration::Instance()->GetAntiSpamConfiguration();
   }

   // ------------------------------------------------------------------------
   // DNS black lists and SURBL servers: the same five fields, the same shape,
   // a live collection each.
   // ------------------------------------------------------------------------

   template <class TItem>
   AnsiString DnsListEntryJson(const Bridge &bridge, const std::shared_ptr<TItem> &item)
   {
      AnsiString entry;
      entry.Format("{\"id\":%I64d,\"active\":%hs,\"dns_host\":\"%hs\",\"expected_result\":\"%hs\",\"reject_message\":\"%hs\",\"score\":%d}",
         item->GetID(),
         item->GetIsActive() ? "true" : "false",
         Quoted(bridge, item->GetDNSHost()).c_str(),
         Quoted(bridge, item->GetExpectedResult()).c_str(),
         Quoted(bridge, item->GetRejectMessage()).c_str(),
         item->GetScore());
      return entry;
   }

   // The body onto the item: everything read and checked first, applied last,
   // so a refusal leaves the item as it was. A new item starts active, as
   // the Control Panel's dialog does.
   template <class TItem>
   bool ReadDnsListFields(const JsonValue &body, std::shared_ptr<TItem> item, bool creating, AnsiString &error)
   {
      static const char *const keys[] = { "active", "dns_host", "expected_result", "reject_message", "score" };
      if (UnknownKey(body, keys, sizeof(keys) / sizeof(keys[0]), error))
         return false;

      bool active = creating ? true : item->GetIsActive();
      String host = item->GetDNSHost(), expected = item->GetExpectedResult(), reject = item->GetRejectMessage();
      long score = item->GetScore();

      if (!ReadBool(body, "active", active, error) ||
          !ReadString(body, "dns_host", host, error) ||
          !ReadString(body, "expected_result", expected, error) ||
          !ReadString(body, "reject_message", reject, error) ||
          !ReadInteger(body, "score", -2000000000L, 2000000000L, score, error))
         return false;

      host.Trim();
      if (host.IsEmpty())
      {
         error = "dns_host is required";
         return false;
      }

      item->SetIsActive(active);
      item->SetDNSHost(host);
      item->SetExpectedResult(expected);
      item->SetRejectMessage(reject);
      item->SetScore((int) score);
      return true;
   }

   template <class TCollection, class TItem>
   HttpResponse ListDnsEntries(const Bridge &bridge, std::shared_ptr<TCollection> collection)
   {
      if (!collection)
         return Refusal(bridge, 503, "the collection is not loaded");

      AnsiString json = "[";
      int count = 0;
      for (std::shared_ptr<TItem> item : collection->GetSnapshot())
      {
         if (!item)
            continue;
         if (count > 0)
            json += ",";
         json += DnsListEntryJson(bridge, item);
         count++;
      }
      json += "]";
      return bridge.respond(200, json, "");
   }

   template <class TCollection, class TItem, class TPersistent>
   HttpResponse CreateDnsEntry(const Bridge &bridge, std::shared_ptr<TCollection> collection, const AnsiString &requestBody, const char *noun)
   {
      if (!collection)
         return Refusal(bridge, 503, "the collection is not loaded");

      JsonValue body;
      if (!ParseObjectBody(requestBody, body))
         return Refusal(bridge, 400, "the body must be a JSON object");

      std::shared_ptr<TItem> item(new TItem);
      AnsiString error;
      if (!ReadDnsListFields(body, item, true, error))
         return Refusal(bridge, 400, error);

      // Saved, then added to the collection the server reads, as
      // InterfaceDNSBlackList::Save and InterfaceSURBLServer::Save do.
      if (!TPersistent::SaveObject(item))
         return Refusal(bridge, 500, AnsiString("the ") + noun + " could not be saved; see the error log");
      collection->AddItem(item);

      LOG_APPLICATION("RestApi: " + String(noun) + " " + item->GetDNSHost() + " created.");

      return bridge.respond(201, DnsListEntryJson(bridge, item), "");
   }

   template <class TCollection, class TItem, class TPersistent>
   HttpResponse UpdateDnsEntry(const Bridge &bridge, std::shared_ptr<TCollection> collection, __int64 id, const AnsiString &requestBody, const char *noun)
   {
      if (!collection)
         return Refusal(bridge, 503, "the collection is not loaded");

      JsonValue body;
      if (!ParseObjectBody(requestBody, body))
         return Refusal(bridge, 400, "the body must be a JSON object");

      std::shared_ptr<TItem> item = collection->GetItemByDBID(id);
      if (!item)
         return Refusal(bridge, 404, AnsiString(noun) + " not found");

      AnsiString error;
      if (!ReadDnsListFields(body, item, false, error))
         return Refusal(bridge, 400, error);

      if (!TPersistent::SaveObject(item))
         return Refusal(bridge, 500, AnsiString("the ") + noun + " could not be saved; see the error log");

      LOG_APPLICATION("RestApi: " + String(noun) + " " + item->GetDNSHost() + " updated.");

      return bridge.respond(200, DnsListEntryJson(bridge, item), "");
   }

   template <class TCollection, class TItem>
   HttpResponse DeleteEntry(const Bridge &bridge, std::shared_ptr<TCollection> collection, __int64 id, const char *noun)
   {
      if (!collection)
         return Refusal(bridge, 503, "the collection is not loaded");

      std::shared_ptr<TItem> item = collection->GetItemByDBID(id);
      if (!item)
         return Refusal(bridge, 404, AnsiString(noun) + " not found");

      String name = item->GetName();
      if (!collection->DeleteItemByDBID(id))
         return Refusal(bridge, 500, AnsiString("the ") + noun + " could not be deleted; see the error log");

      LOG_APPLICATION("RestApi: " + String(noun) + " " + name + " deleted.");

      return bridge.respond(200, "{\"deleted\":true}", "");
   }

   // ------------------------------------------------------------------------
   // White-list addresses: an address range and the sender it exempts.
   // ------------------------------------------------------------------------

   AnsiString WhiteListEntryJson(const Bridge &bridge, const std::shared_ptr<WhiteListAddress> &item)
   {
      AnsiString entry;
      entry.Format("{\"id\":%I64d,\"lower_ip\":\"%hs\",\"upper_ip\":\"%hs\",\"email_address\":\"%hs\",\"description\":\"%hs\"}",
         item->GetID(),
         Quoted(bridge, item->GetLowerIPAddressString()).c_str(),
         Quoted(bridge, item->GetUpperIPAddressString()).c_str(),
         Quoted(bridge, item->GetEmailAddress()).c_str(),
         Quoted(bridge, item->GetDescription()).c_str());
      return entry;
   }

   bool ReadWhiteListFields(const JsonValue &body, std::shared_ptr<WhiteListAddress> item, bool creating, AnsiString &error)
   {
      static const char *const keys[] = { "lower_ip", "upper_ip", "email_address", "description" };
      if (UnknownKey(body, keys, sizeof(keys) / sizeof(keys[0]), error))
         return false;

      String lowerText = creating ? String() : item->GetLowerIPAddressString();
      String upperText = creating ? String() : item->GetUpperIPAddressString();
      String email = item->GetEmailAddress(), description = item->GetDescription();

      if (!ReadString(body, "lower_ip", lowerText, error) ||
          !ReadString(body, "upper_ip", upperText, error) ||
          !ReadString(body, "email_address", email, error) ||
          !ReadString(body, "description", description, error))
         return false;

      // What the COM setters do: LowerIPAddress and UpperIPAddress parse, and
      // an address that does not is refused.
      IPAddress lower, upper;
      if (!ParseAddress(lowerText, lower) || !ParseAddress(upperText, upper))
      {
         error = "lower_ip and upper_ip must be IP addresses";
         return false;
      }

      item->SetLowerIPAddress(lower);
      item->SetUpperIPAddress(upper);
      item->SetEMailAddress(email);
      item->SetDescription(description);
      return true;
   }

   HttpResponse ListWhiteList(const Bridge &bridge)
   {
      std::shared_ptr<WhiteListAddresses> collection = AntiSpam().GetWhiteListAddresses();

      AnsiString json = "[";
      int count = 0;
      for (std::shared_ptr<WhiteListAddress> item : collection->GetSnapshot())
      {
         if (!item)
            continue;
         if (count > 0)
            json += ",";
         json += WhiteListEntryJson(bridge, item);
         count++;
      }
      json += "]";
      return bridge.respond(200, json, "");
   }

   HttpResponse CreateWhiteListAddress(const Bridge &bridge, const AnsiString &requestBody)
   {
      JsonValue body;
      if (!ParseObjectBody(requestBody, body))
         return Refusal(bridge, 400, "the body must be a JSON object");

      std::shared_ptr<WhiteListAddress> item(new WhiteListAddress);
      AnsiString error;
      if (!ReadWhiteListFields(body, item, true, error))
         return Refusal(bridge, 400, error);

      // SaveObject marks WhiteListCache for reload, which is what makes the
      // next message see the entry; nothing else holds the list.
      if (!PersistentWhiteListAddress::SaveObject(item))
         return Refusal(bridge, 500, "the white-list address could not be saved; see the error log");

      LOG_APPLICATION("RestApi: White-list address " + item->GetLowerIPAddressString() + " - " + item->GetUpperIPAddressString() + " created.");

      return bridge.respond(201, WhiteListEntryJson(bridge, item), "");
   }

   HttpResponse UpdateWhiteListAddress(const Bridge &bridge, __int64 id, const AnsiString &requestBody)
   {
      JsonValue body;
      if (!ParseObjectBody(requestBody, body))
         return Refusal(bridge, 400, "the body must be a JSON object");

      std::shared_ptr<WhiteListAddresses> collection = AntiSpam().GetWhiteListAddresses();
      std::shared_ptr<WhiteListAddress> item = collection->GetItemByDBID(id);
      if (!item)
         return Refusal(bridge, 404, "white-list address not found");

      AnsiString error;
      if (!ReadWhiteListFields(body, item, false, error))
         return Refusal(bridge, 400, error);

      if (!PersistentWhiteListAddress::SaveObject(item))
         return Refusal(bridge, 500, "the white-list address could not be saved; see the error log");

      LOG_APPLICATION("RestApi: White-list address " + item->GetLowerIPAddressString() + " - " + item->GetUpperIPAddressString() + " updated.");

      return bridge.respond(200, WhiteListEntryJson(bridge, item), "");
   }

   // ------------------------------------------------------------------------
   // Blocked senders: an address or a domain, its score and a note.
   // ------------------------------------------------------------------------

   AnsiString BlockedSenderEntryJson(const Bridge &bridge, const std::shared_ptr<BlockedSender> &item)
   {
      AnsiString entry;
      entry.Format("{\"id\":%I64d,\"address\":\"%hs\",\"score\":%d,\"description\":\"%hs\"}",
         item->GetID(),
         Quoted(bridge, item->GetAddress()).c_str(),
         item->GetScore(),
         Quoted(bridge, item->GetDescription()).c_str());
      return entry;
   }

   bool ReadBlockedSenderFields(const JsonValue &body, std::shared_ptr<BlockedSender> item, AnsiString &error)
   {
      static const char *const keys[] = { "address", "score", "description" };
      if (UnknownKey(body, keys, sizeof(keys) / sizeof(keys[0]), error))
         return false;

      String address = item->GetAddress(), description = item->GetDescription();
      long score = item->GetScore();

      if (!ReadString(body, "address", address, error) ||
          !ReadInteger(body, "score", -2000000000L, 2000000000L, score, error) ||
          !ReadString(body, "description", description, error))
         return false;

      address.Trim();
      if (address.IsEmpty())
      {
         error = "address is required";
         return false;
      }

      item->SetAddress(address);
      item->SetScore((int) score);
      item->SetDescription(description);
      return true;
   }

   HttpResponse ListBlockedSenders(const Bridge &bridge)
   {
      std::shared_ptr<BlockedSenders> collection = AntiSpam().GetBlockedSenders();

      AnsiString json = "[";
      int count = 0;
      for (std::shared_ptr<BlockedSender> item : collection->GetSnapshot())
      {
         if (!item)
            continue;
         if (count > 0)
            json += ",";
         json += BlockedSenderEntryJson(bridge, item);
         count++;
      }
      json += "]";
      return bridge.respond(200, json, "");
   }

   HttpResponse CreateBlockedSender(const Bridge &bridge, const AnsiString &requestBody)
   {
      JsonValue body;
      if (!ParseObjectBody(requestBody, body))
         return Refusal(bridge, 400, "the body must be a JSON object");

      std::shared_ptr<BlockedSender> item(new BlockedSender);
      AnsiString error;
      if (!ReadBlockedSenderFields(body, item, error))
         return Refusal(bridge, 400, error);

      // SaveObject marks BlockedSenderCache for reload, as for the white list.
      if (!PersistentBlockedSender::SaveObject(item))
         return Refusal(bridge, 500, "the blocked sender could not be saved; see the error log");

      LOG_APPLICATION("RestApi: Blocked sender " + item->GetAddress() + " created.");

      return bridge.respond(201, BlockedSenderEntryJson(bridge, item), "");
   }

   HttpResponse UpdateBlockedSender(const Bridge &bridge, __int64 id, const AnsiString &requestBody)
   {
      JsonValue body;
      if (!ParseObjectBody(requestBody, body))
         return Refusal(bridge, 400, "the body must be a JSON object");

      std::shared_ptr<BlockedSenders> collection = AntiSpam().GetBlockedSenders();
      std::shared_ptr<BlockedSender> item = collection->GetItemByDBID(id);
      if (!item)
         return Refusal(bridge, 404, "blocked sender not found");

      AnsiString error;
      if (!ReadBlockedSenderFields(body, item, error))
         return Refusal(bridge, 400, error);

      if (!PersistentBlockedSender::SaveObject(item))
         return Refusal(bridge, 500, "the blocked sender could not be saved; see the error log");

      LOG_APPLICATION("RestApi: Blocked sender " + item->GetAddress() + " updated.");

      return bridge.respond(200, BlockedSenderEntryJson(bridge, item), "");
   }

   // ------------------------------------------------------------------------
   // Incoming relays: a named address range whose Received headers are
   // skipped when the real sender is looked for.
   // ------------------------------------------------------------------------

   AnsiString IncomingRelayEntryJson(const Bridge &bridge, const std::shared_ptr<IncomingRelay> &item)
   {
      AnsiString entry;
      entry.Format("{\"id\":%I64d,\"name\":\"%hs\",\"lower_ip\":\"%hs\",\"upper_ip\":\"%hs\"}",
         item->GetID(),
         Quoted(bridge, item->GetName()).c_str(),
         Quoted(bridge, item->GetLowerIPString()).c_str(),
         Quoted(bridge, item->GetUpperIPString()).c_str());
      return entry;
   }

   bool ReadIncomingRelayFields(const JsonValue &body, std::shared_ptr<IncomingRelay> item, bool creating, AnsiString &error)
   {
      static const char *const keys[] = { "name", "lower_ip", "upper_ip" };
      if (UnknownKey(body, keys, sizeof(keys) / sizeof(keys[0]), error))
         return false;

      String name = item->GetName();
      String lowerText = creating ? String() : item->GetLowerIPString();
      String upperText = creating ? String() : item->GetUpperIPString();

      if (!ReadString(body, "name", name, error) ||
          !ReadString(body, "lower_ip", lowerText, error) ||
          !ReadString(body, "upper_ip", upperText, error))
         return false;

      name.Trim();
      if (name.IsEmpty())
      {
         error = "name is required";
         return false;
      }

      // What put_LowerIP and put_UpperIP do through SetLowerIPString: parse,
      // and refuse an address that does not.
      IPAddress lower, upper;
      if (!ParseAddress(lowerText, lower) || !ParseAddress(upperText, upper))
      {
         error = "lower_ip and upper_ip must be IP addresses";
         return false;
      }

      item->SetName(name);
      item->SetLowerIP(lower);
      item->SetUpperIP(upper);
      return true;
   }

   HttpResponse ListIncomingRelays(const Bridge &bridge, std::shared_ptr<IncomingRelays> collection)
   {
      if (!collection)
         return Refusal(bridge, 503, "the collection is not loaded");

      AnsiString json = "[";
      int count = 0;
      for (std::shared_ptr<IncomingRelay> item : collection->GetSnapshot())
      {
         if (!item)
            continue;
         if (count > 0)
            json += ",";
         json += IncomingRelayEntryJson(bridge, item);
         count++;
      }
      json += "]";
      return bridge.respond(200, json, "");
   }

   HttpResponse CreateIncomingRelay(const Bridge &bridge, std::shared_ptr<IncomingRelays> collection, const AnsiString &requestBody)
   {
      if (!collection)
         return Refusal(bridge, 503, "the collection is not loaded");

      JsonValue body;
      if (!ParseObjectBody(requestBody, body))
         return Refusal(bridge, 400, "the body must be a JSON object");

      std::shared_ptr<IncomingRelay> item(new IncomingRelay);
      AnsiString error;
      if (!ReadIncomingRelayFields(body, item, true, error))
         return Refusal(bridge, 400, error);

      // Saved, then added to the collection the SMTP session reads, as
      // InterfaceIncomingRelay::Save does.
      if (!PersistentIncomingRelay::SaveObject(item))
         return Refusal(bridge, 500, "the incoming relay could not be saved; see the error log");
      collection->AddItem(item);

      LOG_APPLICATION("RestApi: Incoming relay " + item->GetName() + " created.");

      return bridge.respond(201, IncomingRelayEntryJson(bridge, item), "");
   }

   HttpResponse UpdateIncomingRelay(const Bridge &bridge, std::shared_ptr<IncomingRelays> collection, __int64 id, const AnsiString &requestBody)
   {
      if (!collection)
         return Refusal(bridge, 503, "the collection is not loaded");

      JsonValue body;
      if (!ParseObjectBody(requestBody, body))
         return Refusal(bridge, 400, "the body must be a JSON object");

      std::shared_ptr<IncomingRelay> item = collection->GetItemByDBID(id);
      if (!item)
         return Refusal(bridge, 404, "incoming relay not found");

      AnsiString error;
      if (!ReadIncomingRelayFields(body, item, false, error))
         return Refusal(bridge, 400, error);

      if (!PersistentIncomingRelay::SaveObject(item))
         return Refusal(bridge, 500, "the incoming relay could not be saved; see the error log");

      LOG_APPLICATION("RestApi: Incoming relay " + item->GetName() + " updated.");

      return bridge.respond(200, IncomingRelayEntryJson(bridge, item), "");
   }

   // The OpenAPI entries, one per resource, in the IP range entry's shape.
   const char *AntiSpamListsPaths =
      ",\"/api/v1/dns-blacklists\":{"
      "\"get\":{\"summary\":\"List the DNS black lists\",\"description\":\"AntiSpam.DNSBlackLists over COM. Each entry: id, active, dns_host, expected_result, reject_message, score. Server-wide; refused for domain-restricted keys.\",\"responses\":{\"200\":{\"description\":\"Array of DNS black lists\"}}},"
      "\"post\":{\"summary\":\"Add a DNS black list\",\"description\":\"Body: dns_host (required, the zone queried), expected_result (the answers that mean listed, 127.0.0.2 or a range or wildcard), reject_message, score and active (default true). Saved as a list saved in the Control Panel is, and consulted for the next message. Server-wide; refused for domain-restricted and read-only keys.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"dns_host\"],\"properties\":{\"dns_host\":{\"type\":\"string\"},\"expected_result\":{\"type\":\"string\"},\"reject_message\":{\"type\":\"string\"},\"score\":{\"type\":\"integer\"},\"active\":{\"type\":\"boolean\"}}}}}},\"responses\":{\"201\":{\"description\":\"Created: the entry as the listing shows it\"},\"400\":{\"description\":\"dns_host missing, a field of the wrong type or an unknown field (error names it)\"}}}},"
      "\"/api/v1/dns-blacklists/{id}\":{"
      "\"put\":{\"summary\":\"Change a DNS black list\",\"description\":\"Body: any subset of the fields POST takes; a field left out keeps its value. Nothing changes when the body is refused. Server-wide; refused for domain-restricted and read-only keys.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"properties\":{\"dns_host\":{\"type\":\"string\"},\"expected_result\":{\"type\":\"string\"},\"reject_message\":{\"type\":\"string\"},\"score\":{\"type\":\"integer\"},\"active\":{\"type\":\"boolean\"}}}}}},\"responses\":{\"200\":{\"description\":\"The entry as saved\"},\"400\":{\"description\":\"A field refused (error names it); nothing changed\"},\"404\":{\"description\":\"Unknown id\"}}},"
      "\"delete\":{\"summary\":\"Remove a DNS black list\",\"responses\":{\"200\":{\"description\":\"Deleted\"},\"404\":{\"description\":\"Unknown id\"}}}},"
      "\"/api/v1/surbl-servers\":{"
      "\"get\":{\"summary\":\"List the SURBL servers\",\"description\":\"AntiSpam.SURBLServers over COM. Each entry: id, active, dns_host, expected_result, reject_message, score. Server-wide; refused for domain-restricted keys.\",\"responses\":{\"200\":{\"description\":\"Array of SURBL servers\"}}},"
      "\"post\":{\"summary\":\"Add a SURBL server\",\"description\":\"Body: dns_host (required, the zone the domains in a message are looked up in), expected_result (the answers that mean listed; empty is any answer but a refusal code), reject_message, score and active (default true). Saved as one saved in the Control Panel is, and consulted for the next message. Server-wide; refused for domain-restricted and read-only keys.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"dns_host\"],\"properties\":{\"dns_host\":{\"type\":\"string\"},\"expected_result\":{\"type\":\"string\"},\"reject_message\":{\"type\":\"string\"},\"score\":{\"type\":\"integer\"},\"active\":{\"type\":\"boolean\"}}}}}},\"responses\":{\"201\":{\"description\":\"Created: the entry as the listing shows it\"},\"400\":{\"description\":\"dns_host missing, a field of the wrong type or an unknown field (error names it)\"}}}},"
      "\"/api/v1/surbl-servers/{id}\":{"
      "\"put\":{\"summary\":\"Change a SURBL server\",\"description\":\"Body: any subset of the fields POST takes; a field left out keeps its value. Nothing changes when the body is refused. Server-wide; refused for domain-restricted and read-only keys.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"properties\":{\"dns_host\":{\"type\":\"string\"},\"expected_result\":{\"type\":\"string\"},\"reject_message\":{\"type\":\"string\"},\"score\":{\"type\":\"integer\"},\"active\":{\"type\":\"boolean\"}}}}}},\"responses\":{\"200\":{\"description\":\"The entry as saved\"},\"400\":{\"description\":\"A field refused (error names it); nothing changed\"},\"404\":{\"description\":\"Unknown id\"}}},"
      "\"delete\":{\"summary\":\"Remove a SURBL server\",\"responses\":{\"200\":{\"description\":\"Deleted\"},\"404\":{\"description\":\"Unknown id\"}}}},"
      "\"/api/v1/whitelist-addresses\":{"
      "\"get\":{\"summary\":\"List the white-list addresses\",\"description\":\"AntiSpam.WhiteListAddresses over COM: the senders and address ranges the spam tests skip. Each entry: id, lower_ip, upper_ip, email_address, description. Server-wide; refused for domain-restricted keys.\",\"responses\":{\"200\":{\"description\":\"Array of white-list addresses\"}}},"
      "\"post\":{\"summary\":\"Add a white-list address\",\"description\":\"Body: lower_ip and upper_ip (required, the address range), email_address (the sender, with wildcards; empty is any) and description. Saved as one saved in the Control Panel is; the white-list cache is reloaded for the next message. Server-wide; refused for domain-restricted and read-only keys.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"lower_ip\",\"upper_ip\"],\"properties\":{\"lower_ip\":{\"type\":\"string\"},\"upper_ip\":{\"type\":\"string\"},\"email_address\":{\"type\":\"string\"},\"description\":{\"type\":\"string\"}}}}}},\"responses\":{\"201\":{\"description\":\"Created: the entry as the listing shows it\"},\"400\":{\"description\":\"An address that does not parse, a field of the wrong type or an unknown field (error names it)\"}}}},"
      "\"/api/v1/whitelist-addresses/{id}\":{"
      "\"put\":{\"summary\":\"Change a white-list address\",\"description\":\"Body: any subset of the fields POST takes; a field left out keeps its value. Nothing changes when the body is refused. Server-wide; refused for domain-restricted and read-only keys.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"properties\":{\"lower_ip\":{\"type\":\"string\"},\"upper_ip\":{\"type\":\"string\"},\"email_address\":{\"type\":\"string\"},\"description\":{\"type\":\"string\"}}}}}},\"responses\":{\"200\":{\"description\":\"The entry as saved\"},\"400\":{\"description\":\"A field refused (error names it); nothing changed\"},\"404\":{\"description\":\"Unknown id\"}}},"
      "\"delete\":{\"summary\":\"Remove a white-list address\",\"responses\":{\"200\":{\"description\":\"Deleted\"},\"404\":{\"description\":\"Unknown id\"}}}},"
      "\"/api/v1/blocked-senders\":{"
      "\"get\":{\"summary\":\"List the blocked senders\",\"description\":\"AntiSpam.BlockedSenders over COM: the envelope senders, by address or by domain, that score. Each entry: id, address, score, description. Server-wide; refused for domain-restricted keys.\",\"responses\":{\"200\":{\"description\":\"Array of blocked senders\"}}},"
      "\"post\":{\"summary\":\"Add a blocked sender\",\"description\":\"Body: address (required; an address, or a domain that covers its subdomains), score and description. Saved as one saved in the Control Panel is; the blocked-sender cache is reloaded for the next message. Server-wide; refused for domain-restricted and read-only keys.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"address\"],\"properties\":{\"address\":{\"type\":\"string\"},\"score\":{\"type\":\"integer\"},\"description\":{\"type\":\"string\"}}}}}},\"responses\":{\"201\":{\"description\":\"Created: the entry as the listing shows it\"},\"400\":{\"description\":\"address missing, a field of the wrong type or an unknown field (error names it)\"}}}},"
      "\"/api/v1/blocked-senders/{id}\":{"
      "\"put\":{\"summary\":\"Change a blocked sender\",\"description\":\"Body: any subset of the fields POST takes; a field left out keeps its value. Nothing changes when the body is refused. Server-wide; refused for domain-restricted and read-only keys.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"properties\":{\"address\":{\"type\":\"string\"},\"score\":{\"type\":\"integer\"},\"description\":{\"type\":\"string\"}}}}}},\"responses\":{\"200\":{\"description\":\"The entry as saved\"},\"400\":{\"description\":\"A field refused (error names it); nothing changed\"},\"404\":{\"description\":\"Unknown id\"}}},"
      "\"delete\":{\"summary\":\"Remove a blocked sender\",\"responses\":{\"200\":{\"description\":\"Deleted\"},\"404\":{\"description\":\"Unknown id\"}}}},"
      "\"/api/v1/incoming-relays\":{"
      "\"get\":{\"summary\":\"List the incoming relays\",\"description\":\"Settings.IncomingRelays over COM: the address ranges whose Received headers are skipped when the real sender of a message is looked for. Each entry: id, name, lower_ip, upper_ip. Server-wide; refused for domain-restricted keys.\",\"responses\":{\"200\":{\"description\":\"Array of incoming relays\"}}},"
      "\"post\":{\"summary\":\"Add an incoming relay\",\"description\":\"Body: name, lower_ip and upper_ip (all required). Saved as one saved in the Control Panel is, and in force for the next session. Server-wide; refused for domain-restricted and read-only keys.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"name\",\"lower_ip\",\"upper_ip\"],\"properties\":{\"name\":{\"type\":\"string\"},\"lower_ip\":{\"type\":\"string\"},\"upper_ip\":{\"type\":\"string\"}}}}}},\"responses\":{\"201\":{\"description\":\"Created: the entry as the listing shows it\"},\"400\":{\"description\":\"name missing, an address that does not parse, a field of the wrong type or an unknown field (error names it)\"}}}},"
      "\"/api/v1/incoming-relays/{id}\":{"
      "\"put\":{\"summary\":\"Change an incoming relay\",\"description\":\"Body: any subset of the fields POST takes; a field left out keeps its value. Nothing changes when the body is refused. Server-wide; refused for domain-restricted and read-only keys.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"},\"lower_ip\":{\"type\":\"string\"},\"upper_ip\":{\"type\":\"string\"}}}}}},\"responses\":{\"200\":{\"description\":\"The entry as saved\"},\"400\":{\"description\":\"A field refused (error names it); nothing changed\"},\"404\":{\"description\":\"Unknown id\"}}},"
      "\"delete\":{\"summary\":\"Remove an incoming relay\",\"responses\":{\"200\":{\"description\":\"Deleted\"},\"404\":{\"description\":\"Unknown id\"}}}}";
}

namespace HM
{
   AnsiString
   RestApiServer::OpenApiAntiSpamListsPaths_()
   {
      return AnsiString(AntiSpamListsPaths);
   }

   // The one entry point the dispatcher reaches for the twenty routes: the
   // kind says which collection and which verb, the id names the entry for
   // the update and the delete.
   HttpResponse
   RestApiServer::HandleAntiSpamLists_(RouteKind kind, __int64 id, const AnsiString &requestBody)
   {
      Bridge bridge = { &RestApiServer::JsonEscape_, &RestApiServer::BuildResponse_ };

      switch (kind)
      {
      case RouteDnsBlackListList:
         return ListDnsEntries<DNSBlackLists, DNSBlackList>(bridge, AntiSpam().GetDNSBlackLists());
      case RouteDnsBlackListCreate:
         return CreateDnsEntry<DNSBlackLists, DNSBlackList, PersistentDNSBlackList>(bridge, AntiSpam().GetDNSBlackLists(), requestBody, "DNS black list");
      case RouteDnsBlackListUpdate:
         return UpdateDnsEntry<DNSBlackLists, DNSBlackList, PersistentDNSBlackList>(bridge, AntiSpam().GetDNSBlackLists(), id, requestBody, "DNS black list");
      case RouteDnsBlackListDelete:
         return DeleteEntry<DNSBlackLists, DNSBlackList>(bridge, AntiSpam().GetDNSBlackLists(), id, "DNS black list");

      case RouteSurblServerList:
         return ListDnsEntries<SURBLServers, SURBLServer>(bridge, AntiSpam().GetSURBLServers());
      case RouteSurblServerCreate:
         return CreateDnsEntry<SURBLServers, SURBLServer, PersistentSURBLServer>(bridge, AntiSpam().GetSURBLServers(), requestBody, "SURBL server");
      case RouteSurblServerUpdate:
         return UpdateDnsEntry<SURBLServers, SURBLServer, PersistentSURBLServer>(bridge, AntiSpam().GetSURBLServers(), id, requestBody, "SURBL server");
      case RouteSurblServerDelete:
         return DeleteEntry<SURBLServers, SURBLServer>(bridge, AntiSpam().GetSURBLServers(), id, "SURBL server");

      case RouteWhiteListAddressList:
         return ListWhiteList(bridge);
      case RouteWhiteListAddressCreate:
         return CreateWhiteListAddress(bridge, requestBody);
      case RouteWhiteListAddressUpdate:
         return UpdateWhiteListAddress(bridge, id, requestBody);
      case RouteWhiteListAddressDelete:
         return DeleteEntry<WhiteListAddresses, WhiteListAddress>(bridge, AntiSpam().GetWhiteListAddresses(), id, "white-list address");

      case RouteBlockedSenderList:
         return ListBlockedSenders(bridge);
      case RouteBlockedSenderCreate:
         return CreateBlockedSender(bridge, requestBody);
      case RouteBlockedSenderUpdate:
         return UpdateBlockedSender(bridge, id, requestBody);
      case RouteBlockedSenderDelete:
         return DeleteEntry<BlockedSenders, BlockedSender>(bridge, AntiSpam().GetBlockedSenders(), id, "blocked sender");

      case RouteIncomingRelayList:
         return ListIncomingRelays(bridge, Configuration::Instance()->GetSMTPConfiguration()->GetIncomingRelays());
      case RouteIncomingRelayCreate:
         return CreateIncomingRelay(bridge, Configuration::Instance()->GetSMTPConfiguration()->GetIncomingRelays(), requestBody);
      case RouteIncomingRelayUpdate:
         return UpdateIncomingRelay(bridge, Configuration::Instance()->GetSMTPConfiguration()->GetIncomingRelays(), id, requestBody);
      case RouteIncomingRelayDelete:
         return DeleteEntry<IncomingRelays, IncomingRelay>(bridge, Configuration::Instance()->GetSMTPConfiguration()->GetIncomingRelays(), id, "incoming relay");

      default:
         break;
      }

      return BuildResponse_(404, "{\"error\":\"not found\"}");
   }
}
