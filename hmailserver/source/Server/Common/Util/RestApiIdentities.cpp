// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Who the signed-in account may write as: GET /api/v1/me/identities, and the
// resolution of a "from" the send and draft routes are given. The rule is the
// SMTP submission rule (SMTPConnection::SenderPermittedFor): the account's own
// address, an alias that resolves to it, and another account's address when
// that account has granted this one the post (p) right on its INBOX. The list
// here is that rule turned around - enumerated from the alias and ACL tables
// and confirmed, for a grant, by the same ACLManager question the SMTP side
// asks - so what the picker offers is exactly what the server would accept.

#include "StdAfx.h"
#include "RestApiServer.h"
#include "HttpServer.h"
#include "../BO/Account.h"
#include "../BO/IMAPFolders.h"
#include "../BO/IMAPFolder.h"
#include "../BO/ACLPermission.h"
#include "../Application/ACLManager.h"
#include "../Application/Application.h"
#include "../Cache/CacheContainer.h"
#include "../SQL/SQLCommand.h"
#include "../SQL/DALRecordset.h"
#include "../../IMAP/IMAPFolderContainer.h"
#include "../../SMTP/SMTPConnection.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      const int IdentityAliasHops = 5;
      const int IdentityMaximum = 100;

      // "Name <address>", "<address>" or "address" -> the name (may be empty)
      // and the address, lower-cased, without the brackets.
      void SplitSenderEntry(const String &entry, String &name, String &address)
      {
         String text = entry;
         text.TrimLeft();
         text.TrimRight();
         name = _T("");
         address = _T("");

         int open = text.Find(_T("<"));
         int close = text.Find(_T(">"));
         if (open >= 0 && close > open)
         {
            address = text.Mid(open + 1, close - open - 1);
            name = text.Mid(0, open);
            name.TrimLeft();
            name.TrimRight();
            if (name.GetLength() >= 2 && name.StartsWith(_T("\"")) && name.EndsWith(_T("\"")))
               name = name.Mid(1, name.GetLength() - 2);
         }
         else
         {
            address = text;
         }

         address.TrimLeft();
         address.TrimRight();
         address.ToLower();
      }

      String FullName(std::shared_ptr<const Account> account)
      {
         String name = account->GetPersonFirstName();
         String lastName = account->GetPersonLastName();
         if (!lastName.IsEmpty())
         {
            if (!name.IsEmpty())
               name += _T(" ");
            name += lastName;
         }
         return name;
      }

      String HeaderFor(const String &name, const String &address)
      {
         if (name.IsEmpty())
            return address;

         String header = _T("\"");
         header += name;
         header += _T("\" <");
         header += address;
         header += _T(">");
         return header;
      }

      bool Listed(const std::vector<RestApiServer::Identity> &identities, const String &address)
      {
         for (size_t i = 0; i < identities.size(); i++)
            if (identities[i].address.CompareNoCase(address) == 0)
               return true;
         return false;
      }
   }

   // The account's own address first, then every active alias that resolves to
   // it (a chain of aliases is followed a few hops, as the SMTP rule follows
   // it), then every account whose INBOX grants this one the post right.
   void
   RestApiServer::IdentitiesFor_(std::shared_ptr<const Account> account, std::vector<Identity> &identities)
   {
      identities.clear();

      Identity own;
      own.address = account->GetAddress();
      own.address.ToLower();
      own.name = FullName(account);
      own.kind = "account";
      identities.push_back(own);

      // Aliases. Hop 1 is every alias whose value is the account's address;
      // hop n is every alias whose value is an alias found at hop n-1.
      std::vector<String> targets;
      targets.push_back(own.address);
      for (int hop = 0; hop < IdentityAliasHops && !targets.empty() && (int) identities.size() < IdentityMaximum; hop++)
      {
         std::vector<String> found;
         for (size_t i = 0; i < targets.size() && (int) identities.size() < IdentityMaximum; i++)
         {
            SQLCommand command("select aliasname from hm_aliases where aliasactive = 1 and lower(aliasvalue) = @VALUE order by aliasname");
            command.AddParameter("@VALUE", targets[i]);

            std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
            if (!recordset)
               break;

            while (!recordset->IsEOF() && (int) identities.size() < IdentityMaximum)
            {
               String name = recordset->GetStringValue("aliasname");
               name.ToLower();
               if (!name.IsEmpty() && !Listed(identities, name))
               {
                  Identity alias;
                  alias.address = name;
                  alias.name = own.name;
                  alias.kind = "alias";
                  identities.push_back(alias);
                  found.push_back(name);
               }
               recordset->MoveNext();
            }
         }
         targets = found;
      }

      // Grants: an INBOX whose ACL carries the post right for this account,
      // for a group, or for anyone is a candidate; ACLManager decides, since
      // group membership and the enforcement switch are its to know.
      AnsiString post;
      post.Format("%d", (int) ACLPermission::PermissionPost);
      SQLCommand command("select distinct f.folderaccountid from hm_acl a inner join hm_imapfolders f on f.folderid = a.aclsharefolderid "
                         "where f.folderparentid = -1 and upper(f.foldername) = 'INBOX' and (a.aclvalue & " + post + ") = " + post + " "
                         "and ((a.aclpermissiontype = 0 and a.aclpermissionaccountid = @ACCOUNTID) or a.aclpermissiontype = 1 or a.aclpermissiontype = 2)");
      command.AddParameter("@ACCOUNTID", account->GetID());

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!recordset)
         return;

      while (!recordset->IsEOF() && (int) identities.size() < IdentityMaximum)
      {
         __int64 ownerId = recordset->GetInt64Value("folderaccountid");
         recordset->MoveNext();

         if (ownerId <= 0 || ownerId == account->GetID())
            continue;

         std::shared_ptr<const Account> owner = CacheContainer::Instance()->GetAccount(ownerId);
         if (!owner || !owner->GetActive())
            continue;

         std::shared_ptr<IMAPFolders> ownerFolders = IMAPFolderContainer::Instance()->GetFoldersForAccount(owner->GetID());
         std::shared_ptr<IMAPFolder> inbox = ownerFolders ? ownerFolders->GetFolderByName(_T("INBOX")) : std::shared_ptr<IMAPFolder>();
         if (!inbox || !ACLManager::CheckDelegatedRight(account->GetID(), inbox, ACLPermission::PermissionPost))
            continue;

         String address = owner->GetAddress();
         address.ToLower();
         if (Listed(identities, address))
            continue;

         Identity granted;
         granted.address = address;
         granted.name = FullName(owner);
         granted.kind = "granted";
         identities.push_back(granted);
      }
   }

   HttpResponse
   RestApiServer::HandleMeIdentities_(const Caller &caller)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      std::vector<Identity> identities;
      IdentitiesFor_(account, identities);

      AnsiString json = "{\"identities\":[";
      for (size_t i = 0; i < identities.size(); i++)
      {
         if (i > 0)
            json += ",";
         json += "{\"address\":\"" + JsonEscape_(Utf8_(identities[i].address)) +
                 "\",\"name\":\"" + JsonEscape_(Utf8_(identities[i].name)) +
                 "\",\"kind\":\"" + identities[i].kind +
                 "\",\"header\":\"" + JsonEscape_(Utf8_(HeaderFor(identities[i].name, identities[i].address))) + "\"}";
      }
      json += "]}";
      return BuildResponse_(200, json);
   }

   // The From a send or a draft asked for, checked and turned into a header.
   // Nothing asked for is the account itself. The answer is 0 with address and
   // header filled in, or an HTTP status with problem holding the body: 400 for
   // something that is not an address, 403 for an address the account may not
   // write as, with the SMTP rule's own reason.
   int
   RestApiServer::ResolveSender_(std::shared_ptr<const Account> account, const String &requested, String &address, String &header, AnsiString &problem)
   {
      String name;
      String wanted;
      SplitSenderEntry(requested, name, wanted);

      String own = account->GetAddress();
      own.ToLower();

      if (wanted.IsEmpty())
      {
         address = account->GetAddress();
         header = HeaderFor(name.IsEmpty() ? FullName(account) : name, account->GetAddress());
         return 0;
      }

      if (!StringParser::IsValidEmailAddress(wanted) || wanted.GetLength() > 255)
      {
         problem = "{\"error\":\"from must be one e-mail address, or Name <address>\"}";
         return 400;
      }

      if (wanted != own)
      {
         String reason;
         if (!SMTPConnection::SenderPermittedFor(account->GetAddress(), wanted, reason))
         {
            problem = "{\"error\":\"you may not write as " + JsonEscape_(Utf8_(wanted)) + ": " + JsonEscape_(Utf8_(reason)) + "\"}";
            return 403;
         }
      }

      address = wanted;
      header = HeaderFor(name.IsEmpty() ? FullName(account) : name, wanted);
      return 0;
   }
}
