// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once
#include "../hMailServer/resource.h"       // main symbols

#include "../hMailServer/hMailServer.h"
#include "COMCollection.h"

namespace HM
{
   class DistributionList;
   class DistributionLists;
}

// InterfaceDistributionList

class ATL_NO_VTABLE InterfaceDistributionList : 
   public COMCollectionItem<HM::DistributionList, HM::DistributionLists>,
   public CComObjectRootEx<CComSingleThreadModel>,
   public CComCoClass<InterfaceDistributionList, &CLSID_DistributionList>,
   public IDispatchImpl<IInterfaceDistributionList, &IID_IInterfaceDistributionList, &LIBID_hMailServer, /*wMajor =*/ 1, /*wMinor =*/ 0>,
   public HM::COMAuthenticator,
   public ISupportErrorInfo
{
public:
   InterfaceDistributionList()
   {
#ifdef _DEBUG
      InterlockedIncrement(&counter);
#endif
   }

   ~InterfaceDistributionList()
   {
#ifdef _DEBUG
      InterlockedDecrement(&counter);
#endif
   }

DECLARE_REGISTRY_RESOURCEID(IDR_INTERFACEDISTRIBUTIONLIST)


BEGIN_COM_MAP(InterfaceDistributionList)
   COM_INTERFACE_ENTRY(IInterfaceDistributionList)
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

   STDMETHOD(get_ID)(/*[out, retval]*/ long *pVal);
   STDMETHOD(Delete)();
   STDMETHOD(Save)();
   STDMETHOD(get_Active)(/*[out, retval]*/ VARIANT_BOOL *pVal);
   STDMETHOD(put_Active)(/*[in]*/ VARIANT_BOOL newVal);
   STDMETHOD(get_Recipients)(/*[out, retval]*/ IInterfaceDistributionListRecipients **pVal);

   STDMETHOD(get_RequireSMTPAuth)(/*[out, retval]*/ VARIANT_BOOL *pVal);
   STDMETHOD(put_RequireSMTPAuth)(/*[in]*/ VARIANT_BOOL newVal);

   STDMETHOD(get_Address)(/*[out, retval]*/ BSTR *pVal);
   STDMETHOD(put_Address)(/*[in]*/ BSTR newVal);

   STDMETHOD(get_RequireSenderAddress)(/*[out, retval]*/ BSTR *pVal);
   STDMETHOD(put_RequireSenderAddress)(/*[in]*/ BSTR newVal);

   STDMETHOD(get_Mode)(/*[out, retval]*/ eDistributionListMode *pVal);
   STDMETHOD(put_Mode)(/*[in]*/ eDistributionListMode newVal);

   // Declared ahead of the IDL: IInterfaceDistributionList does not carry these
   // yet, so until hMailServer.idl gains the ModeratorAddress/BounceAddress
   // properties (dispids 11 and 12 are free) and the type library is regenerated,
   // these are plain virtual functions no COM client can reach. The moment the
   // IDL catches up they become the implementations, with no change here.
   STDMETHOD(get_ModeratorAddress)(/*[out, retval]*/ BSTR *pVal);
   STDMETHOD(put_ModeratorAddress)(/*[in]*/ BSTR newVal);

   STDMETHOD(get_BounceAddress)(/*[out, retval]*/ BSTR *pVal);
   STDMETHOD(put_BounceAddress)(/*[in]*/ BSTR newVal);

private:

#ifdef _DEBUG
   static long counter;
#endif
};

OBJECT_ENTRY_AUTO(__uuidof(DistributionList), InterfaceDistributionList)
