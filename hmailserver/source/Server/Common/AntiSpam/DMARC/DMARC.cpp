// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "DMARC.h"
#include "PublicSuffixList.h"
#include "DmarcTreeWalk.h"
#include "../../Application/IniFileSettings.h"

#include "../../TCPIP/DNSResolver.h"
#include "../../Util/Parsing/StringParser.h"
#include "../../Util/Parsing/AddresslistParser.h"

#include <atomic>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // RFC 5322 3.2.2: a comment is a parenthesised run that may nest, may contain
      // quoted strings, and may appear anywhere between lexical tokens. It is NOT part
      // of any address - but AddresslistParser does not know that. Its
      // ExtractWithinGTLT_ tracks only '"' and '\' escapes and treats '(' and ')' as
      // ordinary characters, recording the LAST '<' before the FIRST '>'.
      //
      // So delegating to that parser alone did not close the bypass it was meant to:
      //
      //    From: ceo@bank.example (<attacker@evil.example>)
      //
      // still yielded attacker@evil.example, and
      //
      //    From: Bob (<evil@evil.example>) <ceo@bank.example>
      //
      // yielded evil@evil.example while ignoring the real angle-addr completely - a
      // header with a genuine address that every mail client renders as the bank.
      // Removing comments first is what makes the parser agree with the client.
      String StripComments_(const String &value)
      {
         String result;
         result.reserve(value.GetLength());

         int commentDepth = 0;
         bool insideQuotedString = false;

         for (int i = 0; i < value.GetLength(); i++)
         {
            wchar_t currentChar = value.GetAt(i);

            if (currentChar == '\\')
            {
               // Quoted-pair. The escaped character is literal wherever it appears, so
               // it is consumed here and never re-examined as a delimiter.
               if (commentDepth == 0)
               {
                  result += currentChar;

                  if (i + 1 < value.GetLength())
                     result += value.GetAt(i + 1);
               }

               i++;
               continue;
            }

            if (insideQuotedString)
            {
               if (currentChar == '"')
                  insideQuotedString = false;

               result += currentChar;
               continue;
            }

            if (commentDepth > 0)
            {
               // A '"' inside a comment is just ctext, so it does not open a quoted
               // string; only the nesting depth matters here.
               if (currentChar == '(')
                  commentDepth++;
               else if (currentChar == ')')
                  commentDepth--;

               continue;
            }

            if (currentChar == '"')
            {
               insideQuotedString = true;
               result += currentChar;
               continue;
            }

            if (currentChar == '(')
            {
               commentDepth++;
               continue;
            }

            result += currentChar;
         }

         return result;
      }
   }

   DMARC::DMARC()
   {

   }

   String
   DMARC::ExtractAddressFromHeaderValue(const String &headerValue)
   {
      String value = headerValue;
      value = value.Trim();

      // Parsed rather than scanned for angle brackets, and the difference is a DMARC
      // bypass.
      //
      // This used to take ReverseFind("<") - the LAST '<' anywhere in the value. RFC
      // 5322 allows comments in a From header and '<' and '>' are ordinary ctext, so
      //
      //    From: ceo@bank.example (<attacker@evil.example>)
      //
      // is a legal header that every RFC 5322 parser and every mail client reads as
      // ceo@bank.example, while this read attacker@evil.example. DMARC would then be
      // looked up at _dmarc.evil.example - a domain the sender controls, publishing
      // p=none - so bank.example's p=reject was never fetched and the message was
      // delivered while appearing to come from the bank.
      //
      // AddresslistParser is the tree's RFC 5322 address parser and DKIMSigner already
      // uses it on this same header for this same purpose, so the two now agree on what
      // the From address is - which matters more than either answer on its own.
      //
      // Comments are removed BEFORE parsing, because that parser does not understand
      // them and would otherwise pick an address out of one - see StripComments_.
      AddresslistParser parser;
      auto addresses = parser.ParseList(StripComments_(value));

      if (!addresses.empty() && !addresses[0]->sDomainName.IsEmpty())
         return addresses[0]->sMailboxName + "@" + addresses[0]->sDomainName;

      return value;
   }

   String
   DMARC::GetOrganizationalDomain(const String &domain)
   {
      DiscoveryMethod methodUsed = DiscoveryPublicSuffixList;
      return GetOrganizationalDomain(domain, methodUsed);
   }

   String
   DMARC::GetOrganizationalDomain(const String &domain, DiscoveryMethod &methodUsed)
   {
      // The configured mechanism stands until one of them actually answers
      // below. It is the right answer for the early return too: a name of one
      // label consults neither mechanism, and there is no third value to report.
      methodUsed = IniFileSettings::Instance()->GetDmarcTreeWalkEnabled()
         ? DiscoveryTreeWalk
         : DiscoveryPublicSuffixList;

      String lowerDomain = domain;
      lowerDomain.MakeLower();
      lowerDomain.TrimRight(_T("."));

      std::vector<String> labels = StringParser::SplitString(lowerDomain, ".");
      if (labels.size() <= 1)
         return lowerDomain;

      // RFC 9989 (DMARCbis, which obsoletes 7489) discovers this with a DNS tree
      // walk instead of the Public Suffix List. The difference is not cosmetic:
      // where a domain owner relies on tree-walk semantics, a PSL answer is a
      // WRONG POLICY DECISION rather than a soft failure - in both directions.
      //
      // The PSL below is kept, and is not dead code: DmarcTreeWalkEnabled=0 selects
      // it, which is the escape hatch for an operator who meets a domain the walk
      // handles worse than the list did during the transition. It is also still the
      // answer when the walk hits a transient DNS failure, because the alternative -
      // the RFC fallback of "the domain is its own organizational domain" - silently
      // turns relaxed alignment into strict for the duration of the outage.
      if (IniFileSettings::Instance()->GetDmarcTreeWalkEnabled())
      {
         bool treeWalkDnsError = false;
         String walked = DmarcTreeWalk::Instance()->GetOrganizationalDomain(lowerDomain, treeWalkDnsError);

         if (!treeWalkDnsError)
         {
            methodUsed = DiscoveryTreeWalk;
            return walked;
         }
      }

      // Either the walk is switched off or it could not complete, and the list
      // is what answered. Recorded here rather than inferred from the setting,
      // because on the transient-failure path the two disagree - and a report
      // that named the setting would be telling the domain owner about this
      // server's ini file instead of about their message.
      methodUsed = DiscoveryPublicSuffixList;

      // The public suffix comes from the real Public Suffix List, compiled in
      // (see PublicSuffixList.h and build\generate-public-suffix-list.ps1).
      //
      // This replaced a heuristic - a ~120-entry table of common multi-label
      // suffixes plus a shape rule for registry-looking labels under two-letter
      // ccTLDs - and the heuristic had to go because BOTH of its error
      // directions were live, and each one is a real failure on real mail:
      //
      //  - Too few suffix labels and the "organizational domain" of a name
      //    under a multi-label suffix collapses to the suffix itself:
      //    GetOrganizationalDomain("bank.my.id") gave "my.id", and so did
      //    "attacker.my.id" - relaxed alignment (the DMARC default for both
      //    aspf and adkim) then compares "my.id" with "my.id" and passes, so
      //    anyone holding any name under the suffix could forge any other
      //    name under it past a p=reject. The shape rule could never learn
      //    suffixes whose second label is not a registry word (my.id, gr.jp,
      //    ad.jp, sc.ke, art.br, ...), so each of those stayed a bypass.
      //
      //  - Too many suffix labels and relaxed alignment silently becomes
      //    strict for a legitimate registrant: the shape rule decided
      //    "gov.je" was a registry suffix (registry word under a two-letter
      //    ccTLD not on its exclusion list), so mail.gov.je was its own
      //    organizational domain, subdomain policy discovery never fell back
      //    to _dmarc.gov.je, and correctly-signed mail failed to align.
      //    Rejecting real mail is the worse direction of the two.
      //
      // The full list closes both directions at once because it is no longer
      // an approximation from either side: what it names is a suffix, what it
      // does not name is a registrant.
      size_t suffixLabels = PublicSuffixList::GetPublicSuffixLabelCount(labels);

      if (suffixLabels == 0)
      {
         // The compiled-in tables are empty. A correct build cannot produce
         // this - the generator refuses to emit a truncated header - so this
         // is a guard against a broken regeneration being committed, and it
         // must pick a failure direction:
         //
         // Falling back to a one-label suffix fails OPEN - alignment becomes
         // looser than intended and multi-label-suffix registrants become
         // mutually forgeable again, exactly the pre-hardening behaviour.
         // The alternative, treating the whole domain as its own
         // organizational domain, fails CLOSED - relaxed alignment silently
         // behaves as strict and correctly-signed subdomain mail from every
         // domain on the planet starts failing DMARC. Rejecting legitimate
         // mail wholesale is worse than weakening one check that SPF, DKIM
         // and content scoring still back up, so this fails open - and says
         // so once in the error log, because a security check that has
         // quietly degraded looks identical to one that is working.
         static std::atomic<bool> emptyTablesReported(false);
         if (!emptyTablesReported.exchange(true))
         {
            ErrorManager::Instance()->ReportError(ErrorManager::High, 6150, "DMARC::GetOrganizationalDomain",
               "The compiled-in Public Suffix List is empty, so organizational domains are computed from the "
               "top-level domain alone and DMARC relaxed alignment is weaker than intended. The build is broken: "
               "regenerate PublicSuffixListData.h with build\\generate-public-suffix-list.ps1 and rebuild.");
         }

         suffixLabels = 1;
      }

      // The organizational domain is the public suffix plus the single label
      // the registrant registered under it (RFC 7489 3.2). A domain that is
      // itself a public suffix - or shorter than one, as gr.jp is - has no
      // organizational domain distinct from itself and is returned unchanged;
      // DomainsAligned_ then compares it as-is, so a sender claiming the bare
      // suffix does not align with anything under it.
      size_t organizationalLabels = suffixLabels + 1;
      if (labels.size() <= organizationalLabels)
         return lowerDomain;

      String result;
      for (size_t i = labels.size() - organizationalLabels; i < labels.size(); i++)
      {
         if (!result.IsEmpty())
            result += _T(".");
         result += labels[i];
      }

      return result;
   }

   bool
   DMARC::RetrievePolicy_(const String &domain, String &policyRecord, bool &dnsError)
   {
      dnsError = false;

      String query = _T("_dmarc.") + domain;

      DNSResolver resolver;
      std::vector<String> txtRecords;
      if (!resolver.GetTXTRecords(query, txtRecords))
      {
         dnsError = true;
         return false;
      }

      for (String record : txtRecords)
      {
         String trimmed = record.Trim();
         if (trimmed.StartsWith(_T("v=DMARC1")))
         {
            policyRecord = trimmed;
            return true;
         }
      }

      return false;
   }

   bool
   DMARC::ParseTagValue_(const String &record, const String &tag, String &value)
   {
      // DMARC records are of the form: v=DMARC1; p=reject; sp=quarantine; adkim=s
      std::vector<String> parts = StringParser::SplitString(record, ";");
      for (String part : parts)
      {
         part = part.Trim();

         int equalsPos = part.Find(_T("="));
         if (equalsPos <= 0)
            continue;

         String partTag = part.Mid(0, equalsPos).Trim();
         if (partTag.CompareNoCase(tag.c_str()) == 0)
         {
            value = part.Mid(equalsPos + 1).Trim();
            return true;
         }
      }

      return false;
   }

   bool
   DMARC::DomainsAligned_(const String &authenticatedDomain, const String &fromDomain, AlignmentMode mode)
   {
      if (authenticatedDomain.IsEmpty() || fromDomain.IsEmpty())
         return false;

      String authLower = authenticatedDomain;
      authLower.MakeLower();
      authLower.TrimRight(_T("."));

      String fromLower = fromDomain;
      fromLower.MakeLower();
      fromLower.TrimRight(_T("."));

      if (mode == Strict)
         return authLower == fromLower;

      return GetOrganizationalDomain(authLower) == GetOrganizationalDomain(fromLower);
   }

   bool
   DMARC::SubdomainIsNonExistent_(const String &domain)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Whether np= applies to this From domain: does the name not exist in the DNS?
   //
   // Fails CLOSED against the resolver rather than against the sender. A lookup that
   // cannot be completed answers "it exists", so np= is not applied and the message
   // is judged by sp= or p= exactly as it would have been before np= was published.
   // The alternative - treating a resolver failure as non-existence - would let a
   // brief DNS outage apply a reject policy to every subdomain of every domain that
   // publishes np=, including the real ones.
   //---------------------------------------------------------------------------()
   {
      DNSResolver resolver;
      bool dnsError = false;

      bool exists = resolver.DomainExists(domain, dnsError);

      if (dnsError)
         return false;

      return !exists;
   }

   DMARC::Result
   DMARC::Verify(const String &fromHeaderDomain,
                 const String &envelopeFromDomain,
                 bool spfPassed,
                 const std::vector<AnsiString> &dkimPassingDomains,
                 Evaluation *evaluation)
   {
      if (fromHeaderDomain.IsEmpty())
         return PermError;

      String fromDomain = fromHeaderDomain;
      fromDomain.MakeLower();

      // Policy discovery (RFC 7489, section 6.6.3): query the From domain,
      // falling back to its organizational domain.
      String policyRecord;
      bool dnsError = false;
      bool isSubdomainPolicy = false;
      String policyDomain = fromDomain;

      // Which mechanism discovered the policy record, for RFC 9990's
      // policy_published/discovery_method. A record found at the From domain
      // itself is discovered by a single query for _dmarc.<from domain>, which
      // is step one of BOTH algorithms - so nothing distinguished them and the
      // one in force is the one that answered. Only the organizational-domain
      // fallback below can tell them apart, and it overwrites this.
      DiscoveryMethod discoveryMethod =
         IniFileSettings::Instance()->GetDmarcTreeWalkEnabled()
            ? DiscoveryTreeWalk
            : DiscoveryPublicSuffixList;

      if (!RetrievePolicy_(fromDomain, policyRecord, dnsError))
      {
         if (dnsError)
            return TempError;

         String organizationalDomain = GetOrganizationalDomain(fromDomain, discoveryMethod);
         if (organizationalDomain == fromDomain)
            return NoPolicy;

         if (!RetrievePolicy_(organizationalDomain, policyRecord, dnsError))
            return dnsError ? TempError : NoPolicy;

         isSubdomainPolicy = true;
         policyDomain = organizationalDomain;
      }

      // The published tags are parsed up front - including on the pass path,
      // which used to return before touching p= at all - because the aggregate
      // reporter's policy_published block needs them for every message, passes
      // included. The parse is the only thing moved; which tag governs a
      // failure, and the pct sampling, are applied further down exactly as
      // before.
      String aspfTag;
      ParseTagValue_(policyRecord, _T("aspf"), aspfTag);

      String adkimTag;
      ParseTagValue_(policyRecord, _T("adkim"), adkimTag);

      String pTag;
      ParseTagValue_(policyRecord, _T("p"), pTag);

      String spTag;
      ParseTagValue_(policyRecord, _T("sp"), spTag);

      // np= (RFC 9989 section 5.5.4): the policy for subdomains that do not exist.
      // New in DMARCbis, and the most targeted of the three: p= covers the domain,
      // sp= covers its subdomains, and np= covers the ones a phisher invented -
      // because inventing a subdomain of a real company costs nothing and the
      // company cannot publish a record for a name it has never heard of.
      String npTag;
      ParseTagValue_(policyRecord, _T("np"), npTag);

      // t= (RFC 9989 section 5.5.6): the domain owner is still testing and does not
      // want the policy enforced. Read for the RFC 9990 report only - policy
      // application below is deliberately unchanged, because honouring t= means
      // overriding a disposition and emitting a policy_test_mode override reason,
      // which is a separate decision from being able to REPORT what was published.
      String tTag;
      ParseTagValue_(policyRecord, _T("t"), tTag);

      int pct = 100;
      String tagValue;
      if (ParseTagValue_(policyRecord, _T("pct"), tagValue))
      {
         if (StringParser::IsNumeric(tagValue))
         {
            pct = _ttoi(tagValue.c_str());
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;
         }
      }

      // Parse alignment modes. Default is relaxed for both.
      AlignmentMode spfAlignment = aspfTag.CompareNoCase(_T("s")) == 0 ? Strict : Relaxed;
      AlignmentMode dkimAlignment = adkimTag.CompareNoCase(_T("s")) == 0 ? Strict : Relaxed;

      // Evaluate identifier alignment (RFC 7489, section 3.1).
      bool spfAligned = spfPassed && DomainsAligned_(envelopeFromDomain, fromDomain, spfAlignment);

      bool dkimAligned = false;
      for (AnsiString dkimDomain : dkimPassingDomains)
      {
         String unicodeDomain = dkimDomain;
         if (DomainsAligned_(unicodeDomain, fromDomain, dkimAlignment))
         {
            dkimAligned = true;
            break;
         }
      }

      if (evaluation)
      {
         evaluation->policy_found = true;
         evaluation->policy_domain = policyDomain;
         evaluation->adkim = adkimTag;
         evaluation->aspf = aspfTag;
         evaluation->p = pTag;
         evaluation->sp = spTag;
         evaluation->np = npTag;
         evaluation->t = tTag;
         evaluation->pct = pct;
         evaluation->discovery_method = discoveryMethod;
         evaluation->spf_aligned = spfAligned;
         evaluation->dkim_aligned = dkimAligned;
      }

      if (spfAligned || dkimAligned)
         return Pass;

      // The message failed DMARC. Determine the requested policy. For subdomains
      // that is sp= if published, and np= ahead of it when the subdomain does not
      // exist - the more specific statement wins, which is the same ordering the
      // tree walk's psd= rules use.
      String policy = pTag;

      if (isSubdomainPolicy)
      {
         if (!npTag.IsEmpty() && SubdomainIsNonExistent_(fromDomain))
            policy = npTag;
         else if (!spTag.IsEmpty())
            policy = spTag;
      }

      if (policy.IsEmpty())
         return PermError;

      // Apply pct sampling (RFC 7489, section 6.6.4). Messages outside the
      // sample have the next-less-strict policy applied.

      bool inSample = true;
      if (pct < 100)
      {
         // rand() was the wrong source here. The MSVC CRT keeps its state per
         // thread and seeds every thread with the same value, so the sequence of
         // sampling decisions restarted identically on each new connection thread
         // and after every service restart - which is to say a sender could work
         // out which of its messages escape a pct<100 policy. rand_s is already
         // used elsewhere in the server (ExternalDelivery) for the same reason.
         unsigned int randomValue = 0;
         if (rand_s(&randomValue) == 0)
            inSample = static_cast<int>(randomValue % 100) < pct;
         else
            inSample = true; // No randomness available: apply the published policy in full.
      }

      if (policy.CompareNoCase(_T("reject")) == 0)
         return inSample ? FailReject : FailQuarantine;

      if (policy.CompareNoCase(_T("quarantine")) == 0)
         return inSample ? FailQuarantine : FailNone;

      if (policy.CompareNoCase(_T("none")) == 0)
         return FailNone;

      return PermError;
   }
}
