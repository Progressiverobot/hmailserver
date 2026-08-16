// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "IMAPCommand.h"

namespace HM
{
   
   class IMAPCommandLIST  : public IMAPCommand
   {
   public:
	   IMAPCommandLIST();
	   virtual ~IMAPCommandLIST();

      virtual IMAPResult ExecuteCommand(std::shared_ptr<HM::IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument);

   private:
      // RFC 5258: split a parenthesised mailbox-pattern list into individual patterns.
      static void ExtractPatterns_(const String &sParenContent, std::vector<String> &patterns);

      // True when the parenthesised selection/return option list contains sOption as a
      // whole, case-insensitive token.
      static bool HasOption_(const String &sParenContent, const String &sOption);

      // RFC 5819 (LIST-STATUS): builds the "* STATUS ..." line that follows one
      // "* LIST ..." line, or "" for mailboxes that get none (\Noselect, no read
      // permission, lookup failure).
      static String CreateListStatusLine_(std::shared_ptr<IMAPConnection> pConnection, const String &sListLine, const String &sStatusItems);
   };

}
