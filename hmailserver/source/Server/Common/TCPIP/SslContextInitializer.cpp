// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "SslContextInitializer.h"

#include "../BO/SSLCertificate.h"
#include "../Util/Encoding/Base64.h"
#include "../Util/Unicode.h"
#include "../Util/Utilities.h"

#include <openssl/core_names.h>
#include <openssl/evp.h>
#include <openssl/params.h>
#include <openssl/rand.h>
#include <openssl/x509.h>

#include <cstring>
#include <mutex>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   const int CertificateAlreadyInStore = 185057381;

   namespace
   {
      // An ASN1_TIME as "YYYY-MM-DD HH:MM:SS UTC", for the error message that names
      // the date a certificate expired. Kept file-local rather than added to the
      // class so the header does not have to expose OpenSSL types.
      AnsiString FormatCertificateTime(const ASN1_TIME *asn1Time)
      {
         tm parsed = {};

         if (asn1Time == nullptr || ASN1_TIME_to_tm(asn1Time, &parsed) != 1)
            return AnsiString("an unreadable date");

         AnsiString formatted;
         formatted.Format("%04d-%02d-%02d %02d:%02d:%02d UTC",
            parsed.tm_year + 1900, parsed.tm_mon + 1, parsed.tm_mday,
            parsed.tm_hour, parsed.tm_min, parsed.tm_sec);

         return formatted;
      }

      // The TLS 1.3 suite names in `configured` that did not end up in the context,
      // comma separated; empty when OpenSSL took all of them. File-local for the same
      // reason FormatCertificateTime is - the header does not have to expose OpenSSL
      // types.
      //
      // Worth the trouble because OpenSSL's ciphersuite parser skips an element it does
      // not recognise and carries on, returning success. A list with one typo in it is
      // applied minus that entry and nothing says so, which quietly halves the suites
      // this server offers. Suites that parsed but whose algorithm this build has
      // disabled are dropped too and land here as well, which is the same answer an
      // administrator needs.
      AnsiString FindUnappliedCipherSuites(SSL_CTX *ssl, const AnsiString &configured)
      {
         std::set<AnsiString> applied;

         STACK_OF(SSL_CIPHER) *ciphers = SSL_CTX_get_ciphers(ssl);

         // sk_SSL_CIPHER_num(nullptr) is -1, so a null stack simply skips the loop.
         for (int i = 0; i < sk_SSL_CIPHER_num(ciphers); i++)
         {
            const char *standardName = SSL_CIPHER_standard_name(sk_SSL_CIPHER_value(ciphers, i));

            if (standardName != nullptr)
               applied.insert(AnsiString(standardName));
         }

         AnsiString unapplied;

         for (AnsiString name : StringParser::SplitString(configured, ":"))
         {
            if (name.IsEmpty() || applied.find(name) != applied.end())
               continue;

            if (!unapplied.IsEmpty())
               unapplied += ", ";

            unapplied += name;
         }

         return unapplied;
      }

      // --- TLS session-ticket key rotation -------------------------------------
      //
      // OpenSSL's default is one ticket key per SSL_CTX, generated when the context
      // is created and kept for the life of the process. Every session ticket this
      // server ever hands out is encrypted under that one key, so a passive attacker
      // who records traffic and later recovers the key - a memory disclosure, a core
      // dump, a hibernation file - can decrypt every recorded ticket and with it the
      // resumption secrets inside. That is the forward-secrecy hole rotation closes:
      // with rotation on, a captured ticket is decryptable for at most two rotation
      // intervals (one as the current key, one as the previous), after which the key
      // material it was sealed with no longer exists anywhere.
      //
      // The store is process-global rather than per-context on purpose. All listeners
      // rotate together, and a ticket issued by one listener is *cryptographically*
      // readable by another - but cross-listener resumption is still refused, because
      // each listener gets its own session-ID context (see SetSessionResumption_) and
      // OpenSSL compares that after decrypting the ticket. Keys are generated lazily,
      // on the first ticket operation, so process start-up never depends on the RNG.
      //
      // File-local for the same reason FormatCertificateTime is: the header does not
      // have to expose OpenSSL types.

      struct TicketKey
      {
         unsigned char name[16] = {};
         unsigned char aes[32] = {};
         unsigned char hmac[32] = {};
         time_t created = 0;
         bool valid = false;
      };

      struct TicketKeyStore
      {
         std::mutex mutex;
         TicketKey current;
         TicketKey previous;
         int rotation_interval_seconds = 0;
         bool generation_failure_reported = false;
      };

      // Function-local static so initialization order against other globals can
      // never matter.
      TicketKeyStore& GetTicketKeyStore()
      {
         static TicketKeyStore store;
         return store;
      }

      bool GenerateTicketKey(TicketKey &key, time_t now)
      {
         if (RAND_bytes(key.name, sizeof(key.name)) != 1 ||
             RAND_bytes(key.aes, sizeof(key.aes)) != 1 ||
             RAND_bytes(key.hmac, sizeof(key.hmac)) != 1)
         {
            key.valid = false;
            return false;
         }

         key.created = now;
         key.valid = true;
         return true;
      }

      // The OpenSSL ticket-key callback (SSL_CTX_set_tlsext_ticket_key_evp_cb form),
      // called on every ticket issued and on every ticket presented, for TLS 1.2
      // RFC 5077 tickets and TLS 1.3 NewSessionTicket alike.
      //
      // Every failure path in here returns 0, never -1. For encryption, 0 means "no
      // ticket for this session" and the handshake completes without one; for
      // decryption, 0 means "not my ticket" and the client simply does a full
      // handshake. Returning -1 would abort the handshake, which would let a broken
      // RNG - or a garbage ticket from a hostile client - take a connection down
      // instead of merely costing it resumption. Losing a ticket is always
      // acceptable; losing the connection is not.
      int TicketKeyCallback(SSL* /*ssl*/, unsigned char *key_name, unsigned char *iv,
         EVP_CIPHER_CTX *cipherContext, EVP_MAC_CTX *macContext, int encrypt)
      {
         TicketKeyStore &store = GetTicketKeyStore();

         TicketKey key;
         bool renewTicket = false;
         bool reportGenerationFailure = false;

         {
            std::lock_guard<std::mutex> guard(store.mutex);

            if (encrypt)
            {
               time_t now = time(nullptr);

               // Rotation is lazy: checked whenever a ticket is about to be issued.
               // A timer would rotate on schedule even when idle, but an idle server
               // issues no tickets, so lazy rotation gives the identical guarantee -
               // no ticket is ever sealed under a key older than the interval -
               // without a thread to own, start and stop.
               if (!store.current.valid ||
                   (store.rotation_interval_seconds > 0 &&
                    now - store.current.created >= store.rotation_interval_seconds))
               {
                  TicketKey fresh;
                  if (!GenerateTicketKey(fresh, now))
                  {
                     // Report once per process rather than once per handshake: if the
                     // RNG is broken it is broken for every connection, and a report
                     // per handshake would bury the log.
                     if (!store.generation_failure_reported)
                     {
                        store.generation_failure_reported = true;
                        reportGenerationFailure = true;
                     }
                  }
                  else
                  {
                     store.previous = store.current;
                     store.current = fresh;
                  }
               }

               key = store.current;
            }
            else
            {
               if (store.current.valid &&
                   memcmp(key_name, store.current.name, sizeof(store.current.name)) == 0)
               {
                  key = store.current;
               }
               else if (store.previous.valid &&
                        memcmp(key_name, store.previous.name, sizeof(store.previous.name)) == 0)
               {
                  // Sealed under the previous key: still accepted, but returning 2
                  // below makes OpenSSL issue a replacement ticket under the current
                  // key, so a busy client's ticket keeps rolling forward and never
                  // ages past the two-interval window.
                  key = store.previous;
                  renewTicket = true;
               }
               else
               {
                  // A key this process does not hold - a ticket from before a restart,
                  // or older than two intervals. Full handshake.
                  return 0;
               }
            }
         }

         if (reportGenerationFailure)
         {
            ErrorManager::Instance()->ReportError(ErrorManager::High, 6172, "SslContextInitializer::TicketKeyCallback",
               "Failed to generate a TLS session-ticket key (the OpenSSL random number generator reported an error). Session tickets will not be issued until it recovers; connections and full handshakes are unaffected.");
         }

         if (!key.valid)
            return 0;

         if (encrypt)
         {
            if (RAND_bytes(iv, EVP_MAX_IV_LENGTH) != 1)
               return 0;

            if (EVP_EncryptInit_ex(cipherContext, EVP_aes_256_cbc(), nullptr, key.aes, iv) != 1)
               return 0;

            memcpy(key_name, key.name, sizeof(key.name));
         }
         else
         {
            if (EVP_DecryptInit_ex(cipherContext, EVP_aes_256_cbc(), nullptr, key.aes, iv) != 1)
               return 0;
         }

         // OSSL_PARAM_construct_utf8_string wants a mutable buffer; a local array
         // keeps us clear of casting away const from a string literal.
         char digestName[] = "SHA256";

         OSSL_PARAM params[3];
         params[0] = OSSL_PARAM_construct_octet_string(OSSL_MAC_PARAM_KEY, key.hmac, sizeof(key.hmac));
         params[1] = OSSL_PARAM_construct_utf8_string(OSSL_MAC_PARAM_DIGEST, digestName, 0);
         params[2] = OSSL_PARAM_construct_end();

         if (EVP_MAC_CTX_set_params(macContext, params) != 1)
            return 0;

         return encrypt ? 1 : (renewTicket ? 2 : 1);
      }
   }

   bool
   SslContextInitializer::InitServer(boost::asio::ssl::context& context, std::shared_ptr<SSLCertificate> certificate, String ip_address, int port)
   {  
      if (!certificate)
      {
         String errorMessage = Formatter::Format("Error initializing SSL. Certificate not set. Address: {0}, Port: {1}", ip_address, port);
         ErrorManager::Instance()->ReportError(ErrorManager::High, 5113, "SslContextInitializer::InitServer", errorMessage);
         return false;
      }

      SetContextOptions_(context);
      SetKeyExchangeGroups_(context);

      SetCipherList_(context);
      SetTls13CipherSuites_(context);
      SetSessionResumption_(context, ip_address, port);

      try
      {
         String bin_directory = Utilities::GetBinDirectory();
         String dh2048_file = FileUtilities::Combine(bin_directory, "dh2048.pem");

         if (FileUtilities::Exists(dh2048_file))
         {
            context.use_tmp_dh_file(AnsiString(dh2048_file));
         }
         else
         {
            ErrorManager::Instance()->ReportError(ErrorManager::Critical, 5603, "SslContextInitializer::InitServer", Formatter::Format("Unable to enable Diffie - Hellman key agreement.The required file {0} does not exist.", dh2048_file));
         }
      }
      catch (boost::system::system_error ec)
      {
         String asioError = ec.what();

         String errorMessage;
         errorMessage.Format(_T("Failed to set SSL context options. Address: %s, Port: %i, Error: %s"), 
            String(ip_address).c_str(), port, asioError.c_str());

         ErrorManager::Instance()->ReportError(ErrorManager::High, 5113, "SslContextInitializer::InitServer", errorMessage);

         return false;

      }

      AnsiString certificateFile = certificate->GetCertificateFile();
      AnsiString privateKeyFile = certificate->GetPrivateKeyFile();


      try
      {
         context.use_certificate_file(certificateFile, boost::asio::ssl::context::pem);
      }
      catch (boost::system::system_error ec)
      {
         String asioError = ec.what();

         String errorMessage;
         errorMessage.Format(_T("Failed to load certificate file. Path: %s, Address: %s, Port: %i, Error: %s"), 
            String(certificateFile).c_str(), ip_address.c_str(), port, asioError.c_str());

         ErrorManager::Instance()->ReportError(ErrorManager::High, 5113, "SslContextInitializer::InitServer", errorMessage);

         return false;
      }

      try
      {
         context.use_certificate_chain_file(certificateFile);
      }
      catch (boost::system::system_error ec)
      {
         String asioError = ec.what();

         String errorMessage;
         errorMessage.Format(_T("Failed to load certificate chain from certificate file. Path: %s, Address: %s, Port: %i, Error: %s"), 
            String(certificateFile), ip_address.c_str(), port, asioError.c_str());

         ErrorManager::Instance()->ReportError(ErrorManager::High, 5113, "SslContextInitializer::InitServer", errorMessage);

         return false;
      }

      try
      {
         // The callback captures the certificate so that each listener hands OpenSSL
         // the passphrase configured for *its* key - different certificates can have
         // different passphrases. OpenSSL only calls it when the key file is actually
         // encrypted, so the overwhelmingly common unencrypted key never runs a line
         // of it and loads exactly as before.
         std::shared_ptr<SSLCertificate> passphrase_source = certificate;
         context.set_password_callback(
            [passphrase_source](std::size_t /*max_length*/, boost::asio::ssl::context_base::password_purpose /*purpose*/)
            {
               return GetPassword_(passphrase_source);
            });
         context.use_private_key_file(privateKeyFile, boost::asio::ssl::context::pem);
      }
      catch (boost::system::system_error ec)
      {
         String asioError = ec.what();

         String errorMessage;
         errorMessage.Format(_T("Failed to load private key file. Path: %s, Address: %s, Port: %i, Error: %s"), 
            String(privateKeyFile), ip_address.c_str(), port, asioError.c_str());

         ErrorManager::Instance()->ReportError(ErrorManager::High, 5113, "SslContextInitializer::InitServer", errorMessage);

         return false;
      }
      catch (...)
      {
         String errorMessage = "Error initializing SSL";
         ErrorManager::Instance()->ReportError(ErrorManager::High, 5113, "SslContextInitializer::InitServer", errorMessage);
         return false;
      }

      // Everything above establishes that OpenSSL could *load* the pair. It says
      // nothing about whether the certificate is inside its validity period:
      // OpenSSL does not check the dates on a certificate it is asked to serve,
      // only on one it is asked to verify. So an expired certificate loads without
      // complaint, the listener binds, TLS is offered - and every client that
      // checks the date closes the connection. The server side of that has no
      // symptom at all: no error, no application-log line, and a handshake failure
      // logged (if TCP/IP logging is even on) as an ordinary aborted session.
      //
      // Reported and not refused, deliberately. Refusing would mean that at the
      // moment a certificate expires the mail ports stop listening, which turns a
      // degraded server into an unreachable one and would be a far worse surprise
      // than the one being fixed. The DH-parameter case above sets the same
      // precedent: report loudly, carry on.
      ReportCertificateValidityProblem_(context, String(certificateFile), ip_address, port);

      return true;
   }

   bool
   SslContextInitializer::InitClient(boost::asio::ssl::context& context)
   {
      SetContextOptions_(context);

      // The key-exchange group list belongs on the client context as much as on a
      // server one, and it was only ever applied to server contexts. So
      // [Settings] TlsKeyExchangeGroups reached every inbound listener and reached
      // nothing outbound: hMailServer delivering mail to another server offered
      // OpenSSL's default groups, which is neither what the setting says nor what
      // the post-quantum release note claimed. The same list is correct in both
      // directions - it is a preference order that the peer negotiates against,
      // and the default list still offers X25519 and the NIST curves after the
      // hybrids - and the fallback inside SetKeyExchangeGroups_ is what stops a
      // mistyped list from taking outbound delivery down.
      SetKeyExchangeGroups_(context);

      SetCipherList_(context);
      SetTls13CipherSuites_(context);

      return true;
   }

   void
   SslContextInitializer::ReportCertificateValidityProblem_(boost::asio::ssl::context& context, const String &certificate_file, const String &ip_address, int port)
   {
      X509 *certificate = SSL_CTX_get0_certificate(context.native_handle());

      if (certificate == nullptr)
         return;

      const ASN1_TIME *notBefore = X509_get0_notBefore(certificate);
      const ASN1_TIME *notAfter = X509_get0_notAfter(certificate);

      // ASN1_TIME_cmp_time_t compares the certificate time against a time_t and
      // returns -1 when it is earlier, 0 when equal, 1 when later, and -2 when the
      // value could not be parsed. Both tests below are therefore written against
      // the exact value rather than a sign: -2 is negative, so "< 0" would report
      // an unparseable notAfter as an expired certificate, which is the one wrong
      // answer that matters here - it would tell an administrator to replace a
      // certificate that is fine.
      //
      // X509_cmp_current_time is the obvious call and was what this used, but
      // OpenSSL 4.0 deprecates the whole X509_cmp_* family and this project builds
      // with /WX. ASN1_TIME_to_tm is already the idiom in AcmeClient and
      // MetricsServer for reading the same field.
      const time_t now = time(nullptr);

      if (ASN1_TIME_cmp_time_t(notAfter, now) == -1)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 5991, "SslContextInitializer::InitServer",
            Formatter::Format("The TLS certificate {0} expired on {1}. The listener on {2}:{3} has started and is offering it, and every client that checks the expiry date will refuse to connect. Replace the certificate.",
               certificate_file, String(FormatCertificateTime(notAfter)), ip_address, port));
      }
      else if (ASN1_TIME_cmp_time_t(notBefore, now) == 1)
      {
         // The other half of the same defect, and the one that catches a clock
         // problem rather than a neglected renewal: a certificate issued for a
         // future date, or a server whose clock is behind, is rejected by clients
         // exactly as an expired one is.
         ErrorManager::Instance()->ReportError(ErrorManager::High, 5991, "SslContextInitializer::InitServer",
            Formatter::Format("The TLS certificate {0} is not valid until {1}. The listener on {2}:{3} has started and is offering it, and clients will refuse to connect until then. Check the certificate and this server's clock.",
               certificate_file, String(FormatCertificateTime(notBefore)), ip_address, port));
      }
   }

   std::string
   SslContextInitializer::GetPassword_(std::shared_ptr<SSLCertificate> certificate)
   {
      // Reaching this function at all means OpenSSL found the private key file to
      // be encrypted. Until database version 6009 that was a dead end (error 5143,
      // "hMailServer does not support this"); now the passphrase can be configured
      // per certificate, because different certificates can have different
      // passphrases and a single global one would force them all to match.
      //
      // What the stored passphrase is worth to an attacker: at rest it is protected
      // the same way the other reversible secrets in this database are (see
      // PersistentSSLCertificate::SaveObject), which with the default
      // ProtectStoredSecretsWithDPAPI=1 means a machine-bound DPAPI envelope. An
      // attacker who steals the database, a backup, or hMailServer.ini therefore
      // gets a blob that is useless off this machine - and, notably, gets less than
      // they already had, because the private key FILE the passphrase unlocks sits
      // on this machine's disk too. An attacker who can run code on this machine
      // can decrypt it, exactly as they could read an unencrypted key file today;
      // an encrypted key plus a stored passphrase is protection for the key file
      // when it travels (copied, backed up, committed somewhere by mistake), not
      // against a compromise of the running server.
      String configuredPassphrase = certificate ? certificate->GetPrivateKeyPassword() : String();

      if (configuredPassphrase.IsEmpty())
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 6170, "SslContextInitializer::GetPassword_",
            Formatter::Format("The private key file {0} is encrypted (it has a passphrase), and no passphrase is configured for the certificate. Set the PrivateKeyPassword property on the SSL certificate (COM: SSLCertificate.PrivateKeyPassword), or use an unencrypted key. The key was not loaded.",
               certificate ? certificate->GetPrivateKeyFile() : String()));
         return "";
      }

      // OpenSSL sees bytes, not characters, so a non-ASCII passphrase must be
      // handed over in one well-defined encoding. UTF-8, matching what Crypt uses
      // when it protects the value at rest, so the bytes OpenSSL gets are the bytes
      // the administrator typed regardless of the console or COM client involved.
      AnsiString utf8Passphrase;
      Unicode::WideToMultiByte(configuredPassphrase, utf8Passphrase);

      return std::string(utf8Passphrase);
   }

   void
   SslContextInitializer::SetSessionResumption_(boost::asio::ssl::context& context, const String &ip_address, int port)
   {
      SSL_CTX* ssl = context.native_handle();

      // (1) An explicit session-ID context, always. This is the one call here that
      // is not behind a setting, because leaving it unset is not a behaviour anyone
      // can want: OpenSSL refuses to resume a session into a context whose
      // session-ID context differs from the one the session was created under, and
      // a server that verifies client certificates (per-port policy, database
      // version 6008) but never set a session-ID context fails resumed handshakes
      // outright with "session id context uninitialized".
      //
      // It is derived from the listener's address and port, not shared, so that a
      // session established on one listener can never be resumed on another. That
      // matters because resumption skips certificate verification: a session from a
      // port with no client-certificate requirement, resumed on a port that
      // requires one, would walk straight past the requirement. Distinct session-ID
      // contexts are the mechanism OpenSSL provides to stop exactly that, and they
      // also keep the process-global ticket keys (below) from softening the same
      // boundary. Hashed to fit: the raw string can exceed the 32-byte
      // SSL_MAX_SSL_SESSION_ID_LENGTH limit for an IPv6 listener, and a SHA-256
      // digest is exactly 32 bytes.
      AnsiString sessionIdSource;
      sessionIdSource.Format("hMailServer;%s;%d", AnsiString(ip_address).c_str(), port);

      unsigned char sessionIdContext[EVP_MAX_MD_SIZE];
      unsigned int sessionIdContextLength = 0;

      if (EVP_Digest(sessionIdSource.c_str(), static_cast<size_t>(sessionIdSource.GetLength()), sessionIdContext, &sessionIdContextLength, EVP_sha256(), nullptr) == 1)
      {
         SSL_CTX_set_session_id_context(ssl, sessionIdContext, sessionIdContextLength);
      }

      // Everything below follows the house rule for OpenSSL configuration: the
      // default value of every setting means "make no call at all", so a default
      // configuration keeps OpenSSL's stock behaviour bit for bit, and no
      // empty-or-zero value is ever passed through to a call that would accept it
      // and quietly break TLS.

      // (2) Session cache size. Defends against cache exhaustion: the server-side
      // session-ID cache costs memory per cached session, and a client that churns
      // handshakes grows it. OpenSSL caps it at 20480 sessions by default; a
      // positive value replaces that cap, a negative value turns the server-side
      // cache off entirely (session-ID resumption stops; tickets, which cost the
      // server no memory, are unaffected), and 0 - the default - leaves OpenSSL
      // alone.
      int cacheSize = IniFileSettings::Instance()->GetTlsSessionCacheSize();

      if (cacheSize > 0)
         SSL_CTX_sess_set_cache_size(ssl, cacheSize);
      else if (cacheSize < 0)
         SSL_CTX_set_session_cache_mode(ssl, SSL_SESS_CACHE_OFF);

      // (3) Session lifetime, in seconds, for both cached sessions and tickets.
      // Defends against a stolen resumption secret staying useful: however a
      // session leaks (a ticket recorded off the wire, a session cache read out of
      // a core dump), it stops being resumable when it expires. 0 - the default -
      // leaves OpenSSL's per-protocol default (300 seconds) alone; values below
      // zero are meaningless and are skipped rather than handed to OpenSSL.
      int sessionTimeoutSeconds = IniFileSettings::Instance()->GetTlsSessionTimeoutSeconds();

      if (sessionTimeoutSeconds > 0)
         SSL_CTX_set_timeout(ssl, sessionTimeoutSeconds);

      if (!IniFileSettings::Instance()->GetTlsSessionTicketsEnabled())
      {
         // (4) Tickets off entirely, for an administrator whose policy is that
         // nothing derived from a long-lived key ever goes on the wire. Two calls
         // because OpenSSL treats the versions differently: SSL_OP_NO_TICKET stops
         // RFC 5077 tickets on TLS 1.2 and below (those clients fall back to
         // session-ID resumption, which stays inside this process), but on TLS 1.3
         // it only switches to "stateful" tickets - still a NewSessionTicket
         // message, just an opaque cache handle rather than encrypted state.
         // SSL_CTX_set_num_tickets(0) is what actually stops TLS 1.3 tickets being
         // sent. Costs only resumption efficiency; every client can still connect.
         SSL_CTX_set_options(ssl, SSL_OP_NO_TICKET);
         SSL_CTX_set_num_tickets(ssl, 0);
      }
      else
      {
         // (5) Ticket-key rotation. Defends forward secrecy: OpenSSL's default
         // ticket key is generated once per context and lives as long as the
         // process, so on a mail server - a process that runs for months - a
         // recorded ticket stays decryptable for months if that key is ever
         // recovered. With an interval set, a captured ticket is useless after at
         // most two intervals (see the TicketKeyCallback comment). 0 - the default
         // - installs nothing and keeps OpenSSL's per-context key, i.e. exactly
         // today's behaviour.
         int rotationSeconds = IniFileSettings::Instance()->GetTlsTicketKeyRotationSeconds();

         if (rotationSeconds > 0)
         {
            {
               TicketKeyStore &store = GetTicketKeyStore();
               std::lock_guard<std::mutex> guard(store.mutex);
               store.rotation_interval_seconds = rotationSeconds;
            }

            if (SSL_CTX_set_tlsext_ticket_key_evp_cb(ssl, TicketKeyCallback) != 1)
            {
               // The callback was not installed, so this context keeps OpenSSL's
               // default per-context key. Tickets and resumption keep working -
               // what is missing is the rotation the administrator asked for, so
               // say so and carry on rather than degrade TLS.
               ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6171, "SslContextInitializer::SetSessionResumption_",
                  Formatter::Format("Failed to enable TLS session-ticket key rotation for the listener on {0}:{1}. Message: {2}. Tickets remain enabled with OpenSSL's default non-rotating key.",
                     ip_address, port, String(GetOpenSslError_())));
            }
         }
      }
   }

   void
   SslContextInitializer::SetCipherList_(boost::asio::ssl::context& context)
   {
      AnsiString cipher_list = Configuration::Instance()->GetSslCipherList();

      cipher_list.Replace("\r", "");
      cipher_list.Replace("\n", "");
      cipher_list.Replace(" ", "");

      if (cipher_list.Trim().IsEmpty())
         return;

      // "AEAD-ONLY" (case-insensitive) is a named preset, not an OpenSSL cipher
      // string. An administrator could always reach this policy by hand-editing
      // SslCipherList, but the raw string is easy to get subtly wrong - one typo'd
      // suite name is silently skipped, and nothing says so - so the vetted list
      // ships under a name instead.
      //
      // What it means: for TLS 1.2, only AEAD ciphers (AES-GCM and
      // ChaCha20-Poly1305) with forward-secret key exchange (ECDHE or DHE). What it
      // excludes: every CBC-mode construction and with them every HMAC-SHA1 and
      // CBC-HMAC-SHA2 suite (the padding-oracle family: Lucky13 and its
      // descendants), and static-RSA key exchange (no forward secrecy). What it
      // costs: clients that cannot do TLS 1.2 with an AEAD suite cannot connect at
      // all - GCM and ChaCha20 do not exist below TLS 1.2, so enabling TLS 1.0/1.1
      // alongside this preset leaves those protocols with no usable cipher, and
      // TLS 1.2 stacks from roughly the Windows 7 / Java 6 era that only speak CBC
      // suites are shut out. TLS 1.3 needs no help from the preset: its suites are
      // AEAD by construction, and its separate list (TlsCipherSuites13, handled in
      // SetTls13CipherSuites_) is not touched here.
      //
      // A misspelled preset name ("AEAD_ONLY", "AeadOnly2", ...) is not silently
      // ignored: it flows through to SSL_CTX_set_cipher_list below, which rejects a
      // list containing no known cipher, and the 5511 report names the string. The
      // shipped default SslCipherList is unchanged - the preset only takes effect
      // when an administrator sets the setting to exactly this name.
      if (cipher_list.CompareNoCase("AEAD-ONLY") == 0)
      {
         cipher_list =
            "ECDHE-ECDSA-AES256-GCM-SHA384:"
            "ECDHE-RSA-AES256-GCM-SHA384:"
            "ECDHE-ECDSA-CHACHA20-POLY1305:"
            "ECDHE-RSA-CHACHA20-POLY1305:"
            "ECDHE-ECDSA-AES128-GCM-SHA256:"
            "ECDHE-RSA-AES128-GCM-SHA256:"
            "DHE-RSA-AES256-GCM-SHA384:"
            "DHE-RSA-CHACHA20-POLY1305:"
            "DHE-RSA-AES128-GCM-SHA256";

         LOG_DEBUG("SslContextInitializer::SetCipherList_ - SslCipherList is the AEAD-ONLY preset; applying " + String(cipher_list));
      }

      // Asio does not expose cipher list. Access underlaying layer (OpenSSL) directly.
      SSL_CTX* ssl = context.native_handle();
      int result = SSL_CTX_set_cipher_list(ssl, cipher_list.c_str());

      if (result == 0)
      {
         // Unable to set the SSL cipher list. Collect the error from OpenSSL so that
         // we can include it in the error message we log. GetOpenSslError_ also
         // empties the error queue, which matters here: InitServer goes on to load
         // the certificate and private key, and a leftover error from this call
         // would otherwise surface as a bogus failure of one of those.
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5511, "SslContextInitializer::SetCipherList_", Formatter::Format("Failed to set SSL ciphers. Message: {0}", String(GetOpenSslError_())));
      }

   }

   void
   SslContextInitializer::SetTls13CipherSuites_(boost::asio::ssl::context& context)
   {
      // SetCipherList_ above applies SslCipherList through SSL_CTX_set_cipher_list,
      // which configures the TLS 1.2-and-below cipher list and nothing else. TLS 1.3
      // holds its suites in a separate list, with separate names and a separate setter,
      // so on the protocol most peers now negotiate - and which ships enabled here -
      // SslCipherList restricted nothing at all. An administrator who removed a suite
      // there and confirmed it with a TLS 1.2 scan got a true result and a false
      // conclusion.
      AnsiString cipher_suites = IniFileSettings::Instance()->GetTlsCipherSuites13();

      cipher_suites.Replace("\r", "");
      cipher_suites.Replace("\n", "");
      cipher_suites.Replace(" ", "");
      cipher_suites = cipher_suites.Trim();

      // Empty means "leave whatever OpenSSL defaulted to", and the only way to say that
      // is not to call the function at all. SSL_CTX_set_ciphersuites accepts an empty
      // list, succeeds, and leaves the context with no TLS 1.3 suite whatsoever, after
      // which every TLS 1.3 handshake fails with no shared cipher. So this is a check
      // rather than a look at the return value.
      if (cipher_suites.IsEmpty())
         return;

      // Asio does not expose the TLS 1.3 suite list. Access the underlying layer
      // (OpenSSL) directly, as SetCipherList_ does.
      SSL_CTX* ssl = context.native_handle();

      if (SSL_CTX_set_ciphersuites(ssl, cipher_suites.c_str()) != 1)
      {
         // No fallback list here, unlike SetKeyExchangeGroups_: OpenSSL builds the
         // replacement list first and frees it on failure, so a rejected list is not
         // applied at all and the context keeps its default suites. TLS 1.3 still works;
         // what is wrong is that the administrator's list is not in force, so say so and
         // carry on. GetOpenSslError_ also empties the error queue, which matters
         // because InitServer goes on to load the certificate and private key.
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6120, "SslContextInitializer::SetTls13CipherSuites_",
            Formatter::Format("Failed to set the TLS 1.3 cipher suites '{0}'. Message: {1}. TLS 1.3 keeps the OpenSSL default suites. Note that these are the RFC 8446 suite names (for example TLS_AES_256_GCM_SHA384) and not the OpenSSL cipher-list names used by SslCipherList.",
               String(cipher_suites), String(GetOpenSslError_())));
         return;
      }

      // A name OpenSSL does not recognise is skipped and the rest of the list is still
      // accepted, so a single typo silently reduces the suites this server offers.
      AnsiString ignored = FindUnappliedCipherSuites(ssl, cipher_suites);

      if (!ignored.IsEmpty())
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6121, "SslContextInitializer::SetTls13CipherSuites_",
            Formatter::Format("The TLS 1.3 cipher suite names {0} are not known to, or not available in, this OpenSSL build and were ignored. The rest of '{1}' was applied. Check the spelling against the RFC 8446 names.",
               String(ignored), String(cipher_suites)));
      }
   }

   void
   SslContextInitializer::SetContextOptions_(boost::asio::ssl::context& context)
   {
      bool tlsv10 = Configuration::Instance()->GetSslVersionEnabled(TlsVersion10);
      bool tlsv11 = Configuration::Instance()->GetSslVersionEnabled(TlsVersion11);
      bool tlsv12 = Configuration::Instance()->GetSslVersionEnabled(TlsVersion12);
      bool tlsv13 = Configuration::Instance()->GetSslVersionEnabled(TlsVersion13);

      bool tlsPreferServerCiphers = Configuration::Instance()->GetTlsOptionEnabled(TlsOptionPreferServerCiphers);
      bool tlsPrioritizeChaCha = Configuration::Instance()->GetTlsOptionEnabled(TlsOptionPrioritizeChaCha);

      // SSLv2 and SSLv3 are disabled unconditionally below, so with all four TLS
      // versions turned off this context can negotiate nothing whatsoever. OpenSSL
      // accepts that option mask without complaint, the listener binds, STARTTLS is
      // still advertised - and then every handshake on it fails with "no protocols
      // available" for as long as the setting stays that way. Nothing was reported,
      // so from the server's side the symptom was mail silently not arriving.
      //
      // "TLS, but no version of it" is not a configuration anyone can mean: turning
      // TLS off is done per port, with ConnectionSecurity. So this is treated the
      // same way an unusable key-exchange group list is treated a few functions
      // below - say so in the error log, and install something that works, rather
      // than serve a listener that cannot complete a single handshake.
      if (!tlsv10 && !tlsv11 && !tlsv12 && !tlsv13)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 5990, "SslContextInitializer::SetContextOptions_",
            "No TLS protocol version is enabled, so no TLS handshake could succeed on any listener. Enable at least one version under Settings -> Advanced -> Protocols (the SslVersions setting; SSLv2 and SSLv3 cannot be enabled at all). Falling back to TLS 1.2 and TLS 1.3 so that TLS keeps working.");

         tlsv12 = true;
         tlsv13 = true;
      }

      int options = SSL_OP_ALL | SSL_OP_SINGLE_DH_USE | SSL_OP_NO_SSLv2 | SSL_OP_NO_SSLv3 | SSL_OP_SINGLE_ECDH_USE;

      if (!tlsv10)
         options = options | SSL_OP_NO_TLSv1;
      if (!tlsv11)
         options = options | SSL_OP_NO_TLSv1_1;
      if (!tlsv12)
         options = options | SSL_OP_NO_TLSv1_2;
      if (!tlsv13)
         options = options | SSL_OP_NO_TLSv1_3;

      if (tlsPreferServerCiphers)
         options = options | SSL_OP_CIPHER_SERVER_PREFERENCE;
      if (tlsPrioritizeChaCha && tlsPreferServerCiphers && (tlsv12 || tlsv13))
         options = options | SSL_OP_PRIORITIZE_CHACHA;

      // The same floor expressed through Boost.Asio's own option flags as well,
      // for the reader and the analyser that see the context through that API:
      // SSLv2 and SSLv3 never, each TLS version as the toggles say. The native
      // mask below carries the rest - the single-use ECDH key, the server cipher
      // preference, the ChaCha priority - which Boost has no flags for. Both
      // calls OR into the same option word, so nothing is applied twice.
      boost::asio::ssl::context::options boostOptions =
         boost::asio::ssl::context::default_workarounds |
         boost::asio::ssl::context::single_dh_use |
         boost::asio::ssl::context::no_sslv2 |
         boost::asio::ssl::context::no_sslv3;

      if (!tlsv10)
         boostOptions |= boost::asio::ssl::context::no_tlsv1;
      if (!tlsv11)
         boostOptions |= boost::asio::ssl::context::no_tlsv1_1;
      if (!tlsv12)
         boostOptions |= boost::asio::ssl::context::no_tlsv1_2;
      if (!tlsv13)
         boostOptions |= boost::asio::ssl::context::no_tlsv1_3;

      context.set_options(boostOptions);

      SSL_CTX* ssl = context.native_handle();
      SSL_CTX_set_options(ssl, options);
   }

   AnsiString
   SslContextInitializer::GetOpenSslError_()
   {
      unsigned long firstError = ERR_get_error();

      // Discard anything else this failure queued. The queue is per-thread and is
      // shared with every other OpenSSL call, so errors we leave behind would be
      // picked up and reported by an unrelated call later on in this same thread.
      ERR_clear_error();

      if (firstError == 0)
         return AnsiString("no OpenSSL error was reported");

      char buffer[256] = { 0 };
      ERR_error_string_n(firstError, buffer, sizeof(buffer));

      return AnsiString(buffer);
   }

   bool
   SslContextInitializer::ContainsGroupToAdd_(const AnsiString& groupList)
   {
      // OpenSSL's group-list syntax uses ':' between groups, '/' between
      // preference tuples, and the entry prefixes '?' (ignore this group if this
      // build does not implement it), '*' (send a key share for it - client side
      // only) and '-' (remove the group). A list built only from '-' removals is
      // accepted by OpenSSL but leaves the context with no groups at all, and a
      // context with no groups cannot complete a TLS 1.3 handshake. Require at
      // least one entry that adds a group, so that such a list is treated as a
      // configuration error instead of silently disabling TLS.
      AnsiString flattenedList = groupList;
      flattenedList.Replace("/", ":");

      std::vector<AnsiString> entries = StringParser::SplitString(flattenedList, ":");

      for (AnsiString entry : entries)
      {
         while (!entry.IsEmpty() && (entry.GetAt(0) == '?' || entry.GetAt(0) == '*'))
            entry = entry.Mid(1);

         if (!entry.IsEmpty() && entry.GetAt(0) != '-')
            return true;
      }

      return false;
   }

   void
   SslContextInitializer::SetKeyExchangeGroups_(boost::asio::ssl::context& context)
   {
      // The classical-only list hMailServer used before post-quantum groups
      // existed. Every peer we have ever interoperated with supports one of these,
      // so it is what we fall back to when the configured list is unusable. This
      // function must never return without a list OpenSSL accepted: a context left
      // with no usable group cannot complete a handshake at all, which would take
      // TLS down on every listener at once.
      const AnsiString classicalGroups = "secp384r1:x25519:secp256r1";

      AnsiString configuredGroups = IniFileSettings::Instance()->GetTlsKeyExchangeGroups();

      configuredGroups.Replace("\r", "");
      configuredGroups.Replace("\n", "");
      configuredGroups.Replace(" ", "");
      configuredGroups = configuredGroups.Trim();

      SSL_CTX* ssl = context.native_handle();

      if (!configuredGroups.IsEmpty())
      {
         // Note that we must not hand an empty string to OpenSSL: it treats that
         // as "no groups", *succeeds*, and leaves the context unable to negotiate
         // a key exchange. Hence the check above rather than relying on the return
         // value below.
         if (!ContainsGroupToAdd_(configuredGroups))
         {
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5720, "SslContextInitializer::SetKeyExchangeGroups_",
               Formatter::Format("The configured TLS key exchange groups '{0}' would leave no group enabled. Falling back to '{1}'.",
                  String(configuredGroups), String(classicalGroups)));
         }
         else if (1 == SSL_CTX_set1_curves_list(ssl, configuredGroups.c_str()))
         {
            LOG_DEBUG("SslContextInitializer::SetKeyExchangeGroups_ - TLS key exchange groups set to " + String(configuredGroups));
            return;
         }
         else
         {
            // OpenSSL did not accept the list: an unknown group name (a typo, or a
            // group this OpenSSL build does not implement) or malformed syntax. A
            // rejected list is not applied at all, so we still have to install one.
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5720, "SslContextInitializer::SetKeyExchangeGroups_",
               Formatter::Format("Failed to set the TLS key exchange groups '{0}'. Message: {1}. Falling back to '{2}'.",
                  String(configuredGroups), String(GetOpenSslError_()), String(classicalGroups)));
         }
      }

      if (1 != SSL_CTX_set1_curves_list(ssl, classicalGroups.c_str()))
      {
         // Should not happen - these three groups are present in every OpenSSL
         // build we link against. Report it loudly, because TLS on this listener
         // is now relying on whatever group list OpenSSL defaulted to.
         ErrorManager::Instance()->ReportError(ErrorManager::High, 5721, "SslContextInitializer::SetKeyExchangeGroups_",
            Formatter::Format("Failed to set the fallback TLS key exchange groups '{0}'. Message: {1}. TLS will use the OpenSSL default groups.",
               String(classicalGroups), String(GetOpenSslError_())));
      }
   }

}