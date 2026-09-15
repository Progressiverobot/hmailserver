// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "RemoteDomainPolicies.h"

#include "../Util/Parsing/StringParser.h"
#include "../../SMTP/SMTPConfiguration.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // How specific a pattern is. An exact name - no wildcard character at all -
      // is above every pattern whatever its length, so "bank.example" beats
      // "*.example" even though the pattern is shorter by a character. Among
      // patterns, the one with more literal characters wins.
      int PatternSpecificity_(const String &pattern)
      {
         bool hasWildcard = pattern.Find(_T("*")) >= 0 || pattern.Find(_T("?")) >= 0;

         int literals = 0;
         for (int i = 0; i < pattern.GetLength(); i++)
         {
            TCHAR character = pattern.GetAt(i);
            if (character != _T('*') && character != _T('?'))
               literals++;
         }

         return hasWildcard ? literals : 100000 + literals;
      }

      // The smaller of two ceilings where 0 means "no ceiling", so the stricter
      // of the two always wins and a zero never relaxes a real limit.
      long TighterCeiling_(long left, long right)
      {
         if (left == 0)
            return right;
         if (right == 0)
            return left;

         return left < right ? left : right;
      }
   }

   RemoteDomainPolicies::RemoteDomainPolicies()
   {

   }

   RemoteDomainPolicies::~RemoteDomainPolicies()
   {

   }

   void
   RemoteDomainPolicies::Refresh()
   {
      String sql = "select * from hm_remotedomainpolicies order by policydomainname asc";
      DBLoad_(sql);
   }

   std::shared_ptr<RemoteDomainPolicy>
   RemoteDomainPolicies::GetPolicyForDomain(const String &domainName) const
   {
      std::shared_ptr<RemoteDomainPolicy> best;
      int bestSpecificity = -1;

      if (domainName.IsEmpty())
         return best;

      for (std::shared_ptr<RemoteDomainPolicy> policy : GetSnapshot())
      {
         if (!policy || !policy->GetActive())
            continue;

         const String pattern = policy->GetDomainName();

         if (pattern.IsEmpty())
            continue;

         if (!StringParser::WildcardMatchNoCase(pattern, domainName))
            continue;

         int specificity = PatternSpecificity_(pattern);

         if (specificity > bestSpecificity ||
             (specificity == bestSpecificity && best && policy->GetID() < best->GetID()))
         {
            best = policy;
            bestSpecificity = specificity;
         }
      }

      return best;
   }

   std::shared_ptr<RemoteDomainPolicy>
   RemoteDomainPolicies::GetStrictestPolicy(const std::vector<String> &domainNames) const
   {
      std::shared_ptr<RemoteDomainPolicy> combined;
      int matched = 0;
      String names;

      for (const String &domainName : domainNames)
      {
         std::shared_ptr<RemoteDomainPolicy> policy = GetPolicyForDomain(domainName);

         if (!policy)
            continue;

         matched++;

         if (matched == 1)
         {
            // One match is the common case by a wide margin, and returning the
            // record itself rather than a copy means the caller's log line names
            // the row an administrator can find.
            combined = policy;
            names = policy->GetDomainName();
            continue;
         }

         if (matched == 2)
         {
            // A second match: from here on the answer is a composition, so the
            // first record is copied out of the collection before anything is
            // written into it.
            std::shared_ptr<RemoteDomainPolicy> first = combined;

            combined = std::shared_ptr<RemoteDomainPolicy>(new RemoteDomainPolicy());
            combined->SetActive(true);
            combined->SetOutboundTlsRequirement(first->GetOutboundTlsRequirement());
            combined->SetRequireInboundTls(first->GetRequireInboundTls());
            combined->SetMaxMessageSizeKB(first->GetMaxMessageSizeKB());
            combined->SetMaxConnections(first->GetMaxConnections());
            combined->SetMaxMessagesPerMinute(first->GetMaxMessagesPerMinute());
            combined->SetAllowAutomaticReplies(first->GetAllowAutomaticReplies());
            combined->SetAllowForwarding(first->GetAllowForwarding());

            // The callout is a per-recipient decision made at RCPT TO, where
            // exactly one domain is in hand; it is deliberately not composed
            // here, and the combined record carries none.
            combined->SetCalloutEnabled(false);
         }

         if (policy->GetOutboundTlsRequirement() > combined->GetOutboundTlsRequirement())
            combined->SetOutboundTlsRequirement(policy->GetOutboundTlsRequirement());

         if (policy->GetRequireInboundTls())
            combined->SetRequireInboundTls(true);

         combined->SetMaxMessageSizeKB(TighterCeiling_(combined->GetMaxMessageSizeKB(), policy->GetMaxMessageSizeKB()));
         combined->SetMaxConnections(TighterCeiling_(combined->GetMaxConnections(), policy->GetMaxConnections()));
         combined->SetMaxMessagesPerMinute(TighterCeiling_(combined->GetMaxMessagesPerMinute(), policy->GetMaxMessagesPerMinute()));

         if (!policy->GetAllowAutomaticReplies())
            combined->SetAllowAutomaticReplies(false);

         if (!policy->GetAllowForwarding())
            combined->SetAllowForwarding(false);

         names += _T(", ") + policy->GetDomainName();
      }

      if (matched > 1)
         combined->SetDomainName(names);

      return combined;
   }

   bool
   RemoteDomainPolicies::AutomaticRepliesPermitted(const String &address)
   {
      std::shared_ptr<RemoteDomainPolicies> policies =
         Configuration::Instance()->GetSMTPConfiguration()->GetRemoteDomainPolicies();

      if (!policies)
         return true;

      std::shared_ptr<RemoteDomainPolicy> policy =
         policies->GetPolicyForDomain(StringParser::ExtractDomain(address).ToLower());

      return !policy || policy->GetAllowAutomaticReplies();
   }

   bool
   RemoteDomainPolicies::ForwardingPermitted(const String &address)
   {
      std::shared_ptr<RemoteDomainPolicies> policies =
         Configuration::Instance()->GetSMTPConfiguration()->GetRemoteDomainPolicies();

      if (!policies)
         return true;

      std::shared_ptr<RemoteDomainPolicy> policy =
         policies->GetPolicyForDomain(StringParser::ExtractDomain(address).ToLower());

      return !policy || policy->GetAllowForwarding();
   }
}
