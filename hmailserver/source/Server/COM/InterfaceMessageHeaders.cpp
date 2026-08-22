// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "COMError.h"
#include "InterfaceMessageHeaders.h"
#include "InterfaceMessageHeader.h"

#include "../Common/Mime/Mime.h"

void 
InterfaceMessageHeaders::AttachItem(std::shared_ptr<HM::MimeHeader> pHeader)
{
   header_ = pHeader;
}

STDMETHODIMP InterfaceMessageHeaders::get_Count(long *pVal)
{
   try
   {
      if (!pVal)
         return E_POINTER;

      if (!header_)
         return DISP_E_BADINDEX;

      *pVal = header_->GetFieldCount();

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP 
InterfaceMessageHeaders::get_Item(long Index, IInterfaceMessageHeader **pVal)
{
   try
   {
      if (!pVal)
         return E_POINTER;

      *pVal = nullptr;

      if (!header_)
         return DISP_E_BADINDEX;

      // Bounds-checked here rather than left to MimeHeader::GetField(unsigned int).
      // That function tests "iIndex <= fields_.size() - 1" on a size_t, which
      // underflows to SIZE_MAX when the header has no fields at all - so every index
      // passes the test and it returns the address of element [Index] of an empty
      // vector. Item(0) on a message whose MIME header parsed to no fields therefore
      // handed a script a wild pointer to read and write through. The same comparison
      // is signed-to-unsigned, so a negative index arrives there as a very large one.
      if (Index < 0 || Index >= header_->GetFieldCount())
         return DISP_E_BADINDEX;

      HM::MimeField *pField = header_->GetField(static_cast<unsigned int>(Index));

      if (!pField)
         return DISP_E_BADINDEX;

      CComObject<InterfaceMessageHeader>* pInterfaceMessageHeader = new CComObject<InterfaceMessageHeader>();
      pInterfaceMessageHeader->AttachItem(header_, pField);

      pInterfaceMessageHeader->AddRef();

      *pVal = pInterfaceMessageHeader;

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP 
InterfaceMessageHeaders::get_ItemByName(BSTR sName, IInterfaceMessageHeader **pVal)
{
   try
   {
      if (!pVal)
         return E_POINTER;

      *pVal = nullptr;

      if (!header_)
         return DISP_E_BADINDEX;

      HM::AnsiString sFieldName = sName;
      HM::MimeField *pField = header_->GetField(sFieldName);

      if (!pField)
         return DISP_E_BADINDEX;

      CComObject<InterfaceMessageHeader>* pInterfaceMessageHeader = new CComObject<InterfaceMessageHeader>();
      pInterfaceMessageHeader->AttachItem(header_, pField);
      pInterfaceMessageHeader->AddRef();

      *pVal = pInterfaceMessageHeader;

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}


