// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#pragma once

#include <vector>
#include <memory>

namespace HM
{
   class Account;

   // Server side of the SCRAM-SHA-256 SASL mechanism (RFC 5802 / RFC 7677).
   //
   // hMailServer stores account passwords as PBKDF2-HMAC-SHA256
   // ($h1$<iter>$<salt>$<key>). The 32-byte derived key is, by construction,
   // exactly the SCRAM "SaltedPassword" for the same salt and iteration count, so
   // SCRAM authentication can be served from the existing stored hash without ever
   // seeing or storing the clear-text password.
   //
   // One instance models a single in-progress conversation on a connection. The
   // transport (base64 wrapping of each SASL message, account lookup, capability
   // advertising and connection state) lives in the protocol layer; this class only
   // parses/builds the SCRAM messages and verifies the client proof.
   class ScramSha256
   {
   public:
      ScramSha256();

      enum State
      {
         NeedClientFirst = 0,  // freshly created; awaiting the client-first message
         NeedClientFinal,      // server-first sent; awaiting the client-final message
         NeedAck,              // proof verified, server-final sent; awaiting empty ack
         Complete,             // authentication succeeded
         Failed                // authentication failed (bad proof / malformed)
      };

      State GetState() const { return state_; }
      void SetState(State state) { state_ = state; }

      std::shared_ptr<const Account> GetAccount() const { return account_; }
      void SetAccount(std::shared_ptr<const Account> account) { account_ = account; }

      // The SCRAM authentication identity, retained for failed-login (auto-ban)
      // accounting after the exchange has moved past the client-first message.
      AnsiString GetUsername() const { return username_; }
      void SetUsername(const AnsiString &username) { username_ = username; }

      // Enable the SCRAM-SHA-256-PLUS variant. The client must use the
      // 'p=tls-server-end-point' gs2 channel-binding flag and its channel-binding
      // data (sent in the client-final 'c=' attribute) must equal cbindData, which
      // is the RFC 5929 tls-server-end-point hash of the server certificate. Call
      // before ProcessClientFirst.
      void SetChannelBinding(const std::vector<unsigned char> &cbindData);

      // Indicate that the server offers channel binding on this connection (i.e. it
      // is a TLS connection advertising SCRAM-SHA-256-PLUS). A non-PLUS client that
      // then sends the 'y' gs2 flag is signalling a stripped-PLUS downgrade and is
      // rejected (RFC 5802 section 6). Call before ProcessClientFirst on the
      // non-PLUS mechanism when the connection is TLS.
      void SetServerSupportsChannelBinding() { server_supports_cbind_ = true; }

      // Extract the SCRAM authentication identity (the "n=" attribute) from a decoded
      // client-first message. Returns false on a malformed message.
      static bool ExtractUsername(const AnsiString &clientFirst, AnsiString &usernameOut);

      // Process the decoded client-first message and produce the (plaintext)
      // server-first message to send back. Pass the account's stored PBKDF2 hash, or
      // an empty/invalid string to run a forced-failure exchange that does not reveal
      // whether the account exists or supports SCRAM. Returns false only on a
      // malformed/unsupported client-first (a protocol error).
      bool ProcessClientFirst(const AnsiString &clientFirst, const AnsiString &storedPbkdf2Hash, AnsiString &serverFirstOut);

      // Process the decoded client-final message. On a valid client proof (and a real
      // account) produces the (plaintext) server-final message (v=...) and returns
      // true. Returns false on an invalid proof, nonce or channel-binding mismatch.
      bool ProcessClientFinal(const AnsiString &clientFinal, AnsiString &serverFinalOut);

   private:
      static bool ParseStoredPbkdf2_(const AnsiString &stored, int &iterations,
                                     std::vector<unsigned char> &salt,
                                     std::vector<unsigned char> &saltedPassword);
      static AnsiString GetAttribute_(const AnsiString &message, char name);
      static void HmacSha256_(const unsigned char *key, size_t keyLen,
                              const unsigned char *data, size_t dataLen,
                              unsigned char out[32]);
      static void Sha256_(const unsigned char *data, size_t dataLen, unsigned char out[32]);
      static AnsiString Base64Encode_(const unsigned char *data, size_t length);
      static bool Base64Decode_(const AnsiString &input, std::vector<unsigned char> &out);
      static AnsiString GenerateNonce_();

      // Produce a stable per-installation salt for an unknown/non-PBKDF2 account so
      // that repeated probes for the same name always return the same salt, making a
      // non-existent account indistinguishable from a real one (a per-exchange random
      // salt would change on every probe and so leak whether the account exists).
      static void DeriveAntiEnumerationSalt_(const AnsiString &username, std::vector<unsigned char> &saltOut);

      State state_;
      bool real_account_;   // false => unknown user / non-PBKDF2: always fail at the end
      bool plus_mode_;             // true => SCRAM-SHA-256-PLUS (channel binding required)
      bool server_supports_cbind_; // true => server offered PLUS on this connection
      std::vector<unsigned char> channel_binding_data_; // expected tls-server-end-point
      std::vector<unsigned char> salted_password_;
      AnsiString gs2_header_;
      AnsiString client_first_bare_;
      AnsiString server_first_;
      AnsiString combined_nonce_;
      AnsiString username_;
      std::shared_ptr<const Account> account_;
   };
}
