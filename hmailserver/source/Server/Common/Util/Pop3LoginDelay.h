// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// RFC 2449 LOGIN-DELAY: the minimum interval a POP3 client must leave between
// logins to one account.
//
// It exists because POP3 has no idle notification, so a client that wants to
// look responsive polls - and the ones that poll every ten seconds are not
// rare. Each poll is a TCP connection, a TLS handshake and a password
// verification, and the password verification is Argon2id by default, which is
// deliberately expensive. A handful of over-eager clients can therefore cost
// more than the mail does. LOGIN-DELAY is the standard way to say so: the
// server declares the interval in CAPA, and refuses a login that arrives early
// with -ERR [LOGIN-DELAY], which a conforming client backs off from rather
// than treating as a credential problem.
//
// Off by default (Pop3LoginDelaySeconds = 0), because a delay that surprises
// an administrator looks exactly like a broken mailbox.
//
// Keyed on the ACCOUNT, not on the name as typed, and that is safe here in a
// way it would not be for the lockout: this is only ever consulted AFTER
// authentication has succeeded, so the account has already been resolved and
// there is no existence oracle to worry about. It also means a failed logon
// costs nothing here and cannot be used to probe the table.
//
// In memory, like the sibling stores. A restart lets every client back in
// immediately, which is the right direction to fail: the alternative is a
// restart that locks every mailbox out for the length of the delay.
//
// Every public entry point carries an exception barrier and the fallback is
// always to ALLOW the login. Refusing a legitimate client because a std::map
// threw is a far worse outcome than skipping one rate limit, and an exception
// reaching TCPConnection reports HM5136, drops the session and rethrows so the
// worker writes a minidump.

#pragma once

#include "Singleton.h"

#include <map>

namespace HM
{
   class Pop3LoginDelay : public Singleton<Pop3LoginDelay>
   {
   public:
      // Seconds this account must still wait, or 0 if it may log in now. Reads
      // the configured delay; answers 0 immediately when the feature is off.
      int SecondsRemaining(const String &accountAddress);

      // Records a successful login. Only called when the feature is on, so an
      // administrator switching it on does not find every account already
      // holding a timestamp from an hour ago.
      void RecordLogin(const String &accountAddress);

      // The clock-and-config-supplied twins, public for the self-tests: the
      // /Test process runs against an ini with no delay configured, and expiry
      // across a clock step cannot be tested against time(0). The configuration
      // travels as a parameter exactly as AccountLockout's does.
      int SecondsRemainingAt(const String &accountAddress, time_t now, int delaySeconds);
      void RecordLoginAt(const String &accountAddress, time_t now);

   private:

      static String NormalizeKey_(const String &accountAddress);

      // Drops entries that are already older than the delay, so the table
      // tracks active clients rather than everyone who has ever logged in.
      void Prune_(time_t now, int delaySeconds);

      // Last-resort handler for the catch(...) around each public entry point.
      // Reported once, then only logged. Never throws.
      void ReportSwallowedException_(const char *source);

      boost::mutex mutex_;
      std::map<String, time_t> last_login_;

      // Guarded by mutex_. The sweep walks every tracked account and the mutex
      // is taken by every successful POP3 logon, so it is throttled rather than
      // run per login.
      time_t last_sweep_time_ = 0;
      bool table_full_reported_ = false;

      // Guards internal_error_reported_ only. Never held with mutex_.
      boost::mutex report_mutex_;
      bool internal_error_reported_ = false;
   };
}
