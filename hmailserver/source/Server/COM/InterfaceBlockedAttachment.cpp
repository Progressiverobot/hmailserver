// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "COMError.h"

#include "../COM/InterfaceBlockedAttachment.h"

#include "../Common/Persistence/PersistentBlockedAttachment.h"
#include "../Common/BO/BlockedAttachment.h"

// InterfaceBlockedAttachment

STDMETHODIMP InterfaceBlockedAttachment::InterfaceSupportsErrorInfo(REFIID riid)
{
   try
   {
      // Without this the sentence Save() puts in the error info never reaches
      // a .NET caller - it sees the bare HRESULT - which is how the parity
      // gate of 14 September 2026 read "Exception from HRESULT: 0x800403E9"
      // where the store had said what was wrong.
      static const IID* arr[] =
      {
         &IID_IInterfaceBlockedAttachment,
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

STDMETHODIMP
InterfaceBlockedAttachment::Save()
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();

      // The persistence layer's refusal reaches the caller as the range's
      // and the domain's do, rather than an S_OK over a row that was not
      // written.
      HM::String result;
      if (HM::PersistentBlockedAttachment::SaveObject(object_, result, HM::PersistenceModeNormal))
      {
         // Add to parent collection
         AddToParentCollection();

         return S_OK;
      }

      return COMError::GenerateError(result);
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceBlockedAttachment::get_ID(long *pVal)
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

STDMETHODIMP InterfaceBlockedAttachment::put_Wildcard(BSTR newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetWildcard(newVal);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceBlockedAttachment::get_Wildcard(BSTR *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetWildcard().AllocSysString();
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceBlockedAttachment::put_Description(BSTR newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetDescription(newVal);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceBlockedAttachment::get_Description(BSTR *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetDescription().AllocSysString();
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceBlockedAttachment::Delete()
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();
   
      if (!parent_collection_)
         return HM::PersistentBlockedAttachment::DeleteObject(object_) ? S_OK : S_FALSE;
   
      parent_collection_->DeleteItemByDBID(object_->GetID());
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}


