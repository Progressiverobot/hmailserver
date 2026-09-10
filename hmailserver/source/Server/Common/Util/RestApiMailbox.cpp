// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// The REST API's own-mailbox writes and its change probe. See RestApiServer.h.
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Three things the self-service portal needs and the read-only mailbox routes
// did not give it: folders it can create, rename and delete; and a probe it can
// poll to learn that something arrived without reloading the folder tree.
//
// The rule the folder handlers follow is that IMAP decides. A mailbox is not
// the portal's to define: the same account reaches it from Thunderbird and from
// a phone, and a name the portal accepted but CREATE would refuse - or a folder
// the portal deleted that DELETE would have kept - is a mailbox two clients
// disagree about. So each handler here makes the same checks, in the same
// order, as IMAPCommandCREATE, IMAPCommandRENAME and IMAPCommandDELETE, and
// answers with those commands' own sentences, word for word, in the error
// member. Where this file deliberately differs from IMAP it says so at the
// check itself; there is exactly one such place, in the delete.
//
// Everything acts on the SIGNED-IN ACCOUNT'S OWN tree and nothing else.
// FindReadableFolder_, which the message routes use, also reaches the public
// namespace and a delegating owner's folders - correct for reading a message,
// wrong for a structural write, because a public folder's shape is the
// administrator's and another account's mailbox is that account's. FindOwnFolder_
// is the narrow lookup, and it is the only one used here.

#include "StdAfx.h"

#include "RestApiServer.h"
#include "HttpServer.h"
#include "JsonDocument.h"
#include "Unicode.h"
#include "Encoding/ModifiedUTF7.h"
#include "Hashing/HashCreator.h"
#include "FolderManipulationLock.h"
#include "../BO/Account.h"
#include "../BO/IMAPFolder.h"
#include "../BO/IMAPFolders.h"
#include "../BO/Messages.h"
#include "../BO/ACLPermission.h"
#include "../Application/ACLManager.h"
#include "../Persistence/PersistentIMAPFolder.h"
#include "../../IMAP/IMAPConfiguration.h"
#include "../../IMAP/IMAPFolderContainer.h"
#include "../../IMAP/IMAPSpecialUse.h"

#include <algorithm>
#include <map>
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
      // A refusal body carrying one of the IMAP sentences. Every sentence this
      // file passes in is a literal written here, so there is nothing in it to
      // escape - but a stray quote or backslash would still produce a document
      // no reader could parse, so one is dropped rather than emitted.
      AnsiString Refusal(const char *sentence)
      {
         AnsiString safe;
         for (const char *p = sentence; p != 0 && *p != 0; p++)
         {
            if (*p == '\"' || *p == '\\' || *p == '\r' || *p == '\n')
               continue;

            safe += *p;
         }

         return AnsiString("{\"error\":\"") + safe + "\"}";
      }

      // The body as a JSON object, or false with the refusal already built.
      bool ReadObjectBody(const AnsiString &requestBody, JsonValue &body, AnsiString &refusal)
      {
         std::string error;
         if (!JsonValue::Parse(std::string(requestBody.c_str(), requestBody.GetLength()), body, error) || !body.IsObject())
         {
            refusal = Refusal("the body is not a JSON object");
            return false;
         }

         return true;
      }

      // The name member as the wide string the folder objects hold. Absent, of
      // another type, or not decodable is treated as no name at all, which is
      // what CREATE answers for an empty mailbox name.
      bool ReadName(const JsonValue &body, String &name)
      {
         const JsonValue *member = body.Get("name");
         if (member == 0 || !member->IsString())
            return false;

         return Unicode::MultiByteToWide(AnsiString(member->AsString().c_str()), name) && !name.IsEmpty();
      }

      // A folder is the account's inbox when it sits at the root of the account's
      // own tree and is called INBOX - decided on the resolved folder and never on
      // the text of a path, which is the rule IMAPCommandDELETE and
      // IMAPCommandRENAME both settled on.
      bool IsInbox(std::shared_ptr<IMAPFolder> folder)
      {
         return folder &&
                folder->GetAccountID() != 0 &&
                folder->GetParentFolderID() == -1 &&
                folder->GetFolderName().CompareNoCase(_T("INBOX")) == 0;
      }
   }

   std::shared_ptr<IMAPFolder>
   RestApiServer::FindOwnFolder_(std::shared_ptr<const Account> account, __int64 folderId)
   {
      if (!account || folderId <= 0)
         return std::shared_ptr<IMAPFolder>();

      std::shared_ptr<IMAPFolders> tree = IMAPFolderContainer::Instance()->GetFoldersForAccount(account->GetID());
      if (!tree)
         return std::shared_ptr<IMAPFolder>();

      std::shared_ptr<IMAPFolder> folder = tree->GetItemByDBIDRecursive(folderId);

      // The tree is keyed by account, so a folder found in it belongs to this
      // account - but the id is asked for again rather than assumed, because a
      // public folder grafted into a tree by a future change would otherwise
      // become writable here without anyone deciding that it should be.
      if (!folder || folder->GetAccountID() != account->GetID())
         return std::shared_ptr<IMAPFolder>();

      return folder;
   }

   // A mailbox name as this account typed it, split on the hierarchy
   // delimiter and each element put into the form folder rows are stored in.
   // The split is done on the text rather than on the encoding because the
   // delimiter is ASCII and modified UTF-7 leaves ASCII alone except for '&';
   // encoding after the split therefore cannot move a delimiter or invent one.
   std::vector<String>
   RestApiServer::StoredFolderPath_(const String &name, const String &delimiter)
   {
      std::vector<String> elements = StringParser::SplitString(name, delimiter);

      for (size_t i = 0; i < elements.size(); i++)
         elements[i] = ModifiedUTF7::Encode(elements[i]);

      return elements;
   }

   bool
   RestApiServer::OwnFolderPath_(std::shared_ptr<IMAPFolders> tree, std::shared_ptr<IMAPFolder> folder, std::vector<String> &path)
   {
      path.clear();

      if (!tree || !folder)
         return false;

      std::shared_ptr<IMAPFolder> walk = folder;

      for (int guard = 0; walk && guard <= IMAPFolder::MaxFolderDepth; guard++)
      {
         path.insert(path.begin(), walk->GetFolderName());

         __int64 parentId = walk->GetParentFolderID();
         if (parentId < 0)
            return true;

         walk = tree->GetItemByDBIDRecursive(parentId);
      }

      // The walk ran out of depth or off the end of the tree, which a
      // folderparentid cycle or a half-restored database can produce. Nothing is
      // decided from a path that is not the folder's.
      path.clear();
      return false;
   }

   AnsiString
   RestApiServer::FolderEntryJson_(std::shared_ptr<const Account> account, std::shared_ptr<IMAPFolder> folder)
   {
      if (!account || !folder)
         return "{}";

      String delimiter = Configuration::Instance()->GetIMAPConfiguration()->GetHierarchyDelimiter();

      std::shared_ptr<IMAPFolders> tree = IMAPFolderContainer::Instance()->GetFoldersForAccount(account->GetID());

      std::map<__int64, int> designations;
      IMAPSpecialUse::Resolve(tree, designations);

      std::vector<String> path;
      OwnFolderPath_(tree, folder, path);

      bool readAccess = false;
      bool writeAccess = false;
      ACLManager::GetReadWriteAccess(account->GetID(), folder, readAccess, writeAccess);

      AnsiString json;
      AppendOneFolderJson_(account, folder, StringParser::JoinVector(path, delimiter), designations, delimiter, writeAccess, json, 0);

      return json;
   }

   // POST /api/v1/me/folders. IMAPCommandCREATE's checks, in its order: a name at
   // all, then the folder already existing, then the name being a valid one. A
   // name carrying the hierarchy delimiter creates the whole path, exactly as
   // CREATE "a.b.c" does - CreatePath makes each level that is missing and
   // leaves each level that is there.
   HttpResponse
   RestApiServer::HandleMeFolderCreate_(const Caller &caller, const AnsiString &requestBody)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      JsonValue body;
      AnsiString refusal;
      if (!ReadObjectBody(requestBody, body, refusal))
         return BuildResponse_(400, refusal);

      String name;
      if (!ReadName(body, name))
         return BuildResponse_(400, Refusal("Folder name not specified."));

      std::shared_ptr<IMAPFolders> tree = IMAPFolderContainer::Instance()->GetFoldersForAccount(account->GetID());
      if (!tree)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      String delimiter = Configuration::Instance()->GetIMAPConfiguration()->GetHierarchyDelimiter();

      // parent_id names a folder of this account to create under; absent, the
      // path is read from the root. The names of the parent's own path are put
      // in front of the requested one, so what is validated below is the whole
      // mailbox name - which is what CREATE is given and what the depth and
      // "#" rules are written against.
      std::vector<String> path;

      const JsonValue *parentMember = body.Get("parent_id");
      if (parentMember != 0 && !parentMember->IsNull())
      {
         if (!parentMember->IsNumber())
            return BuildResponse_(400, Refusal("parent_id has to be a folder id"));

         std::shared_ptr<IMAPFolder> parent = FindOwnFolder_(account, parentMember->AsInt64());
         if (!parent)
            return BuildResponse_(404, Refusal("Folder could not be found."));

         if (!OwnFolderPath_(tree, parent, path))
            return BuildResponse_(500, Refusal("The folder could not be created."));
      }

      std::vector<String> requested = StoredFolderPath_(name, delimiter);
      path.insert(path.end(), requested.begin(), requested.end());

      if (tree->GetFolderByFullPath(path))
         return BuildResponse_(409, Refusal("Folder already exists."));

      // Never the public flavour of the check: this route reaches one account's
      // own tree, so a name whose first element begins with "#" - the public
      // namespace, or the other-users namespace - is an invalid folder name here
      // exactly as it is for a CREATE that is not a public one.
      if (!IMAPFolder::IsValidFolderName(path, false))
         return BuildResponse_(400, Refusal("CREATE The folder name is invalid."));

      {
         FolderManipulationLock folderLock((int) account->GetID(), -1);
         folderLock.Lock();

         // As CREATE does for a folder that is not public: not subscribed. A
         // client subscribes to what it wants to see, and the portal lists every
         // folder regardless.
         tree->CreatePath(tree, path, false);
      }

      std::shared_ptr<IMAPFolder> created = tree->GetFolderByFullPath(path);
      if (!created)
      {
         // CreatePath reports the reason itself (it stops at the level whose row
         // would not save), so this only has to be an honest answer to the
         // caller rather than a second diagnosis.
         return BuildResponse_(500, Refusal("The folder could not be created."));
      }

      return BuildResponse_(201, FolderEntryJson_(account, created));
   }

   // PUT /api/v1/me/folders/{id}. IMAPCommandRENAME's classic, non-public flow:
   // the new name is a whole mailbox name, so renaming "Projects" to "Work.Old"
   // moves it under "Work", making "Work" if it is missing. Subfolders are not
   // touched and do not have to be: they carry their parent's id, so they follow
   // the folder and their paths in the listing recompute from the new parent.
   HttpResponse
   RestApiServer::HandleMeFolderRename_(const Caller &caller, __int64 folderId, const AnsiString &requestBody)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      JsonValue body;
      AnsiString refusal;
      if (!ReadObjectBody(requestBody, body, refusal))
         return BuildResponse_(400, refusal);

      std::shared_ptr<IMAPFolders> tree = IMAPFolderContainer::Instance()->GetFoldersForAccount(account->GetID());
      if (!tree)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      std::shared_ptr<IMAPFolder> folder = FindOwnFolder_(account, folderId);
      if (!folder)
         return BuildResponse_(404, Refusal("Folder could not be found."));

      if (IsInbox(folder))
         return BuildResponse_(403, Refusal("Cannot rename INBOX."));

      String name;
      if (!ReadName(body, name))
         return BuildResponse_(400, Refusal("The new folder name is invalid."));

      String delimiter = Configuration::Instance()->GetIMAPConfiguration()->GetHierarchyDelimiter();

      std::vector<String> oldPath;
      if (!OwnFolderPath_(tree, folder, oldPath))
         return BuildResponse_(500, Refusal("The folder could not be renamed."));

      std::vector<String> newPath = StoredFolderPath_(name, delimiter);

      // A name that splits into nothing is not one IsValidFolderName would
      // accept either; refused here so that the size arithmetic below is
      // always done on at least one element.
      if (newPath.empty())
         return BuildResponse_(400, Refusal("The new folder name is invalid."));

      if (newPath.size() == 1 && newPath[0].CompareNoCase(_T("INBOX")) == 0)
         return BuildResponse_(403, Refusal("Cannot rename INBOX."));

      // RFC 4314: a rename is a delete at the old name and a create at the new
      // one, and the delete half is the right that is checked. The same right
      // ConfirmPossibleToRename asks for, with the same sentence.
      if (!RightOn_(account, folder, ACLPermission::PermissionDeleteMailbox))
         return BuildResponse_(403, Refusal("ACL DeleteMailbox permission denied (required for RENAME)."));

      if (tree->GetFolderByFullPath(newPath))
         return BuildResponse_(409, Refusal("Target folder already exist."));

      int recursion = 0;
      int oldDepth = folder->GetFolderDepth(recursion);
      if (oldDepth + (int) (newPath.size() - 1) > IMAPFolder::MaxFolderDepth)
         return BuildResponse_(400, Refusal("To many sub-folders in structure."));

      if (!IMAPFolder::IsValidFolderName(newPath, false))
         return BuildResponse_(400, Refusal("The new folder name is invalid."));

      // A folder cannot become its own descendant: its subtree would have no
      // parent left to hang from. Compared with the configured delimiter, not a
      // hard-coded dot, for the reason the IMAP command records.
      String oldName = StringParser::JoinVector(oldPath, delimiter);
      String newName = StringParser::JoinVector(newPath, delimiter);

      if (newName.FindNoCase(oldName + delimiter) == 0)
         return BuildResponse_(400, Refusal("A folder cannot be moved into one of its subfolders."));

      std::shared_ptr<IMAPFolder> newParent;

      if (newPath.size() > 1)
      {
         std::vector<String> newParentPath(newPath.begin(), newPath.end() - 1);

         newParent = tree->GetFolderByFullPath(newParentPath);

         if (!newParent)
         {
            // The destination's parent does not exist yet. RENAME makes it, and
            // so does this: a client that renames "Old" to "Archive.2026" on a
            // mailbox with no "Archive" expects both to exist afterwards.
            tree->CreatePath(tree, newParentPath, false);
            newParent = tree->GetFolderByFullPath(newParentPath);

            if (!newParent)
               return BuildResponse_(500, Refusal("The folder could not be renamed."));
         }
      }

      FolderManipulationLock folderLock((int) account->GetID(), -1);
      folderLock.Lock();

      std::shared_ptr<IMAPFolder> oldParent;
      __int64 oldParentId = folder->GetParentFolderID();
      if (oldParentId >= 0)
         oldParent = tree->GetItemByDBIDRecursive(oldParentId);

      if (oldParent)
      {
         oldParent->GetSubFolders()->RemoveFolder(folder);
      }
      else
      {
         tree->RemoveFolder(folder);
      }

      folder->SetFolderName(newPath[newPath.size() - 1]);

      if (newParent)
      {
         folder->SetParentFolderID(newParent->GetID());
         newParent->GetSubFolders()->AddItem(folder);
      }
      else
      {
         folder->SetParentFolderID(-1);
         tree->AddItem(folder);
      }

      // The same contract the IMAP command settled on: a refused save is
      // refused to the caller, and the in-memory move is not unwound, because
      // reversing a move between containers half-correctly leaves a tree no
      // reload produces. The next refresh reads the folder back as it is
      // stored.
      if (!PersistentIMAPFolder::SaveObject(folder))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 6520, "RestApiServer::HandleMeFolderRename_",
            "A folder rename asked for over the REST API could not be saved and has been refused. The folder tree held in memory may show the new name until it is reloaded.");

         return BuildResponse_(500, Refusal("The folder could not be renamed."));
      }

      return BuildResponse_(200, FolderEntryJson_(account, folder));
   }

   // DELETE /api/v1/me/folders/{id}. IMAPCommandDELETE, with one deliberate
   // addition named at the check itself.
   HttpResponse
   RestApiServer::HandleMeFolderDelete_(const Caller &caller, __int64 folderId)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      std::shared_ptr<IMAPFolders> tree = IMAPFolderContainer::Instance()->GetFoldersForAccount(account->GetID());
      if (!tree)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      std::shared_ptr<IMAPFolder> folder = FindOwnFolder_(account, folderId);
      if (!folder)
         return BuildResponse_(404, Refusal("Folder could not be found."));

      if (IsInbox(folder))
         return BuildResponse_(403, Refusal("You cannot delete the inbox."));

      // NOT what IMAP DELETE does, and on purpose. IMAP will delete a folder
      // carrying a special use - only the retention sweep's delete keeps one -
      // so a client can and does remove its own \Trash. A portal is a different
      // proposition: one mis-click on the folder the server has designated
      // \Sent takes every sent message with it, the page has no undo, and the
      // designation is the one piece of evidence that says the folder is not an
      // ordinary one. So a designated folder is refused here and stays
      // deletable over IMAP, where the client that designated it can undesignate
      // it first. The designation is the effective one the listing reports in
      // special_use, name fallback and all.
      std::map<__int64, int> designations;
      IMAPSpecialUse::Resolve(tree, designations);

      if (designations.find(folder->GetID()) != designations.end())
         return BuildResponse_(403, Refusal("You cannot delete a folder the server has designated for a special use."));

      if (!RightOn_(account, folder, ACLPermission::PermissionDeleteMailbox))
         return BuildResponse_(403, Refusal("ACL: DeleteMailbox permission denied (required for DELETE)."));

      FolderManipulationLock folderLock((int) account->GetID(), -1);
      folderLock.Lock();

      // Refused before anything is taken out of the in-memory tree, so that what
      // the caller is told and what is stored cannot disagree. The delete takes
      // the folder's subfolders and every message in all of them, which is what
      // DELETE does and what the folder's own row being gone requires: a
      // subfolder whose parent row had been deleted would be reachable by
      // nothing.
      if (!PersistentIMAPFolder::DeleteObject(folder))
         return BuildResponse_(500, Refusal("The folder could not be deleted."));

      __int64 parentId = folder->GetParentFolderID();

      if (parentId >= 0)
      {
         std::shared_ptr<IMAPFolder> parent = tree->GetItemByDBIDRecursive(parentId);

         // No parent node in memory: the rows are gone, which is the part that
         // matters, and the stale entry disappears at the next refresh.
         if (parent && parent->GetSubFolders())
            parent->GetSubFolders()->RemoveFolder(folder);
      }
      else
      {
         tree->RemoveFolder(folder);
      }

      return BuildResponse_(200, "{\"deleted\":true}");
   }

   namespace
   {
      // One folder as the change probe counts it.
      struct FolderCount
      {
         FolderCount() : id(0), count(0), unseen(0) { }

         __int64 id;
         long count;
         long unseen;
      };

      bool ByFolderId(const FolderCount &left, const FolderCount &right)
      {
         return left.id < right.id;
      }
   }

   // GET /api/v1/me/changes[?since=<token>]. Cheap enough to poll: the counts
   // come from the same per-folder message collection the folder listing reads -
   // MessagesContainer's cached collection, which IMAP, POP3 and delivery all
   // share - so nothing here opens a message file or walks the store. Only the
   // account's own folders are counted, so nothing another account does can
   // move this account's token.
   HttpResponse
   RestApiServer::HandleMeChanges_(const Caller &caller, const AnsiString &query)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      std::shared_ptr<IMAPFolders> tree = IMAPFolderContainer::Instance()->GetFoldersForAccount(account->GetID());

      std::vector<FolderCount> counts;

      // Every folder of the account's own tree that the listing shows, under the
      // same two rights the listing asks for - a folder an ACL keeps from its
      // own account is left out of both, so the page never sees a count for a
      // folder it was not given. The order the walk finds them in does not
      // matter: the answer and the token are both taken from the sorted list.
      std::vector<std::shared_ptr<IMAPFolders>> pending;
      if (tree)
         pending.push_back(tree);

      // Breadth-first, with a ceiling on how many folders one answer describes.
      // No mailbox an IMAP client made has ten thousand folders, and the ceiling
      // is what stops a folderparentid cycle - which the commands cannot build
      // but a hand-edited or half-restored database can - turning a poll into an
      // endless walk.
      const size_t MaxFoldersCounted = 10000;

      for (size_t at = 0; at < pending.size() && counts.size() < MaxFoldersCounted; at++)
      {
         std::shared_ptr<IMAPFolders> level = pending[at];
         if (!level)
            continue;

         for (int i = 0; i < level->GetCount(); i++)
         {
            std::shared_ptr<IMAPFolder> folder = level->GetItem(i);
            if (!folder)
               continue;

            if (!RightOn_(account, folder, ACLPermission::PermissionLookup))
               continue;

            bool readAccess = false;
            bool writeAccess = false;
            ACLManager::GetReadWriteAccess(account->GetID(), folder, readAccess, writeAccess);
            if (!readAccess)
               continue;

            std::shared_ptr<Messages> messages = folder->GetMessages();

            FolderCount entry;
            entry.id = folder->GetID();
            entry.count = messages ? messages->GetCount() : 0;
            entry.unseen = entry.count - (messages ? messages->GetNoOfSeen() : 0);
            counts.push_back(entry);

            pending.push_back(folder->GetSubFolders());
         }
      }

      // Sorted by id, not by the order the tree happens to hold, so that the
      // token stands for the state and not for how the tree was loaded. A
      // reordering that changed nothing would otherwise read as a change.
      std::sort(counts.begin(), counts.end(), ByFolderId);

      // What the token stands for, spelled out once and then hashed: this
      // account, and each of its folders with its two counts. Hashed, so the
      // token carries no folder id, no count and no address a holder could read
      // back - it is a value to hand in again and nothing else - and prefixed
      // with the account id so that two accounts whose mailboxes happen to have
      // the same shape do not share one.
      AnsiString state;
      state.Format("hmailserver/me/changes/1\naccount=%I64d\n", account->GetID());

      for (size_t i = 0; i < counts.size(); i++)
      {
         AnsiString line;
         line.Format("%I64d:%ld:%ld\n", counts[i].id, counts[i].count, counts[i].unseen);
         state += line;
      }

      HashCreator hasher(HashCreator::SHA256);
      AnsiString token = hasher.GenerateHashNoSalt(state, HashCreator::hex);

      AnsiString json;
      json.Format("{\"token\":\"%hs\",\"folders\":[", token.c_str());

      for (size_t i = 0; i < counts.size(); i++)
      {
         AnsiString entry;
         entry.Format("%hs{\"id\":%I64d,\"count\":%ld,\"unseen\":%ld}",
            i > 0 ? "," : "", counts[i].id, counts[i].count, counts[i].unseen);
         json += entry;
      }

      json += "]";

      // since is compared, never parsed: any text a caller sends that is not the
      // token this mailbox stands at now means "something differs", which is the
      // safe answer - a page told to reload when nothing changed costs one
      // request, and a page not told when something did loses a message.
      AnsiString since = QueryParameter_(query, "since");
      if (!since.IsEmpty())
         json += since == token ? ",\"changed\":false" : ",\"changed\":true";

      json += "}";

      return BuildResponse_(200, json);
   }

   AnsiString
   RestApiServer::OpenApiMailboxPaths_()
   {
      // The folders path already has an entry in HandleOpenApi_'s head for its
      // listing; the entry here repeats that verb beside the new one, so that a
      // reader keeping the last of two equal keys still sees the whole path.
      return
         ",\"/api/v1/me/folders\":{"
         "\"get\":{\"summary\":\"The signed-in account's folder tree\",\"description\":\"Every folder the account may read, as IMAP LIST gives it: id, name, path (joined with delimiter), parent_id, special_use (the RFC 6154 designation, e.g. \\\\Sent), subscribed, writable, messages, unseen, uidvalidity, subfolders. A folder the ACL keeps from the account is left out with its subtree. shared lists, under owner, the public folders (owner is the public namespace name) and the folders of each account that granted this one a right, named as IMAP names them; each entry carries account_id (0 for public). Every message route accepts a folder or message from those trees under the rights the owner granted.\",\"responses\":{\"200\":{\"description\":\"delimiter, folders, shared\"}}},"
         "\"post\":{\"summary\":\"Create a folder in the signed-in account's mailbox\",\"description\":\"Body: name (required) and parent_id (a folder of this account to create under; absent means the root of the mailbox). The name is judged exactly as IMAP CREATE judges it and refused with CREATE's own sentence in error. A name carrying the hierarchy delimiter names a path and creates every level of it that is missing, as CREATE \\\"a.b.c\\\" does, so one call can make a subtree. The new folder is not subscribed, which is what CREATE leaves a non-public folder as. Acts on this account's own tree only: the public namespace and another account's folders are readable through the message routes but are not this account's to write, and a name whose first element begins with # is refused as invalid. A mutating route, so a browser session must also carry X-Requested-With: hMailServer.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"name\"],\"properties\":{\"name\":{\"type\":\"string\"},\"parent_id\":{\"type\":\"integer\"}}}}}},\"responses\":{\"201\":{\"description\":\"The new folder, in the entry shape the listing emits (id, account_id, name, path, parent_id, special_use, subscribed, writable, messages, unseen, uidvalidity, subfolders)\"},\"400\":{\"description\":\"'Folder name not specified.' (no name), 'CREATE The folder name is invalid.' (empty element, an element over 255 characters, deeper than 25 levels, or a first element beginning with #), 'parent_id has to be a folder id', or the body is not a JSON object\"},\"403\":{\"description\":\"A session without X-Requested-With\"},\"404\":{\"description\":\"'Folder could not be found.' - parent_id is not a folder of this account\"},\"409\":{\"description\":\"'Folder already exists.'\"},\"500\":{\"description\":\"'The folder could not be created.' - the row could not be written; the reason is in the error log\"}}}}"
         ",\"/api/v1/me/folders/{id}\":{"
         "\"put\":{\"summary\":\"Rename or move one of the signed-in account's folders\",\"description\":\"Body: name - a whole mailbox name, as IMAP RENAME's second argument is. Renaming 'Projects' to 'Work.Old' moves it under 'Work' and creates 'Work' if it is missing, exactly as RENAME does. Subfolders are neither moved nor renamed and do not need to be: they carry their parent's id, so they follow the folder and their path in the listing recomputes under the new name. Judged as RENAME judges it, with RENAME's own sentences. A mutating route, so a browser session must also carry X-Requested-With: hMailServer.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"name\"],\"properties\":{\"name\":{\"type\":\"string\"}}}}}},\"responses\":{\"200\":{\"description\":\"The folder as renamed, in the entry shape the listing emits\"},\"400\":{\"description\":\"'The new folder name is invalid.', 'To many sub-folders in structure.' (the moved subtree would pass 25 levels), 'A folder cannot be moved into one of its subfolders.', or the body is not a JSON object\"},\"403\":{\"description\":\"'Cannot rename INBOX.', 'ACL DeleteMailbox permission denied (required for RENAME).', or a session without X-Requested-With\"},\"404\":{\"description\":\"'Folder could not be found.' - not a folder of this account\"},\"409\":{\"description\":\"'Target folder already exist.'\"},\"500\":{\"description\":\"'The folder could not be renamed.' - the row could not be written\"}}},"
         "\"delete\":{\"summary\":\"Delete one of the signed-in account's folders\",\"description\":\"As IMAP DELETE does: the folder's subfolders and every message in all of them go with it, and the account's inbox is refused however it is reached. One rule is stricter than IMAP's on purpose - a folder the server designates for a special use (the special_use the listing reports, whether set by CREATE ... USE or taken from the folder's name) is refused here, because one mis-click on \\\\Sent in a page with no undo takes every sent message with it. Such a folder is NOT undeletable - it stays deletable over IMAP, where the client that designated it can undesignate it first, and where the person deleting it asked for that mailbox by name. A page can tell which folders those are before it calls anything: a folder whose special_use in GET /api/v1/me/folders is anything other than the empty string will be refused here, so its delete control belongs greyed out rather than offered and then refused. A mutating route, so a browser session must also carry X-Requested-With: hMailServer.\",\"responses\":{\"200\":{\"description\":\"deleted true\"},\"403\":{\"description\":\"'You cannot delete the inbox.', 'You cannot delete a folder the server has designated for a special use.', 'ACL: DeleteMailbox permission denied (required for DELETE).', or a session without X-Requested-With\"},\"404\":{\"description\":\"'Folder could not be found.' - not a folder of this account\"},\"500\":{\"description\":\"'The folder could not be deleted.' - nothing was removed\"}}}}"
         ",\"/api/v1/me/changes\":{\"get\":{\"summary\":\"Whether anything in the signed-in account's mailbox has changed\",\"description\":\"Made to be polled every few seconds. token is an opaque value standing for the mailbox as it is now: it is stable while nothing changes, it says nothing about any other account, and it is the SHA-256 of a canonical rendering of the counts below rather than anything a caller can read back. folders carries one entry per folder of this account, ordered by id, with count (messages in the folder) and unseen. Both come from the per-folder message collection the folder listing already reads, so no message file is opened. Hand the previous token back as since and the answer carries changed as well: false when the mailbox stands exactly where that token was taken, true otherwise - including for a since that is not a token this mailbox ever had, because 'something differs' is the answer that cannot lose a message.\",\"parameters\":[{\"name\":\"since\",\"in\":\"query\",\"required\":false,\"schema\":{\"type\":\"string\"},\"description\":\"A token from an earlier answer\"}],\"responses\":{\"200\":{\"description\":\"token, folders (id, count, unseen) and - only when since was given - changed\"},\"403\":{\"description\":\"The administrator password or an API key was presented\"}}}}";
   }
}
