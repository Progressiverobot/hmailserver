// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// An account's resources under its address, administered: what Account.AppPasswords and IMAPFolder.Permissions do over COM. See RestApiServer.h.
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
//    GET    /api/v1/accounts/{address}/folders               the account's own folder tree, with ids
//    GET    /api/v1/accounts/{address}/folders/{id}/permissions        a folder's ACL
//    POST   /api/v1/accounts/{address}/folders/{id}/permissions        grant one
//    PUT    /api/v1/accounts/{address}/folders/{id}/permissions/{pid}  change one
//    DELETE /api/v1/accounts/{address}/folders/{id}/permissions/{pid}  revoke one
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
//
// A folder's ACL is the rows of hm_acl for that folder - what
// IMAPFolder.Permissions edits over COM, and what SETACL edits over IMAP. A
// grant is InterfaceIMAPFolderPermissions::Add, the setters and
// InterfaceIMAPFolderPermission::Save, which is PersistentACLPermission::
// SaveObject; a revoke goes through the folder's collection as DeleteByDBID
// does. The shape the store's Validate insists on - a user permission names
// an account and no group, a group permission the reverse, anyone neither -
// is answered as a 400 naming the fault before the store is touched, since
// Validate reports its refusal to the error log, and an account or a group
// that does not exist is refused the same way. The folder owner's own row
// is refused with SETACL's sentence: the owner's rights are implicit and
// total, and a stored row for the owner could only disagree with the truth.
// Nothing is cached in between: ACLManager reads a folder's rows afresh for
// every decision, so a grant written here is in force for the next command.
// The rights are named as the COM interface's eACLPermission names them and
// carried as the letters SETACL takes, so that the three surfaces are one.

#include "StdAfx.h"
#include "RestApiServer.h"
#include "JsonDocument.h"
#include "Unicode.h"
#include "PasswordPolicy.h"
#include "../Application/Logger.h"
#include "../Application/Configuration.h"
#include "../BO/Account.h"
#include "../BO/AppPassword.h"
#include "../BO/AppPasswords.h"
#include "../BO/IMAPFolder.h"
#include "../BO/IMAPFolders.h"
#include "../BO/ACLPermission.h"
#include "../BO/ACLPermissions.h"
#include "../BO/Group.h"
#include "../BO/Groups.h"
#include "../Cache/CacheContainer.h"
#include "../Persistence/PersistentAccount.h"
#include "../Persistence/PersistentAppPassword.h"
#include "../Persistence/PersistentACLPermission.h"
#include "../../IMAP/IMAPConfiguration.h"
#include "../../IMAP/IMAPFolderContainer.h"
#include "../../IMAP/IMAPSpecialUse.h"

#include <cmath>
#include <map>
#include <string>
#include <vector>

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
      void (*appendFolderJson)(std::shared_ptr<const Account> account, std::shared_ptr<IMAPFolders> folders,
                               const String &parentPath, const std::map<__int64, int> &designations,
                               const String &delimiter, AnsiString &json, int depth);
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

   // A database id in the body: a whole number, zero meaning none.
   bool ReadId(const JsonValue &object, const char *key, __int64 &out, AnsiString &error)
   {
      const JsonValue *member = object.Get(key);
      if (!member || member->IsNull())
         return true;

      double number = member->IsNumber() ? member->AsNumber() : 0.0;

      if (!member->IsNumber() || std::floor(number) != number || number < 0.0 || number > 9007199254740992.0)
      {
         error.Format("%hs must be a whole number", key);
         return false;
      }

      out = (__int64) number;
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
   // Folders and their ACLs.
   // ------------------------------------------------------------------------

   // The folder the id names in the account's OWN tree, the one IMAP LIST
   // walks for it; a public folder or another owner's is not this account's.
   std::shared_ptr<IMAPFolder> OwnFolder(std::shared_ptr<Account> account, __int64 folderId)
   {
      std::shared_ptr<IMAPFolders> folders = IMAPFolderContainer::Instance()->GetFoldersForAccount(account->GetID());
      if (!folders)
         return std::shared_ptr<IMAPFolder>();

      return folders->GetItemByDBIDRecursive(folderId);
   }

   HttpResponse ListFolders(const Bridge &bridge, std::shared_ptr<Account> account)
   {
      String delimiter = Configuration::Instance()->GetIMAPConfiguration()->GetHierarchyDelimiter();

      std::shared_ptr<IMAPFolders> folders = IMAPFolderContainer::Instance()->GetFoldersForAccount(account->GetID());

      std::map<__int64, int> designations;
      if (folders)
         IMAPSpecialUse::Resolve(folders, designations);

      // The very entries GET /api/v1/me/folders shows the account itself, so
      // that an id read here is the id the account's own routes know.
      AnsiString json;
      json.Format("{\"delimiter\":\"%hs\",\"folders\":[", bridge.escape(Utf8(delimiter)).c_str());
      bridge.appendFolderJson(account, folders, String(), designations, delimiter, json, 0);
      json += "]}";
      return bridge.respond(200, json, "");
   }

   const char *TypeWord(ACLPermission::ePermissionType type)
   {
      switch (type)
      {
      case ACLPermission::PTGroup:
         return "group";
      case ACLPermission::PTAnyone:
         return "anyone";
      case ACLPermission::PTUser:
      default:
         return "user";
      }
   }

   bool ParseTypeWord(const String &word, ACLPermission::ePermissionType &type)
   {
      if (word.CompareNoCase(_T("user")) == 0)
         type = ACLPermission::PTUser;
      else if (word.CompareNoCase(_T("group")) == 0)
         type = ACLPermission::PTGroup;
      else if (word.CompareNoCase(_T("anyone")) == 0)
         type = ACLPermission::PTAnyone;
      else
         return false;

      return true;
   }

   // The eleven rights, by the names the COM interface's eACLPermission
   // gives them, in the order RFC 4314 lists the letters.
   struct Right
   {
      const char *name;
      ACLPermission::ePermission bit;
   };

   const Right Rights[] =
   {
      { "lookup", ACLPermission::PermissionLookup },
      { "read", ACLPermission::PermissionRead },
      { "write_seen", ACLPermission::PermissionWriteSeen },
      { "write_others", ACLPermission::PermissionWriteOthers },
      { "insert", ACLPermission::PermissionInsert },
      { "post", ACLPermission::PermissionPost },
      { "create", ACLPermission::PermissionCreate },
      { "delete_mailbox", ACLPermission::PermissionDeleteMailbox },
      { "write_deleted", ACLPermission::PermissionWriteDeleted },
      { "expunge", ACLPermission::PermissionExpunge },
      { "administer", ACLPermission::PermissionAdminister },
   };

   const size_t RightCount = sizeof(Rights) / sizeof(Rights[0]);

   // A permission as every route emits it: who it is for by id and by
   // name, and the rights both by name and as SETACL's letters.
   AnsiString PermissionJson(const Bridge &bridge, std::shared_ptr<ACLPermission> permission)
   {
      String accountAddress;
      String groupName;

      if (permission->GetPermissionType() == ACLPermission::PTUser)
      {
         std::shared_ptr<const Account> holder = CacheContainer::Instance()->GetAccount(permission->GetPermissionAccountID());
         if (holder)
            accountAddress = holder->GetAddress();
      }
      else if (permission->GetPermissionType() == ACLPermission::PTGroup)
      {
         Groups groups;
         groups.Refresh();
         std::shared_ptr<Group> group = groups.GetItemByDBID((unsigned __int64) permission->GetPermissionGroupID());
         if (group)
            groupName = group->GetName();
      }

      AnsiString entry;
      entry.Format("{\"id\":%I64d,\"folder_id\":%I64d,\"type\":\"%hs\",\"account_id\":%I64d,\"account\":\"%hs\",\"group_id\":%I64d,\"group\":\"%hs\",\"rights\":{",
         permission->GetID(),
         permission->GetShareFolderID(),
         TypeWord(permission->GetPermissionType()),
         permission->GetPermissionAccountID(),
         bridge.escape(Utf8(accountAddress)).c_str(),
         permission->GetPermissionGroupID(),
         bridge.escape(Utf8(groupName)).c_str());

      for (size_t i = 0; i < RightCount; i++)
      {
         if (i > 0)
            entry += ",";
         entry += "\"";
         entry += Rights[i].name;
         entry += permission->GetAllow(Rights[i].bit) ? "\":true" : "\":false";
      }

      entry += "},\"rights_text\":\"" + bridge.escape(Utf8(permission->GetRights())) + "\"}";
      return entry;
   }

   // The body onto the permission: everything read and checked first,
   // applied last, so a refusal leaves the row as it was. On a create the
   // type is required and a right not named is not granted; on an update a
   // field left out keeps its value, except that a type change drops the
   // account and the group unless the body names them, since the old ones
   // could not belong to the new type.
   bool ApplyPermissionBody(const JsonValue &body, std::shared_ptr<ACLPermission> permission, std::shared_ptr<Account> owner, bool creating, AnsiString &error)
   {
      static const char *const keys[] = { "type", "account_id", "account", "group_id", "group", "rights" };
      if (UnknownKey(body, keys, sizeof(keys) / sizeof(keys[0]), error))
         return false;

      String typeWord;
      if (!ReadString(body, "type", typeWord, error))
         return false;

      ACLPermission::ePermissionType type = creating ? ACLPermission::PTUser : permission->GetPermissionType();
      bool typeGiven = !typeWord.IsEmpty();

      if (creating && !typeGiven)
      {
         error = "type is required: user, group or anyone";
         return false;
      }

      if (typeGiven && !ParseTypeWord(typeWord, type))
      {
         error = "type must be user, group or anyone";
         return false;
      }

      __int64 accountId = creating || (typeGiven && type != permission->GetPermissionType()) ? 0 : permission->GetPermissionAccountID();
      __int64 groupId = creating || (typeGiven && type != permission->GetPermissionType()) ? 0 : permission->GetPermissionGroupID();
      String accountAddress;
      String groupName;

      if (!ReadId(body, "account_id", accountId, error) ||
          !ReadString(body, "account", accountAddress, error) ||
          !ReadId(body, "group_id", groupId, error) ||
          !ReadString(body, "group", groupName, error))
         return false;

      if (!accountAddress.IsEmpty())
      {
         std::shared_ptr<const Account> named = CacheContainer::Instance()->GetAccount(accountAddress);
         if (!named)
         {
            error = "no account with the address " + Utf8(accountAddress);
            return false;
         }
         accountId = named->GetID();
      }

      if (!groupName.IsEmpty())
      {
         Groups groups;
         groups.Refresh();
         std::shared_ptr<Group> named = groups.GetItemByName(groupName);
         if (!named)
         {
            error = "no group named " + Utf8(groupName);
            return false;
         }
         groupId = named->GetID();
      }

      // The shape PersistentACLPermission::Validate insists on, answered
      // here so that a wrong body is a 400 naming it and not an error-log
      // line; then that what is named exists.
      switch (type)
      {
      case ACLPermission::PTUser:
         if (accountId == 0)
         {
            error = "a user permission names the account it is for: account_id, or account (the address)";
            return false;
         }
         if (groupId != 0)
         {
            error = "a user permission names no group";
            return false;
         }
         if (!CacheContainer::Instance()->GetAccount(accountId))
         {
            error.Format("no account with the id %I64d", accountId);
            return false;
         }
         if (accountId == owner->GetID())
         {
            // IMAPCommandSetAcl's sentence for the same request.
            error = "The folder owner's rights are implicit and cannot be changed.";
            return false;
         }
         break;

      case ACLPermission::PTGroup:
         if (groupId == 0)
         {
            error = "a group permission names the group it is for: group_id, or group (the name)";
            return false;
         }
         if (accountId != 0)
         {
            error = "a group permission names no account";
            return false;
         }
         {
            Groups groups;
            groups.Refresh();
            if (!groups.GetItemByDBID((unsigned __int64) groupId))
            {
               error.Format("no group with the id %I64d", groupId);
               return false;
            }
         }
         break;

      case ACLPermission::PTAnyone:
      default:
         if (accountId != 0 || groupId != 0)
         {
            error = "an anyone permission names neither an account nor a group";
            return false;
         }
         break;
      }

      // The rights: an object of true/false by name. Read whole before any
      // is applied, so an unknown name refuses the lot.
      std::vector<std::pair<ACLPermission::ePermission, bool> > changes;
      const JsonValue *rights = body.Get("rights");
      if (rights && !rights->IsNull())
      {
         if (!rights->IsObject())
         {
            error = "rights must be an object of true or false by right: lookup, read, write_seen, write_others, insert, post, create, delete_mailbox, write_deleted, expunge, administer";
            return false;
         }

         for (const std::pair<std::string, JsonValue> &member : rights->Members())
         {
            const Right *found = nullptr;
            for (size_t i = 0; i < RightCount; i++)
            {
               if (member.first == Rights[i].name)
               {
                  found = &Rights[i];
                  break;
               }
            }

            if (!found)
            {
               error = AnsiString("unknown right: ") + AnsiString(member.first.substr(0, 48).c_str());
               return false;
            }

            if (!member.second.IsBool())
            {
               error = AnsiString("the right ") + found->name + " must be true or false";
               return false;
            }

            changes.push_back(std::make_pair(found->bit, member.second.AsBool()));
         }
      }

      permission->SetPermissionType(type);
      permission->SetPermissionAccountID(accountId);
      permission->SetPermissionGroupID(groupId);
      for (size_t i = 0; i < changes.size(); i++)
         permission->SetAllow(changes[i].first, changes[i].second);

      return true;
   }

   HttpResponse ListPermissions(const Bridge &bridge, std::shared_ptr<Account> account, __int64 folderId)
   {
      std::shared_ptr<IMAPFolder> folder = OwnFolder(account, folderId);
      if (!folder)
         return Refusal(bridge, 404, "folder not found");

      // The rows for exactly this folder, read as ACLManager reads them.
      ACLPermissions permissions(folder->GetID());
      permissions.Refresh();

      AnsiString json = "[";
      int count = 0;
      for (std::shared_ptr<ACLPermission> permission : permissions.GetSnapshot())
      {
         if (!permission)
            continue;
         if (count > 0)
            json += ",";
         json += PermissionJson(bridge, permission);
         count++;
      }
      json += "]";
      return bridge.respond(200, json, "");
   }

   HttpResponse CreatePermission(const Bridge &bridge, std::shared_ptr<Account> account, __int64 folderId, const AnsiString &requestBody)
   {
      std::shared_ptr<IMAPFolder> folder = OwnFolder(account, folderId);
      if (!folder)
         return Refusal(bridge, 404, "folder not found");

      JsonValue body;
      if (!ParseObjectBody(requestBody, body))
         return Refusal(bridge, 400, "the body must be a JSON object");

      // As InterfaceIMAPFolderPermissions::Add makes one: for this folder.
      std::shared_ptr<ACLPermission> permission = std::shared_ptr<ACLPermission>(new ACLPermission());
      permission->SetShareFolderID(folder->GetID());

      AnsiString error;
      if (!ApplyPermissionBody(body, permission, account, true, error))
         return Refusal(bridge, 400, String(error));

      if (!PersistentACLPermission::SaveObject(permission))
         return Refusal(bridge, 500, "the permission could not be saved; see the error log");

      LOG_APPLICATION("REST API: permission " + permission->GetRights() + " granted on folder " + folder->GetFolderName() + " of " + account->GetAddress() + " by the administrator.");

      return bridge.respond(201, PermissionJson(bridge, permission), "");
   }

   HttpResponse UpdatePermission(const Bridge &bridge, std::shared_ptr<Account> account, __int64 folderId, __int64 id, const AnsiString &requestBody)
   {
      std::shared_ptr<IMAPFolder> folder = OwnFolder(account, folderId);
      if (!folder)
         return Refusal(bridge, 404, "folder not found");

      JsonValue body;
      if (!ParseObjectBody(requestBody, body))
         return Refusal(bridge, 400, "the body must be a JSON object");

      ACLPermissions permissions(folder->GetID());
      permissions.Refresh();

      std::shared_ptr<ACLPermission> permission = permissions.GetItemByDBID((unsigned __int64) id);
      if (!permission)
         return Refusal(bridge, 404, "permission not found");

      AnsiString error;
      if (!ApplyPermissionBody(body, permission, account, false, error))
         return Refusal(bridge, 400, String(error));

      if (!PersistentACLPermission::SaveObject(permission))
         return Refusal(bridge, 500, "the permission could not be saved; see the error log");

      LOG_APPLICATION("REST API: permission on folder " + folder->GetFolderName() + " of " + account->GetAddress() + " changed to " + permission->GetRights() + " by the administrator.");

      return bridge.respond(200, PermissionJson(bridge, permission), "");
   }

   HttpResponse DeletePermission(const Bridge &bridge, std::shared_ptr<Account> account, __int64 folderId, __int64 id)
   {
      std::shared_ptr<IMAPFolder> folder = OwnFolder(account, folderId);
      if (!folder)
         return Refusal(bridge, 404, "folder not found");

      ACLPermissions permissions(folder->GetID());
      permissions.Refresh();

      std::shared_ptr<ACLPermission> permission = permissions.GetItemByDBID((unsigned __int64) id);
      if (!permission)
         return Refusal(bridge, 404, "permission not found");

      // Through the folder's collection, as InterfaceIMAPFolderPermissions::DeleteByDBID goes.
      if (!permissions.DeleteItemByDBID(id))
         return Refusal(bridge, 500, "the permission could not be removed; see the error log");

      LOG_APPLICATION("REST API: permission on folder " + folder->GetFolderName() + " of " + account->GetAddress() + " revoked by the administrator.");

      return bridge.respond(200, "{\"deleted\":true}", "");
   }

   // ------------------------------------------------------------------------
   // The OpenAPI entries, each beginning with a comma as HandleOpenApi_ asks.
   // ------------------------------------------------------------------------

   const char *AccountResourcesPaths =
      ",\"/api/v1/accounts/{address}/app-passwords\":{"
      "\"get\":{\"summary\":\"An account's app passwords (administrator)\",\"description\":\"What Account.AppPasswords lists over COM: id, name, created, last_used, active - never the password. The same entries the account's own GET /api/v1/me/app-passwords shows. A key restricted to named domains reaches the accounts of those domains only.\",\"responses\":{\"200\":{\"description\":\"app_passwords\"},\"404\":{\"description\":\"No such account\"}}},"
      "\"post\":{\"summary\":\"Make an app password for an account (administrator)\",\"description\":\"Body: name (required: what the password is for), password (optional: a chosen secret of at least 12 characters that the password policy accepts, as InterfaceAppPassword.SetPassword requires; left out, one is generated as Generate does) and active (default true). Saved as InterfaceAppPassword.Save saves it: the store judges the name, the hash and the ceiling of twenty per account, and its sentence is the 400. The answer carries the password in clear text, the only time it exists outside the caller. No account password is asked for - the administrator credential is the proof - and the issue is logged with the account's address.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"name\"],\"properties\":{\"name\":{\"type\":\"string\"},\"password\":{\"type\":\"string\",\"writeOnly\":true},\"active\":{\"type\":\"boolean\"}}}}}},\"responses\":{\"201\":{\"description\":\"id, name, created, last_used, active, password\"},\"400\":{\"description\":\"No name, a chosen password the policy or the floor of twelve refuses, an unknown field, or twenty already\"},\"404\":{\"description\":\"No such account\"}}}},"
      "\"/api/v1/accounts/{address}/app-passwords/{id}\":{\"delete\":{\"summary\":\"Remove an account's app password (administrator)\",\"description\":\"Through the account's collection, as InterfaceAppPasswords.DeleteByDBID goes; an app password of another account is not found here. Logged with the account's address.\",\"responses\":{\"200\":{\"description\":\"deleted true\"},\"404\":{\"description\":\"No such account, or no app password with that id in it\"}}}},"
      "\"/api/v1/accounts/{address}/folders\":{\"get\":{\"summary\":\"An account's folders (administrator)\",\"description\":\"The account's own folder tree - what Account.IMAPFolders is over COM - as GET /api/v1/me/folders shows it to the account itself: delimiter, and folders each with its id, name, path, parent_id, subfolders and the rest of that entry, read here without the account's password so that a folder's id can be named in the permission routes. The public namespace and the folders other owners share with the account are not listed; they are not this account's. A key restricted to named domains reaches the accounts of those domains only.\",\"responses\":{\"200\":{\"description\":\"delimiter, folders\"},\"404\":{\"description\":\"No such account\"}}}},"
      "\"/api/v1/accounts/{address}/folders/{id}/permissions\":{"
      "\"get\":{\"summary\":\"A folder's access-control list (administrator)\",\"description\":\"The rows of the folder's ACL - IMAPFolder.Permissions over COM, GETACL over IMAP - read as ACLManager reads them for every decision. Each: id, folder_id, type (user, group or anyone), account_id and account (the address, for a user permission), group_id and group (the name, for a group permission), rights - the eleven RFC 4314 rights by the names eACLPermission gives them (lookup, read, write_seen, write_others, insert, post, create, delete_mailbox, write_deleted, expunge, administer), each true or false - and rights_text, the same as the letters SETACL takes. The folder is one of the account's own; a public folder is not reached here.\",\"responses\":{\"200\":{\"description\":\"Array of permissions\"},\"404\":{\"description\":\"No such account, or no folder with that id in its tree\"}}},"
      "\"post\":{\"summary\":\"Grant a permission on a folder (administrator)\",\"description\":\"Body: type (required: user, group or anyone); for a user permission account_id or account (the address), for a group permission group_id or group (the name); rights, an object of true/false by right - a right not named is not granted. What InterfaceIMAPFolderPermissions.Add, the setters and Save do, saved by PersistentACLPermission::SaveObject and in force for the next IMAP command. Checked before anything is saved: the shape the store insists on (a user permission names an account and no group, a group permission the reverse, anyone neither), that the account or group exists, and that the account is not the folder's owner - whose rights are implicit and cannot be changed, as SETACL says. Logged with the account's address.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"type\"],\"properties\":{\"type\":{\"type\":\"string\",\"enum\":[\"user\",\"group\",\"anyone\"]},\"account_id\":{\"type\":\"integer\"},\"account\":{\"type\":\"string\"},\"group_id\":{\"type\":\"integer\"},\"group\":{\"type\":\"string\"},\"rights\":{\"type\":\"object\",\"properties\":{\"lookup\":{\"type\":\"boolean\"},\"read\":{\"type\":\"boolean\"},\"write_seen\":{\"type\":\"boolean\"},\"write_others\":{\"type\":\"boolean\"},\"insert\":{\"type\":\"boolean\"},\"post\":{\"type\":\"boolean\"},\"create\":{\"type\":\"boolean\"},\"delete_mailbox\":{\"type\":\"boolean\"},\"write_deleted\":{\"type\":\"boolean\"},\"expunge\":{\"type\":\"boolean\"},\"administer\":{\"type\":\"boolean\"}}}}}}}},\"responses\":{\"201\":{\"description\":\"Created: the permission as the listing shows it, with its id\"},\"400\":{\"description\":\"No type, a type other than the three, an account or group missing or unknown, the folder's owner, an unknown right or field, or a value of the wrong type (error names it); nothing saved\"},\"404\":{\"description\":\"No such account, or no folder with that id in its tree\"}}}},"
      "\"/api/v1/accounts/{address}/folders/{id}/permissions/{pid}\":{"
      "\"put\":{\"summary\":\"Change a permission (administrator)\",\"description\":\"Body: any subset of what POST takes. A field left out keeps its value; a right not named keeps its value; a change of type drops the account and the group unless the body names them, since the old ones could not belong to the new type. The same checks as POST, and nothing changes when one fails.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\"}}}},\"responses\":{\"200\":{\"description\":\"The permission as saved\"},\"400\":{\"description\":\"A field refused; nothing changed\"},\"404\":{\"description\":\"No such account, folder or permission\"}}},"
      "\"delete\":{\"summary\":\"Revoke a permission (administrator)\",\"description\":\"Through the folder's collection as InterfaceIMAPFolderPermissions.DeleteByDBID goes; a permission of another folder is not found here. Logged with the account's address.\",\"responses\":{\"200\":{\"description\":\"deleted true\"},\"404\":{\"description\":\"No such account, folder or permission\"}}}}";
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

      // /folders, /folders/<id>/permissions and /folders/<id>/permissions/<pid>.
      const AnsiString folders = "/folders";

      if (tail == folders)
      {
         if (method == "GET")
            route.kind = RouteAccountFolderList;

         return route.kind != RouteUnknown;
      }

      if (tail.StartsWith(folders + "/"))
      {
         AnsiString rest = tail.Mid(folders.GetLength() + 1);
         int slash = rest.Find("/");
         AnsiString idPart = slash < 0 ? rest : rest.Mid(0, slash);
         AnsiString sub = slash < 0 ? AnsiString() : rest.Mid(slash);

         __int64 folderId = 0;
         if (!ParseId(idPart, folderId))
            return false;

         const AnsiString permissions = "/permissions";

         if (sub == permissions)
         {
            if (method == "GET")
               route.kind = RouteAccountFolderPermissionList;
            else if (method == "POST")
               route.kind = RouteAccountFolderPermissionCreate;

            if (route.kind != RouteUnknown)
               route.folder_id = folderId;
            return route.kind != RouteUnknown;
         }

         if (sub.StartsWith(permissions + "/"))
         {
            AnsiString pidPart = sub.Mid(permissions.GetLength() + 1);
            __int64 permissionId = 0;
            if (pidPart.Find("/") < 0 && ParseId(pidPart, permissionId))
            {
               if (method == "PUT")
                  route.kind = RouteAccountFolderPermissionUpdate;
               else if (method == "DELETE")
                  route.kind = RouteAccountFolderPermissionDelete;

               if (route.kind != RouteUnknown)
               {
                  route.folder_id = folderId;
                  route.record_id = permissionId;
               }
            }
            return route.kind != RouteUnknown;
         }

         return false;
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
      Bridge bridge = { &RestApiServer::JsonEscape_, &RestApiServer::BuildResponse_, &RestApiServer::AppPasswordJson_, &RestApiServer::AppendFolderJson_ };

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

      case RouteAccountFolderList:
         return ListFolders(bridge, account);
      case RouteAccountFolderPermissionList:
         return ListPermissions(bridge, account, route.folder_id);
      case RouteAccountFolderPermissionCreate:
         return CreatePermission(bridge, account, route.folder_id, requestBody);
      case RouteAccountFolderPermissionUpdate:
         return UpdatePermission(bridge, account, route.folder_id, route.record_id, requestBody);
      case RouteAccountFolderPermissionDelete:
         return DeletePermission(bridge, account, route.folder_id, route.record_id);
      default:
         break;
      }

      return Refusal(bridge, 404, "not found");
   }
}
