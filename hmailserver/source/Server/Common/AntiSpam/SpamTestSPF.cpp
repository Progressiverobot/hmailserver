// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "SpamTestSPF.h"

#include "SpamTestData.h"
#include "AuthenticationResults.h"
#include "SpamTestResult.h"

#include "AntiSpamConfiguration.h"

#include "../../SMTP/SPF/SPF.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   String 
   SpamTestSPF::GetName() const
   {
      return GetTestName();
   }

   String 
   SpamTestSPF::GetTestName() 
   {
      return "SpamTestSPF";
   }


   bool 
   SpamTestSPF::GetIsEnabled()
   {
      if (Configuration::Instance()->GetAntiSpamConfiguration().GetUseSPF())
         return true;
      else
         return false;
   }

   std::set<std::shared_ptr<SpamTestResult> > 
   SpamTestSPF::RunTest(std::shared_ptr<SpamTestData> pTestData)
   {
      std::set<std::shared_ptr<SpamTestResult> > setSpamTestResults;

      String sMessage = "";
      int iScore = 0;

      const IPAddress &originatingAddress = pTestData->GetOriginatingIP();

      if (originatingAddress.IsAny())
         return setSpamTestResults;

      String sExplanation;
      SPF::Result result = SPF::Instance()->Test(originatingAddress.ToString(), pTestData->GetEnvelopeFrom(), pTestData->GetHeloHost(), sExplanation);

      // Recorded before the scoring below, because the scoring throws most of it away.
      // Only Fail and Pass produce a SpamTestResult at all, so from the outside a
      // neutral verdict and "SPF never ran" look identical - and RFC 8601 needs to say
      // which of those happened.
      //
      // Every one of the seven results of RFC 7208 section 2.6 is carried straight
      // across, because RFC 8601 section 2.7.2 has a keyword for each of them and they
      // do not mean the same thing to a receiver downstream: "none" says the domain
      // published no policy, "temperror" says ours could not be read and the message
      // may deserve another look, "permerror" says the domain's own record is broken.
      // Until the evaluator was replaced, SPF::Test could only answer Pass, Fail or
      // Neutral, so the four below were written as neutral - a claim about the domain
      // that this server had not actually made.
      std::shared_ptr<AuthenticationResults> authenticationResults = pTestData->GetAuthenticationResults();
      if (authenticationResults)
      {
         AuthenticationResults::MethodResult methodResult = AuthenticationResults::ResultNeutral;

         switch (result)
         {
         case SPF::Result::Pass:
            methodResult = AuthenticationResults::ResultPass;
            break;
         case SPF::Result::Fail:
            methodResult = AuthenticationResults::ResultFail;
            break;
         case SPF::Result::SoftFail:
            methodResult = AuthenticationResults::ResultSoftFail;
            break;
         case SPF::Result::None:
            methodResult = AuthenticationResults::ResultNone;
            break;
         case SPF::Result::TempError:
            methodResult = AuthenticationResults::ResultTempError;
            break;
         case SPF::Result::PermError:
            methodResult = AuthenticationResults::ResultPermError;
            break;
         case SPF::Result::Neutral:
            break;
         }

         // smtp.mailfrom is the identity SPF was evaluated against here; SPF::Test is
         // given the envelope sender, and falls back to HELO only inside the evaluator.
         authenticationResults->SetSpf(methodResult,
                                       "smtp.mailfrom",
                                       AnsiString(pTestData->GetEnvelopeFrom()),
                                       AnsiString(originatingAddress.ToString()),
                                       AnsiString(pTestData->GetHeloHost()));
      }

      // Only a fail is scored and only a pass is recorded as one, which is what this
      // test has always done: a softfail is the sending domain asking that the message
      // be accepted anyway, and neither error is a statement about the message.
      if (result == SPF::Result::Fail)
      {
         // Blocked by SPF.s
         if (!sExplanation.IsEmpty())
            sMessage.Format(_T("Blocked by SPF. (%s)"), sExplanation.c_str());
         else
            sMessage = "Blocked by SPF.";
         iScore = Configuration::Instance()->GetAntiSpamConfiguration().GetUseSPFScore();

         std::shared_ptr<SpamTestResult> pResult = std::shared_ptr<SpamTestResult>(new SpamTestResult(GetName(), SpamTestResult::Fail, iScore, sMessage));
         setSpamTestResults.insert(pResult);
      }      
      else if (result == SPF::Result::Pass)
      {
         std::shared_ptr<SpamTestResult> pResult = std::shared_ptr<SpamTestResult>(new SpamTestResult(GetName(), SpamTestResult::Pass, 0, ""));
         setSpamTestResults.insert(pResult);
      }


      return setSpamTestResults;
   }

}