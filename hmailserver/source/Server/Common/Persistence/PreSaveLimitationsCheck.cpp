// Copyright (c) 2008 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"

#include "PreSaveLimitationsCheck.h"

#include "../BO/Domain.h"
#include "../BO/DomainAlias.h"
#include "../BO/DomainAliases.h"
#include "../BO/Accounts.h"
#include "../BO/Aliases.h"
#include "../BO/DistributionLists.h"
#include "../BO/DistributionListRecipient.h"
#include "../BO/Groups.h"
#include "../BO/Route.h"
#include "../BO/Routes.h"
#include "../BO/SecurityRanges.h"

#include "../Cache/CacheContainer.h"
#include "../Application/ObjectCache.h"

#include "PersistentDomain.h"

#include "../../IMAP/IMAPConfiguration.h"
#include "../../SMTP/SMTPConfiguration.h"

#include "../Util/RegularExpression.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM   
{
   PreSaveLimitationsCheck::PreSaveLimitationsCheck()
   {
   }

   PreSaveLimitationsCheck::~PreSaveLimitationsCheck()
   {

   }

   std::shared_ptr<Domain> 
   PreSaveLimitationsCheck::GetDomain(__int64 id)
   {
      std::shared_ptr<Domain> domain = std::shared_ptr<Domain>(new Domain);

      PersistentDomain::ReadObject(domain, id);

      return domain;
   }

   bool
   PreSaveLimitationsCheck::IsValidAccountAddress_(const String &sEmailAddress)
   {
      // Stricter than general email validation: additionally forbids \ / ? * | :
      // in the local part because hMailServer uses the account address to build
      // filesystem paths for message storage, and these characters are illegal
      // in Windows file/directory names.
      //
      // The colon was missing from that set until 15 August 2026, and it belongs there
      // for exactly the same reason as the other five rather than a special one. The
      // local part becomes a DIRECTORY component - PersistentMessage::GetFileName
      // builds <data>\<domain>\<local part>\<2 guid chars>\<guid>.eml - and
      // CreateDirectory refuses a name containing a colon outright ("The directory name
      // is invalid"), measured on Windows 11 rather than assumed. So an account at
      // "a:b@example.com" saves, appears in every list, accepts mail at RCPT TO, and
      // then cannot have its folder created when the message is filed. It is a mailbox
      // that can never work, created without complaint.
      //
      // Worth being exact about one thing this does NOT do, because the colon's other
      // reputation invites the mistake: it is not stopping an alternate-data-stream
      // trick. A colon opens an ADS only where the component is a FILE name; here it is
      // an intermediate directory, two components above the file, so the failure is a
      // plain refusal rather than a hidden stream.
      //
      // PersistenceModeRestore and PersistenceModeRename return before this is reached,
      // so an installation that somehow holds such an address can still be restored and
      // renamed. Anything else is now refused - not only creating a new one, but SAVING
      // an existing one, because a normal save revalidates the address. That is the
      // right trade (the account cannot receive mail either way) but it does mean such
      // an account must be renamed rather than edited.

      const int maxEmailAddressLength = 254;
      if (sEmailAddress.GetLength() > maxEmailAddressLength)
         return false;

      // Note: RFC 5321 Section 4.5.3.1.1 limits the local part to 64 octets, but we
      // intentionally do not enforce this to maintain backwards compatibility with
      // existing accounts that have longer local parts.
      //
      // Original: ^(("[^<>@\\/\?\*|:]+")|(?!\.|.*\.(\.|@))[^<> @\\/"\?\*|:]+)@(\[([0-9]{1,3}\.){3}[0-9]{1,3}\]|\[IPv6:(?:[A-Fa-f0-9]{1,4}:){7}[A-Fa-f0-9]{1,4}\]|(?=.{1,255}$)((?!-|\.)[a-zA-Z0-9-]{0,62}[a-zA-Z0-9])(|\.(?!-|\.)[a-zA-Z0-9-]{0,62}[a-zA-Z0-9]){1,126})$
      //
      // The colon is excluded from the two LOCAL-PART classes only. It still appears
      // twice in the domain alternation - once in the "[IPv6:" literal and once in the
      // group matching the address itself - and must stay there, or an IPv6 address
      // literal would stop being a legal domain.
      //
      // That is a deliberate narrowing, not an oversight, and it is worth naming the
      // gap it leaves: the domain DOES reach the store path (GetFileName's first
      // component is the domain name), so an account in a domain literally named
      // "[IPv6:...]" would hit the same unbuildable-path problem. It cannot arise here,
      // because CheckLimitations already requires the account's domain to match the
      // owning hMailServer domain and IsValidDomainName refuses bracketed literals for
      // a domain object - so there is no path by which such an account exists to be
      // saved. If domain creation ever loosens, this is the second place to look.
      //
      // Conversion:
      // 1) Replace \ with \\
      // 2) Replace " with \"

      String regularExpression = "^((\"[^<>@\\\\/\\?\\*|:]+\")|(?!\\.|.*\\.(\\.|@))[^<> @\\\\/\"\\?\\*|:]+)@(\\[([0-9]{1,3}\\.){3}[0-9]{1,3}\\]|\\[IPv6:(?:[A-Fa-f0-9]{1,4}:){7}[A-Fa-f0-9]{1,4}\\]|(?=.{1,255}$)((?!-|\\.)[a-zA-Z0-9-]{0,62}[a-zA-Z0-9])(|\\.(?!-|\\.)[a-zA-Z0-9-]{0,62}[a-zA-Z0-9]){1,126})$";

      RegularExpression regexpEvaluator;
      return regexpEvaluator.TestExactMatch(regularExpression, sEmailAddress);
   }

   bool
   PreSaveLimitationsCheck::CheckLimitations(PersistenceMode mode, std::shared_ptr<Account> account, String &resultDescription)
   {
      if (mode == PersistenceModeRestore || mode == PersistenceModeRename)
         return true;

      if (account->GetVacationMessage().GetLength() > 1000)
      {
         resultDescription = "The auto reply message length exceeds the 1000 character limit.";
         return false;
      }

      if (!IsValidAccountAddress_(account->GetAddress()))
      {
         resultDescription = "The account address is not a valid email address.";
         return false;
      }

      // hMailServer does nopt support spaces or quotes in local accounts.
      if (account->GetAddress().FindOneOf(_T(" \"")) != -1)
      {
         resultDescription = "The account address may not contain spaces or quotes.";
         return false;
      }

      std::shared_ptr<Domain> domain = GetDomain(account->GetDomainID());

      if (GetDuplicateExist(domain, TypeAccount, account->GetID(), account->GetAddress()))      
         return DuplicateError(resultDescription);

      auto domainName = StringParser::ExtractDomain(account->GetAddress());
      if (domainName.CompareNoCase(domain->GetName()) != 0)
      {
         resultDescription = "The account address domain does not match the owning domain name.";
         return false;
      }

      if (account->GetID() == 0)
      {
         if (domain->GetMaxNoOfAccountsEnabled() && 
             domain->GetAccounts()->GetCount() >= domain->GetMaxNoOfAccounts())
         {
            resultDescription = "The maximum number of accounts have been created.";
            return false;
         }
      }

      if (domain->GetMaxAccountSize() != 0)
      {
         if (account->GetAccountMaxSize() > domain->GetMaxAccountSize())
         {
            resultDescription = "The account is larger than the maximum account size specified in the domain settings.";
            return false;
         }

         if (account->GetAccountMaxSize() == 0)
         {
            resultDescription = "The domain has a maximum account size set. When this is the case, all accounts in the domains must have an account size set.";
            return false;
         }
      }

      if (domain->GetMaxSizeMB() > 0)
      {

         if (account->GetAccountMaxSize() == 0)
         {
            resultDescription = "The domain has a maximum account size set. When this is the case, all accounts in the domains must have an account size set.";
            return false;
         }

         String sError = "Account could not be saved. The total size of all accounts in the domain would exceed the maximum size for the domain.";
         
         __int64 currentSize = PersistentDomain::GetAllocatedSize(domain);

         if (account->GetID() == 0)
         {
            if (currentSize + account->GetAccountMaxSize() > domain->GetMaxSizeMB())
            {
               resultDescription = sError;
               return false;
            }
         }
         else
         {
            std::shared_ptr<Account> currentAccountSettings = std::shared_ptr<Account>(new Account);
            PersistentAccount::ReadObject(currentAccountSettings, account->GetID());

            if (currentSize - currentAccountSettings->GetAccountMaxSize() + account->GetAccountMaxSize() > domain->GetMaxSizeMB())
            {
               resultDescription = sError;
               return false;
            }
         }
      }

      String address = account->GetAddress().ToLower();
      if (address.Find(_T("\\")) >= 0 || address.Find(_T("/")) >= 0)
      {
         resultDescription = "An account name may not contain the characters \\ or /.";
         return false;
      }


      return true;
   }

   bool
   PreSaveLimitationsCheck::CheckLimitations(PersistenceMode mode, std::shared_ptr<DistributionListRecipient> recipient, String &resultDescription)
   {
      if (mode == PersistenceModeRestore || mode == PersistenceModeRename)
         return true;

      if (recipient->GetAddress().GetLength() == 0)
      {
         resultDescription = "The recipient address is empty";
         return false;
      }

      return true;
   }

   bool
   PreSaveLimitationsCheck::CheckLimitations(PersistenceMode mode, std::shared_ptr<Alias> alias, String &resultDescription)
   {
      if (mode == PersistenceModeRestore || mode == PersistenceModeRename)
         return true;

      std::shared_ptr<Domain> domain = GetDomain(alias->GetDomainID());

      if (GetDuplicateExist(domain, TypeAlias, alias->GetID(), alias->GetName()))      
         return DuplicateError(resultDescription);

      auto domainName = StringParser::ExtractDomain(alias->GetName());
      if (domainName.CompareNoCase(domain->GetName()) != 0)
      {
         resultDescription = "The alias address domain does not match the owning domain name.";
         return false;
      }

      if (alias->GetID() == 0)
      {
         if (domain->GetMaxNoOfAliasesEnabled() && 
             domain->GetAliases()->GetCount() >= domain->GetMaxNoOfAliases())
         {
            resultDescription = "The maximum number of aliases have been created.";
            return false;
         }
      }


      return true;
   }

   bool
   PreSaveLimitationsCheck::CheckLimitations(PersistenceMode mode, std::shared_ptr<DistributionList> list, String &resultDescription)
   {
      if (mode == PersistenceModeRestore || mode == PersistenceModeRename)
         return true;

      std::shared_ptr<Domain> domain = GetDomain(list->GetDomainID());

      if (GetDuplicateExist(domain, TypeList,list->GetID(), list->GetAddress()))      
         return DuplicateError(resultDescription);

      auto domainName = StringParser::ExtractDomain(list->GetAddress());
      if (domainName.CompareNoCase(domain->GetName()) != 0)
      {
         resultDescription = "The distribution list domain does not match the owning domain name.";
         return false;
      }

      if (list->GetID() == 0)
      {
         // The flag, not the number. The Control Panel draws this limit as a
         // checkbox beside a number - DomainDialog.cs writes both
         // MaxNumberOfDistributionListsEnabled and MaxNumberOfDistributionLists -
         // and leaves the number alone when the box is cleared, so testing the
         // number meant clearing the box did nothing at all: the old number went on
         // being enforced. Ticking it with the number at 0 enforced nothing. The
         // account and alias checks above both test their flag. Both columns
         // arrived in the same schema step (Upgrade4301to4400MSSQL.sql), so no
         // upgraded database can hold a number without the flag - the only way to
         // reach the broken state is for an administrator to switch the limit off.
         if (domain->GetMaxNoOfDistributionListsEnabled() &&
             domain->GetDistributionLists()->GetCount() >= domain->GetMaxNoOfDistributionLists())
         {
            resultDescription = "The maximum number of distribution lists have been created.";
            return false;
         }
      }

      return true;
   }

   bool
   PreSaveLimitationsCheck::CheckLimitations(PersistenceMode mode, std::shared_ptr<Group> group, String &resultDescription)
   {
      if (mode == PersistenceModeRestore || mode == PersistenceModeRename)
         return true;

      std::shared_ptr<Group> pGroup = Configuration::Instance()->GetIMAPConfiguration()->GetGroups()->GetItemByName(group->GetName());

      if (pGroup && (group->GetID() == 0 || group->GetID() != pGroup->GetID()))
      {
         resultDescription = "Another group with this name already exists.";
         return false;
      }

      return true;
   }

   bool
   PreSaveLimitationsCheck::DuplicateError(String &resultDescription)
   {
      resultDescription = "Another object with the same name already exists in this domain.";
      return false;
   }

   bool 
   PreSaveLimitationsCheck::GetDuplicateExist(std::shared_ptr<Domain> domain, ObjectType objectType, __int64 objectID, const String &objectName)
   {

      std::shared_ptr<Account> pAccount = std::shared_ptr<Account>(new Account);
      if (PersistentAccount::ReadObject(pAccount, objectName))
      {
         if (pAccount && (pAccount->GetID() != objectID || objectType != TypeAccount) )
            return true;
      }

      std::shared_ptr<Alias> pAlias = std::shared_ptr<Alias>(new Alias);
      if (PersistentAlias::ReadObject(pAlias, objectName))
      {
         if (pAlias && (pAlias->GetID() != objectID || objectType != TypeAlias))
            return true;
      }

      std::shared_ptr<DistributionList> pList = std::shared_ptr<DistributionList>(new DistributionList);; 
      if (PersistentDistributionList::ReadObject(pList,objectName ))
      {
         if (pList && (pList->GetID() != objectID || objectType != TypeList))
            return true;
      }

      return false;
   }

   bool
   PreSaveLimitationsCheck::CheckLimitations(PersistenceMode mode, std::shared_ptr<Domain> domain, String &resultDescription)
   {
      if (mode == PersistenceModeRestore || mode == PersistenceModeRename)
         return true;

      std::shared_ptr<const Domain> pDomain = CacheContainer::Instance()->GetDomain(domain->GetName());

      if (pDomain && (domain->GetID() == 0 || domain->GetID() != pDomain->GetID()))
      {
         resultDescription = "Another domain with this name already exists.";
         return false;
      }

      String sName = domain->GetName();
      if (!StringParser::IsValidDomainName(sName))
      {
         resultDescription = "The domain name you have entered is not a valid domain name.";
         return false;
      }

      // Check if there's a domain alias with this name. If so, this domain would be a duplciate.
      std::shared_ptr<DomainAliases> pDomainAliases = ObjectCache::Instance()->GetDomainAliases();
      std::shared_ptr<DomainAlias> pDomainAlias = pDomainAliases->GetItemByName(domain->GetName());
      if (pDomainAlias)
      {
         resultDescription = "A domain alias with this name already exists.";
         return false;
      }

      // If plus addressing is enabled, a plus addressing character must be selected.
      if (domain->GetUsePlusAddressing() && domain->GetPlusAddressingChar() == _T(""))
      {
         resultDescription = "A plus addressing character must be selected when enabling plus addressing.";
         return false;
      }
   
      return true;
   }

   bool
   PreSaveLimitationsCheck::CheckLimitations(PersistenceMode mode, std::shared_ptr<DomainAlias> domainAlias, String &resultDescription)
   {
      if (mode == PersistenceModeRestore || mode == PersistenceModeRename)
         return true;


      std::shared_ptr<const Domain> pDomain = CacheContainer::Instance()->GetDomain(domainAlias->GetName());

      if (pDomain)
      {
         resultDescription = "Another domain with this name already exists.";
         return false;
      }

      // Check if there's a domain alias with this name. If so, this domain would be a duplciate.
      std::shared_ptr<DomainAliases> pDomainAliases = ObjectCache::Instance()->GetDomainAliases();
      std::shared_ptr<DomainAlias> pDomainAlias = pDomainAliases->GetItemByName(domainAlias->GetName());
      if (pDomainAlias && (domainAlias->GetID() == 0 || domainAlias->GetID() != pDomainAlias->GetID()))
      {
         resultDescription = "A domain alias with this name already exists.";
         return false;
      }

      return true;
   }

   bool 
   PreSaveLimitationsCheck::CheckLimitations(PersistenceMode mode, std::shared_ptr<Route> route, String &resultDescription)
   {
      if (mode == PersistenceModeRestore || mode == PersistenceModeRename)
         return true;

      std::shared_ptr<Routes> pRoutes = Configuration::Instance()->GetSMTPConfiguration()->GetRoutes();

      std::shared_ptr<Route> existingRoute = pRoutes->GetItemByName(route->GetName());
      if (existingRoute && existingRoute->GetID() != route->GetID())
      {
        resultDescription = "Another route with this name already exists.";
        return false;  
      }
 
      return true;
   }

   bool
   PreSaveLimitationsCheck::CheckLimitations(PersistenceMode mode, std::shared_ptr<SecurityRange> securityRange, String &resultDescription)
   {
      if (mode == PersistenceModeRestore || mode == PersistenceModeRename)
         return true;

      std::shared_ptr<SecurityRange> existingRange = std::make_shared<SecurityRange>();
      if (PersistentSecurityRange::ReadObject(existingRange, securityRange->GetName()))
      {
         if (existingRange->GetID() != securityRange->GetID())
         {
            resultDescription = "There is already an IP range with this name.";
            return false;
         }
      }

      return true;
   }

}