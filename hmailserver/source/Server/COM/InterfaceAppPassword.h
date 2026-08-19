// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#pragma once

#include "../hMailServer/resource.h"
#include "../hMailServer/hMailServer.h"
#include "../Common/BO/AppPassword.h"

#include "COMCollection.h"

namespace HM
{
   class AppPassword;
   class AppPasswords;
}

// One app password over COM.
//
// There is deliberately no way to read the secret. Generate returns the clear text
// once, at the moment it is created, and what the object holds afterwards is a hash -
// so a caller that loses it issues a new one rather than looking the old one up.
// Anything else would make the store a list of usable mailbox credentials for whoever
// can reach the API.
class ATL_NO_VTABLE InterfaceAppPassword :
   public COMCollectionItem<HM::AppPassword, HM::AppPasswords>,
   public CComObjectRootEx<CComSingleThreadModel>,
   public CComCoClass<InterfaceAppPassword, &CLSID_AppPassword>,
   public IDispatchImpl<IInterfaceAppPassword, &IID_IInterfaceAppPassword, &LIBID_hMailServer, /*wMajor =*/ 1, /*wMinor =*/ 0>,
   public HM::COMAuthenticator
{
public:

   InterfaceAppPassword();

DECLARE_REGISTRY_RESOURCEID(IDR_INTERFACEAPPPASSWORD)

BEGIN_COM_MAP(InterfaceAppPassword)
   COM_INTERFACE_ENTRY(IInterfaceAppPassword)
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

   STDMETHOD(get_ID)(LONG* pVal);
   STDMETHOD(get_Name)(BSTR* pVal);
   STDMETHOD(put_Name)(BSTR newVal);
   STDMETHOD(get_CreatedTime)(BSTR* pVal);
   STDMETHOD(get_LastUsedTime)(BSTR* pVal);
   STDMETHOD(get_Active)(VARIANT_BOOL* pVal);
   STDMETHOD(put_Active)(VARIANT_BOOL newVal);
   STDMETHOD(Generate)(BSTR* pVal);
   STDMETHOD(SetPassword)(BSTR Password);
   STDMETHOD(Save)();
   STDMETHOD(Delete)();
};

OBJECT_ENTRY_AUTO(__uuidof(AppPassword), InterfaceAppPassword)
