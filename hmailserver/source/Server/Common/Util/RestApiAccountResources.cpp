// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// An account's resources under its address, administered: what Account.AppPasswords, IMAPFolder.Permissions and the Messages collections do over COM. See RestApiServer.h.
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
//    PUT    /api/v1/accounts/{address}/app-passwords/{id}    rename it, or revoke it without deleting (active) and put it back
//    DELETE /api/v1/accounts/{address}/app-passwords/{id}    remove one
//
//    GET    /api/v1/accounts/{address}/folders               the account's own folder tree, with ids
//    GET    /api/v1/accounts/{address}/folders/{id}/permissions        a folder's ACL
//    POST   /api/v1/accounts/{address}/folders/{id}/permissions        grant one
//    PUT    /api/v1/accounts/{address}/folders/{id}/permissions/{pid}  change one
//    DELETE /api/v1/accounts/{address}/folders/{id}/permissions/{pid}  revoke one
//
//    GET    /api/v1/accounts/{address}/messages                   every message of the account (Account.Messages)
//    DELETE /api/v1/accounts/{address}/messages                   Account.DeleteMessages
//    GET    /api/v1/accounts/{address}/folders/{id}/messages      a folder's messages, the \Deleted included (IMAPFolder.Messages)
//    POST   /api/v1/accounts/{address}/folders/{id}/messages      a raw message into the folder (Messages.Add + Message.Save)
//    GET    /api/v1/accounts/{address}/messages/{id}              the row, the decoded header fields and every header as written
//    PUT    /api/v1/accounts/{address}/messages/{id}              its flags
//    DELETE /api/v1/accounts/{address}/messages/{id}              Messages.DeleteByDBID
//    GET    /api/v1/accounts/{address}/messages/{id}/source       the file, message/rfc822
//    POST   /api/v1/accounts/{address}/messages/{id}/copy         Message.Copy: the message into another folder of the account
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
//
// The messages are the rows of hm_messages as COM's Message reports them -
// id, uid, folder, account, size, state, the time received, the envelope
// sender and the flags - and a folder's listing is the folder's whole
// collection, the live one every IMAP session shares, so a message flagged
// \Deleted and not yet expunged is in it as it is in COM's; the account's
// own /api/v1/me listing is a reader's view and leaves those out on purpose,
// which is why the fixtures that count a folder after flagging could not run
// on it. A message added here is what Messages.Add and Message.Save do: the
// bytes written where the server keeps a delivered message, the row saved
// with its UID, the folder's sessions told; the flags travel in the query,
// as APPEND carries them beside the literal. A delete is what
// Messages.DeleteByDBID does, and what EXPUNGE does for one message - the
// row and the file go, through the folder's live collection, and every
// session is told. The flags are written on the path STORE takes, with the
// folder's next mod-sequence, so CONDSTORE clients see the change. The
// account's whole is Account.Messages - every folder, in the collection's
// order - and its DELETE is Account.DeleteMessages: PersistentAccount's, with
// the same caches dropped. A copy is Message.Copy to the call -
// MessageUtilities::CopyToIMAPFolder: the file copied, the row written with
// the destination folder's next UID, the folder's sessions told - into a
// folder of the same account's tree, which is the only destination COM's
// Copy reaches either.

#include "StdAfx.h"
#include "RestApiServer.h"
#include "JsonDocument.h"
#include "Unicode.h"
#include "PasswordPolicy.h"
#include "FileUtilities.h"
#include "MessageUtilities.h"
#include "../Application/Logger.h"
#include "../Application/Application.h"
#include "../Application/Configuration.h"
#include "../Application/FolderManager.h"
#include "../BO/Account.h"
#include "../BO/AppPassword.h"
#include "../BO/AppPasswords.h"
#include "../BO/IMAPFolder.h"
#include "../BO/IMAPFolders.h"
#include "../BO/ACLPermission.h"
#include "../BO/ACLPermissions.h"
#include "../BO/Group.h"
#include "../BO/Groups.h"
#include "../BO/Message.h"
#include "../BO/Messages.h"
#include "../Cache/CacheContainer.h"
#include "../Mime/Mime.h"
#include "../Persistence/PersistentAccount.h"
#include "../Persistence/PersistentAppPassword.h"
#include "../Persistence/PersistentACLPermission.h"
#include "../Persistence/PersistentMessage.h"
#include "../Tracking/ChangeNotification.h"
#include "../Tracking/NotificationServer.h"
#include "../../IMAP/IMAPConfiguration.h"
#include "../../IMAP/IMAPFolderContainer.h"
#include "../../IMAP/IMAPSpecialUse.h"
#include "../../IMAP/MessagesContainer.h"

#include <cmath>
#include <fstream>
#include <map>
#include <set>
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

      // The ceiling the account's own route holds (RestApiAppPasswords.cpp):
      // twenty. The store's own check is a backstop of twenty-five, which is
      // how a twenty-first was let through here on 14 September 2026.
      {
         AppPasswords existing;
         existing.Refresh(account->GetID());
         if (existing.GetCount() >= 20)
            return Refusal(bridge, 400, "This account already has the maximum of 20 app passwords; remove one that is no longer used first");
      }
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

   // The two things the COM setters change on a stored app password - its
   // name, and whether it may authenticate, which is how one is revoked
   // without being deleted so it can be put back - saved as Save saves them.
   HttpResponse UpdateAppPassword(const Bridge &bridge, std::shared_ptr<Account> account, __int64 id, const AnsiString &requestBody)
   {
      JsonValue body;
      if (!ParseObjectBody(requestBody, body))
         return Refusal(bridge, 400, "the body must be a JSON object");

      static const char *const keys[] = { "name", "active" };
      AnsiString error;
      if (UnknownKey(body, keys, sizeof(keys) / sizeof(keys[0]), error))
         return Refusal(bridge, 400, String(error));

      AppPasswords list;
      list.Refresh(account->GetID());

      std::shared_ptr<AppPassword> password = list.GetItemByDBID((unsigned __int64) id);
      if (!password || password->GetAccountID() != account->GetID())
         return Refusal(bridge, 404, "no such app password");

      String name = password->GetName();
      bool active = password->GetActive();
      if (!ReadString(body, "name", name, error) ||
          !ReadBool(body, "active", active, error))
         return Refusal(bridge, 400, String(error));

      name.TrimLeft();
      name.TrimRight();
      if (name.IsEmpty())
         return Refusal(bridge, 400, "name is required: what the password is for");
      if (name.GetLength() > 255)
         return Refusal(bridge, 400, "name is at most 255 characters");

      password->SetName(name);
      password->SetActive(active);

      String result;
      if (!PersistentAppPassword::SaveObject(password, result, PersistenceModeNormal))
      {
         if (!result.IsEmpty())
            return Refusal(bridge, 400, result);
         return Refusal(bridge, 500, "the app password could not be saved; see the error log");
      }
      PersistentAppPassword::InvalidateExistenceCache();

      LOG_APPLICATION("REST API: app password \"" + name + "\" of " + account->GetAddress() + (active ? " kept active" : " revoked") + " by the administrator.");

      return bridge.respond(200, bridge.appPasswordJson(password, String()), "");
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
            error = "no such account, or not one this credential may name";
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
            error = "no such account, or not one this credential may name";
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

      // One row per (folder, type, group, account) - the table is unique on
      // them, and the store would answer the duplicate with an integrity
      // error and a 500. Said here, as a conflict, with the way out.
      {
         ACLPermissions existing(folder->GetID());
         existing.Refresh();
         for (std::shared_ptr<ACLPermission> other : existing.GetSnapshot())
         {
            if (other &&
                other->GetPermissionType() == permission->GetPermissionType() &&
                other->GetPermissionAccountID() == permission->GetPermissionAccountID() &&
                other->GetPermissionGroupID() == permission->GetPermissionGroupID())
               return Refusal(bridge, 409, "this folder already has a permission for that account, group or anyone: update it (PUT), or delete it first");
         }
      }
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
   // Messages: the rows, as COM's Message reports them.
   // ------------------------------------------------------------------------

   // The listener admits 16 MB on its large-body routes (RestApiServer.cpp),
   // so that is the ceiling here too; a larger figure was never reachable.
   const __int64 MaxMessageBytes = 16 * 1024 * 1024;
   const int MaxHeaderBytes = 64 * 1024;

   // Every IMAP session on the folder is told, the way APPEND, STORE and
   // EXPUNGE tell them: by message id, which each turns into its own
   // sequence numbers.
   void NotifyFolder(std::shared_ptr<IMAPFolder> folder, ChangeNotification::NotificationType type, const std::vector<__int64> &messageIds)
   {
      std::shared_ptr<ChangeNotification> notification =
         std::shared_ptr<ChangeNotification>(new ChangeNotification(folder->GetAccountID(), folder->GetID(), type, messageIds));

      Application::Instance()->GetNotificationServer()->SendNotification(notification);
   }

   // The value of one query parameter, or nothing: the flags and the
   // envelope sender of a message added, as APPEND carries its flags.
   AnsiString ParameterOf(const AnsiString &query, const AnsiString &name)
   {
      int start = 0;
      while (start <= query.GetLength())
      {
         int end = query.Find("&", start);
         AnsiString pair = end < 0 ? query.Mid(start) : query.Mid(start, end - start);
         if (pair.StartsWith(name + "="))
            return pair.Mid(name.GetLength() + 1);
         if (end < 0)
            break;
         start = end + 1;
      }
      return AnsiString();
   }

   AnsiString FlagsJson(const Bridge &bridge, std::shared_ptr<Message> message)
   {
      AnsiString keywords;
      std::vector<String> list = message->GetKeywordList();
      for (size_t i = 0; i < list.size(); i++)
      {
         if (i > 0)
            keywords += ",";
         keywords += "\"" + bridge.escape(Utf8(list[i])) + "\"";
      }

      AnsiString flags;
      flags.Format("{\"seen\":%hs,\"deleted\":%hs,\"flagged\":%hs,\"answered\":%hs,\"draft\":%hs,\"recent\":%hs,\"keywords\":[%hs]}",
         message->GetFlagSeen() ? "true" : "false",
         message->GetFlagDeleted() ? "true" : "false",
         message->GetFlagFlagged() ? "true" : "false",
         message->GetFlagAnswered() ? "true" : "false",
         message->GetFlagDraft() ? "true" : "false",
         message->GetFlagRecent() ? "true" : "false",
         keywords.c_str());
      return flags;
   }

   // The row, as every message route emits it: what COM's Message reports
   // of the row. Without its closing brace, so the whole-message answer can
   // carry the header fields beside it.
   AnsiString RowFields(const Bridge &bridge, std::shared_ptr<Message> message)
   {
      AnsiString fields;
      fields.Format("\"id\":%I64d,\"uid\":%u,\"folder_id\":%I64d,\"account_id\":%I64d,\"size\":%d,\"state\":%d,\"received\":\"%hs\",\"from_address\":\"%hs\",\"flags\":%hs",
         message->GetID(),
         message->GetUID(),
         message->GetFolderID(),
         message->GetAccountID(),
         message->GetSize(),
         (int) message->GetState(),
         bridge.escape(Utf8(message->GetCreateTime())).c_str(),
         bridge.escape(Utf8(message->GetFromAddress())).c_str(),
         FlagsJson(bridge, message).c_str());
      return fields;
   }

   AnsiString RowJson(const Bridge &bridge, std::shared_ptr<Message> message)
   {
      return "{" + RowFields(bridge, message) + "}";
   }

   // The header block of the file: the five fields a reader asks for by
   // name, decoded, and every field as written, name and value - what
   // Message.Headers and Message.HeaderValue answer over COM. A header block
   // is bytes and the JSON is UTF-8; decoding and re-encoding turns any byte
   // that is not UTF-8 into the replacement character, so the document stays
   // valid whatever an old client wrote in a header.
   AnsiString HeaderJson(const Bridge &bridge, const String &fileName)
   {
      AnsiString header = FileUtilities::Exists(fileName) ? PersistentMessage::LoadHeader(fileName, false) : AnsiString();
      if (header.GetLength() > MaxHeaderBytes)
         header = header.Mid(0, MaxHeaderBytes);

      MimeHeader mimeHeader;
      if (!header.IsEmpty())
         mimeHeader.Load(header.c_str(), header.GetLength(), true);

      AnsiString json;
      json.Format(",\"subject\":\"%hs\",\"from\":\"%hs\",\"to\":\"%hs\",\"cc\":\"%hs\",\"date\":\"%hs\",\"headers\":[",
         bridge.escape(Utf8(mimeHeader.GetUnicodeFieldValue("Subject"))).c_str(),
         bridge.escape(Utf8(mimeHeader.GetUnicodeFieldValue("From"))).c_str(),
         bridge.escape(Utf8(mimeHeader.GetUnicodeFieldValue("To"))).c_str(),
         bridge.escape(Utf8(mimeHeader.GetUnicodeFieldValue("Cc"))).c_str(),
         bridge.escape(Utf8(mimeHeader.GetUnicodeFieldValue("Date"))).c_str());

      int written = 0;
      for (int i = 0; i < mimeHeader.GetFieldCount(); i++)
      {
         MimeField *field = mimeHeader.GetField((unsigned int) i);
         if (!field || !field->GetName())
            continue;

         String name;
         Unicode::MultiByteToWide(AnsiString(field->GetName()), name);
         String value;
         Unicode::MultiByteToWide(AnsiString(field->GetValue() ? field->GetValue() : ""), value);

         if (written > 0)
            json += ",";
         json += "{\"name\":\"" + bridge.escape(Utf8(name)) + "\",\"value\":\"" + bridge.escape(Utf8(value)) + "\"}";
         written++;
      }

      json += "]";
      return json;
   }

   // The message the id names, if it is this account's and in a folder of
   // its tree: the row, and the folder. Another account's message, or one
   // whose folder is not in the tree, is not found: the id space is shared,
   // and an answer that differed would say it exists.
   std::shared_ptr<Message> OwnMessageRow(std::shared_ptr<Account> account, __int64 messageId, std::shared_ptr<IMAPFolder> &folder)
   {
      std::shared_ptr<Message> row = std::shared_ptr<Message>(new Message());
      if (!PersistentMessage::ReadObject(row, messageId) || row->GetID() == 0)
         return std::shared_ptr<Message>();

      if (row->GetAccountID() != account->GetID())
         return std::shared_ptr<Message>();

      folder = OwnFolder(account, row->GetFolderID());
      if (!folder)
         return std::shared_ptr<Message>();

      return row;
   }

   // The live object every IMAP session on the folder shares, since what
   // is changed here must be what they see.
   std::shared_ptr<Message> LiveMessage(std::shared_ptr<IMAPFolder> folder, __int64 messageId)
   {
      std::shared_ptr<Messages> messages = MessagesContainer::Instance()->GetMessages(folder->GetAccountID(), folder->GetID());
      if (!messages)
         return std::shared_ptr<Message>();

      return messages->GetItemByDBID((unsigned __int64) messageId);
   }

   HttpResponse ListFolderMessages(const Bridge &bridge, std::shared_ptr<Account> account, __int64 folderId)
   {
      std::shared_ptr<IMAPFolder> folder = OwnFolder(account, folderId);
      if (!folder)
         return Refusal(bridge, 404, "folder not found");

      // A snapshot of the live collection - the folder's whole, \Deleted
      // included - walked in its own order, which is UID order.
      std::shared_ptr<Messages> messages = MessagesContainer::Instance()->GetMessages(folder->GetAccountID(), folder->GetID());
      std::vector<std::shared_ptr<Message> > snapshot = messages ? messages->GetCopy() : std::vector<std::shared_ptr<Message> >();

      AnsiString json;
      json.Format("{\"folder_id\":%I64d,\"total\":%d,\"messages\":[", folder->GetID(), (int) snapshot.size());
      for (size_t i = 0; i < snapshot.size(); i++)
      {
         if (i > 0)
            json += ",";
         json += RowJson(bridge, snapshot[i]);
      }
      json += "]}";
      return bridge.respond(200, json, "");
   }

   HttpResponse ListAccountMessages(const Bridge &bridge, std::shared_ptr<Account> account)
   {
      // Account.Messages over COM: every message of the account whatever
      // its folder, read fresh as Account::GetMessages reads it.
      std::shared_ptr<Messages> messages = account->GetMessages();
      std::vector<std::shared_ptr<Message> > snapshot = messages ? messages->GetCopy() : std::vector<std::shared_ptr<Message> >();

      AnsiString json;
      json.Format("{\"total\":%d,\"messages\":[", (int) snapshot.size());
      for (size_t i = 0; i < snapshot.size(); i++)
      {
         if (i > 0)
            json += ",";
         json += RowJson(bridge, snapshot[i]);
      }
      json += "]}";
      return bridge.respond(200, json, "");
   }

   // ?flags=seen,flagged,... onto a message being added, as APPEND's flag
   // list goes beside the literal.
   bool ApplyFlagList(const AnsiString &query, std::shared_ptr<Message> message, AnsiString &error)
   {
      AnsiString list = ParameterOf(query, "flags");
      int start = 0;
      while (start <= list.GetLength() && !list.IsEmpty())
      {
         int end = list.Find(",", start);
         AnsiString word = end < 0 ? list.Mid(start) : list.Mid(start, end - start);
         word.TrimLeft();
         word.TrimRight();
         word.ToLower();

         if (word == "seen")
            message->SetFlagSeen(true);
         else if (word == "flagged")
            message->SetFlagFlagged(true);
         else if (word == "answered")
            message->SetFlagAnswered(true);
         else if (word == "draft")
            message->SetFlagDraft(true);
         else if (word == "deleted")
            message->SetFlagDeleted(true);
         else if (!word.IsEmpty())
         {
            error = "unknown flag: " + word.Mid(0, 48) + "; the flags are seen, flagged, answered, draft and deleted";
            return false;
         }

         if (end < 0)
            break;
         start = end + 1;
      }
      return true;
   }

   // One message, the request body, into a folder of the account's: what
   // Messages.Add and Message.Save do over COM, and what APPEND does.
   HttpResponse CreateFolderMessage(const Bridge &bridge, std::shared_ptr<Account> account, __int64 folderId, const AnsiString &requestBody, const AnsiString &query)
   {
      std::shared_ptr<IMAPFolder> folder = OwnFolder(account, folderId);
      if (!folder)
         return Refusal(bridge, 404, "folder not found");

      if (requestBody.size() == 0)
         return Refusal(bridge, 400, "the body is the message, as a .eml file");
      if ((__int64) requestBody.size() > MaxMessageBytes)
         return Refusal(bridge, 413, "a message is at most 16 MB here");

      int colon = requestBody.Find(":");
      int lineEnd = requestBody.Find("\n");
      if (colon < 0 || (lineEnd >= 0 && colon > lineEnd))
         return Refusal(bridge, 400, "the body does not begin with a header line");

      std::shared_ptr<Message> message = std::shared_ptr<Message>(new Message());
      message->SetAccountID(folder->GetAccountID());
      message->SetFolderID(folder->GetID());

      AnsiString error;
      if (!ApplyFlagList(query, message, error))
         return Refusal(bridge, 400, String(error));

      // The envelope sender the row carries, as Message.FromAddress sets it.
      String fromAddress;
      Unicode::MultiByteToWide(ParameterOf(query, "from"), fromAddress);
      fromAddress.Trim();
      message->SetFromAddress(fromAddress);

      // Lines end in CRLF in a stored message; a body that came with bare LF
      // is given them.
      AnsiString text;
      for (int i = 0; i < requestBody.GetLength(); i++)
      {
         char c = requestBody[i];
         if (c == '\n' && (i == 0 || requestBody[i - 1] != '\r'))
            text += '\r';
         text += c;
      }
      if (text.GetLength() < 2 || text.Mid(text.GetLength() - 2) != "\r\n")
         text += "\r\n";

      const String fileName = PersistentMessage::GetFileName(account, message);
      // An account that has never had a message on disk has no directory
      // yet; APPEND makes it on the way, and so does this.
      String directory = FileUtilities::GetFilePath(fileName);
      if (!FileUtilities::Exists(directory) && !FileUtilities::CreateDirectory(directory))
         return Refusal(bridge, 500, "the account's directory could not be made");
      if (!FileUtilities::WriteToFile(fileName, text))
         return Refusal(bridge, 500, "the message could not be written");

      message->SetSize((int) FileUtilities::FileSize(fileName));
      // A message in a folder is a delivered one, as InterfaceMessage::Save
      // marks it before the store sees it; the row gets its UID on the way.
      message->SetState(Message::Delivered);
      if (!PersistentMessage::SaveObject(message))
      {
         FileUtilities::DeleteFile(fileName);
         return Refusal(bridge, 500, "the message could not be stored; see the error log");
      }

      MessagesContainer::Instance()->SetFolderNeedsRefresh(folder->GetID());
      std::vector<__int64> added;
      added.push_back(message->GetID());
      NotifyFolder(folder, ChangeNotification::NotificationMessageAdded, added);

      return bridge.respond(201, RowJson(bridge, message), "");
   }

   HttpResponse GetMessage(const Bridge &bridge, std::shared_ptr<Account> account, __int64 messageId)
   {
      std::shared_ptr<IMAPFolder> folder;
      std::shared_ptr<Message> row = OwnMessageRow(account, messageId, folder);
      if (!row)
         return Refusal(bridge, 404, "message not found");

      // The file's path is the COM Filename: where the bytes are on the
      // server's own disk, for an operator beside it.
      const String fileName = PersistentMessage::GetFileName(account, row);

      AnsiString json = "{" + RowFields(bridge, row) +
         ",\"file\":\"" + bridge.escape(Utf8(fileName)) + "\"" +
         ",\"file_exists\":" + (FileUtilities::Exists(fileName) ? "true" : "false") +
         HeaderJson(bridge, fileName) + "}";
      return bridge.respond(200, json, "");
   }

   HttpResponse MessageSource(const Bridge &bridge, std::shared_ptr<Account> account, __int64 messageId)
   {
      std::shared_ptr<IMAPFolder> folder;
      std::shared_ptr<Message> row = OwnMessageRow(account, messageId, folder);
      if (!row)
         return Refusal(bridge, 404, "message not found");

      const String fileName = PersistentMessage::GetFileName(account, row);
      if (!FileUtilities::Exists(fileName))
         return Refusal(bridge, 404, "the message file is missing");

      if ((__int64) FileUtilities::FileSize(fileName) > MaxMessageBytes)
         return Refusal(bridge, 413, "the message is larger than 16 MB; fetch it with a mail client");

      AnsiString bytes;
      {
#ifdef HM_PLATFORM_POSIX
         // A narrow path, because libstdc++ has no wide-path constructor.
         const AnsiString narrowFileName = fileName.c_str();
         std::ifstream in(narrowFileName.c_str(), std::ios::binary);
#else
         std::ifstream in(fileName.c_str(), std::ios::binary);
#endif
         if (!in)
            return Refusal(bridge, 500, "the message file could not be read");
         std::string contents((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
         bytes = AnsiString(contents);
      }

      AnsiString fileLabel;
      fileLabel.Format("message-%I64d.eml", row->GetID());

      HttpResponse response;
      response.status = 200;
      response.content_type = "message/rfc822";
      response.body = bytes;
      response.extra_headers =
         "Content-Disposition: attachment; filename=\"" + fileLabel + "\"\r\n"
         "X-Content-Type-Options: nosniff\r\n"
         "Content-Security-Policy: sandbox\r\n"
         "Cache-Control: no-store\r\n";
      return response;
   }

   HttpResponse UpdateMessageFlags(const Bridge &bridge, std::shared_ptr<Account> account, __int64 messageId, const AnsiString &requestBody)
   {
      std::shared_ptr<IMAPFolder> folder;
      std::shared_ptr<Message> row = OwnMessageRow(account, messageId, folder);
      if (!row)
         return Refusal(bridge, 404, "message not found");

      std::shared_ptr<Message> message = LiveMessage(folder, messageId);
      if (!message)
         return Refusal(bridge, 404, "message not found");

      JsonValue body;
      if (!ParseObjectBody(requestBody, body))
         return Refusal(bridge, 400, "the body must be a JSON object");

      // Only the flags the body names change; the rest stay as they are,
      // so two clients touching different flags do not undo each other.
      static const char *const names[] = { "seen", "deleted", "flagged", "answered", "draft" };
      AnsiString error;
      if (UnknownKey(body, names, sizeof(names) / sizeof(names[0]), error))
         return Refusal(bridge, 400, String(error));

      bool named[5] = { false, false, false, false, false };
      bool values[5] = { false, false, false, false, false };
      int mentioned = 0;
      for (int i = 0; i < 5; i++)
      {
         const JsonValue *member = body.Get(names[i]);
         if (!member || member->IsNull())
            continue;
         if (!ReadBool(body, names[i], values[i], error))
            return Refusal(bridge, 400, String(error));
         named[i] = true;
         mentioned++;
      }
      if (mentioned == 0)
         return Refusal(bridge, 400, "no flag named: seen, deleted, flagged, answered or draft");

      if (named[0])
         message->SetFlagSeen(values[0]);
      if (named[1])
         message->SetFlagDeleted(values[1]);
      if (named[2])
         message->SetFlagFlagged(values[2]);
      if (named[3])
         message->SetFlagAnswered(values[3]);
      if (named[4])
         message->SetFlagDraft(values[4]);

      // The path STORE takes: the flags are written with the folder's next
      // mod-sequence, so CONDSTORE and QRESYNC clients see the change.
      if (!Application::Instance()->GetFolderManager()->UpdateMessageFlags((int) folder->GetAccountID(), (int) folder->GetID(), message->GetID(), message->GetFlags(), message->GetKeywords()))
         return Refusal(bridge, 500, "the flags could not be stored");

      std::vector<__int64> changed;
      changed.push_back(message->GetID());
      NotifyFolder(folder, ChangeNotification::NotificationMessageFlagsChanged, changed);

      return bridge.respond(200, RowJson(bridge, message), "");
   }

   // What EXPUNGE does for one message, and Messages.DeleteByDBID over COM:
   // the row and the file go, the folder's shared collection drops it, and
   // every session is told.
   HttpResponse DeleteMessage(const Bridge &bridge, std::shared_ptr<Account> account, __int64 messageId)
   {
      std::shared_ptr<IMAPFolder> folder;
      std::shared_ptr<Message> row = OwnMessageRow(account, messageId, folder);
      if (!row)
         return Refusal(bridge, 404, "message not found");

      std::shared_ptr<Messages> messages = MessagesContainer::Instance()->GetMessages(folder->GetAccountID(), folder->GetID());
      if (!messages)
         return Refusal(bridge, 500, "the folder could not be read");

      std::set<__int64> ids;
      ids.insert(messageId);

      std::vector<__int64> deleted = messages->DeleteMessagesById(ids);
      if (deleted.empty())
         return Refusal(bridge, 500, "the message could not be deleted; see the error log");

      NotifyFolder(folder, ChangeNotification::NotificationMessageDeleted, deleted);

      return bridge.respond(200, "{\"deleted\":true}", "");
   }

   HttpResponse CopyMessage(const Bridge &bridge, std::shared_ptr<Account> account, __int64 messageId, const AnsiString &requestBody)
   {
      std::shared_ptr<IMAPFolder> folder;
      std::shared_ptr<Message> row = OwnMessageRow(account, messageId, folder);
      if (!row)
         return Refusal(bridge, 404, "message not found");

      JsonValue body;
      if (!ParseObjectBody(requestBody, body))
         return Refusal(bridge, 400, "the body must be a JSON object");

      static const char *const keys[] = { "folder_id" };
      AnsiString error;
      if (UnknownKey(body, keys, sizeof(keys) / sizeof(keys[0]), error))
         return Refusal(bridge, 400, String(error));

      const JsonValue *folderMember = body.Get("folder_id");
      if (!folderMember || folderMember->IsNull())
         return Refusal(bridge, 400, "folder_id is required");
      if (!folderMember->IsNumber() || std::floor(folderMember->AsNumber()) != folderMember->AsNumber() || folderMember->AsNumber() < 1.0)
         return Refusal(bridge, 400, "folder_id has to be a folder id");

      // The destination is judged here, so that its absence is a 404 naming
      // it rather than the one false CopyToIMAPFolder answers for every
      // failure; the same tree the call itself looks the folder up in.
      const __int64 folderId = folderMember->AsInt64();
      std::shared_ptr<IMAPFolder> destination = OwnFolder(account, folderId);
      if (!destination)
         return Refusal(bridge, 404, "folder not found");

      // Message.Copy over COM, to the call: the file copied, the row saved
      // with the destination's next UID, its sessions told.
      __int64 copyId = 0;
      if (!MessageUtilities::CopyToIMAPFolder(row, (int) folderId, copyId))
         return Refusal(bridge, 500, "the message could not be copied; see the error log");

      std::shared_ptr<IMAPFolder> copyFolder;
      std::shared_ptr<Message> copy = OwnMessageRow(account, copyId, copyFolder);
      if (!copy)
         return Refusal(bridge, 500, "the copy was written but could not be read back; see the error log");

      return bridge.respond(201, "{" + RowFields(bridge, copy) + "}", "");
   }

   HttpResponse DeleteAccountMessages(const Bridge &bridge, std::shared_ptr<Account> account)
   {
      // Account.DeleteMessages over COM, to the call.
      if (!PersistentAccount::DeleteMessages(account))
         return Refusal(bridge, 500, "the messages could not be deleted; see the error log");

      LOG_APPLICATION("REST API: every message of " + account->GetAddress() + " deleted by the administrator.");

      return bridge.respond(200, "{\"deleted\":true}", "");
   }

   // ------------------------------------------------------------------------
   // The OpenAPI entries, each beginning with a comma as HandleOpenApi_ asks.
   // ------------------------------------------------------------------------

   const char *AccountResourcesPaths =
      ",\"/api/v1/accounts/{address}/app-passwords\":{"
      "\"get\":{\"summary\":\"An account's app passwords (administrator)\",\"description\":\"What Account.AppPasswords lists over COM: id, name, created, last_used, active - never the password. The same entries the account's own GET /api/v1/me/app-passwords shows. A key restricted to named domains reaches the accounts of those domains only.\",\"responses\":{\"200\":{\"description\":\"app_passwords\"},\"404\":{\"description\":\"No such account\"}}},"
      "\"post\":{\"summary\":\"Make an app password for an account (administrator)\",\"description\":\"Body: name (required: what the password is for), password (optional: a chosen secret of at least 12 characters that the password policy accepts, as InterfaceAppPassword.SetPassword requires; left out, one is generated as Generate does) and active (default true). Saved as InterfaceAppPassword.Save saves it: the store judges the name, the hash and the ceiling of twenty per account, and its sentence is the 400. The answer carries the password in clear text, the only time it exists outside the caller. No account password is asked for - the administrator credential is the proof - and the issue is logged with the account's address.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"name\"],\"properties\":{\"name\":{\"type\":\"string\"},\"password\":{\"type\":\"string\",\"writeOnly\":true},\"active\":{\"type\":\"boolean\"}}}}}},\"responses\":{\"201\":{\"description\":\"id, name, created, last_used, active, password\"},\"400\":{\"description\":\"No name, a chosen password the policy or the floor of twelve refuses, an unknown field, or twenty already\"},\"404\":{\"description\":\"No such account\"}}}},"
      "\"/api/v1/accounts/{address}/app-passwords/{id}\":{"
      "\"put\":{\"summary\":\"Rename an app password, or revoke it without deleting (administrator)\",\"description\":\"Body: name and/or active - the two things the COM setters change on a stored app password, saved as InterfaceAppPassword.Save saves them. active false refuses the credential without deleting it, so it can be put back with active true; the secret is never changed here (delete and issue another). A field left out keeps its value. Logged with the account's address.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"},\"active\":{\"type\":\"boolean\"}}}}}},\"responses\":{\"200\":{\"description\":\"id, name, created, last_used, active\"},\"400\":{\"description\":\"An empty name, an unknown field, or a value of the wrong type\"},\"404\":{\"description\":\"No such account, or no app password with that id in it\"}}},"
      "\"delete\":{\"summary\":\"Remove an account's app password (administrator)\",\"description\":\"Through the account's collection, as InterfaceAppPasswords.DeleteByDBID goes; an app password of another account is not found here. Logged with the account's address.\",\"responses\":{\"200\":{\"description\":\"deleted true\"},\"404\":{\"description\":\"No such account, or no app password with that id in it\"}}}},"
      "\"/api/v1/accounts/{address}/folders\":{\"get\":{\"summary\":\"An account's folders (administrator)\",\"description\":\"The account's own folder tree - what Account.IMAPFolders is over COM - as GET /api/v1/me/folders shows it to the account itself: delimiter, and folders each with its id, name, path, parent_id, subfolders and the rest of that entry, read here without the account's password so that a folder's id can be named in the permission routes. The public namespace and the folders other owners share with the account are not listed; they are not this account's. A key restricted to named domains reaches the accounts of those domains only.\",\"responses\":{\"200\":{\"description\":\"delimiter, folders\"},\"404\":{\"description\":\"No such account\"}}}},"
      "\"/api/v1/accounts/{address}/folders/{id}/permissions\":{"
      "\"get\":{\"summary\":\"A folder's access-control list (administrator)\",\"description\":\"The rows of the folder's ACL - IMAPFolder.Permissions over COM, GETACL over IMAP - read as ACLManager reads them for every decision. Each: id, folder_id, type (user, group or anyone), account_id and account (the address, for a user permission), group_id and group (the name, for a group permission), rights - the eleven RFC 4314 rights by the names eACLPermission gives them (lookup, read, write_seen, write_others, insert, post, create, delete_mailbox, write_deleted, expunge, administer), each true or false - and rights_text, the same as the letters SETACL takes. The folder is one of the account's own; a public folder is not reached here.\",\"responses\":{\"200\":{\"description\":\"Array of permissions\"},\"404\":{\"description\":\"No such account, or no folder with that id in its tree\"}}},"
      "\"post\":{\"summary\":\"Grant a permission on a folder (administrator)\",\"description\":\"Body: type (required: user, group or anyone); for a user permission account_id or account (the address), for a group permission group_id or group (the name); rights, an object of true/false by right - a right not named is not granted. What InterfaceIMAPFolderPermissions.Add, the setters and Save do, saved by PersistentACLPermission::SaveObject and in force for the next IMAP command. Checked before anything is saved: the shape the store insists on (a user permission names an account and no group, a group permission the reverse, anyone neither), that the account or group exists, and that the account is not the folder's owner - whose rights are implicit and cannot be changed, as SETACL says. Logged with the account's address.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"type\"],\"properties\":{\"type\":{\"type\":\"string\",\"enum\":[\"user\",\"group\",\"anyone\"]},\"account_id\":{\"type\":\"integer\"},\"account\":{\"type\":\"string\"},\"group_id\":{\"type\":\"integer\"},\"group\":{\"type\":\"string\"},\"rights\":{\"type\":\"object\",\"properties\":{\"lookup\":{\"type\":\"boolean\"},\"read\":{\"type\":\"boolean\"},\"write_seen\":{\"type\":\"boolean\"},\"write_others\":{\"type\":\"boolean\"},\"insert\":{\"type\":\"boolean\"},\"post\":{\"type\":\"boolean\"},\"create\":{\"type\":\"boolean\"},\"delete_mailbox\":{\"type\":\"boolean\"},\"write_deleted\":{\"type\":\"boolean\"},\"expunge\":{\"type\":\"boolean\"},\"administer\":{\"type\":\"boolean\"}}}}}}}},\"responses\":{\"201\":{\"description\":\"Created: the permission as the listing shows it, with its id\"},\"400\":{\"description\":\"No type, a type other than the three, an account or group missing or unknown, the folder's owner, an unknown right or field, or a value of the wrong type (error names it); nothing saved\"},\"404\":{\"description\":\"No such account, or no folder with that id in its tree\"}}}},"
      "\"/api/v1/accounts/{address}/folders/{id}/permissions/{pid}\":{"
      "\"put\":{\"summary\":\"Change a permission (administrator)\",\"description\":\"Body: any subset of what POST takes. A field left out keeps its value; a right not named keeps its value; a change of type drops the account and the group unless the body names them, since the old ones could not belong to the new type. The same checks as POST, and nothing changes when one fails.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\"}}}},\"responses\":{\"200\":{\"description\":\"The permission as saved\"},\"400\":{\"description\":\"A field refused; nothing changed\"},\"404\":{\"description\":\"No such account, folder or permission\"}}},"
      "\"delete\":{\"summary\":\"Revoke a permission (administrator)\",\"description\":\"Through the folder's collection as InterfaceIMAPFolderPermissions.DeleteByDBID goes; a permission of another folder is not found here. Logged with the account's address.\",\"responses\":{\"200\":{\"description\":\"deleted true\"},\"404\":{\"description\":\"No such account, folder or permission\"}}}},"
      "\"/api/v1/accounts/{address}/messages\":{"
      "\"get\":{\"summary\":\"Every message of an account (administrator)\",\"description\":\"What Account.Messages is over COM: every message of the account whatever its folder, in the collection's order, read fresh. total, and messages, each the row as COM's Message reports it: id, uid, folder_id, account_id, size (bytes), state (0 created, 1 delivering, 2 delivered), received, from_address (the envelope sender) and flags - seen, deleted, flagged, answered, draft, recent and keywords. A message flagged deleted and not yet expunged is listed, as it is in the COM collection; the account's own /api/v1/me listing is a reader's view and leaves it out. A key restricted to named domains reaches the accounts of those domains only.\",\"responses\":{\"200\":{\"description\":\"total, messages\"},\"404\":{\"description\":\"No such account\"}}},"
      "\"delete\":{\"summary\":\"Delete every message of an account (administrator)\",\"description\":\"What Account.DeleteMessages does over COM, to the call - PersistentAccount::DeleteMessages: the account's messages go with the folders the server does not keep, the inbox and the designated folders stay emptied, and the account's caches are dropped. Logged with the account's address.\",\"responses\":{\"200\":{\"description\":\"deleted true\"},\"404\":{\"description\":\"No such account\"}}}},"
      "\"/api/v1/accounts/{address}/folders/{id}/messages\":{"
      "\"get\":{\"summary\":\"A folder's messages, every one of them (administrator)\",\"description\":\"What IMAPFolder.Messages is over COM: the folder's whole collection, the live one every IMAP session shares, in UID order - a message flagged deleted and not yet expunged included. folder_id, total, and messages, each the row as GET /api/v1/accounts/{address}/messages describes it. The folder is one of the account's own.\",\"responses\":{\"200\":{\"description\":\"folder_id, total, messages\"},\"404\":{\"description\":\"No such account, or no folder with that id in its tree\"}}},"
      "\"post\":{\"summary\":\"Add a message to a folder from its text (administrator)\",\"description\":\"The body is the message, as a .eml file; a bare-LF body is given CRLF. What Messages.Add and Message.Save do over COM, and what APPEND does: the bytes are written where the server keeps a delivered message, the row saved with its UID and the state delivered, and every session on the folder told. flags= in the query names the flags to store it with, comma-separated from seen, flagged, answered, draft and deleted, as APPEND's flag list goes beside the literal; from= the envelope sender the row carries, as Message.FromAddress sets it. Nothing in the text is changed or added.\",\"requestBody\":{\"content\":{\"message/rfc822\":{\"schema\":{\"type\":\"string\"}}}},\"responses\":{\"201\":{\"description\":\"The row, with its id and uid\"},\"400\":{\"description\":\"An empty body, one that does not begin with a header line, or an unknown flag\"},\"404\":{\"description\":\"No such account, or no folder with that id in its tree\"},\"413\":{\"description\":\"Over 16 MB\"}}}},"
      "\"/api/v1/accounts/{address}/messages/{id}\":{"
      "\"get\":{\"summary\":\"One message: its row, its header fields and every header (administrator)\",\"description\":\"The row as the listing shows it, then file - the path on the server's own disk, the COM Filename - and file_exists, then subject, from, to, cc and date decoded from the header block, and headers: every field as written, name and value in order, which is what Message.Headers and Message.HeaderValue answer over COM. The header block is read to 64 KB. A message of another account, or in a folder outside the account's tree, is not found.\",\"responses\":{\"200\":{\"description\":\"The message\"},\"404\":{\"description\":\"No such account or message\"}}},"
      "\"put\":{\"summary\":\"Change a message's flags (administrator)\",\"description\":\"Body: any of seen, deleted, flagged, answered and draft, true or false; a flag not named keeps its value. Written on the path STORE takes, with the folder's next mod-sequence, and every session told. Answers the row as changed.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"properties\":{\"seen\":{\"type\":\"boolean\"},\"deleted\":{\"type\":\"boolean\"},\"flagged\":{\"type\":\"boolean\"},\"answered\":{\"type\":\"boolean\"},\"draft\":{\"type\":\"boolean\"}}}}}},\"responses\":{\"200\":{\"description\":\"The row\"},\"400\":{\"description\":\"No flag named, an unknown field, or a value that is not true or false\"},\"404\":{\"description\":\"No such account or message\"}}},"
      "\"delete\":{\"summary\":\"Delete a message (administrator)\",\"description\":\"What Messages.DeleteByDBID does over COM and EXPUNGE does for one message: the row and the file go, through the folder's live collection, and every session is told.\",\"responses\":{\"200\":{\"description\":\"deleted true\"},\"404\":{\"description\":\"No such account or message\"}}}},"
      "\"/api/v1/accounts/{address}/messages/{id}/source\":{\"get\":{\"summary\":\"A message's file (administrator)\",\"description\":\"The bytes as stored, message/rfc822, as a download; up to 16 MB.\",\"responses\":{\"200\":{\"description\":\"The message file\"},\"404\":{\"description\":\"No such account or message, or the file is missing\"},\"413\":{\"description\":\"Over 16 MB\"}}}},"
      "\"/api/v1/accounts/{address}/messages/{id}/copy\":{\"post\":{\"summary\":\"Copy a message into another folder of the account (administrator)\",\"description\":\"Body: folder_id (required), a folder of this account's own tree. What Message.Copy does over COM, to the call: the file is copied, the row written with the destination folder's next UID, and every session on that folder told. The source is left as it is, flags included. Answers the copy's row as the listing shows it.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"folder_id\"],\"properties\":{\"folder_id\":{\"type\":\"integer\"}}}}}},\"responses\":{\"201\":{\"description\":\"The copy: id, uid, folder_id, account_id, size, state, received, from_address, flags\"},\"400\":{\"description\":\"folder_id missing or not a folder id, an unknown field, or the body is not a JSON object\"},\"404\":{\"description\":\"No such account or message, or folder_id is not a folder of the account\"}}}}";
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
         if ((method == "DELETE" || method == "PUT") && idPart.Find("/") < 0 && ParseId(idPart, id))
         {
            route.kind = method == "PUT" ? RouteAccountAppPasswordUpdate : RouteAccountAppPasswordDelete;
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

         if (sub == "/messages")
         {
            if (method == "GET")
               route.kind = RouteAccountFolderMessageList;
            else if (method == "POST")
               route.kind = RouteAccountFolderMessageCreate;

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

      // /messages, /messages/<id> and /messages/<id>/source.
      const AnsiString messages = "/messages";

      if (tail == messages)
      {
         if (method == "GET")
            route.kind = RouteAccountMessageList;
         else if (method == "DELETE")
            route.kind = RouteAccountMessagesDelete;

         return route.kind != RouteUnknown;
      }

      if (tail.StartsWith(messages + "/"))
      {
         AnsiString rest = tail.Mid(messages.GetLength() + 1);
         int slash = rest.Find("/");
         AnsiString idPart = slash < 0 ? rest : rest.Mid(0, slash);
         AnsiString sub = slash < 0 ? AnsiString() : rest.Mid(slash);

         __int64 messageId = 0;
         if (!ParseId(idPart, messageId))
            return false;

         if (sub.IsEmpty())
         {
            if (method == "GET")
               route.kind = RouteAccountMessageGet;
            else if (method == "PUT")
               route.kind = RouteAccountMessageFlags;
            else if (method == "DELETE")
               route.kind = RouteAccountMessageDelete;
         }
         else if (sub == "/source" && method == "GET")
            route.kind = RouteAccountMessageSource;
         else if (sub == "/copy" && method == "POST")
            route.kind = RouteAccountMessageCopy;

         if (route.kind != RouteUnknown)
            route.message_id = messageId;
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
      case RouteAccountAppPasswordUpdate:
         return UpdateAppPassword(bridge, account, route.record_id, requestBody);
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

      case RouteAccountMessageList:
         return ListAccountMessages(bridge, account);
      case RouteAccountMessagesDelete:
         return DeleteAccountMessages(bridge, account);
      case RouteAccountFolderMessageList:
         return ListFolderMessages(bridge, account, route.folder_id);
      case RouteAccountFolderMessageCreate:
         return CreateFolderMessage(bridge, account, route.folder_id, requestBody, route.query);
      case RouteAccountMessageGet:
         return GetMessage(bridge, account, route.message_id);
      case RouteAccountMessageFlags:
         return UpdateMessageFlags(bridge, account, route.message_id, requestBody);
      case RouteAccountMessageDelete:
         return DeleteMessage(bridge, account, route.message_id);
      case RouteAccountMessageSource:
         return MessageSource(bridge, account, route.message_id);
      case RouteAccountMessageCopy:
         return CopyMessage(bridge, account, route.message_id, requestBody);
      default:
         break;
      }

      return Refusal(bridge, 404, "not found");
   }
}
