// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once
#include "../hMailServer/resource.h"       // main symbols

#include "../hMailServer/hMailServer.h"


// InterfaceLogging

class ATL_NO_VTABLE InterfaceLogging : 
   public CComObjectRootEx<CComSingleThreadModel>,
   public CComCoClass<InterfaceLogging, &CLSID_Logging>,
   public IDispatchImpl<IInterfaceLogging, &IID_IInterfaceLogging, &LIBID_hMailServer, /*wMajor =*/ 1, /*wMinor =*/ 0>,
   public HM::COMAuthenticator
{
public:
   InterfaceLogging();

   bool LoadSettings();

DECLARE_REGISTRY_RESOURCEID(IDR_INTERFACELOGGING)


BEGIN_COM_MAP(InterfaceLogging)
   COM_INTERFACE_ENTRY(IInterfaceLogging)
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
   STDMETHOD(get_LogDebug)(/*[out, retval]*/ VARIANT_BOOL *pVal);
   STDMETHOD(put_LogDebug)(/*[in]*/ VARIANT_BOOL newVal);

   STDMETHOD(get_Enabled)(/*[out, retval]*/ VARIANT_BOOL *pVal);
   STDMETHOD(put_Enabled)(/*[in]*/ VARIANT_BOOL newVal);

   STDMETHOD(get_LogSMTP)(/*[out, retval]*/ VARIANT_BOOL *pVal);
   STDMETHOD(put_LogSMTP)(/*[in]*/ VARIANT_BOOL newVal);

   STDMETHOD(get_LogPOP3)(/*[out, retval]*/ VARIANT_BOOL *pVal);
   STDMETHOD(put_LogPOP3)(/*[in]*/ VARIANT_BOOL newVal);

   STDMETHOD(get_LogIMAP)(/*[out, retval]*/ VARIANT_BOOL *pVal);
   STDMETHOD(put_LogIMAP)(/*[in]*/ VARIANT_BOOL newVal);

   STDMETHOD(get_AWStatsEnabled)(/*[out, retval]*/ VARIANT_BOOL *pVal);
   STDMETHOD(put_AWStatsEnabled)(/*[in]*/ VARIANT_BOOL newVal);

   STDMETHOD(get_LogTCPIP)(/*[out, retval]*/ VARIANT_BOOL *pVal);
   STDMETHOD(put_LogTCPIP)(/*[in]*/ VARIANT_BOOL newVal);

   STDMETHOD(get_LogApplication)(/*[out, retval]*/ VARIANT_BOOL *pVal);
   STDMETHOD(put_LogApplication)(/*[in]*/ VARIANT_BOOL newVal);

   STDMETHOD(EnableLiveLogging)(VARIANT_BOOL newVal);

   STDMETHOD(get_MaskPasswordsInLog)(/*[out, retval]*/ VARIANT_BOOL *pVal);
   STDMETHOD(put_MaskPasswordsInLog)(/*[in]*/ VARIANT_BOOL newVal);

   STDMETHOD(get_KeepFilesOpen)(/*[out, retval]*/ VARIANT_BOOL *pVal);
   STDMETHOD(put_KeepFilesOpen)(/*[in]*/ VARIANT_BOOL newVal);


   STDMETHOD(get_Device)(/*[out, retval]*/ eLogDevice *pVal);
   STDMETHOD(put_Device)(/*[in]*/ eLogDevice newVal);

   STDMETHOD(get_LogFormat)(/*[out, retval]*/ eLogOutputFormat *pVal);
   STDMETHOD(put_LogFormat)(/*[in]*/ eLogOutputFormat newVal);


   int COMLogDevice2INTLogDevice_(eLogDevice newVal);
   eLogDevice INTLogDevice2COMLogDevice_(int RelayMode);

   int COMLogFormat2IntLogFormat_(eLogOutputFormat newVal);
   eLogOutputFormat IntLogFormat2ComLogFormat_(int RelayMode);   

   STDMETHOD(get_Directory)(/*[out, retval]*/ BSTR *pVal);
   STDMETHOD(get_LiveLog)(/*[out, retval]*/ BSTR *pVal);
   STDMETHOD(get_LiveLoggingEnabled)(/*[out, retval]*/ VARIANT_BOOL *pVal);

   // The syslog sink. The six SyslogLog* switches are bits of one stored
   // integer, exactly as LogSMTP and its five siblings above are bits of the
   // logging mask.
   STDMETHOD(get_SyslogEnabled)(/*[out, retval]*/ VARIANT_BOOL *pVal);
   STDMETHOD(put_SyslogEnabled)(/*[in]*/ VARIANT_BOOL newVal);

   STDMETHOD(get_SyslogHost)(/*[out, retval]*/ BSTR *pVal);
   STDMETHOD(put_SyslogHost)(/*[in]*/ BSTR newVal);

   STDMETHOD(get_SyslogPort)(/*[out, retval]*/ long *pVal);
   STDMETHOD(put_SyslogPort)(/*[in]*/ long newVal);

   STDMETHOD(get_SyslogTransport)(/*[out, retval]*/ long *pVal);
   STDMETHOD(put_SyslogTransport)(/*[in]*/ long newVal);

   STDMETHOD(get_SyslogFacility)(/*[out, retval]*/ long *pVal);
   STDMETHOD(put_SyslogFacility)(/*[in]*/ long newVal);

   STDMETHOD(get_SyslogMinimumSeverity)(/*[out, retval]*/ long *pVal);
   STDMETHOD(put_SyslogMinimumSeverity)(/*[in]*/ long newVal);

   STDMETHOD(get_SyslogLogSMTP)(/*[out, retval]*/ VARIANT_BOOL *pVal);
   STDMETHOD(put_SyslogLogSMTP)(/*[in]*/ VARIANT_BOOL newVal);

   STDMETHOD(get_SyslogLogPOP3)(/*[out, retval]*/ VARIANT_BOOL *pVal);
   STDMETHOD(put_SyslogLogPOP3)(/*[in]*/ VARIANT_BOOL newVal);

   STDMETHOD(get_SyslogLogIMAP)(/*[out, retval]*/ VARIANT_BOOL *pVal);
   STDMETHOD(put_SyslogLogIMAP)(/*[in]*/ VARIANT_BOOL newVal);

   STDMETHOD(get_SyslogLogApplication)(/*[out, retval]*/ VARIANT_BOOL *pVal);
   STDMETHOD(put_SyslogLogApplication)(/*[in]*/ VARIANT_BOOL newVal);

   STDMETHOD(get_SyslogLogTCPIP)(/*[out, retval]*/ VARIANT_BOOL *pVal);
   STDMETHOD(put_SyslogLogTCPIP)(/*[in]*/ VARIANT_BOOL newVal);

   STDMETHOD(get_SyslogLogDebug)(/*[out, retval]*/ VARIANT_BOOL *pVal);
   STDMETHOD(put_SyslogLogDebug)(/*[in]*/ VARIANT_BOOL newVal);

   STDMETHOD(get_CurrentEventLog)(/*[out, retval]*/ BSTR *pVal);
   STDMETHOD(get_CurrentErrorLog)(/*[out, retval]*/ BSTR *pVal);
   STDMETHOD(get_CurrentAwstatsLog)(/*[out, retval]*/ BSTR *pVal);
   STDMETHOD(get_CurrentDefaultLog)(/*[out, retval]*/ BSTR *pVal);
private:

   HM::Configuration *config_;
   HM::IniFileSettings *ini_file_settings_;
};

OBJECT_ENTRY_AUTO(__uuidof(Logging), InterfaceLogging)
