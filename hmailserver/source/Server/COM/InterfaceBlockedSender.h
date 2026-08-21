// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#pragma once

#include "../hMailServer/resource.h"       // main symbols
#include "../hMailServer/hMailServer.h"

#include "COMCollection.h"

#include "..\Common\BO\BlockedSender.h"
#include "..\Common\BO\BlockedSenders.h"

#if defined(_WIN32_WCE) && !defined(_CE_DCOM) && !defined(_CE_ALLOW_SINGLE_THREADED_OBJECTS_IN_MTA)
#error "Single-threaded COM objects are not properly supported on Windows CE platform, such as the Windows Mobile platforms that do not include full DCOM support. Define _CE_ALLOW_SINGLE_THREADED_OBJECTS_IN_MTA to force ATL to support creating single-thread COM object's and allow use of it's single-threaded COM object implementations. The threading model in your rgs file was set to 'Free' as that is the only threading model supported in non DCOM Windows CE platforms."
#endif


// InterfaceBlockedSender

class ATL_NO_VTABLE InterfaceBlockedSender :
   public COMCollectionItem<HM::BlockedSender, HM::BlockedSenders>,
	public CComObjectRootEx<CComSingleThreadModel>,
	public CComCoClass<InterfaceBlockedSender, &CLSID_BlockedSender>,
	public IDispatchImpl<IInterfaceBlockedSender, &IID_IInterfaceBlockedSender, &LIBID_hMailServer, /*wMajor =*/ 1, /*wMinor =*/ 0>,
   public HM::COMAuthenticator
{
public:
	InterfaceBlockedSender()
	{
	}

DECLARE_REGISTRY_RESOURCEID(IDR_INTERFACEBLOCKEDSENDER)


BEGIN_COM_MAP(InterfaceBlockedSender)
	COM_INTERFACE_ENTRY(IInterfaceBlockedSender)
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
   STDMETHOD(Save)();
   STDMETHOD(Delete)();

   STDMETHOD(get_ID)(/*[out, retval]*/ long *pVal);

   STDMETHOD(get_Address)(/*[out, retval]*/ BSTR *pVal);
   STDMETHOD(put_Address)(/*[in]*/ BSTR newVal);
   STDMETHOD(get_Score)(/*[out, retval]*/ long *pVal);
   STDMETHOD(put_Score)(/*[in]*/ long newVal);
   STDMETHOD(get_Description)(/*[out, retval]*/ BSTR *pVal);
   STDMETHOD(put_Description)(/*[in]*/ BSTR newVal);

};

OBJECT_ENTRY_AUTO(__uuidof(BlockedSender), InterfaceBlockedSender)
