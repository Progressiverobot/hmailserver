// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "IMAPConnection.h"
#include "IMAPCommand.h"


namespace HM
{
   class IMAPCommand;

   class StaticIMAPCommandHandlers : public Singleton<StaticIMAPCommandHandlers>
   {

   public:
	   StaticIMAPCommandHandlers();
      static std::map<IMAPConnection::eIMAPCommandType, std::shared_ptr<IMAPCommand> > &GetStaticHandlers() {return mapCommandHandlers; }

   private:
      

      static std::map<IMAPConnection::eIMAPCommandType, std::shared_ptr<IMAPCommand> > mapCommandHandlers;
   };

   class IMAPCommandUNKNOWN : public IMAPCommand
   {
      virtual IMAPResult ExecuteCommand(std::shared_ptr<HM::IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument);

   };

   class IMAPCommandNOOP : public IMAPCommand
   {
      virtual IMAPResult ExecuteCommand(std::shared_ptr<HM::IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument);
   };

   // RFC 3691: closes the selected mailbox WITHOUT the implicit EXPUNGE that CLOSE performs.
   class IMAPCommandUNSELECT : public IMAPCommand
   {
      virtual IMAPResult ExecuteCommand(std::shared_ptr<HM::IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument);
   };

   // RFC 5161: negotiates use of extensions that change behaviour the client must opt in to.
   class IMAPCommandENABLE : public IMAPCommand
   {
      virtual IMAPResult ExecuteCommand(std::shared_ptr<HM::IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument);
   };



}