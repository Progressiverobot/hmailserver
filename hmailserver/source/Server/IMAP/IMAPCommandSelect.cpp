// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "IMAPCommandSelect.h"
#include "IMAPConnection.h"
#include "IMAPSimpleCommandParser.h"
#include "IMAPConfiguration.h"
#include "MessagesContainer.h"

#include "../Common/Application/ACLManager.h"
#include "../Common/BO/ACLPermission.h"
#include "../Common/BO/Account.h"
#include "../Common/BO/IMAPFolders.h"
#include "../Common/BO/IMAPFolder.h"
#include "../Common/Persistence/PersistentIMAPFolder.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   IMAPResult
   IMAPCommandSELECT::ExecuteCommand(std::shared_ptr<HM::IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument)
   {

      if (!pConnection->IsAuthenticated())
         return IMAPResult(IMAPResult::ResultNo, "Authenticate first");

      // RFC 7162: a "SELECT mailbox (CONDSTORE)" parameter enables CONDSTORE for the session.
      // A "SELECT mailbox (QRESYNC (uidvalidity modseq ...))" parameter additionally enables
      // QRESYNC and asks the server to replay changes since the supplied mod-sequence.
      bool qresyncRequested = false;
      __int64 qresyncModSeq = 0;
      {
         String sCmdUpper = pArgument->Command();
         sCmdUpper.MakeUpper();
         if (sCmdUpper.Find(_T("CONDSTORE")) >= 0)
            pConnection->SetCondstoreEnabled(true);

         int qresyncPos = sCmdUpper.Find(_T("QRESYNC"));
         if (qresyncPos >= 0)
         {
            qresyncRequested = true;
            pConnection->SetCondstoreEnabled(true);
            pConnection->SetQResyncEnabled(true);

            // Parse "(uidvalidity modseq ...)" - the second number is the client's mod-sequence.
            int openParen = sCmdUpper.Find(_T("("), qresyncPos);
            if (openParen >= 0)
            {
               int closeParen = sCmdUpper.Find(_T(")"), openParen);
               if (closeParen > openParen)
               {
                  String sInner = sCmdUpper.Mid(openParen + 1, closeParen - openParen - 1);
                  sInner.TrimLeft();
                  int sp = sInner.Find(_T(" "));
                  if (sp > 0)
                  {
                     String sRest = sInner.Mid(sp + 1);
                     sRest.TrimLeft();
                     qresyncModSeq = _ttoi64(sRest);
                  }
               }
            }
         }
      }

      std::shared_ptr<IMAPSimpleCommandParser> pParser = std::shared_ptr<IMAPSimpleCommandParser>(new IMAPSimpleCommandParser());

      pParser->Parse(pArgument);

      if (pParser->ParamCount() < 1)
         return IMAPResult(IMAPResult::ResultBad, "SELECT Command requires at least 1 parameter.");

      String sFolderName = pParser->GetParamValue(pArgument, 0);
      if (sFolderName == Configuration::Instance()->GetIMAPConfiguration()->GetIMAPPublicFolderName())
         return IMAPResult(IMAPResult::ResultBad, "SELECT Only sub folders of the root shared folder can be selected.");
         
      std::shared_ptr<IMAPFolder> pSelectedFolder = pConnection->GetFolderByFullPath(sFolderName);
      if (!pSelectedFolder)
      {
         // Note that this is also the answer for a shared ("#Users") path the
         // caller lacks the lookup right on: resolution refuses those before
         // this command ever sees a folder, so "no rights" and "no such folder"
         // are indistinguishable here by construction.
         return IMAPResult(IMAPResult::ResultBad, "Folder could not be found.");
      }

      // A folder that belongs to another account - reached through the "#Users"
      // namespace, and only reachable there with the lookup right.
      bool bIsDelegatedFolder = pSelectedFolder->GetAccountID() != 0 &&
                                pSelectedFolder->GetAccountID() != pConnection->GetAccount()->GetID();

      bool readAccess = false;
      bool writeAccess = false;
      pConnection->CheckFolderPermissions(pSelectedFolder, readAccess, writeAccess);

      // Check if the user has access to read this folder.
      if (!readAccess)
      {
         // RFC 4314: for a mailbox the user may look up but not read, SELECT
         // fails with NO. The folder's existence is not a secret at this point -
         // the lookup right already made it visible in LIST.
         if (bIsDelegatedFolder)
            return IMAPResult(IMAPResult::ResultNo, "ACL: Read permission denied (Required for SELECT command).");

         return IMAPResult(IMAPResult::ResultBad, "ACL: Read permission denied (Required for SELECT command).");
      }

      pConnection->SetCurrentFolder(pSelectedFolder, false);

      std::set<__int64> recent_messages;
      auto messages = MessagesContainer::Instance()->GetMessages(pSelectedFolder->GetAccountID(), pSelectedFolder->GetID(), recent_messages, true);

      pConnection->SetRecentMessages(recent_messages);

      long lCount = messages->GetCount();
      // RFC 3501: [UNSEEN] carries a message SEQUENCE NUMBER, not a UID. Sending
      // a UID sent clients that jump to the first unseen message to a message
      // that does not exist.
      __int64 lFirstUnseenID = messages->GetFirstUnseenSequenceNumber();
      long lRecentCount = (int) recent_messages.size();

      String sRespTemp;

      sRespTemp.Format(_T("* %d EXISTS\r\n"), lCount);
      String sResponse = sRespTemp; // EXISTS

      // RFC 9051 (IMAP4rev2): the RECENT response and the \Recent flag were removed.
      if (!pConnection->GetImap4Rev2Enabled())
      {
         sRespTemp.Format(_T("* %d RECENT\r\n"), lRecentCount);
         sResponse += sRespTemp;
      }

	   sResponse += _T("* FLAGS (\\Deleted \\Seen \\Draft \\Answered \\Flagged)\r\n");

      sRespTemp.Format(_T("* OK [UIDVALIDITY %d] current uidvalidity\r\n"), pSelectedFolder->GetCreationTime().ToInt());   
      sResponse += sRespTemp;
      
      if (lFirstUnseenID > 0 && !pConnection->GetImap4Rev2Enabled())
      {
         // RFC 9051 (IMAP4rev2): the [UNSEEN] response code on SELECT was removed.
         sRespTemp.Format(_T("* OK [UNSEEN %d] unseen messages\r\n"), lFirstUnseenID);
         sResponse += sRespTemp;
      }
      
      sRespTemp.Format(_T("* OK [UIDNEXT %d] next uid\r\n"), pSelectedFolder->GetCurrentUID()+1);
      sResponse += sRespTemp;

      // RFC 8474 (OBJECTID): the mailbox id is the folder's database id, which
      // is exactly the stability the RFC wants - it survives RENAME, and a
      // deleted-then-recreated folder gets a different one.
      sRespTemp.Format(_T("* OK [MAILBOXID (F%I64d)] mailbox id\r\n"), pSelectedFolder->GetID());
      sResponse += sRespTemp;

      // RFC 7162 (CONDSTORE/QRESYNC): report the mailbox HIGHESTMODSEQ once CONDSTORE is enabled.
      if (pConnection->GetCondstoreEnabled())
      {
         sRespTemp.Format(_T("* OK [HIGHESTMODSEQ %I64d] highest mod-sequence\r\n"), pSelectedFolder->GetCurrentModSeq());
         sResponse += sRespTemp;
      }

      // RFC 3501/4314: PERMANENTFLAGS advertises what this user may change. On a
      // shared folder that is the caller's rights, flag by flag: \Deleted needs
      // t, \Seen needs s, the rest need w. The enforcement itself lives in the
      // STORE/FETCH/APPEND handlers (via ACLManager); this response is what lets
      // a well-behaved client grey the controls out instead of discovering the
      // refusal one NO at a time. Note that \Seen here is SHARED state - see the
      // design record in ACLManager.h.
      if (bIsDelegatedFolder && ACLManager::GetAclEnforcementEnabled())
      {
         ACLManager aclManager;
         std::shared_ptr<ACLPermission> pDelegateRights = aclManager.GetPermissionForFolder(pConnection->GetAccount()->GetID(), pSelectedFolder);

         String sPermanentFlags;
         if (pDelegateRights)
         {
            if (pDelegateRights->GetAllow(ACLPermission::PermissionWriteDeleted))
               sPermanentFlags += _T("\\Deleted");
            if (pDelegateRights->GetAllow(ACLPermission::PermissionWriteSeen))
               sPermanentFlags += sPermanentFlags.IsEmpty() ? _T("\\Seen") : _T(" \\Seen");
            if (pDelegateRights->GetAllow(ACLPermission::PermissionWriteOthers))
               sPermanentFlags += sPermanentFlags.IsEmpty() ? _T("\\Draft \\Answered \\Flagged") : _T(" \\Draft \\Answered \\Flagged");
         }

         sRespTemp.Format(_T("* OK [PERMANENTFLAGS (%s)] limited\r\n"), sPermanentFlags.c_str());
         sResponse += sRespTemp;
      }
      else
      {
         sResponse += _T("* OK [PERMANENTFLAGS (\\Deleted \\Seen \\Draft \\Answered \\Flagged)] limited\r\n");
      }

      // RFC 7162 (QRESYNC): replay flag/MODSEQ changes since the client's mod-sequence.
      if (qresyncRequested)
      {
         String sVanishedSet;

         if (PersistentIMAPFolder::RemembersExpungesSince(pSelectedFolder->GetID(), qresyncModSeq))
         {
            sVanishedSet = IMAPConnection::CompactUidSet(
               PersistentIMAPFolder::GetExpungedUIDsSince(pSelectedFolder->GetID(), qresyncModSeq));
         }
         else
         {
            /*
               The expunge records covering this client's mod-sequence have been
               pruned (see IMAPExpungeRetentionTask), so the table can no longer say
               which UIDs went. RFC 7162 section 3.2.6 says what to do instead:
               "behave as if it was requested to report all expunged messages from
               the provided UID set parameter".

               The client did not provide a UID set - this server does not parse the
               optional known-uids argument - and section 3.2.5.1 says what that
               means: "If the client did not provide the list of UIDs, the server
               acts as if the client has specified '1:<maxuid>', where <maxuid> is
               the mailbox's UIDNEXT value minus 1."

               Answering with the records that happen to be left instead would be the
               one genuinely harmful outcome: the client would be told a subset
               vanished and would go on believing the rest are still there.
            */
            std::vector<std::pair<unsigned int, unsigned int>> wholeMailbox;
            wholeMailbox.push_back(std::make_pair((unsigned int) 1, pSelectedFolder->GetCurrentUID()));

            sVanishedSet = IMAPConnection::CompactMissingUidSet(messages, wholeMailbox, pSelectedFolder->GetCurrentUID());
         }

         if (!sVanishedSet.IsEmpty())
         {
            String sVanished;
            sVanished.Format(_T("* VANISHED (EARLIER) %s\r\n"), sVanishedSet.c_str());
            sResponse += sVanished;
         }

         sResponse += pConnection->GetQResyncChangedFetch(qresyncModSeq);
      }

      if (writeAccess)
         sResponse += pArgument->Tag() + _T(" OK [READ-WRITE] SELECT completed\r\n");
      else
         sResponse += pArgument->Tag() + _T(" OK [READ-ONLY] SELECT completed\r\n");


      pConnection->SendAsciiData(sResponse);   
 
      return IMAPResult();
   }
}