// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "../hMailServer/resource.h"
#include "../hMailServer/hMailServer.h"

#include "../Common/Util/MessageTrace.h"

class ATL_NO_VTABLE InterfaceMessageTraceEvent :
   public CComObjectRootEx<CComSingleThreadModel>,
   public CComCoClass<InterfaceMessageTraceEvent, &CLSID_MessageTraceEvent>,
   public IDispatchImpl<IInterfaceMessageTraceEvent, &IID_IInterfaceMessageTraceEvent, &LIBID_hMailServer, /*wMajor =*/ 1, /*wMinor =*/ 0>,
   public HM::COMAuthenticator
{
public:
   InterfaceMessageTraceEvent()
   {
   }

DECLARE_REGISTRY_RESOURCEID(IDR_INTERFACEMESSAGETRACEEVENT)

BEGIN_COM_MAP(InterfaceMessageTraceEvent)
   COM_INTERFACE_ENTRY(IInterfaceMessageTraceEvent)
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

   // A snapshot. A trace event is immutable by nature - it records something that
   // has already happened - so there is nothing to re-read and nothing to save.
   void Attach(const HM::MessageTraceEvent &traceEvent);

public:

   STDMETHOD(get_ID)(LONG* pVal);
   STDMETHOD(get_QueueID)(LONG* pVal);
   STDMETHOD(get_OccurredTime)(BSTR* pVal);
   STDMETHOD(get_EventName)(BSTR* pVal);
   STDMETHOD(get_Sender)(BSTR* pVal);
   STDMETHOD(get_Recipient)(BSTR* pVal);
   STDMETHOD(get_SourceIP)(BSTR* pVal);
   STDMETHOD(get_StatusCode)(LONG* pVal);
   STDMETHOD(get_Detail)(BSTR* pVal);

private:

   HM::MessageTraceEvent event_;
};

OBJECT_ENTRY_AUTO(__uuidof(MessageTraceEvent), InterfaceMessageTraceEvent)
