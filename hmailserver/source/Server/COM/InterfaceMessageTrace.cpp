// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#include "stdafx.h"
#include "InterfaceMessageTrace.h"
#include "InterfaceMessageTraceEvent.h"

#include "COMError.h"

STDMETHODIMP InterfaceMessageTrace::Search(BSTR Address)
{
   try
   {
      // Server admin, not domain admin. A trace search matches sender OR recipient
      // across the whole table, so scoping it to one domain is not something this
      // query can honestly do - and a domain administrator who could search it would
      // see who every other domain's users correspond with.
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();

      events_ = HM::MessageTrace::Search(HM::String(Address), max_listed_);

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceMessageTrace::SearchByQueueID(long QueueID)
{
   try
   {
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();

      events_ = HM::MessageTrace::GetByQueueID(QueueID);

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceMessageTrace::get_Count(LONG* pVal)
{
   try
   {
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();

      // The number of events LOADED, unlike the quarantine's Count which reports the
      // whole store. The difference is what each is for: "how much is in quarantine"
      // is a backlog to work through, while a trace is an answer to a question and
      // the useful number is how many lines that answer has.
      *pVal = (LONG) events_.size();

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceMessageTrace::get_Item(long Index, IInterfaceMessageTraceEvent** pVal)
{
   try
   {
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();

      if (Index < 0 || Index >= (long) events_.size())
         return COMError::GenerateError("Index out of range. Call Search or SearchByQueueID before indexing the trace.");

      CComObject<InterfaceMessageTraceEvent>* pItem = new CComObject<InterfaceMessageTraceEvent>();
      pItem->SetAuthentication(authentication_);
      pItem->Attach(events_[Index]);
      pItem->AddRef();

      *pVal = pItem;

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceMessageTrace::DeleteExpired(LONG* pVal)
{
   try
   {
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();

      *pVal = (LONG) HM::MessageTrace::DeleteExpired();

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}
