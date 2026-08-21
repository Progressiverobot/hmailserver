// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#pragma once

#include "../hMailServer/resource.h"       // main symbols
#include "../hMailServer/hMailServer.h"

namespace HM
{
   class BlockedSenders;
}


#if defined(_WIN32_WCE) && !defined(_CE_DCOM) && !defined(_CE_ALLOW_SINGLE_THREADED_OBJECTS_IN_MTA)
#error "Single-threaded COM objects are not properly supported on Windows CE platform, such as the Windows Mobile platforms that do not include full DCOM support. Define _CE_ALLOW_SINGLE_THREADED_OBJECTS_IN_MTA to force ATL to support creating single-thread COM object's and allow use of it's single-threaded COM object implementations. The threading model in your rgs file was set to 'Free' as that is the only threading model supported in non DCOM Windows CE platforms."
#endif



// InterfaceBlockedSenders

class ATL_NO_VTABLE InterfaceBlockedSenders :
	public CComObjectRootEx<CComSingleThreadModel>,
	public CComCoClass<InterfaceBlockedSenders, &CLSID_BlockedSenders>,
	public IDispatchImpl<IInterfaceBlockedSenders, &IID_IInterfaceBlockedSenders, &LIBID_hMailServer, /*wMajor =*/ 1, /*wMinor =*/ 0>,
   public HM::COMAuthenticator
{
public:
	InterfaceBlockedSenders()
	{
	}

DECLARE_REGISTRY_RESOURCEID(IDR_INTERFACEBLOCKEDSENDERS)


BEGIN_COM_MAP(InterfaceBlockedSenders)
	COM_INTERFACE_ENTRY(IInterfaceBlockedSenders)
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
   STDMETHOD(Refresh)();
   STDMETHOD(Clear)();

   STDMETHOD(get_Item)(/*[in]*/ long Index, /*[out, retval]*/ IInterfaceBlockedSender **pVal);
   STDMETHOD(get_Count)(/*[out, retval]*/ long *pVal);
   STDMETHOD(get_ItemByDBID)(/*[in]*/ long lDBID, /*[out, retval]*/ IInterfaceBlockedSender** pVal);
   STDMETHOD(get_ItemByName)(/*[in]*/ BSTR sName, /*[out, retval]*/ IInterfaceBlockedSender** pVal);
   STDMETHOD(DeleteByDBID)(/*[in]*/ long DBID);
   STDMETHOD(Add)(/*[out, retval]*/ IInterfaceBlockedSender **pVal);

   void Attach(std::shared_ptr<HM::BlockedSenders> pBlockedSenders);

public:

   std::shared_ptr<HM::BlockedSenders> object_;

};

OBJECT_ENTRY_AUTO(__uuidof(BlockedSenders), InterfaceBlockedSenders)
