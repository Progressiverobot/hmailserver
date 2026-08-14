// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#pragma once

#include "../Util/Singleton.h"

namespace HM
{
   // How the LDAP connection is protected on the wire.
   //
   // TransportLdaps is the default and the only configuration that should be used
   // outside a lab. The two weaker values exist because real directories are not
   // always reachable over TLS, not because they are equivalent - see
   // LdapConfiguration::PasswordIsProtected below for what turning TLS off actually
   // costs, and note that it costs nothing at all with BindNegotiate.
   enum class LdapTransportSecurity
   {
      TransportPlain = 0,      // cleartext LDAP, normally port 389
      TransportStartTls = 1,   // cleartext connect on 389, then StartTLS before the bind
      TransportLdaps = 2       // TLS from the first byte, normally port 636
   };

   // How the end user's password is proved to the directory.
   //
   // BindSimple sends the password to the server, so it needs a protected
   // transport. BindNegotiate runs an SSPI (Kerberos, falling back to NTLM)
   // exchange, which never puts the password on the wire and signs the connection,
   // so it satisfies a directory that refuses unprotected simple binds even when no
   // server certificate exists.
   //
   // BindNegotiate is what makes the workgroup case work at all: NTLM with explicit
   // credentials does not require the mail server host to be domain-joined, which is
   // exactly the requirement that makes SSPIValidation's LogonUser path unusable in a
   // DMZ. Kerberos is tried first and needs to resolve the realm, so on an unjoined
   // host the exchange normally lands on NTLM.
   enum class LdapBindMethod
   {
      BindSimple = 0,
      BindNegotiate = 1
   };

   // A snapshot of the [LDAP] section. Passed by value on purpose: an
   // authentication attempt must run against one consistent set of values even if an
   // administrator edits the ini halfway through it.
   class LdapConfiguration
   {
   public:
      bool enabled = false;
      String server;
      int port = 0;
      LdapTransportSecurity security = LdapTransportSecurity::TransportLdaps;
      bool verify_certificate = true;
      bool allow_unprotected_password = false;
      LdapBindMethod bind_method = LdapBindMethod::BindSimple;
      String search_base;
      String user_search_filter;
      String user_dn_template;
      String service_username;
      String service_domain;
      String service_password;
      int timeout_seconds = 10;
      bool fallback_to_windows_logon = false;

      // ---- directory as an account source -----------------------------------
      //
      // Separate from everything above, which is about authenticating a user who is
      // ALREADY an account here. These describe reading the directory to find out
      // which accounts should exist at all - a different question, with a different
      // filter, so it gets its own settings rather than overloading
      // user_search_filter (which carries %u placeholders and matches exactly one
      // person by construction).

      // The filter that selects the people who should have a mailbox. Defaults to
      // enabled person objects that actually carry a mail address, because an account
      // source keyed on anything else provisions mailboxes for service accounts,
      // computers and disabled leavers.
      String sync_filter;

      // Which attribute carries the address, the logon name, and the display name.
      // Configurable because these differ between directories - a non-Windows LDAP
      // server has no sAMAccountName at all.
      String sync_mail_attribute;
      String sync_username_attribute;
      String sync_display_name_attribute;

      // A ceiling on how many directory entries one pass will read into memory. Not a
      // page size - SearchEntries pages regardless - but a bound on a directory far
      // larger than anyone expected, so a misconfigured search base cannot be answered
      // by allocating until the process dies.
      int sync_max_users = 5000;

      // 636 for LDAPS and 389 otherwise, unless Port says otherwise. Derived rather
      // than defaulted to 389 because a configuration that sets Security=2 and
      // forgets Port would otherwise attempt TLS against the cleartext port, which
      // fails in a way that looks like a certificate problem.
      int EffectivePort() const;

      // Whether the user's password reaches the directory protected. True when the
      // transport is TLS, and also true for BindNegotiate on a cleartext transport,
      // because that exchange never transmits the password - only a challenge
      // response over a connection SSPI has signed.
      bool PasswordIsProtected() const;

      // Whether enough has been configured to attempt anything. Returns false and
      // fills missing with the setting names that are absent; the caller reports it
      // once rather than failing silently, because "LDAP is enabled but does nothing"
      // is otherwise indistinguishable from a wrong password.
      bool IsComplete(String &missing) const;

      // True when the user's DN has to be found by searching the directory.
      //
      // Only a simple bind needs a DN at all, so this is false for BindNegotiate:
      // SSPI authenticates by user name and domain, which the account already
      // carries, so that mode needs no search, no SearchBase and no service
      // credential. It is also false when UserDnTemplate is set, which is the other
      // way to reach a DN without a service credential.
      bool UsesSearch() const;
   };

   // Reads and caches the [LDAP] section of hMailServer.ini.
   //
   // Deliberately NOT part of IniFileSettings, and that is worth stating because it
   // looks like an omission. Two reasons. The first is mechanical: IniFileSettings is
   // shared by several developers at once and every addition to it is a merge
   // conflict, whereas a self-contained section costs nobody anything. The second is
   // behavioural: IniFileSettings caches the whole file at InitInstance and only
   // re-reads it on Application::Reinitialize, so an LDAP setting could not be
   // corrected without a reinitialize. This class keys its cache on the ini file's
   // last-write time, so a correction takes effect within kSettingsRecheckSeconds of
   // being saved - which matters when the thing being corrected is the reason nobody
   // can log in. RateLimiter's [SendingLimits] section already works this way and is
   // the precedent being followed.
   class LdapSettings : public Singleton<LdapSettings>
   {
   public:
      LdapSettings();
      virtual ~LdapSettings();

      LdapConfiguration GetConfiguration();

      // The cheap gate on the authentication path. Still goes through the
      // recheck, so enabling LDAP does not need a restart, but it does no file I/O
      // in the common case.
      bool GetEnabled();

   private:

      void MaybeRefresh_();
      void Load_();

      static String ReadString_(const String &iniFile, const wchar_t *key, const wchar_t *defaultValue);
      static int ReadInteger_(const String &iniFile, const wchar_t *key, int defaultValue);

      // settings_mutex_ guards the cached snapshot and is only ever held for a copy;
      // settings_refresh_mutex_ is held across the ini read, so at most one thread
      // reads the file and every other carries on with the values it already has.
      // Same split, and for the same reason, as RateLimiter.
      boost::mutex settings_mutex_;
      boost::mutex settings_refresh_mutex_;

      LdapConfiguration configuration_;
      bool settings_loaded_ = false;
      time_t settings_checked_time_ = 0;
      ULONGLONG settings_file_write_time_ = 0;
   };
}
