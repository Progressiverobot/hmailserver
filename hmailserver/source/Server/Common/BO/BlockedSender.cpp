// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#include "StdAfx.h"
#include "BlockedSender.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   BlockedSender::BlockedSender(void) :
      // The default score is far above the shipped SpamDeleteThreshold (20),
      // so that an entry created without an explicit score does what the
      // administrator who created it meant: it blocks. An administrator who
      // wants a listed sender merely scored rather than refused lowers the
      // score on the entry.
      score_(100)
   {
   }

   BlockedSender::~BlockedSender(void)
   {
   }

   bool
   BlockedSender::Matches(const String &sFromAddress) const
   {
      // The null sender (<>) carries bounces and delivery status
      // notifications. It can never be listed.
      if (sFromAddress.IsEmpty())
         return false;

      String entry = address_;
      entry.Trim();
      entry.MakeLower();

      // "@example.com" is accepted as an alternative spelling of the
      // documented "example.com".
      if (entry.Left(1) == _T("@"))
         entry = entry.Mid(1);

      if (entry.IsEmpty())
         return false;

      String from = sFromAddress;
      from.Trim();
      from.MakeLower();

      if (entry.Find(_T("@")) >= 0)
      {
         // Full-address entry: exact match only. No wildcards, on purpose -
         // a deny list must be predictable, and "notspammer@example.com"
         // must never be caught by an entry for "spammer@example.com".
         return from == entry;
      }

      // Domain entry. Match the domain part of the envelope sender against
      // the entry, exactly or as a subdomain.
      int atPos = from.ReverseFind('@');
      if (atPos < 0)
         return false;

      String fromDomain = from.Mid(atPos + 1);

      if (fromDomain == entry)
         return true;

      // Subdomain: anchored at a label boundary, so "example.com" covers
      // "mail.example.com" but neither "notexample.com" nor
      // "example.com.attacker.net".
      String dottedSuffix = _T(".");
      dottedSuffix += entry;
      if (fromDomain.GetLength() > dottedSuffix.GetLength() &&
          fromDomain.Right(dottedSuffix.GetLength()) == dottedSuffix)
         return true;

      return false;
   }

   bool
   BlockedSender::XMLStore(XNode *pParentNode, int iOptions)
   {
      XNode *pNode = pParentNode->AppendChild(_T("BlockedSender"));

      pNode->AppendAttr(_T("Name"), GetName());
      pNode->AppendAttr(_T("Address"), address_);
      pNode->AppendAttr(_T("Score"), StringParser::IntToString(score_));
      pNode->AppendAttr(_T("Description"), description_);

      return true;
   }

   bool
   BlockedSender::XMLLoad(XNode *pNode, int iOptions)
   {
      address_ = pNode->GetAttrValue(_T("Address"));
      score_ = _ttoi(pNode->GetAttrValue(_T("Score")));
      description_ = pNode->GetAttrValue(_T("Description"));

      return true;
   }
}
