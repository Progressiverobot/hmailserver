// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"

#include "PersistentDomain.h"
#include "PersistentAccount.h"
#include "PersistentMessage.h"
#include "PersistentAlias.h"
#include "PersistentDistributionList.h"
#include "PersistentDistributionListRecipient.h"

#include "../Util/Crypt.h"
#include "../BO/Domain.h"
#include "../BO/DomainAliases.h"
#include "../BO/Accounts.h"
#include "../BO/Groups.h"
#include "../BO/Aliases.h"
#include "../BO/Account.h"
#include "../BO/DistributionLists.h"
#include "../BO/DistributionListRecipients.h"
#include "../BO/DistributionListRecipient.h"
#include "../Cache/CacheContainer.h"

#include "NameChanger.h"

#include "PreSaveLimitationsCheck.h"
#include "PersistenceMode.h"
#include "../Sieve/SieveStorage.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // ---------------------------------------------------------------------
      // References held by ONE domain's configuration to a mailbox in ANOTHER.
      //
      // Three columns can name an address that lives outside the domain of the
      // row holding it: hm_aliases.aliasvalue,
      // hm_distributionlistsrecipients.distributionlistrecipientaddress and
      // hm_accounts.accountforwardaddress.
      //
      // Neither the rename cascade (NameChanger::RenameDomain) nor the delete
      // cascade below looked outside the domain being changed, and that is a
      // mail-confidentiality defect rather than untidiness. The moment the old
      // domain name stops being local, RecipientParser stops finding an account
      // for it and falls through to its route/external branch, so a message
      // that used to be delivered into a local mailbox is instead relayed to
      // whatever MX now answers for the old name - and on the alias path
      // bTreatSecurityAsLocal has already been set, so the relay restriction
      // does not stop it either. The admin who renamed or deleted the domain is
      // given no indication that mail is now leaving the server.
      // ---------------------------------------------------------------------

      bool AddressIsInDomains_(const String &address, const std::set<String> &lowerCaseDomainNames)
      {
         // StringParser::ExtractDomain returns the whole string when there is no
         // @ in it, which would make a bare local part look like a domain name.
         if (address.ReverseFind(_T("@")) == -1)
            return false;

         String domainName = StringParser::ExtractDomain(address).ToLower();
         if (domainName.IsEmpty())
            return false;

         return lowerCaseDomainNames.find(domainName) != lowerCaseDomainNames.end();
      }

      // Same local part, different domain. Built with Formatter rather than
      // concatenation so a quoted local part containing an @ survives it.
      String MoveAddressToDomain_(const String &address, const String &newDomainName)
      {
         return Formatter::Format("{0}@{1}", StringParser::ExtractAddress(address), newDomainName);
      }

      // The three sweeps below are given an empty newDomainName when the domain
      // is being deleted rather than renamed. There is then no address to point
      // at, so the reference is switched off instead: refusing the message is
      // recoverable and visible, relaying it off-site is neither.
      //
      // A sweep never fails the operation that triggered it. On a rename the
      // domain row and its own objects have already been written by the time we
      // get here, so aborting would leave the domain named one thing and its
      // accounts another; on a delete the domain row is already gone. Reporting
      // and carrying on leaves exactly the pre-fix state for the row that could
      // not be updated, and says so.

      void UpdateDependentAliases_(const std::set<String> &oldDomainNames, const String &newDomainName)
      {
         // Whole table: an alias in any domain may target any address. Read fully
         // before anything is written - saving while the recordset is open would
         // interleave writes with the read.
         SQLCommand command("select * from hm_aliases");

         std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
         if (!pRS)
         {
            ErrorManager::Instance()->ReportError(ErrorManager::High, 5980, "PersistentDomain::UpdateDependentAliases_",
               "Could not read hm_aliases. Aliases in other domains may still point at a domain which is no longer hosted here, in which case mail to them is relayed off-site.");
            return;
         }

         std::vector<std::shared_ptr<Alias> > affected;

         while (!pRS->IsEOF())
         {
            std::shared_ptr<Alias> alias = std::shared_ptr<Alias>(new Alias);
            if (PersistentAlias::ReadObject(alias, pRS) && AddressIsInDomains_(alias->GetValue(), oldDomainNames))
               affected.push_back(alias);

            pRS->MoveNext();
         }

         for (std::shared_ptr<Alias> alias : affected)
         {
            String description;

            if (newDomainName.IsEmpty())
            {
               if (!alias->GetIsActive())
                  continue;

               alias->SetIsActive(false);
               description = Formatter::Format("Alias {0} was disabled because its target {1} is no longer hosted on this server.", alias->GetName(), alias->GetValue());
            }
            else
            {
               String updatedValue = MoveAddressToDomain_(alias->GetValue(), newDomainName);
               description = Formatter::Format("Alias {0} now points at {1}, following the rename of its target domain.", alias->GetName(), updatedValue);
               alias->SetValue(updatedValue);
            }

            // PersistenceModeRename: this is the internal cascade of a rename or a
            // delete, not an administrator editing the alias, so the pre-save
            // validation that belongs to the edit path is not applied to it. The
            // existing loops in NameChanger::RenameDomain save the same way.
            String saveError;
            if (!PersistentAlias::SaveObject(alias, saveError, PersistenceModeRename))
            {
               ErrorManager::Instance()->ReportError(ErrorManager::High, 5981, "PersistentDomain::UpdateDependentAliases_",
                  Formatter::Format("Could not update alias {0}. It still points at {1}. {2}", alias->GetName(), alias->GetValue(), saveError));
               continue;
            }

            LOG_APPLICATION(description);
         }
      }

      // The list address, for the log line. A membership row only carries the list
      // id, and an id is not something an administrator can act on.
      String DescribeList_(__int64 listID)
      {
         std::shared_ptr<DistributionList> list = std::shared_ptr<DistributionList>(new DistributionList);

         if (PersistentDistributionList::ReadObject(list, listID) && !list->GetAddress().IsEmpty())
            return list->GetAddress();

         return Formatter::Format("distribution list {0}", listID);
      }

      void UpdateDependentListMembers_(const std::set<String> &oldDomainNames, const String &newDomainName)
      {
         SQLCommand command("select * from hm_distributionlistsrecipients");

         std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
         if (!pRS)
         {
            ErrorManager::Instance()->ReportError(ErrorManager::High, 5980, "PersistentDomain::UpdateDependentListMembers_",
               "Could not read hm_distributionlistsrecipients. Distribution lists may still hold members at a domain which is no longer hosted here, in which case mail to those lists is relayed off-site.");
            return;
         }

         std::vector<std::shared_ptr<DistributionListRecipient> > affected;

         while (!pRS->IsEOF())
         {
            std::shared_ptr<DistributionListRecipient> recipient = std::shared_ptr<DistributionListRecipient>(new DistributionListRecipient);
            if (PersistentDistributionListRecipient::ReadObject(recipient, pRS) && AddressIsInDomains_(recipient->GetAddress(), oldDomainNames))
               affected.push_back(recipient);

            pRS->MoveNext();
         }

         for (std::shared_ptr<DistributionListRecipient> recipient : affected)
         {
            String listName = DescribeList_(recipient->GetListID());

            if (newDomainName.IsEmpty())
            {
               // A membership row carries no active flag, so unlike an alias there
               // is nothing to switch off - the row either names an address or it
               // does not exist. Removal is logged with the address so that it can
               // be put back by hand.
               String removed = Formatter::Format("Removed {0} from the distribution list {1}: that domain is no longer hosted on this server.", recipient->GetAddress(), listName);

               if (!PersistentDistributionListRecipient::DeleteObject(recipient))
               {
                  ErrorManager::Instance()->ReportError(ErrorManager::High, 5981, "PersistentDomain::UpdateDependentListMembers_",
                     Formatter::Format("Could not remove {0} from the distribution list {1}. Mail to the list is relayed to that address off-site.", recipient->GetAddress(), listName));
                  continue;
               }

               LOG_APPLICATION(removed);
               continue;
            }

            String updatedAddress = MoveAddressToDomain_(recipient->GetAddress(), newDomainName);
            String description = Formatter::Format("Distribution list {0}: the member {1} is now {2}, following the rename of its domain.", listName, recipient->GetAddress(), updatedAddress);
            recipient->SetAddress(updatedAddress);

            String saveError;
            if (!PersistentDistributionListRecipient::SaveObject(recipient, saveError, PersistenceModeRename))
            {
               ErrorManager::Instance()->ReportError(ErrorManager::High, 5981, "PersistentDomain::UpdateDependentListMembers_",
                  Formatter::Format("Could not update the member {0} of the distribution list {1}. {2}", recipient->GetAddress(), listName, saveError));
               continue;
            }

            LOG_APPLICATION(description);
         }
      }

      void UpdateDependentForwards_(const std::set<String> &oldDomainNames, const String &newDomainName)
      {
         // Only rows that actually hold a forward address. The column is NOT NULL
         // with an empty-string default on all four backends, so this is a plain
         // comparison rather than an IS NULL / IS NOT NULL dialect question.
         SQLCommand command("select * from hm_accounts where accountforwardaddress <> ''");

         std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
         if (!pRS)
         {
            ErrorManager::Instance()->ReportError(ErrorManager::High, 5980, "PersistentDomain::UpdateDependentForwards_",
               "Could not read hm_accounts. Accounts may still forward to a domain which is no longer hosted here, in which case their mail is forwarded off-site.");
            return;
         }

         std::vector<std::shared_ptr<Account> > affected;

         while (!pRS->IsEOF())
         {
            std::shared_ptr<Account> account = std::shared_ptr<Account>(new Account);
            if (PersistentAccount::ReadObject(account, pRS) && AddressIsInDomains_(account->GetForwardAddress(), oldDomainNames))
               affected.push_back(account);

            pRS->MoveNext();
         }

         for (std::shared_ptr<Account> account : affected)
         {
            String description;

            if (newDomainName.IsEmpty())
            {
               // Forwarding is switched off but the address is left in place, so
               // the administrator can see where the mail used to go. A disabled
               // forward address is inert, so there is nothing to do for one.
               if (!account->GetForwardEnabled())
                  continue;

               account->SetForwardEnabled(false);
               description = Formatter::Format("Forwarding was switched off for {0}: its forward address {1} is no longer hosted on this server.", account->GetAddress(), account->GetForwardAddress());
            }
            else
            {
               // Rewritten whether or not forwarding is currently enabled, so that
               // switching it back on later still reaches the right mailbox.
               String updatedAddress = MoveAddressToDomain_(account->GetForwardAddress(), newDomainName);
               description = Formatter::Format("Account {0} now forwards to {1}, following the rename of that domain.", account->GetAddress(), updatedAddress);
               account->SetForwardAddress(updatedAddress);
            }

            String saveError;
            if (!PersistentAccount::SaveObject(account, saveError, PersistenceModeRename))
            {
               ErrorManager::Instance()->ReportError(ErrorManager::High, 5981, "PersistentDomain::UpdateDependentForwards_",
                  Formatter::Format("Could not update the forward address of {0}. It still names {1}. {2}", account->GetAddress(), account->GetForwardAddress(), saveError));
               continue;
            }

            LOG_APPLICATION(description);
         }
      }

      void UpdateDependentAddresses_(const std::set<String> &oldDomainNames, const String &newDomainName)
      {
         if (oldDomainNames.empty())
            return;

         UpdateDependentAliases_(oldDomainNames, newDomainName);
         UpdateDependentListMembers_(oldDomainNames, newDomainName);
         UpdateDependentForwards_(oldDomainNames, newDomainName);
      }
   }

   PersistentDomain::PersistentDomain()
   {

   }

   PersistentDomain::~PersistentDomain()
   {

   }

   bool
   PersistentDomain::DeleteObject(std::shared_ptr<Domain> pDomain)
   {
      __int64 iDomainID = pDomain->GetID();
      HM_ASSERT(iDomainID);

      if (iDomainID > 0)
      {
         // Every name under which this domain currently accepts mail, collected
         // before the domain aliases are deleted below because they are about to
         // stop existing. All of these names stop resolving locally when the
         // domain goes, so a reference from another domain to any of them - not
         // only to the primary name - has to be dealt with afterwards.
         std::set<String> deletedDomainNames;

         if (!pDomain->GetName().IsEmpty())
            deletedDomainNames.insert(pDomain->GetName().ToLower());

         std::vector<std::shared_ptr<DomainAlias> > domainAliases = pDomain->GetDomainAliases()->GetVector();
         for (std::shared_ptr<DomainAlias> domainAlias : domainAliases)
         {
            if (!domainAlias->GetAlias().IsEmpty())
               deletedDomainNames.insert(domainAlias->GetAlias().ToLower());
         }

         // Note for whoever picks this up next: the five statements below are not
         // one transaction and cannot easily be made into one. Every
         // Persistent*::Execute takes its own connection out of the pool, so a
         // transaction opened here would not cover them, and the default backend
         // (SQL Server Compact) does not implement transactions at all - see
         // SQLCEConnection::BeginTransaction. A failure part way through
         // therefore still leaves a half-deleted domain, and the message files
         // already unlinked from disk could not be rolled back in any case.
         if (!pDomain->GetAccounts()->DeleteAll())
            return false;

         if (!pDomain->GetAliases()->DeleteAll())
            return false;

         if (!pDomain->GetDistributionLists()->DeleteAll())
            return false;

         if (!pDomain->GetDomainAliases()->DeleteAll())
            return false;

         SQLCommand command("delete from hm_domains where domainid = @DOMAINID");
         command.AddParameter("@DOMAINID", iDomainID);

         if (!Application::Instance()->GetDBManager()->Execute(command))
            return false;

         // Only once the domain is definitely gone. Doing this first would
         // disable aliases and switch off forwarding for a domain that then
         // turned out to still exist because the delete above failed.
         UpdateDependentAddresses_(deletedDomainNames, String());

         // Refresh the BO cache
         CacheContainer::Instance()->RemoveDomain(pDomain);

         // Delete folder from data directory
         if (!pDomain->GetName().IsEmpty())
         {
            String sDomainFolder = IniFileSettings::Instance()->GetDataDirectory() + "\\" + pDomain->GetName();
            FileUtilities::DeleteDirectory(sDomainFolder, false);

            // The domain's Sieve tree lives outside that directory, so it survived
            // the delete - and recreating the domain and any of its addresses later
            // silently reactivated the previous holders' filters for whoever now
            // owns those addresses. The per-account deletes above remove their own
            // Sieve directories; this removes the domain directory itself, and
            // anything left in it by an account whose row had already gone.
            FileUtilities::DeleteDirectory(SieveStorage::GetDomainDirectory(pDomain->GetName()), true);
         }

         return true;

      }

      return true;
   }

   bool
   PersistentDomain::ReadObject(std::shared_ptr<Domain> pDomain, __int64 ObjectID)
   {
      SQLCommand command("select * from hm_domains where domainid = @DOMAINID");
      command.AddParameter("@DOMAINID", ObjectID);

      bool bResult = ReadObject(pDomain, command);

      return bResult;
   }

   bool
   PersistentDomain::ReadObject(std::shared_ptr<Domain> pDomain, const String & sDomainName)
   {
      SQLStatement statement;

      statement.SetStatementType(SQLStatement::STSelect);
      statement.SetTable("hm_domains");
      statement.AddWhereClauseColumn("domainname", sDomainName);

      bool bResult = ReadObject(pDomain, statement.GetCommand());

      return bResult;
   }


   bool
   PersistentDomain::ReadObject(std::shared_ptr<Domain> pDomain, const SQLCommand &command)
   {
      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!pRS)
         return false;

      bool bRetVal = false;
      if (!pRS->IsEOF())
         bRetVal = ReadObject(pDomain, pRS);

      return bRetVal;
   }



   bool
   PersistentDomain::ReadObject(std::shared_ptr<Domain> pDomain, std::shared_ptr<DALRecordset> pRS)
   {
      pDomain->SetID(pRS->GetLongValue("domainid"));
      pDomain->SetName(pRS->GetStringValue("domainname"));
      pDomain->SetIsActive(pRS->GetLongValue("domainactive") == 1);
      pDomain->SetPostmaster(pRS->GetStringValue("domainpostmaster"));
      pDomain->SetADDomainName(pRS->GetStringValue("domainaddomain"));
      pDomain->SetMaxMessageSize(pRS->GetLongValue("domainmaxmessagesize"));
      pDomain->SetMaxAccountSize(pRS->GetLongValue("domainmaxaccountsize"));

      pDomain->SetUsePlusAddressing(pRS->GetLongValue("domainuseplusaddressing") ? true : false);
      pDomain->SetPlusAddressingChar(pRS->GetStringValue("domainplusaddressingchar"));
      pDomain->SetAntiSpamOptions(pRS->GetLongValue("domainantispamoptions"));
      pDomain->SetMaxSizeMB(pRS->GetLongValue("domainmaxsize"));
      pDomain->SetMessageRetentionDays(pRS->GetLongValue("domainmessageretentiondays"));

      pDomain->SetEnableSignature(pRS->GetLongValue("domainenablesignature") ? true : false);
      pDomain->SetSignatureMethod((Domain::DomainSignatureMethod) pRS->GetLongValue("domainsignaturemethod"));
      pDomain->SetSignaturePlainText(pRS->GetStringValue("domainsignatureplaintext"));
      pDomain->SetSignatureHTML(pRS->GetStringValue("domainsignaturehtml"));

      pDomain->SetAddSignaturesToReplies(pRS->GetLongValue("domainaddsignaturestoreplies") ? true : false);
      pDomain->SetAddSignaturesToLocalMail(pRS->GetLongValue("domainaddsignaturestolocalemail") ? true : false);

      pDomain->SetMaxNoOfAccounts(pRS->GetLongValue("domainmaxnoofaccounts"));
      pDomain->SetMaxNoOfAliases(pRS->GetLongValue("domainmaxnoofaliases"));
      pDomain->SetMaxNoOfDistributionLists(pRS->GetLongValue("domainmaxnoofdistributionlists"));
      
      pDomain->SetLimitationsEnabled(pRS->GetLongValue("domainlimitationsenabled"));

      pDomain->SetDKIMSelector(pRS->GetStringValue("domaindkimselector"));
      pDomain->SetDKIMPrivateKeyFile(pRS->GetStringValue("domaindkimprivatekeyfile"));

      // Both columns default to an empty string (upgrade step 6006 -> 6007), so
      // a domain that has never staged a DKIM key rotation reads back exactly
      // the state it had before the columns existed.
      pDomain->SetDKIMSecondarySelector(pRS->GetStringValue("domaindkimsecondaryselector"));
      pDomain->SetDKIMSecondaryPrivateKeyFile(pRS->GetStringValue("domaindkimsecondaryprivatekeyfile"));

      pDomain->SetRelayHost(pRS->GetStringValue("domainrelayhost"));
      pDomain->SetRelayPort(pRS->GetLongValue("domainrelayport"));
      pDomain->SetRelayRequiresAuth(pRS->GetLongValue("domainrelayrequiresauth") == 1);
      pDomain->SetRelayUsername(pRS->GetStringValue("domainrelayusername"));

      // Protected with the same machine-bound scheme a route's relay password uses.
      // It has to be recoverable rather than hashed, because the server presents it to
      // another server - so it is protected against being read out of the database,
      // which is the threat that actually applies to it.
      pDomain->SetRelayPassword(Crypt::Instance()->UnprotectSecret(pRS->GetStringValue("domainrelaypassword")));
      pDomain->SetRelayConnectionSecurity((ConnectionSecurity) pRS->GetLongValue("domainrelayconnectionsecurity"));

      // All six columns default to off/empty, so a domain that predates them reads
      // back as one that has never configured a domain-wide out-of-office reply.
      pDomain->SetVacationMessageIsOn(pRS->GetLongValue("domainvacationmessageon") == 1);
      pDomain->SetVacationSubject(pRS->GetStringValue("domainvacationsubject"));
      pDomain->SetVacationMessage(pRS->GetStringValue("domainvacationmessage"));
      pDomain->SetVacationInternalSubject(pRS->GetStringValue("domainvacationinternalsubject"));
      pDomain->SetVacationInternalMessage(pRS->GetStringValue("domainvacationinternalmessage"));
      pDomain->SetVacationExternalOverride(pRS->GetLongValue("domainvacationexternaloverride") == 1);

      return true;
   }

   bool
   PersistentDomain::SaveObject(std::shared_ptr<Domain> pDomain)
   {
      String sErrorMessage;
      return SaveObject(pDomain, sErrorMessage, PersistenceModeNormal);
   }

   bool
   PersistentDomain::SaveObject(std::shared_ptr<Domain> pDomain, String &sErrorMessage, PersistenceMode mode)
   {
      if (!PreSaveLimitationsCheck::CheckLimitations(mode, pDomain, sErrorMessage))
         return false;

      // Set when this save is a rename, so that the references other domains hold
      // to addresses at the old name can be rewritten once the new name is
      // committed. Empty for every other save.
      String renamedFromDomainName;

      __int64 iID = pDomain->GetID();
      if (iID > 0)
      {
         // First read the domain to see if we've changed its name.
         std::shared_ptr<Domain> pTempDomain = std::shared_ptr<Domain>(new Domain());
         if (!PersistentDomain::ReadObject(pTempDomain, iID))
            return false;

         if (pTempDomain->GetName().CompareNoCase(pDomain->GetName()) != 0)
         {
            // Disallow change, if not all files are partial in the database.
            if (!PersistentMessage::GetAllMessageFilesArePartialNames())
            {
               sErrorMessage = "As of hMailServer 5.4, partial file names are stored in the database, rather than the full path\r\n" 
                                  "To be able to rename the domain, you must first migrate your database to the new partial paths scheme. To do\r\n"
                                  "this, run Data Directory Synchronizer.";
               return false;
            }

            // Name has been changed. Rename all sub objects first.
            NameChanger nameChanger;
            if (!nameChanger.RenameDomain(pTempDomain->GetName(), pDomain, sErrorMessage))
               return false;

            renamedFromDomainName = pTempDomain->GetName();

            // Remove the old domain from the cache.
            CacheContainer::Instance()->RemoveDomain(pTempDomain);
         }
      }

      SQLStatement oStatement;
      oStatement.AddColumn("domainname", pDomain->GetName());
      oStatement.AddColumn("domainactive", pDomain->GetIsActive());
      oStatement.AddColumn("domainpostmaster", pDomain->GetPostmaster());
      oStatement.AddColumn("domainmaxsize", pDomain->GetMaxSizeMB());
      oStatement.AddColumn("domainmessageretentiondays", pDomain->GetMessageRetentionDays());
      oStatement.AddColumn("domainaddomain", pDomain->GetADDomainName());
      oStatement.AddColumn("domainmaxmessagesize", pDomain->GetMaxMessageSize());
      oStatement.AddColumn("domainmaxaccountsize", pDomain->GetMaxAccountSize());

      oStatement.AddColumn("domainuseplusaddressing", pDomain->GetUsePlusAddressing());
      oStatement.AddColumn("domainplusaddressingchar", pDomain->GetPlusAddressingChar());
      oStatement.AddColumn("domainantispamoptions", pDomain->GetAntiSpamOptions());

      oStatement.AddColumn("domainenablesignature", pDomain->GetEnableSignature());
      oStatement.AddColumn("domainsignaturemethod", pDomain->GetSignatureMethod());
      oStatement.AddColumn("domainsignatureplaintext", pDomain->GetSignaturePlainText());
      oStatement.AddColumn("domainsignaturehtml", pDomain->GetSignatureHTML());
      oStatement.AddColumn("domainaddsignaturestoreplies", pDomain->GetAddSignaturesToReplies());
      oStatement.AddColumn("domainaddsignaturestolocalemail", pDomain->GetAddSignaturesToLocalMail());

      oStatement.AddColumn("domainmaxnoofaccounts", pDomain->GetMaxNoOfAccounts());
      oStatement.AddColumn("domainmaxnoofaliases", pDomain->GetMaxNoOfAliases());
      oStatement.AddColumn("domainmaxnoofdistributionlists", pDomain->GetMaxNoOfDistributionLists());
      oStatement.AddColumn("domainlimitationsenabled", pDomain->GetLimitationsEnabled());

      oStatement.AddColumn("domaindkimselector", pDomain->GetDKIMSelector());
      oStatement.AddColumn("domaindkimprivatekeyfile", pDomain->GetDKIMPrivateKeyFile());
      oStatement.AddColumn("domaindkimsecondaryselector", pDomain->GetDKIMSecondarySelector());
      oStatement.AddColumn("domaindkimsecondaryprivatekeyfile", pDomain->GetDKIMSecondaryPrivateKeyFile());

      oStatement.AddColumn("domainrelayhost", pDomain->GetRelayHost());
      oStatement.AddColumn("domainrelayport", pDomain->GetRelayPort());
      oStatement.AddColumn("domainrelayrequiresauth", pDomain->GetRelayRequiresAuth() ? 1 : 0);
      oStatement.AddColumn("domainrelayusername", pDomain->GetRelayUsername());
      oStatement.AddColumn("domainrelaypassword", Crypt::Instance()->ProtectSecret(pDomain->GetRelayPassword()));
      oStatement.AddColumn("domainrelayconnectionsecurity", (int) pDomain->GetRelayConnectionSecurity());

      oStatement.AddColumn("domainvacationmessageon", pDomain->GetVacationMessageIsOn() ? 1 : 0);
      oStatement.AddColumn("domainvacationsubject", pDomain->GetVacationSubject());
      oStatement.AddColumn("domainvacationmessage", pDomain->GetVacationMessage());
      oStatement.AddColumn("domainvacationinternalsubject", pDomain->GetVacationInternalSubject());
      oStatement.AddColumn("domainvacationinternalmessage", pDomain->GetVacationInternalMessage());
      oStatement.AddColumn("domainvacationexternaloverride", pDomain->GetVacationExternalOverride() ? 1 : 0);

      oStatement.SetTable("hm_domains");
      
      if (pDomain->GetID() == 0)
      {
         oStatement.SetStatementType(SQLStatement::STInsert);
         oStatement.SetIdentityColumn("domainid");
      }
      else
      {
         oStatement.SetStatementType(SQLStatement::STUpdate);

         String sWhere;
         sWhere.Format(_T("domainid = %I64d"), pDomain->GetID());

         oStatement.SetWhereClause(sWhere);

      }

      bool bNewObject = pDomain->GetID() == 0;

      // Save and fetch ID
      __int64 iDBID = 0;
      bool bRetVal = Application::Instance()->GetDBManager()->Execute(oStatement, bNewObject ? &iDBID : 0);
      if (bRetVal && bNewObject)
         pDomain->SetID((int) iDBID);

      // NameChanger::RenameDomain only walks the objects this domain owns. An
      // alias target, a distribution list membership or a forward address held by
      // a SECOND local domain went on naming the old domain, which is no longer
      // hosted - so the message was relayed to whatever MX answers for the old
      // name instead of being delivered locally. Done after the row is written so
      // that a failed write leaves the old name in place everywhere rather than
      // pointing the rest of the configuration at a name the domain does not have.
      if (bRetVal && !renamedFromDomainName.IsEmpty())
      {
         std::set<String> oldDomainNames;
         oldDomainNames.insert(renamedFromDomainName.ToLower());

         UpdateDependentAddresses_(oldDomainNames, pDomain->GetName());
      }

      // Refresh the BO cache
      CacheContainer::Instance()->RemoveDomain(pDomain);

      return bRetVal;
   }

   bool
   PersistentDomain::DomainExists(const String &DomainName, bool &bIsActive)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Returns true if an active account with the address accountaddress exists.
   //---------------------------------------------------------------------------()
   {
      std::shared_ptr<const Domain> pDomain = CacheContainer::Instance()->GetDomain(DomainName);

      if (pDomain)
      {
         bIsActive = pDomain->GetIsActive();
         return true;
      }

      return false;
   }

   int 
   PersistentDomain::GetSize(std::shared_ptr<Domain> pDomain)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Returns the current size of this domain, measured in mega bytes.
   //---------------------------------------------------------------------------()
   {
      
      HM::DatabaseSettings::SQLDBType DBType = IniFileSettings::Instance()->GetDatabaseType();

      String sSQL;
      if (DBType == DatabaseSettings::TypeMSSQLServer || DBType == DatabaseSettings::TypeMSSQLCompactEdition || DBType == DatabaseSettings::TypePGServer)
      {
         // The subquery must project the account id being matched against
         // messageaccountid; projecting accountdomainid compared message owners
         // against a domain id and reported a nonsense domain size.
         sSQL = "select sum(messagesize) as size from hm_messages "
                "where messageaccountid in "
                "(select accountid from hm_accounts where accountdomainid = @DOMAINID) ";
      }
      else if (DBType == DatabaseSettings::TypeMYSQLServer)
      {
         sSQL = "select sum(messagesize) as size from hm_messages "
                "inner join hm_accounts on "
                "(hm_messages.messageaccountid = hm_accounts.accountid and hm_accounts.accountdomainid = @DOMAINID)";

      }

      SQLCommand command (sSQL);
      command.AddParameter("@DOMAINID", pDomain->GetID());

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!pRS || pRS->IsEOF())
         return 0;

      __int64 iSize = pRS->GetInt64Value("size");
   
      // Convert from bytes to megabytes. We'll lose
      // some precision here, but that's OK.
      int iMB = (int) (iSize / 1024 / 1024);

      return iMB;

   }

   __int64 
   PersistentDomain::GetAllocatedSize(std::shared_ptr<Domain> pDomain)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Returns the current allocated size of the domain in MB. This means the
   // total max size of all accounts in the domain.
   //---------------------------------------------------------------------------()
   {
      HM::DatabaseSettings::SQLDBType DBType = IniFileSettings::Instance()->GetDatabaseType();

      SQLCommand command;
      
      if (DBType == DatabaseSettings::TypeMSSQLServer || 
          DBType == DatabaseSettings::TypePGServer ||
          DBType == DatabaseSettings::TypeMSSQLCompactEdition)
      {
         command.SetQueryString("select sum(cast(accountmaxsize as bigint)) as size from hm_accounts where accountdomainid = @DOMAINID");
      }
      else 
      {
         command.SetQueryString("select sum(accountmaxsize) as size from hm_accounts where accountdomainid = @DOMAINID");
      }

      command.AddParameter("@DOMAINID", pDomain->GetID());

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!pRS || pRS->IsEOF())
         return 0;

      // The account size is stored in MB so we don't have to do any conversion.
      __int64 iSize = pRS->GetInt64Value("size");

      return iSize;
   }
}
