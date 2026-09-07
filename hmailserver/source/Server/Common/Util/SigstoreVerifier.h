// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include <string>
#include <vector>

namespace HM
{
   // What a Sigstore bundle is checked against: the certificate authority that
   // issued the signing certificate, the transparency log that recorded the
   // signature, and the identity the certificate must name.
   struct SigstoreTrust
   {
      std::vector<std::string> ca_pems;   // the CA certificates trusted to have issued the signer's certificate
      std::string log_key_pem;            // the transparency log's public key
      AnsiString identity_prefix;         // the certificate's subject alternative name must start with this
      AnsiString issuer;                  // the OIDC issuer the certificate records must be this
      AnsiString repository;              // the source repository it records must be this (empty: not checked)
   };

   struct SigstoreVerdict
   {
      SigstoreVerdict() : verified(false), integrated_time(0) {}

      bool verified;
      String error;                       // why not, when not
      AnsiString identity;                // the signer's subject alternative name
      __int64 integrated_time;            // when the log recorded the signature, Unix seconds
   };

   // Verifies a file against the Sigstore bundle cosign wrote for it, with no network
   // and no cosign: the bundle carries everything, and the trust is embedded.
   //
   // A release of this project is signed keylessly by its sign-release workflow: the
   // workflow proves who it is to Fulcio with a GitHub OIDC token, receives a
   // ten-minute certificate naming the workflow, signs the file's SHA-256 with the
   // ephemeral key, and records the signature with Rekor, which vouches for the time.
   // The bundle is the certificate, the signature, and Rekor's record with its proof.
   // Verifying it means (RFC 9162 for the log, RFC 5280 for the chain):
   //
   //   1. the file's SHA-256 is the digest the bundle signs and the log recorded;
   //   2. the certificate chains to the Fulcio CA at the time the log recorded the
   //      signature - which is the only time a ten-minute certificate is valid, and
   //      why the log's word on the time matters;
   //   3. the certificate names this repository's release workflow, issued by
   //      GitHub's token service, for this repository;
   //   4. the signature verifies under the certificate's key;
   //   5. the log's record matches the signature and certificate, and the log vouches
   //      for it: its signed entry timestamp, or its inclusion proof to a checkpoint
   //      it signed, under the log's key.
   //
   // A file that fails any of these is not the release's file, whatever the
   // transport said.
   class SigstoreVerifier
   {
   public:
      // The public Sigstore instance (Fulcio's root and intermediate, Rekor's key)
      // and this repository's release workflow.
      static SigstoreTrust DefaultTrust();

      // The trust as configured: DefaultTrust, with any UpdateTrustRootsFile,
      // UpdateLogPublicKeyFile, UpdateSigningIdentity, UpdateSigningIssuer and
      // UpdateSourceRepository from hMailServer.INI replacing their part of it.
      // False, with error, when a configured file cannot be read.
      static bool ConfiguredTrust(SigstoreTrust &trust, String &error);

      static bool VerifyFile(const String &path, const AnsiString &bundleJson, const SigstoreTrust &trust, SigstoreVerdict &verdict);

      static bool Sha256File(const String &path, std::vector<unsigned char> &digest, String &error);
      static bool Base64Decode(const std::string &text, std::vector<unsigned char> &out);
      static std::string Hex(const std::vector<unsigned char> &bytes);
   };
}
