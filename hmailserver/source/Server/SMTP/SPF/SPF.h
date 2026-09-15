// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "SPFResult.h"

namespace HM
{
   class SPF : public Singleton<SPF>
   {
   public:
      SPF(void);
      ~SPF(void);

      // The results of RFC 7208 section 2.6, which SPFResult spells. Until the
      // evaluator was replaced this was three values - Neutral, Fail and Pass - and
      // everything else the old library reported was collapsed onto Neutral, so a
      // domain that publishes no policy and a domain whose policy could not be read
      // were reported as the same thing. RFC 8601 needs them apart.
      typedef SPFResult Result;

      // check_host() of RFC 7208 section 4, over real DNS. The explanation is the
      // text of the record's exp modifier, expanded, and is set only for a Fail
      // which the record explained.
      Result Test(const String &sSenderIP, const String &sSenderEmail, const String &sHeloHost, String &sExplanation);

      // The domain a check is made against, RFC 7208 section 4.3: the sender's domain,
      // or the HELO argument where the sender has none, section 2.4. Public because
      // Authentication-Results names it and the two must not be able to disagree.
      static String GetCheckedDomain(const String &senderEmail, const String &heloHost);

   private:

   };

   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // The SPF module's own tests, run from ClassTester::DoTests - which the
   // regression suite reaches through Utilities.RunTestSuite - and from the
   // spf-tests executable the CMake build makes for Linux.
   //
   // Four testers: the openspf.org conformance suite for RFC 7208 (203 cases, all
   // of them, with the count asserted), and the three that cover what the corpus
   // does not - the grammar rule by rule, macro expansion rule by rule, and the
   // corners of section 4 no case visits. None of them touches the network: each
   // answers the evaluation's DNS queries out of a table, which is what makes them
   // a test of this code rather than of somebody else's zone.
   //
   // That distinction is why the tester this replaces had to be repaired twice.
   // It evaluated a live policy for hmailserver.com; on 17 August 2026 that zone
   // began answering SERVFAIL and the self-test suite died on it, and the void
   // lookup limit could only be pinned by feeding the old library a policy by
   // hand, because it resolved through DnsQuery and no test could get in front of
   // it. Neither is possible here.
   //---------------------------------------------------------------------------()
   class SPFTester
   {
   public :
      SPFTester () {};
      ~SPFTester () {};

      void Test();

   private:

      // Reports every line the testers returned and then fails, rather than
      // failing at the first: a run should say everything that is wrong with it.
      void Report_(const AnsiString &what, const std::vector<AnsiString> &failures);
   };
}
