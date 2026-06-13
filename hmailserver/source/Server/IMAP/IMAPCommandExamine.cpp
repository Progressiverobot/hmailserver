// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "IMAPCommandExamine.h"
#include "IMAPConnection.h"
#include "IMAPSimpleCommandParser.h"

#include "MessagesContainer.h"

#include "../Common/BO/ACLPermission.h"
#include "../Common/BO/IMAPFolders.h"
#include "../Common/BO/IMAPFolder.h"

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
         return IMAPResult(IMAPResult::ResultBad, "Folder could not be found.");

      if (!pConnection->CheckPermission(pSelectedFolder, ACLPermission::PermissionRead))
         return IMAPResult(IMAPResult::ResultBad, "ACL: Read permission denied (Required for EXAMINE command).");

      pConnection->SetCurrentFolder(pSelectedFolder, true);
      
      std::set<__int64> recent_messages;
      auto messages = MessagesContainer::Instance()->GetMessages(pSelectedFolder->GetAccountID(), pSelectedFolder->GetID(), recent_messages, false);

      pConnection->SetRecentMessages(recent_messages);

      long lCount = messages->GetCount();
      __int64 lFirstUnseenID = messages->GetFirstUnseenUID();
      long lRecentCount = (int) recent_messages.size();

      String sRespTemp;
   
      sRespTemp.Format(_T("* %d EXISTS\r\n"), lCount);
      String sResponse = sRespTemp; // EXISTS

      sRespTemp.Format(_T("* %d RECENT\r\n"), lRecentCount);
      sResponse += sRespTemp;

      sResponse += _T("* FLAGS (\\Deleted \\Seen \\Draft \\Answered \\Flagged)\r\n");
   
      sRespTemp.Format(_T("* OK [UIDVALIDITY %d] current uidvalidity\r\n"), pSelectedFolder->GetCreationTime().ToInt());   
      sResponse += sRespTemp;

      if (lFirstUnseenID > 0)
      {
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
         sResponse += pConnection->GetQResyncChangedFetch(qresyncModSeq);

      sResponse += pArgument->Tag() + _T(" OK [READ-ONLY] EXAMINE completed\r\n");

      pConnection->SendAsciiData(sResponse);   

      return IMAPResult();
   }
}