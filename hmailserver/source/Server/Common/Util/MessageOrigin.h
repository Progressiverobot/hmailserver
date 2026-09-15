// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// Whether a message came from outside this installation. One rule, in one
// place, because the answer decides what a reader is told about the sender and
// it must not drift between the callers that ask.

#pragma once

#include <functional>

namespace HM
{
   class Message;

   class MessageOrigin
   {
   public:

      // What the rule is decided from. Gathered from the message once and then
      // judged, so that the judgement itself is a function of values and can be
      // tested without a delivery, a database or a network.
      struct Facts
      {
         Facts() : authenticated_submission(false), from_trusted_relay(false) {}

         // The envelope sender - MAIL FROM. Empty on a bounce or a delivery
         // report, which RFC 5321 requires to carry a null return path.
         String envelope_sender;

         // The address in the RFC 5322 From field, which is what the reader
         // actually sees. Empty when the message carries no From.
         String header_from;

         // The session that handed us this message authenticated. Read from the
         // topmost Received header, which is the one THIS server wrote - see
         // ReceivedSaysAuthenticated.
         bool authenticated_submission;

         // The message was handed to us by an address in the incoming-relay
         // list: a machine the administrator has declared part of this
         // installation's own edge.
         bool from_trusted_relay;
      };

      // Answers whether an address belongs to this installation. Passed in
      // rather than called directly so the rule can be tested against a set the
      // test names, and so there is exactly one production implementation.
      typedef std::function<bool(const String &address)> HostedAddressTest;

      // THE RULE, and the only copy of it. A sender is outside when none of the
      // three things that make a sender ours is true: the session did not
      // authenticate, the message was not handed to us by a trusted incoming
      // relay, and neither address the message names is at a domain this server
      // hosts.
      //
      // Either named address being outside is enough, so a message whose
      // envelope claims a hosted domain while its visible From does not is
      // outside - the disagreement is resolved in the direction that warns.
      //
      // reason is a short English phrase naming which test decided it, for the
      // log. It is set whichever way the answer goes.
      static bool IsExternalSender(const Facts &facts, const HostedAddressTest &isHosted, String &reason);

      // The same rule against this server's own domains, accounts and aliases.
      static bool IsExternalSender(const Facts &facts, String &reason);

      // Gathers the facts from a message that has arrived. header is the
      // message's header block as stored; sendersIP is the address the delivery
      // path already determined (MessageUtilities::GetSendersIP), which is the
      // peer of the session rather than an originator further back in the
      // Received chain.
      static Facts ReadFacts(std::shared_ptr<Message> message, const AnsiString &header, const String &sendersIP);

      // True when the topmost Received header records an authenticated session:
      // RFC 3848's ESMTPA or ESMTPSA in the "with" clause. That header is the
      // one this server wrote - SMTPMessageHeaderCreator prepends it before the
      // file is written, so it sits above anything the sender sent - and the
      // check requires it to name this server in its "by" clause, so a Received
      // line a sender forged cannot answer for us.
      //
      // X-AuthUser is deliberately NOT consulted. The header creator adds it
      // only when the field is absent, so a sender who supplies their own keeps
      // it: it is a claim, not a fact.
      static bool ReceivedSaysAuthenticated(const AnsiString &header, const String &thisServerName);

      // The unfolded value of the first field with this name in a header block,
      // or an empty string. The FIRST is deliberate: the topmost Received and
      // the topmost From are the ones this server can vouch for.
      static AnsiString FirstFieldValue(const AnsiString &header, const AnsiString &fieldName);

      // True when the address is at a domain this server hosts - directly, as a
      // domain alias, or because the address itself is an account or an active
      // alias.
      static bool IsHostedAddress(const String &address);
   };

   // Run from ClassTester::DoTests, which the regression suite reaches through
   // Utilities.RunTestSuite (Infrastructure.MainOperations.TestInternals).
   class MessageOriginTester
   {
   public:
      void Test();

   private:
      void TestTheRule_();
      void TestReceivedParsing_();
      void TestFieldReading_();
   };
}
