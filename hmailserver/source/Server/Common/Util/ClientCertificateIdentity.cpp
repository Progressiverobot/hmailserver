// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "ClientCertificateIdentity.h"

#include "AccountLogon.h"
#include "ServerStatus.h"
#include "../BO/Account.h"
#include "../BO/Domain.h"
#include "../BO/DomainAliases.h"
#include "../Cache/CacheContainer.h"
#include "../Persistence/PersistentAccount.h"
#include "../Application/ObjectCache.h"
#include "../Application/DefaultDomain.h"
#include "../TCPIP/IPAddress.h"

#include <openssl/x509.h>
#include <openssl/x509v3.h>
#include <openssl/obj_mac.h>

#include <set>
#include <string>
#include <algorithm>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // Adds one candidate to the list: lower-cased, only if it is shaped like an
      // address, only once.
      void AddIdentity_(const char *data, int length, std::vector<AnsiString> &identities, std::set<std::string> &seen)
      {
         if (data == nullptr || length <= 0)
            return;

         std::string value(data, (size_t) length);

         // An X.509 string may legally contain a NUL; an address never does, and the
         // classic attack on certificate identities is exactly "user@good.example\0.evil"
         // read by a C string function. Anything with one is not an identity.
         if (value.find('\0') != std::string::npos)
            return;

         std::transform(value.begin(), value.end(), value.begin(), [](unsigned char c) { return (char) std::tolower(c); });

         const size_t at = value.find('@');
         if (at == std::string::npos || at == 0 || at == value.size() - 1)
            return;

         if (!seen.insert(value).second)
            return;

         identities.push_back(AnsiString(value.c_str()));
      }
   }

   std::vector<AnsiString>
   ClientCertificateIdentity::Extract(X509 *certificate)
   {
      std::vector<AnsiString> identities;
      std::set<std::string> seen;

      if (certificate == nullptr)
         return identities;

      // subjectAltName rfc822Name entries: where an address belongs in a certificate
      // (RFC 5280 section 4.2.1.6), and where every modern issuer puts it.
      GENERAL_NAMES *names = (GENERAL_NAMES *) X509_get_ext_d2i(certificate, NID_subject_alt_name, nullptr, nullptr);
      if (names != nullptr)
      {
         const int count = sk_GENERAL_NAME_num(names);
         for (int i = 0; i < count; i++)
         {
            const GENERAL_NAME *name = sk_GENERAL_NAME_value(names, i);
            if (name == nullptr || name->type != GEN_EMAIL)
               continue;

            const ASN1_IA5STRING *email = name->d.rfc822Name;
            AddIdentity_((const char *) ASN1_STRING_get0_data(email), ASN1_STRING_length(email), identities, seen);
         }

         GENERAL_NAMES_free(names);
      }

      // The subject's emailAddress attribute (deprecated by RFC 5280 but still issued),
      // then a CN that is an address, which is how an older or a hand-made client
      // certificate names its user.
      const X509_NAME *subject = X509_get_subject_name(certificate);
      if (subject != nullptr)
      {
         const int nids[] = { NID_pkcs9_emailAddress, NID_commonName };
         for (int nid : nids)
         {
            int index = -1;
            while ((index = X509_NAME_get_index_by_NID(subject, nid, index)) >= 0)
            {
               const X509_NAME_ENTRY *entry = X509_NAME_get_entry(subject, index);
               if (entry == nullptr)
                  continue;

               unsigned char *utf8 = nullptr;
               const int length = ASN1_STRING_to_UTF8(&utf8, X509_NAME_ENTRY_get_data(entry));
               if (length > 0)
                  AddIdentity_((const char *) utf8, length, identities, seen);

               if (utf8 != nullptr)
                  OPENSSL_free(utf8);
            }
         }
      }

      return identities;
   }

   std::shared_ptr<const Account>
   ClientCertificateIdentity::Logon(const std::vector<AnsiString> &identities, const String &authzid,
                                    const IPAddress &remote_address, String &out_login_name, bool &disconnect)
   {
      disconnect = false;
      out_login_name = authzid;

      std::shared_ptr<const Account> account;

      if (!authzid.IsEmpty())
      {
         // The client named who it wants to be. That is fine exactly when the
         // certificate names the same mailbox; a certificate for one user is not a
         // credential for another, whatever the client asks.
         const String wanted = Normalise_(authzid);
         out_login_name = wanted;

         for (const AnsiString &identity : identities)
         {
            if (Normalise_(String(identity)).CompareNoCase(wanted) == 0)
            {
               account = FindActiveAccount_(wanted);
               break;
            }
         }
      }
      else
      {
         for (const AnsiString &identity : identities)
         {
            const String candidate = Normalise_(String(identity));
            account = FindActiveAccount_(candidate);
            if (account)
            {
               out_login_name = candidate;
               break;
            }
         }

         if (!account && !identities.empty())
            out_login_name = String(identities.front());
      }

      if (account)
      {
         PersistentAccount::UpdateLastLogonTime(account);
         ServerStatus::Instance()->OnAuthenticationSucceeded();
         return account;
      }

      ServerStatus::Instance()->OnAuthenticationFailed();

      // The per-IP half of auto-ban, so a client that keeps presenting a certificate
      // for a mailbox that does not exist is treated like any other repeated failure
      // from its address. Not the per-name lockout: nothing here is guessed.
      AccountLogon accountLogon;
      accountLogon.RegisterFailedLogin(remote_address, out_login_name, disconnect, false);

      return account;
   }

   String
   ClientCertificateIdentity::Normalise_(const String &address)
   {
      std::shared_ptr<DomainAliases> aliases = ObjectCache::Instance()->GetDomainAliases();
      String normalised = aliases->ApplyAliasesOnAddress(address);
      return DefaultDomain::ApplyDefaultDomain(normalised);
   }

   std::shared_ptr<const Account>
   ClientCertificateIdentity::FindActiveAccount_(const String &address)
   {
      // The same eligibility the bearer-token logon applies: an active account in an
      // active domain, whatever its stored password hash - the certificate is the
      // proof, so the hash type is irrelevant.
      std::shared_ptr<const Account> account = CacheContainer::Instance()->GetAccount(address);
      if (!account || !account->GetActive())
         return std::shared_ptr<const Account>();

      const String domainName = StringParser::ExtractDomain(address);
      std::shared_ptr<const Domain> domain = CacheContainer::Instance()->GetDomain(domainName);
      if (!domain || !domain->GetIsActive())
         return std::shared_ptr<const Account>();

      return account;
   }
}
