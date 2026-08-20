// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#pragma once

#include "SpamTest.h"

namespace HM
{
   // Puts an external filtering engine - rspamd, most likely - in the path of every
   // message this server accepts.
   //
   // There has never been a way to do that natively. The custom virus scanner runs a
   // process per message and answers only "infected or not"; SpamAssassin has its
   // own protocol and its own daemon; an event script can be given the message but
   // pays a script engine per message and cannot return a score. What was missing
   // was the ordinary thing every other MTA has: hand the message to something over
   // the network and use what it says.
   //
   // Milter was the obvious candidate and was ruled out deliberately. It is a binary
   // protocol with a session state machine, and this server already has an HTTP
   // client, HTTP listeners and a JSON dialect - so the same integration costs a
   // fraction as much over HTTP and can be debugged with curl.
   //
   // The verdict arrives as a SCORE, which is what makes it compose. It lands
   // alongside SPF, DKIM, DMARC and the rest, and the administrator's existing
   // thresholds decide what happens - rather than a second, parallel notion of
   // "spam" that has to be reconciled with the first. An engine that wants a message
   // gone says so with a score, and FilterHookRejectScore is what "reject" is worth.
   class SpamTestFilterHook : public SpamTest
   {
   public:

      virtual String GetName() const;
      virtual bool GetIsEnabled();
      virtual SpamTestType GetTestType();
      virtual std::set<std::shared_ptr<SpamTestResult> > RunTest(std::shared_ptr<SpamTestData> pTestData);
   };
}
