// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "COMError.h"

#include "InterfaceBlockedSender.h"

#include "..\Common\Persistence\PersistentBlockedSender.h"
#include "..\Common\BO\BlockedSender.h"

// InterfaceBlockedSender

STDMETHODIMP
InterfaceBlockedSender::Save()
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();

      if (HM::PersistentBlockedSender::SaveObject(object_))
      {
         // Add to parent collection
         AddToParentCollection();
      }

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP
InterfaceBlockedSender::Delete()
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();

      if (!parent_collection_)
         return HM::PersistentBlockedSender::DeleteObject(object_) ? S_OK : S_FALSE;

      parent_collection_->DeleteItemByDBID(object_->GetID());

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceBlockedSender::get_ID(long *pVal)
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

STDMETHODIMP InterfaceBlockedSender::get_Address(BSTR *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetAddress().AllocSysString();

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceBlockedSender::put_Address(BSTR newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetAddress(newVal);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceBlockedSender::get_Score(long *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetScore();

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceBlockedSender::put_Score(long newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetScore(newVal);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceBlockedSender::get_Description(BSTR *pVal)
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

STDMETHODIMP InterfaceBlockedSender::put_Description(BSTR newVal)
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
