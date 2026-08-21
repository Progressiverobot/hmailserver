// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"

#include "IMAPCommandNamespace.h"
#include "IMAPConnection.h"
#include "IMAPSimpleCommandParser.h"

#include "IMAPConfiguration.h"

#include "../Common/Application/ACLManager.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   IMAPResult
   IMAPCommandNamespace::ExecuteCommand(std::shared_ptr<HM::IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument)
   {
      if (!pConnection->IsAuthenticated())
         return IMAPResult(IMAPResult::ResultNo, "Authenticate first");

      std::shared_ptr<IMAPSimpleCommandParser> pParser = std::shared_ptr<IMAPSimpleCommandParser>(new IMAPSimpleCommandParser());
      pParser->Parse(pArgument);

      if (pParser->WordCount() < 1)
         return IMAPResult(IMAPResult::ResultBad, "NAMESPACE command requires exactly 0 parameter.");

      String sPublicFolderName = Configuration::Instance()->GetIMAPConfiguration()->GetIMAPPublicFolderName();
      String hierarchyDelimiter = Configuration::Instance()->GetIMAPConfiguration()->GetHierarchyDelimiter();
      hierarchyDelimiter.Replace(_T("\\"), _T("\\\\"));

      String sPersonalNamespace = "((\"\" \"" + hierarchyDelimiter + "\"))";

      // RFC 2342, the Other Users namespace: where other accounts' folders appear
      // when their owners have granted this user rights on them (shared/delegated
      // mailboxes). The prefix is advertised without a trailing delimiter, the
      // same convention this response has always used for the public namespace.
      // With ACL enforcement off the namespace does not exist and NIL is
      // advertised - see ACLManager::GetOtherUsersNamespaceEnabled.
      String sOtherUsersNamespace = "NIL";
      if (ACLManager::GetOtherUsersNamespaceEnabled())
         sOtherUsersNamespace = "((\"" + ACLManager::GetOtherUsersFolderName() + "\" \"" + hierarchyDelimiter + "\"))";

      String sSharedNamespaces = "((\"" + sPublicFolderName + "\" \"" + hierarchyDelimiter + "\"))";
      String sResponse;
      
      sResponse.Format(_T("* NAMESPACE %s %s %s\r\n"), sPersonalNamespace.c_str(), sOtherUsersNamespace.c_str(), sSharedNamespaces.c_str());
      sResponse += pArgument->Tag() + _T(" OK namespace command complete\r\n");

      pConnection->SendAsciiData(sResponse);   

      return IMAPResult();
   }
}