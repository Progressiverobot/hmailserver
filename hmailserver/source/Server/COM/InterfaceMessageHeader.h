// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "../hMailServer/resource.h"       // main symbols
#include "../hMailServer/hMailServer.h"

#if defined(_WIN32_WCE) && !defined(_CE_DCOM) && !defined(_CE_ALLOW_SINGLE_THREADED_OBJECTS_IN_MTA)
#error "Single-threaded COM objects are not properly supported on Windows CE platform, such as the Windows Mobile platforms that do not include full DCOM support. Define _CE_ALLOW_SINGLE_THREADED_OBJECTS_IN_MTA to force ATL to support creating single-thread COM object's and allow use of it's single-threaded COM object implementations. The threading model in your rgs file was set to 'Free' as that is the only threading model supported in non DCOM Windows CE platforms."
#endif

namespace HM
{
   class MimeHeader;
   class MimeField; 
}

// InterfaceMessageHeader

class ATL_NO_VTABLE InterfaceMessageHeader :
	public CComObjectRootEx<CComSingleThreadModel>,
	public CComCoClass<InterfaceMessageHeader, &CLSID_MessageHeader>,
	public IDispatchImpl<IInterfaceMessageHeader, &IID_IInterfaceMessageHeader, &LIBID_hMailServer, /*wMajor =*/ 1, /*wMinor =*/ 0>
{
public:
   InterfaceMessageHeader();
	
   void AttachItem (std::shared_ptr<HM::MimeHeader> pHeader, HM::MimeField *pField);

DECLARE_REGISTRY_RESOURCEID(IDR_INTERFACEMESSAGEHEADER)


BEGIN_COM_MAP(InterfaceMessageHeader)
	COM_INTERFACE_ENTRY(IInterfaceMessageHeader)
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
   STDMETHOD(Delete)();

   STDMETHOD(get_Name)(/*[out, retval]*/ BSTR *pVal);
   STDMETHOD(put_Name)(/*[in]*/ BSTR newVal);
   STDMETHOD(get_Value)(/*[out, retval]*/ BSTR *pVal);
   STDMETHOD(put_Value)(/*[in]*/ BSTR newVal);

private:

   HM::MimeField *ResolveField_();
   // Looks the field up in the collection as it stands right now. Returns NULL if it
   // is no longer there. See the comment at the definition: this object must not
   // cache a MimeField*, because a MimeHeader keeps its fields in a vector by value.

   HRESULT ReportUnavailable_();
   // Refuses a call whose field could not be resolved, with a description the caller
   // can act on.

   static bool IsValidFieldName_(const HM::AnsiString &name);
   // Whether name is an RFC 5322 field name: at least one character, all of them
   // printable US-ASCII other than the colon.

   std::shared_ptr<HM::MimeHeader> header_;

   // The name of the field, and where it was when this object was handed out.
   //
   // Deliberately not a MimeField*. MimeHeader stores its fields in a
   // std::vector<MimeField> *by value*, so adding a field can reallocate the vector
   // and deleting one shifts its successors down over it and destroys the final
   // slot. Any raw pointer into that vector is invalidated by either. The position
   // is a hint that is re-checked against the name before it is used, never
   // dereferenced blind.
   HM::AnsiString field_name_;
   int field_index_;

   // Set once Delete has removed the field. Neither the position nor the name
   // identifies it any more, so every later call is refused rather than allowed to
   // resolve onto some other field that happens to share the name.
   bool deleted_;

};

OBJECT_ENTRY_AUTO(__uuidof(MessageHeader), InterfaceMessageHeader)
