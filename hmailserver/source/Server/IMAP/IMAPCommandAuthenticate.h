// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "IMAPCommand.h"
namespace HM
{
   class IMAPConnection;
   class Account;
   class IMAPCommandArgument;

   class IMAPCommandAUTHENTICATE : public IMAPCommand
   {
   public:
      virtual IMAPResult ExecuteCommand(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument);

   private:
      // SCRAM-SHA-256 (RFC 5802 / RFC 7677) multi-step SASL exchange. State lives on
      // the connection (this handler is a shared singleton), driven across lines by
      // re-seeding the command buffer the same way the PLAIN path does. bPlus selects
      // the channel-bound SCRAM-SHA-256-PLUS variant (RFC 5929 tls-server-end-point).
      IMAPResult StartScram_(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument, const String &sInitialResponse, bool bHasInitialResponse, bool bPlus);
      IMAPResult ContinueScram_(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument, const String &sClientData);
      IMAPResult ProcessScramClientFirst_(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument, const String &sClientData);
      IMAPResult ProcessScramClientFinal_(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument, const String &sClientData);
      IMAPResult FinishScram_(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument);
      IMAPResult AbortScram_(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument, const String &sMessage);
      IMAPResult ScramAuthFailed_(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument, const String &sUsername);
      std::shared_ptr<const Account> LookupPbkdf2Account_(const String &sAddress);
   };
}