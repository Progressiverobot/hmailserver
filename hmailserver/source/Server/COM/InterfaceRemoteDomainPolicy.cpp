// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "InterfaceRemoteDomainPolicy.h"

#include "../Common/BO/RemoteDomainPolicies.h"
#include "../Common/Persistence/PersistentRemoteDomainPolicy.h"
#include "../SMTP/RecipientCallout.h"

#include "COMError.h"

STDMETHODIMP InterfaceRemoteDomainPolicy::InterfaceSupportsErrorInfo(REFIID riid)
{
   try
   {
      static const IID* arr[] =
      {
         &IID_IInterfaceRemoteDomainPolicy,
      };

      for (int i = 0; i < sizeof(arr) / sizeof(arr[0]); i++)
      {
         if (InlineIsEqualGUID(*arr[i], riid))
            return S_OK;
      }
      return S_FALSE;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::get_ID(long *pVal)
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

STDMETHODIMP InterfaceRemoteDomainPolicy::get_DomainName(BSTR *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetDomainName().AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::put_DomainName(BSTR newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetDomainName(newVal);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::get_Description(BSTR *pVal)
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

STDMETHODIMP InterfaceRemoteDomainPolicy::put_Description(BSTR newVal)
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

STDMETHODIMP InterfaceRemoteDomainPolicy::get_Active(VARIANT_BOOL *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetActive() ? VARIANT_TRUE : VARIANT_FALSE;
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::put_Active(VARIANT_BOOL newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetActive(newVal == VARIANT_TRUE);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::get_OutboundTls(eRemoteTlsRequirement *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = (eRemoteTlsRequirement) object_->GetOutboundTlsRequirement();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::put_OutboundTls(eRemoteTlsRequirement newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      // A number outside the enum is refused rather than stored. The values
      // decide whether mail is held up, and a stored 7 would be read back by
      // PersistentRemoteDomainPolicy as RemoteTlsDefault at the next start -
      // so the policy would work until the service was restarted and then
      // silently stop. Refusing here is the only place that can be seen.
      if (newVal < HM::RemoteTlsDefault || newVal > HM::RemoteTlsDane)
         return COMError::GenerateError("Invalid TLS requirement. Use 0 (none), 1 (encrypted), 2 (verified) or 3 (DANE).");

      object_->SetOutboundTlsRequirement((HM::RemoteTlsRequirement) newVal);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::get_RequireInboundTls(VARIANT_BOOL *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetRequireInboundTls() ? VARIANT_TRUE : VARIANT_FALSE;
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::put_RequireInboundTls(VARIANT_BOOL newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetRequireInboundTls(newVal == VARIANT_TRUE);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::get_MaxMessageSizeKB(long *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetMaxMessageSizeKB();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::put_MaxMessageSizeKB(long newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      if (newVal < 0)
         return COMError::GenerateError("The maximum message size cannot be negative. 0 means no limit.");

      object_->SetMaxMessageSizeKB(newVal);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::get_MaxConnections(long *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetMaxConnections();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::put_MaxConnections(long newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      if (newVal < 0)
         return COMError::GenerateError("The maximum number of connections cannot be negative. 0 means unlimited.");

      object_->SetMaxConnections(newVal);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::get_MaxMessagesPerMinute(long *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetMaxMessagesPerMinute();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::put_MaxMessagesPerMinute(long newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      if (newVal < 0)
         return COMError::GenerateError("The message rate cannot be negative. 0 means unlimited.");

      object_->SetMaxMessagesPerMinute(newVal);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::get_AllowAutomaticReplies(VARIANT_BOOL *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetAllowAutomaticReplies() ? VARIANT_TRUE : VARIANT_FALSE;
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::put_AllowAutomaticReplies(VARIANT_BOOL newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetAllowAutomaticReplies(newVal == VARIANT_TRUE);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::get_AllowForwarding(VARIANT_BOOL *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetAllowForwarding() ? VARIANT_TRUE : VARIANT_FALSE;
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::put_AllowForwarding(VARIANT_BOOL newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetAllowForwarding(newVal == VARIANT_TRUE);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::get_CalloutEnabled(VARIANT_BOOL *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetCalloutEnabled() ? VARIANT_TRUE : VARIANT_FALSE;
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::put_CalloutEnabled(VARIANT_BOOL newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetCalloutEnabled(newVal == VARIANT_TRUE);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::get_CalloutHost(BSTR *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetCalloutHost().AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::put_CalloutHost(BSTR newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetCalloutHost(newVal);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::get_CalloutPort(long *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetCalloutPort();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::put_CalloutPort(long newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      if (newVal < 1 || newVal > 65535)
         return COMError::GenerateError("The verification port must be between 1 and 65535.");

      object_->SetCalloutPort(newVal);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::get_CalloutTimeoutSeconds(long *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetCalloutTimeoutSeconds();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::put_CalloutTimeoutSeconds(long newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      // The ceiling is enforced here as well as in RecipientCallout, because a
      // value refused at the door is a value an administrator sees refused; one
      // silently clamped at use looks like it worked and did not.
      if (newVal < 1 || newVal > HM::RecipientCallout::CalloutMaxTimeoutSeconds)
         return COMError::GenerateError("The verification timeout must be between 1 and 60 seconds. It is spent inside an SMTP session that a sender is waiting on.");

      object_->SetCalloutTimeoutSeconds(newVal);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::get_CalloutCacheMinutes(long *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetCalloutCacheMinutes();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::put_CalloutCacheMinutes(long newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      if (newVal < 0)
         return COMError::GenerateError("The verification cache lifetime cannot be negative.");

      object_->SetCalloutCacheMinutes(newVal);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::get_CalloutMaxPerMinute(long *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetCalloutMaxPerMinute();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::put_CalloutMaxPerMinute(long newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      if (newVal < 0)
         return COMError::GenerateError("The verification rate cannot be negative. 0 means unlimited, which is not recommended.");

      object_->SetCalloutMaxPerMinute(newVal);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::Save()
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();

      HM::String errorMessage;
      if (HM::PersistentRemoteDomainPolicy::SaveObject(object_, errorMessage, HM::PersistenceModeNormal))
      {
         AddToParentCollection();

         return S_OK;
      }

      return COMError::GenerateError(errorMessage);
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRemoteDomainPolicy::Delete()
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();

      if (!parent_collection_)
         return HM::PersistentRemoteDomainPolicy::DeleteObject(object_) ? S_OK : S_FALSE;

      parent_collection_->DeleteItemByDBID(object_->GetID());

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}
