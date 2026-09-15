// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "COMError.h"
#include "InterfaceRemoteDomainPolicies.h"

#include "InterfaceRemoteDomainPolicy.h"

#include "../SMTP/SMTPConfiguration.h"
#include "../SMTP/RecipientCallout.h"


bool
InterfaceRemoteDomainPolicies::LoadSettings()
{
   // Server administrators only, as the routes are. A policy decides whether
   // this server refuses to deliver a domain's mail in the clear and whether it
   // opens verification sessions to a third party; neither belongs to one
   // hosted domain's administrator.
   if (!GetIsServerAdmin())
      return false;

   policies_ = HM::Configuration::Instance()->GetSMTPConfiguration()->GetRemoteDomainPolicies();

   return true;
}

STDMETHODIMP InterfaceRemoteDomainPolicies::get_Count(long *pVal)
{
   try
   {
      if (!policies_)
         return GetAccessDenied();

      *pVal = policies_->GetCount();

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicies::get_Item(long Index, IInterfaceRemoteDomainPolicy **pVal)
{
   try
   {
      if (!policies_)
         return GetAccessDenied();

      std::shared_ptr<HM::RemoteDomainPolicy> policy = policies_->GetItem(Index);

      if (!policy)
         return DISP_E_BADINDEX;

      CComObject<InterfaceRemoteDomainPolicy>* item = new CComObject<InterfaceRemoteDomainPolicy>();
      item->SetAuthentication(authentication_);

      item->AttachItem(policy);
      item->AttachParent(policies_, true);
      item->AddRef();
      *pVal = item;

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicies::DeleteByDBID(long DBID)
{
   try
   {
      if (!policies_)
         return GetAccessDenied();

      policies_->DeleteItemByDBID(DBID);

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicies::Add(IInterfaceRemoteDomainPolicy **pVal)
{
   try
   {
      if (!policies_)
         return GetAccessDenied();

      CComObject<InterfaceRemoteDomainPolicy>* item = new CComObject<InterfaceRemoteDomainPolicy>();
      item->SetAuthentication(authentication_);

      std::shared_ptr<HM::RemoteDomainPolicy> policy = std::shared_ptr<HM::RemoteDomainPolicy>(new HM::RemoteDomainPolicy);

      item->AttachItem(policy);
      item->AttachParent(policies_, false);

      item->AddRef();
      *pVal = item;

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP
InterfaceRemoteDomainPolicies::get_ItemByName(BSTR ItemName, IInterfaceRemoteDomainPolicy **pVal)
{
   try
   {
      if (!policies_)
         return GetAccessDenied();

      // The pattern as it was typed, which is not the same question as
      // get_PolicyForDomain below: this finds the record called "*.example",
      // that one finds the record that governs "mail.example".
      std::shared_ptr<HM::RemoteDomainPolicy> policy = policies_->GetItemByName(ItemName);
      if (!policy)
         return DISP_E_BADINDEX;

      CComObject<InterfaceRemoteDomainPolicy>* item = new CComObject<InterfaceRemoteDomainPolicy>();
      item->SetAuthentication(authentication_);

      item->AttachItem(policy);
      item->AttachParent(policies_, true);

      item->AddRef();
      *pVal = item;

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP
InterfaceRemoteDomainPolicies::get_ItemByDBID(long lDBID, IInterfaceRemoteDomainPolicy **pVal)
{
   try
   {
      if (!policies_)
         return GetAccessDenied();

      std::shared_ptr<HM::RemoteDomainPolicy> policy = policies_->GetItemByDBID(lDBID);
      if (!policy)
         return DISP_E_BADINDEX;

      CComObject<InterfaceRemoteDomainPolicy>* item = new CComObject<InterfaceRemoteDomainPolicy>();
      item->SetAuthentication(authentication_);

      item->AttachItem(policy);
      item->AttachParent(policies_, true);

      item->AddRef();
      *pVal = item;

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP
InterfaceRemoteDomainPolicies::get_PolicyForDomain(BSTR DomainName, IInterfaceRemoteDomainPolicy **pVal)
{
   try
   {
      if (!policies_)
         return GetAccessDenied();

      // The question delivery asks: which record governs this domain. The most
      // specific active match wins, and an administrator can ask it here before
      // finding out from a message that did not go.
      HM::String domain = DomainName;
      domain.ToLower();

      std::shared_ptr<HM::RemoteDomainPolicy> policy = policies_->GetPolicyForDomain(domain);
      if (!policy)
         return DISP_E_BADINDEX;

      CComObject<InterfaceRemoteDomainPolicy>* item = new CComObject<InterfaceRemoteDomainPolicy>();
      item->SetAuthentication(authentication_);

      item->AttachItem(policy);
      item->AttachParent(policies_, true);

      item->AddRef();
      *pVal = item;

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP
InterfaceRemoteDomainPolicies::Refresh()
{
   try
   {
      if (!policies_)
         return GetAccessDenied();

      policies_->Refresh();

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP
InterfaceRemoteDomainPolicies::ClearVerificationCache()
{
   try
   {
      if (!policies_)
         return GetAccessDenied();

      HM::RecipientCallout::Instance()->ClearCache();

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP
InterfaceRemoteDomainPolicies::get_VerificationCacheSize(long *pVal)
{
   try
   {
      if (!policies_)
         return GetAccessDenied();

      *pVal = HM::RecipientCallout::Instance()->GetCacheSize();

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}
