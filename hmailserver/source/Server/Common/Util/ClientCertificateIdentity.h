// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include <vector>

typedef struct x509_st X509;

namespace HM
{
   class Account;
   class IPAddress;

   // The identities a verified inbound client certificate asserts, and the SASL
   // EXTERNAL logon (RFC 4422 Appendix A) they allow.
   //
   // EXTERNAL is the mechanism with no credential of its own: the client's proof is
   // whatever the transport already established, which here is the certificate the TLS
   // handshake verified against the port's CA bundle (TCPIPPort.ClientCertificatePolicy
   // and ClientCertificateCAFile). Everything security-relevant therefore happened
   // before this class is reached, and it is only ever handed a certificate that
   // verified; what it adds is the mapping from that certificate to a mailbox.
   //
   // The mapping is deliberately narrow. A certificate names the mailboxes it may log
   // on as through the addresses it carries - each subjectAltName rfc822Name, the
   // subject's emailAddress attribute, and a CN that is shaped like an address - and
   // nothing else: no lookup table from subject to account, no "any certificate from
   // this CA may be anyone". An administrator who wants a certificate to open a
   // mailbox issues it with that address in it, which is what the CA is for.
   class ClientCertificateIdentity
   {
   public:

      // Every address the certificate names, lower-cased and deduplicated, in the order
      // they were found: rfc822Name entries first, then the subject's emailAddress, then
      // a CN containing '@'. Empty when the certificate names no address at all.
      static std::vector<AnsiString> Extract(X509 *certificate);

      // The account a SASL EXTERNAL exchange logs on, or null.
      //
      // identities are the certificate's (Extract). authzid is the identity the client
      // asked to be authorized as, or empty for "whoever the certificate says": with one
      // given it has to be among the certificate's addresses, without one the first of
      // them that names an active account in an active domain wins. Aliases and the
      // default domain apply to both, the way they do for a password logon.
      //
      // out_login_name is what the OnClientLogon event and the log see. On failure the
      // per-IP auto-ban accounting is fed and disconnect says whether it wants the session
      // closed; the per-name lockout is deliberately not, because a certificate is not
      // something an attacker guesses at.
      static std::shared_ptr<const Account> Logon(const std::vector<AnsiString> &identities, const String &authzid,
                                                  const IPAddress &remote_address, String &out_login_name, bool &disconnect);

   private:

      static String Normalise_(const String &address);
      static std::shared_ptr<const Account> FindActiveAccount_(const String &address);
   };
}
