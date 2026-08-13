// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "COMError.h"
#include "InterfaceScripting.h"

#include "..\Common\Scripting\ScriptServer.h"

InterfaceScripting::InterfaceScripting() :
   config_(nullptr),
   ini_file_settings_(nullptr)
{ 

}

bool 
InterfaceScripting::LoadSettings()
{
   if (!GetIsServerAdmin())
      return false;

   config_ = HM::Configuration::Instance();
   ini_file_settings_ = HM::IniFileSettings::Instance();

   return true;
}

STDMETHODIMP InterfaceScripting::get_Enabled(VARIANT_BOOL *pVal)
{
   try
   {
      if (!ini_file_settings_)
         return GetAccessDenied();

      *pVal = config_->GetUseScriptServer() ? VARIANT_TRUE : VARIANT_FALSE;
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceScripting::put_Enabled(VARIANT_BOOL newVal)
{
   try
   {
      if (!ini_file_settings_)
         return GetAccessDenied();

      config_->SetUseScriptServer(newVal == VARIANT_TRUE ? true : false);
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceScripting::get_Language(BSTR *pVal)
{
   try
   {
      if (!ini_file_settings_)
         return GetAccessDenied();

      *pVal = config_->GetScriptLanguage().AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceScripting::put_Language(BSTR newVal)
{
   try
   {
      if (!ini_file_settings_)
         return GetAccessDenied();

      // Validated, because an unrecognised language does not fail loudly - it turns
      // every event handler off in silence, and reports success while doing it.
      //
      // ScriptServer knows exactly two language names. Anything else makes
      // GetScriptExtension_ return an empty extension, so the file the server looks
      // for is "EventHandlers." with nothing after the dot; no such file exists, the
      // script it loads is empty, and OnAcceptMessage, OnClientLogon and
      // OnClientValidatePassword simply stop firing. An operator who mistyped the
      // language got no error from this setter, no error in the log, and an
      // anti-spam or logon handler that had quietly stopped running.
      HM::String requested = newVal;
      requested.Trim();

      HM::String canonical;

      if (requested.CompareNoCase(_T("VBScript")) == 0)
         canonical = _T("VBScript");
      else if (requested.CompareNoCase(_T("JScript")) == 0)
         canonical = _T("JScript");
      else
         return COMError::GenerateError(HM::Formatter::Format("The script language '{0}' is not supported. hMailServer can run VBScript or JScript.", requested));

      // Stored in the spelling ScriptServer compares against, not the caller's. Those
      // comparisons are String::operator==, which is case-sensitive, so accepting
      // "vbscript" and storing it as typed would leave scripting exactly as silently
      // dead as an unknown name would.
      config_->SetScriptLanguage(canonical);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceScripting::Reload(void)
{
   try
   {
      if (!ini_file_settings_)
         return GetAccessDenied();

      HM::ScriptServer::Instance()->LoadScripts();
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceScripting::CheckSyntax(BSTR *pVal)
{
   try
   {
      if (!ini_file_settings_)
         return GetAccessDenied();

      *pVal = HM::ScriptServer::Instance()->CheckSyntax().AllocSysString();
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceScripting::get_Directory(BSTR *pVal)
{
   try
   {
      if (!ini_file_settings_)
         return GetAccessDenied();

      *pVal = ini_file_settings_->GetEventDirectory().AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceScripting::get_CurrentScriptFile(BSTR *pVal)
{
   try
   {
      if (!ini_file_settings_)
         return GetAccessDenied();

   
      *pVal = HM::ScriptServer::Instance()->GetCurrentScriptFile().AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}


