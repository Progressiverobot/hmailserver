// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "../Common/Util/VariantDateTime.h"

namespace HM
{
   class IMAPConnection;
   class Message;

   // RFC 5256 THREAD: arranges the messages a search matched into conversation
   // trees and renders the parenthesised response body.
   //
   // Two algorithms, because the RFC requires ORDEREDSUBJECT of anyone advertising
   // THREAD at all and clients differ in what they ask for. REFERENCES is the one
   // that produces real conversation trees - it links messages by the Message-ID /
   // References / In-Reply-To headers, the same chain every mail client writes -
   // and ORDEREDSUBJECT is the cheap approximation that groups by subject and
   // pretends each group is a chain.
   //
   // THREAD is deliberately NOT gated behind a setting, unlike SORT. The honest
   // reasons, recorded because the asymmetry looks like an oversight: the work is
   // bounded by the same per-command ceilings as SEARCH and SORT (the caller
   // re-checks them after the tree is built), a new setting would need seeded
   // property rows in three create scripts plus upgrade scripts plus a schema bump
   // - a missing row does not default quietly, it reports HM5015 on every read -
   // and SEARCH itself, which does strictly more file reading, has never been
   // gated. A switch that costs a schema migration and guards nothing the ceilings
   // do not already bound is not a switch worth shipping.
   class IMAPThread
   {
   public:
      enum Algorithm
      {
         AlgorithmOrderedSubject = 0,
         AlgorithmReferences = 1
      };

      // Maps the atom the client sent onto an algorithm. False for a name this
      // server does not implement, which the caller must answer with BAD - RFC
      // 5256 is explicit that an unadvertised algorithm is a protocol error, not
      // something to silently substitute a different algorithm for.
      static bool ParseAlgorithm(const String &name, Algorithm &algorithm);

      // Builds the response body - the "(2)(3 6 (4 23)(44 7 96))" part, with its
      // leading space, or an empty string when nothing matched.
      //
      // useUid picks what the numbers in the tree are: message sequence numbers
      // for THREAD, UIDs for UID THREAD.
      //
      // abortRequested is asked before each message's header is read, and the pass
      // stops the moment it answers true - the return is then false and the body is
      // meaningless. It exists because this reads one header per matched message
      // and the caller's ceilings live in the caller: checking them only after the
      // pass returns cannot stop the pass, it can only disown the result once the
      // connection thread has already spent minutes producing it. Pass an
      // always-false predicate for an unbounded build.
      bool BuildThreads(std::shared_ptr<IMAPConnection> pConnection,
                        const std::vector<std::pair<int, std::shared_ptr<Message> > > &vecMessages,
                        bool useUid, Algorithm algorithm,
                        const std::function<bool()> &abortRequested,
                        String &responseBody);
   };
}
