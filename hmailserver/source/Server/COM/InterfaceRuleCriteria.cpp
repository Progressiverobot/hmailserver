// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "InterfaceRuleCriteria.h"

#include "../Common/BO/RuleCriteria.h"
#include "../Common/BO/RuleCriterias.h"
#include "../Common/Persistence/PersistentRuleCriteria.h"

#include "COMError.h"

STDMETHODIMP InterfaceRuleCriteria::get_ID(long *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = (long) object_->GetID();
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRuleCriteria::Save()
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      if (parent_collection_->GetRuleID() > 0)
      {
         // Through the overload that can SAY why - the length check refuses
         // over-long criteria values with the number in the message, and an
         // administrator staring at "see the error log" for a validation error
         // is the silence the check exists to end.
         HM::String errorMessage;
         if (!HM::PersistentRuleCriteria::SaveObject(object_, errorMessage, HM::PersistenceModeNormal))
         {
            if (!errorMessage.IsEmpty())
               return COMError::GenerateError(errorMessage);

            return COMError::GenerateError("Failed to save object. See hMailServer error log.");
         }
      }
   
      AddToParentCollection();
         
      return S_OK;
      
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRuleCriteria::get_RuleID(long *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = (long) object_->GetRuleID();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRuleCriteria::put_RuleID(long newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetRuleID(newVal);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRuleCriteria::get_UsePredefined(VARIANT_BOOL *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetUsePredefined() ? VARIANT_TRUE : VARIANT_FALSE;
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRuleCriteria::put_UsePredefined(VARIANT_BOOL newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetUsePredefined(newVal == VARIANT_TRUE);
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRuleCriteria::get_HeaderField(BSTR *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetHeaderField().AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRuleCriteria::put_HeaderField(BSTR newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetHeaderField(newVal);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRuleCriteria::get_MatchValue(BSTR *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetMatchValue().AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRuleCriteria::put_MatchValue(BSTR newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetMatchValue(newVal);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRuleCriteria::get_PredefinedField(eRulePredefinedField *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = (eRulePredefinedField) object_->GetPredefinedField();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRuleCriteria::put_PredefinedField(eRulePredefinedField newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetPredefinedField((HM::RuleCriteria::PredefinedField)newVal);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRuleCriteria::get_MatchType(eRuleMatchType *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = (eRuleMatchType) object_->GetMatchType();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRuleCriteria::put_MatchType(eRuleMatchType newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetMatchType((HM::RuleCriteria::MatchType)newVal);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceRuleCriteria::Delete()
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      if (!parent_collection_)
         return HM::PersistentRuleCriteria::DeleteObject(object_) ? S_OK : S_FALSE;
   
      parent_collection_->DeleteItemByDBID(object_->GetID());
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

