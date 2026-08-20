// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"

#include "SPF.h"
#include "rmspf.h"
#include "../../Common/Application/IniFileSettings.h"
#include "../../Common/Application/ErrorManager.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   // Reached from RMSPF.cpp, which is C and knows nothing about this server's
   // configuration - the same bridge shape DnssecTxtLookupIsBogus already uses.
   // Kept here rather than in the library because the library is vendored, and a
   // setting it read directly would be one more thing to reconcile the next time
   // it is updated.
   int
   SpfVoidLookupLimit()
   {
      return IniFileSettings::Instance()->GetSpfVoidLookupLimit();
   }

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

   int
   SPF::EvaluatePolicy(const String &sSenderIP, const String &sSenderEmail,
                       const String &sHeloHost, const String &sPolicy)
   {
      USES_CONVERSION;

      int family = sSenderIP.Find(_T(":")) >= 0 ? AF_INET6 : AF_INET;

      char BinaryIP[100];
      if (SPFStringToAddr(T2A(sSenderIP), family, BinaryIP) == NULL)
         return SPF_PermError;

      AnsiString policy = sPolicy;

      return SPFQuery(family, BinaryIP, T2A(sSenderEmail), policy.c_str(),
                      T2A(sHeloHost), NULL, NULL);
   }

   void SPFTester::Test()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Evaluates a REAL policy over live DNS, which makes this as much a test of
   // somebody else's zone as of this code - and on 17 August 2026 that zone
   // stopped resolving: hmailserver.com answers SERVFAIL, so the evaluator
   // correctly returned Neutral, the old code threw, and hMailServer.exe /Test
   // terminated on an unhandled exception before it reached any of the tests
   // after this one. A build machine with no network did the same thing.
   //
   // The evaluator cannot distinguish "no policy" from "could not fetch the
   // policy" - it only reports Pass, Fail and Neutral (see the roadmap row on
   // SPF checking) - so Neutral is exactly what an unreachable zone produces. The
   // assertions therefore run when the policy is actually reachable, and the test
   // stands down when it is not, rather than failing the entire self-test suite
   // for a reason that has nothing to do with this server.
   //
   // The two bare `throw;` statements are gone as well. Outside a catch block
   // that is a rethrow with no active exception, which calls std::terminate - so
   // even a genuine SPF regression was reported as a crash rather than as the
   // assertion failure it is. The rest of this suite uses `throw 0`.
   //---------------------------------------------------------------------------()
   {
      String sExplanation;

      const SPF::Result permittedSender =
         SPF::Instance()->Test("185.216.75.37", "example@hmailserver.com", "mail.hmailserver.com", sExplanation);

      if (permittedSender == SPF::Neutral)
      {
         // No policy came back. Nothing here is testable without one.
         OutputDebugString(_T("hMailServer: SPF self-test stood down - the policy for hmailserver.com could not be resolved.\n"));
         return;
      }

      if (permittedSender != SPF::Pass)
      {
         // A published policy that does not permit its own sender: a real failure.
         throw 0;
      }

      if (SPF::Instance()->Test("1.2.3.4", "example@hmailserver.com", "mail.hmailserver.com", sExplanation) != SPF::Fail)
      {
         // Should not be allowed.
         throw 0;
      }

      TestVoidLookupLimit_();
   }

   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // RFC 7208 4.6.4, the void lookup limit, pinned at its boundary.
   //
   // The existing limit of ten DNS-querying terms bounds what a policy can demand.
   // It does not bound what a policy can WASTE, and those are different numbers:
   // ten terms naming hosts that do not exist cost a full resolution each, for a
   // record that cannot match anything. Two is the RFC's answer.
   //
   // Driven through a supplied policy rather than a published one, because the SPF
   // library resolves through DnsQuery and not through this server's resolver - so
   // the suite's DNS server cannot be placed in front of it, and the policy is the
   // only part of the evaluation a test can control. The mechanism targets are
   // resolved for real against .invalid, which RFC 2606 reserves precisely so that
   // it never resolves.
   //
   // Both sides of the boundary are asserted. Only checking that three voids fail
   // would pass on an implementation that rejects the FIRST one, which would turn
   // every policy with one decommissioned host into a permerror and reject mail
   // that every other receiver accepts.
   //---------------------------------------------------------------------------()
   void SPFTester::TestVoidLookupLimit_()
   {
      // Does .invalid actually not resolve here? A resolver that answers for
      // everything - NXDOMAIN hijacking, still sold as a feature - would make these
      // lookups non-void, and every assertion below would be about nothing. 'exists'
      // matches on any A record at all, so this probe distinguishes "does not exist"
      // from "answered anyway", which is the one thing the vectors cannot do for
      // themselves.
      if (SPF::Instance()->EvaluatePolicy("1.2.3.4", "test@spf-void.invalid", "spf-void.invalid",
                                          "v=spf1 exists:probe.spf-void.invalid -all") != SPF_Fail)
      {
         OutputDebugString(_T("hMailServer: SPF void-lookup self-test stood down - this resolver does not return NXDOMAIN for .invalid.\n"));
         return;
      }

      // The SAME name in every term, deliberately. Three different names meant three
      // resolutions, and the first run of this test found the third one returning a
      // temporary failure while the first two were clean NXDOMAINs - so the vector
      // measured the resolver's mood rather than this code. One name is resolved
      // once, negatively cached, and answered from that cache for every term after,
      // which leaves the term COUNT as the only thing that varies. RFC 7208 4.6.4
      // counts terms, not queries, so repeating a term is exactly the right shape.
      const String voidTerm = "exists:probe.spf-void.invalid ";

      // Two voids is within the limit, so evaluation reaches the ip4 term and passes.
      // This is the assertion that stops the limit being made stricter than the RFC:
      // a policy naming one host that was decommissioned years ago is ordinary, and
      // rejecting it would refuse mail every other receiver accepts.
      AssertPolicyResult_("v=spf1 " + voidTerm + voidTerm + "ip4:1.2.3.4 -all",
                          SPF_Pass, "two void lookups are within the limit");

      // Three is one too many, and the whole evaluation is a permerror - not a fail,
      // and not a silent skip of the offending term while the rest of the policy
      // carries on and passes.
      AssertPolicyResult_("v=spf1 " + voidTerm + voidTerm + voidTerm + "ip4:1.2.3.4 -all",
                          SPF_PermError, "three void lookups exceed the limit");
   }

   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Evaluates one policy and throws unless it produced the expected result, naming
   // the policy and both results first. A bare throw here would report only that
   // "the class tests failed", and the whole point of a vector is to say WHICH one
   // and by how much - a permerror where a pass was expected and a fail where a
   // permerror was expected are different defects with different causes.
   //---------------------------------------------------------------------------()
   void SPFTester::AssertPolicyResult_(const String &policy, int expected, const String &what)
   {
      int actual = SPF::Instance()->EvaluatePolicy("1.2.3.4", "test@spf-void.invalid",
                                                   "spf-void.invalid", policy);

      if (actual == expected)
         return;

      // A temporary DNS failure is not a verdict about this code. The probe above
      // has already shown the name answers NXDOMAIN, so a temperror here means the
      // resolver stopped cooperating part-way through - and failing the whole
      // self-test suite for that would be the same mistake the SPF test above this
      // one was already fixed for.
      if (actual == SPF_TempError)
      {
         OutputDebugString(_T("hMailServer: SPF void-lookup self-test stood down - the resolver returned a temporary failure.\n"));
         return;
      }

      String message;
      message.Format(_T("SPF self-test: %s. Policy '%s' produced result %d, expected %d."),
                     what.c_str(), policy.c_str(), actual, expected);

      ErrorManager::Instance()->ReportError(ErrorManager::High, 4404, "SPFTester::AssertPolicyResult_", message);

      throw 0;
   }


}