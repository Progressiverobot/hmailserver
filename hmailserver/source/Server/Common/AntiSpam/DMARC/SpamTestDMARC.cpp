// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"

#include "SpamTestDMARC.h"

#include "DMARC.h"

#include "../SpamTestData.h"
#include "../SpamTestResult.h"
#include "../AntiSpamConfiguration.h"
#include "../DKIM/DKIM.h"

#include "../../BO/MessageData.h"
#include "../../Persistence/PersistentMessage.h"
#include "../../Util/Parsing/StringParser.h"
#include "../../../SMTP/SPF/SPF.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   String 
   SpamTestDMARC::GetName() const
   {
      return GetTestName();
   }

   String 
   SpamTestDMARC::GetTestName()
   {
      return "SpamTestDMARC";
   }

   bool 
   SpamTestDMARC::GetIsEnabled()
   {
      AntiSpamConfiguration &config = Configuration::Instance()->GetAntiSpamConfiguration();
      return config.GetDMARCEnabled();
   }

   std::set<std::shared_ptr<SpamTestResult> >
   SpamTestDMARC::RunTest(std::shared_ptr<SpamTestData> pTestData)
   {
      std::set<std::shared_ptr<SpamTestResult> > setSpamTestResults;

      std::shared_ptr<MessageData> pMessageData = pTestData->GetMessageData();
      if (!pMessageData)
         return setSpamTestResults;

      // Determine the RFC5322.From domain.
      String fromHeader = pMessageData->GetFrom();
      if (fromHeader.IsEmpty())
         return setSpamTestResults;

      String fromAddress = DMARC::ExtractAddressFromHeaderValue(fromHeader);
      String fromDomain = StringParser::ExtractDomain(fromAddress);
      if (fromDomain.IsEmpty())
         return setSpamTestResults;

      // Evaluate SPF for the envelope sender.
      bool spfPassed = false;
      String envelopeFromDomain = StringParser::ExtractDomain(pTestData->GetEnvelopeFrom());

      const IPAddress &originatingAddress = pTestData->GetOriginatingIP();
      if (!originatingAddress.IsAny() && !pTestData->GetEnvelopeFrom().IsEmpty())
      {
         String sExplanation;
         SPF::Result spfResult = SPF::Instance()->Test(originatingAddress.ToString(), pTestData->GetEnvelopeFrom(), pTestData->GetHeloHost(), sExplanation);
         spfPassed = (spfResult == SPF::Pass);
      }

      // Evaluate DKIM and collect the signing domains that verified.
      std::vector<AnsiString> dkimPassingDomains;

      std::shared_ptr<Message> pMessage = pMessageData->GetMessage();
      if (pMessage)
      {
         const String fileName = PersistentMessage::GetFileName(pMessage);

         DKIM dkim;
         dkim.Verify(fileName, dkimPassingDomains);
      }

      DMARC dmarc;
      DMARC::Result result = dmarc.Verify(fromDomain, envelopeFromDomain, spfPassed, dkimPassingDomains);

      AntiSpamConfiguration &config = Configuration::Instance()->GetAntiSpamConfiguration();

      switch (result)
      {
      case DMARC::Pass:
         {
            std::shared_ptr<SpamTestResult> pResult = std::shared_ptr<SpamTestResult>(new SpamTestResult(GetName(), SpamTestResult::Pass, 0, ""));
            setSpamTestResults.insert(pResult);
            break;
         }
      case DMARC::FailReject:
      case DMARC::FailQuarantine:
         {
            int iScore = config.GetDMARCFailureScore();

            String sMessage;
            sMessage.Format(_T("Blocked by DMARC policy of %s."), fromDomain.c_str());

            std::shared_ptr<SpamTestResult> pResult = std::shared_ptr<SpamTestResult>(new SpamTestResult(GetName(), SpamTestResult::Fail, iScore, sMessage));
            setSpamTestResults.insert(pResult);
            break;
         }
      case DMARC::FailNone:
         {
            // The domain owner requested no action (p=none); only log.
            LOG_DEBUG("DMARC: Message failed DMARC for domain " + AnsiString(fromDomain) + " but policy is none.");
            break;
         }
      default:
         {
            // NoPolicy, TempError or PermError: no scoring.
            break;
         }
      }

      return setSpamTestResults;
   }

}
