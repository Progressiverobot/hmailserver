// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "InterfaceQuarantine.h"
#include "InterfaceQuarantinedMessage.h"

#include "COMError.h"

STDMETHODIMP InterfaceQuarantine::Refresh()
{
   try
   {
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();

      messages_ = HM::QuarantineStore::List(max_listed_);

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceQuarantine::get_Count(LONG* pVal)
{
   try
   {
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();

      // The TOTAL held, deliberately, and not the size of the page loaded by Refresh.
      // "How much is in quarantine" is the question this answers, and a count that
      // silently stopped at 500 would tell an administrator the backlog had levelled
      // off exactly when it had not.
      *pVal = (LONG) HM::QuarantineStore::GetCount();

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceQuarantine::get_Item(long Index, IInterfaceQuarantinedMessage** pVal)
{
   try
   {
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();

      if (Index < 0 || Index >= (long) messages_.size())
         return COMError::GenerateError("Index out of range. Call Refresh before indexing the quarantine.");

      CComObject<InterfaceQuarantinedMessage>* pItem = new CComObject<InterfaceQuarantinedMessage>();
      pItem->SetAuthentication(authentication_);
      pItem->Attach(messages_[Index]);
      pItem->AddRef();

      *pVal = pItem;

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceQuarantine::get_ItemByDBID(long DBID, IInterfaceQuarantinedMessage** pVal)
{
   try
   {
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();

      // Read straight from the store rather than searched in the loaded page: an
      // identifier the caller already holds should not depend on whether Refresh
      // happened to include that row.
      HM::QuarantinedMessage message;

      if (!HM::QuarantineStore::GetById(DBID, message))
         return COMError::GenerateError("No quarantined message with that identifier.");

      CComObject<InterfaceQuarantinedMessage>* pItem = new CComObject<InterfaceQuarantinedMessage>();
      pItem->SetAuthentication(authentication_);
      pItem->Attach(message);
      pItem->AddRef();

      *pVal = pItem;

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceQuarantine::ReleaseByDBID(long DBID)
{
   try
   {
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();

      HM::String error;

      if (!HM::QuarantineStore::Release(DBID, error))
         return COMError::GenerateError(error);

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceQuarantine::DeleteByDBID(long DBID)
{
   try
   {
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();

      if (!HM::QuarantineStore::Delete(DBID))
         return COMError::GenerateError("The quarantined message could not be deleted.");

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceQuarantine::DeleteExpired(LONG* pVal)
{
   try
   {
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();

      *pVal = (LONG) HM::QuarantineStore::DeleteExpired();

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}
