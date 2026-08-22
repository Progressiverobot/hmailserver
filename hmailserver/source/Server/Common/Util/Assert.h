// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class Assert
   {
   public:
      Assert(void);
      ~Assert(void);

      static void IsTrue(bool argument);
      static void IsFalse(bool argument);
      static void AreEqual(const String &str1, const String &str2);

   private:

    
   };
}