// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "SpamTest.h"

namespace HM
{
   class SpamTestSpamAssassin : public SpamTest
   {
   public:
      
      virtual SpamTestType GetTestType()
      {
         return SpamTest::PostTransmission;
      }

      virtual String GetName() const;
      virtual bool GetIsEnabled();
      virtual std::set<std::shared_ptr<SpamTestResult> > RunTest(std::shared_ptr<SpamTestData> pTestData);

   private:

      int ParseSpamAssassinScore_(const AnsiString &sHeader);

      

   };

}