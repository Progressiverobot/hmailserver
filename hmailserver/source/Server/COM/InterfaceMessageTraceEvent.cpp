// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#include "stdafx.h"
#include "InterfaceMessageTraceEvent.h"

#include "COMError.h"

void
InterfaceMessageTraceEvent::Attach(const HM::MessageTraceEvent &traceEvent)
{
   event_ = traceEvent;
}

STDMETHODIMP InterfaceMessageTraceEvent::get_ID(LONG* pVal)
{
   try
   {
      *pVal = (LONG) event_.id;
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceMessageTraceEvent::get_QueueID(LONG* pVal)
{
   try
   {
      *pVal = (LONG) event_.queue_id;
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceMessageTraceEvent::get_OccurredTime(BSTR* pVal)
{
   try
   {
      *pVal = event_.occurred.AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceMessageTraceEvent::get_EventName(BSTR* pVal)
{
   try
   {
      *pVal = event_.event_name.AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceMessageTraceEvent::get_Sender(BSTR* pVal)
{
   try
   {
      *pVal = event_.sender.AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceMessageTraceEvent::get_Recipient(BSTR* pVal)
{
   try
   {
      *pVal = event_.recipient.AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceMessageTraceEvent::get_SourceIP(BSTR* pVal)
{
   try
   {
      *pVal = event_.source_ip.AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceMessageTraceEvent::get_StatusCode(LONG* pVal)
{
   try
   {
      *pVal = (LONG) event_.status_code;
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceMessageTraceEvent::get_Detail(BSTR* pVal)
{
   try
   {
      *pVal = event_.detail.AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}
