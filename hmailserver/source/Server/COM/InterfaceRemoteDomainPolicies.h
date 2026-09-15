// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once
#include "../hMailServer/resource.h"       // main symbols

#include "../hMailServer/hMailServer.h"

#include "../Common/BO/RemoteDomainPolicies.h"

namespace HM
{
   class RemoteDomainPolicy;
}

class ATL_NO_VTABLE InterfaceRemoteDomainPolicies :
   public CComObjectRootEx<CComSingleThreadModel>,
   public CComCoClass<InterfaceRemoteDomainPolicies, &CLSID_RemoteDomainPolicies>,
   public IDispatchImpl<IInterfaceRemoteDomainPolicies, &IID_IInterfaceRemoteDomainPolicies, &LIBID_hMailServer, /*wMajor =*/ 1, /*wMinor =*/ 0>,
   public HM::COMAuthenticator
{
public:
   InterfaceRemoteDomainPolicies()
   {
   }

   bool LoadSettings();

DECLARE_REGISTRY_RESOURCEID(IDR_INTERFACEREMOTEDOMAINPOLICIES)


BEGIN_COM_MAP(InterfaceRemoteDomainPolicies)
   COM_INTERFACE_ENTRY(IInterfaceRemoteDomainPolicies)
   COM_INTERFACE_ENTRY(IDispatch)
END_COM_MAP()


   DECLARE_PROTECT_FINAL_CONSTRUCT()

   HRESULT FinalConstruct()
   {
      return S_OK;
   }

   void FinalRelease()
   {
   }

   STDMETHOD(get_Item)(/*[in]*/ long Index, /*[out, retval]*/ IInterfaceRemoteDomainPolicy **pVal);
   STDMETHOD(get_Count)(/*[out, retval]*/ long *pVal);

   STDMETHOD(get_ItemByName)(/*[in]*/ BSTR ItemName, /*[out, retval]*/ IInterfaceRemoteDomainPolicy** pVal);
   STDMETHOD(get_ItemByDBID)(/*[in]*/ long lDBID, /*[out, retval]*/ IInterfaceRemoteDomainPolicy** pVal);
   STDMETHOD(get_PolicyForDomain)(/*[in]*/ BSTR DomainName, /*[out, retval]*/ IInterfaceRemoteDomainPolicy** pVal);
   STDMETHOD(DeleteByDBID)(/*[in]*/ long DBID);

   STDMETHOD(Add)(/*[out, retval]*/ IInterfaceRemoteDomainPolicy **pVal);

   STDMETHOD(Refresh)();

   STDMETHOD(ClearVerificationCache)();
   STDMETHOD(get_VerificationCacheSize)(/*[out, retval]*/ long *pVal);

public:

   std::shared_ptr<HM::RemoteDomainPolicies> policies_;

};

OBJECT_ENTRY_AUTO(__uuidof(RemoteDomainPolicies), InterfaceRemoteDomainPolicies)
