// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{

   class IMAPFolder;
   class ACL;
   enum PersistenceMode;

   class PersistentIMAPFolder
   {
   private:
	   PersistentIMAPFolder();
	   virtual ~PersistentIMAPFolder();
   public:

      static bool DeleteObject(std::shared_ptr<IMAPFolder> pFolder);
      static bool DeleteObject(std::shared_ptr<IMAPFolder> pFolder, bool forceDelete);
      static bool SaveObject(std::shared_ptr<IMAPFolder> pFolder, String &errorMessage, PersistenceMode mode);
      static bool SaveObject(std::shared_ptr<IMAPFolder> pFolder);
      static bool DeleteByAccount(__int64 iAccountID);

      static bool GetExistsFolderContainingCharacter(String theChar);

      static unsigned int GetUniqueMessageID(__int64 accountID, __int64 folderID);

      // RFC 7162 (CONDSTORE/QRESYNC): atomically bumps and returns the next
      // per-mailbox mod-sequence value, used when a message arrives or its flags change.
      static __int64 GetNextModSeq(__int64 accountID, __int64 folderID);

      static __int64 GetUserInboxFolder(__int64 accountID);

   private:

      static bool IncreaseCurrentUID_(__int64 folderID);
      static unsigned int GetCurrentUID_(__int64 folderID);

      static bool IncreaseCurrentModSeq_(__int64 folderID);
      static __int64 GetCurrentModSeq_(__int64 folderID);


   };
}
