// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#pragma once

#include "../hMailServer/resource.h"
#include "../hMailServer/hMailServer.h"

#include "../Common/Util/MessageTrace.h"

class ATL_NO_VTABLE InterfaceMessageTrace :
   public CComObjectRootEx<CComSingleThreadModel>,
   public CComCoClass<InterfaceMessageTrace, &CLSID_MessageTrace>,
   public IDispatchImpl<IInterfaceMessageTrace, &IID_IInterfaceMessageTrace, &LIBID_hMailServer, /*wMajor =*/ 1, /*wMinor =*/ 0>,
   public HM::COMAuthenticator
{
public:
   InterfaceMessageTrace()
   {
   }

DECLARE_REGISTRY_RESOURCEID(IDR_INTERFACEMESSAGETRACE)

BEGIN_COM_MAP(InterfaceMessageTrace)
   COM_INTERFACE_ENTRY(IInterfaceMessageTrace)
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
   STDMETHOD(get_Item)(/*[in]*/ long Index, /*[out, retval]*/ IInterfaceMessageTraceEvent** pVal);
   STDMETHOD(Search)(/*[in]*/ BSTR Address);
   STDMETHOD(SearchByQueueID)(/*[in]*/ long QueueID);
   STDMETHOD(DeleteExpired)(/*[out, retval]*/ LONG* pVal);

private:

   // There is no Refresh here, and no ItemByDBID either, and both absences are
   // deliberate. A trace is asked a QUESTION - "what happened to this address", or
   // "tell me about this queue id" - so the two Search methods are the only ways to
   // load it, and neither has a meaningful "reload the same thing" partner. An
   // individual event has no identity worth addressing on its own: it is one line of
   // an answer, not a record somebody acts on.
   static const int max_listed_ = 500;

   std::vector<HM::MessageTraceEvent> events_;
};

OBJECT_ENTRY_AUTO(__uuidof(MessageTrace), InterfaceMessageTrace)
