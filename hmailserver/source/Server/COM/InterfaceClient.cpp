// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "COMError.h"
#include "..\COM\InterfaceClient.h"

#include "../Common/Scripting/ClientInfo.h"

namespace
{
   // Client is a registered coclass with a ProgID of its own, so it can be created
   // directly rather than received from the script engine - and one created that way
   // has no session attached. Every accessor below dereferenced the empty shared_ptr.
   // See the same comment in InterfaceResult.cpp: /EHa is the only reason that read
   // turned into a COM error rather than the end of the service process.
   HRESULT NotAttached_()
   {
      return COMError::GenerateError("This Client object is not attached to a session. The Client object is handed to a script event handler by the server; it cannot be created directly.");
   }
}

// InterfaceClient

void 
InterfaceClient::AttachItem(std::shared_ptr<HM::ClientInfo> pClientInfo)
{
   client_info_ = pClientInfo;
}

STDMETHODIMP InterfaceClient::get_Port(long *pVal)
{
   try
   {
      if (!client_info_)
         return NotAttached_();

      if (!pVal)
         return E_POINTER;

      *pVal = client_info_->GetPort();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceClient::get_IPAddress(BSTR *pVal)
{
   try
   {
      if (!client_info_)
         return NotAttached_();

      if (!pVal)
         return E_POINTER;

      *pVal = client_info_->GetIPAddress().AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceClient::get_Username(BSTR *pVal)
{
   try
   {
      if (!client_info_)
         return NotAttached_();

      if (!pVal)
         return E_POINTER;

      *pVal = client_info_->GetUsername().AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceClient::get_HELO(BSTR *pVal)
{
   try
   {
      if (!client_info_)
         return NotAttached_();

      if (!pVal)
         return E_POINTER;

      *pVal = client_info_->GetHELO().AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceClient::get_Authenticated(VARIANT_BOOL *pVal)
{
   try
   {
      if (!client_info_)
         return NotAttached_();

      if (!pVal)
         return E_POINTER;

      *pVal = client_info_->GetIsAuthenticated() ? VARIANT_TRUE : VARIANT_FALSE;
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceClient::get_EncryptedConnection(VARIANT_BOOL* pVal)
{
   try
   {
      if (!client_info_)
         return NotAttached_();

      if (!pVal)
         return E_POINTER;

      *pVal = client_info_->GetIsEncryptedConnection() ? VARIANT_TRUE : VARIANT_FALSE;
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceClient::get_CipherVersion(BSTR* pVal)
{
   try
   {
      if (!client_info_)
         return NotAttached_();

      if (!pVal)
         return E_POINTER;

      *pVal = client_info_->GetCipherVersion().AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceClient::get_CipherName(BSTR* pVal)
{
   try
   {
      if (!client_info_)
         return NotAttached_();

      if (!pVal)
         return E_POINTER;

      *pVal = client_info_->GetCipherName().AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceClient::get_CipherBits(long* pVal)
{
   try
   {
      if (!client_info_)
         return NotAttached_();

      if (!pVal)
         return E_POINTER;

      *pVal = client_info_->GetCipherBits();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceClient::get_SessionID(long* pVal)
{
   try
   {
      if (!client_info_)
         return NotAttached_();

      if (!pVal)
         return E_POINTER;

      *pVal = client_info_->GetSessionID();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}
