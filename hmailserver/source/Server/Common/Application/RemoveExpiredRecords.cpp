// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"

#include "RemoveExpiredRecords.h"

#include "../Persistence/PersistentSecurityRange.h"
#include "../Persistence/PersistentLogonFailure.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   RemoveExpiredRecords::RemoveExpiredRecords(void)
   {
   }

   RemoveExpiredRecords::~RemoveExpiredRecords(void)
   {
   }
   
   void
   RemoveExpiredRecords::DoWork()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Remove old records from the greylist
   //---------------------------------------------------------------------------()
   {
      // This is what un-bans an address once its auto-ban expires, and its result was
      // discarded - the mirror image of the auto-ban save that was also unchecked.
      // A failure here does not fail open, it fails *closed* and stays that way: the
      // expired range keeps matching, so the address is refused indefinitely, and
      // "I can't connect and the server says nothing" is a support call nobody can
      // answer from the logs. The task runs again on its own schedule, so one failure
      // corrects itself; being told about it is what turns a permanent-looking ban
      // into a transient one.
      if (!PersistentSecurityRange::DeleteExpired())
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6112, "RemoveExpiredRecords::DoWork",
            "Expired IP ranges could not be removed. Any auto-ban that has run out will keep blocking the address until this succeeds on a later run.");
      }


      if (Configuration::Instance()->GetAutoBanLogonEnabled())
      {
         int logonFailureMinutes = Configuration::Instance()->GetMaxLogonAttemptsWithin();
         
         PersistentLogonFailure persistentLogonFailure;
         persistentLogonFailure.ClearOldFailures(logonFailureMinutes);
      }
   }

}