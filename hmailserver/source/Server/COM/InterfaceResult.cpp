// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "COMError.h"
#include "..\COM\InterfaceResult.h"

namespace
{
   // Result is a registered coclass with a ProgID of its own, so anything that can
   // activate the server's COM objects - including any event script, which reaches the
   // whole object model through CreateObject - can create one directly instead of
   // using the one the script engine puts in scope. Such an object has no Result
   // attached, and every accessor here dereferenced the empty shared_ptr.
   //
   // The reason that read did not end the service process is that the server builds
   // with /EHa, so the catch (...) in each accessor caught the access violation and
   // turned it into a generic COM error. It is still an access violation, the crash
   // oracle records it as one, and relying on it being caught is not a design.
   HRESULT NotAttached_()
   {
      return COMError::GenerateError("This Result object is not attached to an event. The Result object is handed to a script event handler by the server; it cannot be created directly.");
   }
}

// InterfaceResult

void 
InterfaceResult::AttachItem(std::shared_ptr<HM::Result> pResult)
{
   result_ = pResult;
}

STDMETHODIMP InterfaceResult::get_Value(long *pVal)
{
   try
   {
      if (!result_)
         return NotAttached_();

      if (!pVal)
         return E_POINTER;

      *pVal = result_->GetValue();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceResult::put_Value(long newVal)
{
   try
   {
      if (!result_)
         return NotAttached_();

      result_->SetValue(newVal);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceResult::get_Parameter(long *pVal)
{
   try
   {
      if (!result_)
         return NotAttached_();

      if (!pVal)
         return E_POINTER;

      *pVal = result_->GetParameter();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceResult::put_Parameter(long newVal)
{
   try
   {
      if (!result_)
         return NotAttached_();

      result_->SetParameter(newVal);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceResult::get_Message(BSTR *pVal)
{
   try
   {
      if (!result_)
         return NotAttached_();

      if (!pVal)
         return E_POINTER;

      *pVal = result_->GetMessage().AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceResult::put_Message(BSTR newVal)
{
   try
   {
      if (!result_)
         return NotAttached_();

      result_->SetMessage(newVal);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}


