// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "../hMailServer/resource.h"
#include "../hMailServer/hMailServer.h"

#include "../Common/AntiSpam/QuarantineStore.h"

class ATL_NO_VTABLE InterfaceQuarantine :
   public CComObjectRootEx<CComSingleThreadModel>,
   public CComCoClass<InterfaceQuarantine, &CLSID_Quarantine>,
   public IDispatchImpl<IInterfaceQuarantine, &IID_IInterfaceQuarantine, &LIBID_hMailServer, /*wMajor =*/ 1, /*wMinor =*/ 0>,
   public HM::COMAuthenticator
{
public:
   InterfaceQuarantine()
   {
   }

DECLARE_REGISTRY_RESOURCEID(IDR_INTERFACEQUARANTINE)

BEGIN_COM_MAP(InterfaceQuarantine)
   COM_INTERFACE_ENTRY(IInterfaceQuarantine)
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

public:

   STDMETHOD(get_Count)(LONG* pVal);
   STDMETHOD(get_ItemByDBID)(/*[in]*/ long DBID, /*[out, retval]*/ IInterfaceQuarantinedMessage** pVal);
   STDMETHOD(get_Item)(/*[in]*/ long Index, /*[out, retval]*/ IInterfaceQuarantinedMessage** pVal);
   STDMETHOD(Refresh)();
   STDMETHOD(ReleaseByDBID)(/*[in]*/ long DBID);
   STDMETHOD(DeleteByDBID)(/*[in]*/ long DBID);
   STDMETHOD(DeleteExpired)(/*[out, retval]*/ LONG* pVal);

private:

   // The page an administrator is looking at, not the whole table. A quarantine on a
   // busy server is unbounded until the retention sweep runs, and a review queue that
   // materialises every row to draw one screen is slowest exactly when it matters.
   static const int max_listed_ = 500;

   std::vector<HM::QuarantinedMessage> messages_;
};

OBJECT_ENTRY_AUTO(__uuidof(Quarantine), InterfaceQuarantine)
