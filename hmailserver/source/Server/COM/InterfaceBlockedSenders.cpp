// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#include "stdafx.h"
#include "COMError.h"
#include "InterfaceBlockedSenders.h"


#include "InterfaceBlockedSender.h"

#include "..\Common\BO\BlockedSender.h"
#include "..\Common\BO\BlockedSenders.h"


void
InterfaceBlockedSenders::Attach(std::shared_ptr<HM::BlockedSenders> pBlockedSenders)
{
   object_ = pBlockedSenders;
}

STDMETHODIMP
InterfaceBlockedSenders::Refresh()
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->Refresh();

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP
InterfaceBlockedSenders::Clear()
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->DeleteAll();

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceBlockedSenders::get_Count(long *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetCount();

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP
InterfaceBlockedSenders::get_Item(long Index, IInterfaceBlockedSender **pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      CComObject<InterfaceBlockedSender>* pInterfaceBlockedSender = new CComObject<InterfaceBlockedSender>();
      pInterfaceBlockedSender->SetAuthentication(authentication_);

      std::shared_ptr<HM::BlockedSender> pBS = object_->GetItem(Index);

      if (!pBS)
         return DISP_E_BADINDEX;

      pInterfaceBlockedSender->AttachItem(pBS);
      pInterfaceBlockedSender->AttachParent(object_, true);
      pInterfaceBlockedSender->AddRef();
      *pVal = pInterfaceBlockedSender;

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP
InterfaceBlockedSenders::DeleteByDBID(long DBID)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->DeleteItemByDBID(DBID);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP
InterfaceBlockedSenders::get_ItemByDBID(long lDBID, IInterfaceBlockedSender **pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      CComObject<InterfaceBlockedSender>* pInterfaceBlockedSender = new CComObject<InterfaceBlockedSender>();
      pInterfaceBlockedSender->SetAuthentication(authentication_);

      std::shared_ptr<HM::BlockedSender> pBS = object_->GetItemByDBID(lDBID);

      if (!pBS)
         return DISP_E_BADINDEX;

      pInterfaceBlockedSender->AttachItem(pBS);
      pInterfaceBlockedSender->AttachParent(object_, true);
      pInterfaceBlockedSender->AddRef();

      *pVal = pInterfaceBlockedSender;

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP
InterfaceBlockedSenders::get_ItemByName(BSTR sName, IInterfaceBlockedSender **pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      CComObject<InterfaceBlockedSender>* pInterfaceBlockedSender = new CComObject<InterfaceBlockedSender>();
      pInterfaceBlockedSender->SetAuthentication(authentication_);

      std::shared_ptr<HM::BlockedSender> pBS = object_->GetItemByName(sName);

      if (!pBS)
         return DISP_E_BADINDEX;

      pInterfaceBlockedSender->AttachItem(pBS);
      pInterfaceBlockedSender->AttachParent(object_, true);
      pInterfaceBlockedSender->AddRef();

      *pVal = pInterfaceBlockedSender;

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP
InterfaceBlockedSenders::Add(IInterfaceBlockedSender **pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      CComObject<InterfaceBlockedSender>* pInterfaceBlockedSender = new CComObject<InterfaceBlockedSender>();
      pInterfaceBlockedSender->SetAuthentication(authentication_);

      std::shared_ptr<HM::BlockedSender> pBS = std::shared_ptr<HM::BlockedSender>(new HM::BlockedSender);

      pInterfaceBlockedSender->AttachItem(pBS);
      pInterfaceBlockedSender->AttachParent(object_, false);

      pInterfaceBlockedSender->AddRef();

      *pVal = pInterfaceBlockedSender;

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}
