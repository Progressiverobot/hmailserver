// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "IMAPCommand.h"

namespace HM
{
   class IMAPFolder;

   class IMAPCommandSTATUS : public IMAPCommand
   {
   public:
      virtual IMAPResult ExecuteCommand(std::shared_ptr<HM::IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument);

      // Builds one "* STATUS <name> (<items>)\r\n" line for the folder. Shared
      // with LIST's RETURN (STATUS ...) option (RFC 5819), which emits the same
      // line after each listed mailbox. sFolderName is the unescaped full path;
      // no permission check happens here - each caller decides how a denied
      // folder is handled (STATUS errors, LIST-STATUS silently omits the line).
      static String CreateStatusLine(std::shared_ptr<HM::IMAPConnection> pConnection, std::shared_ptr<IMAPFolder> pTheFolder, String sFolderName, const String &sFlags);
   };
}


