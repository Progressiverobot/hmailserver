// Copyright (c) 2010 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "IMAPCommand.h"

namespace HM
{
   class IMAPFolder;

   class IMAPCommandCREATE : public IMAPCommand
   {
      virtual IMAPResult ExecuteCommand(std::shared_ptr<HM::IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument);

   private:

      IMAPResult ConfirmPossibleToCreate(std::shared_ptr<HM::IMAPConnection> pConnection, const std::vector<String> &vecNewPath, bool bIsPublicFolder);

      // RFC 6154 section 3: can the requested special-use attributes be granted at all?
      // Refuses a public folder, and refuses an attribute that a folder other than
      // excludeFolderID already owns in this mailbox. Pass 0 as excludeFolderID when
      // the mailbox is about to be created and therefore has no id yet.
      IMAPResult ConfirmPossibleToDesignate_(std::shared_ptr<HM::IMAPConnection> pConnection, bool bIsPublicFolder, int requestedDesignations, __int64 excludeFolderID);

      // Stores the designation on the folder that was just created.
      IMAPResult ApplyDesignations_(std::shared_ptr<HM::IMAPConnection> pConnection, const String &sFolderName, int requestedDesignations);

      // Adds the requested designation to a mailbox that already exists. This is the
      // one part of the feature RFC 6154 does not define; see the comment on the
      // implementation for why it is spelled as a CREATE.
      IMAPResult DesignateExistingFolder_(std::shared_ptr<HM::IMAPConnection> pConnection, std::shared_ptr<IMAPFolder> pFolder, int requestedDesignations);

      // Writes the designation and reports the failure. Shared by the create path and
      // the designate-existing path so that one bad database write is logged with one
      // error code and answered with one response text.
      IMAPResult StoreDesignations_(std::shared_ptr<IMAPFolder> pFolder, const String &sFolderName, int designations);
   };
}
