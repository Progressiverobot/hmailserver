// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "Collection.h"

#include "../Persistence/PersistentRemoteDomainPolicy.h"
#include "RemoteDomainPolicy.h"

namespace HM
{
   class RemoteDomainPolicies : public Collection<RemoteDomainPolicy, PersistentRemoteDomainPolicy>
   {
   public:
      RemoteDomainPolicies();
      virtual ~RemoteDomainPolicies();

      // Reloads every policy from the database.
      void Refresh();

      // The policy that governs a named remote domain, or an empty pointer.
      //
      // MOST SPECIFIC WINS, which is where this parts company with
      // Routes::GetItemByNameWithWildcardMatch - that one answers the first row
      // whose pattern matches, in whatever order the database returned them, so
      // a "*" row would shadow every named one depending on the sort. A security
      // policy cannot be decided by row order: an administrator who writes "*"
      // meaning "everyone else" and "bank.example" meaning "and this one
      // strictly" must get the strict record for the bank.
      //
      // So: an exact name beats any pattern; between two patterns the longer one
      // (more literal characters) wins; between two equally specific ones the
      // lower id wins, so the answer is stable rather than incidental. Inactive
      // rows take part in nothing - an inactive policy is one the administrator
      // has switched off, not one that falls through to a broader row, because
      // falling through would silently apply somebody else's limits.
      std::shared_ptr<RemoteDomainPolicy> GetPolicyForDomain(const String &domainName) const;

      // The strictest policy over several recipient domains.
      //
      // Delivery batches recipients by the server they go to, and
      // ServerTargetResolver merges the batches of different domains that share
      // a relay target - so one connection can carry two domains with two
      // policies. This composes them into one decision: the highest TLS
      // requirement, the smallest non-zero size and rate ceilings, the smallest
      // non-zero connection ceiling. The result is a synthetic record and is not
      // in the collection; its name is the domains it was made from, so the
      // deferral text can say which rule refused.
      //
      // A caller that has one domain gets that domain's own record back.
      std::shared_ptr<RemoteDomainPolicy> GetStrictestPolicy(const std::vector<String> &domainNames) const;

      // Exchange's two remote-domain switches, asked of one address.
      //
      // Static, and they find the running collection themselves, because the two
      // callers are the automatic-reply composer and the forwarder - neither of
      // which has any other reason to know the SMTP configuration exists, and
      // both of which are asking the same one-line question. True when no policy
      // governs the address's domain, which is every installation that has not
      // written one.
      static bool AutomaticRepliesPermitted(const String &address);
      static bool ForwardingPermitted(const String &address);

   protected:

      virtual String GetCollectionName() const { return "RemoteDomainPolicies"; }
   };
}
