// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "IMAPCommandCapability.h"
#include "IMAPConnection.h"
#include "IMAPConfiguration.h"
#include "../common/Util/OAuth2TokenValidator.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   IMAPResult
   IMAPCommandCapability::ExecuteCommand(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument)
   {
      String sResponse = "* CAPABILITY IMAP4 IMAP4rev1 IMAP4rev2 CHILDREN";
      
      std::shared_ptr<IMAPConfiguration> pConfig = Configuration::Instance()->GetIMAPConfiguration();

      if (pConfig->GetUseIMAPIdle())
         sResponse += " IDLE";

      if (pConfig->GetUseIMAPQuota())
         sResponse += " QUOTA";

      if (pConfig->GetUseIMAPSort())
         sResponse += " SORT";

      if (pConfig->GetUseIMAPACL())
         sResponse += " ACL";

      if (pConnection->GetConnectionSecurity() == CSSTARTTLSOptional ||
          pConnection->GetConnectionSecurity() == CSSTARTTLSRequired)
         sResponse += " STARTTLS";

      if (pConfig->GetUseIMAPSASLPlain() && (pConnection->IsSSLConnection() || pConnection->GetConnectionSecurity() != CSSTARTTLSRequired))
	      sResponse += " AUTH=PLAIN";

      // SCRAM-SHA-256 (RFC 7677) never transmits the password, so it is offered
      // alongside AUTH=PLAIN whenever IMAP AUTHENTICATE is enabled.
      if (pConfig->GetUseIMAPSASLPlain() && (pConnection->IsSSLConnection() || pConnection->GetConnectionSecurity() != CSSTARTTLSRequired))
	      sResponse += " AUTH=SCRAM-SHA-256";

      // SCRAM-SHA-256-PLUS (RFC 5802/5929) additionally binds the authentication to
      // the TLS channel via the server certificate, so it is only meaningful — and
      // only advertised — on a TLS connection.
      if (pConfig->GetUseIMAPSASLPlain() && pConnection->IsSSLConnection())
	      sResponse += " AUTH=SCRAM-SHA-256-PLUS";

      // OAuth2 bearer mechanisms (RFC 7628), advertised only when enabled and (by
      // default) only over TLS.
      if (OAuth2TokenValidator::IsEnabled() && (!OAuth2TokenValidator::RequireTLS() || pConnection->IsSSLConnection()))
	      sResponse += " AUTH=XOAUTH2 AUTH=OAUTHBEARER";

      if (pConfig->GetUseIMAPSASLInitialResponse())
	      sResponse += " SASL-IR";

      // RFC 6154 section 6 defines two capability names and a client needs both. Only
      // SPECIAL-USE used to be advertised, which says the attributes and the LIST
      // options are understood; CREATE-SPECIAL-USE is the one a client checks before it
      // will send CREATE ... USE (\Sent), so without it the clients that want to create
      // their own sent folder - Apple Mail, Outlook mobile - never ask the server to
      // designate it, and go back to naming it and hoping.
      // WITHIN (RFC 5032) is advertised because SEARCH now implements OLDER and YOUNGER.
      // A client will not send a relative-age search without seeing it, and after the
      // SEARCH parser started refusing keys it does not know, sending one unannounced
      // would have been answered with BAD rather than silently ignored.
      sResponse += " NAMESPACE RIGHTS=texk MOVE ID SPECIAL-USE CREATE-SPECIAL-USE UNSELECT UIDPLUS ENABLE STATUS=SIZE ESEARCH CONDSTORE QRESYNC LIST-EXTENDED SEARCHRES WITHIN UTF8=ACCEPT";

      sResponse += "\r\n";
      sResponse += pArgument->Tag() + " OK CAPABILITY completed\r\n";

      pConnection->SendAsciiData(sResponse);   
 
      return IMAPResult();
   }
}