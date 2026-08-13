// Copyright (c) 2005 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"


#include "../Cache/Cache.h"


#include "PersistentAccount.h"
#include "PersistentACLPermission.h"
#include "PersistentFetchAccount.h"
#include "PersistentRule.h"
#include "PersistentIMAPFolder.h"
#include "PersistentMessage.h"
#include "PersistentGroupMember.h"
#include "PersistenceMode.h"

#include "../Util/File.h"
#include "../Util/Crypt.h"
#include "../Util/Time.h"
#include "../BO/Account.h"
#include "../BO/Rules.h"
#include "../BO/IMAPFolder.h"
#include "../BO/IMAPFolders.h"

#include "NameChanger.h"

#include "../Cache/AccountSizeCache.h"

#include "../../IMAP/IMAPFolderContainer.h"
#include "../../IMAP/MessagesContainer.h"
#include "../../SMTP/SMTPVacationMessageCreator.h"

#include "PreSaveLimitationsCheck.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   boost::recursive_mutex PersistentAccount::password_upgrade_mutex_;
   std::set<__int64> PersistentAccount::password_upgrade_pending_;

   PersistentAccount::PersistentAccount()
   {

   }

   PersistentAccount::~PersistentAccount()
   {

   }

   bool
   PersistentAccount::DeleteObject(std::shared_ptr<Account> pAccount)
   {
      __int64 iID = pAccount->GetID();
      assert(iID);

      if (iID <= 0)
         return false;

      // Drop any pending-upgrade flag for this id. Account ids are not reused by the
      // identity columns in practice, but a flag naming a deleted account is dead
      // weight at best and, if an id ever were reused, would attribute one account's
      // clear-text state to another.
      ClearPasswordUpgradePending_(iID);

      // Everything from here to the delete of the account row itself is a cascade with
      // no transaction around it, and every step of it discarded its result. It has to
      // carry on after a failure - stopping half way leaves more behind, not less -
      // but "carry on" and "say nothing" are different decisions, and only the first
      // of them was ever made. What is collected instead is a list of what survived,
      // reported once at the end, because an orphan row belonging to an account that
      // no longer exists is something nothing else in the system will ever mention
      // again.
      std::vector<String> orphans;

      // Delete messages connected to this account.
      if (!DeleteMessages(pAccount))
         orphans.push_back(_T("its messages"));

      // Force delete the inbox as well. DeleteMessages above does not delete it.
      std::shared_ptr<IMAPFolder> inbox = pAccount->GetFolders()->GetFolderByName("Inbox");
      if (inbox)
      {
         if (!PersistentIMAPFolder::DeleteObject(inbox, true))
            orphans.push_back(_T("its inbox"));
      }

      if (!pAccount->GetRules()->DeleteAll())
         orphans.push_back(_T("its rules"));

      // Delete fetch accounts connected to this account. This one returns void, so
      // there is nothing to check - noted rather than silently skipped.
      PersistentFetchAccount::DeleteByAccountID(iID);

      // Delete references from groups...
      if (!PersistentGroupMember::DeleteByAccount(iID))
         orphans.push_back(_T("its group memberships"));

      if (!PersistentACLPermission::DeleteOwnedByAccount(iID))
         orphans.push_back(_T("the folder permissions it granted"));

      if (!orphans.empty())
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6109, "PersistentAccount::DeleteObject",
            Formatter::Format("Account {0} is being deleted but {1} could not be, and will be left behind referring to an account that no longer exists.",
               iID, StringParser::JoinVector(orphans, _T(", "))));
      }

      SQLCommand deleteCommand("delete from hm_accounts where accountid = @ACCOUNTID");
      deleteCommand.AddParameter("@ACCOUNTID", iID);

      bool bRet = Application::Instance()->GetDBManager()->Execute(deleteCommand);

      // The directory is only removed once the row is definitely gone. Before this
      // it was removed either way, so a delete that failed at the statement above -
      // a locked database, a dropped connection - left an account that still
      // exists, still authenticates and still accepts mail, with its entire
      // mailbox directory removed from under it. Recreating the tree is not the
      // problem; the problem was that the failure looked like nothing had
      // happened, because DeleteObject's return value is all the caller sees.
      if (bRet)
      {
         // Delete folder from data directory
         String sDomainName = StringParser::ExtractDomain(pAccount->GetAddress());
         String sMailbox = StringParser::ExtractAddress(pAccount->GetAddress());

         if (!sDomainName.IsEmpty() && !sMailbox.IsEmpty())
         {
            String sAccountFolder = IniFileSettings::Instance()->GetDataDirectory() + "\\" + sDomainName + "\\" + sMailbox;
            FileUtilities::DeleteDirectory(sAccountFolder, false);
         }
      }
      else
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 5983, "PersistentAccount::DeleteObject",
            Formatter::Format("The account row for {0} could not be deleted. Its messages, rules, fetch accounts and group memberships have already been removed, so the account still exists but is empty. Retry the delete.", pAccount->GetAddress()));
      }

      // Refresh caches.
      Cache<Account>::Instance()->RemoveObject(pAccount);

      return bRet;
   }

   bool
   PersistentAccount::ReadObject(std::shared_ptr<Account> pAccount, __int64 ObjectID)
   {
      String sSQL = "select * from hm_accounts where accountid = @ACCOUNTID";
 
      SQLCommand command(sSQL);
      command.AddParameter("@ACCOUNTID", ObjectID);
      
      return ReadObject(pAccount, command);
   }

   bool
   PersistentAccount::ReadObject(std::shared_ptr<Account> pAccount, const String & sAddress)
   {
      SQLStatement statement;
      statement.SetStatementType(SQLStatement::STSelect);
      statement.SetTable("hm_accounts");
      statement.AddWhereClauseColumn("accountaddress", sAddress);

      bool bResult = ReadObject(pAccount, statement.GetCommand());

      return bResult;

   }

   bool
   PersistentAccount::ReadObject(std::shared_ptr<Account> pAccount, const SQLCommand &command)
   {
      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!pRS)
         return false;

      bool bRetVal = false;
      if (!pRS->IsEOF())
      {
         bRetVal = ReadObject(pAccount, pRS);
      }

      return bRetVal;
   }



   bool
   PersistentAccount::ReadObject(std::shared_ptr<Account> pAccount, std::shared_ptr<DALRecordset> pRS)
   {
      pAccount->SetID(pRS->GetLongValue("accountid"));
      pAccount->SetActive(pRS->GetLongValue("accountactive") ? true : false);
      pAccount->SetADDomain(pRS->GetStringValue("accountaddomain"));
      pAccount->SetAddress(pRS->GetStringValue("accountaddress"));
      pAccount->SetADUsername(pRS->GetStringValue("accountadusername"));
      pAccount->SetDomainID(pRS->GetLongValue("accountdomainid"));
      pAccount->SetAdminLevel((Account::AdminLevel) pRS->GetLongValue("accountadminlevel"));
      
      pAccount->SetIsAD(pRS->GetLongValue("accountisad") ? true : false);
      pAccount->SetPassword(pRS->GetStringValue("accountpassword"));
      pAccount->SetAccountMaxSize(pRS->GetLongValue("accountmaxsize"));

      pAccount->SetVacationMessageIsOn(pRS->GetLongValue("accountvacationmessageon") ? true : false);
      pAccount->SetVacationMessage(pRS->GetStringValue("accountvacationmessage"));
      pAccount->SetVacationSubject(pRS->GetStringValue("accountvacationsubject"));
      pAccount->SetVacationExpires(pRS->GetLongValue("accountvacationexpires") ? true : false);
      pAccount->SetVacationAbortSpamFlagged(pRS->GetLongValue("accountvacationabortspamflagged") ? true : false);

      String sVacationExpiresDate = pRS->GetStringValue("accountvacationexpiredate");
      if (sVacationExpiresDate.Left(4) != _T("0000"))
         pAccount->SetVacationExpiresDate(sVacationExpiresDate);

      pAccount->SetForwardEnabled(pRS->GetLongValue("accountforwardenabled") ? true : false);
      pAccount->SetForwardAddress(pRS->GetStringValue("accountforwardaddress"));
      pAccount->SetForwardKeepOriginal(pRS->GetLongValue("accountforwardkeeporiginal") ? true : false);
      pAccount->SetForwardAbortSpamFlagged(pRS->GetLongValue("accountforwardabortspamflagged") ? true : false);

      pAccount->SetPasswordEncryption(pRS->GetLongValue("accountpwencryption"));

      pAccount->SetEnableSignature(pRS->GetLongValue("accountenablesignature") ? true : false);
      pAccount->SetSignaturePlainText(pRS->GetStringValue("accountsignatureplaintext"));
      pAccount->SetSignatureHTML(pRS->GetStringValue("accountsignaturehtml"));

      pAccount->SetLastLogonTime(pRS->GetStringValue("accountlastlogontime"));

      pAccount->SetPersonFirstName(pRS->GetStringValue("accountpersonfirstname"));
      pAccount->SetPersonLastName(pRS->GetStringValue("accountpersonlastname"));

      if (pAccount->GetPasswordEncryption() == 0)
      {
         // The password isn't encrypted. Encrypt it now using the preferred hash algorithm.
         int preferredHashAlgorithm = IniFileSettings::Instance()->GetPreferredHashAlgorithm();
         pAccount->SetPassword(Crypt::Instance()->EnCrypt(pAccount->GetPassword(), (HM::Crypt::EncryptionType) preferredHashAlgorithm));
         pAccount->SetPasswordEncryption(preferredHashAlgorithm);

         // The in-memory object now carries a hash but the row does not. Remember that,
         // so that the next successful login can write the upgrade back. See the header
         // for why the write does not happen here.
         if (pAccount->GetID() > 0 && preferredHashAlgorithm != (int) Crypt::ETNone)
            MarkPasswordUpgradePending_(pAccount->GetID());
      }

      return true;
   }

   void
   PersistentAccount::MarkPasswordUpgradePending_(__int64 accountID)
   {
      boost::lock_guard<boost::recursive_mutex> guard(password_upgrade_mutex_);
      password_upgrade_pending_.insert(accountID);
   }

   void
   PersistentAccount::ClearPasswordUpgradePending_(__int64 accountID)
   {
      boost::lock_guard<boost::recursive_mutex> guard(password_upgrade_mutex_);
      password_upgrade_pending_.erase(accountID);
   }

   void
   PersistentAccount::ClearAllPasswordUpgradePending()
   {
      boost::lock_guard<boost::recursive_mutex> guard(password_upgrade_mutex_);
      password_upgrade_pending_.clear();
   }

   bool
   PersistentAccount::IsPasswordUpgradePending(__int64 accountID)
   {
      if (accountID <= 0)
         return false;

      boost::lock_guard<boost::recursive_mutex> guard(password_upgrade_mutex_);
      return password_upgrade_pending_.find(accountID) != password_upgrade_pending_.end();
   }

   bool
   PersistentAccount::PersistUpgradedPassword(std::shared_ptr<const Account> pAccount, const String &passwordHash, int encryptionType)
   {
      if (!pAccount || pAccount->GetID() <= 0 || passwordHash.IsEmpty())
         return false;

      // Deliberately a narrow UPDATE of the two password columns rather than
      // SaveObject: SaveObject writes every column from the in-memory object, and the
      // object reaching here is a cached snapshot taken when the account was last
      // read. Writing all of it back from the authentication path would overwrite
      // anything changed since - accountlastlogontime in particular is updated by
      // direct SQL (UpdateLastLogonTime) without refreshing the cached object, so a
      // full save would silently roll it back - and PreSaveLimitationsCheck can refuse
      // to save a pre-existing account for reasons that have nothing to do with the
      // password.
      SQLCommand command("update hm_accounts set accountpassword = @PASSWORD, accountpwencryption = @ENCRYPTION where accountid = @ACCOUNTID");
      command.AddParameter("@PASSWORD", passwordHash);
      command.AddParameter("@ENCRYPTION", encryptionType);
      command.AddParameter("@ACCOUNTID", pAccount->GetID());

      if (!Application::Instance()->GetDBManager()->Execute(command))
         return false;

      // Note the order: the pending flag is taken and released before the cache lock is
      // touched, never nested inside it. ReadObject marks the flag while the account
      // cache is being populated, so nesting the two the other way round here would be
      // a lock-order inversion.
      ClearPasswordUpgradePending_(pAccount->GetID());

      // Drop the now-stale cached copy so that the next read picks up the persisted
      // hash and type. Whoever is authenticating right now holds its own handle to the
      // object, so the login in progress is unaffected.
      Cache<Account>::Instance()->RemoveObject(pAccount->GetAddress());

      return true;
   }

   bool
   PersistentAccount::DeleteMessages(std::shared_ptr<Account> pAccount)
   {
      if (!pAccount || pAccount->GetID() == 0)
         return false;

	   PersistentIMAPFolder::DeleteByAccount(pAccount->GetID());
	   
      Cache<Account>::Instance()->RemoveObject(pAccount);
      AccountSizeCache::Instance()->Reset(pAccount->GetID());
      IMAPFolderContainer::Instance()->UncacheAccount(pAccount->GetID());
      MessagesContainer::Instance()->UncacheAccount(pAccount->GetID());
      return true;
   }
   
   bool
   PersistentAccount::SaveObject(std::shared_ptr<Account> pAccount)
   {
      String sErrorMessage;
      return SaveObject(pAccount, sErrorMessage, false, PersistenceModeNormal);
   }

   bool
   PersistentAccount::SaveObject(std::shared_ptr<Account> pAccount, String &sErrorMessage, PersistenceMode mode)
   {
      return SaveObject(pAccount, sErrorMessage, false, mode);
   }


   bool
   PersistentAccount::SaveObject(std::shared_ptr<Account> pAccount, String &sErrorMessage, bool createInbox, PersistenceMode mode)
   {
      if (!PreSaveLimitationsCheck::CheckLimitations(mode, pAccount, sErrorMessage))
         return false;

      __int64 iID = pAccount->GetID();
      if (iID > 0)
      {
         // First read the domain to see if we've changed its name.
         std::shared_ptr<Account> tempAccount = std::shared_ptr<Account>(new Account());
         if (!PersistentAccount::ReadObject(tempAccount, iID))
            return false;

         if (tempAccount->GetAddress().CompareNoCase(pAccount->GetAddress()) != 0)
         {
            // Check if this account contains messages with full path. If so, the user cannot 
            // rename it at this point.
            // Disallow change, if not all files are partial in the database.
            if (!PersistentMessage::GetAllMessageFilesArePartialNames())
            {
               sErrorMessage = "As of hMailServer 5.4, partial file names are stored in the database, rather than the full path\r\n" 
                                 "To be able to rename the account, you must first migrate your database to the new partial paths scheme. To do\r\n" 
                                 "this, run Data Directory Synchronizer.";
               return false;
            }

            // Name has been changed. Rename all sub objects first.
            NameChanger nameChanger;
            if (!nameChanger.RenameAccount(tempAccount->GetAddress(), pAccount, sErrorMessage))
               return false;

            // Remove the old account from the cache.
            Cache<Account>::Instance()->RemoveObject(tempAccount->GetAddress());
         }
      }

      SQLStatement oStatement;
      oStatement.AddColumnInt64("accountdomainid", pAccount->GetDomainID());
      oStatement.AddColumn("accountaddress", pAccount->GetAddress());
      oStatement.AddColumn("accountpassword", pAccount->GetPassword());
      oStatement.AddColumn("accountactive", pAccount->GetActive());
      oStatement.AddColumn("accountisad", pAccount->GetIsAD());
      oStatement.AddColumn("accountaddomain", pAccount->GetADDomain());
      oStatement.AddColumn("accountadusername", pAccount->GetADUsername());
      oStatement.AddColumn("accountmaxsize", pAccount->GetAccountMaxSize());

      oStatement.AddColumn("accountvacationmessageon", pAccount->GetVacationMessageIsOn());
      oStatement.AddColumn("accountvacationmessage", pAccount->GetVacationMessage());
      oStatement.AddColumn("accountvacationsubject", pAccount->GetVacationSubject());
      oStatement.AddColumn("accountvacationexpires", pAccount->GetVacationExpires());
      oStatement.AddColumn("accountvacationexpiredate", pAccount->GetVacationExpiresDate());
      oStatement.AddColumn("accountvacationabortspamflagged", pAccount->GetVacationAbortSpamFlagged());

      oStatement.AddColumn("accountpwencryption", pAccount->GetPasswordEncryption());
      oStatement.AddColumn("accountadminlevel", pAccount->GetAdminLevel());

      oStatement.AddColumn("accountforwardenabled", pAccount->GetForwardEnabled());
      oStatement.AddColumn("accountforwardaddress", pAccount->GetForwardAddress());
      oStatement.AddColumn("accountforwardkeeporiginal", pAccount->GetForwardKeepOriginal());
      oStatement.AddColumn("accountforwardabortspamflagged", pAccount->GetForwardAbortSpamFlagged());

      oStatement.AddColumn("accountenablesignature", pAccount->GetEnableSignature());
      oStatement.AddColumn("accountsignatureplaintext", pAccount->GetSignaturePlainText());
      oStatement.AddColumn("accountsignaturehtml", pAccount->GetSignatureHTML());

      oStatement.AddColumn("accountlastlogontime", pAccount->GetLastLogonTime());
      
      oStatement.AddColumn("accountpersonfirstname", pAccount->GetPersonFirstName());
      oStatement.AddColumn("accountpersonlastname", pAccount->GetPersonLastName());

      oStatement.SetTable("hm_accounts");


      if (pAccount->GetID() == 0)
      {
         oStatement.SetStatementType(SQLStatement::STInsert);
         oStatement.SetIdentityColumn("accountid");
      }
      else
      {
         oStatement.SetStatementType(SQLStatement::STUpdate);

         String sWhere;
         sWhere.Format(_T("accountid = %I64d"), pAccount->GetID());
         
         oStatement.SetWhereClause(sWhere);

      }

      bool bNewObject = pAccount->GetID() == 0;

      // Save and fetch ID
      __int64 iDBID = 0;
      bool bRetVal = Application::Instance()->GetDBManager()->Execute(oStatement, bNewObject ? &iDBID : 0);
      if (bRetVal && bNewObject)
      {
         pAccount->SetID((long) iDBID);

         if (createInbox)
         {
            if (!CreateInbox(*pAccount))
            {
               // Two things were wrong here, and the smaller one is the unchecked
               // delete. The larger is that bRetVal was left true: the account row had
               // inserted successfully, so this function reported that the account was
               // created even though the rollback below had just removed it - or, when
               // the rollback failed too, even though what was left was an account with
               // no inbox, which authenticates perfectly well and can never be
               // delivered to.
               if (!PersistentAccount::DeleteObject(pAccount))
               {
                  ErrorManager::Instance()->ReportError(ErrorManager::High, 6108, "PersistentAccount::SaveObject",
                     Formatter::Format("The inbox for account {0} could not be created and the account could not be removed again, so an account now exists that cannot receive mail. It should be deleted by hand.",
                        pAccount->GetAddress()));
               }

               bRetVal = false;
            }
         }
      }

	   if (!bNewObject)
		  Cache<Account>::Instance()->RemoveObject(pAccount);

      // A full save writes accountpassword and accountpwencryption from the in-memory
      // object, so whatever the row held before - clear text included - has just been
      // replaced. Any "this row is still clear text" flag set by an earlier ReadObject
      // is now false, and leaving it set would let the upgrade path treat a
      // freshly-written strong hash as an unencrypted one.
      if (bRetVal)
         ClearPasswordUpgradePending_(pAccount->GetID());

      return bRetVal;
   }

   __int64
   PersistentAccount::GetMessageBoxSize(__int64 iAccountID)
   {
      SQLCommand selectCommand("select sum(messagesize) as mailboxsize from hm_messages where messageaccountid = @ACCOUNTID");
      selectCommand.AddParameter("@ACCOUNTID", iAccountID);

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(selectCommand);
      if (!pRS)
         return false;

      // Different data types on MySQL and MSSQL...

      __int64 iSize = 0;
      switch (IniFileSettings::Instance()->GetDatabaseType())
      {
      case DatabaseSettings::TypeMYSQLServer:
         iSize = (__int64) pRS->GetDoubleValue("mailboxsize");;
         break;
      case DatabaseSettings::TypeMSSQLServer:
      case DatabaseSettings::TypeMSSQLCompactEdition:
         iSize = pRS->GetInt64Value("mailboxsize");;
         break;
      case DatabaseSettings::TypePGServer:
         iSize = (__int64) pRS->GetDoubleValue("mailboxsize");
         break;
      case DatabaseSettings::TypeUnknown:
         assert(0);
      }

      return iSize;
      
   }

   bool
   PersistentAccount::CreateInbox(const Account &account)
   {
      std::shared_ptr<IMAPFolder> inbox = std::shared_ptr<IMAPFolder>(new IMAPFolder(account.GetID(), -1));
      inbox->SetFolderName("INBOX");
      inbox->SetIsSubscribed(true);

      return PersistentIMAPFolder::SaveObject(inbox);
   }

   bool 
   PersistentAccount::UpdateLastLogonTime(std::shared_ptr<const Account> pAccount)
   {
      if (!pAccount)
         return false; 


      AnsiString sCurrentTime = Time::GetCurrentDateTime();

      SQLCommand command("update hm_accounts set accountlastlogontime = @LASTLOGONTIME where accountid = @ACCOUNTID");
      command.AddParameter("@LASTLOGONTIME", sCurrentTime);
      command.AddParameter("@ACCOUNTID", pAccount->GetID());

      return Application::Instance()->GetDBManager()->Execute(command);
   }

   bool
   PersistentAccount::GetIsVacationMessageOn(std::shared_ptr<const Account> pAccount)
   {
      if (!pAccount->GetVacationMessageIsOn())
         return false;

      // Vacation message is switched on. We could simply return true here,
      // but we're should disable the vacation message if it's time for that now...
      if (!pAccount->GetVacationExpires())
      {
         // Vacation is on, and set to not expire.
         return true;
      }

      // Check if vacation message has expired.
      DateTime dtNow = DateTime::GetCurrentTime();
      DateTime dtExpires = Time::GetDateFromSystemDate(pAccount->GetVacationExpiresDate());

      if (dtNow.status_ == DateTime::invalid || dtExpires.status_ == DateTime::invalid)
         return true;

      if (dtNow > dtExpires)
      {
         DisableVacationMessage(pAccount);
         return false;
      }

      // It has not expired yet.
      return true;
   }

   bool 
   PersistentAccount::DisableVacationMessage(std::shared_ptr<const Account> pAccount)
   {
      if (!pAccount)
         return false; 

      String sCurrentTime = Time::GetCurrentDateTime();

      SQLCommand command("update hm_accounts set accountvacationmessageon = 0 where accountid = @ACCOUNTID");
      command.AddParameter("@ACCOUNTID", pAccount->GetID());

      bool result = Application::Instance()->GetDBManager()->Execute(command);

      if (result)
         SMTPVacationMessageCreator::Instance()->VacationMessageTurnedOff(pAccount->GetAddress());

      return result;
   }
}