// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#pragma once

#include "LdapSettings.h"

// The wldap32 session handle, forward-declared so that <winldap.h> stays inside
// LdapClient.cpp. winldap.h drags in a large amount of the Windows security surface
// and defines names (ldap_*, LDAP_*) that would then be visible to every translation
// unit that touched authentication.
struct ldap;

namespace HM
{
   // The result of one LDAP operation, and the distinction the whole class exists to
   // preserve.
   //
   // OutcomeRejected means the directory answered and said no. OutcomeUnavailable
   // means the directory did not answer, or answered something that has nothing to do
   // with the credentials: it is down, the certificate is not trusted, the search base
   // is wrong, the filter does not parse, the DC refuses the bind method. Collapsing
   // the two into a bool is exactly the defect that makes the existing LogonUser path
   // undiagnosable - every failure there looks like a wrong password - so the
   // separation is carried in the type rather than reconstructed by the caller.
   enum class LdapOperationOutcome
   {
      OutcomeSuccess = 0,
      OutcomeRejected = 1,
      OutcomeUnavailable = 2
   };

   // One entry returned by an enumeration: its distinguished name, and whichever of
   // the requested attributes the directory actually sent back.
   //
   // Values are a vector because the attributes that matter here are genuinely
   // multi-valued - proxyAddresses carries every address an Exchange-style directory
   // knows for a user, and taking only the first would quietly discard aliases.
   // An attribute the entry does not carry is ABSENT from the map rather than present
   // and empty, so "the directory did not set this" stays distinguishable from "the
   // directory set it to nothing".
   struct LdapDirectoryEntry
   {
      String dn;
      std::map<String, std::vector<String> > attributes;

      // The first value of an attribute, or an empty string when the entry does not
      // carry it. For the single-valued cases, which is most of them.
      String First(const String &name) const
      {
         auto found = attributes.find(name);

         if (found == attributes.end() || found->second.empty())
            return String();

         return found->second.front();
      }
   };

   // A single LDAP session, over the Windows LDAP API (wldap32). One instance owns at
   // most one connection and closes it in the destructor, so an early return on any
   // path cannot leak a session or leave a socket to the directory open.
   //
   // Not thread safe, and not meant to be: an instance belongs to the connection
   // thread that is authenticating one user.
   class LdapClient
   {
   public:
      LdapClient();
      virtual ~LdapClient();

      // Opens the connection and applies every transport setting BEFORE anything is
      // sent. For TransportStartTls the upgrade happens here too, so a caller that
      // gets OutcomeSuccess back is holding a connection that is already protected -
      // there is no window in which a bind could be sent in the clear because the
      // StartTLS call had not been made yet.
      LdapOperationOutcome Connect(const LdapConfiguration &configuration);

      // An explicit anonymous bind, used only to read the directory when no service
      // credential is configured. Separate from BindSimple because "bind with no
      // credentials" must be something a call site asks for by name, never something
      // it falls into by passing two empty strings.
      LdapOperationOutcome BindAnonymous();

      // RFC 4511 simple bind: the password is sent to the server, so this must only
      // be used on a protected connection. Refuses an empty password outright - see
      // the comment at the implementation, it is a real bypass rather than an
      // argument check.
      LdapOperationOutcome BindSimple(const String &dn, const String &password);

      // SASL Negotiate (Kerberos, then NTLM) with explicit credentials. The password
      // is never transmitted and the connection is signed, which is why this works
      // against a domain controller that refuses cleartext simple binds and has no
      // usable certificate.
      LdapOperationOutcome BindNegotiate(const String &username, const String &domain, const String &password);

      // Binds whatever credential the configuration nominates for READING the
      // directory - the service credential, or anonymously when none is set.
      //
      // Honours BindMethod, which the previous file-local version of this did not.
      // That matters for reading the directory as an ACCOUNT SOURCE, which searches
      // whatever method users authenticate by: on a domain controller with no usable
      // LDAPS certificate - the unjoined mail server this feature exists for - a
      // simple bind cannot be made safely, while Negotiate never puts the password on
      // the wire and signs the connection.
      //
      // It changes nothing for authentication. That path binds a service credential
      // only inside `UsesSearch()`, which is false whenever BindMethod is Negotiate,
      // so the Negotiate branch here is unreachable from it by construction.
      LdapOperationOutcome BindService(const LdapConfiguration &configuration);

      // Enumerates every entry matching a filter, with the named attributes.
      //
      // Separate from FindUserDn, which exists to authenticate ONE user and is built
      // around that: it asks for two entries so it can detect ambiguity, and treats
      // more than one match as a configuration fault. Enumeration wants the opposite -
      // every match, however many - so folding the two together would make each worse.
      //
      // PAGED, and that is why this is not four lines. Active Directory caps a single
      // search at MaxPageSize, 1000 by default, and answers a larger directory by
      // returning the first thousand entries WITH A SUCCESS STATUS. A naive search
      // against a real company therefore reports a complete-looking result missing
      // everyone after the first thousand - and an account source that silently sees
      // part of a directory is worse than one that fails, because the accounts it does
      // not see look deleted rather than unread.
      //
      // maxEntries bounds what this accumulates in memory regardless; when it is
      // reached, truncated is set and the caller must not treat the result as whole.
      LdapOperationOutcome SearchEntries(const String &searchBase, const String &filter,
         const std::vector<String> &attributeNames, int maxEntries,
         std::vector<LdapDirectoryEntry> &entries, bool &truncated);

      // Runs the user search and returns the single matching DN.
      //
      // matchCount is an out parameter rather than being folded into the outcome
      // because the three cases need three different responses: one match proceeds,
      // zero matches is a rejection (that user is not in the directory), and more
      // than one is a configuration fault that must authenticate nobody.
      LdapOperationOutcome FindUserDn(const LdapConfiguration &configuration, const String &filter,
         String &dn, int &matchCount);

      void Disconnect();

      // The LDAP result code from the last operation (LDAP_SUCCESS when none failed).
      // Named GetLastLdapError, not GetLastError, because the latter is a Windows
      // macro and would be textually replaced.
      unsigned long GetLastLdapError() const { return last_error_; }

      // Human-readable text for the last failure, from ldap_err2string plus the cases
      // where that text is accurate but useless to an administrator.
      String DescribeLastError() const;

      // The server's own diagnosticMessage for the last failure, when it sent one.
      // Active Directory puts its real reason in here and nowhere else.
      String GetLastDiagnostic() const { return last_diagnostic_; }

      // RFC 4515 section 3 escaping for a value interpolated into a search filter.
      // Not cosmetic: without it a username containing ")(" rewrites the filter, and
      // a filter is a query with an implicit boolean structure - the LDAP equivalent
      // of SQL injection, reachable by anyone who can type a username at a logon
      // prompt.
      static String EscapeFilterValue(const String &value);

      // RFC 4514 section 2.4 escaping for a value interpolated into a DN.
      static String EscapeDistinguishedNameValue(const String &value);

      // Substitutes %u (username), %d (domain) and %m (full mail address) in a
      // filter or DN template. escapeFor decides which escaping rule is applied to
      // the substituted values; there is no "no escaping" option on purpose.
      enum class TemplateEscaping
      {
         EscapeForFilter = 0,
         EscapeForDistinguishedName = 1
      };

      static String ExpandTemplate(const String &templateText, const String &username,
         const String &domain, const String &address, TemplateEscaping escapeFor);

      // Translates the hexadecimal sub-status Active Directory hides in the
      // diagnosticMessage of a rejected bind ("... comment: AcceptSecurityContext
      // error, data 52e, v4563") into the reason it actually stands for. Empty when
      // there is no recognisable sub-status. This is the only place the difference
      // between "wrong password" and "the account is locked out" is available.
      static String DescribeActiveDirectorySubStatus(const String &diagnostic);

      // Whether an LDAP result code means "these credentials are not valid" rather
      // than "this directory cannot answer". Public because the authenticator logs
      // the distinction and the test fixture asserts on it.
      static bool IsCredentialRejection(unsigned long ldapError);

   private:

      // Owns a session handle, so copying one would close the same connection twice.
      LdapClient(const LdapClient &);
      LdapClient &operator=(const LdapClient &);

      // Waits for the reply to an asynchronous operation, bounded by
      // timeout_seconds_, and turns it into an outcome. Records last_error_ and
      // last_diagnostic_ on the way through.
      LdapOperationOutcome AwaitResult_(unsigned long messageId);

      // Records an LDAP result code and returns the outcome it maps to.
      LdapOperationOutcome Record_(unsigned long ldapError);

      // Closes the session without reporting anything. Used on every path where the
      // connection state has become unknown - a timed-out bind, a failed StartTLS -
      // because reusing such a connection is how a credential ends up on a socket
      // whose protection nobody has established.
      void Abandon_();

      struct ldap *session_ = nullptr;
      unsigned long last_error_ = 0;
      String last_diagnostic_;
      int timeout_seconds_ = 10;

      // Set by Connect once TLS is actually established - after the handshake for
      // LDAPS, after the upgrade for StartTLS - not merely because TLS was
      // configured. BindSimple consults it, so a password cannot reach a connection
      // whose protection was never confirmed.
      bool transport_protected_ = false;

      // Whether the administrator has explicitly accepted sending a password over an
      // unprotected connection. Copied from the configuration in Connect so that
      // BindSimple enforces the rule itself rather than trusting its caller to have
      // checked: the authenticator checks it too, and the duplication is deliberate
      // because the cost of the check being missing on one path is a cleartext
      // password on the wire.
      bool unprotected_password_allowed_ = false;
   };
}
