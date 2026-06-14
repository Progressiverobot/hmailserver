// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "IMAPCommandAuthenticate.h"
#include "IMAPConnection.h"
#include "IMAPConfiguration.h"
#include "IMAPSimpleCommandParser.h"
#include "../common/Application/DefaultDomain.h"
#include "../common/Application/ObjectCache.h"
#include "../common/Cache/CacheContainer.h"
#include "../common/Util/AccountLogon.h"
#include "../common/Util/Crypt.h"
#include "../common/Util/Hashing/ScramSha256.h"
#include "../common/BO/Account.h"
#include "../common/BO/Domain.h"
#include "../common/BO/DomainAliases.h"
#include "../common/BO/SecurityRange.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
	IMAPResult
   IMAPCommandAUTHENTICATE::ExecuteCommand(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument)
	{
      if (!Configuration::Instance()->GetIMAPConfiguration()->GetUseIMAPSASLPlain())
         return IMAPResult(IMAPResult::ResultNo, "IMAP AUTHENTICATE is not enabled.");

      String imapmasteruser = Configuration::Instance()->GetIMAPConfiguration()->GetIMAPMasterUser();
		String sParam, authzid, authcid, password, sDecode64;
		String sDefaultDomain = Configuration::Instance()->GetDefaultDomain();

		if (pConnection->GetConnectionSecurity() == CSSTARTTLSRequired)
		{
			if (!pConnection->IsSSLConnection())
			{
				return IMAPResult(IMAPResult::ResultBad, "STARTTLS is required.");
			}
		}

		if (pConnection->GetSecurityRange()->GetRequireTLSForAuth() && !pConnection->IsSSLConnection())
		{
			return IMAPResult(IMAPResult::ResultBad, "A SSL/TLS-connection is required for authentication.");
		}

		std::shared_ptr<IMAPSimpleCommandParser> pParser = std::shared_ptr<IMAPSimpleCommandParser>(new IMAPSimpleCommandParser());

		pParser->Parse(pArgument);

		size_t paramcount = pParser->ParamCount();

		// Continuation line of an in-progress SCRAM-SHA-256 exchange. Once a SCRAM
		// session exists on the connection, every subsequent line belongs to it.
		if (pConnection->GetScramSession())
		{
			String sClientData = paramcount >= 2 ? pParser->GetParamValue(pArgument, 1) : String();
			return ContinueScram_(pConnection, pArgument, sClientData);
		}

		if (paramcount < 1 || paramcount > 2)
			return IMAPResult(IMAPResult::ResultBad, "Unsupported Authenticate mechanism.");

		sParam = pParser->GetParamValue(pArgument, 0);

		if (sParam == _T("SCRAM-SHA-256-PLUS"))
		{
			// Channel binding only has meaning over TLS; the mechanism is advertised
			// (and accepted) only on a TLS connection.
			if (!pConnection->IsSSLConnection())
				return IMAPResult(IMAPResult::ResultBad, "SCRAM-SHA-256-PLUS requires a TLS connection.");

			String sInitialResponse = paramcount == 2 ? pParser->GetParamValue(pArgument, 1) : String();
			return StartScram_(pConnection, pArgument, sInitialResponse, paramcount == 2, true);
		}

		if (sParam == _T("SCRAM-SHA-256"))
		{
			String sInitialResponse = paramcount == 2 ? pParser->GetParamValue(pArgument, 1) : String();
			return StartScram_(pConnection, pArgument, sInitialResponse, paramcount == 2, false);
		}

		if (sParam != _T("PLAIN"))
			return IMAPResult(IMAPResult::ResultBad, "Unsupported Authenticate mechanism.");

		if (paramcount == 1)
		{
			pConnection->SetCommandBuffer(pArgument->Tag() + " AUTHENTICATE PLAIN ");
			pConnection->SendAsciiData("+ \r\n");
			return IMAPResult();
		}

		sParam = pParser->GetParamValue(pArgument, 1);
		StringParser::Base64Decode(sParam, sDecode64);
		std::vector<String> plain_args = StringParser::SplitString(sDecode64, "\t");

		if (plain_args.size() != 3)
			return IMAPResult(IMAPResult::ResultBad, "Command has malformed base64 token.");

		authzid = plain_args[0];

		authcid = plain_args[1];
		if (plain_args[1].GetLength() == 0)
			return IMAPResult(IMAPResult::ResultBad, "Command is missing username.");

		password = plain_args[2];
		if (plain_args[2].GetLength() == 0)
			return IMAPResult(IMAPResult::ResultBad, "Command is missing password.");

		// we don't really need to canonicalize the username(s), but it makes it much
		// cleaner and safer to not have to worry about who has a domain name in their
		// user name

		if (authcid.Find(_T("@")) == -1)
		{
			if (sDefaultDomain.IsEmpty())
				return IMAPResult(IMAPResult::ResultNo, "Invalid user name. Please use full email address as user name.");

			authcid = DefaultDomain::ApplyDefaultDomain(authcid);
		}

		// if the client specified two usernames, the first is who we will be acting as,
		// the second is who we authenticate as.  make sure the client isn't trying to
		// pull a fast one (or is confused)

		if (authzid.GetLength())
		{
			if (imapmasteruser.GetLength() == 0)
				return IMAPResult(IMAPResult::ResultBad, "No master user defined.");

			if (authzid.Find(_T("@")) == -1)
			{
				if (sDefaultDomain.IsEmpty())
					return IMAPResult(IMAPResult::ResultNo, "Invalid user name. Please use full email address as user name.");

				imapmasteruser = DefaultDomain::ApplyDefaultDomain(imapmasteruser);
				authzid = DefaultDomain::ApplyDefaultDomain(authzid);
			}
			else
				imapmasteruser += "@" + StringParser::ExtractDomain(authcid);

			if (imapmasteruser.compare(authcid))
				return IMAPResult(IMAPResult::ResultBad, "Invalid master user.");
		}

		AccountLogon accountLogon;
		bool disconnect = false;
		std::shared_ptr<const Account> pAccount = accountLogon.Logon(pConnection->GetRemoteEndpointAddress(), authzid, authcid, password, disconnect);

		if (disconnect)
		{
			String sResponse = "* Too many invalid logon attempts.\r\n";
			sResponse += pArgument->Tag() + " BAD Goodbye\r\n";
			pConnection->Logout(sResponse);

			return IMAPResult(IMAPResult::ResultOKSupressRead, "");
		}

		if (!pAccount)
		{
			if (pConnection->RegisterAuthenticationFailure())
			{
				String sResponse = "* Too many invalid logon attempts.\r\n";
				sResponse += pArgument->Tag() + " BAD Goodbye\r\n";
				pConnection->Logout(sResponse);

				return IMAPResult(IMAPResult::ResultOKSupressRead, "");
			}

			return IMAPResult(IMAPResult::ResultNo, "Invalid user name or password.");
		}

		// Load mail boxes
		pConnection->Login(pAccount);

		String sResponse = pArgument->Tag() + " OK LOGIN completed\r\n";

		pConnection->SendAsciiData(sResponse);

		return IMAPResult();
	}

   IMAPResult
   IMAPCommandAUTHENTICATE::StartScram_(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument, const String &sInitialResponse, bool bHasInitialResponse, bool bPlus)
   {
      // Begin a fresh SCRAM-SHA-256 conversation for this connection.
      std::shared_ptr<ScramSha256> session = std::make_shared<ScramSha256>();

      if (bPlus)
      {
         // SCRAM-SHA-256-PLUS: bind the exchange to this TLS channel via the
         // server certificate (RFC 5929 tls-server-end-point).
         std::vector<unsigned char> cbindData;
         if (!pConnection->GetTlsServerEndPoint(cbindData))
            return IMAPResult(IMAPResult::ResultBad, "Channel binding is not available on this connection.");

         session->SetChannelBinding(cbindData);
      }
      else if (pConnection->IsSSLConnection())
      {
         // The non-PLUS mechanism is being used on a TLS connection where PLUS is
         // advertised, so reject a stripped-PLUS downgrade (a 'y' gs2 flag).
         session->SetServerSupportsChannelBinding();
      }

      pConnection->SetScramSession(session);

      if (!bHasInitialResponse)
      {
         // No SASL-IR: ask the client for the client-first message via a continuation.
         pConnection->SetCommandBuffer(pArgument->Tag() + " AUTHENTICATE SCRAM-SHA-256 ");
         pConnection->SendAsciiData("+ \r\n");
         return IMAPResult();
      }

      return ProcessScramClientFirst_(pConnection, pArgument, sInitialResponse);
   }

   IMAPResult
   IMAPCommandAUTHENTICATE::ContinueScram_(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument, const String &sClientData)
   {
      std::shared_ptr<ScramSha256> session = pConnection->GetScramSession();
      if (!session)
         return IMAPResult(IMAPResult::ResultBad, "No authentication in progress.");

      // A bare "*" cancels the SASL exchange (RFC 3501).
      if (sClientData == _T("*"))
         return AbortScram_(pConnection, pArgument, "AUTHENTICATE cancelled.");

      switch (session->GetState())
      {
      case ScramSha256::NeedClientFirst:
         return ProcessScramClientFirst_(pConnection, pArgument, sClientData);
      case ScramSha256::NeedClientFinal:
         return ProcessScramClientFinal_(pConnection, pArgument, sClientData);
      case ScramSha256::NeedAck:
         return FinishScram_(pConnection, pArgument);
      default:
         return AbortScram_(pConnection, pArgument, "Invalid authentication state.");
      }
   }

   IMAPResult
   IMAPCommandAUTHENTICATE::ProcessScramClientFirst_(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument, const String &sClientData)
   {
      std::shared_ptr<ScramSha256> session = pConnection->GetScramSession();

      String sDecoded;
      StringParser::Base64Decode(sClientData, sDecoded);
      AnsiString clientFirst = sDecoded;

      AnsiString username;
      if (!ScramSha256::ExtractUsername(clientFirst, username))
         return AbortScram_(pConnection, pArgument, "Invalid SCRAM client-first message.");

      // Canonicalize the user name the same way the PLAIN path does.
      String sUsername = username;
      if (sUsername.Find(_T("@")) == -1)
      {
         String sDefaultDomain = Configuration::Instance()->GetDefaultDomain();
         if (!sDefaultDomain.IsEmpty())
            sUsername = DefaultDomain::ApplyDefaultDomain(sUsername);
      }
      session->SetUsername(sUsername);

      // Only a PBKDF2-hashed account can serve SCRAM (its stored key is the SCRAM
      // SaltedPassword). For any other account the helper runs a forced-failure
      // exchange so the protocol does not reveal whether the account exists.
      AnsiString storedHash = "";
      std::shared_ptr<const Account> pAccount = LookupPbkdf2Account_(sUsername);
      if (pAccount)
      {
         session->SetAccount(pAccount);
         storedHash = pAccount->GetPassword();
      }

      AnsiString serverFirst;
      if (!session->ProcessClientFirst(clientFirst, storedHash, serverFirst))
         return AbortScram_(pConnection, pArgument, "Invalid SCRAM client-first message.");

      String sServerFirst = serverFirst;
      String sEncoded;
      StringParser::Base64Encode(sServerFirst, sEncoded);

      pConnection->SetCommandBuffer(pArgument->Tag() + " AUTHENTICATE SCRAM-SHA-256 ");
      pConnection->SendAsciiData("+ " + sEncoded + "\r\n");
      return IMAPResult();
   }

   IMAPResult
   IMAPCommandAUTHENTICATE::ProcessScramClientFinal_(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument, const String &sClientData)
   {
      std::shared_ptr<ScramSha256> session = pConnection->GetScramSession();

      String sDecoded;
      StringParser::Base64Decode(sClientData, sDecoded);
      AnsiString clientFinal = sDecoded;

      AnsiString serverFinal;
      if (!session->ProcessClientFinal(clientFinal, serverFinal))
         return ScramAuthFailed_(pConnection, pArgument, session->GetUsername());

      String sServerFinal = serverFinal;
      String sEncoded;
      StringParser::Base64Encode(sServerFinal, sEncoded);

      pConnection->SetCommandBuffer(pArgument->Tag() + " AUTHENTICATE SCRAM-SHA-256 ");
      pConnection->SendAsciiData("+ " + sEncoded + "\r\n");
      return IMAPResult();
   }

   IMAPResult
   IMAPCommandAUTHENTICATE::FinishScram_(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument)
   {
      std::shared_ptr<ScramSha256> session = pConnection->GetScramSession();
      std::shared_ptr<const Account> pAccount = session->GetAccount();

      pConnection->SetScramSession(nullptr);

      if (!pAccount)
         return IMAPResult(IMAPResult::ResultNo, "Invalid user name or password.");

      pConnection->Login(pAccount);

      String sResponse = pArgument->Tag() + " OK AUTHENTICATE completed\r\n";
      pConnection->SendAsciiData(sResponse);
      return IMAPResult();
   }

   IMAPResult
   IMAPCommandAUTHENTICATE::AbortScram_(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument, const String &sMessage)
   {
      pConnection->SetScramSession(nullptr);
      return IMAPResult(IMAPResult::ResultBad, sMessage);
   }

   IMAPResult
   IMAPCommandAUTHENTICATE::ScramAuthFailed_(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument, const String &sUsername)
   {
      pConnection->SetScramSession(nullptr);

      // Feed the per-IP auto-ban accounting (parity with the LOGIN/PLAIN path)...
      AccountLogon accountLogon;
      bool disconnect = false;
      accountLogon.RegisterFailedLogin(pConnection->GetRemoteEndpointAddress(), sUsername, disconnect);

      // ...and the per-connection brute-force cap (effective even when auto-ban is off).
      if (disconnect || pConnection->RegisterAuthenticationFailure())
      {
         String sResponse = "* Too many invalid logon attempts.\r\n";
         sResponse += pArgument->Tag() + " BAD Goodbye\r\n";
         pConnection->Logout(sResponse);

         return IMAPResult(IMAPResult::ResultOKSupressRead, "");
      }

      return IMAPResult(IMAPResult::ResultNo, "Invalid user name or password.");
   }

   std::shared_ptr<const Account>
   IMAPCommandAUTHENTICATE::LookupPbkdf2Account_(const String &sAddress)
   {
      std::shared_ptr<DomainAliases> pDA = ObjectCache::Instance()->GetDomainAliases();
      String sAccountAddress = pDA->ApplyAliasesOnAddress(sAddress);
      sAccountAddress = DefaultDomain::ApplyDefaultDomain(sAccountAddress);

      std::shared_ptr<const Account> pAccount = CacheContainer::Instance()->GetAccount(sAccountAddress);
      if (!pAccount || !pAccount->GetActive())
         return std::shared_ptr<const Account>();

      // Active Directory accounts authenticate via SSPI, not a stored hash.
      if (pAccount->GetIsAD())
         return std::shared_ptr<const Account>();

      String sDomain = StringParser::ExtractDomain(sAccountAddress);
      std::shared_ptr<const Domain> pDomain = CacheContainer::Instance()->GetDomain(sDomain);
      if (!pDomain || !pDomain->GetIsActive())
         return std::shared_ptr<const Account>();

      if (pAccount->GetPasswordEncryption() != Crypt::ETPBKDF2)
         return std::shared_ptr<const Account>();

      return pAccount;
   }
}
