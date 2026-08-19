// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#include "stdafx.h"
#include "InterfaceAppPassword.h"

#include "../Common/BO/AppPasswords.h"
#include "../Common/Persistence/PersistentAppPassword.h"
#include "../Common/Util/Time.h"
#include "../Common/Util/PasswordPolicy.h"

#include "COMError.h"

InterfaceAppPassword::InterfaceAppPassword()
{
   object_ = std::shared_ptr<HM::AppPassword>(new HM::AppPassword());
}

STDMETHODIMP InterfaceAppPassword::get_ID(LONG* pVal)
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

STDMETHODIMP InterfaceAppPassword::get_Name(BSTR* pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetName().AllocSysString();

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAppPassword::put_Name(BSTR newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetName(newVal);

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAppPassword::get_CreatedTime(BSTR* pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetCreatedTime().AllocSysString();

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAppPassword::get_LastUsedTime(BSTR* pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetLastUsedTime().AllocSysString();

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAppPassword::get_Active(VARIANT_BOOL* pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetActive() ? VARIANT_TRUE : VARIANT_FALSE;

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAppPassword::put_Active(VARIANT_BOOL newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetActive(newVal == VARIANT_TRUE);

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAppPassword::Generate(BSTR* pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      // Issuing a credential for a mailbox is an administrative act, and the value
      // this returns opens that mailbox. Anything less than the same check the rest of
      // the account interface uses would make this the cheapest way past it.
      if (!authentication_->GetIsDomainAdmin())
         return authentication_->GetAccessDenied();

      HM::String clearText = HM::AppPassword::GenerateSecret();

      if (clearText.IsEmpty())
         return COMError::GenerateError("The random number generator failed, so no app password was generated. Nothing has been stored. See the hMailServer error log.");

      object_->SetPassword(clearText);

      // The only moment this value exists outside the caller. It is not stored, not
      // logged and not returned by anything else - see the class comment.
      *pVal = clearText.AllocSysString();

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAppPassword::SetPassword(BSTR Password)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      if (!authentication_->GetIsDomainAdmin())
         return authentication_->GetAccessDenied();

      HM::String clearText = Password;

      // A generated one is 20 characters. A chosen one is typed into a client once and
      // then never again, so there is no usability argument for a short one - and it
      // authenticates a mailbox exactly as the account password does.
      //
      // Twelve is this credential's own floor, independent of the server's password
      // policy: an installation with no policy configured still must not be handed a
      // four-character app password.
      if (clearText.GetLength() < 12)
         return COMError::GenerateError("An app password must be at least 12 characters. It is typed into a client once and then lives for years, and it opens the mailbox exactly as the account password does; use Generate unless there is a reason not to.");

      // ...and the configured policy applies on top, because an app password opens the
      // mailbox exactly as the account password does. Without this, a server requiring
      // sixteen characters of mixed case would accept a twelve-character lower-case app
      // password for the same mailbox - a policy with a documented way round it is not
      // a policy.
      //
      // The account name is passed empty, which skips the "must not contain the
      // account name" rule: this object holds an account id rather than an address, and
      // resolving one here would put a cache lookup on a path that does not otherwise
      // need one. It is the least valuable rule of the set for a credential that is
      // normally generated rather than typed, and claiming to apply it while silently
      // passing the wrong name would be worse than not applying it.
      HM::String policyFailure;

      if (!HM::PasswordPolicy::IsAcceptable(_T(""), clearText, policyFailure))
         return COMError::GenerateError(policyFailure);

      object_->SetPassword(clearText);

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAppPassword::Save()
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      if (!authentication_->GetIsDomainAdmin())
         return authentication_->GetAccessDenied();

      // Stamped here rather than in the constructor: an object that is created,
      // abandoned and never saved should leave no trace, and the creation time that
      // matters is the one the row was written with.
      if (object_->GetID() == 0 && object_->GetCreatedTime().IsEmpty())
         object_->SetCreatedTime(HM::Time::GetCurrentDateTime());

      HM::String result;

      if (HM::PersistentAppPassword::SaveObject(object_, result, HM::PersistenceModeNormal))
      {
         AddToParentCollection();

         return S_OK;
      }

      if (result.IsEmpty())
         result = "Failed to save object. See hMailServer error log.";

      return COMError::GenerateError(result);
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAppPassword::Delete()
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      if (!authentication_->GetIsDomainAdmin())
         return authentication_->GetAccessDenied();

      if (!parent_collection_)
         return HM::PersistentAppPassword::DeleteObject(object_) ? S_OK : S_FALSE;

      parent_collection_->DeleteItemByDBID(object_->GetID());

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}
