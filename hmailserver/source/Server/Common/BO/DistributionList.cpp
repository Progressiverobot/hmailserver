// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "distributionlist.h"

#include "DistributionListRecipients.h"

#include "../Persistence/PersistentDistributionListRecipient.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   DistributionList::DistributionList(void) :
      domain_id_(false),
      require_auth_(false),
      list_mode_(LMPublic),
      enabled_(false)
   {
      
   }

   DistributionList::~DistributionList(void)
   {
   }

   bool 
   DistributionList::XMLStore(XNode *pParentNode, int iOptions)
   {
      XNode *pNode = pParentNode->AppendChild(_T("DistributionList"));

      String sListMode;
      sListMode.Format(_T("%d"), list_mode_);

      pNode->AppendAttr(_T("Name"), address_);
      pNode->AppendAttr(_T("Active"), enabled_ ? _T("1") : _T("0"));
      pNode->AppendAttr(_T("RequiresAuth"), require_auth_ ? _T("1") : _T("0"));
      pNode->AppendAttr(_T("RequiresAuthAddress"), require_address_);
      pNode->AppendAttr(_T("ListMode"), sListMode);

      return GetMembers()->XMLStore(pNode, iOptions);
   }

   bool
   DistributionList::XMLLoad(XNode *pNode, int iRestoreOptions)
   {
      address_ = pNode->GetAttrValue(_T("Name"));
      enabled_ = pNode->GetAttrValue(_T("Active")) == _T("1");
      require_auth_ = pNode->GetAttrValue(_T("RequiresAuth")) == _T("1");
      require_address_ = pNode->GetAttrValue(_T("RequiresAuthAddress"));
      list_mode_ = (ListMode) _ttoi(pNode->GetAttrValue(_T("ListMode")));

      return true;
   }

   bool
   DistributionList::XMLLoadSubItems(XNode *pNode, int iRestoreOptions)
   {
      std::shared_ptr<DistributionListRecipients> pDistListRecipients = GetMembers();
      return pDistListRecipients->XMLLoad(pNode, iRestoreOptions);
   }

   String
   DistributionList::GetRfc2919ListId() const
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Builds the RFC 2919 List-Id value from the list address. The identifier is
   // required to have the syntax of a domain name, so any character of the local
   // part which is not legal in a DNS label is replaced with a hyphen. The result
   // is never resolved - RFC 2919 identifiers are opaque - but it does have to
   // parse, and hMailServer permits list addresses (underscores, quoted local
   // parts) which would otherwise produce a syntactically invalid header.
   //---------------------------------------------------------------------------()
   {
      // Both halves of the address have to exist before it can be split. This is
      // checked here rather than left to ExtractAddress/ExtractDomain because those
      // split on the last @ without validating: handed an address with no @ at all
      // they return the whole string as the domain, which would turn a malformed
      // list address into a plausible-looking but meaningless identifier.
      const int atSignPosition = address_.ReverseFind(_T("@"));

      if (atSignPosition < 1 || atSignPosition == address_.GetLength() - 1)
         return "";

      const String localPart = StringParser::ExtractAddress(address_);
      const String domainPart = StringParser::ExtractDomain(address_);

      if (localPart.IsEmpty() || domainPart.IsEmpty())
         return "";

      String sanitizedLocalPart;

      for (const wchar_t character : localPart)
      {
         const bool legalInLabel =
            (character >= L'a' && character <= L'z') ||
            (character >= L'A' && character <= L'Z') ||
            (character >= L'0' && character <= L'9') ||
            character == L'-' || character == L'.';

         sanitizedLocalPart += legalInLabel ? character : L'-';
      }

      String listId;
      listId += "<";
      listId += sanitizedLocalPart;
      listId += ".";
      listId += domainPart;
      listId += ">";

      return listId;
   }

   std::shared_ptr<DistributionListRecipients>
   DistributionList::GetMembers() const
   {
      std::shared_ptr<DistributionListRecipients> members = std::shared_ptr<DistributionListRecipients> (new DistributionListRecipients(id_)) ;
      members->Refresh();
      return members;
   }

   size_t
   DistributionList::GetEstimatedCachingSize()
   {
      return 1024;
   }

}
