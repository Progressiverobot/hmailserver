// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// The REST API's settings routes: PUT /api/v1/settings and the anti-spam and logging groups. See RestApiServer.h.
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Three groups of settings, each a flat JSON object of snake_case keys, each
// read with GET and changed with PUT:
//
//    /api/v1/settings            the server-wide group InterfaceSettings holds
//    /api/v1/settings/antispam   InterfaceAntiSpam's scalars
//    /api/v1/settings/logging    InterfaceLogging's scalars
//
// Everything about a key is one row of one table below: its name, its type,
// whether it can be read and written, whether the running server picks the
// change up at once or at its next start, the words an enumerated value takes,
// the sentence the OpenAPI document shows for it, and the getter, setter and
// pre-apply check that reach the same code the COM property reaches. The GET
// walks the table, the PUT walks the table and the OpenAPI description is
// generated from the table, so the three cannot disagree about a key.
//
// A PUT accepts any subset of the group's writable keys and is all or nothing:
// every member of the body is resolved to a row and a typed value, every row's
// own check is run, and only then is anything applied - an unknown key, a
// value of the wrong type, a read-only key or a value a setting refuses is a
// 400 that names the key (or carries the setting's own sentence, the one the
// Control Panel shows) with nothing changed. The setters are the ones COM
// calls: a Property write reaches hm_settings at once and raises
// Configuration::OnPropertyChanged, which is what refreshes the logger, the
// work queues and the delivery manager, so a change over this route is seen
// by the running server exactly as one made in the Control Panel is. The
// three settings whose setter can itself refuse (the IMAP hierarchy delimiter,
// and the two tarpit values that live in hMailServer.ini) are applied before
// the rest, so a refusal there still leaves nothing else applied.

#include "StdAfx.h"
#include "RestApiServer.h"
#include "JsonDocument.h"
#include "Unicode.h"
#include "../Persistence/PersistentIMAPFolder.h"
#include "../Persistence/PersistentRuleAction.h"
#include "../../SMTP/SMTPConfiguration.h"
#include "../../IMAP/IMAPConfiguration.h"
#include "../../POP3/POP3Configuration.h"

#include <climits>
#include <string>
#include <vector>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace
{
   using namespace HM;

   // ---------------------------------------------------------------------
   // The shape of a row.
   // ---------------------------------------------------------------------

   // A setting's value in transit: one of the three members is meaningful,
   // decided by the row's kind. An enumerated value travels as its number,
   // the word having been mapped on the way in and being mapped on the way out.
   struct Value
   {
      Value() : number(0), flag(false) { }

      String text;
      long number;
      bool flag;
   };

   enum Kind
   {
      KindString,
      KindInteger,
      KindBoolean,
      KindEnum
   };

   enum Access
   {
      ReadWrite,

      // Emitted by GET, refused by PUT: the log directory and the names of
      // the current log files are facts about the server, not choices.
      ReadOnly,

      // Accepted by PUT, never emitted: the relayer password.
      WriteOnly
   };

   // Whether the running server reads the setting again after it changes,
   // decided per key from the code that reads it (see the table). The
   // OpenAPI description says so for every key that waits.
   enum Effect
   {
      EffectNow,

      // Read when the server starts - the network thread pool, which servers
      // to start, the TLS contexts the listeners are built with.
      EffectRestart,

      // Written to the store and read by nothing in this server. Kept because
      // the Control Panel writes it and a Deck that mirrors the Control Panel
      // must be able to as well; the description says it is inert.
      EffectStored
   };

   struct Word
   {
      const char *word;
      long value;
   };

   // What a setter reports. status 0 is applied; 400 carries the setting's
   // own refusal sentence; 500 is a write that failed after the value was
   // accepted (an INI file that could not be written).
   struct Outcome
   {
      Outcome() : status(0) { }

      int status;
      String message;
   };

   struct Row
   {
      const char *key;
      Kind kind;
      Access access;
      Effect effect;

      // KindEnum only: the words and their stored values, ended by a null word.
      const Word *words;

      // The OpenAPI description, in one sentence. No quotes or backslashes.
      const char *description;

      // Null for a write-only row.
      Value (*get)();

      // Null for a read-only row.
      Outcome (*set)(const Value &value);

      // Optional: refuses a value before anything in the request is applied,
      // in the setting's own sentence. Empty means acceptable.
      String (*check)(const Value &value);
   };

   struct Group
   {
      const char *name;          // for the log line and the OpenAPI summary
      const char *path;
      const Row *rows;
      size_t count;
   };

   // ---------------------------------------------------------------------
   // The accessors the rows use.
   // ---------------------------------------------------------------------

   Value Text(const String &text) { Value v; v.text = text; return v; }
   Value Number(long number) { Value v; v.number = number; return v; }
   Value Flag(bool flag) { Value v; v.flag = flag; return v; }

   Outcome Applied() { return Outcome(); }

   Outcome Refused(const char *sentence)
   {
      Outcome o;
      o.status = 400;
      o.message = sentence;
      return o;
   }

   Outcome Failed(const char *sentence)
   {
      Outcome o;
      o.status = 500;
      o.message = sentence;
      return o;
   }

   Configuration *Config() { return Configuration::Instance(); }
   std::shared_ptr<SMTPConfiguration> Smtp() { return Configuration::Instance()->GetSMTPConfiguration(); }
   std::shared_ptr<IMAPConfiguration> Imap() { return Configuration::Instance()->GetIMAPConfiguration(); }
   std::shared_ptr<POP3Configuration> Pop3() { return Configuration::Instance()->GetPOP3Configuration(); }
   AntiSpamConfiguration &AntiSpam() { return Configuration::Instance()->GetAntiSpamConfiguration(); }
   IniFileSettings *Ini() { return IniFileSettings::Instance(); }

   // The words the ports and routes use for eConnectionSecurity, in the order
   // the Control Panel lists them.
   const Word ConnectionSecurityWords[] =
   {
      { "none", CSNone },
      { "starttls_optional", CSSTARTTLSOptional },
      { "starttls_required", CSSTARTTLSRequired },
      { "tls", CSSSL },
      { nullptr, 0 }
   };

   // Logger::LogDevice after the COM layer's mapping. "unknown" is the value
   // every installation has until the device is chosen, and behaves as file.
   const Word LogDeviceWords[] =
   {
      { "unknown", 0 },
      { "sql", 1 },
      { "file", 2 },
      { nullptr, 0 }
   };

   // Logger::LogOutputFormat after the COM layer's mapping.
   const Word LogFormatWords[] =
   {
      { "default", 0 },
      { "csa", 1 },
      { nullptr, 0 }
   };

   // InterfaceSettings::put_IMAPHierarchyDelimiter's sentence, verbatim.
   const char *HierarchyDelimiterRefusal =
      "It was not possible to change the IMAP hierarchy delimiter. It has probably failed because there exists one or more IMAP folders containing the new character.";

   // InterfaceAntiSpam's sentences for the two tarpit values, verbatim.
   const char *TarpitDelayRefusal = "TarpitDelay must be between 0 and 30 seconds.";
   const char *TarpitCountRefusal = "TarpitCount cannot be negative.";
   const char *IniWriteFailure = "The setting could not be written to hMailServer.INI. Nothing has been changed.";

   // Every logging switch is one bit of PROPERTY_LOGGING, and each Configuration
   // setter rewrites the whole mask, so setting one bit at a time is what COM
   // does too (InterfaceLogging::put_LogSMTP and the rest).

   // ---------------------------------------------------------------------
   // The tables. A captureless lambda converts to the function pointer the
   // row holds; the macros keep a row to a few lines so that the table reads
   // as a table. v is the Value being applied.
   // ---------------------------------------------------------------------

#define ROW_TEXT(expr)      [] { return Text(expr); }
#define ROW_NUMBER(expr)    [] { return Number((long) (expr)); }
#define ROW_FLAG(expr)      [] { return Flag((expr) ? true : false); }
#define ROW_SET(statement)      [] (const Value &v) { statement; return Applied(); }
#define ROW_NO_CHECK            nullptr

   const Row ServerRows[] =
   {
      // Identity.
      { "host_name", KindString, ReadWrite, EffectNow, nullptr,
        "The name the server gives in its SMTP greeting, in HELO/EHLO and in Received headers.",
        ROW_TEXT(Config()->GetHostName()),
        [] (const Value &v) { String name = v.text; Config()->SetHostName(name); return Applied(); }, ROW_NO_CHECK },
      { "default_domain", KindString, ReadWrite, EffectNow, nullptr,
        "The domain appended to a bare user name at logon.",
        ROW_TEXT(Config()->GetDefaultDomain()), ROW_SET(Config()->SetDefaultDomain(v.text)), ROW_NO_CHECK },
      { "mirror_email_address", KindString, ReadWrite, EffectNow, nullptr,
        "An address that receives a copy of every message the server handles, or empty for none.",
        ROW_TEXT(Config()->GetMirrorAddress()), ROW_SET(Config()->SetMirrorAddress(v.text)), ROW_NO_CHECK },

      // Limits.
      { "max_message_size_kb", KindInteger, ReadWrite, EffectNow, nullptr,
        "The largest message SMTP and IMAP APPEND accept, in KB; 0 is no limit.",
        ROW_NUMBER(Smtp()->GetMaxMessageSize()), ROW_SET(Smtp()->SetMaxMessageSize((int) v.number)), ROW_NO_CHECK },
      { "max_smtp_connections", KindInteger, ReadWrite, EffectNow, nullptr,
        "The most simultaneous SMTP connections; 0 is no limit.",
        ROW_NUMBER(Smtp()->GetMaxSMTPConnections()), ROW_SET(Smtp()->SetMaxSMTPConnections((int) v.number)), ROW_NO_CHECK },
      { "max_imap_connections", KindInteger, ReadWrite, EffectNow, nullptr,
        "The most simultaneous IMAP connections; 0 is no limit.",
        ROW_NUMBER(Imap()->GetMaxIMAPConnections()), ROW_SET(Imap()->SetMaxIMAPConnections((int) v.number)), ROW_NO_CHECK },
      { "max_pop3_connections", KindInteger, ReadWrite, EffectNow, nullptr,
        "The most simultaneous POP3 connections; 0 is no limit.",
        ROW_NUMBER(Pop3()->GetMaxPOP3Connections()), ROW_SET(Pop3()->SetMaxPOP3Connections((int) v.number)), ROW_NO_CHECK },
      { "max_delivery_threads", KindInteger, ReadWrite, EffectNow, nullptr,
        "The most messages delivered at the same time.",
        ROW_NUMBER(Smtp()->GetMaxNoOfDeliveryThreads()), ROW_SET(Smtp()->SetMaxNoOfDeliveryThreads((int) v.number)), ROW_NO_CHECK },
      { "max_asynchronous_threads", KindInteger, ReadWrite, EffectNow, nullptr,
        "The most background tasks run at the same time.",
        ROW_NUMBER(Config()->GetAsynchronousThreads()), ROW_SET(Config()->SetAsynchronousThreads((int) v.number)), ROW_NO_CHECK },
      { "tcpip_threads", KindInteger, ReadWrite, EffectRestart, nullptr,
        "The size of the thread pool that serves every connection.",
        ROW_NUMBER(Config()->GetTCPIPThreads()), ROW_SET(Config()->SetTCPIPThreads((int) v.number)), ROW_NO_CHECK },
      { "worker_thread_priority", KindInteger, ReadWrite, EffectStored, nullptr,
        "The Control Panel's worker thread priority.",
        ROW_NUMBER(Config()->GetWorkerThreadPriority()), ROW_SET(Config()->SetWorkerThreadPriority((int) v.number)), ROW_NO_CHECK },

      // Which servers run.
      { "service_smtp", KindBoolean, ReadWrite, EffectRestart, nullptr,
        "Whether the SMTP server is started.",
        ROW_FLAG(Config()->GetUseSMTP()), ROW_SET(Config()->SetUseSMTP(v.flag)), ROW_NO_CHECK },
      { "service_pop3", KindBoolean, ReadWrite, EffectRestart, nullptr,
        "Whether the POP3 server is started.",
        ROW_FLAG(Config()->GetUsePOP3()), ROW_SET(Config()->SetUsePOP3(v.flag)), ROW_NO_CHECK },
      { "service_imap", KindBoolean, ReadWrite, EffectRestart, nullptr,
        "Whether the IMAP server is started.",
        ROW_FLAG(Config()->GetUseIMAP()), ROW_SET(Config()->SetUseIMAP(v.flag)), ROW_NO_CHECK },

      // Greetings.
      { "welcome_smtp", KindString, ReadWrite, EffectNow, nullptr,
        "The SMTP greeting, or empty for the default.",
        ROW_TEXT(Smtp()->GetWelcomeMessage()), ROW_SET(Smtp()->SetWelcomeMessage(v.text)), ROW_NO_CHECK },
      { "welcome_pop3", KindString, ReadWrite, EffectNow, nullptr,
        "The POP3 greeting, or empty for the default.",
        ROW_TEXT(Pop3()->GetWelcomeMessage()), ROW_SET(Pop3()->SetWelcomeMessage(v.text)), ROW_NO_CHECK },
      { "welcome_imap", KindString, ReadWrite, EffectNow, nullptr,
        "The IMAP greeting, or empty for the default.",
        ROW_TEXT(Imap()->GetWelcomeMessage()), ROW_SET(Imap()->SetWelcomeMessage(v.text)), ROW_NO_CHECK },

      // Outbound delivery.
      { "smtp_relayer", KindString, ReadWrite, EffectNow, nullptr,
        "The smart host every outbound message is handed to, or empty for direct delivery to the recipient's MX.",
        ROW_TEXT(Smtp()->GetSMTPRelayer()), ROW_SET(Smtp()->SetSMTPRelayer(v.text)), ROW_NO_CHECK },
      { "smtp_relayer_port", KindInteger, ReadWrite, EffectNow, nullptr,
        "The smart host's port.",
        ROW_NUMBER(Smtp()->GetSMTPRelayerPort()), ROW_SET(Smtp()->SetSMTPRelayerPort(v.number)), ROW_NO_CHECK },
      { "smtp_relayer_connection_security", KindEnum, ReadWrite, EffectNow, ConnectionSecurityWords,
        "How the connection to the smart host is secured.",
        ROW_NUMBER(Smtp()->GetSMTPRelayerConnectionSecurity()),
        ROW_SET(Smtp()->SetSMTPRelayerConnectionSecurity((ConnectionSecurity) v.number)), ROW_NO_CHECK },
      { "smtp_relayer_requires_authentication", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether the server authenticates to the smart host.",
        ROW_FLAG(Smtp()->GetSMTPRelayerRequiresAuthentication()),
        ROW_SET(Smtp()->SetSMTPRelayerRequiresAuthentication(v.flag)), ROW_NO_CHECK },
      { "smtp_relayer_username", KindString, ReadWrite, EffectNow, nullptr,
        "The user name for the smart host.",
        ROW_TEXT(Smtp()->GetSMTPRelayerUsername()), ROW_SET(Smtp()->SetSMTPRelayerUsername(v.text)), ROW_NO_CHECK },
      { "smtp_relayer_password", KindString, WriteOnly, EffectNow, nullptr,
        "The password for the smart host. Accepted here and never emitted.",
        nullptr, ROW_SET(Smtp()->SetSMTPRelayerPassword(v.text)), ROW_NO_CHECK },
      { "smtp_connection_security", KindEnum, ReadWrite, EffectNow, ConnectionSecurityWords,
        "How a direct delivery to a recipient's MX is secured.",
        ROW_NUMBER(Smtp()->GetSMTPConnectionSecurity()),
        ROW_SET(Smtp()->SetSMTPConnectionSecurity((ConnectionSecurity) v.number)), ROW_NO_CHECK },
      { "smtp_no_of_tries", KindInteger, ReadWrite, EffectNow, nullptr,
        "How many times a delivery is attempted before the message is returned.",
        ROW_NUMBER(Smtp()->GetNoOfRetries()), ROW_SET(Smtp()->SetNoOfRetries(v.number)), ROW_NO_CHECK },
      { "smtp_minutes_between_try", KindInteger, ReadWrite, EffectNow, nullptr,
        "The minutes between two delivery attempts.",
        ROW_NUMBER(Smtp()->GetMinutesBetweenTry()), ROW_SET(Smtp()->SetMinutesBetweenTry(v.number)), ROW_NO_CHECK },
      { "smtp_delivery_bind_to_ip", KindString, ReadWrite, EffectNow, nullptr,
        "The local address outbound SMTP connects from, or empty for any.",
        ROW_TEXT(Smtp()->GetSMTPDeliveryBindToIP()), ROW_SET(Smtp()->SetSMTPDeliveryBindToIP(v.text)), ROW_NO_CHECK },
      { "max_smtp_recipients_in_batch", KindInteger, ReadWrite, EffectNow, nullptr,
        "The most recipients handed to one host in one SMTP session.",
        ROW_NUMBER(Smtp()->GetMaxSMTPRecipientsInBatch()), ROW_SET(Smtp()->SetMaxSMTPRecipientsInBatch((int) v.number)), ROW_NO_CHECK },
      { "max_number_of_mx_hosts", KindInteger, ReadWrite, EffectNow, nullptr,
        "The most MX hosts tried for one recipient domain.",
        ROW_NUMBER(Smtp()->GetMaxNumberOfMXHosts()), ROW_SET(Smtp()->SetMaxNumberOfMXHosts((int) v.number)), ROW_NO_CHECK },

      // Inbound SMTP behaviour.
      { "allow_smtp_auth_plain", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether AUTH PLAIN and AUTH LOGIN are offered on an unencrypted connection.",
        ROW_FLAG(Smtp()->GetAuthAllowPlainText()), ROW_SET(Smtp()->SetAuthAllowPlainText(v.flag)), ROW_NO_CHECK },
      { "deny_mail_from_null", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether an empty envelope sender (MAIL FROM:<>) is refused.",
        ROW_FLAG(!Smtp()->GetAllowMailFromNull()), ROW_SET(Smtp()->SetAllowMailFromNull(!v.flag)), ROW_NO_CHECK },
      { "allow_incorrect_line_endings", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether a bare LF in SMTP data is accepted as a line ending.",
        ROW_FLAG(Smtp()->GetAllowIncorrectLineEndings()), ROW_SET(Smtp()->SetAllowIncorrectLineEndings(v.flag)), ROW_NO_CHECK },
      { "add_delivered_to_header", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether a Delivered-To header is added on local delivery.",
        ROW_FLAG(Smtp()->GetAddDeliveredToHeader()), ROW_SET(Smtp()->SetAddDeliveredToHeader(v.flag)), ROW_NO_CHECK },
      { "disconnect_invalid_clients", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether a client is disconnected after too many invalid commands.",
        ROW_FLAG(Config()->GetDisconnectInvalidClients()), ROW_SET(Config()->SetDisconnectInvalidClients(v.flag)), ROW_NO_CHECK },
      { "max_number_of_invalid_commands", KindInteger, ReadWrite, EffectNow, nullptr,
        "How many invalid commands are tolerated before the disconnect.",
        ROW_NUMBER(Config()->GetMaximumIncorrectCommands()), ROW_SET(Config()->SetMaximumIncorrectCommands((int) v.number)), ROW_NO_CHECK },
      { "rule_loop_limit", KindInteger, ReadWrite, EffectNow, nullptr,
        "How many times rules may re-deliver one message before the loop is stopped.",
        ROW_NUMBER(Smtp()->GetRuleLoopLimit()), ROW_SET(Smtp()->SetRuleLoopLimit((int) v.number)), ROW_NO_CHECK },

      // IMAP.
      { "imap_public_folder_name", KindString, ReadWrite, EffectNow, nullptr,
        "The name under which the public folders appear in IMAP LIST.",
        ROW_TEXT(Imap()->GetIMAPPublicFolderName()), ROW_SET(Imap()->SetIMAPPublicFolderName(v.text)), ROW_NO_CHECK },
      { "imap_hierarchy_delimiter", KindString, ReadWrite, EffectNow, nullptr,
        "The character between IMAP folder names. Refused while a folder or a rule action contains the new character; rule actions are rewritten when it changes.",
        ROW_TEXT(Imap()->GetHierarchyDelimiter()),
        [] (const Value &v) { return Imap()->SetHierarchyDelimiter(v.text) ? Applied() : Refused(HierarchyDelimiterRefusal); },
        [] (const Value &v) -> String
        {
           // The setter's own two read-only checks, run before anything in the
           // request is applied; the setter runs them again when it applies.
           if (Imap()->GetHierarchyDelimiter() == v.text)
              return String();
           if (PersistentIMAPFolder::GetExistsFolderContainingCharacter(v.text) ||
               PersistentRuleAction::GetExistsFolderReferenceContainingCharacter(v.text))
              return String(HierarchyDelimiterRefusal);
           return String();
        } },
      { "imap_master_user", KindString, ReadWrite, EffectNow, nullptr,
        "The IMAP master user name, or empty for none.",
        ROW_TEXT(Imap()->GetIMAPMasterUser()), ROW_SET(Imap()->SetIMAPMasterUser(v.text)), ROW_NO_CHECK },
      { "imap_sasl_plain_enabled", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether AUTHENTICATE PLAIN is offered.",
        ROW_FLAG(Imap()->GetUseIMAPSASLPlain()), ROW_SET(Imap()->SetUseIMAPSASLPlain(v.flag)), ROW_NO_CHECK },
      { "imap_sasl_initial_response_enabled", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether SASL-IR is offered.",
        ROW_FLAG(Imap()->GetUseIMAPSASLInitialResponse()), ROW_SET(Imap()->SetUseIMAPSASLInitialResponse(v.flag)), ROW_NO_CHECK },
      { "imap_sort_enabled", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether the SORT extension is offered.",
        ROW_FLAG(Imap()->GetUseIMAPSort()), ROW_SET(Imap()->SetUseIMAPSort(v.flag)), ROW_NO_CHECK },
      { "imap_quota_enabled", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether the QUOTA extension is offered.",
        ROW_FLAG(Imap()->GetUseIMAPQuota()), ROW_SET(Imap()->SetUseIMAPQuota(v.flag)), ROW_NO_CHECK },
      { "imap_idle_enabled", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether the IDLE extension is offered.",
        ROW_FLAG(Imap()->GetUseIMAPIdle()), ROW_SET(Imap()->SetUseIMAPIdle(v.flag)), ROW_NO_CHECK },
      { "imap_acl_enabled", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether the ACL extension is offered.",
        ROW_FLAG(Imap()->GetUseIMAPACL()), ROW_SET(Imap()->SetUseIMAPACL(v.flag)), ROW_NO_CHECK },

      // The auto-ban.
      { "auto_ban_on_logon_failure", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether an address is banned after repeated failed logons.",
        ROW_FLAG(Config()->GetAutoBanLogonEnabled()), ROW_SET(Config()->SetAutoBanLogonEnabled(v.flag)), ROW_NO_CHECK },
      { "max_invalid_logon_attempts", KindInteger, ReadWrite, EffectNow, nullptr,
        "The failed logons from one address that trip the ban.",
        ROW_NUMBER(Config()->GetMaxInvalidLogonAttempts()), ROW_SET(Config()->SetMaxInvalidLogonAttempts((int) v.number)), ROW_NO_CHECK },
      { "minutes_before_reset", KindInteger, ReadWrite, EffectNow, nullptr,
        "The minutes a failed logon counts towards the ban.",
        ROW_NUMBER(Config()->GetMaxLogonAttemptsWithin()), ROW_SET(Config()->SetMaxLogonAttemptsWithin((int) v.number)), ROW_NO_CHECK },
      { "minutes_to_ban", KindInteger, ReadWrite, EffectNow, nullptr,
        "How long the ban lasts, in minutes.",
        ROW_NUMBER(Config()->GetAutoBanMinutes()), ROW_SET(Config()->SetAutoBanMinutes((int) v.number)), ROW_NO_CHECK },

      // TLS. The listeners build their contexts when they start and the
      // outbound client context is built with the I/O service, so these wait
      // for a restart (SslContextInitializer, TCPServer::Run, IOService).
      { "tls_prefer_server_ciphers", KindBoolean, ReadWrite, EffectRestart, nullptr,
        "Whether the server's cipher order wins over the client's.",
        ROW_FLAG(Config()->GetTlsOptionEnabled(TlsOptionPreferServerCiphers)),
        ROW_SET(Config()->SetTlsOptionEnabled(TlsOptionPreferServerCiphers, v.flag)), ROW_NO_CHECK },
      { "tls_prioritize_chacha", KindBoolean, ReadWrite, EffectRestart, nullptr,
        "Whether ChaCha20-Poly1305 is preferred for a client that puts it first.",
        ROW_FLAG(Config()->GetTlsOptionEnabled(TlsOptionPrioritizeChaCha)),
        ROW_SET(Config()->SetTlsOptionEnabled(TlsOptionPrioritizeChaCha, v.flag)), ROW_NO_CHECK },
      { "ssl_cipher_list", KindString, ReadWrite, EffectRestart, nullptr,
        "The OpenSSL cipher list for TLS 1.2 and below.",
        ROW_TEXT(Config()->GetSslCipherList()), ROW_SET(Config()->SetSslCipherList(v.text)), ROW_NO_CHECK },
      { "tls_version_10_enabled", KindBoolean, ReadWrite, EffectRestart, nullptr,
        "Whether TLS 1.0 is accepted.",
        ROW_FLAG(Config()->GetSslVersionEnabled(TlsVersion10)), ROW_SET(Config()->SetSslVersionEnabled(TlsVersion10, v.flag)), ROW_NO_CHECK },
      { "tls_version_11_enabled", KindBoolean, ReadWrite, EffectRestart, nullptr,
        "Whether TLS 1.1 is accepted.",
        ROW_FLAG(Config()->GetSslVersionEnabled(TlsVersion11)), ROW_SET(Config()->SetSslVersionEnabled(TlsVersion11, v.flag)), ROW_NO_CHECK },
      { "tls_version_12_enabled", KindBoolean, ReadWrite, EffectRestart, nullptr,
        "Whether TLS 1.2 is accepted.",
        ROW_FLAG(Config()->GetSslVersionEnabled(TlsVersion12)), ROW_SET(Config()->SetSslVersionEnabled(TlsVersion12, v.flag)), ROW_NO_CHECK },
      { "tls_version_13_enabled", KindBoolean, ReadWrite, EffectRestart, nullptr,
        "Whether TLS 1.3 is accepted.",
        ROW_FLAG(Config()->GetSslVersionEnabled(TlsVersion13)), ROW_SET(Config()->SetSslVersionEnabled(TlsVersion13, v.flag)), ROW_NO_CHECK },
      { "verify_remote_ssl_certificate", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether the certificate of a server this one connects to is verified.",
        ROW_FLAG(Config()->GetVerifyRemoteSslCertificate()), ROW_SET(Config()->SetVerifyRemoteSslCertificate(v.flag)), ROW_NO_CHECK },

      // Miscellany.
      { "ipv6_preferred", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether an AAAA record is tried before an A record.",
        ROW_FLAG(Config()->GetIPv6Preferred()), ROW_SET(Config()->SetIPv6Preferred(v.flag)), ROW_NO_CHECK },
      { "rewrite_envelope_from_when_forwarding", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether a forwarded message leaves with the forwarding account as its envelope sender.",
        ROW_FLAG(Ini()->GetRewriteEnvelopeFromWhenForwarding()), ROW_SET(Ini()->SetRewriteEnvelopeFromWhenForwarding(v.flag)), ROW_NO_CHECK },
      { "create_default_special_use_folders", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether a new account gets Drafts, Sent, Junk and Trash folders.",
        ROW_FLAG(Config()->GetCreateDefaultSpecialUseFolders()), ROW_SET(Config()->SetCreateDefaultSpecialUseFolders(v.flag)), ROW_NO_CHECK },

      // The conversation-logging switches the snapshot has always carried;
      // the same bits as log_smtp, log_imap and log_pop3 in the logging group.
      { "log_smtp_conversations", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether SMTP sessions are written to the log; the same switch as log_smtp in the logging group.",
        ROW_FLAG(Config()->GetLogSMTPConversations()), ROW_SET(Config()->SetLogSMTPConversations(v.flag)), ROW_NO_CHECK },
      { "log_imap_conversations", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether IMAP sessions are written to the log; the same switch as log_imap in the logging group.",
        ROW_FLAG(Config()->GetLogIMAPConversations()), ROW_SET(Config()->SetLogIMAPConversations(v.flag)), ROW_NO_CHECK },
      { "log_pop3_conversations", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether POP3 sessions are written to the log; the same switch as log_pop3 in the logging group.",
        ROW_FLAG(Config()->GetLogPOP3Conversations()), ROW_SET(Config()->SetLogPOP3Conversations(v.flag)), ROW_NO_CHECK },
   };

   const Row AntiSpamRows[] =
   {
      { "spam_mark_threshold", KindInteger, ReadWrite, EffectNow, nullptr,
        "The score at which a message is treated as spam.",
        ROW_NUMBER(AntiSpam().GetSpamMarkThreshold()), ROW_SET(AntiSpam().SetSpamMarkThreshold((int) v.number)), ROW_NO_CHECK },
      { "spam_delete_threshold", KindInteger, ReadWrite, EffectNow, nullptr,
        "The score at which a message is deleted.",
        ROW_NUMBER(AntiSpam().GetSpamDeleteThreshold()), ROW_SET(AntiSpam().SetSpamDeleteThreshold((int) v.number)), ROW_NO_CHECK },
      { "add_header_spam", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether an X-hMailServer-Spam header is added to a message treated as spam.",
        ROW_FLAG(AntiSpam().GetAddHeaderSpam()), ROW_SET(AntiSpam().SetAddHeaderSpam(v.flag)), ROW_NO_CHECK },
      { "add_header_reason", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether X-hMailServer-Reason headers name each test that scored.",
        ROW_FLAG(AntiSpam().GetAddHeaderReason()), ROW_SET(AntiSpam().SetAddHeaderReason(v.flag)), ROW_NO_CHECK },
      { "prepend_subject", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether the subject of a message treated as spam is prefixed.",
        ROW_FLAG(AntiSpam().GetPrependSubject()), ROW_SET(AntiSpam().SetPrependSubject(v.flag)), ROW_NO_CHECK },
      { "prepend_subject_text", KindString, ReadWrite, EffectNow, nullptr,
        "The subject prefix.",
        ROW_TEXT(AntiSpam().GetPrependSubjectText()), ROW_SET(AntiSpam().SetPrependSubjectText(v.text)), ROW_NO_CHECK },
      { "use_spf", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether the sender's SPF record is checked.",
        ROW_FLAG(AntiSpam().GetUseSPF()), ROW_SET(AntiSpam().SetUseSPF(v.flag)), ROW_NO_CHECK },
      { "use_spf_score", KindInteger, ReadWrite, EffectNow, nullptr,
        "The score an SPF failure adds.",
        ROW_NUMBER(AntiSpam().GetUseSPFScore()), ROW_SET(AntiSpam().SetUseSPFScore((int) v.number)), ROW_NO_CHECK },
      { "check_host_in_helo", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether the HELO/EHLO name must resolve to the connecting address.",
        ROW_FLAG(AntiSpam().GetCheckHostInHelo()), ROW_SET(AntiSpam().SetCheckHostInHelo(v.flag)), ROW_NO_CHECK },
      { "check_host_in_helo_score", KindInteger, ReadWrite, EffectNow, nullptr,
        "The score a HELO/EHLO mismatch adds.",
        ROW_NUMBER(AntiSpam().GetCheckHostInHeloScore()), ROW_SET(AntiSpam().SetCheckHostInHeloScore((int) v.number)), ROW_NO_CHECK },
      { "check_mx_records", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether the sender's domain must have an MX or an A record.",
        ROW_FLAG(AntiSpam().GetUseMXChecks()), ROW_SET(AntiSpam().SetUseMXChecks(v.flag)), ROW_NO_CHECK },
      { "check_mx_records_score", KindInteger, ReadWrite, EffectNow, nullptr,
        "The score a sender domain without an MX or an A record adds.",
        ROW_NUMBER(AntiSpam().GetUseMXChecksScore()), ROW_SET(AntiSpam().SetUseMXChecksScore((int) v.number)), ROW_NO_CHECK },
      { "check_ptr", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether the connecting address must have a PTR record.",
        ROW_FLAG(AntiSpam().GetCheckPTR()), ROW_SET(AntiSpam().SetCheckPTR(v.flag)), ROW_NO_CHECK },
      { "check_ptr_score", KindInteger, ReadWrite, EffectNow, nullptr,
        "The score a missing PTR record adds.",
        ROW_NUMBER(AntiSpam().GetCheckPTRScore()), ROW_SET(AntiSpam().SetCheckPTRScore((int) v.number)), ROW_NO_CHECK },
      { "dkim_verification_enabled", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether DKIM signatures are verified.",
        ROW_FLAG(AntiSpam().GetDKIMVerificationEnabled()), ROW_SET(AntiSpam().SetDKIMVerificationEnabled(v.flag)), ROW_NO_CHECK },
      { "dkim_verification_failure_score", KindInteger, ReadWrite, EffectNow, nullptr,
        "The score a failed DKIM signature adds.",
        ROW_NUMBER(AntiSpam().GetDKIMVerificationFailureScore()), ROW_SET(AntiSpam().SetDKIMVerificationFailureScore((int) v.number)), ROW_NO_CHECK },
      { "dmarc_enabled", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether the sender domain's DMARC policy is evaluated.",
        ROW_FLAG(AntiSpam().GetDMARCEnabled()), ROW_SET(AntiSpam().SetDMARCEnabled(v.flag)), ROW_NO_CHECK },
      { "dmarc_failure_score", KindInteger, ReadWrite, EffectNow, nullptr,
        "The score a DMARC failure adds.",
        ROW_NUMBER(AntiSpam().GetDMARCFailureScore()), ROW_SET(AntiSpam().SetDMARCFailureScore((int) v.number)), ROW_NO_CHECK },
      { "arc_filtering_enabled", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether an ARC chain from a trusted sealer overrides SPF, DKIM and DMARC failures.",
        ROW_FLAG(AntiSpam().GetArcFilteringEnabled()), ROW_SET(AntiSpam().SetArcFilteringEnabled(v.flag)), ROW_NO_CHECK },
      { "arc_trusted_sealers", KindString, ReadWrite, EffectNow, nullptr,
        "The ARC sealer domains whose chains are trusted, comma-separated.",
        ROW_TEXT(AntiSpam().GetArcTrustedSealers()), ROW_SET(AntiSpam().SetArcTrustedSealers(v.text)), ROW_NO_CHECK },
      { "spamassassin_enabled", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether messages are passed to a SpamAssassin daemon.",
        ROW_FLAG(AntiSpam().GetSpamAssassinEnabled()), ROW_SET(AntiSpam().SetSpamAssassinEnabled(v.flag)), ROW_NO_CHECK },
      { "spamassassin_host", KindString, ReadWrite, EffectNow, nullptr,
        "The SpamAssassin daemon's host.",
        ROW_TEXT(AntiSpam().GetSpamAssassinHost()), ROW_SET(AntiSpam().SetSpamAssassinHost(v.text)), ROW_NO_CHECK },
      { "spamassassin_port", KindInteger, ReadWrite, EffectNow, nullptr,
        "The SpamAssassin daemon's port.",
        ROW_NUMBER(AntiSpam().GetSpamAssassinPort()), ROW_SET(AntiSpam().SetSpamAssassinPort((int) v.number)), ROW_NO_CHECK },
      { "spamassassin_score", KindInteger, ReadWrite, EffectNow, nullptr,
        "The score added when SpamAssassin judges a message to be spam and its own score is not merged.",
        ROW_NUMBER(AntiSpam().GetSpamAssassinScore()), ROW_SET(AntiSpam().SetSpamAssassinScore((int) v.number)), ROW_NO_CHECK },
      { "spamassassin_merge_score", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether SpamAssassin's own score is added instead of spamassassin_score.",
        ROW_FLAG(AntiSpam().GetSpamAssassinMergeScore()), ROW_SET(AntiSpam().SetSpamAssassinMergeScore(v.flag)), ROW_NO_CHECK },
      { "tarpit_count", KindInteger, ReadWrite, EffectNow, nullptr,
        "The recipient count in one session after which each further RCPT TO is delayed; 0 is off. Kept in hMailServer.ini.",
        ROW_NUMBER(Ini()->GetSmtpTarpitCount()),
        [] (const Value &v) { return Ini()->SetSmtpTarpitCount((int) v.number) ? Applied() : Failed(IniWriteFailure); },
        [] (const Value &v) { return v.number < 0 ? String(TarpitCountRefusal) : String(); } },
      { "tarpit_delay", KindInteger, ReadWrite, EffectNow, nullptr,
        "The delay in seconds, 0 to 30. Kept in hMailServer.ini.",
        ROW_NUMBER(Ini()->GetSmtpTarpitDelaySeconds()),
        [] (const Value &v) { return Ini()->SetSmtpTarpitDelaySeconds((int) v.number) ? Applied() : Failed(IniWriteFailure); },
        [] (const Value &v) { return (v.number < 0 || v.number > IniFileSettings::TARPIT_MAX_SECONDS) ? String(TarpitDelayRefusal) : String(); } },
      { "greylisting_enabled", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether a first delivery attempt from an unknown sender, recipient and address is deferred.",
        ROW_FLAG(AntiSpam().GetUseGreyListing()), ROW_SET(AntiSpam().SetUseGreyListing(v.flag)), ROW_NO_CHECK },
      { "greylisting_initial_delay", KindInteger, ReadWrite, EffectNow, nullptr,
        "The minutes a first attempt is deferred.",
        ROW_NUMBER(AntiSpam().GetGreyListingInitialDelay()), ROW_SET(AntiSpam().SetGreyListingInitialDelay((int) v.number)), ROW_NO_CHECK },
      { "greylisting_initial_delete", KindInteger, ReadWrite, EffectNow, nullptr,
        "The hours after which an unconfirmed triplet is forgotten.",
        ROW_NUMBER(AntiSpam().GetGreyListingInitialDelete()), ROW_SET(AntiSpam().SetGreyListingInitialDelete((int) v.number)), ROW_NO_CHECK },
      { "greylisting_final_delete", KindInteger, ReadWrite, EffectNow, nullptr,
        "The days after which a confirmed triplet is forgotten.",
        ROW_NUMBER(AntiSpam().GetGreyListingFinalDelete()), ROW_SET(AntiSpam().SetGreyListingFinalDelete((int) v.number)), ROW_NO_CHECK },
      { "bypass_greylisting_on_spf_success", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether a sender that passes SPF is not greylisted.",
        ROW_FLAG(AntiSpam().GetBypassGreyListingOnSPFSuccess()), ROW_SET(AntiSpam().SetBypassGreyListingOnSPFSuccess(v.flag)), ROW_NO_CHECK },
      { "bypass_greylisting_on_mail_from_mx", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether a message from one of the sender domain's MX hosts is not greylisted.",
        ROW_FLAG(AntiSpam().GetBypassGreyListingOnMailFromMX()), ROW_SET(AntiSpam().SetBypassGreyListingOnMailFromMX(v.flag)), ROW_NO_CHECK },
      { "maximum_message_size_kb", KindInteger, ReadWrite, EffectNow, nullptr,
        "Messages larger than this, in KB, are not scanned; 0 is no limit.",
        ROW_NUMBER(AntiSpam().GetAntiSpamMaxSizeKB()), ROW_SET(AntiSpam().SetAntiSpamMaxSizeKB((int) v.number)), ROW_NO_CHECK },
   };

   const Row LoggingRows[] =
   {
      { "enabled", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether anything is logged at all.",
        ROW_FLAG(Config()->GetUseLogging()), ROW_SET(Config()->SetUseLogging(v.flag)), ROW_NO_CHECK },
      { "log_application", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether the server's own events are logged.",
        ROW_FLAG(Config()->GetLogApplication()), ROW_SET(Config()->SetLogApplication(v.flag)), ROW_NO_CHECK },
      { "log_smtp", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether SMTP sessions are logged.",
        ROW_FLAG(Config()->GetLogSMTPConversations()), ROW_SET(Config()->SetLogSMTPConversations(v.flag)), ROW_NO_CHECK },
      { "log_pop3", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether POP3 sessions are logged.",
        ROW_FLAG(Config()->GetLogPOP3Conversations()), ROW_SET(Config()->SetLogPOP3Conversations(v.flag)), ROW_NO_CHECK },
      { "log_imap", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether IMAP sessions are logged.",
        ROW_FLAG(Config()->GetLogIMAPConversations()), ROW_SET(Config()->SetLogIMAPConversations(v.flag)), ROW_NO_CHECK },
      { "log_tcpip", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether connections and disconnections are logged.",
        ROW_FLAG(Config()->GetLogTCPIP()), ROW_SET(Config()->SetLogTCPIP(v.flag)), ROW_NO_CHECK },
      { "log_debug", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether debugging detail is logged.",
        ROW_FLAG(Config()->GetLogDebug()), ROW_SET(Config()->SetLogDebug(v.flag)), ROW_NO_CHECK },
      { "log_awstats", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether an AWStats log is written.",
        ROW_FLAG(Config()->GetAWStatsEnabled()), ROW_SET(Config()->SetAWStatsEnabled(v.flag)), ROW_NO_CHECK },
      { "keep_files_open", KindBoolean, ReadWrite, EffectNow, nullptr,
        "Whether the log files stay open between writes.",
        ROW_FLAG(Config()->GetKeepFilesOpen()), ROW_SET(Config()->SetKeepFilesOpen(v.flag)), ROW_NO_CHECK },
      { "device", KindEnum, ReadWrite, EffectNow, LogDeviceWords,
        "Where the log is written: file, or the sql database. unknown is the value an installation has until one is chosen, and behaves as file.",
        ROW_NUMBER(Config()->GetLogDevice()), ROW_SET(Config()->SetLogDevice(v.number)), ROW_NO_CHECK },
      { "log_format", KindEnum, ReadWrite, EffectNow, LogFormatWords,
        "How each line is rendered: the default, or the CSA format.",
        ROW_NUMBER(Config()->GetLogFormat()), ROW_SET(Config()->SetLogFormat(v.number)), ROW_NO_CHECK },

      // Facts about the log, from the same COM object, for a client that
      // wants to know where to look. Not settings, so not writable.
      { "directory", KindString, ReadOnly, EffectNow, nullptr,
        "The directory the log files are written to.",
        ROW_TEXT(Ini()->GetLogDirectory()), nullptr, ROW_NO_CHECK },
      { "current_default_log", KindString, ReadOnly, EffectNow, nullptr,
        "The full path of the current default log file.",
        ROW_TEXT(Logger::Instance()->GetCurrentLogFileName(Logger::Normal)), nullptr, ROW_NO_CHECK },
      { "current_error_log", KindString, ReadOnly, EffectNow, nullptr,
        "The full path of the current error log file.",
        ROW_TEXT(Logger::Instance()->GetCurrentLogFileName(Logger::Error)), nullptr, ROW_NO_CHECK },
      { "current_event_log", KindString, ReadOnly, EffectNow, nullptr,
        "The full path of the current event log file.",
        ROW_TEXT(Logger::Instance()->GetCurrentLogFileName(Logger::Events)), nullptr, ROW_NO_CHECK },
      { "current_awstats_log", KindString, ReadOnly, EffectNow, nullptr,
        "The full path of the current AWStats log file.",
        ROW_TEXT(Logger::Instance()->GetCurrentLogFileName(Logger::AWStats)), nullptr, ROW_NO_CHECK },
   };

#undef ROW_TEXT
#undef ROW_NUMBER
#undef ROW_FLAG
#undef ROW_SET
#undef ROW_NO_CHECK

   const Group ServerGroup = { "server", "/api/v1/settings", ServerRows, sizeof(ServerRows) / sizeof(ServerRows[0]) };
   const Group AntiSpamGroup = { "antispam", "/api/v1/settings/antispam", AntiSpamRows, sizeof(AntiSpamRows) / sizeof(AntiSpamRows[0]) };
   const Group LoggingGroup = { "logging", "/api/v1/settings/logging", LoggingRows, sizeof(LoggingRows) / sizeof(LoggingRows[0]) };

   // ---------------------------------------------------------------------
   // The two private helpers of RestApiServer these functions need, handed
   // in by the member functions that own the right to name them. Nothing
   // here duplicates an escape or a response line.
   // ---------------------------------------------------------------------

   struct Bridge
   {
      AnsiString (*escape)(const AnsiString &value);
      HttpResponse (*respond)(int statusCode, const AnsiString &body, const AnsiString &extraHeaders);
   };

   AnsiString Utf8(const String &value)
   {
      AnsiString utf8;
      Unicode::WideToMultiByte(value, utf8);
      return utf8;
   }

   const Row *FindRow(const Group &group, const std::string &key)
   {
      for (size_t i = 0; i < group.count; i++)
      {
         if (key == group.rows[i].key)
            return &group.rows[i];
      }
      return nullptr;
   }

   const char *WordFor(const Row &row, long value)
   {
      for (const Word *word = row.words; word && word->word; word++)
      {
         if (word->value == value)
            return word->word;
      }
      return nullptr;
   }

   AnsiString WordList(const Row &row)
   {
      AnsiString list;
      for (const Word *word = row.words; word && word->word; word++)
      {
         if (!list.IsEmpty())
            list += ", ";
         list += word->word;
      }
      return list;
   }

   // The three configuration objects a row may reach. Null only before
   // Configuration::Load has run, which is before this listener exists; the
   // guard costs nothing and turns a startup race into a 503 rather than a crash.
   bool ConfigurationLoaded()
   {
      return Smtp() && Imap() && Pop3();
   }

   // ---------------------------------------------------------------------
   // GET: the group as one flat object, in table order.
   // ---------------------------------------------------------------------

   AnsiString GroupJson(const Group &group, const Bridge &bridge)
   {
      AnsiString json = "{";
      bool first = true;

      for (size_t i = 0; i < group.count; i++)
      {
         const Row &row = group.rows[i];
         if (row.access == WriteOnly)
            continue;

         Value value = row.get();

         if (!first)
            json += ",";
         first = false;

         json += "\"";
         json += row.key;
         json += "\":";

         switch (row.kind)
         {
         case KindString:
            json += "\"" + bridge.escape(Utf8(value.text)) + "\"";
            break;
         case KindInteger:
            {
               AnsiString number;
               number.Format("%ld", value.number);
               json += number;
            }
            break;
         case KindBoolean:
            json += value.flag ? "true" : "false";
            break;
         case KindEnum:
            {
               // A stored value no word covers can only come from a
               // hand-edited row; null says so without inventing a word.
               const char *word = WordFor(row, value.number);
               if (word)
                  json += "\"" + AnsiString(word) + "\"";
               else
                  json += "null";
            }
            break;
         }
      }

      json += "}";
      return json;
   }

   // ---------------------------------------------------------------------
   // PUT.
   // ---------------------------------------------------------------------

   struct Change
   {
      const Row *row;
      Value value;
   };

   HttpResponse Refusal(const Bridge &bridge, int status, const AnsiString &sentence)
   {
      return bridge.respond(status, "{\"error\":\"" + bridge.escape(sentence) + "\"}", "");
   }

   // A refusal that names the caller's key: the key is the caller's own text,
   // escaped, and cut short so a hostile body cannot make the answer or the
   // log line arbitrarily long.
   AnsiString Named(const std::string &key, const char *rest)
   {
      AnsiString shown = AnsiString(key.substr(0, 100).c_str());
      if (key.size() > 100)
         shown += "...";
      return shown + " " + rest;
   }

   // The member's JSON value as the row's type, or the sentence that says why not.
   bool ReadValue(const Row &row, const std::string &key, const JsonValue &member, Value &value, AnsiString &problem)
   {
      switch (row.kind)
      {
      case KindString:
         if (!member.IsString())
         {
            problem = Named(key, "must be a string");
            return false;
         }
         if (!Unicode::MultiByteToWide(AnsiString(member.AsString().c_str()), value.text))
         {
            problem = Named(key, "is not valid UTF-8");
            return false;
         }
         return true;

      case KindInteger:
         {
            // A JSON number with a fraction is not an integer; one with an
            // exponent is, when it comes out whole. The range is the COM
            // property's, a 32-bit long, whatever the platform's long is.
            if (!member.IsNumber())
            {
               problem = Named(key, "must be an integer");
               return false;
            }
            __int64 whole = member.AsInt64();
            if ((double) whole != member.AsNumber())
            {
               problem = Named(key, "must be an integer");
               return false;
            }
            if (whole < INT_MIN || whole > INT_MAX)
            {
               problem = Named(key, "is out of range");
               return false;
            }
            value.number = (long) whole;
            return true;
         }

      case KindBoolean:
         if (!member.IsBool())
         {
            problem = Named(key, "must be true or false");
            return false;
         }
         value.flag = member.AsBool();
         return true;

      case KindEnum:
         {
            if (member.IsString())
            {
               for (const Word *word = row.words; word && word->word; word++)
               {
                  if (member.AsString() == word->word)
                  {
                     value.number = word->value;
                     return true;
                  }
               }
            }
            problem = Named(key, "must be one of ") + WordList(row);
            return false;
         }
      }

      problem = Named(key, "has an unknown kind");
      return false;
   }

   HttpResponse GroupPut(const Group &group, const AnsiString &requestBody, const Bridge &bridge)
   {
      if (!ConfigurationLoaded())
         return Refusal(bridge, 503, "the server's configuration is not loaded");

      JsonValue document;
      std::string parseError;
      if (!JsonValue::Parse(std::string(requestBody.c_str(), requestBody.GetLength()), document, parseError) || !document.IsObject())
         return Refusal(bridge, 400, "the body must be a JSON object");

      // Resolve everything first: every key to its row, every value to its
      // type, every value through its row's own check. The first problem
      // is the answer, and nothing has been applied when it is given.
      std::vector<Change> changes;

      const std::vector<std::pair<std::string, JsonValue> > &members = document.Members();
      for (size_t i = 0; i < members.size(); i++)
      {
         const std::string &key = members[i].first;

         const Row *row = FindRow(group, key);
         if (!row)
            return Refusal(bridge, 400, Named(key, "is not a setting in this group"));

         if (row->access == ReadOnly)
            return Refusal(bridge, 400, Named(key, "is read-only"));

         Change change;
         change.row = row;

         AnsiString problem;
         if (!ReadValue(*row, key, members[i].second, change.value, problem))
            return Refusal(bridge, 400, problem);

         changes.push_back(change);
      }

      for (size_t i = 0; i < changes.size(); i++)
      {
         if (!changes[i].row->check)
            continue;

         String refusal = changes[i].row->check(changes[i].value);
         if (!refusal.IsEmpty())
         {
            LOG_APPLICATION("RestApi: Refused to change " + String(group.name) + " setting " + String(changes[i].row->key) + ": " + refusal);
            return Refusal(bridge, 400, Utf8(refusal));
         }
      }

      // Apply, the rows with a check first: those are the ones whose setter
      // can still say no (a delimiter whose rewrite failed, an INI file that
      // could not be written), and a no from them must leave the rest untouched.
      String applied;

      for (int pass = 0; pass < 2; pass++)
      {
         for (size_t i = 0; i < changes.size(); i++)
         {
            const Row &row = *changes[i].row;
            bool checked = row.check != nullptr;
            if ((pass == 0) != checked)
               continue;

            Outcome outcome = row.set(changes[i].value);
            if (outcome.status != 0)
            {
               LOG_APPLICATION("RestApi: Failed to change " + String(group.name) + " setting " + String(row.key) + ": " + outcome.message);
               return Refusal(bridge, outcome.status, Utf8(outcome.message));
            }

            if (!applied.IsEmpty())
               applied += ", ";
            applied += row.key;
         }
      }

      // The keys and never the values: a value may be the relayer password.
      if (!applied.IsEmpty())
         LOG_APPLICATION("RestApi: Changed " + String(group.name) + " settings: " + applied + ".");

      return bridge.respond(200, GroupJson(group, bridge), "");
   }

   HttpResponse GroupGet(const Group &group, const Bridge &bridge)
   {
      if (!ConfigurationLoaded())
         return Refusal(bridge, 503, "the server's configuration is not loaded");

      return bridge.respond(200, GroupJson(group, bridge), "");
   }

   // ---------------------------------------------------------------------
   // The OpenAPI paths, from the same tables.
   // ---------------------------------------------------------------------

   AnsiString OpenApiProperties(const Group &group, const Bridge &bridge)
   {
      AnsiString properties;

      for (size_t i = 0; i < group.count; i++)
      {
         const Row &row = group.rows[i];

         if (!properties.IsEmpty())
            properties += ",";

         AnsiString type;
         switch (row.kind)
         {
         case KindString:
         case KindEnum:
            type = "string";
            break;
         case KindInteger:
            type = "integer";
            break;
         case KindBoolean:
            type = "boolean";
            break;
         }

         AnsiString description = row.description;
         switch (row.effect)
         {
         case EffectNow:
            break;
         case EffectRestart:
            description += " Takes effect when the server restarts.";
            break;
         case EffectStored:
            description += " Stored for the Control Panel; nothing in this server reads it.";
            break;
         }

         properties += "\"";
         properties += row.key;
         properties += "\":{\"type\":\"" + type + "\",\"description\":\"" + bridge.escape(description) + "\"";

         if (row.kind == KindEnum)
         {
            properties += ",\"enum\":[";
            for (const Word *word = row.words; word && word->word; word++)
            {
               if (word != row.words)
                  properties += ",";
               properties += "\"";
               properties += word->word;
               properties += "\"";
            }
            properties += "]";
         }

         if (row.access == ReadOnly)
            properties += ",\"readOnly\":true";
         if (row.access == WriteOnly)
            properties += ",\"writeOnly\":true";

         properties += "}";
      }

      return properties;
   }

   AnsiString OpenApiPath(const Group &group, const char *getSummary, const char *putSummary, const Bridge &bridge)
   {
      AnsiString path = ",\"";
      path += group.path;
      path += "\":{";

      path += "\"get\":{\"summary\":\"";
      path += getSummary;
      path += "\",\"description\":\"Every key the PUT lists, as the server holds it now; the write-only ones are left out. Server-wide; refused for domain-restricted keys.\","
              "\"responses\":{\"200\":{\"description\":\"The group as one flat object\"}}},";

      path += "\"put\":{\"summary\":\"";
      path += putSummary;
      path += "\",\"description\":\"Body: any subset of the writable keys below, applied through the setters the Control Panel uses. "
              "Everything is checked before anything is applied: an unknown key, a value of the wrong type, a read-only key, or a value the setting refuses is a 400 whose error names the key or carries the setting's own sentence, and nothing changes. "
              "A change takes effect at once unless the key's description says it waits for a restart; the response always shows the stored value. "
              "Answers the whole group as GET emits it. Server-wide; refused for domain-restricted and read-only keys.\","
              "\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"properties\":{";
      path += OpenApiProperties(group, bridge);
      path += "}}}}},"
              "\"responses\":{\"200\":{\"description\":\"The whole group after the change\"},"
              "\"400\":{\"description\":\"Refused, and nothing applied: error names the key or carries the setting's own sentence\"},"
              "\"500\":{\"description\":\"A value was accepted and could not be written\"}}}}";

      return path;
   }
}

namespace HM
{
   // The server-wide group for GET /api/v1/settings, whose handler lives in
   // RestApiServer.cpp and declares this function where it calls it.
   AnsiString
   RestApiSettingsServerGroupJson(AnsiString (*escape)(const AnsiString &))
   {
      Bridge bridge = { escape, nullptr };

      if (!ConfigurationLoaded())
         return "{}";

      return GroupJson(ServerGroup, bridge);
   }

   AnsiString
   RestApiServer::OpenApiSettingsPaths_()
   {
      Bridge bridge = { &RestApiServer::JsonEscape_, &RestApiServer::BuildResponse_ };

      AnsiString paths;
      paths += OpenApiPath(ServerGroup, "The server-wide settings", "Change server-wide settings", bridge);
      paths += OpenApiPath(AntiSpamGroup, "The anti-spam settings", "Change anti-spam settings", bridge);
      paths += OpenApiPath(LoggingGroup, "The logging settings", "Change logging settings", bridge);
      return paths;
   }

   HttpResponse
   RestApiServer::HandleSettingsPut_(const AnsiString &requestBody)
   {
      Bridge bridge = { &RestApiServer::JsonEscape_, &RestApiServer::BuildResponse_ };
      return GroupPut(ServerGroup, requestBody, bridge);
   }

   HttpResponse
   RestApiServer::HandleSettingsAntiSpam_()
   {
      Bridge bridge = { &RestApiServer::JsonEscape_, &RestApiServer::BuildResponse_ };
      return GroupGet(AntiSpamGroup, bridge);
   }

   HttpResponse
   RestApiServer::HandleSettingsAntiSpamPut_(const AnsiString &requestBody)
   {
      Bridge bridge = { &RestApiServer::JsonEscape_, &RestApiServer::BuildResponse_ };
      return GroupPut(AntiSpamGroup, requestBody, bridge);
   }

   HttpResponse
   RestApiServer::HandleSettingsLogging_()
   {
      Bridge bridge = { &RestApiServer::JsonEscape_, &RestApiServer::BuildResponse_ };
      return GroupGet(LoggingGroup, bridge);
   }

   HttpResponse
   RestApiServer::HandleSettingsLoggingPut_(const AnsiString &requestBody)
   {
      Bridge bridge = { &RestApiServer::JsonEscape_, &RestApiServer::BuildResponse_ };
      return GroupPut(LoggingGroup, requestBody, bridge);
   }
}
