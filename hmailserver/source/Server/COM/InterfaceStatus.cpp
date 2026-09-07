// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "COMError.h"
#include "InterfaceStatus.h"

#include "../Common/Util/ServerStatus.h"
#include "../Common/Util/UpdateChecker.h"
#include "../Common/Util/UpdateDownloader.h"

InterfaceStatus::InterfaceStatus() :
   status_(nullptr),
   application_(nullptr)
{

}


bool 
InterfaceStatus::LoadSettings()
{
   if (!GetIsServerAdmin())
      return false;

   status_ = HM::ServerStatus::Instance();
   application_ = HM::Application::Instance();

   return true;
}


STDMETHODIMP 
InterfaceStatus::get_UndeliveredMessages(BSTR *pVal)
{
   try
   {
      if (!status_)
         return GetAccessDenied();

      HM::String sRetVal = status_->GetUnsortedMessageStatus();
      *pVal = sRetVal.AllocSysString();
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP 
InterfaceStatus::get_StartTime(BSTR *pVal)
{
   try
   {
      if (!status_)
         return GetAccessDenied();

      HM::String sRetVal = application_->GetStartTime();
      *pVal = sRetVal.AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP 
InterfaceStatus::get_ProcessedMessages(long *pVal)
{
   try
   {
      if (!status_)
         return GetAccessDenied();

      *pVal = status_->GetNumberOfProcessedMessages();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP 
InterfaceStatus::get_RemovedViruses(long *pVal)
{
   try
   {
      if (!status_)
         return GetAccessDenied();

      *pVal = status_->GetNumberOfRemovedViruses();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP 
InterfaceStatus::get_RemovedSpamMessages(long *pVal)
{
   try
   {
      if (!status_)
         return GetAccessDenied();

      *pVal = status_->GetNumberOfDetectedSpamMessages();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP 
InterfaceStatus::get_SessionCount(eSessionType iType, long *pVal)
{
   try
   {
      if (!status_)
         return GetAccessDenied();

      *pVal = status_->GetNumberOfSessions(iType);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP
InterfaceStatus::get_ThreadID(long* pVal)
{
   try
   {
      if (!status_)
         return GetAccessDenied();

      *pVal = status_->GetThreadID();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

// The update check. The verdict lives in UpdateChecker, process-wide, so the
// Control Panel and the REST API read the same one; these are its view over COM.

STDMETHODIMP
InterfaceStatus::get_UpdateState(long *pVal)
{
   try
   {
      if (!status_)
         return GetAccessDenied();

      *pVal = (long) HM::UpdateChecker::Current().state;
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP
InterfaceStatus::get_AvailableVersion(BSTR *pVal)
{
   try
   {
      if (!status_)
         return GetAccessDenied();

      *pVal = HM::UpdateChecker::Current().available_version.AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP
InterfaceStatus::get_AvailableVersionPublished(BSTR *pVal)
{
   try
   {
      if (!status_)
         return GetAccessDenied();

      *pVal = HM::UpdateChecker::Current().published_at.AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP
InterfaceStatus::get_AvailableVersionUrl(BSTR *pVal)
{
   try
   {
      if (!status_)
         return GetAccessDenied();

      *pVal = HM::UpdateChecker::Current().release_url.AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP
InterfaceStatus::get_UpdateLastChecked(BSTR *pVal)
{
   try
   {
      if (!status_)
         return GetAccessDenied();

      *pVal = HM::UpdateChecker::Current().last_checked.AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP
InterfaceStatus::get_UpdateLastError(BSTR *pVal)
{
   try
   {
      if (!status_)
         return GetAccessDenied();

      *pVal = HM::UpdateChecker::Current().last_error.AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP
InterfaceStatus::CheckForUpdate(VARIANT_BOOL *pVal)
{
   try
   {
      if (!status_)
         return GetAccessDenied();

      // On the caller's thread, so that when this returns the properties are the
      // answer. A feed that cannot be read is false with UpdateLastError set, not
      // a COM error: the caller asked a question and this is the answer to it.
      HM::String error;
      *pVal = HM::UpdateChecker::CheckNow(error) ? VARIANT_TRUE : VARIANT_FALSE;
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP
InterfaceStatus::DownloadUpdate(VARIANT_BOOL *pVal)
{
   try
   {
      if (!status_)
         return GetAccessDenied();

      // On the caller's thread, as CheckForUpdate is: an installer is tens of
      // megabytes, and the caller asked to wait for the answer.
      HM::String error;
      *pVal = HM::UpdateDownloader::DownloadAndVerify(error) ? VARIANT_TRUE : VARIANT_FALSE;
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP
InterfaceStatus::get_UpdateInstallerPath(BSTR *pVal)
{
   try
   {
      if (!status_)
         return GetAccessDenied();

      *pVal = HM::UpdateChecker::Current().installer_path.AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP
InterfaceStatus::get_UpdateSignerIdentity(BSTR *pVal)
{
   try
   {
      if (!status_)
         return GetAccessDenied();

      *pVal = HM::String(HM::UpdateChecker::Current().signer_identity).AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

