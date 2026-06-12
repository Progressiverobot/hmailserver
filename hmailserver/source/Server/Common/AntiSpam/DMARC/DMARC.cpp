// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"

#include "DMARC.h"

#include "../../TCPIP/DNSResolver.h"
#include "../../Util/Parsing/StringParser.h"

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
         L"com.vn", L"net.vn", L"org.vn"
      };
   }

   DMARC::DMARC()
   {

   }

   String
   DMARC::ExtractAddressFromHeaderValue(const String &headerValue)
   {
      String value = headerValue;
      value = value.Trim();

      int startBracket = value.ReverseFind(_T("<"));
      if (startBracket >= 0)
      {
         int endBracket = value.Find(_T(">"), startBracket);
         if (endBracket > startBracket)
            return value.Mid(startBracket + 1, endBracket - startBracket - 1).Trim();
      }

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

      String lastTwo = labels[labels.size() - 2] + _T(".") + labels[labels.size() - 1];
      for (const wchar_t *suffix : multiLabelPublicSuffixes)
      {
         if (lastTwo.CompareNoCase(suffix) == 0)
         {
            suffixLabels = 2;
            break;
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
         inSample = (rand() % 100) < pct;

      if (policy.CompareNoCase(_T("reject")) == 0)
         return inSample ? FailReject : FailQuarantine;

      if (policy.CompareNoCase(_T("quarantine")) == 0)
         return inSample ? FailQuarantine : FailNone;

      if (policy.CompareNoCase(_T("none")) == 0)
         return FailNone;

      return PermError;
   }
}
