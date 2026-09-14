// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// The REST API's blocked attachments as a resource. See RestApiServer.h.
//
// The file-name wildcards the anti-virus page's attachment blocking strips
// from incoming messages - AntiVirus.BlockedAttachments over COM, the
// desktop Control Panel's Blocked attachments collection editor - had no REST
// resource. This is one, in the shape of the five collections of
// RestApiAntiSpamLists.cpp: a listing and a create at the collection's path,
// an update and a delete at /{id}, one entry that every route answers with,
// and a body read whole before anything is applied, so that a wrong type, an
// unknown field or a value the owner refuses is a 400 naming it and nothing
// changes:
//
//    /api/v1/blocked-attachments   AntiVirus.BlockedAttachments (InterfaceBlockedAttachment)
//
// The collection is the live one Configuration holds and VirusScanner reads
// for every message it scans, so a created entry is added to it as
// AddToParentCollection adds it, an updated one is the collection's own
// object, and a deleted one goes through the collection's DeleteItemByDBID.
// Whether the list is consulted at all is attachment_blocking_enabled in the
// anti-virus settings group.

#include "StdAfx.h"
#include "RestApiServer.h"
#include "JsonDocument.h"
#include "Unicode.h"
#include "../BO/BlockedAttachments.h"
#include "../BO/BlockedAttachment.h"
#include "../Persistence/PersistentBlockedAttachment.h"
#include "../Persistence/PersistenceMode.h"

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

   AnsiString Quoted(const Bridge &bridge, const String &value)
   {
      return bridge.escape(Utf8(value));
   }

   HttpResponse Refusal(const Bridge &bridge, int status, const String &sentence)
   {
      return bridge.respond(status, "{\"error\":\"" + Quoted(bridge, sentence) + "\"}", "");
   }

   bool ParseObjectBody(const AnsiString &requestBody, JsonValue &body)
   {
      std::string parseError;
      std::string text(requestBody.c_str(), (size_t) requestBody.GetLength());
      return JsonValue::Parse(text, body, parseError) && body.IsObject();
   }

   // The typed read: untouched when the member is absent or null; false,
   // with error naming the member, when it is present with another type.
   bool ReadString(const JsonValue &object, const char *key, String &out, String &error)
   {
      const JsonValue *member = object.Get(key);
      if (!member || member->IsNull())
         return true;

      if (!member->IsString())
      {
         error = String(key) + " must be a string";
         return false;
      }

      out = Utf8ToString(member->AsString());
      return true;
   }

   bool UnknownKey(const JsonValue &object, const char *const *known, size_t knownCount, String &error)
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
            error = String("unknown field: ") + Utf8ToString(member.first.substr(0, 48));
            return true;
         }
      }
      return false;
   }

   std::shared_ptr<BlockedAttachments> Collection()
   {
      return Configuration::Instance()->GetBlockedAttachments();
   }

   AnsiString EntryJson(const Bridge &bridge, const std::shared_ptr<BlockedAttachment> &item)
   {
      AnsiString entry;
      entry.Format("{\"id\":%I64d,\"wildcard\":\"%hs\",\"description\":\"%hs\"}",
         item->GetID(),
         Quoted(bridge, item->GetWildcard()).c_str(),
         Quoted(bridge, item->GetDescription()).c_str());
      return entry;
   }

   // The body onto the item: everything read and checked first, applied
   // last, so a refusal leaves the item as it was. What the value must be is
   // the persistence layer's to say, when the item is saved.
   bool ReadFields(const JsonValue &body, std::shared_ptr<BlockedAttachment> item, String &error)
   {
      static const char *const keys[] = { "wildcard", "description" };
      if (UnknownKey(body, keys, sizeof(keys) / sizeof(keys[0]), error))
         return false;

      String wildcard = item->GetWildcard(), description = item->GetDescription();
      if (!ReadString(body, "wildcard", wildcard, error) ||
          !ReadString(body, "description", description, error))
         return false;

      wildcard.Trim();
      item->SetWildcard(wildcard);
      item->SetDescription(description);
      return true;
   }

   HttpResponse List(const Bridge &bridge)
   {
      std::shared_ptr<BlockedAttachments> collection = Collection();
      if (!collection)
         return Refusal(bridge, 503, "the collection is not loaded");

      AnsiString json = "[";
      int count = 0;
      for (std::shared_ptr<BlockedAttachment> item : collection->GetSnapshot())
      {
         if (!item)
            continue;
         if (count > 0)
            json += ",";
         json += EntryJson(bridge, item);
         count++;
      }
      json += "]";
      return bridge.respond(200, json, "");
   }

   HttpResponse Create(const Bridge &bridge, const AnsiString &requestBody)
   {
      std::shared_ptr<BlockedAttachments> collection = Collection();
      if (!collection)
         return Refusal(bridge, 503, "the collection is not loaded");

      JsonValue body;
      if (!ParseObjectBody(requestBody, body))
         return Refusal(bridge, 400, "the body must be a JSON object");

      std::shared_ptr<BlockedAttachment> item(new BlockedAttachment);
      String error;
      if (!ReadFields(body, item, error))
         return Refusal(bridge, 400, error);

      // Saved, then added to the collection the scanner reads, as
      // InterfaceBlockedAttachment::Save does; the persistence layer's
      // refusal is the answer, in its sentence.
      if (!PersistentBlockedAttachment::SaveObject(item, error, PersistenceModeNormal))
         return Refusal(bridge, error.IsEmpty() ? 500 : 400, error.IsEmpty() ? String("the blocked attachment could not be saved; see the error log") : error);
      collection->AddItem(item);

      LOG_APPLICATION("RestApi: Blocked attachment " + item->GetWildcard() + " created.");

      return bridge.respond(201, EntryJson(bridge, item), "");
   }

   HttpResponse Update(const Bridge &bridge, __int64 id, const AnsiString &requestBody)
   {
      std::shared_ptr<BlockedAttachments> collection = Collection();
      if (!collection)
         return Refusal(bridge, 503, "the collection is not loaded");

      JsonValue body;
      if (!ParseObjectBody(requestBody, body))
         return Refusal(bridge, 400, "the body must be a JSON object");

      std::shared_ptr<BlockedAttachment> item = collection->GetItemByDBID(id);
      if (!item)
         return Refusal(bridge, 404, "blocked attachment not found");

      // Read onto a copy, so that a refusal - the route's or the persistence
      // layer's - leaves the collection's own object as it was.
      std::shared_ptr<BlockedAttachment> changed(new BlockedAttachment(*item));
      String error;
      if (!ReadFields(body, changed, error))
         return Refusal(bridge, 400, error);

      if (!PersistentBlockedAttachment::SaveObject(changed, error, PersistenceModeNormal))
         return Refusal(bridge, error.IsEmpty() ? 500 : 400, error.IsEmpty() ? String("the blocked attachment could not be saved; see the error log") : error);

      item->SetWildcard(changed->GetWildcard());
      item->SetDescription(changed->GetDescription());

      LOG_APPLICATION("RestApi: Blocked attachment " + item->GetWildcard() + " updated.");

      return bridge.respond(200, EntryJson(bridge, item), "");
   }

   HttpResponse Delete(const Bridge &bridge, __int64 id)
   {
      std::shared_ptr<BlockedAttachments> collection = Collection();
      if (!collection)
         return Refusal(bridge, 503, "the collection is not loaded");

      std::shared_ptr<BlockedAttachment> item = collection->GetItemByDBID(id);
      if (!item)
         return Refusal(bridge, 404, "blocked attachment not found");

      String wildcard = item->GetWildcard();
      if (!collection->DeleteItemByDBID(id))
         return Refusal(bridge, 500, "the blocked attachment could not be deleted; see the error log");

      LOG_APPLICATION("RestApi: Blocked attachment " + wildcard + " deleted.");

      return bridge.respond(200, "{\"deleted\":true}", "");
   }

   // The OpenAPI entries, in the shape of the five collections'.
   const char *BlockedAttachmentsPaths =
      ",\"/api/v1/blocked-attachments\":{"
      "\"get\":{\"summary\":\"List the blocked attachments\",\"description\":\"AntiVirus.BlockedAttachments over COM: the file-name wildcards stripped from incoming messages when attachment_blocking_enabled is on in the anti-virus settings. Each entry: id, wildcard, description. Server-wide; refused for domain-restricted keys.\",\"responses\":{\"200\":{\"description\":\"Array of blocked attachments\"}}},"
      "\"post\":{\"summary\":\"Add a blocked attachment\",\"description\":\"Body: wildcard (required; a file-name pattern such as *.exe) and description. Saved as one saved in the Control Panel is, and consulted for the next message scanned. Server-wide; refused for domain-restricted and read-only keys.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"wildcard\"],\"properties\":{\"wildcard\":{\"type\":\"string\"},\"description\":{\"type\":\"string\"}}}}}},\"responses\":{\"201\":{\"description\":\"Created: the entry as the listing shows it\"},\"400\":{\"description\":\"wildcard empty, a field of the wrong type or an unknown field (error names it)\"}}}},"
      "\"/api/v1/blocked-attachments/{id}\":{"
      "\"put\":{\"summary\":\"Change a blocked attachment\",\"description\":\"Body: any subset of the fields POST takes; a field left out keeps its value. Nothing changes when the body is refused. Server-wide; refused for domain-restricted and read-only keys.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"properties\":{\"wildcard\":{\"type\":\"string\"},\"description\":{\"type\":\"string\"}}}}}},\"responses\":{\"200\":{\"description\":\"The entry as saved\"},\"400\":{\"description\":\"A field refused (error names it); nothing changed\"},\"404\":{\"description\":\"Unknown id\"}}},"
      "\"delete\":{\"summary\":\"Remove a blocked attachment\",\"responses\":{\"200\":{\"description\":\"Deleted\"},\"404\":{\"description\":\"Unknown id\"}}}}";
}

namespace HM
{
   AnsiString
   RestApiServer::OpenApiBlockedAttachmentsPaths_()
   {
      return AnsiString(BlockedAttachmentsPaths);
   }

   HttpResponse
   RestApiServer::HandleBlockedAttachments_(RouteKind kind, __int64 id, const AnsiString &requestBody)
   {
      Bridge bridge = { &RestApiServer::JsonEscape_, &RestApiServer::BuildResponse_ };

      switch (kind)
      {
      case RouteBlockedAttachmentList:
         return List(bridge);
      case RouteBlockedAttachmentCreate:
         return Create(bridge, requestBody);
      case RouteBlockedAttachmentUpdate:
         return Update(bridge, id, requestBody);
      case RouteBlockedAttachmentDelete:
         return Delete(bridge, id);
      default:
         break;
      }

      return BuildResponse_(404, "{\"error\":\"not found\"}");
   }
}
