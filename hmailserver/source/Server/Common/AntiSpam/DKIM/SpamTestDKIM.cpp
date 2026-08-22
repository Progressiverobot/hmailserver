// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "SpamTestDKIM.h"

#include "../SpamTestData.h"
#include "../AuthenticationResults.h"
#include "../SpamTestResult.h"
#include "../../BO/MessageData.h"
#include "../AntiSpamConfiguration.h"
#include "DKIM.h"
#include "../../Persistence/PersistentMessage.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   String 
   SpamTestDKIM::GetName() const
   {
      return "SpamTestDKIM";
   }

   bool 
   SpamTestDKIM::GetIsEnabled()
   {
      AntiSpamConfiguration &config = Configuration::Instance()->GetAntiSpamConfiguration();
      return config.GetDKIMVerificationEnabled();
   }

   std::set<std::shared_ptr<SpamTestResult> >
   SpamTestDKIM::RunTest(std::shared_ptr<SpamTestData> pTestData)
   {
      std::shared_ptr<Message> pMessage = pTestData->GetMessageData()->GetMessage();

      std::set<std::shared_ptr<SpamTestResult> > setSpamTestResults;

      const String fileName = PersistentMessage::GetFileName(pMessage);

      DKIM dkim;

      // The two-argument overload, so the signing domains survive the call. The
      // one-argument form returns only the message-level verdict, and header.d is the
      // whole point of a dkim= clause: "dkim=pass" without it says a signature verified
      // but not whose, which is exactly the part a downstream filter needs.
      std::vector<AnsiString> passingDomains;
      DKIM::Result result = dkim.Verify(fileName, passingDomains);

      std::shared_ptr<AuthenticationResults> authenticationResults = pTestData->GetAuthenticationResults();
      if (authenticationResults)
      {
         if (!passingDomains.empty())
         {
            // One clause per verified signature. A message may legitimately carry
            // several - the author's domain and a forwarder's, say - and collapsing
            // them into one verdict loses the distinction a receiver acts on.
            for (const AnsiString &domain : passingDomains)
               authenticationResults->AddDkim(AuthenticationResults::ResultPass, domain);
         }
         else
         {
            AuthenticationResults::MethodResult methodResult = AuthenticationResults::ResultNone;

            if (result == DKIM::PermFail)
               methodResult = AuthenticationResults::ResultFail;
            else if (result == DKIM::TempFail)
               methodResult = AuthenticationResults::ResultTempError;
            else if (result == DKIM::Neutral)
               methodResult = AuthenticationResults::ResultNeutral;

            authenticationResults->AddDkim(methodResult, "");
         }
      }

      if (result == DKIM::PermFail)
      {
         // Blocked
         AntiSpamConfiguration &config= Configuration::Instance()->GetAntiSpamConfiguration();
         int iSomeScore = config.GetDKIMVerificationFailureScore();
         std::shared_ptr<SpamTestResult> pResult = std::shared_ptr<SpamTestResult>(new SpamTestResult(GetName(), SpamTestResult::Fail, iSomeScore, "Rejected by DKIM."));
         setSpamTestResults.insert(pResult);
      }
      else if (result == DKIM::Pass)
      {
         std::shared_ptr<SpamTestResult> pResult = std::shared_ptr<SpamTestResult>(new SpamTestResult(GetName(), SpamTestResult::Pass, 0, ""));
         setSpamTestResults.insert(pResult);
      }

      return setSpamTestResults;
   }

}