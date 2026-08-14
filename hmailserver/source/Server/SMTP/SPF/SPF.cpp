// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"

#include "SPF.h"
#include "rmspf.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   SPF::SPF(void)
   {
      // Initialize. This is only done once.
      SPFInit(NULL,0, SPF_Multithread);
   }

   SPF::~SPF(void)
   {

   }

   SPF::Result
   SPF::Test(const String &sSenderIP, const String &sSenderEmail, const String &sHeloHost, String &sExplanation)
   {
      USES_CONVERSION;
      String sDomain = StringParser::ExtractDomain(sSenderEmail);

      // >= 0, not > 0. Find returns the index, and an IPv6 address is allowed to start
      // with its separator - "::1" and the v4-mapped "::ffff:203.0.113.5" both put the
      // first colon at index 0, so those were classified AF_INET, SPFStringToAddr
      // failed to parse them as IPv4 and the function returned Neutral below without
      // evaluating the policy at all.
      //
      // Silent, and in the permissive direction: no Fail score for a -all sender, no
      // greylisting decision, and SpamTestDMARC records spfPassed=false, so an
      // SPF-only DMARC domain lost its alignment and could be forged. Reaching it
      // needs a dual-stack listener with IPV6_V6ONLY off, since the string comes from
      // the remote endpoint's to_string(); Windows defaults that option on, so this is
      // configuration-dependent rather than universal.
      int family;
      if (sSenderIP.Find(_T(":")) >= 0)
         family=AF_INET6;
      else
         family=AF_INET;

      // Convert the IP address from a dotted string
      // to a binary form. We use the SPF library to
      // do this.

      char BinaryIP[100];
      if (SPFStringToAddr(T2A(sSenderIP),family,BinaryIP)==NULL)
         return Neutral;

      const char* explain;
      int result=SPFQuery(family,BinaryIP,T2A(sSenderEmail),NULL,T2A(sHeloHost),NULL,&explain);

      if (explain != NULL)
      {
         sExplanation = explain;
         SPFFree(explain);
      }

      if (result == SPF_Fail)
      {
         // FAIL
         return Fail;
      }
      else if (result == SPF_Pass)
      {
         return Pass;
      }

      return Neutral;
   }

   void SPFTester::Test()
   {
      String sExplanation;
      
      if (SPF::Instance()->Test("185.216.75.37", "example@hmailserver.com", "mail.hmailserver.com", sExplanation) != SPF::Pass)
      {
         // Should be allowed. 
         throw;
      }

      if (SPF::Instance()->Test("1.2.3.4", "example@hmailserver.com", "mail.hmailserver.com", sExplanation) != SPF::Fail)
      {
         // Should not be allowed.
         throw;
      }
   }


}