// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "SecurityRanges.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   SecurityRanges::SecurityRanges()
   {

   }

   SecurityRanges::~SecurityRanges()
   {

   }


   void
   SecurityRanges::Refresh()
   //---------------------------------------------------------------------------
   // DESCRIPTION:
   // Refreshes the collection from the database.
   //---------------------------------------------------------------------------
   {
      String sSQL;
      sSQL.Format(_T("select * from hm_securityranges order by rangeexpires asc, rangepriorityid desc, rangename asc"));

      DBLoad_(sSQL);

      return;
   }
   bool
   SecurityRanges::SetDefault()
   //---------------------------------------------------------------------------
   // DESCRIPTION:
   // Reverts back to the default IP ranges
   //---------------------------------------------------------------------------
   // This deletes every range before it writes the replacements, and it used to
   // discard the result of both writes and return void - so a database that failed
   // between the delete and the second insert left the server with a partial set of
   // IP ranges, or none, and said nothing to anybody. The COM caller got S_OK and
   // the administration tool reported that the defaults had been restored.
   //
   // The direction of that failure is at least the safe one: an address that matches
   // no range is refused rather than allowed, so a half-finished revert stops mail
   // rather than relaying it. That makes this a denial of service on the one action
   // an administrator takes when they are already trying to fix something, which is
   // reason enough to report it instead of hiding it.
   {
      Refresh();

      // Delete all existing ports and then add new ones.
      DeleteAll();

      std::shared_ptr<SecurityRange> pSecurityRange = std::shared_ptr<SecurityRange>(new SecurityRange);
      pSecurityRange->SetName("My computer");
      pSecurityRange->SetPriority(30);
      pSecurityRange->SetLowerIPString("127.0.0.1");
      pSecurityRange->SetUpperIPString("127.0.0.1");
      pSecurityRange->SetOptions(71627);
      bool localRangeSaved = PersistentSecurityRange::SaveObject(pSecurityRange);

      pSecurityRange = std::shared_ptr<SecurityRange>(new SecurityRange);
      pSecurityRange->SetName("Internet");
      pSecurityRange->SetPriority(10);
      pSecurityRange->SetLowerIPString("0.0.0.0");
      pSecurityRange->SetUpperIPString("255.255.255.255");
      pSecurityRange->SetOptions(96203);
      bool internetRangeSaved = PersistentSecurityRange::SaveObject(pSecurityRange);

      Refresh();

      // Both are attempted before reporting, so that a failure on the first still
      // leaves the second in place - the ranges are independent rows and half of the
      // default set is better than none of it.
      if (!localRangeSaved || !internetRangeSaved)
      {
         String message;
         message.Format(_T("Restoring the default IP ranges failed. The existing ranges were deleted; 'My computer' was %s and 'Internet' was %s. Until this is corrected the server will refuse connections from any address that no longer matches a range."),
            localRangeSaved ? _T("restored") : _T("NOT restored"),
            internetRangeSaved ? _T("restored") : _T("NOT restored"));

         ErrorManager::Instance()->ReportError(ErrorManager::High, 6078, "SecurityRanges::SetDefault", message);

         return false;
      }

      return true;
   }
}

