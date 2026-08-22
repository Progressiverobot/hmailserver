// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "InterfaceUtilities.h"
#include "../Common/Util/AddressTraceEraser.h"
#include "../Common/TCPIP/DNSResolver.h"
#include "../Common/TCPIP/HostNameAndIpAddress.h"
#include "../Common/util/ServiceManager.h"
#include "../Common/util/Crypt.h"
#include "../Common/util/MailImporter.h"
#include "../Common/util/EmailAllUsers.h"
#include "../Common/util/GUIDcreator.h"
#include "../Common/util/ClassTester.h"
#include "../Common/util/Utilities.h"
#include "../Common/util/PasswordGenerator.h"
#include "../SMTP/RuleApplier.h"
#include "../SMTP/DeliveryQueue.h"
#include "../SMTP/TlsRptReporterTask.h"
#include "../SMTP/DmarcRptReporterTask.h"
#include "../Common/Persistence/PersistentMessage.h"
#include "../Common/Persistence/Maintenance/Maintenance.h"
#include "../Common/Util/Parsing/StringParser.h"
#include "../Common/Sieve/SieveScript.h"
#include "COMError.h"

STDMETHODIMP InterfaceUtilities::InterfaceSupportsErrorInfo(REFIID riid)
{
   try
   {
      static const IID* arr[] = 
      {
         &IID_IInterfaceUtilities,
      };
   
      for (int i=0;i<sizeof(arr)/sizeof(arr[0]);i++)
      {
         if (InlineIsEqualGUID(*arr[i],riid))
            return S_OK;
      }
      return S_FALSE;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceUtilities::ResolveMXRecords(BSTR DomainName, BSTR *Result)
{
   try
   {
      if (!Result)
         return COMError::GenerateGenericMessage();

      *Result = nullptr;

      HM::String sDomainName(DomainName);
      sDomainName.Trim();

      if (sDomainName.IsEmpty())
         return COMError::GenerateError("No domain name was given.");

      // The server's own resolver, which is the point of the method - issue #29 was
      // the Control Panel's MX-query tool shelling out to nslookup, whose answer
      // comes from the OPERATING SYSTEM's resolver and disagrees with the server
      // whenever hMailServer.ini names a custom DNSServer. This is the same path
      // SMTP delivery resolves through, so what it reports is where mail will
      // actually go, from this server, today.
      std::vector<HM::HostNameAndIpAddress> hosts;

      HM::DNSResolver oDNSResolver;
      bool lookupSucceeded = oDNSResolver.GetEmailServers(sDomainName, hosts);

      // "The lookup failed" and "this domain has no mail servers" are different
      // answers and must not arrive as the same empty string. The resolver already
      // distinguishes them - false means the MX query itself failed, or every
      // address lookup for the exchange hosts did - and the server's own bounce
      // text keeps them apart too. Collapsing both into an empty result told an
      // administrator whose custom DNS server was unreachable that the RECIPIENT's
      // domain has no mail servers, which is the wrong diagnosis in exactly the
      // scenario this method was added for (issue #29).
      if (!lookupSucceeded && hosts.empty())
         return COMError::GenerateError("The DNS lookup failed, so where mail for this domain would go could not be determined. This is a failure of the lookup itself - the domain may well have mail servers. Check the server's DNS configuration (a custom DNSServer in hMailServer.ini, if set, must be reachable) and the DNS entries in the server log.");

      // Hostname and address per line, in the order delivery would try them:
      // GetEmailServers sorts by MX preference. It does NOT randomise among equal
      // preferences - RFC 5321 5.1 asks for that and this server has never done it,
      // so saying otherwise here would misdescribe both this tool and delivery.
      // A tab separates the two so a caller can split them apart without guessing
      // at the hostname's own characters.
      HM::String sResult;

      for (size_t i = 0; i < hosts.size(); i++)
      {
         if (i > 0)
            sResult += _T("\r\n");

         sResult += hosts[i].GetHostName();
         sResult += _T("\t");
         sResult += hosts[i].GetIpAddress();
      }

      *Result = sResult.AllocSysString();

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceUtilities::SendTlsRptReports(VARIANT_BOOL IncludeCurrentDay, long *ReportCount)
{
   try
   {
      if (!ReportCount)
         return COMError::GenerateGenericMessage();

      *ReportCount = 0;

      // Sends mail to third parties, so it is an administrator's call to make.
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();

      // Refused rather than run: SendReportsNow POPS each day's statistics, and
      // with no sender address it can only discard what it popped. A diagnostic
      // that destroys the data it was asked to show - silently, returning 0 as
      // if there were simply nothing to send - would be worse than no
      // diagnostic. The hourly task keeps its discard behaviour, which is what
      // bounds the store's memory on an unconfigured server; this entry point
      // exists for an administrator who believes reporting is set up, so tell
      // them when it is not.
      if (HM::IniFileSettings::Instance()->GetTlsRptFromAddress().IsEmpty())
         return COMError::GenerateError("TlsRptFromAddress is not set in the [Settings] section of hMailServer.ini, so no report can be sent. The collected statistics have been preserved.");

      *ReportCount = HM::TlsRptReporterTask::SendReportsNow(IncludeCurrentDay != VARIANT_FALSE);

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceUtilities::SendDmarcReports(VARIANT_BOOL IncludeCurrentDay, long *ReportCount)
{
   try
   {
      if (!ReportCount)
         return COMError::GenerateGenericMessage();

      *ReportCount = 0;

      // Sends mail to third parties, so it is an administrator's call to make.
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();

      // Refused for the same reason SendTlsRptReports refuses: the send pass
      // POPS each day's statistics, and with no sender address it can only
      // discard what it popped. A diagnostic that destroys the data it was
      // asked to show would be worse than none.
      if (HM::IniFileSettings::Instance()->GetDmarcRptFromAddress().IsEmpty())
         return COMError::GenerateError("DmarcRptFromAddress is not set in the [Settings] section of hMailServer.ini, so no report can be sent. The collected statistics have been preserved.");

      *ReportCount = HM::DmarcRptReporterTask::SendReportsNow(IncludeCurrentDay != VARIANT_FALSE);

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceUtilities::EraseAddressTraces(BSTR Address, VARIANT_BOOL IncludeArchive, long *RemovedCount)
{
   try
   {
      if (!RemovedCount)
         return COMError::GenerateGenericMessage();

      *RemovedCount = 0;

      // Destroys records across half a dozen stores, so it is the server
      // administrator's call - a domain administrator's authority ends at
      // their domain, and greylisting triplets and quarantine rows do not
      // belong to one.
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();

      HM::String address = Address;
      address.Trim();

      if (address.IsEmpty() || address.Find(_T("@")) <= 0)
         return COMError::GenerateError("A full email address must be given.");

      int removed = 0;
      if (!HM::AddressTraceEraser::Erase(address, IncludeArchive != VARIANT_FALSE, removed))
      {
         *RemovedCount = removed;
         return COMError::GenerateError("Some traces could not be removed - the hMailServer error log names the stores to retry. What could be removed has been.");
      }

      *RemovedCount = removed;
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceUtilities::GetMailServer(BSTR EMailAddress, BSTR *MailServer)
{
   try
   {
      HM::String sDomainName;
      HM::String sEMail(EMailAddress);
   
      sDomainName = HM::StringParser::ExtractDomain (EMailAddress);
   
      std::vector<HM::String> saDomainNames;

      std::vector<HM::HostNameAndIpAddress> hostname_and_ipaddresses;
   
      HM::DNSResolver oDNSResolver;
      oDNSResolver.GetEmailServers(sDomainName, hostname_and_ipaddresses);
   
      HM::String sMailServer = "";
      for (unsigned int i = 0; i < hostname_and_ipaddresses.size(); i++)
      {
         if (!sMailServer.IsEmpty())
            sMailServer += ",";
   
         sMailServer += hostname_and_ipaddresses[i].GetIpAddress();
      }
   
      *MailServer = sMailServer.AllocSysString();
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceUtilities::MD5(BSTR Input, BSTR *Output)
{
   try
   {
      HM::String sInput(Input);
   
      HM::String sOutput = HM::Crypt::Instance()->EnCrypt(sInput, HM::Crypt::ETMD5);
   
      *Output = sOutput.AllocSysString();
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceUtilities::SHA256(BSTR Input, BSTR *Output)
{
   try
   {
      HM::String sInput(Input);
   
      HM::String sOutput = HM::Crypt::Instance()->EnCrypt(sInput, HM::Crypt::ETSHA256);
   
      *Output = sOutput.AllocSysString();
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceUtilities::CheckSieveSyntax(BSTR Script, BSTR *Result)
{
   try
   {
      HM::String sScript(Script);

      HM::String sError = HM::SieveScript::CheckSyntax(sScript);

      *Result = sError.AllocSysString();

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceUtilities::EvaluateSieveScript(BSTR Script, BSTR RawMessage, BSTR *Result)
{
   try
   {
      HM::String sScript(Script);
      HM::String sRawMessage(RawMessage);

      HM::String sResult = HM::SieveScript::Evaluate(sScript, sRawMessage);

      *Result = sResult.AllocSysString();

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceUtilities::BlowfishEncrypt(BSTR Input, BSTR *Output)
{
   try
   {
      HM::String sInput(Input);  
   
      HM::String sOutput = HM::Crypt::Instance()->EnCrypt(sInput, HM::Crypt::ETBlowFish);
   
      *Output = sOutput.AllocSysString();
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceUtilities::BlowfishDecrypt(BSTR Input, BSTR *Output)
{
   try
   {
      HM::String sInput(Input);
   
      HM::String sOutput = HM::Crypt::Instance()->DeCrypt(sInput, HM::Crypt::ETBlowFish);
   
      *Output = sOutput.AllocSysString();
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceUtilities::GenerateGUID(BSTR *Output)
{
   try
   {
      HM::String sOutput = HM::GUIDCreator::GetGUID();
      *Output = sOutput.AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceUtilities::IsValidEmailAddress(BSTR EMailAddress, VARIANT_BOOL *bIsValid)
{
   try
   {
      bool bValid = HM::StringParser::IsValidEmailAddress(EMailAddress);
      if (bValid)
         *bIsValid = VARIANT_TRUE;
      else
         *bIsValid = VARIANT_FALSE;
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceUtilities::IsValidIPAddress(BSTR IPAddress, VARIANT_BOOL *bIsValid)
{
   try
   {
      HM::Utilities utilities;
      
      *bIsValid = utilities.IsValidIPAddress(IPAddress) ? VARIANT_TRUE : VARIANT_FALSE;
      
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceUtilities::IsStrongPassword(BSTR Username, BSTR Password, VARIANT_BOOL *bIsValid)
{
   try
   {
      bool bStrong = HM::PasswordGenerator::IsStrongPassword(Username, Password);
   
      if (bStrong)
         *bIsValid = VARIANT_TRUE;
      else
         *bIsValid = VARIANT_FALSE;
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceUtilities::IsValidDomainName(BSTR sDomainName, VARIANT_BOOL *bIsValid)
{
   try
   {
      bool bValid = HM::StringParser::IsValidDomainName(sDomainName);
      if (bValid)
         *bIsValid = VARIANT_TRUE;
      else
         *bIsValid = VARIANT_FALSE;
   
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceUtilities::IsLocalHost(BSTR sHostname, VARIANT_BOOL *bIsValid)
{
   try
   {
      *bIsValid = HM::Utilities::IsLocalHost(sHostname) ? VARIANT_TRUE : VARIANT_FALSE;
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceUtilities::CriteriaMatch(BSTR MatchValue, eRuleMatchType matchType, BSTR TestValue, VARIANT_BOOL *bMatch)
{
   try
   {
      HM::RuleApplier ruleApplier;
   
      *bMatch = ruleApplier.TestMatch(MatchValue, (HM::RuleCriteria::MatchType) matchType, TestValue) ? VARIANT_TRUE : VARIANT_FALSE;
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceUtilities::MakeDependent(BSTR sOtherService)
{
   try
   {
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();
   
      HM::ServiceManager oManager; 
      oManager.MakeDependentOn(sOtherService);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceUtilities::ImportMessageFromFile(BSTR sFilename, long iAccountID, VARIANT_BOOL *bIsSuccessful)
{
   try
   {
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();
   
      *bIsSuccessful = HM::MailImporter::Import(sFilename, iAccountID, "") ? VARIANT_TRUE : VARIANT_FALSE;
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceUtilities::RetrieveMessageID(BSTR sFilename, hyper *messageID)
{
   try
   {
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();
   
      __int64 id = 0;
      bool partialFileName;
      bool result = HM::PersistentMessage::GetMessageID(sFilename, id, partialFileName);
   
      *messageID = id;
      
      if (result == false)
         return COMError::GenerateError("Retrieval of message-ID failed. Please check hMailServer error log.");
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceUtilities::ImportMessageFromFileToIMAPFolder(BSTR sFilename, long iAccountID, BSTR sIMAPFolder, VARIANT_BOOL *bIsSuccessful)
{
   try
   {
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();
   
      *bIsSuccessful = HM::MailImporter::Import(sFilename, iAccountID, sIMAPFolder) ? VARIANT_TRUE : VARIANT_FALSE;
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP
InterfaceUtilities::EmailAllAccounts(BSTR sRecipientWildcard, BSTR sFromAddress, BSTR sFromName, BSTR sSubject, BSTR sBody, VARIANT_BOOL *bIsSuccessful)
{
   try
   {
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();
   
      HM::EmailAllUsers oEmailAllUsers;
      *bIsSuccessful = oEmailAllUsers.Start(sRecipientWildcard, sFromAddress, sFromName, sSubject, sBody) ? VARIANT_TRUE : VARIANT_FALSE;
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceUtilities::RunTestSuite(BSTR sTestPassword)
{
   try
   {
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();
   
      HM::String sCorrectPassword = _T("I know what I am doing.");
      HM::String sPassword = sTestPassword;
   
      if (sPassword.Compare(sCorrectPassword) != 0)
         return S_FALSE;
   
      HM::ClassTester oTester;
      oTester.DoTests();
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceUtilities::PerformMaintenance(eMaintenanceOperation operation)
{
   try
   {
      if (!authentication_->GetIsServerAdmin())
         return authentication_->GetAccessDenied();
   
      HM::Maintenance maintenance;
   
      HM::Maintenance::MaintenanceOperation internalOperation;
   
      switch (operation)
      {
      case eUpdateIMAPFolderUID:
         internalOperation = HM::Maintenance::RecalculateFolderUID;
         break;
      default:
         return COMError::GenerateError("Unknown maintenance operation.");
      }
   
      bool result = maintenance.Perform(internalOperation);
      if (result == false)
         return COMError::GenerateError("The maintenance operation failed. Please see hMailServer log for details.");
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

