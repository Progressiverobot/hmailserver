// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "SpamTest.h"

namespace HM
{
   class SpamTestData;
   class SpamTestResult;

   class SpamTestRunner
   {
   public:
      
      SpamTestRunner();
      virtual ~SpamTestRunner();

      void LoadSpamTests();

      std::set<std::shared_ptr<SpamTestResult> > RunSpamTest(std::shared_ptr<SpamTestData> pInputData, SpamTest::SpamTestType iType, int iMaxScore);

   private:

      std::vector<std::shared_ptr<SpamTest> > spam_tests_;

   };

}