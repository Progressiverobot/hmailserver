// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "../hMailServer/resource.h"
#include "../hMailServer/hMailServer.h"

#include "../Common/AntiSpam/QuarantineStore.h"

class ATL_NO_VTABLE InterfaceQuarantinedMessage :
   public CComObjectRootEx<CComSingleThreadModel>,
   public CComCoClass<InterfaceQuarantinedMessage, &CLSID_QuarantinedMessage>,
   public IDispatchImpl<IInterfaceQuarantinedMessage, &IID_IInterfaceQuarantinedMessage, &LIBID_hMailServer, /*wMajor =*/ 1, /*wMinor =*/ 0>,
   public HM::COMAuthenticator
{
public:
   InterfaceQuarantinedMessage()
   {
   }

DECLARE_REGISTRY_RESOURCEID(IDR_INTERFACEQUARANTINEDMESSAGE)

BEGIN_COM_MAP(InterfaceQuarantinedMessage)
   COM_INTERFACE_ENTRY(IInterfaceQuarantinedMessage)
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

   // A snapshot of the row rather than a live object. The quarantine is a queue an
   // administrator works through, so the alternative - re-reading each row on every
   // property access - would turn drawing one screen into dozens of queries for data
   // that has not changed.
   void Attach(const HM::QuarantinedMessage &message);

public:

   STDMETHOD(get_ID)(LONG* pVal);
   STDMETHOD(get_Sender)(BSTR* pVal);
   STDMETHOD(get_Recipients)(BSTR* pVal);
   STDMETHOD(get_Subject)(BSTR* pVal);
   STDMETHOD(get_Reason)(BSTR* pVal);
   STDMETHOD(get_Score)(LONG* pVal);
   STDMETHOD(get_Size)(LONG* pVal);
   STDMETHOD(get_CreatedTime)(BSTR* pVal);

   // NOT Release: that name is already taken by IUnknown, which every COM
   // interface inherits, and the collision is a compile error rather than
   // something subtle.
   STDMETHOD(ReleaseMessage)();
   STDMETHOD(Delete)();

private:

   HM::QuarantinedMessage message_;
};

OBJECT_ENTRY_AUTO(__uuidof(QuarantinedMessage), InterfaceQuarantinedMessage)
