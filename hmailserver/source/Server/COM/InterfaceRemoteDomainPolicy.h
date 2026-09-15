// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once
#include "../hMailServer/resource.h"       // main symbols

#include "../hMailServer/hMailServer.h"

#include "../Common/BO/RemoteDomainPolicy.h"

#include "COMCollection.h"

namespace HM
{
   class RemoteDomainPolicies;
}


class ATL_NO_VTABLE InterfaceRemoteDomainPolicy :
   public COMCollectionItem<HM::RemoteDomainPolicy, HM::RemoteDomainPolicies>,
   public CComObjectRootEx<CComSingleThreadModel>,
   public CComCoClass<InterfaceRemoteDomainPolicy, &CLSID_RemoteDomainPolicy>,
   public IDispatchImpl<IInterfaceRemoteDomainPolicy, &IID_IInterfaceRemoteDomainPolicy, &LIBID_hMailServer, /*wMajor =*/ 1, /*wMinor =*/ 0>,
   public HM::COMAuthenticator,
   public ISupportErrorInfo
{
public:
   InterfaceRemoteDomainPolicy()
   {
   }

DECLARE_REGISTRY_RESOURCEID(IDR_INTERFACEREMOTEDOMAINPOLICY)


BEGIN_COM_MAP(InterfaceRemoteDomainPolicy)
   COM_INTERFACE_ENTRY(IInterfaceRemoteDomainPolicy)
   COM_INTERFACE_ENTRY(IDispatch)
   COM_INTERFACE_ENTRY(ISupportErrorInfo)
END_COM_MAP()


   DECLARE_PROTECT_FINAL_CONSTRUCT()

   HRESULT FinalConstruct()
   {
      return S_OK;
   }

   void FinalRelease()
   {
   }

   STDMETHOD(InterfaceSupportsErrorInfo)(REFIID riid);

   STDMETHOD(get_ID)(/*[out, retval]*/ long *pVal);

   STDMETHOD(get_DomainName)(/*[out, retval]*/ BSTR *pVal);
   STDMETHOD(put_DomainName)(/*[in]*/ BSTR newVal);

   STDMETHOD(get_Description)(/*[out, retval]*/ BSTR *pVal);
   STDMETHOD(put_Description)(/*[in]*/ BSTR newVal);

   STDMETHOD(get_Active)(/*[out, retval]*/ VARIANT_BOOL *pVal);
   STDMETHOD(put_Active)(/*[in]*/ VARIANT_BOOL newVal);

   STDMETHOD(get_OutboundTls)(/*[out, retval]*/ eRemoteTlsRequirement *pVal);
   STDMETHOD(put_OutboundTls)(/*[in]*/ eRemoteTlsRequirement newVal);

   STDMETHOD(get_RequireInboundTls)(/*[out, retval]*/ VARIANT_BOOL *pVal);
   STDMETHOD(put_RequireInboundTls)(/*[in]*/ VARIANT_BOOL newVal);

   STDMETHOD(get_MaxMessageSizeKB)(/*[out, retval]*/ long *pVal);
   STDMETHOD(put_MaxMessageSizeKB)(/*[in]*/ long newVal);

   STDMETHOD(get_MaxConnections)(/*[out, retval]*/ long *pVal);
   STDMETHOD(put_MaxConnections)(/*[in]*/ long newVal);

   STDMETHOD(get_MaxMessagesPerMinute)(/*[out, retval]*/ long *pVal);
   STDMETHOD(put_MaxMessagesPerMinute)(/*[in]*/ long newVal);

   STDMETHOD(get_AllowAutomaticReplies)(/*[out, retval]*/ VARIANT_BOOL *pVal);
   STDMETHOD(put_AllowAutomaticReplies)(/*[in]*/ VARIANT_BOOL newVal);

   STDMETHOD(get_AllowForwarding)(/*[out, retval]*/ VARIANT_BOOL *pVal);
   STDMETHOD(put_AllowForwarding)(/*[in]*/ VARIANT_BOOL newVal);

   STDMETHOD(get_CalloutEnabled)(/*[out, retval]*/ VARIANT_BOOL *pVal);
   STDMETHOD(put_CalloutEnabled)(/*[in]*/ VARIANT_BOOL newVal);

   STDMETHOD(get_CalloutHost)(/*[out, retval]*/ BSTR *pVal);
   STDMETHOD(put_CalloutHost)(/*[in]*/ BSTR newVal);

   STDMETHOD(get_CalloutPort)(/*[out, retval]*/ long *pVal);
   STDMETHOD(put_CalloutPort)(/*[in]*/ long newVal);

   STDMETHOD(get_CalloutTimeoutSeconds)(/*[out, retval]*/ long *pVal);
   STDMETHOD(put_CalloutTimeoutSeconds)(/*[in]*/ long newVal);

   STDMETHOD(get_CalloutCacheMinutes)(/*[out, retval]*/ long *pVal);
   STDMETHOD(put_CalloutCacheMinutes)(/*[in]*/ long newVal);

   STDMETHOD(get_CalloutMaxPerMinute)(/*[out, retval]*/ long *pVal);
   STDMETHOD(put_CalloutMaxPerMinute)(/*[in]*/ long newVal);

   STDMETHOD(Save)();
   STDMETHOD(Delete)();

private:

};

OBJECT_ENTRY_AUTO(__uuidof(RemoteDomainPolicy), InterfaceRemoteDomainPolicy)
