// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"

#include "InterfaceApplication.h"
#include "InterfaceAccount.h"
#include "InterfaceDomains.h"
#include "InterfaceDatabase.h"
#include "InterfaceDiagnostics.h"
#include "InterfaceStatus.h"
#include "InterfaceUtilities.h"
#include "InterfaceRules.h"
#include "InterfaceSettings.h"
#include "InterfaceBackupManager.h"
#include "InterfaceGlobalObjects.h"
#include "InterfaceLinks.h"

#include "COMError.h"

#include "../Common/Application/IniFileSettings.h"

#include "../Common/Util/ServiceManager.h"
#include "../Common/Util/ServerStatus.h"

#include "../Common/BO/Rules.h"
#include "../Common/BO/Domains.h"

#include "COMAuthentication.h"

STDMETHODIMP InterfaceApplication::InterfaceSupportsErrorInfo(REFIID riid)
{
   static const IID* arr[] = 
   {
      &IID_IInterfaceApplication,
   };

   for (int i=0;i<sizeof(arr)/sizeof(arr[0]);i++)
   {
      if (InlineIsEqualGUID(*arr[i],riid))
         return S_OK;
   }

   return S_FALSE;
}

InterfaceApplication::InterfaceApplication()
{
   authentication_ = std::shared_ptr<HM::COMAuthentication>(new HM::COMAuthentication);

   authentication_->AttempAnonymousAuthentication();
}

STDMETHODIMP InterfaceApplication::Start()
{
   try
   {
      // Start the server threads.
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();
   
      if (!HM::Application::Instance()->StartServers())
         return COMError::GenerateError("Server start failed. Please check the hMailServer error log.");
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceApplication::Stop()
{
   try
   {
      // Stop the server threads.
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();
       
      HM::Application::Instance()->StopServers();
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceApplication::Reinitialize()
{
   try
   {
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();
   
      HM::String sErrorMessage = HM::Application::Instance()->Reinitialize();
      if (!sErrorMessage.IsEmpty())
         return COMError::GenerateError(sErrorMessage);
      
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceApplication::get_Settings(IInterfaceSettings **pVal)
{
   try
   {
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();
   
      HRESULT hResult = EnsureDatabaseConnectivity_();
      if (hResult != S_OK)
         return hResult;
   
      CComObject<InterfaceSettings>* pInterfaceSettings = new CComObject<InterfaceSettings>;
      pInterfaceSettings->SetAuthentication(authentication_);
      pInterfaceSettings->LoadSettings();
   
      pInterfaceSettings->AddRef();
      *pVal = pInterfaceSettings;
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceApplication::get_BackupManager(IInterfaceBackupManager **pVal)
{
   try
   {
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();
   
      HRESULT hResult = EnsureDatabaseConnectivity_();
      if (hResult != S_OK)
         return hResult;
   
      CComObject<InterfaceBackupManager>* pInterfaceBackupManager = new CComObject<InterfaceBackupManager>;
      pInterfaceBackupManager->SetAuthentication(authentication_);
      if (!pInterfaceBackupManager->LoadSettings())
         return COMError::GenerateError("Backup manager not available");
        
      pInterfaceBackupManager->AddRef();
      *pVal = pInterfaceBackupManager;
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceApplication::get_GlobalObjects(IInterfaceGlobalObjects **pVal)
{
   try
   {
      HRESULT hResult = EnsureDatabaseConnectivity_();
      if (hResult != S_OK)
         return hResult;

      CComObject<InterfaceGlobalObjects>* pInterfaceGlobalObjects = new CComObject<InterfaceGlobalObjects>;
      
      pInterfaceGlobalObjects->SetAuthentication(authentication_);
      pInterfaceGlobalObjects->AddRef();
      *pVal = pInterfaceGlobalObjects;
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceApplication::get_Domains(IInterfaceDomains **pVal)
{
   try
   {
      if (!authentication_->GetIsAuthenticated())
         return authentication_->GetAccessDenied();
   
      HRESULT hResult = EnsureDatabaseConnectivity_();
      if (hResult != S_OK)
         return hResult;
   
   
      CComObject<InterfaceDomains>* pInterfaceDomains = new CComObject<InterfaceDomains>;
   
      pInterfaceDomains->SetAuthentication(authentication_);
      pInterfaceDomains->Refresh();
   
      pInterfaceDomains->AddRef();
      *pVal = pInterfaceDomains;
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceApplication::get_ServerState(eServerState *pVal)
{
   try
   {
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();
   
      HM::ServerStatus::ServerState iState = (HM::ServerStatus::ServerState) HM::ServerStatus::Instance()->GetState();
   
      switch (iState)
      {
      case HM::ServerStatus::StateStarting:
         *pVal = hStateStarting;
         break;
      case HM::ServerStatus::StateRunning:
         *pVal = hStateRunning;
         break;
      case HM::ServerStatus::StateStopping:
         *pVal = hStateStopping;
         break;
      case HM::ServerStatus::StateStopped:
         *pVal = hStateStopped;
         break;
      default:
         *pVal = hStateUnknown;
         break;
      }
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceApplication::get_Database(IInterfaceDatabase **pVal)
{
   try
   {
      CComObject<InterfaceDatabase>* pInterfaceDatabase = new CComObject<InterfaceDatabase>;
   
      pInterfaceDatabase->SetAuthentication(authentication_);
      pInterfaceDatabase->LoadSettings();
   
      pInterfaceDatabase->AddRef();
      *pVal = pInterfaceDatabase;
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP 
InterfaceApplication::get_Status(IInterfaceStatus **pVal)
{
   try
   {
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();
   
      CComObject<InterfaceStatus>* pStatus = new CComObject<InterfaceStatus>;
   
      pStatus->SetAuthentication(authentication_);
      pStatus->LoadSettings();
   
      pStatus->AddRef();
      *pVal = pStatus;
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceApplication::SubmitEMail()
{
   try
   {
      DWORD SubmitEmailOpCode = 200;
   
      HM::ServiceManager pServiceManager;
      pServiceManager.UserControlService("hMailServer", SubmitEmailOpCode);
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceApplication::Connect()
{
   try
   {
      HM::String sErrorMessage = HM::Application::Instance()->GetLastErrorMessage();
      if (!sErrorMessage.IsEmpty())
         return COMError::GenerateError(sErrorMessage);
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceApplication::AuthenticateWithCode(BSTR sUsername, BSTR sPassword, BSTR sCode, IInterfaceAccount **pVal)
{
   try
   {
      // The one entry point that can satisfy a second factor. An account with one
      // enrolled is refused by Authenticate above, and by every mail protocol, because
      // none of them has anywhere to present a code - which is what app passwords are
      // for. See PasswordValidator::ValidatePassword.
      std::shared_ptr<const HM::Account> pAccount = authentication_->Authenticate(sUsername, sPassword, sCode);

      if (pAccount)
      {
         std::shared_ptr<HM::Account> accountCopy = std::shared_ptr<HM::Account>(new HM::Account(*pAccount.get()));

         CComObject<InterfaceAccount>* pAccountInt = new CComObject<InterfaceAccount>();

         pAccountInt->AttachItem(accountCopy);
         pAccountInt->SetAuthentication(authentication_);
         pAccountInt->AddRef();

         *pVal = pAccountInt;
      }

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceApplication::Authenticate(BSTR sUsername, BSTR sPassword, IInterfaceAccount **pVal)
{
   try
   {
      // Authenticates the user and returns the account object.
      std::shared_ptr<const HM::Account> pAccount = authentication_->Authenticate(sUsername, sPassword);
   
      if (pAccount)
      {
         std::shared_ptr<HM::Account> accountCopy = std::shared_ptr<HM::Account>(new HM::Account(*pAccount.get()));
   
         // Return the account.
         CComObject<InterfaceAccount>* pAccountInt = new CComObject<InterfaceAccount>();
   
         pAccountInt->AttachItem(accountCopy);
         pAccountInt->SetAuthentication(authentication_);
         pAccountInt->AddRef();
   
         *pVal = pAccountInt;
   
      }
      
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceApplication::get_AdministratorTOTPEnabled(VARIANT_BOOL *pVal)
{
   try
   {
      // Deliberately readable without authenticating: a client has to know
      // whether to ask for a code before it can present the credential, and
      // "this account has a second factor" is what every logon form reveals the
      // moment the password is accepted anyway. The secret itself is never
      // readable from anywhere.
      *pVal = HM::IniFileSettings::Instance()->GetAdministratorTotpSecret().IsEmpty() ? VARIANT_FALSE : VARIANT_TRUE;

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceApplication::get_Version(BSTR *pVal)
{
   try
   {
      *pVal = HM::Application::Instance()->GetVersionNumber().AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceApplication::get_VersionArchitecture(BSTR *pVal)
{
   try
   {
      *pVal = HM::Application::Instance()->GetVersionArchitecture().AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceApplication::get_Utilities(IInterfaceUtilities **pVal)
{
   try
   {
      CComObject<InterfaceUtilities>* pUtilities = new CComObject<InterfaceUtilities>;
   
      pUtilities->SetAuthentication(authentication_);
      pUtilities->AddRef();
      *pVal = pUtilities;
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceApplication::get_InitializationFile(BSTR *pVal)
{
   try
   {
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();
   
      *pVal = HM::IniFileSettings::Instance()->GetInitializationFile().AllocSysString();
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceApplication::get_Rules(IInterfaceRules **pVal)
{
   try
   {
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();
   
      HRESULT hResult = EnsureDatabaseConnectivity_();
      if (hResult != S_OK)
         return hResult;
   
      CComObject<InterfaceRules >* pItem = new CComObject<InterfaceRules >();
      pItem->SetAuthentication(authentication_);
   
      std::shared_ptr<HM::Rules> pRules = std::shared_ptr<HM::Rules>(new HM::Rules(0));
   
      if (pRules)
      {
         pRules->Refresh();
         pItem->Attach(pRules);
         pItem->AddRef();
         *pVal = pItem;
      }
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

HRESULT
InterfaceApplication::EnsureDatabaseConnectivity_()
{
   std::shared_ptr<HM::DatabaseConnectionManager> pConnectionManager = HM::Application::Instance()->GetDBManager();
   if (!pConnectionManager || !pConnectionManager->GetIsConnected())
   {
      return COMError::GenerateError("The connection to the database is not available. Please check the hMailServer error log for details.");
   }

   // Connected is not enough. A database this server opened and then refused -
   // its schema older or newer than this build requires (HM5011), or its
   // version unreadable (HM5010) - stays open for hMailServer.Database, which
   // is how DBUpdater brings it up to date, and nothing else was loaded:
   // Configuration::Load never ran. The objects the getters hand out read the
   // configuration's property set on every call, and until 12 September 2026
   // that was an access violation inside the call, reported to the client as
   // "an error occurred processing the request" and to the crash oracle as a
   // memory fault. The refusal's own message says what to do instead.
   if (!HM::Application::Instance()->IsInitialized())
   {
      HM::String reason = HM::Application::Instance()->GetLastErrorMessage();
      if (reason.IsEmpty())
         reason = "The server has not finished starting.";

      return COMError::GenerateError(HM::String("The server has not loaded its configuration. ") + reason);
   }

   return S_OK;
}

STDMETHODIMP InterfaceApplication::get_Links(IInterfaceLinks **pVal)
{
   try
   {
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();
   
      HRESULT hResult = EnsureDatabaseConnectivity_();
      if (hResult != S_OK)
         return hResult;
   
      CComObject<InterfaceLinks>* pItem = new CComObject<InterfaceLinks >();
      pItem->SetAuthentication(authentication_);
      pItem->AddRef();
      *pVal = pItem;
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceApplication::get_Diagnostics(IInterfaceDiagnostics **pVal)
{
   try
   {
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();
   
      HRESULT hResult = EnsureDatabaseConnectivity_();
      if (hResult != S_OK)
         return hResult;

      CComObject<InterfaceDiagnostics>* pInterfaceDiagnostics = new CComObject<InterfaceDiagnostics>;
   
      pInterfaceDiagnostics->SetAuthentication(authentication_);
      pInterfaceDiagnostics->AddRef();
   
      *pVal = pInterfaceDiagnostics;
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

