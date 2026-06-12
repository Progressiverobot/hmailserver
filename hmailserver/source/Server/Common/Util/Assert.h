// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

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