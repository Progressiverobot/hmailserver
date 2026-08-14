// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"

#include "DMARC.h"

#include "../../TCPIP/DNSResolver.h"
#include "../../Util/Parsing/StringParser.h"
#include "../../Util/Parsing/AddresslistParser.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // Common multi-label public suffixes. Used by the organizational-domain
      // heuristic. This is intentionally a compact subset of the Public Suffix
      // List covering the most frequently seen registry suffixes.
      const wchar_t *multiLabelPublicSuffixes[] =
      {
         L"co.uk", L"org.uk", L"me.uk", L"ltd.uk", L"plc.uk", L"net.uk", L"sch.uk", L"ac.uk", L"gov.uk", L"nhs.uk",
         L"com.au", L"net.au", L"org.au", L"edu.au", L"gov.au", L"id.au", L"asn.au",
         L"co.nz", L"net.nz", L"org.nz", L"govt.nz", L"ac.nz",
         L"co.jp", L"ne.jp", L"or.jp", L"ac.jp", L"go.jp",
         L"com.br", L"net.br", L"org.br", L"gov.br",
         L"com.cn", L"net.cn", L"org.cn", L"gov.cn",
         L"com.mx", L"com.ar", L"com.tr", L"com.tw", L"com.hk", L"com.sg", L"com.my", L"com.ph",
         L"co.in", L"net.in", L"org.in", L"firm.in", L"gen.in", L"ind.in",
         L"co.za", L"net.za", L"org.za", L"web.za",
         L"co.kr", L"or.kr", L"ne.kr", L"re.kr", L"go.kr",
         L"com.es", L"org.es", L"nom.es",
         L"com.pl", L"net.pl", L"org.pl",
         L"com.ru", L"net.ru", L"org.ru",
         L"co.il", L"org.il", L"net.il", L"ac.il", L"gov.il",
         L"com.ua", L"net.ua", L"org.ua",
         L"com.co", L"net.co", L"org.co",
         L"com.vn", L"net.vn", L"org.vn",

         // Second-level labels that are NOT generic registry words, so the shape rule
         // below cannot infer them. Added after a review found the shape rule still
         // collapsed these to the bare suffix.
         L"my.id", L"biz.id", L"desa.id", L"ponpes.id",
         L"gr.jp", L"ad.jp", L"ed.jp", L"lg.jp",
         L"sc.ke", L"me.ke", L"mi.th",
         L"art.br", L"eco.br", L"emp.br", L"eng.br", L"blog.br", L"med.br", L"adv.br",
         L"esp.br", L"etc.br", L"ind.br", L"inf.br", L"jor.br", L"rec.br", L"srv.br",
         L"tur.br", L"vet.br", L"wiki.br"
      };

      // Two-letter TLDs that are FLAT - anybody may register directly beneath them, so
      // a two-label name under one of these is a registrant, not a public suffix.
      //
      // This list exists to bound the shape rule below, and it was added because that
      // rule without it was a REGRESSION rather than a fix. "co", "gen", "web", "info",
      // "in", "pro" and friends are perfectly ordinary company names, and they are
      // registered directly under .io/.ai/.me every day. Treating co.io as a public
      // suffix makes GetOrganizationalDomain("mail.co.io") return "mail.co.io" while
      // GetOrganizationalDomain("co.io") returns "co.io", so relaxed alignment compares
      // two different strings and correctly-signed mail from a real company FAILS
      // DMARC. Rejecting legitimate mail is a worse outcome than the forgery the shape
      // rule was added to prevent, so the rule is not applied here.
      const wchar_t *flatTwoLetterRegistries[] =
      {
         L"io", L"ai", L"me", L"cc", L"tv", L"sh", L"fm", L"gg", L"im",
         L"la", L"to", L"ly", L"st", L"is", L"ms", L"nu", L"tk", L"ws",
         L"vc", L"gs", L"mn", L"si", L"su", L"ee", L"lv", L"lt", L"sk",
         L"cz", L"be", L"nl", L"de", L"fr", L"it", L"ch", L"at", L"dk",
         L"se", L"no", L"fi", L"pl", L"pt", L"gr", L"ie", L"lu", L"ca",
         L"us", L"eu", L"cl", L"uy", L"ec", L"bo", L"py"
      };

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
      String lowerDomain = domain;
      lowerDomain.MakeLower();
      lowerDomain.TrimRight(_T("."));

      std::vector<String> labels = StringParser::SplitString(lowerDomain, ".");
      if (labels.size() <= 2)
         return lowerDomain;

      // Determine the number of labels making up the public suffix.
      size_t suffixLabels = 1;

      String secondLevel = labels[labels.size() - 2];
      String topLevel = labels[labels.size() - 1];

      String lastTwo = secondLevel + _T(".") + topLevel;
      for (const wchar_t *suffix : multiLabelPublicSuffixes)
      {
         if (lastTwo.CompareNoCase(suffix) == 0)
         {
            suffixLabels = 2;
            break;
         }
      }

      // The table above is a subset of the Public Suffix List, and a subset used on
      // its own is a DMARC bypass rather than merely an approximation.
      //
      // com.pt, co.id, co.th, com.ng, co.ke and com.pe were among the suffixes not in
      // it. For any of those, this returned the *public suffix itself* as the
      // organizational domain - GetOrganizationalDomain("bank.com.pt") gave "com.pt".
      // Relaxed alignment is the DMARC default for both aspf and adkim, so
      // DomainsAligned_ then compared "com.pt" with "com.pt" and passed: register
      // attacker.com.pt, publish "v=spf1 +all", send MAIL FROM there with
      // From: ceo@bank.com.pt, and bank.com.pt's p=reject was bypassed. Every domain
      // under a suffix missing from the table was forgeable by anyone holding any
      // other name under the same suffix.
      //
      // Completing the table is not the fix - the PSL has ~9,000 rules and changes
      // weekly, so the next missing entry is the next bypass. This covers the shape
      // instead: a registry label directly under a two-letter ccTLD. That is what
      // essentially every ccTLD of this kind looks like, including all of the ones the
      // table already lists, and it needs no maintenance.
      //
      // It errs towards treating a name as a public suffix, which makes alignment
      // stricter rather than looser - the safe direction. The cost is a registrant who
      // literally holds "co.<cc>" as their own name seeing relaxed alignment behave
      // strictly for their subdomains; the alternative is the bypass above.
      bool topLevelIsFlat = false;
      for (const wchar_t *flat : flatTwoLetterRegistries)
      {
         if (topLevel.CompareNoCase(flat) == 0)
         {
            topLevelIsFlat = true;
            break;
         }
      }

      if (suffixLabels == 1 && topLevel.GetLength() == 2 && !topLevelIsFlat)
      {
         static const wchar_t *registryLabels[] =
         {
            L"com", L"net", L"org", L"edu", L"gov", L"mil", L"int",
            L"co", L"ac", L"or", L"ne", L"go", L"re", L"id", L"in",
            L"biz", L"info", L"name", L"nom", L"web", L"firm", L"gen", L"ind",
            L"sch", L"asn", L"govt", L"nhs", L"ltd", L"plc", L"priv", L"pro"
         };

         for (const wchar_t *label : registryLabels)
         {
            if (secondLevel.CompareNoCase(label) == 0)
            {
               suffixLabels = 2;
               break;
            }
         }
      }

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

   DMARC::Result
   DMARC::Verify(const String &fromHeaderDomain,
                 const String &envelopeFromDomain,
                 bool spfPassed,
                 const std::vector<AnsiString> &dkimPassingDomains)
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

      if (!RetrievePolicy_(fromDomain, policyRecord, dnsError))
      {
         if (dnsError)
            return TempError;

         String organizationalDomain = GetOrganizationalDomain(fromDomain);
         if (organizationalDomain == fromDomain)
            return NoPolicy;

         if (!RetrievePolicy_(organizationalDomain, policyRecord, dnsError))
            return dnsError ? TempError : NoPolicy;

         isSubdomainPolicy = true;
      }

      // Parse alignment modes. Default is relaxed for both.
      AlignmentMode spfAlignment = Relaxed;
      AlignmentMode dkimAlignment = Relaxed;

      String tagValue;
      if (ParseTagValue_(policyRecord, _T("aspf"), tagValue) && tagValue.CompareNoCase(_T("s")) == 0)
         spfAlignment = Strict;

      if (ParseTagValue_(policyRecord, _T("adkim"), tagValue) && tagValue.CompareNoCase(_T("s")) == 0)
         dkimAlignment = Strict;

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

      if (spfAligned || dkimAligned)
         return Pass;

      // The message failed DMARC. Determine the requested policy.
      String policy;
      if (isSubdomainPolicy)
      {
         // For subdomains the sp= tag takes precedence if present.
         if (!ParseTagValue_(policyRecord, _T("sp"), policy))
            ParseTagValue_(policyRecord, _T("p"), policy);
      }
      else
      {
         ParseTagValue_(policyRecord, _T("p"), policy);
      }

      if (policy.IsEmpty())
         return PermError;

      // Apply pct sampling (RFC 7489, section 6.6.4). Messages outside the
      // sample have the next-less-strict policy applied.
      int pct = 100;
      if (ParseTagValue_(policyRecord, _T("pct"), tagValue))
      {
         if (StringParser::IsNumeric(tagValue))
         {
            pct = _ttoi(tagValue.c_str());
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;
         }
      }

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
