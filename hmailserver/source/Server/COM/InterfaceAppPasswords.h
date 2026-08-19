// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#pragma once

#include "../hMailServer/resource.h"
#include "../hMailServer/hMailServer.h"

#include "../Common/BO/AppPasswords.h"

class ATL_NO_VTABLE InterfaceAppPasswords :
   public CComObjectRootEx<CComSingleThreadModel>,
   public CComCoClass<InterfaceAppPasswords, &CLSID_AppPasswords>,
   public IDispatchImpl<IInterfaceAppPasswords, &IID_IInterfaceAppPasswords, &LIBID_hMailServer, /*wMajor =*/ 1, /*wMinor =*/ 0>,
   public HM::COMAuthenticator

{
public:
   InterfaceAppPasswords() :
      account_id_(0)
   {
   }

DECLARE_REGISTRY_RESOURCEID(IDR_INTERFACEAPPPASSWORDS)

BEGIN_COM_MAP(InterfaceAppPasswords)
   COM_INTERFACE_ENTRY(IInterfaceAppPasswords)
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

   void Attach(std::shared_ptr<HM::AppPasswords> passwords, __int64 accountID);

public:

   STDMETHOD(get_Count)(LONG* pVal);

   STDMETHOD(get_ItemByDBID)(/*[in]*/ long DBID, /*[out, retval]*/ IInterfaceAppPassword** pVal);
   STDMETHOD(get_Item)(/*[in]*/ long Index, /*[out, retval]*/ IInterfaceAppPassword** pVal);
   STDMETHOD(Refresh)();
   STDMETHOD(Delete)(/*[in]*/ long Index);
   STDMETHOD(DeleteByDBID)(/*[in]*/ long DBID);

   STDMETHOD(Add)(/*[out, retval]*/ IInterfaceAppPassword** pVal);

private:

   std::shared_ptr<HM::AppPasswords> app_passwords_;

   // Held separately because the collection's own account id is only set by Refresh,
   // and Add has to stamp a new row with it before anything has been refreshed.
   __int64 account_id_;
};

OBJECT_ENTRY_AUTO(__uuidof(AppPasswords), InterfaceAppPasswords)
