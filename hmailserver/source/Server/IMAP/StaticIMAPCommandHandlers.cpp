// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "StaticIMAPCommandHandlers.h"

#include "IMAPCommandAuthenticate.h"
#include "IMAPCommandLogin.h"
#include "IMAPCommandCheck.h"
#include "IMAPCommandSelect.h"
#include "IMAPCommandClose.h"
#include "IMAPCommandCreate.h"
#include "IMAPCommandDelete.h"
#include "IMAPCommandExamine.h"
#include "IMAPCommandExpunge.h"
#include "IMAPCommandSubscribe.h"
#include "IMAPCommandUnsubscribe.h"
#include "IMAPCommandStatus.h"
#include "IMAPCommandRename.h"
#include "IMAPCommandList.h"
#include "IMAPCommandLsub.h"
#include "IMAPCommandCopy.h"
#include "IMAPCommandMove.h"
#include "IMAPCommandID.h"
#include "IMAPCommandFetch.h"
#include "IMAPCommandCapability.h"
#include "IMAPCommandStore.h"
#include "IMAPCommandLogout.h"
#include "IMAPCommandNamespace.h"
#include "IMAPCommandMyRights.h"
#include "IMAPCommandGetAcl.h"
#include "IMAPCommandDeleteAcl.h"
#include "IMAPCommandSetAcl.h"
#include "IMAPCommandListRights.h"
#include "IMAPCommandStartTls.h"

// IMAP QUOTA EXTENSION
#include "IMAPCommandGetQuota.h"
#include "IMAPCommandGetQuotaRoot.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   std::map<IMAPConnection::eIMAPCommandType, std::shared_ptr<IMAPCommand> > StaticIMAPCommandHandlers::mapCommandHandlers;

   StaticIMAPCommandHandlers::StaticIMAPCommandHandlers()
   {
      mapCommandHandlers[IMAPConnection::IMAP_LOGIN] = std::shared_ptr<IMAPCommandLOGIN>(new IMAPCommandLOGIN());
      mapCommandHandlers[IMAPConnection::IMAP_CHECK] = std::shared_ptr<IMAPCommandCHECK>(new IMAPCommandCHECK());
      mapCommandHandlers[IMAPConnection::IMAP_SELECT] = std::shared_ptr<IMAPCommandSELECT>(new IMAPCommandSELECT());
      mapCommandHandlers[IMAPConnection::IMAP_CLOSE] = std::shared_ptr<IMAPCommandCLOSE>(new IMAPCommandCLOSE());
      mapCommandHandlers[IMAPConnection::IMAP_CREATE] = std::shared_ptr<IMAPCommandCREATE>(new IMAPCommandCREATE());
      mapCommandHandlers[IMAPConnection::IMAP_DELETE] = std::shared_ptr<IMAPCommandDELETE>(new IMAPCommandDELETE());
      mapCommandHandlers[IMAPConnection::IMAP_EXAMINE] = std::shared_ptr<IMAPCommandEXAMINE>(new IMAPCommandEXAMINE());
      mapCommandHandlers[IMAPConnection::IMAP_EXPUNGE] = std::shared_ptr<IMAPCommandEXPUNGE>(new IMAPCommandEXPUNGE());
      mapCommandHandlers[IMAPConnection::IMAP_UNSUBSCRIBE] = std::shared_ptr<IMAPCommandUNSUBSCRIBE>(new IMAPCommandUNSUBSCRIBE());
      mapCommandHandlers[IMAPConnection::IMAP_SUBSCRIBE] = std::shared_ptr<IMAPCommandSUBSCRIBE>(new IMAPCommandSUBSCRIBE());
      mapCommandHandlers[IMAPConnection::IMAP_STATUS] = std::shared_ptr<IMAPCommandSTATUS>(new IMAPCommandSTATUS());
      mapCommandHandlers[IMAPConnection::IMAP_RENAME] = std::shared_ptr<IMAPCommandRENAME>(new IMAPCommandRENAME());
      mapCommandHandlers[IMAPConnection::IMAP_LIST] = std::shared_ptr<IMAPCommandLIST>(new IMAPCommandLIST());
      mapCommandHandlers[IMAPConnection::IMAP_LSUB] = std::shared_ptr<IMAPCommandLSUB>(new IMAPCommandLSUB());
      mapCommandHandlers[IMAPConnection::IMAP_COPY] = std::shared_ptr<IMAPCommandCOPY>(new IMAPCommandCOPY());
      mapCommandHandlers[IMAPConnection::IMAP_MOVE] = std::shared_ptr<IMAPCommandMOVE>(new IMAPCommandMOVE());
      mapCommandHandlers[IMAPConnection::IMAP_ID] = std::shared_ptr<IMAPCommandID>(new IMAPCommandID());
      mapCommandHandlers[IMAPConnection::IMAP_FETCH] = std::shared_ptr<IMAPCommandFETCH>(new IMAPCommandFETCH());
      mapCommandHandlers[IMAPConnection::IMAP_CAPABILITY] = std::shared_ptr<IMAPCommandCapability>(new IMAPCommandCapability());
      mapCommandHandlers[IMAPConnection::IMAP_STORE] = std::shared_ptr<IMAPCommandStore>(new IMAPCommandStore());
      mapCommandHandlers[IMAPConnection::IMAP_AUTHENTICATE] = std::shared_ptr<IMAPCommandAUTHENTICATE>(new IMAPCommandAUTHENTICATE());
      mapCommandHandlers[IMAPConnection::IMAP_NOOP] = std::shared_ptr<IMAPCommandNOOP>(new IMAPCommandNOOP());
      mapCommandHandlers[IMAPConnection::IMAP_LOGOUT] = std::shared_ptr<IMAPCommandLOGOUT>(new IMAPCommandLOGOUT());
      mapCommandHandlers[IMAPConnection::IMAP_UNKNOWN] = std::shared_ptr<IMAPCommandUNKNOWN>(new IMAPCommandUNKNOWN());
      mapCommandHandlers[IMAPConnection::IMAP_GETQUOTAROOT] = std::shared_ptr<IMAPCommandGetQuotaRoot>(new IMAPCommandGetQuotaRoot());
      mapCommandHandlers[IMAPConnection::IMAP_GETQUOTA] = std::shared_ptr<IMAPCommandGetQuota>(new IMAPCommandGetQuota());
      mapCommandHandlers[IMAPConnection::IMAP_NAMESPACE] = std::shared_ptr<IMAPCommandNamespace>(new IMAPCommandNamespace());
      mapCommandHandlers[IMAPConnection::IMAP_MYRIGHTS] = std::shared_ptr<IMAPCommandMyRights>(new IMAPCommandMyRights());
      mapCommandHandlers[IMAPConnection::IMAP_GETACL] = std::shared_ptr<IMAPCommandGetAcl>(new IMAPCommandGetAcl());
      mapCommandHandlers[IMAPConnection::IMAP_DELETEACL] = std::shared_ptr<IMAPCommandDeleteAcl>(new IMAPCommandDeleteAcl());
      mapCommandHandlers[IMAPConnection::IMAP_SETACL] = std::shared_ptr<IMAPCommandSetAcl>(new IMAPCommandSetAcl());
      mapCommandHandlers[IMAPConnection::IMAP_LISTRIGHTS] = std::shared_ptr<IMAPCommandListRights>(new IMAPCommandListRights());
      mapCommandHandlers[IMAPConnection::IMAP_STARTTLS] = std::shared_ptr<IMAPCommandStartTls>(new IMAPCommandStartTls());
      mapCommandHandlers[IMAPConnection::IMAP_UNSELECT] = std::shared_ptr<IMAPCommandUNSELECT>(new IMAPCommandUNSELECT());
      mapCommandHandlers[IMAPConnection::IMAP_ENABLE] = std::shared_ptr<IMAPCommandENABLE>(new IMAPCommandENABLE());
   }



   // Tiny commands

   IMAPResult
   IMAPCommandUNKNOWN::ExecuteCommand(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument)
   {
      pConnection->SendResponseString(pArgument->Tag(), "BAD", "Unknown or NULL command");

      return IMAPResult();
   }

   IMAPResult
   IMAPCommandNOOP::ExecuteCommand(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument)
   {
      pConnection->SendAsciiData(pArgument->Tag() + " OK NOOP completed\r\n");   

      return IMAPResult();
   
   }

   IMAPResult
   IMAPCommandUNSELECT::ExecuteCommand(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument)
   {
      if (!pConnection->IsAuthenticated())
         return IMAPResult(IMAPResult::ResultNo, "Authenticate first");

      auto pCurFolder = pConnection->GetCurrentFolder();
      if (!pCurFolder)
         return IMAPResult(IMAPResult::ResultBad, "No mailbox is selected.");

      // RFC 3691: free the selected mailbox WITHOUT the implicit EXPUNGE that
      // CLOSE performs, so \\Deleted messages are retained.
      pConnection->CloseCurrentFolder();

      pConnection->SendAsciiData(pArgument->Tag() + " OK UNSELECT completed\r\n");

      return IMAPResult();
   }

   IMAPResult
   IMAPCommandENABLE::ExecuteCommand(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument)
   {
      if (!pConnection->IsAuthenticated())
         return IMAPResult(IMAPResult::ResultNo, "Authenticate first");

      // RFC 5161: ENABLE takes a non-empty, space-separated list of capability
      // names following the command word. Strip the leading "ENABLE".
      String sLine = pArgument->Command();
      int iSpace = sLine.Find(_T(" "));
      String sCapabilities = (iSpace >= 0) ? sLine.Mid(iSpace + 1) : _T("");
      sCapabilities.TrimLeft();
      sCapabilities.TrimRight();

      if (sCapabilities.IsEmpty())
         return IMAPResult(IMAPResult::ResultBad, "The ENABLE command requires at least one capability name.");

      String sUpper = sCapabilities;
      sUpper.MakeUpper();

      String sEnabled;

      // RFC 7162: enabling QRESYNC implicitly enables CONDSTORE.
      if (sUpper.Find(_T("QRESYNC")) >= 0)
      {
         pConnection->SetQResyncEnabled(true);
         pConnection->SetCondstoreEnabled(true);
         sEnabled += _T(" QRESYNC");
      }

      if (sUpper.Find(_T("CONDSTORE")) >= 0)
      {
         pConnection->SetCondstoreEnabled(true);
         sEnabled += _T(" CONDSTORE");
      }

      // RFC 5161: only emit the untagged ENABLED response when at least one
      // recognised extension was actually switched on.
      if (!sEnabled.IsEmpty())
         pConnection->SendAsciiData("* ENABLED" + sEnabled + "\r\n");

      pConnection->SendAsciiData(pArgument->Tag() + " OK ENABLE completed\r\n");

      return IMAPResult();
   }

}
