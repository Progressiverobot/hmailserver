// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "InterfaceDistributionList.h"
#include "InterfaceDistributionListRecipients.h"

#include "../common/persistence/PersistentDistributionList.h"
#include "../common/persistence/PersistentDistributionListRecipient.h"

#include "../common/bo/DistributionLists.h"
#include "../common/bo/DistributionListRecipients.h"

#include "COMError.h"


#ifdef _DEBUG
   long InterfaceDistributionList::counter = 0;
#endif

STDMETHODIMP InterfaceDistributionList::InterfaceSupportsErrorInfo(REFIID riid)
{
   try
   {
      static const IID* arr[] = 
      {
         &IID_IInterfaceDistributionList,
      };
   
      for (int i=0;i<sizeof(arr)/sizeof(arr[0]);i++)
      {
         if (InlineIsEqualGUID(*arr[i],riid))
            return S_OK;
      }
      return S_FALSE;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceDistributionList::get_ID(long *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = (long) object_->GetID();
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceDistributionList::get_Address(BSTR *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

   
   
      HM::String sName = object_->GetAddress();
   
      *pVal = sName.AllocSysString();
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceDistributionList::put_Address(BSTR newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

   
   
      HM::String sName = newVal;
      object_->SetAddress(sName);
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceDistributionList::get_RequireSenderAddress(BSTR *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

   
   
      HM::String sAddress = object_->GetRequireAddress();
   
      *pVal = sAddress.AllocSysString();
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceDistributionList::put_RequireSenderAddress(BSTR newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

   
   
      HM::String sName = newVal;
      object_->SetRequireAddress(sName);
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceDistributionList::get_RequireSMTPAuth(VARIANT_BOOL *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

   
   
      if (object_->GetRequireAuth())
         *pVal = VARIANT_TRUE;
      else
         *pVal = VARIANT_FALSE;
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceDistributionList::put_RequireSMTPAuth(VARIANT_BOOL newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

   
   
      if (newVal == VARIANT_TRUE)
         object_->SetRequireAuth(true);
      else
         object_->SetRequireAuth(false);
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceDistributionList::get_Active(VARIANT_BOOL *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

   
   
      if (object_->GetActive())
         *pVal = VARIANT_TRUE;
      else
         *pVal = VARIANT_FALSE;
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceDistributionList::put_Active(VARIANT_BOOL newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

   
   
      if (newVal == VARIANT_TRUE)
         object_->SetActive(true);
      else
         object_->SetActive(false);
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceDistributionList::Delete()
{
   try
   {
      if (!object_)
         return GetAccessDenied();

   
   
      if (!object_)
         return S_FALSE;
   
      HM::PersistentDistributionList::DeleteObject(object_);
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceDistributionList::Save()
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      if (!object_)
         return S_FALSE;
   
      HM::String sErrorMessage;
      if (HM::PersistentDistributionList::SaveObject(object_, sErrorMessage, HM::PersistenceModeNormal))
      {
         // Add to parent collection
         AddToParentCollection();
   
         return S_OK;
      }
   
      return COMError::GenerateError("Failed to save object. " +  sErrorMessage);
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceDistributionList::get_Recipients(IInterfaceDistributionListRecipients **pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      CComObject<InterfaceDistributionListRecipients>* pItem = new CComObject<InterfaceDistributionListRecipients>();
      pItem->SetAuthentication(authentication_);
   
      pItem->SetListID(object_->GetID());
      std::shared_ptr<HM::DistributionListRecipients> pRecipients = object_->GetMembers();
   
      if (pRecipients)
      {
         pItem->Attach(pRecipients);
         pItem->AddRef();
         *pVal = pItem;
      }
   
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceDistributionList::get_Mode(eDistributionListMode *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      switch (object_->GetListMode())
      {
      case HM::DistributionList::LMPublic:
         *pVal = eLMPublic;
         break;
      case HM::DistributionList::LMMembership:
         *pVal = eLMMembership;
         break;
      case HM::DistributionList::LMAnnouncement:
         *pVal = eLMAnnouncement;
         break;
      case HM::DistributionList::LMDomainMembers:
         *pVal = eLMDomainMembers;
         break;
      default:
         *pVal = eLMPublic;
         break;
      }
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceDistributionList::put_Mode(eDistributionListMode newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      /*
         The seed value used to be LMPublic and the switch had no default, so a
         value this server does not implement fell straight through and stored
         "anyone may send" - the single most permissive mode there is.

         That is not a theoretical hole. The IDL declares eLMServerMembers = 4
         ("anyone with an account on this server"), which nothing in the server
         implements: DistributionList::ListMode stops at LMDomainMembers = 3 and
         RecipientParser::UserCanSendToList_ has no branch for a fifth mode. So
         a caller selecting the more restrictive-sounding of the two "anyone..."
         options - to keep outsiders off a list - silently got a list that
         accepts mail from anywhere on the internet. get_Mode's own default then
         reported it back as "Public", so the only visible symptom was a
         selection that appeared not to have stuck.

         Failing closed is not the answer either: silently substituting the most
         restrictive mode would leave the caller equally misinformed, just in the
         other direction. The value is refused, with a message naming what was
         asked for, so the caller learns that the mode does not exist. This is
         the same correction put_AdminLevel needed for the same reason.
      */
      HM::DistributionList::ListMode iMode;

      switch (newVal)
      {
      case eLMPublic:
         iMode = HM::DistributionList::LMPublic;
         break;
      case eLMMembership:
         iMode = HM::DistributionList::LMMembership;
         break;
      case eLMAnnouncement:
         iMode = HM::DistributionList::LMAnnouncement;
         break;
      case eLMDomainMembers:
         iMode = HM::DistributionList::LMDomainMembers;
         break;
      default:
         // HM::Formatter::Format, spelled the way InterfaceScripting.cpp spells it -
         // the COM sources reach it through the precompiled header rather than a
         // direct include.
         return COMError::GenerateError(
            HM::Formatter::Format("The distribution list mode {0} is not supported by this server. "
                                  "Valid modes are 0 (Public), 1 (Membership), 2 (Announcement) and "
                                  "3 (Domain members). The list has not been changed.", (int) newVal));
      }

      object_->SetListMode(iMode);

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

