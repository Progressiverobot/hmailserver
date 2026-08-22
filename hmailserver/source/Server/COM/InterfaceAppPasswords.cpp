// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "COMError.h"

#include "InterfaceAppPasswords.h"
#include "InterfaceAppPassword.h"

#include "../Common/Persistence/PersistentAppPassword.h"

void
InterfaceAppPasswords::Attach(std::shared_ptr<HM::AppPasswords> passwords, __int64 accountID)
{
   app_passwords_ = passwords;
   account_id_ = accountID;
}

STDMETHODIMP InterfaceAppPasswords::get_ItemByDBID(long lDBID, IInterfaceAppPassword** pVal)
{
   try
   {
      if (!app_passwords_)
         return GetAccessDenied();

      std::shared_ptr<HM::AppPassword> password = app_passwords_->GetItemByDBID(lDBID);
      if (!password)
         return DISP_E_BADINDEX;

      CComObject<InterfaceAppPassword>* item = new CComObject<InterfaceAppPassword>();
      item->SetAuthentication(authentication_);

      item->AttachItem(password);
      item->AttachParent(app_passwords_, true);
      item->AddRef();
      *pVal = item;

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAppPasswords::get_Item(long lIndex, IInterfaceAppPassword** pVal)
{
   try
   {
      if (!app_passwords_)
         return GetAccessDenied();

      std::shared_ptr<HM::AppPassword> password = app_passwords_->GetItem(lIndex);
      if (!password)
         return DISP_E_BADINDEX;

      CComObject<InterfaceAppPassword>* item = new CComObject<InterfaceAppPassword>();
      item->SetAuthentication(authentication_);

      item->AttachItem(password);
      item->AttachParent(app_passwords_, true);
      item->AddRef();
      *pVal = item;

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAppPasswords::Refresh(void)
{
   try
   {
      if (!app_passwords_)
         return GetAccessDenied();

      app_passwords_->Refresh(account_id_);

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAppPasswords::Delete(LONG Index)
{
   try
   {
      if (!app_passwords_)
         return GetAccessDenied();

      if (!authentication_->GetIsDomainAdmin())
         return authentication_->GetAccessDenied();

      app_passwords_->DeleteItem(Index);

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAppPasswords::DeleteByDBID(LONG DBID)
{
   try
   {
      if (!app_passwords_)
         return GetAccessDenied();

      if (!authentication_->GetIsDomainAdmin())
         return authentication_->GetAccessDenied();

      app_passwords_->DeleteItemByDBID(DBID);

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAppPasswords::get_Count(long *pVal)
{
   try
   {
      if (!app_passwords_)
         return GetAccessDenied();

      *pVal = app_passwords_->GetCount();

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAppPasswords::Add(IInterfaceAppPassword **pVal)
{
   try
   {
      if (!app_passwords_)
         return GetAccessDenied();

      if (!authentication_->GetIsDomainAdmin())
         return authentication_->GetAccessDenied();

      CComObject<InterfaceAppPassword>* item = new CComObject<InterfaceAppPassword>();
      item->SetAuthentication(authentication_);

      std::shared_ptr<HM::AppPassword> password = std::shared_ptr<HM::AppPassword>(new HM::AppPassword);

      password->SetAccountID(account_id_);

      item->AttachItem(password);
      item->AttachParent(app_passwords_, false);

      item->AddRef();
      *pVal = item;

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}
