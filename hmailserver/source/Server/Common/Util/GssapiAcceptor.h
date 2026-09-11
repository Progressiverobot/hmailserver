// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// SASL GSSAPI (RFC 4752), the server side: a Kerberos client presents a
// ticket for this server's service principal, the server proves itself back
// (mutual authentication), the two agree on no security layer, and the
// client's principal names the account. On Windows this is SSPI's Kerberos
// package: the credentials are the process's own on a domain-joined host
// (the machine account, whose service principal names the administrator
// registers with setspn), or GssapiServiceAccount and GssapiServicePassword
// in [Settings] for a service account of the domain. The mechanism is
// offered only when GssapiEnabled is 1. Elsewhere it is not available yet.

#pragma once

#include <memory>

namespace HM
{
   class Account;
   class IPAddress;

   class GssapiAcceptor
   {
   public:
      enum State
      {
         NeedToken,   // waiting for the client's next token
         NeedLayer,   // the security-layer offer went out; waiting for the client's choice
         Done,        // the client is authenticated: GetPrincipal() names it
         Failed       // the exchange ended without an authentication
      };

      GssapiAcceptor();
      ~GssapiAcceptor();

      // Whether the mechanism is offered at all: GssapiEnabled=1, on a platform
      // that has it.
      static bool IsEnabled();

      // One step of the exchange. clientToken is the raw token (base64 undone
      // by the protocol), empty when the client sent none yet; serverToken is
      // what goes back, raw, possibly empty. failure says why on Failed.
      State Step(const AnsiString &clientToken, AnsiString &serverToken, String &failure);

      State GetState() const { return state_; }
      // After Done: user@REALM as the ticket named the client.
      String GetPrincipal() const { return principal_; }
      // After Done: the identity the client asked to act as; empty for itself.
      String GetAuthorizationIdentity() const { return authzid_; }

   private:
      bool Acquire_(String &failure);
      State OfferLayer_(AnsiString &serverToken, String &failure);
      State AcceptLayer_(const AnsiString &clientToken, String &failure);
      bool Wrap_(const AnsiString &plain, AnsiString &token, String &failure);
      bool Unwrap_(const AnsiString &token, AnsiString &plain, String &failure);
      bool ReadNames_(String &failure);

      State state_;
      String principal_;
      String authzid_;
      struct Impl;
      Impl *impl_;
   };

   // The account a Kerberos principal is: the account linked to that directory
   // user (its Active Directory domain and user name), else the account whose
   // address is user@realm; active; and the authorization identity, when the
   // client gave one, has to be that account. A failure is registered the way
   // a wrong password is.
   class GssapiIdentity
   {
   public:
      static std::shared_ptr<const Account> Logon(const String &principal, const String &authzid, const IPAddress &remote_address, String &out_login_name, bool &disconnect);
   };
}
