// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "COMError.h"
#include "InterfaceSecurityRanges.h"

#include "../Common/Persistence/PersistentSecurityRange.h"

#include "InterfaceSecurityRange.h"

bool 
InterfaceSecurityRanges::LoadSettings()
{
   if (!GetIsServerAdmin())
      return false;

   security_ranges_ = std::shared_ptr<HM::SecurityRanges> (new HM::SecurityRanges);
   security_ranges_->Refresh();

   return true;
}


STDMETHODIMP 
InterfaceSecurityRanges::get_Count(long *pVal)
{
   try
   {
      if (!security_ranges_)
         return GetAccessDenied();

      *pVal = security_ranges_->GetCount();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP 
InterfaceSecurityRanges::Delete(long Index)
{
   try
   {
      if (!security_ranges_)
         return GetAccessDenied();

      security_ranges_->DeleteItem(Index);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP 
InterfaceSecurityRanges::DeleteByDBID(long DBID)
{
   try
   {
      if (!security_ranges_)
         return GetAccessDenied();

      security_ranges_->DeleteItemByDBID(DBID);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceSecurityRanges::Refresh()
{
   try
   {
      if (!security_ranges_)
         return GetAccessDenied();

      security_ranges_->Refresh();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceSecurityRanges::get_Item(long Index, IInterfaceSecurityRange **pVal)
{
   try
   {
      if (!security_ranges_)
         return GetAccessDenied();

      CComObject<InterfaceSecurityRange>* pRangeInt = new CComObject<InterfaceSecurityRange>();
      pRangeInt->SetAuthentication(authentication_);
   
      std::shared_ptr<HM::SecurityRange> pRange = security_ranges_->GetItem(Index);
   
      if (pRange)
      {
         pRangeInt->AttachItem(pRange);
         pRangeInt->AttachParent(security_ranges_, true);
         pRangeInt->AddRef();
         *pVal = pRangeInt;
      }
      else
      {
         return DISP_E_BADINDEX;  
      }
   
   
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceSecurityRanges::get_ItemByDBID(long DBID, IInterfaceSecurityRange **pVal)
{
   try
   {
      if (!security_ranges_)
         return GetAccessDenied();

      CComObject<InterfaceSecurityRange>* pRangeInt = new CComObject<InterfaceSecurityRange>();
      pRangeInt->SetAuthentication(authentication_);
   
      std::shared_ptr<HM::SecurityRange> pRange = security_ranges_->GetItemByDBID(DBID);
   
      if (pRange)
      {
         pRangeInt->AttachItem(pRange);
         pRangeInt->AttachParent(security_ranges_, true);
         pRangeInt->AddRef();
         *pVal = pRangeInt;
      }
      else
      {
         return DISP_E_BADINDEX;  
      }
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceSecurityRanges::Add(IInterfaceSecurityRange **pVal)
{
   try
   {
      if (!security_ranges_)
         return GetAccessDenied();

      if (!security_ranges_)
         return authentication_->GetAccessDenied();
   
      CComObject<InterfaceSecurityRange>* pInterfaceRange = new CComObject<InterfaceSecurityRange>();
      pInterfaceRange->SetAuthentication(authentication_);
   
      std::shared_ptr<HM::SecurityRange> pRange = std::shared_ptr<HM::SecurityRange>(new HM::SecurityRange); 
   
      pInterfaceRange->AttachItem(pRange);
      pInterfaceRange->AttachParent(security_ranges_, false);
   
      pInterfaceRange->AddRef();
      *pVal = pInterfaceRange;
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceSecurityRanges::get_ItemByName(BSTR sName, IInterfaceSecurityRange **pVal)
{
   try
   {
      if (!security_ranges_)
         return GetAccessDenied();

      CComObject<InterfaceSecurityRange>* pRangeInt = new CComObject<InterfaceSecurityRange>();
      pRangeInt->SetAuthentication(authentication_);
   
      std::shared_ptr<HM::SecurityRange> pRange = security_ranges_->GetItemByName(sName);
   
      if (pRange)
      {
         pRangeInt->AttachItem(pRange);
         pRangeInt->AttachParent(security_ranges_, true);
         pRangeInt->AddRef();
         *pVal = pRangeInt;
      }
      else
      {
         return DISP_E_BADINDEX;  
      }
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceSecurityRanges::SetDefault()
{
   try
   {
      if (!security_ranges_)
         return GetAccessDenied();

      if (!security_ranges_->SetDefault())
      {
         // As above for the ports: the previous ranges are gone whether or not the
         // replacements were written, and an address matching no range is refused.
         return COMError::GenerateError("The default IP ranges could not be restored. The previous ranges were removed first, so the server may now refuse connections it previously accepted - check the hMailServer error log, correct the problem and try again.");
      }

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}


