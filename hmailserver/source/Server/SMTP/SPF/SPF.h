// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class SPF : public Singleton<SPF>
   {
   public:
      SPF(void);
      ~SPF(void);

      enum Result
      {
         Neutral = 0,
         Fail = 1,
         Pass = 2
      };

      Result Test(const String &sSenderIP, const String &sSenderEmail, const String &sHeloHost, String &sExplanation);  

      // Evaluates a policy supplied by the caller instead of one fetched from DNS,
      // and returns the library's OWN result code rather than the three-value Result
      // above - so a permerror is distinguishable from a neutral, which Test()
      // deliberately collapses because nothing on the delivery path acts on the
      // difference.
      //
      // For the self-tests. It is the only way to pin behaviour that depends on what
      // a policy CONTAINS without publishing a zone: the SPF library resolves through
      // DnsQuery directly rather than through this server's resolver, so the test
      // harness's DNS server cannot be put in front of it. The mechanism targets are
      // still resolved for real, which is the point - a lookup that finds nothing has
      // to actually find nothing.
      int EvaluatePolicy(const String &sSenderIP, const String &sSenderEmail,
                         const String &sHeloHost, const String &sPolicy);

   private:
      
   };

   class SPFTester
   {
   public :
      SPFTester () {};
      ~SPFTester () {};      

      void Test();

   private:

      void TestVoidLookupLimit_();
      void AssertPolicyResult_(const String &policy, int expected, const String &what);
   };
}