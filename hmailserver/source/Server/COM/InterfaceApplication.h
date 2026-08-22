// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once
#include "../hMailServer/resource.h"       // main symbols

#include "../hMailServer/hMailServer.h"


namespace HM
{
   class COMAuthentication;
}

class ATL_NO_VTABLE InterfaceApplication : 
	public CComObjectRootEx<CComSingleThreadModel>,
	public CComCoClass<InterfaceApplication, &CLSID_Application>,
   public ISupportErrorInfo,
	public IDispatchImpl<IInterfaceApplication, &IID_IInterfaceApplication, &LIBID_hMailServer, /*wMajor =*/ 1, /*wMinor =*/ 0>
{
public:
	InterfaceApplication();


DECLARE_REGISTRY_RESOURCEID(IDR_INTERFACEAPPLICATION)


BEGIN_COM_MAP(InterfaceApplication)
	COM_INTERFACE_ENTRY(IInterfaceApplication)
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

public:

   STDMETHOD(InterfaceSupportsErrorInfo)(REFIID riid);

   STDMETHOD(Connect)();
   STDMETHOD(Authenticate)(BSTR sUsername, BSTR sPassword, IInterfaceAccount **pVal);
   STDMETHOD(AuthenticateWithCode)(BSTR sUsername, BSTR sPassword, BSTR sCode, IInterfaceAccount **pVal);
   STDMETHOD(SubmitEMail)();
   STDMETHOD(Stop)();
   STDMETHOD(Start)();
   STDMETHOD(Reinitialize)();

   STDMETHOD(get_Database)(/*[out, retval]*/ IInterfaceDatabase **pVal);
   STDMETHOD(get_Domains)(/*[out, retval]*/ IInterfaceDomains **pVal);
   STDMETHOD(get_Utilities)(/*[out, retval]*/ IInterfaceUtilities * *pVal);
   STDMETHOD(get_Settings)(/*[out, retval]*/ IInterfaceSettings** pVal);
   STDMETHOD(get_Status)(/*[out, retval]*/ IInterfaceStatus** pVal);
   STDMETHOD(get_ServerState)(eServerState *pVal);
   STDMETHOD(get_Version)(/*[out, retval]*/ BSTR *pVal);
   STDMETHOD(get_VersionArchitecture)(/*[out, retval]*/ BSTR *pVal);
   STDMETHOD(get_InitializationFile)(/*[out, retval]*/ BSTR *pVal);
   STDMETHOD(get_BackupManager)(/*[out, retval]*/ IInterfaceBackupManager** pVal);
   STDMETHOD(get_GlobalObjects)(/*[out, retval]*/ IInterfaceGlobalObjects** pVal);
   STDMETHOD(get_Links)(/*[out, retval]*/ IInterfaceLinks** pVal);
   STDMETHOD(get_Diagnostics)(/*[out, retval]*/ IInterfaceDiagnostics** pVal);

   STDMETHOD(get_Rules)(/*[out, retval]*/ IInterfaceRules **pVal);

private:
   HRESULT EnsureDatabaseConnectivity_();
   std::shared_ptr<HM::COMAuthentication> authentication_;
};

OBJECT_ENTRY_AUTO(__uuidof(Application), InterfaceApplication)
