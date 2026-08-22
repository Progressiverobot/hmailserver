// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "SpamTest.h"

namespace HM
{
   // Checks the envelope sender (MAIL FROM) against the administrator's
   // blocked-senders list and, on a match, contributes the entry's score to
   // the message's total. With the entry at its default score that total
   // crosses SpamDeleteThreshold on its own, so the message is refused with
   // 550 5.7.1 where every other pre-transmission verdict is: at MAIL FROM
   // by default (DNSBLChecksAfterMailFrom defaults to 1 in hMailServer.ini),
   // or at the first RCPT TO when that setting is 0. Rejecting at MAIL FROM
   // confirms the listing to anyone probing addresses without their naming a
   // victim; deferring to RCPT reveals less per probe but accepts more of
   // the conversation first. That trade-off already has a knob, and this
   // test follows it rather than growing a second one.
   //
   // Honesty note: the envelope sender is chosen by the connecting client.
   // A blacklist on it stops mail from senders who keep using the same
   // address - unsubscribable newsletters, a persistent harasser, a
   // misbehaving relay - and stops nothing that rotates addresses. It is a
   // nuisance filter, not an authentication mechanism, which is also why a
   // match is expressed as a spam score rather than as a separate refusal
   // path: an administrator who prefers quarantine-or-mark over refusal
   // lowers the entry's score below SpamDeleteThreshold.
   class SpamTestSenderBlacklist : public SpamTest
   {
   public:

      virtual SpamTestType GetTestType()
      {
         return SpamTest::PreTransmission;
      }

      virtual String GetName() const;
      virtual bool GetIsEnabled();

      virtual std::set<std::shared_ptr<SpamTestResult> > RunTest(std::shared_ptr<SpamTestData> pTestData);

   private:

   };
}
