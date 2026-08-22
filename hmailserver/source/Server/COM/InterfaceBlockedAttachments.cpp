// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "COMError.h"
#include "..\COM\InterfaceBlockedAttachments.h"

#include "..\Common\BO\BlockedAttachment.h"
#include "InterfaceBlockedAttachment.h"

void 
InterfaceBlockedAttachments::Attach(std::shared_ptr<HM::BlockedAttachments> pBA) 
{ 
   blocked_attachments_ = pBA; 
}

STDMETHODIMP 
InterfaceBlockedAttachments::Refresh()
{
   try
   {
      if (!blocked_attachments_)
         return S_FALSE;
   
      blocked_attachments_->Refresh();
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceBlockedAttachments::get_Count(long *pVal)
{
   try
   {
      if (!pVal)
         return E_POINTER;

      // Guarded, as Refresh already was. BlockedAttachments is a registered coclass, so
      // one can be created directly rather than obtained from AntiVirus, and one created
      // that way has no collection attached for this to count.
      //
      // Refused rather than answered S_FALSE, which Refresh uses: S_FALSE is a success
      // code, so a caller would go on to read an out parameter nobody had written -
      // which is the same defect as the one this guard is here to close.
      if (!blocked_attachments_)
         return COMError::GenerateError("This BlockedAttachments collection is not attached to anything. Obtain it from Settings.AntiVirus.BlockedAttachments rather than creating it directly.");

      *pVal = blocked_attachments_->GetCount();

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP
InterfaceBlockedAttachments::get_Item(long Index, IInterfaceBlockedAttachment **pVal)
{
   try
   {
      if (!pVal)
         return E_POINTER;

      *pVal = nullptr;

      if (!blocked_attachments_)
         return COMError::GenerateError("This BlockedAttachments collection is not attached to anything. Obtain it from Settings.AntiVirus.BlockedAttachments rather than creating it directly.");

      // Looked up before the wrapper is created, not after: the wrapper used to be
      // constructed first and abandoned on the bad-index path, and a CComObject that
      // has never been AddRef'd is never freed.
      std::shared_ptr<HM::BlockedAttachment> pBA = blocked_attachments_->GetItem(Index);

      if (!pBA)
         return DISP_E_BADINDEX;

      CComObject<InterfaceBlockedAttachment>* pInterfaceBlockedAttachment = new CComObject<InterfaceBlockedAttachment>();
      pInterfaceBlockedAttachment->SetAuthentication(authentication_);

      pInterfaceBlockedAttachment->AttachItem(pBA);
      pInterfaceBlockedAttachment->AttachParent(blocked_attachments_, true);
      pInterfaceBlockedAttachment->AddRef();
      *pVal = pInterfaceBlockedAttachment;
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP 
InterfaceBlockedAttachments::DeleteByDBID(long DBID)
{
   try
   {
      if (!blocked_attachments_)
         return GetAccessDenied();

      blocked_attachments_->DeleteItemByDBID(DBID);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP 
InterfaceBlockedAttachments::get_ItemByDBID(long lDBID, IInterfaceBlockedAttachment **pVal)
{
   try
   {
      if (!blocked_attachments_)
         return GetAccessDenied();

      CComObject<InterfaceBlockedAttachment>* pInterfaceBlockedAttachment = new CComObject<InterfaceBlockedAttachment>();
      pInterfaceBlockedAttachment->SetAuthentication(authentication_);
   
      std::shared_ptr<HM::BlockedAttachment> pBA = blocked_attachments_->GetItemByDBID(lDBID);
   
      if (!pBA)
         return DISP_E_BADINDEX;
   
      pInterfaceBlockedAttachment->AttachItem(pBA);
      pInterfaceBlockedAttachment->AttachParent(blocked_attachments_, true);
      pInterfaceBlockedAttachment->AddRef();
   
      *pVal = pInterfaceBlockedAttachment;
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP 
InterfaceBlockedAttachments::Add(IInterfaceBlockedAttachment **pVal)
{
   try
   {
      if (!blocked_attachments_)
         return GetAccessDenied();

      if (!blocked_attachments_)
         return authentication_->GetAccessDenied();
   
      CComObject<InterfaceBlockedAttachment>* pInterfaceBlockedAttachment = new CComObject<InterfaceBlockedAttachment>();
      pInterfaceBlockedAttachment->SetAuthentication(authentication_);
   
      std::shared_ptr<HM::BlockedAttachment> pBA = std::shared_ptr<HM::BlockedAttachment>(new HM::BlockedAttachment);
   
      pInterfaceBlockedAttachment->AttachItem(pBA);
      pInterfaceBlockedAttachment->AttachParent(blocked_attachments_, false);
   
      pInterfaceBlockedAttachment->AddRef();
   
      *pVal = pInterfaceBlockedAttachment;
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}


