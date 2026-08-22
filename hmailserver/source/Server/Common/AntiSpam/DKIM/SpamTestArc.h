// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// ARC results used for inbound filtering (RFC 8617).
//
// A mailing list or forwarder breaks SPF (the envelope sender changes) and
// often breaks DKIM (the body is modified), so legitimate forwarded mail
// fails DMARC here even though it authenticated cleanly where the forwarder
// received it. A valid ARC chain from a sealer the ADMINISTRATOR trusts lets
// this test recover the authentication result recorded at the first ARC hop
// and offset the DMARC failure score that the forwarding itself caused.
//
// The one thing this test must never become is a way to launder mail:
//
//  - A syntactically perfect, cryptographically valid chain proves only that
//    the domains named in it sealed it - anyone can fabricate an entire chain
//    with their own keys and it validates flawlessly. RFC 8617 (section 7.1)
//    is explicit that a passing chain conveys no trust by itself. So the test
//    is inert unless the administrator has both enabled it and listed the
//    sealer domains whose chains are to be honoured, and every sealer in the
//    chain must be on that list.
//
//  - The only score it ever produces is a negative offset of exactly the
//    DMARC failure score, and only when this test's own DMARC evaluation says
//    that penalty is about to be applied. The net effect of DMARC-plus-ARC is
//    therefore at best zero - the same as a message that passed DMARC - and
//    every other spam test (DNSBL, SURBL, SpamAssassin, ...) still scores the
//    message untouched. A trusted chain can un-punish forwarding damage; it
//    cannot vouch a message below the score it would earn on its content.
//
//  - Every failure mode - cv=fail, a chain that does not validate, a DNS
//    temperror, an untrusted or unlisted sealer, a malformed chain - produces
//    the same outcome: no result at all, exactly as if the message carried no
//    chain. Nothing here is ever more permissive than having no chain.
//
// REGISTRATION ORDER MATTERS: this test must run BEFORE SpamTestDMARC (see
// SpamTestRunner::LoadSpamTests). The runner stops running tests as soon as
// the accumulated score reaches the mark/delete threshold, and the DMARC
// failure score alone typically reaches it - so an offset registered after
// DMARC would be skipped in exactly the case it exists for. Running first,
// the offset lands before the penalty and the pair cancels.

#pragma once

#include "../SpamTest.h"

namespace HM
{
   class SpamTestArc : public SpamTest
   {
   public:

      virtual SpamTestType GetTestType()
      {
         return SpamTest::PostTransmission;
      }

      virtual String GetName() const;
      virtual bool GetIsEnabled();
      virtual std::set<std::shared_ptr<SpamTestResult> > RunTest(std::shared_ptr<SpamTestData> pTestData);

      static String GetTestName();

   private:

   };

}
