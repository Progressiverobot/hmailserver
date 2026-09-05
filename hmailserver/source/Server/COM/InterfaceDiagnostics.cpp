// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "COMError.h"
#include "InterfaceDiagnostics.h"
#include "InterfaceDiagnosticResults.h"


// InterfaceDiagnostics

STDMETHODIMP InterfaceDiagnostics::PerformTests(IInterfaceDiagnosticResults **pVal)
{
   try
   {
      if (!authentication_)
         return GetAccessDenied();

      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();
   
      HM::String str;
      
      std::vector<HM::DiagnosticResult> results = diagnostics_.PerformTests();
      
      CComObject<InterfaceDiagnosticResults>* pResult = new CComObject<InterfaceDiagnosticResults>();
      pResult->SetAuthentication(authentication_);
      pResult->AttachResults(results);
      pResult->AddRef();
      *pVal = pResult;
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceDiagnostics::get_LocalDomainName(BSTR *pVal)
{
   try
   {
      if (!authentication_)
         return GetAccessDenied();

      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();
   
      *pVal = diagnostics_.GetLocalDomain().AllocSysString();
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceDiagnostics::put_LocalDomainName(BSTR newVal)
{
   try
   {
      if (!authentication_)
         return GetAccessDenied();

      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();
   
      HM::String localDomainName = newVal;
      diagnostics_.SetLocalDomain(localDomainName);
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceDiagnostics::get_TestDomainName(BSTR *pVal)
{
   try
   {
      if (!authentication_)
         return GetAccessDenied();

      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();
   
      *pVal = diagnostics_.GetTestDomain().AllocSysString();
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceDiagnostics::put_TestDomainName(BSTR newVal)
{
   try
   {
      if (!authentication_)
         return GetAccessDenied();

      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();
   
      HM::String TestDomainName = newVal;
      diagnostics_.SetTestDomain(TestDomainName);
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}


STDMETHODIMP InterfaceDiagnostics::get_AssertionsEnabled(VARIANT_BOOL *pVal)
{
   try
   {
      // Readable without authenticating: it says which kind of binary this is,
      // not anything about its data, and the regression suite reads it to know
      // what TriggerAssertion below must be expected to do.
#if defined(HM_KEEP_ASSERTIONS)
      *pVal = VARIANT_TRUE;
#else
      *pVal = VARIANT_FALSE;
#endif
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceDiagnostics::TriggerAssertion()
{
   try
   {
      if (!authentication_)
         return GetAccessDenied();

      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();

      // Only the reporting build does anything here. In Debug HM_ASSERT is the
      // CRT assert, which would abort the server, and that is not a diagnostic
      // anybody asks for over COM.
#if defined(HM_KEEP_ASSERTIONS)
      HM_ASSERT(false && "Diagnostics.TriggerAssertion: an assertion failed on purpose");
#endif
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}
