// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once


#include "IMAPCommand.h"

namespace HM
{
   class IMAPFolder;

   class IMAPCommandRENAME : public IMAPCommand
   {
      virtual IMAPResult ExecuteCommand(std::shared_ptr<HM::IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument);

   private:

      IMAPResult ConfirmPossibleToRename(std::shared_ptr<HM::IMAPConnection> pConnection, std::shared_ptr<IMAPFolder> pFolderToRename, const std::vector<String> &vecOldPath, const std::vector<String> &vecNewPath);

      std::shared_ptr<IMAPFolder> GetParentFolder(std::shared_ptr<HM::IMAPConnection> pConnection, const std::vector<String> &vecFolderPath);

      // True when the path names the RFC 2342 "Other Users" namespace
      // ("#Users<delim>..."). Shape only - whether the path resolves, and for
      // whom, is decided elsewhere.
      static bool IsOtherUsersPath_(const std::vector<String> &vecPath);

      // The rename flow for anything that touches the "#Users" namespace: the
      // source folder belongs to another account, or either path is
      // namespace-shaped. Decides in terms of which account OWNS the source
      // folder and which account the destination path names, and refuses every
      // combination that would move a folder between owners - a rename never
      // moves message files between mailbox directories, so an owner-crossing
      // rename would strand the messages in a directory the new tree never
      // reads.
      IMAPResult ExecuteOtherUsersRename_(std::shared_ptr<HM::IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument, std::shared_ptr<IMAPFolder> pFolderToRename, const std::vector<String> &vecNewPath);
   };

}

