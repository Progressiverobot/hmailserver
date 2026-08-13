// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class IMAPFolder;
   class IMAPFolders;
   class ACLPermission;
   class ACLPermissions;

   class ACLManager
   {
   public:
      ACLManager(void);
      ~ACLManager(void);
      
      // Whether folder access control is enforced at all.
      //
      // The bypass this answers - "IMAP ACL has been disabled, allow everything" -
      // was written out twice, in two neighbouring functions in IMAPConnection, each
      // reading the setting itself and each deciding separately what "allow
      // everything" means. Two copies of a security decision is one more than can be
      // changed correctly: anything that narrows this later - ACL enforced on shared
      // folders but not on a user's own, say - has to find both, and finding one is
      // indistinguishable from finding both until somebody reads other people's mail.
      //
      // It lives here rather than on IMAPConnection because IMAP is not the only
      // thing that needs the answer. The REST API and shared mailboxes both introduce
      // paths where access has to be decided against the owner of the folder rather
      // than the logged-in account, and they will ask this class, not an IMAP
      // connection. A small down payment on the single authorisation choke point:
      // one decision rather than one decision per protocol.
      static bool GetAclEnforcementEnabled();

      std::shared_ptr<ACLPermission> GetPermissionForFolder(__int64 iAccountID, std::shared_ptr<IMAPFolder> pFolder);

      bool SetACL(std::shared_ptr<IMAPFolder> pFolder, const String& sIdentifier, const String &sPermissions);

	private:

      std::shared_ptr<ACLPermission> GetPermissionForAccount_(std::shared_ptr<ACLPermissions> pPermissions, __int64 iAccountID);
   };
}