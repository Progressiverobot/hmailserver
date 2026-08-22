// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "COMError.h"
#include "InterfaceTCPIPPorts.h"


#include "..\Common\BO\TCPIPPort.h"
#include "InterfaceTCPIPPort.h"

void 
InterfaceTCPIPPorts::Attach(std::shared_ptr<HM::TCPIPPorts> pBA) 
{ 
   tcpip_ports_ = pBA; 
}

STDMETHODIMP 
InterfaceTCPIPPorts::Refresh()
{
   try
   {
      if (!tcpip_ports_)
         return S_FALSE;
   
      tcpip_ports_->Refresh();
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP 
InterfaceTCPIPPorts::SetDefault()
{
   try
   {
      if (!tcpip_ports_)
         return S_FALSE;

      if (!tcpip_ports_->SetDefault())
      {
         // The existing ports were deleted before the defaults were written, so this
         // is not "nothing happened" - the server may be listening on fewer ports
         // than it was a moment ago. Reported as an error rather than swallowed,
         // because the administration tool otherwise says the defaults were restored.
         return COMError::GenerateError("The default TCP/IP ports could not be restored. The previous ports were removed first, so the server may now be listening on fewer ports than before - check the hMailServer error log, correct the problem and try again.");
      }

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceTCPIPPorts::get_Count(long *pVal)
{
   try
   {
      if (!tcpip_ports_)
         return GetAccessDenied();

      *pVal = tcpip_ports_->GetCount();
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP 
InterfaceTCPIPPorts::get_Item(long Index, IInterfaceTCPIPPort **pVal)
{
   try
   {
      if (!tcpip_ports_)
         return GetAccessDenied();

      CComObject<InterfaceTCPIPPort>* pInterfaceTCPIPPort = new CComObject<InterfaceTCPIPPort>();
      pInterfaceTCPIPPort->SetAuthentication(authentication_);
   
      std::shared_ptr<HM::TCPIPPort> pBA = tcpip_ports_->GetItem(Index);
   
      if (!pBA)
         return DISP_E_BADINDEX;
   
      pInterfaceTCPIPPort->AttachItem(pBA);
      pInterfaceTCPIPPort->AttachParent(tcpip_ports_, true);
      pInterfaceTCPIPPort->AddRef();
      *pVal = pInterfaceTCPIPPort;
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP 
InterfaceTCPIPPorts::DeleteByDBID(long DBID)
{
   try
   {
      if (!tcpip_ports_)
         return GetAccessDenied();

      tcpip_ports_->DeleteItemByDBID(DBID);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP 
InterfaceTCPIPPorts::get_ItemByDBID(long lDBID, IInterfaceTCPIPPort **pVal)
{
   try
   {
      if (!tcpip_ports_)
         return GetAccessDenied();

      CComObject<InterfaceTCPIPPort>* pInterfaceTCPIPPort = new CComObject<InterfaceTCPIPPort>();
      pInterfaceTCPIPPort->SetAuthentication(authentication_);
   
      std::shared_ptr<HM::TCPIPPort> pBA = tcpip_ports_->GetItemByDBID(lDBID);
   
      if (!pBA)
         return DISP_E_BADINDEX;
   
      pInterfaceTCPIPPort->AttachItem(pBA);
      pInterfaceTCPIPPort->AttachParent(tcpip_ports_, true);
      pInterfaceTCPIPPort->AddRef();
   
      *pVal = pInterfaceTCPIPPort;
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP 
InterfaceTCPIPPorts::Add(IInterfaceTCPIPPort **pVal)
{
   try
   {
      if (!tcpip_ports_)
         return GetAccessDenied();

      if (!tcpip_ports_)
         return authentication_->GetAccessDenied();
   
      CComObject<InterfaceTCPIPPort>* pInterfaceTCPIPPort = new CComObject<InterfaceTCPIPPort>();
      pInterfaceTCPIPPort->SetAuthentication(authentication_);
   
      std::shared_ptr<HM::TCPIPPort> pBA = std::shared_ptr<HM::TCPIPPort>(new HM::TCPIPPort);
   
      pInterfaceTCPIPPort->AttachItem(pBA);
      pInterfaceTCPIPPort->AttachParent(tcpip_ports_, false);
   
      pInterfaceTCPIPPort->AddRef();
   
      *pVal = pInterfaceTCPIPPort;
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}


