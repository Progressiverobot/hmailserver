// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// See GssapiAcceptor.h. The wrap tokens that carry the security-layer
// exchange are what EncryptMessage and DecryptMessage produce with the
// Kerberos package - the RFC 4121 wrap token - laid out as the token buffer,
// the data and the padding in that order on the wire, which is what MIT and
// Heimdal clients unwrap; integrity only (SECQOP_WRAP_NO_ENCRYPT), as RFC
// 4752 says for the layer negotiation.

#include "StdAfx.h"
#include "GssapiAcceptor.h"
#include "AccountLogon.h"
#include "ServerStatus.h"
#include "Unicode.h"
#include "../BO/Account.h"
#include "../BO/DomainAliases.h"
#include "../Cache/CacheContainer.h"
#include "../Persistence/PersistentAccount.h"
#include "../Application/IniFileSettings.h"
#include "../Application/Application.h"
#include "../Application/ObjectCache.h"
#include "../Application/DefaultDomain.h"
#include "../SQL/SQLCommand.h"
#include "../SQL/DALRecordset.h"
#include "../TCPIP/IPAddress.h"
#include <string>
#include <vector>

#ifdef _WIN32
#define SECURITY_WIN32
#include <sspi.h>
#pragma comment(lib, "secur32.lib")
#endif

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
#ifdef _WIN32
   struct GssapiAcceptor::Impl
   {
      Impl() : haveCredential(false), haveContext(false), established(false) { }
      CredHandle credential;
      CtxtHandle context;
      bool haveCredential;
      bool haveContext;
      bool established;
      std::wstring user;
      std::wstring domain;
      std::wstring password;
   };

   namespace
   {
      String Describe(SECURITY_STATUS status)
      {
         switch (status)
         {
         case SEC_E_INVALID_TOKEN: return _T("the token is not a Kerberos token this server can read");
         case SEC_E_LOGON_DENIED: return _T("the ticket was refused");
         case SEC_E_NO_AUTHENTICATING_AUTHORITY: return _T("no domain controller could be reached to validate the credentials");
         case SEC_E_NO_CREDENTIALS: return _T("this server has no Kerberos credentials: set GssapiServiceAccount and GssapiServicePassword, or run it on a domain-joined host");
         case SEC_E_UNKNOWN_CREDENTIALS: return _T("the service account or its password is not accepted by the domain");
         case SEC_E_TIME_SKEW: return _T("the clocks of the client, this server and the domain controller differ by too much");
         case SEC_E_WRONG_PRINCIPAL: return _T("the ticket is for another service principal");
         case SEC_E_INTERNAL_ERROR: return _T("the Kerberos package could not use the credentials on this host (a workgroup host needs GssapiServiceAccount and a reachable domain controller)");
         default:
            {
               String text;
               text.Format(_T("SSPI status 0x%08X"), (unsigned int) status);
               return text;
            }
         }
      }
   }

   GssapiAcceptor::GssapiAcceptor() :
      state_(NeedToken),
      impl_(new Impl())
   {
   }

   GssapiAcceptor::~GssapiAcceptor()
   {
      if (impl_->haveContext)
         DeleteSecurityContext(&impl_->context);
      if (impl_->haveCredential)
         FreeCredentialsHandle(&impl_->credential);
      delete impl_;
   }

   bool
   GssapiAcceptor::IsEnabled()
   {
      return IniFileSettings::Instance()->GetSettingsValue(_T("GssapiEnabled")) == _T("1");
   }

   bool
   GssapiAcceptor::Acquire_(String &failure)
   {
      const String account = IniFileSettings::Instance()->GetSettingsValue(_T("GssapiServiceAccount"));
      const String password = IniFileSettings::Instance()->GetSettingsValue(_T("GssapiServicePassword"));
      SEC_WINNT_AUTH_IDENTITY_W identity;
      memset(&identity, 0, sizeof(identity));
      void *identityPointer = NULL;
      if (!account.IsEmpty())
      {
         // DOMAIN\user, or user@realm (a UPN, the domain left empty).
         const int slash = account.Find(_T("\\"));
         if (slash > 0)
         {
            impl_->domain = std::wstring(account.Mid(0, slash).c_str());
            impl_->user = std::wstring(account.Mid(slash + 1).c_str());
         }
         else
         {
            impl_->domain.clear();
            impl_->user = std::wstring(account.c_str());
         }
         impl_->password = std::wstring(password.c_str());
         identity.User = (unsigned short *) impl_->user.c_str();
         identity.UserLength = (unsigned long) impl_->user.size();
         identity.Domain = (unsigned short *) impl_->domain.c_str();
         identity.DomainLength = (unsigned long) impl_->domain.size();
         identity.Password = (unsigned short *) impl_->password.c_str();
         identity.PasswordLength = (unsigned long) impl_->password.size();
         identity.Flags = SEC_WINNT_AUTH_IDENTITY_UNICODE;
         identityPointer = &identity;
      }
      TimeStamp expiry;
      const SECURITY_STATUS status = AcquireCredentialsHandleW(NULL, (SEC_WCHAR *) L"Kerberos", SECPKG_CRED_INBOUND, NULL, identityPointer, NULL, NULL, &impl_->credential, &expiry);
      if (status != SEC_E_OK)
      {
         failure = _T("the server's Kerberos credentials could not be acquired: ") + Describe(status);
         return false;
      }
      impl_->haveCredential = true;
      return true;
   }

   GssapiAcceptor::State
   GssapiAcceptor::Step(const AnsiString &clientToken, AnsiString &serverToken, String &failure)
   {
      serverToken = "";
      if (state_ == Failed || state_ == Done)
         return state_;
      if (!impl_->haveCredential && !Acquire_(failure))
      {
         state_ = Failed;
         return state_;
      }
      if (state_ == NeedLayer)
         return AcceptLayer_(clientToken, failure);
      if (impl_->established)
      {
         // The client has taken the last token (its answer is empty): the
         // security-layer offer is next.
         return OfferLayer_(serverToken, failure);
      }
      if (clientToken.IsEmpty())
      {
         // Nothing yet: an empty challenge asks the client for its first token.
         return state_;
      }

      SecBuffer inBuffer;
      inBuffer.cbBuffer = (unsigned long) clientToken.GetLength();
      inBuffer.BufferType = SECBUFFER_TOKEN;
      inBuffer.pvBuffer = (void *) clientToken.c_str();
      SecBufferDesc inDesc;
      inDesc.ulVersion = SECBUFFER_VERSION;
      inDesc.cBuffers = 1;
      inDesc.pBuffers = &inBuffer;
      SecBuffer outBuffer;
      outBuffer.cbBuffer = 0;
      outBuffer.BufferType = SECBUFFER_TOKEN;
      outBuffer.pvBuffer = NULL;
      SecBufferDesc outDesc;
      outDesc.ulVersion = SECBUFFER_VERSION;
      outDesc.cBuffers = 1;
      outDesc.pBuffers = &outBuffer;
      unsigned long attributes = 0;
      TimeStamp expiry;
      SECURITY_STATUS status = AcceptSecurityContext(&impl_->credential, impl_->haveContext ? &impl_->context : NULL, &inDesc,
         ASC_REQ_MUTUAL_AUTH | ASC_REQ_ALLOCATE_MEMORY | ASC_REQ_INTEGRITY | ASC_REQ_SEQUENCE_DETECT | ASC_REQ_REPLAY_DETECT,
         SECURITY_NATIVE_DREP, &impl_->context, &outDesc, &attributes, &expiry);
      if (status == SEC_E_OK || status == SEC_I_CONTINUE_NEEDED || status == SEC_I_COMPLETE_NEEDED || status == SEC_I_COMPLETE_AND_CONTINUE)
         impl_->haveContext = true;
      if (status == SEC_I_COMPLETE_NEEDED || status == SEC_I_COMPLETE_AND_CONTINUE)
      {
         CompleteAuthToken(&impl_->context, &outDesc);
         status = status == SEC_I_COMPLETE_NEEDED ? SEC_E_OK : SEC_I_CONTINUE_NEEDED;
      }
      if (outBuffer.pvBuffer)
      {
         if (outBuffer.cbBuffer > 0)
            serverToken = AnsiString(std::string((const char *) outBuffer.pvBuffer, outBuffer.cbBuffer));
         FreeContextBuffer(outBuffer.pvBuffer);
      }
      if (status == SEC_I_CONTINUE_NEEDED)
         return state_;
      if (status != SEC_E_OK)
      {
         failure = Describe(status);
         state_ = Failed;
         return state_;
      }
      impl_->established = true;
      if (!ReadNames_(failure))
      {
         state_ = Failed;
         return state_;
      }
      if (!serverToken.IsEmpty())
      {
         // The proof of this server goes to the client; its empty answer
         // brings the exchange back here for the layer offer.
         return state_;
      }
      return OfferLayer_(serverToken, failure);
   }

   GssapiAcceptor::State
   GssapiAcceptor::OfferLayer_(AnsiString &serverToken, String &failure)
   {
      // RFC 4752 3.1: one octet of layers offered (1: none), three of the
      // largest message the server would take under a layer (none: 0).
      AnsiString offer;
      offer += (char) 1;
      offer += (char) 0;
      offer += (char) 0;
      offer += (char) 0;
      if (!Wrap_(offer, serverToken, failure))
      {
         state_ = Failed;
         return state_;
      }
      state_ = NeedLayer;
      return state_;
   }

   GssapiAcceptor::State
   GssapiAcceptor::AcceptLayer_(const AnsiString &clientToken, String &failure)
   {
      AnsiString plain;
      if (clientToken.IsEmpty() || !Unwrap_(clientToken, plain, failure))
      {
         if (failure.IsEmpty())
            failure = _T("the client sent no security-layer choice");
         state_ = Failed;
         return state_;
      }
      if (plain.GetLength() < 4 || (unsigned char) plain[0] != 1)
      {
         failure = _T("the client asked for a security layer this server does not offer");
         state_ = Failed;
         return state_;
      }
      AnsiString authzidUtf8 = plain.Mid(4);
      Unicode::MultiByteToWide(authzidUtf8, authzid_);
      state_ = Done;
      return state_;
   }

   bool
   GssapiAcceptor::Wrap_(const AnsiString &plain, AnsiString &token, String &failure)
   {
      SecPkgContext_Sizes sizes;
      SECURITY_STATUS status = QueryContextAttributes(&impl_->context, SECPKG_ATTR_SIZES, &sizes);
      if (status != SEC_E_OK)
      {
         failure = _T("the wrap sizes could not be read: ") + Describe(status);
         return false;
      }
      std::vector<char> header(sizes.cbSecurityTrailer > 0 ? sizes.cbSecurityTrailer : 1);
      std::vector<char> data(plain.c_str(), plain.c_str() + plain.GetLength());
      std::vector<char> padding(sizes.cbBlockSize > 0 ? sizes.cbBlockSize : 1);
      SecBuffer buffers[3];
      buffers[0].cbBuffer = sizes.cbSecurityTrailer;
      buffers[0].BufferType = SECBUFFER_TOKEN;
      buffers[0].pvBuffer = header.data();
      buffers[1].cbBuffer = (unsigned long) data.size();
      buffers[1].BufferType = SECBUFFER_DATA;
      buffers[1].pvBuffer = data.data();
      buffers[2].cbBuffer = sizes.cbBlockSize;
      buffers[2].BufferType = SECBUFFER_PADDING;
      buffers[2].pvBuffer = padding.data();
      SecBufferDesc desc;
      desc.ulVersion = SECBUFFER_VERSION;
      desc.cBuffers = 3;
      desc.pBuffers = buffers;
      status = EncryptMessage(&impl_->context, SECQOP_WRAP_NO_ENCRYPT, &desc, 0);
      if (status != SEC_E_OK)
      {
         failure = _T("the security-layer offer could not be wrapped: ") + Describe(status);
         return false;
      }
      std::string wire;
      wire.append((const char *) buffers[0].pvBuffer, buffers[0].cbBuffer);
      wire.append((const char *) buffers[1].pvBuffer, buffers[1].cbBuffer);
      wire.append((const char *) buffers[2].pvBuffer, buffers[2].cbBuffer);
      token = AnsiString(wire);
      return true;
   }

   bool
   GssapiAcceptor::Unwrap_(const AnsiString &token, AnsiString &plain, String &failure)
   {
      std::vector<char> stream(token.c_str(), token.c_str() + token.GetLength());
      SecBuffer buffers[2];
      buffers[0].cbBuffer = (unsigned long) stream.size();
      buffers[0].BufferType = SECBUFFER_STREAM;
      buffers[0].pvBuffer = stream.data();
      buffers[1].cbBuffer = 0;
      buffers[1].BufferType = SECBUFFER_DATA;
      buffers[1].pvBuffer = NULL;
      SecBufferDesc desc;
      desc.ulVersion = SECBUFFER_VERSION;
      desc.cBuffers = 2;
      desc.pBuffers = buffers;
      unsigned long qop = 0;
      const SECURITY_STATUS status = DecryptMessage(&impl_->context, &desc, 0, &qop);
      if (status != SEC_E_OK)
      {
         failure = _T("the client's security-layer choice could not be unwrapped: ") + Describe(status);
         return false;
      }
      if (buffers[1].pvBuffer && buffers[1].cbBuffer > 0)
         plain = AnsiString(std::string((const char *) buffers[1].pvBuffer, buffers[1].cbBuffer));
      else
         plain = "";
      return true;
   }

   bool
   GssapiAcceptor::ReadNames_(String &failure)
   {
      SecPkgContext_NativeNamesW native;
      memset(&native, 0, sizeof(native));
      if (QueryContextAttributesW(&impl_->context, SECPKG_ATTR_NATIVE_NAMES, &native) == SEC_E_OK && native.sClientName)
      {
         principal_ = String(native.sClientName);
         FreeContextBuffer(native.sClientName);
         if (native.sServerName)
            FreeContextBuffer(native.sServerName);
         return true;
      }
      SecPkgContext_NamesW names;
      memset(&names, 0, sizeof(names));
      if (QueryContextAttributesW(&impl_->context, SECPKG_ATTR_NAMES, &names) == SEC_E_OK && names.sUserName)
      {
         // DOMAIN\user becomes user@DOMAIN.
         String nt = String(names.sUserName);
         FreeContextBuffer(names.sUserName);
         const int slash = nt.Find(_T("\\"));
         principal_ = slash > 0 ? nt.Mid(slash + 1) + _T("@") + nt.Mid(0, slash) : nt;
         return true;
      }
      failure = _T("the client's name could not be read from the context");
      return false;
   }
#else
   struct GssapiAcceptor::Impl { };
   GssapiAcceptor::GssapiAcceptor() : state_(NeedToken), impl_(new Impl()) { }
   GssapiAcceptor::~GssapiAcceptor() { delete impl_; }
   bool GssapiAcceptor::IsEnabled() { return false; }
   GssapiAcceptor::State GssapiAcceptor::Step(const AnsiString &, AnsiString &serverToken, String &failure)
   {
      serverToken = "";
      failure = _T("GSSAPI is available on Windows (SSPI) only in this release");
      state_ = Failed;
      return state_;
   }
   bool GssapiAcceptor::Acquire_(String &) { return false; }
   GssapiAcceptor::State GssapiAcceptor::OfferLayer_(AnsiString &, String &) { return Failed; }
   GssapiAcceptor::State GssapiAcceptor::AcceptLayer_(const AnsiString &, String &) { return Failed; }
   bool GssapiAcceptor::Wrap_(const AnsiString &, AnsiString &, String &) { return false; }
   bool GssapiAcceptor::Unwrap_(const AnsiString &, AnsiString &, String &) { return false; }
   bool GssapiAcceptor::ReadNames_(String &) { return false; }
#endif

   std::shared_ptr<const Account>
   GssapiIdentity::Logon(const String &principal, const String &authzid, const IPAddress &remote_address, String &out_login_name, bool &disconnect)
   {
      disconnect = false;
      out_login_name = principal;

      String user = principal;
      String realm;
      const int at = principal.Find(_T("@"));
      if (at > 0)
      {
         user = principal.Mid(0, at);
         realm = principal.Mid(at + 1);
      }
      String userLower = user;
      userLower.ToLower();
      String realmLower = realm;
      realmLower.ToLower();
      String netbios = realmLower;
      const int dot = netbios.Find(_T("."));
      if (dot > 0)
         netbios = netbios.Mid(0, dot);

      std::shared_ptr<const Account> account;

      // 1. The account an administrator linked to this directory user.
      if (!userLower.IsEmpty())
      {
         SQLCommand command("select accountaddress from hm_accounts where lower(accountadusername) = @USERNAME and (lower(accountaddomain) = @REALM or lower(accountaddomain) = @NETBIOS)");
         command.AddParameter("@USERNAME", userLower);
         command.AddParameter("@REALM", realmLower);
         command.AddParameter("@NETBIOS", netbios);
         std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
         if (recordset && !recordset->IsEOF())
            account = CacheContainer::Instance()->GetAccount(recordset->GetStringValue("accountaddress"));
      }

      // 2. The address the directory gives the user: user@realm.
      if (!account && !userLower.IsEmpty() && !realmLower.IsEmpty())
         account = CacheContainer::Instance()->GetAccount(userLower + _T("@") + realmLower);

      if (account && !account->GetActive())
         account.reset();

      // The authorization identity, when the client gave one, is this account.
      if (account && !authzid.IsEmpty())
      {
         std::shared_ptr<DomainAliases> aliases = ObjectCache::Instance()->GetDomainAliases();
         const String wanted = DefaultDomain::ApplyDefaultDomain(aliases->ApplyAliasesOnAddress(authzid));
         if (wanted.CompareNoCase(account->GetAddress()) != 0)
            account.reset();
      }

      if (account)
      {
         out_login_name = account->GetAddress();
         PersistentAccount::UpdateLastLogonTime(account);
         ServerStatus::Instance()->OnAuthenticationSucceeded();
         return account;
      }

      ServerStatus::Instance()->OnAuthenticationFailed();
      AccountLogon accountLogon;
      accountLogon.RegisterFailedLogin(remote_address, out_login_name, disconnect, false);
      return account;
   }
}
