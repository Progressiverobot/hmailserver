// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#include "StdAfx.h"
#include "LdapSettings.h"

#include <ctime>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   static const wchar_t *kLdapSection = _T("LDAP");

   // How long a cached snapshot is trusted before the ini file's timestamp is looked
   // at again. Two seconds, matching RateLimiter: short enough that an administrator
   // fixing a broken directory configuration sees the effect while still looking at
   // the screen, long enough that a burst of logons does not stat the file per
   // attempt.
   static const time_t kSettingsRecheckSeconds = 2;

   // The shipped filter. objectCategory is indexed in Active Directory and
   // objectClass is not, so putting it first is the difference between an indexed
   // lookup and a subtree walk on a large directory. sAMAccountName rather than
   // userPrincipalName because that is what hMailServer's existing AD Username field
   // holds - the same value the LogonUser path passes today - so an account that is
   // already linked to AD keeps working without being edited.
   static const wchar_t *kDefaultUserSearchFilter =
      _T("(&(objectCategory=person)(objectClass=user)(sAMAccountName=%u))");

   // Bounds on the operation timeout. Zero or negative would mean "wait forever",
   // which is how an unreachable directory turns into exhausted connection threads;
   // and a very large value is the same defect wearing a number. Ninety seconds is
   // already longer than any client will wait.
   static const int kMinTimeoutSeconds = 1;
   static const int kMaxTimeoutSeconds = 90;
   static const int kDefaultTimeoutSeconds = 10;

   // Enabled person objects that carry a mail address.
   //
   // Every clause earns its place. objectCategory=person with objectClass=user is the
   // pair Active Directory itself uses to mean "a human account" - either alone also
   // matches contacts or computers. mail=* is what makes this an account source
   // rather than a list of everyone: a directory entry with no address has nothing to
   // provision a mailbox for. And the userAccountControl bit test excludes DISABLED
   // accounts (ADS_UF_ACCOUNTDISABLE, 0x0002) using the LDAP_MATCHING_RULE_BIT_AND
   // OID - without it, provisioning would create a live mailbox for every leaver the
   // directory still holds, which is the opposite of what a directory is being trusted
   // for.
   static const wchar_t *kDefaultSyncFilter =
      L"(&(objectClass=user)(objectCategory=person)(mail=*)"
      L"(!(userAccountControl:1.2.840.113556.1.4.803:=2)))";

   static const wchar_t *kDefaultSyncMailAttribute = L"mail";
   static const wchar_t *kDefaultSyncUsernameAttribute = L"sAMAccountName";
   static const wchar_t *kDefaultSyncDisplayNameAttribute = L"displayName";

   static const int kDefaultSyncMaxUsers = 5000;

   // A directory larger than this is not read in one pass. Not a judgement about how
   // many people an organisation may have - it is the point beyond which holding the
   // whole answer in memory, and then building a report out of it, costs more than
   // the answer is worth.
   static const int kMaxSyncMaxUsers = 100000;

   // The schedule floor and ceiling. Fifteen minutes because a directory read is a
   // network round trip plus a full enumeration, and anything faster is load on a
   // server somebody else administers for a set of people that changes a few times a
   // month. A week because a value larger than that is almost always a units mistake,
   // and clamping it to something that visibly runs is more useful than honouring a
   // schedule nobody will observe firing.
   static const int kMinSyncScheduleMinutes = 15;
   static const int kMaxSyncScheduleMinutes = 7 * 24 * 60;

   int
   LdapConfiguration::EffectivePort() const
   {
      if (port > 0 && port <= 65535)
         return port;

      return security == LdapTransportSecurity::TransportLdaps ? 636 : 389;
   }

   bool
   LdapConfiguration::PasswordIsProtected() const
   {
      if (security != LdapTransportSecurity::TransportPlain)
         return true;

      // A Negotiate bind proves the password with a challenge response and signs the
      // connection, so the password itself never crosses the network. This is not a
      // loophole being left open - it is the only configuration that authenticates
      // against a default-configured Windows Server 2025 domain controller that has
      // no server certificate installed, because such a DC refuses cleartext simple
      // binds outright (strongAuthRequired, LDAP error 8) and cannot complete a TLS
      // handshake either.
      return bind_method == LdapBindMethod::BindNegotiate;
   }

   bool
   LdapConfiguration::UsesSearch() const
   {
      if (bind_method == LdapBindMethod::BindNegotiate)
         return false;

      return user_dn_template.IsEmpty();
   }

   bool
   LdapConfiguration::IsComplete(String &missing) const
   {
      missing.Empty();

      if (server.IsEmpty())
         missing = _T("Server");

      if (UsesSearch())
      {
         // Search mode needs somewhere to search. A filter always has a value
         // because the default is non-empty, so only the base can be missing.
         if (search_base.IsEmpty())
         {
            if (!missing.IsEmpty())
               missing += _T(", ");

            missing += _T("SearchBase (or UserDnTemplate, to bind directly without searching)");
         }
      }

      return missing.IsEmpty();
   }

   LdapSettings::LdapSettings()
   {
   }

   LdapSettings::~LdapSettings()
   {
   }

   bool
   LdapSettings::GetEnabled()
   {
      MaybeRefresh_();

      boost::lock_guard<boost::mutex> guard(settings_mutex_);

      return configuration_.enabled;
   }

   LdapConfiguration
   LdapSettings::GetConfiguration()
   {
      MaybeRefresh_();

      boost::lock_guard<boost::mutex> guard(settings_mutex_);

      return configuration_;
   }

   void
   LdapSettings::MaybeRefresh_()
   {
      bool firstLoad = false;

      {
         boost::lock_guard<boost::mutex> guard(settings_mutex_);

         time_t now = time(0);

         // A clock that stepped backwards must not defer the next check for ever.
         if (settings_checked_time_ > now)
            settings_checked_time_ = now;

         if (settings_loaded_ && now - settings_checked_time_ < kSettingsRecheckSeconds)
            return;

         settings_checked_time_ = now;
         firstLoad = !settings_loaded_;
      }

      boost::unique_lock<boost::mutex> refreshGuard(settings_refresh_mutex_, boost::defer_lock);

      if (firstLoad)
      {
         // Nothing is cached yet, so this caller has to wait for the read.
         refreshGuard.lock();
      }
      else
      {
         // A refresh already in flight is good enough; carry on with the cached
         // values rather than queueing behind file I/O on the logon path.
         refreshGuard.try_lock();
         if (!refreshGuard.owns_lock())
            return;
      }

      Load_();
   }

   void
   LdapSettings::Load_()
   {
      String iniFile = IniFileSettings::GetInitializationFile();

      ULONGLONG writeTime = 0;
      WIN32_FILE_ATTRIBUTE_DATA attributes = {};
      if (GetFileAttributesEx(iniFile.c_str(), GetFileExInfoStandard, &attributes))
      {
         writeTime = ((ULONGLONG) attributes.ftLastWriteTime.dwHighDateTime << 32) |
                     (ULONGLONG) attributes.ftLastWriteTime.dwLowDateTime;
      }

      {
         boost::lock_guard<boost::mutex> guard(settings_mutex_);

         // Another thread may have done the work while we waited for the refresh
         // lock, and an unchanged file needs no re-read.
         if (settings_loaded_ && writeTime == settings_file_write_time_)
            return;
      }

      LdapConfiguration loaded;

      loaded.enabled = ReadInteger_(iniFile, _T("Enabled"), 0) != 0;
      loaded.server = ReadString_(iniFile, _T("Server"), _T(""));
      loaded.port = ReadInteger_(iniFile, _T("Port"), 0);

      // Anything outside the three known values is treated as LDAPS rather than as
      // "off". A typo in a security setting must never silently weaken it.
      int security = ReadInteger_(iniFile, _T("Security"), (int) LdapTransportSecurity::TransportLdaps);
      switch (security)
      {
         case (int) LdapTransportSecurity::TransportPlain:
            loaded.security = LdapTransportSecurity::TransportPlain;
            break;
         case (int) LdapTransportSecurity::TransportStartTls:
            loaded.security = LdapTransportSecurity::TransportStartTls;
            break;
         default:
            loaded.security = LdapTransportSecurity::TransportLdaps;
            break;
      }

      // Both of these default to the safe value, and both are read as "is it exactly
      // zero" so that a value nobody meant - a stray word, an empty string - leaves
      // validation on rather than turning it off.
      loaded.verify_certificate = ReadInteger_(iniFile, _T("VerifyCertificate"), 1) != 0;
      loaded.allow_unprotected_password = ReadInteger_(iniFile, _T("AllowUnprotectedPassword"), 0) != 0;

      loaded.bind_method = ReadInteger_(iniFile, _T("BindMethod"), (int) LdapBindMethod::BindSimple) ==
         (int) LdapBindMethod::BindNegotiate ? LdapBindMethod::BindNegotiate : LdapBindMethod::BindSimple;

      loaded.search_base = ReadString_(iniFile, _T("SearchBase"), _T(""));
      loaded.user_search_filter = ReadString_(iniFile, _T("UserSearchFilter"), kDefaultUserSearchFilter);
      loaded.user_dn_template = ReadString_(iniFile, _T("UserDnTemplate"), _T(""));
      loaded.service_username = ReadString_(iniFile, _T("ServiceUsername"), _T(""));
      loaded.service_domain = ReadString_(iniFile, _T("ServiceDomain"), _T(""));
      loaded.service_password = ReadString_(iniFile, _T("ServicePassword"), _T(""));

      // An emptied filter would expand to "()" and match nothing, which reads as
      // "every password is wrong" - so fall back to the shipped filter instead.
      if (loaded.user_search_filter.IsEmpty())
         loaded.user_search_filter = kDefaultUserSearchFilter;

      loaded.timeout_seconds = ReadInteger_(iniFile, _T("TimeoutSeconds"), kDefaultTimeoutSeconds);
      if (loaded.timeout_seconds < kMinTimeoutSeconds)
         loaded.timeout_seconds = kDefaultTimeoutSeconds;
      if (loaded.timeout_seconds > kMaxTimeoutSeconds)
         loaded.timeout_seconds = kMaxTimeoutSeconds;

      loaded.fallback_to_windows_logon = ReadInteger_(iniFile, _T("FallbackToWindowsLogon"), 0) != 0;

      loaded.sync_filter = ReadString_(iniFile, _T("SyncFilter"), kDefaultSyncFilter);
      loaded.sync_mail_attribute = ReadString_(iniFile, _T("SyncMailAttribute"), kDefaultSyncMailAttribute);
      loaded.sync_username_attribute = ReadString_(iniFile, _T("SyncUsernameAttribute"), kDefaultSyncUsernameAttribute);
      loaded.sync_display_name_attribute = ReadString_(iniFile, _T("SyncDisplayNameAttribute"), kDefaultSyncDisplayNameAttribute);

      // Emptied by hand in the ini, these fall back to the defaults rather than to
      // nothing. An empty filter would match every object in the directory and an
      // empty attribute name would read nothing from any of them - both are ways for
      // a blank line to mean something much worse than the default.
      if (loaded.sync_filter.IsEmpty())
         loaded.sync_filter = kDefaultSyncFilter;

      if (loaded.sync_mail_attribute.IsEmpty())
         loaded.sync_mail_attribute = kDefaultSyncMailAttribute;

      if (loaded.sync_username_attribute.IsEmpty())
         loaded.sync_username_attribute = kDefaultSyncUsernameAttribute;

      if (loaded.sync_display_name_attribute.IsEmpty())
         loaded.sync_display_name_attribute = kDefaultSyncDisplayNameAttribute;

      loaded.sync_max_users = ReadInteger_(iniFile, _T("SyncMaxUsers"), kDefaultSyncMaxUsers);

      // Clamped at BOTH ends, like TimeoutSeconds above and for the same reason: this
      // value exists to stop a misconfigured search base being answered by allocating
      // until the process dies, and a ceiling that the misconfigured file sets itself
      // is not a ceiling. Each entry costs a DN plus three attribute vectors - a few
      // hundred bytes - and the report is then built as one string and copied again
      // into a BSTR, so a value of 100000000 is two copies of a multi-gigabyte report
      // inside the service.
      if (loaded.sync_max_users <= 0)
         loaded.sync_max_users = kDefaultSyncMaxUsers;

      if (loaded.sync_max_users > kMaxSyncMaxUsers)
         loaded.sync_max_users = kMaxSyncMaxUsers;

      // Read UNSIGNED and clamped before it is ever an int, which is not fussiness.
      // GetPrivateProfileInt returns a UINT, so ReadInteger_'s cast wraps for anything
      // above INT_MAX: SyncScheduleMinutes=2592000000 - a plausible units mistake,
      // milliseconds in a month - arrives as -1702967296. The signed reading would then
      // hit the "<= 0 means off" branch below and SILENTLY DISABLE the schedule, which
      // is the exact opposite of what the ceiling exists to do, and it would do it
      // without a word to an administrator who has just asked for a schedule.
      const unsigned int rawScheduleMinutes =
         GetPrivateProfileInt(kLdapSection, _T("SyncScheduleMinutes"), 0, iniFile.c_str());

      // Zero means "no schedule". A value the ini cannot express as a positive number
      // at all - a negative sign, a word - is read by GetPrivateProfileInt as 0 and
      // means the same thing, and that is the safe reading of a mistake in the one
      // setting that creates accounts unattended: do nothing, rather than start running
      // every fifteen minutes because a character was wrong.
      if (rawScheduleMinutes == 0)
         loaded.sync_schedule_minutes = 0;
      else if (rawScheduleMinutes < (unsigned int) kMinSyncScheduleMinutes)
         loaded.sync_schedule_minutes = kMinSyncScheduleMinutes;
      else if (rawScheduleMinutes > (unsigned int) kMaxSyncScheduleMinutes)
         loaded.sync_schedule_minutes = kMaxSyncScheduleMinutes;
      else
         loaded.sync_schedule_minutes = (int) rawScheduleMinutes;

      {
         boost::lock_guard<boost::mutex> guard(settings_mutex_);

         configuration_ = loaded;
         settings_file_write_time_ = writeTime;
         settings_loaded_ = true;
      }
   }

   String
   LdapSettings::ReadString_(const String &iniFile, const wchar_t *key, const wchar_t *defaultValue)
   {
      // Same buffer size as IniFileSettings::ReadIniSettingString_, and for the same
      // reason: a value that does not fit is silently truncated, and a truncated
      // search base or DN template fails in a way that points at the directory rather
      // than at the ini file.
      const DWORD bufferSize = 4096;
      TCHAR value[bufferSize];

      GetPrivateProfileString(kLdapSection, key, defaultValue, value, bufferSize, iniFile.c_str());

      String result = value;
      result.Trim();

      return result;
   }

   int
   LdapSettings::ReadInteger_(const String &iniFile, const wchar_t *key, int defaultValue)
   {
      return (int) GetPrivateProfileInt(kLdapSection, key, defaultValue, iniFile.c_str());
   }
}
