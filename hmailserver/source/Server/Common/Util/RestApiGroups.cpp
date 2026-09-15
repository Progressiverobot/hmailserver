// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// The REST API's account groups as a resource. See RestApiServer.h.
//
// A group is a named set of accounts that a folder permission can name as
// one principal - Settings.Groups, Group and Group.Members over COM, the
// desktop Control Panel's Groups page - and until this unit no route
// carried it, which left the Linux regression suite skipping every fixture
// that puts an account in a group. The resource, in the shape of the small
// collections (RestApiAntiSpamLists.cpp, RestApiBlockedAttachments.cpp): a
// listing and a create at the collection's path, a read, an update and a
// delete at /{id}, and the members beneath a group as their own small
// collection, since a membership is a pair and not a property of either
// side:
//
//    GET    /api/v1/groups                          Settings.Groups
//    POST   /api/v1/groups                          Groups.Add, Group.Save
//    GET    /api/v1/groups/{id}                     Groups.ItemByDBID
//    PUT    /api/v1/groups/{id}                     Group.Name, Group.Save
//    DELETE /api/v1/groups/{id}                     Groups.DeleteByDBID
//    GET    /api/v1/groups/{id}/members             Group.Members
//    POST   /api/v1/groups/{id}/members             GroupMembers.Add, GroupMember.Save
//    DELETE /api/v1/groups/{id}/members/{account}   GroupMembers.DeleteByDBID, by the account's id
//
// The collection is the live one IMAPConfiguration holds and ACLManager
// consults for every decision that names a group, so a created group is
// added to it as AddToParentCollection adds one over COM, an updated one is
// the collection's own object, and a deleted one goes through the
// collection's DeleteItemByDBID - which is PersistentGroup::DeleteObject,
// where the membership rows and the ACL grants that named the group go with
// it. The collection is refreshed from the database before every use, as
// InterfaceSettings::get_Groups refreshes it, so a group the Control Panel
// made a moment ago is here too. A group's members are read from the
// database on every use, as Group::GetMembers reads them and as ACLManager
// reads them for every decision, so a membership written here is in force
// for the next IMAP command and nothing is cached in between.
//
// The name is judged as the Control Panel's save judges it -
// PreSaveLimitationsCheck, whose sentence for a second group of the same
// name is answered as a 409 - with two checks of this unit's own before the
// store is touched: an empty name and one over the column's 255 characters
// are refused as a 400 naming the field, where COM would write the first
// and fail the second inside the INSERT. A member is named by the account's
// id or its address; an account that does not exist is a 400 naming what
// was sent, and one that is a member already is a 409, since the store
// would otherwise hold the pair twice and the desktop page would show it
// twice. A key restricted to named domains is refused the whole resource in
// Authorize_: a group holds accounts of any domain and is a principal on
// any folder, so a domain's key could otherwise put its own account into a
// group another domain's folders trust.

#include "StdAfx.h"
#include "RestApiServer.h"
#include "JsonDocument.h"
#include "Unicode.h"
#include "../Application/Configuration.h"
#include "../BO/Account.h"
#include "../BO/Group.h"
#include "../BO/Groups.h"
#include "../BO/GroupMember.h"
#include "../BO/GroupMembers.h"
#include "../Cache/CacheContainer.h"
#include "../Persistence/PersistentGroup.h"
#include "../Persistence/PersistentGroupMember.h"
#include "../Persistence/PersistenceMode.h"
#include "../../IMAP/IMAPConfiguration.h"

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

   // The width of hm_groups.groupname in every create script.
   const int GroupNameWidth = 255;

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

   // The typed reads: untouched when the member is absent or null; false,
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

   bool ReadId(const JsonValue &object, const char *key, __int64 &out, String &error)
   {
      const JsonValue *member = object.Get(key);
      if (!member || member->IsNull())
         return true;

      double number = member->IsNumber() ? member->AsNumber() : 0.0;
      if (!member->IsNumber() || std::floor(number) != number || number < 1.0 || number > 9007199254740991.0)
      {
         error = String(key) + " must be a whole number above zero";
         return false;
      }

      out = member->AsInt64();
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

   // The live collection, read again from the database first: the
   // Control Panel writes the same table over COM, and ACLManager answers
   // from this object, so what is listed here is what is in force.
   std::shared_ptr<Groups> Collection()
   {
      std::shared_ptr<Groups> groups = Configuration::Instance()->GetIMAPConfiguration()->GetGroups();
      if (groups)
         groups->Refresh();
      return groups;
   }

   AnsiString GroupJson(const Bridge &bridge, const std::shared_ptr<Group> &group)
   {
      AnsiString entry;
      entry.Format("{\"id\":%I64d,\"name\":\"%hs\"}",
         group->GetID(),
         Quoted(bridge, group->GetName()).c_str());
      return entry;
   }

   AnsiString MemberJson(const Bridge &bridge, const std::shared_ptr<GroupMember> &member)
   {
      // The address beside the id, as the desktop page shows the member; an
      // account that no longer exists has none, and the row is still shown
      // rather than hidden, since it is what the store holds.
      String address;
      std::shared_ptr<const Account> account = CacheContainer::Instance()->GetAccount(member->GetAccountID());
      if (account)
         address = account->GetAddress();

      AnsiString entry;
      entry.Format("{\"id\":%I64d,\"group_id\":%I64d,\"account_id\":%I64d,\"account\":\"%hs\"}",
         member->GetID(),
         member->GetGroupID(),
         member->GetAccountID(),
         Quoted(bridge, address).c_str());
      return entry;
   }

   // The name onto the group: read and checked first, applied last, so a
   // refusal leaves the group as it was. Whether another group has the
   // name is the persistence layer's to say, when the group is saved.
   bool ReadName(const JsonValue &body, std::shared_ptr<Group> group, bool required, String &error)
   {
      static const char *const keys[] = { "name" };
      if (UnknownKey(body, keys, sizeof(keys) / sizeof(keys[0]), error))
         return false;

      String name = group->GetName();
      if (!ReadString(body, "name", name, error))
         return false;

      name.Trim();
      if (name.IsEmpty())
      {
         error = required ? "name is required" : "name must not be empty";
         return false;
      }

      if (name.GetLength() > GroupNameWidth)
      {
         error.Format(_T("name must be at most %d characters"), GroupNameWidth);
         return false;
      }

      group->SetName(name);
      return true;
   }

   // The persistence layer's refusal as the answer: its one sentence for a
   // name another group has is a conflict, anything else it says a 400,
   // and silence a 500 with the log to read.
   HttpResponse SaveRefused(const Bridge &bridge, const String &error)
   {
      if (error.IsEmpty())
         return Refusal(bridge, 500, "the group could not be saved; see the error log");

      if (error.Find(_T("already exists")) >= 0)
         return Refusal(bridge, 409, error);

      return Refusal(bridge, 400, error);
   }

   HttpResponse List(const Bridge &bridge)
   {
      std::shared_ptr<Groups> collection = Collection();
      if (!collection)
         return Refusal(bridge, 503, "the collection is not loaded");

      AnsiString json = "[";
      int count = 0;
      for (std::shared_ptr<Group> group : collection->GetSnapshot())
      {
         if (!group)
            continue;
         if (count > 0)
            json += ",";
         json += GroupJson(bridge, group);
         count++;
      }
      json += "]";
      return bridge.respond(200, json, "");
   }

   HttpResponse Create(const Bridge &bridge, const AnsiString &requestBody)
   {
      std::shared_ptr<Groups> collection = Collection();
      if (!collection)
         return Refusal(bridge, 503, "the collection is not loaded");

      JsonValue body;
      if (!ParseObjectBody(requestBody, body))
         return Refusal(bridge, 400, "the body must be a JSON object");

      std::shared_ptr<Group> group(new Group);
      String error;
      if (!ReadName(body, group, true, error))
         return Refusal(bridge, 400, error);

      // Saved, then added to the collection ACLManager reads, as
      // InterfaceGroup::Save does.
      if (!PersistentGroup::SaveObject(group, error, PersistenceModeNormal))
         return SaveRefused(bridge, error);
      collection->AddItem(group);

      LOG_APPLICATION("RestApi: Group " + group->GetName() + " created.");

      return bridge.respond(201, GroupJson(bridge, group), "");
   }

   HttpResponse Get(const Bridge &bridge, __int64 id)
   {
      std::shared_ptr<Groups> collection = Collection();
      if (!collection)
         return Refusal(bridge, 503, "the collection is not loaded");

      std::shared_ptr<Group> group = collection->GetItemByDBID(id);
      if (!group)
         return Refusal(bridge, 404, "group not found");

      return bridge.respond(200, GroupJson(bridge, group), "");
   }

   HttpResponse Update(const Bridge &bridge, __int64 id, const AnsiString &requestBody)
   {
      std::shared_ptr<Groups> collection = Collection();
      if (!collection)
         return Refusal(bridge, 503, "the collection is not loaded");

      JsonValue body;
      if (!ParseObjectBody(requestBody, body))
         return Refusal(bridge, 400, "the body must be a JSON object");

      std::shared_ptr<Group> group = collection->GetItemByDBID(id);
      if (!group)
         return Refusal(bridge, 404, "group not found");

      // Read onto a copy, so that a refusal - the route's or the persistence
      // layer's - leaves the collection's own object as it was.
      std::shared_ptr<Group> changed(new Group(*group));
      String error;
      if (!ReadName(body, changed, false, error))
         return Refusal(bridge, 400, error);

      if (!PersistentGroup::SaveObject(changed, error, PersistenceModeNormal))
         return SaveRefused(bridge, error);

      group->SetName(changed->GetName());

      LOG_APPLICATION("RestApi: Group " + group->GetName() + " updated.");

      return bridge.respond(200, GroupJson(bridge, group), "");
   }

   HttpResponse Delete(const Bridge &bridge, __int64 id)
   {
      std::shared_ptr<Groups> collection = Collection();
      if (!collection)
         return Refusal(bridge, 503, "the collection is not loaded");

      std::shared_ptr<Group> group = collection->GetItemByDBID(id);
      if (!group)
         return Refusal(bridge, 404, "group not found");

      String name = group->GetName();
      if (!collection->DeleteItemByDBID(id))
         return Refusal(bridge, 500, "the group could not be deleted; see the error log");

      LOG_APPLICATION("RestApi: Group " + name + " deleted, with its members and the folder permissions that named it.");

      return bridge.respond(200, "{\"deleted\":true}", "");
   }

   HttpResponse ListMembers(const Bridge &bridge, __int64 groupId)
   {
      std::shared_ptr<Groups> collection = Collection();
      if (!collection)
         return Refusal(bridge, 503, "the collection is not loaded");

      std::shared_ptr<Group> group = collection->GetItemByDBID(groupId);
      if (!group)
         return Refusal(bridge, 404, "group not found");

      AnsiString json = "[";
      int count = 0;
      for (std::shared_ptr<GroupMember> member : group->GetMembers()->GetSnapshot())
      {
         if (!member)
            continue;
         if (count > 0)
            json += ",";
         json += MemberJson(bridge, member);
         count++;
      }
      json += "]";
      return bridge.respond(200, json, "");
   }

   HttpResponse CreateMember(const Bridge &bridge, __int64 groupId, const AnsiString &requestBody)
   {
      std::shared_ptr<Groups> collection = Collection();
      if (!collection)
         return Refusal(bridge, 503, "the collection is not loaded");

      std::shared_ptr<Group> group = collection->GetItemByDBID(groupId);
      if (!group)
         return Refusal(bridge, 404, "group not found");

      JsonValue body;
      if (!ParseObjectBody(requestBody, body))
         return Refusal(bridge, 400, "the body must be a JSON object");

      static const char *const keys[] = { "account_id", "account" };
      String error;
      if (UnknownKey(body, keys, sizeof(keys) / sizeof(keys[0]), error))
         return Refusal(bridge, 400, error);

      __int64 accountId = 0;
      String address;
      if (!ReadId(body, "account_id", accountId, error) ||
          !ReadString(body, "account", address, error))
         return Refusal(bridge, 400, error);

      address.Trim();
      if (accountId == 0 && address.IsEmpty())
         return Refusal(bridge, 400, "account_id or account is required");

      // The account, by whichever the body named; both, and they must be
      // the same account.
      std::shared_ptr<const Account> account;
      if (accountId != 0)
      {
         account = CacheContainer::Instance()->GetAccount(accountId);
         if (!account)
            return Refusal(bridge, 400, "no account with that id");
      }
      if (!address.IsEmpty())
      {
         std::shared_ptr<const Account> named = CacheContainer::Instance()->GetAccount(address);
         if (!named)
            return Refusal(bridge, 400, "no account named " + address);
         if (account && account->GetID() != named->GetID())
            return Refusal(bridge, 400, "account_id and account name different accounts");
         account = named;
      }

      std::shared_ptr<GroupMembers> members = group->GetMembers();
      if (members->UserIsMember(account->GetID()))
         return Refusal(bridge, 409, "the account is already a member of the group");

      // What InterfaceGroupMembers::Add, put_AccountID and Save do.
      std::shared_ptr<GroupMember> member(new GroupMember);
      member->SetGroupID(group->GetID());
      member->SetAccountID(account->GetID());

      if (!PersistentGroupMember::SaveObject(member, error, PersistenceModeNormal))
         return Refusal(bridge, error.IsEmpty() ? 500 : 400, error.IsEmpty() ? String("the member could not be saved; see the error log") : error);

      LOG_APPLICATION("RestApi: " + account->GetAddress() + " added to group " + group->GetName() + ".");

      return bridge.respond(201, MemberJson(bridge, member), "");
   }

   HttpResponse DeleteMember(const Bridge &bridge, __int64 groupId, __int64 accountId)
   {
      std::shared_ptr<Groups> collection = Collection();
      if (!collection)
         return Refusal(bridge, 503, "the collection is not loaded");

      std::shared_ptr<Group> group = collection->GetItemByDBID(groupId);
      if (!group)
         return Refusal(bridge, 404, "group not found");

      std::shared_ptr<GroupMembers> members = group->GetMembers();
      std::shared_ptr<GroupMember> member;
      for (std::shared_ptr<GroupMember> candidate : members->GetSnapshot())
      {
         if (candidate && candidate->GetAccountID() == accountId)
         {
            member = candidate;
            break;
         }
      }
      if (!member)
         return Refusal(bridge, 404, "the account is not a member of the group");

      if (!members->DeleteItemByDBID(member->GetID()))
         return Refusal(bridge, 500, "the member could not be removed; see the error log");

      LOG_APPLICATION(Formatter::Format("RestApi: account {0} removed from group {1}.", accountId, group->GetName()));

      return bridge.respond(200, "{\"deleted\":true}", "");
   }

   // The OpenAPI entries, in the shape of the small collections'.
   const char *GroupsPaths =
      ",\"/api/v1/groups\":{"
      "\"get\":{\"summary\":\"List the account groups\",\"description\":\"Settings.Groups over COM: the named sets of accounts a folder permission can name as one principal. Each entry: id, name. Read from the database on every call, so a group the Control Panel made is here. Server-wide; refused for domain-restricted keys.\",\"responses\":{\"200\":{\"description\":\"Array of groups\"}}},"
      "\"post\":{\"summary\":\"Create a group\",\"description\":\"Body: name (required, at most 255 characters). Saved as Groups.Add and Group.Save save one over COM, with the same check - a second group of the same name is refused - and added to the collection ACLManager consults, so a permission may name it at once. Server-wide; refused for domain-restricted and read-only keys.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"name\"],\"properties\":{\"name\":{\"type\":\"string\"}}}}}},\"responses\":{\"201\":{\"description\":\"Created: id, name\"},\"400\":{\"description\":\"name missing, empty, over 255 characters or not a string, or an unknown field (error names it)\"},\"409\":{\"description\":\"Another group with this name already exists\"}}}},"
      "\"/api/v1/groups/{id}\":{"
      "\"get\":{\"summary\":\"One group\",\"responses\":{\"200\":{\"description\":\"id, name\"},\"404\":{\"description\":\"Unknown id\"}}},"
      "\"put\":{\"summary\":\"Rename a group\",\"description\":\"Body: name. The same check as POST; nothing changes when it is refused, and the permissions that name the group by id keep naming it. Server-wide; refused for domain-restricted and read-only keys.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}}}}}},\"responses\":{\"200\":{\"description\":\"The group as saved\"},\"400\":{\"description\":\"name empty, over 255 characters or not a string, or an unknown field (error names it)\"},\"404\":{\"description\":\"Unknown id\"},\"409\":{\"description\":\"Another group with this name already exists\"}}},"
      "\"delete\":{\"summary\":\"Delete a group\",\"description\":\"What Groups.DeleteByDBID does over COM: the group, its membership rows and every folder permission that named it go together, and the next IMAP command decides without them. Server-wide; refused for domain-restricted and read-only keys.\",\"responses\":{\"200\":{\"description\":\"Deleted\"},\"404\":{\"description\":\"Unknown id\"}}}},"
      "\"/api/v1/groups/{id}/members\":{"
      "\"get\":{\"summary\":\"The accounts in a group\",\"description\":\"Group.Members over COM, read from the database as ACLManager reads them for every decision. Each entry: id (the membership row), group_id, account_id, account (the address; empty when the account no longer exists).\",\"responses\":{\"200\":{\"description\":\"Array of members\"},\"404\":{\"description\":\"Unknown group\"}}},"
      "\"post\":{\"summary\":\"Add an account to a group\",\"description\":\"Body: account_id or account (the address); both, and they must name the same account. What GroupMembers.Add, put_AccountID and Save do over COM; in force for the next IMAP command. Server-wide; refused for domain-restricted and read-only keys.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"properties\":{\"account_id\":{\"type\":\"integer\"},\"account\":{\"type\":\"string\"}}}}}},\"responses\":{\"201\":{\"description\":\"Created: the member as the listing shows it\"},\"400\":{\"description\":\"Neither account_id nor account, an account that does not exist, a field of the wrong type or an unknown field (error names it)\"},\"404\":{\"description\":\"Unknown group\"},\"409\":{\"description\":\"The account is already a member of the group\"}}}},"
      "\"/api/v1/groups/{id}/members/{account_id}\":{\"delete\":{\"summary\":\"Remove an account from a group\",\"description\":\"By the account's id, as the membership is the pair. What GroupMembers.DeleteByDBID does over COM. Server-wide; refused for domain-restricted and read-only keys.\",\"responses\":{\"200\":{\"description\":\"Deleted\"},\"404\":{\"description\":\"Unknown group, or the account is not a member of it\"}}}}";
}

namespace HM
{
   AnsiString
   RestApiServer::OpenApiGroupsPaths_()
   {
      return AnsiString(GroupsPaths);
   }

   HttpResponse
   RestApiServer::HandleGroups_(const Route &route, const AnsiString &requestBody)
   {
      Bridge bridge = { &RestApiServer::JsonEscape_, &RestApiServer::BuildResponse_ };

      switch (route.kind)
      {
      case RouteGroupList:
         return List(bridge);
      case RouteGroupCreate:
         return Create(bridge, requestBody);
      case RouteGroupGet:
         return Get(bridge, route.record_id);
      case RouteGroupUpdate:
         return Update(bridge, route.record_id, requestBody);
      case RouteGroupDelete:
         return Delete(bridge, route.record_id);
      case RouteGroupMemberList:
         return ListMembers(bridge, route.record_id);
      case RouteGroupMemberCreate:
         return CreateMember(bridge, route.record_id, requestBody);
      case RouteGroupMemberDelete:
         return DeleteMember(bridge, route.record_id, route.member_account_id);
      default:
         break;
      }

      return BuildResponse_(404, "{\"error\":\"not found\"}");
   }
}
