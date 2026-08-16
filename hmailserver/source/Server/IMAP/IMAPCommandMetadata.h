// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#pragma once

#include "IMAPCommand.h"

namespace HM
{
   class IMAPFolder;

   // RFC 5464 (METADATA): annotations on mailboxes and on the server itself.
   // Shared plumbing for the two commands: entry-name validation and the
   // mapping from a mailbox name and a namespace to the store's two ids.
   class IMAPCommandMetadataBase : public IMAPCommand
   {
   protected:
      // Resolves the target: "" is the server, anything else must be an
      // accessible folder. Returns false with a response already decided when
      // the target is unusable.
      bool ResolveTarget_(std::shared_ptr<IMAPConnection> pConnection, const String &mailboxName,
                          std::shared_ptr<IMAPFolder> &folder, __int64 &folderId, String &error);

      // A valid entry name starts with /private/ or /shared/ and contains no
      // wildcards, no empty segments and no trailing slash (RFC 5464 section 3.1).
      bool IsValidEntryName_(const String &entryName);

      // The store's account id for an entry: the session's account for
      // /private, 0 for /shared - which is what makes a shared entry one row
      // visible to every session with access to the folder.
      __int64 EntryAccountId_(std::shared_ptr<IMAPConnection> pConnection, const String &entryName);

      bool IsSharedEntry_(const String &entryName);
   };

   class IMAPCommandGETMETADATA : public IMAPCommandMetadataBase
   {
      virtual IMAPResult ExecuteCommand(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument);
   };

   class IMAPCommandSETMETADATA : public IMAPCommandMetadataBase
   {
      virtual IMAPResult ExecuteCommand(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument);
   };
}
