// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "IMAPCommandExamine.h"
#include "IMAPConnection.h"
#include "IMAPSimpleCommandParser.h"

#include "MessagesContainer.h"

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
   IMAPCommandEXAMINE::ExecuteCommand(std::shared_ptr<HM::IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument)
   {
      if (!pConnection->IsAuthenticated())
         return IMAPResult(IMAPResult::ResultNo, "Authenticate first");

      // RFC 7162: an "EXAMINE mailbox (CONDSTORE)" parameter enables CONDSTORE for the session.
      // A "EXAMINE mailbox (QRESYNC (uidvalidity modseq ...))" parameter additionally enables
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
         return IMAPResult(IMAPResult::ResultBad, "EXAMINE Command requires at least 1 parameter.");

      // Fetch the folder
      String sFolderName = pParser->GetParamValue(pArgument, 0);
      std::shared_ptr<IMAPFolder> pSelectedFolder = pConnection->GetFolderByFullPath(sFolderName);
      
      if (!pSelectedFolder)
      {
         // Also the answer for a shared ("#Users") path the caller lacks the
         // lookup right on: resolution refuses those before this command sees a
         // folder, so "no rights" and "no such folder" are indistinguishable.
         return IMAPResult(IMAPResult::ResultBad, "Folder could not be found.");
      }

      if (!pConnection->CheckPermission(pSelectedFolder, ACLPermission::PermissionRead))
      {
         // RFC 4314: a mailbox the user may look up but not read fails with NO.
         // Delegated folders only resolve at all with the lookup right, so their
         // existence is not a secret on this branch.
         bool bIsDelegatedFolder = pSelectedFolder->GetAccountID() != 0 &&
                                   pSelectedFolder->GetAccountID() != pConnection->GetAccount()->GetID();

         if (bIsDelegatedFolder)
            return IMAPResult(IMAPResult::ResultNo, "ACL: Read permission denied (Required for EXAMINE command).");

         return IMAPResult(IMAPResult::ResultBad, "ACL: Read permission denied (Required for EXAMINE command).");
      }

      pConnection->SetCurrentFolder(pSelectedFolder, true);
      
      std::set<__int64> recent_messages;
      auto messages = MessagesContainer::Instance()->GetMessages(pSelectedFolder->GetAccountID(), pSelectedFolder->GetID(), recent_messages, false);

      pConnection->SetRecentMessages(recent_messages);

      long lCount = messages->GetCount();
      // RFC 3501: [UNSEEN] carries a message sequence number, not a UID
      // (see IMAPCommandSelect).
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
         // RFC 9051 (IMAP4rev2): the [UNSEEN] response code on EXAMINE was removed.
         sRespTemp.Format(_T("* OK [UNSEEN %d] unseen messages\r\n"), lFirstUnseenID);
         sResponse += sRespTemp;
      }

      sRespTemp.Format(_T("* OK [UIDNEXT %d] next uid\r\n"), pSelectedFolder->GetCurrentUID()+1);
      sResponse += sRespTemp;

      // RFC 7162 (CONDSTORE/QRESYNC): report the mailbox HIGHESTMODSEQ once CONDSTORE is enabled.
      if (pConnection->GetCondstoreEnabled())
      {
         sRespTemp.Format(_T("* OK [HIGHESTMODSEQ %I64d] highest mod-sequence\r\n"), pSelectedFolder->GetCurrentModSeq());
         sResponse += sRespTemp;
      }

      sResponse += _T("* OK [PERMANENTFLAGS ()] limited\r\n");

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
            // The expunge records covering this mod-sequence have been pruned. RFC
            // 7162 section 3.2.6 then requires the complete list of UIDs in the
            // requested set that the mailbox no longer holds, which for a client
            // that supplied no UID set is 1:UIDNEXT-1 (section 3.2.5.1). Same rule
            // and same reasoning as SELECT; see IMAPCommandSelect.
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

      sResponse += pArgument->Tag() + _T(" OK [READ-ONLY] EXAMINE completed\r\n");

      pConnection->SendAsciiData(sResponse);   

      return IMAPResult();
   }
}