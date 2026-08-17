// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "IMAPCommandAuthenticate.h"
#include "IMAPConnection.h"
#include "IMAPConfiguration.h"
#include "IMAPSimpleCommandParser.h"
#include "../common/Application/DefaultDomain.h"
#include "../common/Application/IniFileSettings.h"
#include "../common/Application/ObjectCache.h"
#include "../common/Cache/CacheContainer.h"
#include "../common/Util/AccountLogon.h"
#include "../common/Util/AccountLockout.h"
#include "../common/Util/Crypt.h"
#include "../common/Util/Hashing/ScramSha256.h"
#include "../common/Util/OAuth2TokenValidator.h"
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
		String sParam, authzid, authcid, password;
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

		if (sParam == _T("XOAUTH2") || sParam == _T("OAUTHBEARER"))
		{
			if (!OAuth2TokenValidator::IsEnabled())
				return IMAPResult(IMAPResult::ResultNo, "OAuth2 authentication is not enabled.");

			if (OAuth2TokenValidator::RequireTLS() && !pConnection->IsSSLConnection())
				return IMAPResult(IMAPResult::ResultBad, "A SSL/TLS-connection is required for authentication.");

			if (paramcount == 1)
			{
				// No initial response: ask the client for the SASL message via a continuation.
				pConnection->SetCommandBuffer(pArgument->Tag() + " AUTHENTICATE " + sParam + " ");
				pConnection->SendAsciiData("+ \r\n");
				return IMAPResult();
			}

			String sResponse64 = pParser->GetParamValue(pArgument, 1);

			// A bare "*" cancels the SASL exchange (RFC 3501).
			if (sResponse64 == _T("*"))
				return IMAPResult(IMAPResult::ResultBad, "Authentication cancelled.");

			// The XOAUTH2 / OAUTHBEARER client response is ASCII, so the standard base64
			// decode is sufficient here.
			String sDecodedW;
			StringParser::Base64Decode(sResponse64, sDecodedW);
			AnsiString sDecoded = sDecodedW;

			AnsiString sIdentity, sToken;
			String sTokenUser;
			bool authenticated = false;
			if (OAuth2TokenValidator::ParseSaslBearer(sDecoded, sIdentity, sToken))
			{
				AnsiString sError;
				if (OAuth2TokenValidator::ValidateBearerToken(sToken, sTokenUser, sError))
				{
					// When the client also asserts an identity it must match the token.
					AnsiString sTokenUserA = sTokenUser;
					if (sIdentity.IsEmpty() || sIdentity.CompareNoCase(sTokenUserA) == 0)
						authenticated = true;
				}
			}

			std::shared_ptr<const Account> pAccount;
			String sLoginName = sTokenUser;
			if (authenticated)
			{
				std::shared_ptr<DomainAliases> pDA = ObjectCache::Instance()->GetDomainAliases();
				String sAddress = pDA->ApplyAliasesOnAddress(sTokenUser);
				sAddress = DefaultDomain::ApplyDefaultDomain(sAddress);
				sLoginName = sAddress;

				std::shared_ptr<const Account> pCandidate = CacheContainer::Instance()->GetAccount(sAddress);
				if (pCandidate && pCandidate->GetActive())
				{
					String sDomain = StringParser::ExtractDomain(sAddress);
					std::shared_ptr<const Domain> pDomain = CacheContainer::Instance()->GetDomain(sDomain);
					if (pDomain && pDomain->GetIsActive())
						pAccount = pCandidate;
				}
			}

			pConnection->FireOnClientLogon(sLoginName, pAccount != nullptr);

			if (!pAccount)
			{
				// Feed the per-IP auto-ban accounting, then the per-connection cap. Deliberately not the per-name lockout: a bearer token is not guessable, and a client looping on an expired one would lock its own user out of every password client (see AccountLogon.h).
				AccountLogon accountLogon;
				bool disconnect = false;
				accountLogon.RegisterFailedLogin(pConnection->GetRemoteEndpointAddress(), sLoginName, disconnect, false);

				if (disconnect || pConnection->RegisterAuthenticationFailure())
				{
					String sResponse = "* Too many invalid logon attempts.\r\n";
					sResponse += pArgument->Tag() + " BAD Goodbye\r\n";
					pConnection->Logout(sResponse);

					return IMAPResult(IMAPResult::ResultOKSupressRead, "");
				}

				return IMAPResult(IMAPResult::ResultNo, "Invalid authentication token.");
			}

			pConnection->Login(pAccount);

			String sResponse = pArgument->Tag() + " OK LOGIN completed\r\n";
			pConnection->SendAsciiData(sResponse);

			return IMAPResult();
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
		if (!StringParser::DecodeSaslPlain(sParam, authzid, authcid, password))
			return IMAPResult(IMAPResult::ResultBad, "Command has malformed base64 token.");

		if (authcid.GetLength() == 0)
			return IMAPResult(IMAPResult::ResultBad, "Command is missing username.");

		// RFC 4422/4013: prepare the authcid (username) with SASLprep before lookup.
		if (!StringParser::SaslPrep(authcid, authcid))
			return IMAPResult(IMAPResult::ResultBad, "Command has an invalid username.");

		if (password.GetLength() == 0)
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

			// Three things were wrong here, and none of them could be seen from one
			// branch alone. The feature has no test of any kind - TestSetup only
			// clears IMAPMasterUser - which is how all three survived.
			//
			// 1. The two branches resolved a bare master name against DIFFERENT
			//    domains: the server's default domain when authzid arrived without
			//    one, and the authenticating account's domain when it arrived with
			//    one. So which account held impersonation privilege depended on how
			//    the client happened to format its input, and on a multi-domain
			//    server those are two different accounts. They agree only when the
			//    authenticating account is in the default domain - the single-domain
			//    install, i.e. exactly the deployment where nobody would notice.
			//
			//    Resolved against the authenticating account's domain in both cases
			//    now. That is the narrower of the two grants - a bare "admin" means
			//    "admin in the domain of whoever is authenticating", so a master can
			//    only ever impersonate within their own domain - and it is what the
			//    qualified branch already did.
			//
			// 2. That branch appended a domain WITHOUT asking whether the configured
			//    master user already had one, so "admin@example.com" became
			//    "admin@example.com@example.com" and could never match. A master user
			//    configured as a full address simply did not work, and the client was
			//    told "Invalid master user" whatever it did. ApplyDefaultDomain on the
			//    sibling branch is careful about precisely this case.
			//
			// 3. The comparison was std::wstring::compare, which is case-sensitive,
			//    while every other address comparison in this tree uses CompareNoCase.
			//    "Admin@example.com" was refused where "admin@example.com" was not.
			//
			// All three failed closed, so this is a security feature that did not
			// work rather than a hole - but a master user who cannot log in is
			// indistinguishable from one whose password is wrong, and that is a bad
			// way to spend an afternoon.

			// The account being impersonated is canonicalised exactly as authcid was.
			if (authzid.Find(_T("@")) == -1)
			{
				if (sDefaultDomain.IsEmpty())
					return IMAPResult(IMAPResult::ResultNo, "Invalid user name. Please use full email address as user name.");

				authzid = DefaultDomain::ApplyDefaultDomain(authzid);
			}

			// And so is the configured master user - only when it needs it.
			if (imapmasteruser.Find(_T("@")) == -1)
				imapmasteruser += "@" + StringParser::ExtractDomain(authcid);

			if (imapmasteruser.CompareNoCase(authcid) != 0)
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

		pConnection->FireOnClientLogon(authcid, pAccount != nullptr);

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
      String sUsername = session->GetUsername();

      pConnection->SetScramSession(nullptr);

      pConnection->FireOnClientLogon(sUsername, pAccount != nullptr);

      if (!pAccount)
         return IMAPResult(IMAPResult::ResultNo, "Invalid user name or password.");

      // The name authenticated, so its failure counters go - the same clearing
      // AccountLogon::Logon performs on the LOGIN/PLAIN path. Without it a user
      // who mistypes twice and then succeeds over SCRAM carries those failures
      // for ever, and one later slip locks them out.
      AccountLockout::Instance()->RecordSuccess(sUsername);

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
      // Whether there was ever a key to verify against. When the helper returned
      // no account - an unknown name, an Argon2id or Active Directory account, a
      // hash policy that excludes PBKDF2, or a name already locked - the exchange
      // was a forced failure that no password could have passed, so it is not a
      // password guess and must not count towards the per-name lockout. See the
      // fuller note in POP3Connection::ScramAuthFailed_.
      std::shared_ptr<ScramSha256> failedSession = pConnection->GetScramSession();
      const bool verifiableAccount = failedSession && failedSession->GetAccount() != nullptr;

      pConnection->SetScramSession(nullptr);

      pConnection->FireOnClientLogon(sUsername, false);

      // Feed the per-IP auto-ban accounting (parity with the LOGIN/PLAIN path) -
      // unless the exchange was doomed by the lock itself, in which case nothing
      // is reported, exactly as AccountLogon::Logon's locked branch does: the
      // owner retrying a locked name with the CORRECT password must not get their
      // address banned for everyone behind it. Not extended to the other
      // forced-failure causes (unknown name, Argon2id/AD account, hash policy),
      // which would let an attacker spray names over SCRAM for free. See the
      // fuller note in POP3Connection::ScramAuthFailed_.
      bool disconnect = false;

      if (!AccountLockout::Instance()->IsLockedOut(sUsername))
      {
         AccountLogon accountLogon;
         accountLogon.RegisterFailedLogin(pConnection->GetRemoteEndpointAddress(), sUsername, disconnect, verifiableAccount);
      }

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

      // A locked name is treated exactly as an account that cannot serve SCRAM:
      // the empty handle runs the forced-failure exchange documented at the call
      // site, so the lock is enforced here without a second refusal shape for an
      // attacker to tell apart. Without it the lock bound only the LOGIN/PLAIN
      // path, so an attacker chose SCRAM and guessed freely while their attempts
      // still locked the victim out of every password client.
      if (AccountLockout::Instance()->IsLockedOut(sAccountAddress))
         return std::shared_ptr<const Account>();

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

      // Honour the MinimumAcceptedHashAlgorithm policy: SCRAM can only be served from a
      // PBKDF2 hash, so when the administrator requires a stronger hash type than PBKDF2
      // no account is eligible. Returning an empty handle makes the exchange a forced
      // failure (the same as an unknown account) rather than revealing the policy.
      if (IniFileSettings::Instance()->GetMinimumAcceptedHashAlgorithm() > Crypt::ETPBKDF2)
         return std::shared_ptr<const Account>();

      if (pAccount->GetPasswordEncryption() != Crypt::ETPBKDF2)
         return std::shared_ptr<const Account>();

      return pAccount;
   }
}
