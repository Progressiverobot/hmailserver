// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// Asking a domain's primary server whether an address exists, before this
// server accepts mail for it.
//
// WHY IT EXISTS. A backup MX accepts everything addressed to the domain it
// backs up, because it has no list of the domain's addresses - that list is on
// the primary, which is down, which is why the backup is being used. Every
// message for an address that does not exist is therefore accepted with 250,
// queued, offered to the primary when it returns, refused 550, and bounced by
// this server to whatever the envelope claimed as the sender. The envelope
// sender of a dictionary attack is somebody else, so the bounces go to them:
// the backup MX has become a backscatter source, and gets listed for it. The
// callout is how the address is checked while the SMTP session is still open,
// so the refusal goes to the machine that is actually sending.
//
// WHY IT IS DANGEROUS, and what is done about each part.
//
//   * A callout is a request this server makes on a stranger's say-so, which is
//     the shape of every reflection abuse. Rate-limited per remote (the policy's
//     own CalloutMaxPerMinute, ten a minute by default), and answered from a
//     cache for the rest of the lifetime, so a flood of addresses costs the
//     remote a handful of sessions rather than one per recipient.
//   * It can be used to enumerate a third party's recipients: point a policy at
//     somebody else's server, then read this server's answers. That is why the
//     callout is OFF unless an administrator turns it on for a named domain,
//     why the verdict is only ever used for the domain the policy names, and why
//     the documentation says plainly that it should be turned on for a domain
//     this server is a backup MX for and for nothing else.
//   * A refusal must never come from our own trouble. Anything other than a
//     clear permanent refusal from the primary - a timeout, a connection that
//     fails, a 4xx, a server that refuses the null sender, a rate limit of ours,
//     no host to ask - is Unknown, and Unknown accepts. A callout that cannot be
//     made leaves the server exactly where it was before this existed.
//   * A server this server is authoritative for is never called out to, and
//     neither is any address:port this server itself listens on. Without that,
//     a policy naming a local domain would make the server ask itself, on its
//     own SMTP thread, and wait for a reply it is not going to produce.
//
// The probe is an ordinary SMTP conversation with an empty sender - EHLO, MAIL
// FROM:<>, RCPT TO:<address>, QUIT - and the body is never offered, so nothing
// is delivered by it. The null sender is what RFC 5321 reserves for exactly
// this kind of non-delivery traffic; it also means the probe cannot be answered
// with a bounce.
//
// The conversation is synchronous and bounded. It runs on the thread handling
// RCPT TO, which is the same thread the DNS blacklist and SURBL lookups already
// block, and it is bounded by the policy's timeout (ten seconds by default,
// capped at CalloutMaxTimeoutSeconds) applied as a deadline over a private
// io_context - not by SO_RCVTIMEO, whose argument shape differs between Windows
// and Linux, so that the bound is the same on both.

#pragma once

#include "../Common/Util/Singleton.h"

#include <map>

namespace HM
{
   class RemoteDomainPolicy;

   class RecipientCallout : public Singleton<RecipientCallout>
   {
   public:
      RecipientCallout();
      virtual ~RecipientCallout();

      enum Verdict
      {
         // The primary answered 2xx to RCPT TO. Accept.
         CalloutAccepted = 0,

         // The primary answered a permanent refusal to RCPT TO. Refuse.
         CalloutRejected = 1,

         // Everything else. Accept - see the header comment.
         CalloutUnknown = 2
      };

      // The verdict for one address under one policy. reason is filled with a
      // sentence for the log and, for a rejection, for the SMTP reply.
      Verdict Verify(const String &recipientAddress, std::shared_ptr<RemoteDomainPolicy> policy, String &reason);

      // Forgets every remembered verdict. The administrator's way of saying "the
      // primary is fixed now, stop refusing"; reached from COM and from the REST
      // API, and used by the fixtures between cases.
      void ClearCache();
      int GetCacheSize() const;

      // How many verification sessions have been opened since the last reset.
      // The way "asked once, answered from the cache after that" is proved: the
      // count stands still while the answers keep coming.
      int GetProbeCount() const;
      void ResetProbeCount();

      // No policy may ask for longer than this. A policy is administrator input
      // and this runs inside an SMTP session; a four-hour timeout typed by
      // accident would hold the session, a delivery thread and the sender's
      // connection for four hours.
      static const int CalloutMaxTimeoutSeconds = 60;

   private:

      struct CachedVerdict
      {
         Verdict verdict = CalloutUnknown;
         String reason;
         time_t expires_at = 0;
      };

      // The conversation itself.
      static Verdict Probe_(const String &host, int port, const String &address, int timeoutSeconds, String &reason);

      // Where to ask: the policy's host if it names one, otherwise the domain's
      // MX hosts, lowest preference first. Anything this server listens on is
      // left out, which is the loop guard.
      static std::vector<std::pair<String, String> > ResolveTargets_(const String &domain, std::shared_ptr<RemoteDomainPolicy> policy, int port);

      mutable boost::recursive_mutex mutex_;
      std::map<String, CachedVerdict> cache_;
      int probe_count_;
   };
}
