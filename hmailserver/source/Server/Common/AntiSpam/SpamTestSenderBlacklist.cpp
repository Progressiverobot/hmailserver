// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "SpamTestSenderBlacklist.h"

#include "SpamTestData.h"
#include "SpamTestResult.h"

#include "BlockedSenderCache.h"
#include "../BO/BlockedSender.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   String
   SpamTestSenderBlacklist::GetName() const
   {
      return "SpamTestSenderBlacklist";
   }

   bool
   SpamTestSenderBlacklist::GetIsEnabled()
   {
      // There is no separate on/off switch, mirroring the whitelist: an
      // empty list is the off state, and RunTest is a single walk of an
      // in-memory list, so an empty list costs nothing per message.
      return true;
   }

   std::set<std::shared_ptr<SpamTestResult> >
   SpamTestSenderBlacklist::RunTest(std::shared_ptr<SpamTestData> pTestData)
   {
      std::set<std::shared_ptr<SpamTestResult> > setSpamTestResults;

      const String sFromAddress = pTestData->GetEnvelopeFrom();

      if (sFromAddress.IsEmpty())
      {
         // The null sender (<>) carries bounces and delivery status
         // notifications. It is never matched against the list.
         return setSpamTestResults;
      }

      BlockedSenderCache cache;
      std::shared_ptr<BlockedSender> pEntry = cache.GetBlockingEntry(sFromAddress);

      if (pEntry)
      {
         // The text below is sent to the connecting client in the 550 and
         // written into the X-hMailServer-Reason header. It deliberately
         // does not say which entry matched: the refusal itself already
         // tells the sender they are listed, and naming the entry would
         // additionally reveal whether the address or its whole domain is
         // listed. The matched entry goes to the debug log instead, where
         // the administrator can see it.
         String sDebugMessage;
         sDebugMessage.Format(_T("Envelope sender %s matched blocked-senders entry %s (score %d)."),
            sFromAddress.c_str(), pEntry->GetAddress().c_str(), pEntry->GetScore());
         LOG_DEBUG(sDebugMessage);

         String sMessage = "Sender address is blocked.";

         std::shared_ptr<SpamTestResult> pResult =
            std::shared_ptr<SpamTestResult>(new SpamTestResult(GetName(), SpamTestResult::Fail, pEntry->GetScore(), sMessage));

         setSpamTestResults.insert(pResult);
      }

      return setSpamTestResults;
   }
}
