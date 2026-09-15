// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// What this server will do when it talks to one named remote domain.
//
// Three separate questions an administrator asks about a partner, a customer or
// a provider, and they are one record because they are one subject and because
// answering them in three places would mean three pages, three REST resources
// and three chances to configure a domain in two contradictory ways:
//
//   * transport security - must mail to this domain be encrypted, must the
//     certificate verify, must it be proven by DANE; and must mail FROM this
//     domain arrive encrypted;
//   * what this server will attempt - the largest message it will try to send
//     there, how many connections it will open to it at once, how many messages
//     a minute it will push at it;
//   * whether a recipient is verified with the domain's primary server before
//     this server accepts mail for it, which is what a backup MX needs so that
//     it does not become a backscatter source while the primary is down.
//
// A route (Route.h) is the nearest thing that already exists and this is
// deliberately not one: a route says WHERE mail for a domain goes - a target
// host, a port, a credential - and replaces MX discovery. A policy says what
// this server will and will not do when it gets there, whatever found the
// server. The two compose: a domain may have both, and a policy applies to a
// routed delivery exactly as it applies to an MX one, because the administrator
// is making a statement about the domain rather than about how its server was
// found.
//
// HOW THIS COMPOSES WITH MTA-STS AND DANE, which already require TLS for a
// domain that publishes a policy (TlsPolicy.h): the requirement is the STRONGEST
// of the three, never the newest and never the average. MTA-STS enforce and a
// usable TLSA record each raise the floor on their own, and this record can only
// raise it further - there is no value here that lowers what a published policy
// demanded, and ServerInfo::DisableConnectionSecurity already refuses to
// downgrade a connection any of them marked. A domain that publishes an enforce
// policy and is also named here at RemoteTlsEncrypted still gets certificate
// verification, because MTA-STS asked for it.

#pragma once

namespace HM
{
   // How much transport security this server demands of a named remote.
   //
   // Ordered from permissive to strict and compared with <, so "the strictest of
   // the policies that match" is a max() and a value nobody recognises cannot
   // come out below the one that was asked for. Zero is the permissive value,
   // because zero is what a default-constructed record, a zeroed row or a
   // forgotten column produces: a policy that was never filled in cannot quietly
   // start refusing a domain's mail.
   //
   // The numbers are persisted in hm_remotedomainpolicies.policyoutboundtls and
   // are given by name over the REST API, so they must never be renumbered.
   enum RemoteTlsRequirement
   {
      // Whatever this server would have done anyway: the SMTP connection
      // security setting, plus MTA-STS and DANE if the domain publishes them.
      RemoteTlsDefault = 0,

      // The session must be encrypted. STARTTLS must be offered and the
      // handshake must succeed; the certificate is not judged. This is the
      // level for a partner whose certificate is self-signed or internal.
      RemoteTlsEncrypted = 1,

      // Encrypted, and the certificate must chain to a trusted root and name
      // the host being connected to. This is what MTA-STS enforce requires.
      RemoteTlsVerified = 2,

      // Encrypted, and the certificate must match a DNSSEC-validated TLSA
      // record published for the host (RFC 7672). A host that publishes no
      // usable TLSA record cannot satisfy this, and the delivery is deferred
      // rather than sent - which is the point of asking for it.
      RemoteTlsDane = 3
   };

   class RemoteDomainPolicy : public BusinessObject<RemoteDomainPolicy>
   {
   public:
      RemoteDomainPolicy();
      virtual ~RemoteDomainPolicy();

      // The collection's GetItemByName and the XML restore both need this.
      String GetName() const { return domain_name_; }

      void SetDomainName(const String &value) { domain_name_ = value; }
      String GetDomainName() const { return domain_name_; }

      void SetDescription(const String &value) { description_ = value; }
      String GetDescription() const { return description_; }

      void SetActive(bool value) { active_ = value; }
      bool GetActive() const { return active_; }

      void SetOutboundTlsRequirement(RemoteTlsRequirement value) { outbound_tls_ = value; }
      RemoteTlsRequirement GetOutboundTlsRequirement() const { return outbound_tls_; }

      void SetRequireInboundTls(bool value) { require_inbound_tls_ = value; }
      bool GetRequireInboundTls() const { return require_inbound_tls_; }

      // 0 means no limit of ours. A message larger than this is never offered to
      // the remote at all: the administrator has said it would not be taken.
      void SetMaxMessageSizeKB(long value) { max_message_size_kb_ = value; }
      long GetMaxMessageSizeKB() const { return max_message_size_kb_; }

      // 0 means unlimited. The number of SMTP connections this server will hold
      // open to this remote at the same time, across every delivery thread.
      void SetMaxConnections(long value) { max_connections_ = value; }
      long GetMaxConnections() const { return max_connections_; }

      // 0 means unlimited. Messages per minute this server will push at the
      // remote; beyond it a delivery is deferred and retried.
      void SetMaxMessagesPerMinute(long value) { max_messages_per_minute_ = value; }
      long GetMaxMessagesPerMinute() const { return max_messages_per_minute_; }

      // Exchange's remote-domain settings, and the two that actually matter:
      // whether this server may send an automatic reply to the domain, and
      // whether it may forward mail there. Both default to true, which is what
      // every installation did before this record existed.
      void SetAllowAutomaticReplies(bool value) { allow_automatic_replies_ = value; }
      bool GetAllowAutomaticReplies() const { return allow_automatic_replies_; }

      void SetAllowForwarding(bool value) { allow_forwarding_ = value; }
      bool GetAllowForwarding() const { return allow_forwarding_; }

      // The backup-MX callout: ask the domain's primary whether the address
      // exists before accepting mail for it.
      void SetCalloutEnabled(bool value) { callout_enabled_ = value; }
      bool GetCalloutEnabled() const { return callout_enabled_; }

      // The host to ask. Empty means the domain's own MX hosts, lowest
      // preference first, skipping any that resolves to this server.
      void SetCalloutHost(const String &value) { callout_host_ = value; }
      String GetCalloutHost() const { return callout_host_; }

      void SetCalloutPort(long value) { callout_port_ = value; }
      long GetCalloutPort() const { return callout_port_; }

      void SetCalloutTimeoutSeconds(long value) { callout_timeout_seconds_ = value; }
      long GetCalloutTimeoutSeconds() const { return callout_timeout_seconds_; }

      // How long a verdict is believed. 0 would mean asking the primary once per
      // recipient of every message, which is how a callout becomes a nuisance.
      void SetCalloutCacheMinutes(long value) { callout_cache_minutes_ = value; }
      long GetCalloutCacheMinutes() const { return callout_cache_minutes_; }

      // How many times a minute this server will open a verification session to
      // one remote. Beyond it the recipient is accepted without asking, never
      // refused: our own throttle must not reject somebody else's mail.
      void SetCalloutMaxPerMinute(long value) { callout_max_per_minute_ = value; }
      long GetCalloutMaxPerMinute() const { return callout_max_per_minute_; }

      bool XMLStore(XNode *parentNode, int options);
      bool XMLLoad(XNode *node, int options);
      bool XMLLoadSubItems(XNode *node, int options) { return true; }

   protected:

      String domain_name_;
      String description_;
      bool active_;
      RemoteTlsRequirement outbound_tls_;
      bool require_inbound_tls_;
      long max_message_size_kb_;
      long max_connections_;
      long max_messages_per_minute_;
      bool allow_automatic_replies_;
      bool allow_forwarding_;
      bool callout_enabled_;
      String callout_host_;
      long callout_port_;
      long callout_timeout_seconds_;
      long callout_cache_minutes_;
      long callout_max_per_minute_;
   };
}
