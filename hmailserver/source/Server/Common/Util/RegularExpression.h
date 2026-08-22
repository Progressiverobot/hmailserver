// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class RegularExpression
   {
   public:
      RegularExpression(void);
      ~RegularExpression(void);

      static bool TestExactMatch(const String &sExpression, const String &sValue);

      
   };

   class RegularExpressionTester
   {
   public:
      RegularExpressionTester() {}; 

      static void Test();
   };
}