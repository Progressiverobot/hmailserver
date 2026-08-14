// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"

#include "SpamTestRunner.h"

#include "SpamTestData.h"
#include "SpamTestResult.h"

#include "SpamTestDNSBlackLists.h"
#include "SpamTestHeloHost.h"
#include "SpamTestPTR.h"
#include "SpamTestMXRecords.h"
#include "SpamTestSPF.h"
#include "SpamTestSURBL.h"
#include "SpamTestSpamAssassin.h"
#include "DKIM/SpamTestArc.h"
#include "DKIM/SpamTestDKIM.h"
#include "DMARC/SpamTestDMARC.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   SpamTestRunner::SpamTestRunner(void)
   {

   }

   SpamTestRunner::~SpamTestRunner(void)
   {

   }

   void 
   SpamTestRunner::LoadSpamTests()
   {
      spam_tests_.push_back(std::shared_ptr<SpamTestDNSBlackLists> (new SpamTestDNSBlackLists));
      spam_tests_.push_back(std::shared_ptr<SpamTestHeloHost> (new SpamTestHeloHost));
      spam_tests_.push_back(std::shared_ptr<SpamTestPTR> (new SpamTestPTR));
      spam_tests_.push_back(std::shared_ptr<SpamTestMXRecords> (new SpamTestMXRecords));
      spam_tests_.push_back(std::shared_ptr<SpamTestSPF> (new SpamTestSPF));
      spam_tests_.push_back(std::shared_ptr<SpamTestSURBL> (new SpamTestSURBL));
      spam_tests_.push_back(std::shared_ptr<SpamTestDKIM> (new SpamTestDKIM));
      // ARC must run before DMARC: its only output is a negative offset of the
      // DMARC failure score, and the early-out below on iMaxScore would skip a
      // test registered after DMARC in exactly the case the offset exists for.
      spam_tests_.push_back(std::shared_ptr<SpamTestArc> (new SpamTestArc));
      spam_tests_.push_back(std::shared_ptr<SpamTestDMARC> (new SpamTestDMARC));
      spam_tests_.push_back(std::shared_ptr<SpamTestSpamAssassin> (new SpamTestSpamAssassin));
   }

   std::set<std::shared_ptr<SpamTestResult> >
   SpamTestRunner::RunSpamTest(std::shared_ptr<SpamTestData> pInputData, SpamTest::SpamTestType iType, int iMaxScore)
   {
      auto iter = spam_tests_.begin(); 
      auto iterEnd = spam_tests_.end();

      std::set<std::shared_ptr<SpamTestResult> > setTotalResult;

      int iTotalScore = 0;

      for (; iter != iterEnd; iter++)
      {
         std::shared_ptr<SpamTest> pSpamTest = (*iter);

         if (!pSpamTest->GetIsEnabled())
            continue;

         // Pre or post transmission?

         if (pSpamTest->GetTestType() != iType)
            continue;
         
         String sName = pSpamTest->GetName();

         // Time each test. Post-transmission tests (and, for an incoming relay, all
         // of them) run on the thread that will send the "250"; a single slow test
         // - a DNS lookup against a sick resolver, a stalled SpamAssassin - is what
         // makes a relayed message time out on the sender (discussion #18). The
         // name plus elapsed here is what tells DNSBL from SURBL from SPF from
         // SpamAssassin on a live report.
         const ULONGLONG testStartTick = GetTickCount64();

         std::set<std::shared_ptr<SpamTestResult> > setResult = pSpamTest->RunTest(pInputData);

         const ULONGLONG testElapsed = GetTickCount64() - testStartTick;

         auto iter = setResult.begin();
         auto iterEnd = setResult.end();

         int totalScoreBefore = iTotalScore;
         for (; iter != iterEnd; iter++)
         {
            std::shared_ptr<SpamTestResult> pResult = (*iter);
            setTotalResult.insert(pResult);

            iTotalScore += pResult->GetSpamScore();
         }

         int totalDiff = iTotalScore - totalScoreBefore;

         String sSpamTestResult;
         sSpamTestResult.Format(_T("Spam test: %s, Score: %d, Time: %I64u ms"), sName.c_str(), totalDiff, testElapsed);
         if (testElapsed >= 10000)
         {
            LOG_APPLICATION(sSpamTestResult);
         }
         else
         {
            LOG_DEBUG(sSpamTestResult);
         }

         // Stop only if there is a threshold to reach. iMaxScore is
         // max(mark threshold, delete threshold), and both are disabled by being
         // set to zero - a supported configuration, because greylisting and the
         // white list do not depend on scoring. With iMaxScore at zero the
         // condition below was true before a single test had run, so the pipeline
         // stopped after the first enabled test. Nothing about scoring changed
         // (neither action is armed), but SpamProtection::PerformGreyListing looks
         // for the SPF result *in this result set* to honour
         // BypassGreyListingOnSPFSuccess - and SPF is the fifth test, so it never
         // ran and the bypass silently stopped working.
         if (iMaxScore > 0 && iTotalScore >= iMaxScore)
         {
            // Threshold has been reached. No point in running any more tests.
            break;
         }

      }

      String sSpamTestResult;
      sSpamTestResult.Format(_T("Total spam score: %d"), iTotalScore);
      LOG_DEBUG(sSpamTestResult);
      
      return setTotalResult;
   }
}