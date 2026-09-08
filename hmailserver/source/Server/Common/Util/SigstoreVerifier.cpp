// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "SigstoreVerifier.h"
#include "JsonDocument.h"
#include "FileUtilities.h"
#include "Unicode.h"
#include "../Application/IniFileSettings.h"

#include <openssl/bio.h>
#include <openssl/err.h>
#include <openssl/evp.h>
#include <openssl/pem.h>
#include <openssl/sha.h>
#include <openssl/x509.h>
#include <openssl/x509v3.h>

#include <fstream>
#include <memory>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // The public Sigstore instance's trust root, as published in the
      // sigstore/root-signing repository (targets/fulcio_v1.crt.pem,
      // fulcio_intermediate_v1.crt.pem, rekor.pub). Both certificates expire on
      // 5 October 2031; a release before then will carry their successors.
      const char *FULCIO_ROOT_PEM =
         "-----BEGIN CERTIFICATE-----\n"
         "MIIB9zCCAXygAwIBAgIUALZNAPFdxHPwjeDloDwyYChAO/4wCgYIKoZIzj0EAwMw\n"
         "KjEVMBMGA1UEChMMc2lnc3RvcmUuZGV2MREwDwYDVQQDEwhzaWdzdG9yZTAeFw0y\n"
         "MTEwMDcxMzU2NTlaFw0zMTEwMDUxMzU2NThaMCoxFTATBgNVBAoTDHNpZ3N0b3Jl\n"
         "LmRldjERMA8GA1UEAxMIc2lnc3RvcmUwdjAQBgcqhkjOPQIBBgUrgQQAIgNiAAT7\n"
         "XeFT4rb3PQGwS4IajtLk3/OlnpgangaBclYpsYBr5i+4ynB07ceb3LP0OIOZdxex\n"
         "X69c5iVuyJRQ+Hz05yi+UF3uBWAlHpiS5sh0+H2GHE7SXrk1EC5m1Tr19L9gg92j\n"
         "YzBhMA4GA1UdDwEB/wQEAwIBBjAPBgNVHRMBAf8EBTADAQH/MB0GA1UdDgQWBBRY\n"
         "wB5fkUWlZql6zJChkyLQKsXF+jAfBgNVHSMEGDAWgBRYwB5fkUWlZql6zJChkyLQ\n"
         "KsXF+jAKBggqhkjOPQQDAwNpADBmAjEAj1nHeXZp+13NWBNa+EDsDP8G1WWg1tCM\n"
         "WP/WHPqpaVo0jhsweNFZgSs0eE7wYI4qAjEA2WB9ot98sIkoF3vZYdd3/VtWB5b9\n"
         "TNMea7Ix/stJ5TfcLLeABLE4BNJOsQ4vnBHJ\n"
         "-----END CERTIFICATE-----\n";

      const char *FULCIO_INTERMEDIATE_PEM =
         "-----BEGIN CERTIFICATE-----\n"
         "MIICGjCCAaGgAwIBAgIUALnViVfnU0brJasmRkHrn/UnfaQwCgYIKoZIzj0EAwMw\n"
         "KjEVMBMGA1UEChMMc2lnc3RvcmUuZGV2MREwDwYDVQQDEwhzaWdzdG9yZTAeFw0y\n"
         "MjA0MTMyMDA2MTVaFw0zMTEwMDUxMzU2NThaMDcxFTATBgNVBAoTDHNpZ3N0b3Jl\n"
         "LmRldjEeMBwGA1UEAxMVc2lnc3RvcmUtaW50ZXJtZWRpYXRlMHYwEAYHKoZIzj0C\n"
         "AQYFK4EEACIDYgAE8RVS/ysH+NOvuDZyPIZtilgUF9NlarYpAd9HP1vBBH1U5CV7\n"
         "7LSS7s0ZiH4nE7Hv7ptS6LvvR/STk798LVgMzLlJ4HeIfF3tHSaexLcYpSASr1kS\n"
         "0N/RgBJz/9jWCiXno3sweTAOBgNVHQ8BAf8EBAMCAQYwEwYDVR0lBAwwCgYIKwYB\n"
         "BQUHAwMwEgYDVR0TAQH/BAgwBgEB/wIBADAdBgNVHQ4EFgQU39Ppz1YkEZb5qNjp\n"
         "KFWixi4YZD8wHwYDVR0jBBgwFoAUWMAeX5FFpWapesyQoZMi0CrFxfowCgYIKoZI\n"
         "zj0EAwMDZwAwZAIwPCsQK4DYiZYDPIaDi5HFKnfxXx6ASSVmERfsynYBiX2X6SJR\n"
         "nZU84/9DZdnFvvxmAjBOt6QpBlc4J/0DxvkTCqpclvziL6BCCPnjdlIB3Pu3BxsP\n"
         "mygUY7Ii2zbdCdliiow=\n"
         "-----END CERTIFICATE-----\n";

      const char *REKOR_PUBLIC_KEY_PEM =
         "-----BEGIN PUBLIC KEY-----\n"
         "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE2G2Y+2tabdTV5BcGiBIx0a9fAFwr\n"
         "kBbmLSGtks4L3qX6yYY0zufBnhC8Ur/iy55GhWP/9A/bY2LhC30M9+RYtw==\n"
         "-----END PUBLIC KEY-----\n";

      // This repository's release workflow, as Fulcio names it in the certificate's
      // subject alternative name (the ref after @ is the branch the workflow ran
      // from), GitHub's token service as the issuer, and the repository itself.
      const char *RELEASE_IDENTITY_PREFIX = "https://github.com/Progressiverobot/hmailserver/.github/workflows/sign-release.yml@";
      const char *GITHUB_ISSUER = "https://token.actions.githubusercontent.com";
      const char *REPOSITORY = "https://github.com/Progressiverobot/hmailserver";

      // Fulcio's certificate extensions (github.com/sigstore/fulcio/blob/main/docs/oid-info.md).
      const char *OID_ISSUER_V1 = "1.3.6.1.4.1.57264.1.1";          // raw string
      const char *OID_ISSUER_V2 = "1.3.6.1.4.1.57264.1.8";          // DER UTF8String
      const char *OID_SOURCE_REPOSITORY_URI = "1.3.6.1.4.1.57264.1.12"; // DER UTF8String

      struct X509Free { void operator()(X509 *x) const { X509_free(x); } };
      struct PkeyFree { void operator()(EVP_PKEY *k) const { EVP_PKEY_free(k); } };
      struct BioFree { void operator()(BIO *b) const { BIO_free(b); } };
      struct StoreFree { void operator()(X509_STORE *s) const { X509_STORE_free(s); } };
      struct StoreCtxFree { void operator()(X509_STORE_CTX *c) const { X509_STORE_CTX_free(c); } };
      struct PkeyCtxFree { void operator()(EVP_PKEY_CTX *c) const { EVP_PKEY_CTX_free(c); } };
      struct MdCtxFree { void operator()(EVP_MD_CTX *c) const { EVP_MD_CTX_free(c); } };

      typedef std::unique_ptr<X509, X509Free> X509Ptr;
      typedef std::unique_ptr<EVP_PKEY, PkeyFree> PkeyPtr;
      typedef std::unique_ptr<BIO, BioFree> BioPtr;

      typedef std::vector<unsigned char> Bytes;

      std::string Utf8Of_(const String &text)
      {
         AnsiString narrow;
         Unicode::WideToMultiByte(text, narrow);
         return std::string(narrow.c_str());
      }

      // Protobuf's JSON form writes a 64-bit integer as a string; an older bundle
      // may carry a number. Either is the number.
      bool Int64Of_(const JsonValue *value, __int64 &out)
      {
         if (!value)
            return false;
         if (value->IsNumber())
         {
            out = value->AsInt64();
            return true;
         }
         if (value->IsString())
         {
            const std::string &text = value->AsString();
            if (text.empty() || text.find_first_not_of("0123456789") != std::string::npos)
               return false;
#ifdef HM_PLATFORM_POSIX
            // strtoll is the standard C name for _strtoi64 - the same conversion,
            // the same arguments, the same 64 bits.
            out = ::strtoll(text.c_str(), nullptr, 10);
#else
            out = _strtoi64(text.c_str(), nullptr, 10);
#endif
            return true;
         }
         return false;
      }

      std::string StringOf_(const JsonValue *value)
      {
         return value && value->IsString() ? value->AsString() : std::string();
      }

      bool Base64Of_(const JsonValue *value, Bytes &out)
      {
         return value && value->IsString() && SigstoreVerifier::Base64Decode(value->AsString(), out);
      }

      Bytes Sha256_(const unsigned char *data, size_t length)
      {
         Bytes digest(SHA256_DIGEST_LENGTH);
         SHA256(data, length, digest.data());
         return digest;
      }

      Bytes Sha256_(const Bytes &data)
      {
         return Sha256_(data.empty() ? nullptr : data.data(), data.size());
      }

      bool ParseCertificatesPem_(const std::string &pem, std::vector<X509Ptr> &out)
      {
         BioPtr bio(BIO_new_mem_buf(pem.data(), (int) pem.size()));
         if (!bio)
            return false;

         while (true)
         {
            X509 *certificate = PEM_read_bio_X509(bio.get(), nullptr, nullptr, nullptr);
            if (!certificate)
               break;
            out.push_back(X509Ptr(certificate));
         }
         ERR_clear_error();
         return !out.empty();
      }

      PkeyPtr ParsePublicKeyPem_(const std::string &pem)
      {
         BioPtr bio(BIO_new_mem_buf(pem.data(), (int) pem.size()));
         if (!bio)
            return PkeyPtr();
         PkeyPtr key(PEM_read_bio_PUBKEY(bio.get(), nullptr, nullptr, nullptr));
         ERR_clear_error();
         return key;
      }

      bool PublicKeyDer_(EVP_PKEY *key, Bytes &out)
      {
         int length = i2d_PUBKEY(key, nullptr);
         if (length <= 0)
            return false;
         out.resize((size_t) length);
         unsigned char *cursor = out.data();
         return i2d_PUBKEY(key, &cursor) == length;
      }

      bool CertificateDer_(X509 *certificate, Bytes &out)
      {
         int length = i2d_X509(certificate, nullptr);
         if (length <= 0)
            return false;
         out.resize((size_t) length);
         unsigned char *cursor = out.data();
         return i2d_X509(certificate, &cursor) == length;
      }

      // ECDSA (or any EVP key) with SHA-256 over a message, DER signature.
      bool VerifyMessage_(EVP_PKEY *key, const unsigned char *message, size_t length, const Bytes &signature)
      {
         std::unique_ptr<EVP_MD_CTX, MdCtxFree> context(EVP_MD_CTX_new());
         if (!context)
            return false;
         if (EVP_DigestVerifyInit(context.get(), nullptr, EVP_sha256(), nullptr, key) != 1)
            return false;
         return EVP_DigestVerify(context.get(), signature.data(), signature.size(), message, length) == 1;
      }

      // The same, over a digest already computed: what cosign signs is the SHA-256
      // of the file, and the file is not read again to check it.
      bool VerifyDigest_(EVP_PKEY *key, const Bytes &digest, const Bytes &signature)
      {
         std::unique_ptr<EVP_PKEY_CTX, PkeyCtxFree> context(EVP_PKEY_CTX_new(key, nullptr));
         if (!context)
            return false;
         if (EVP_PKEY_verify_init(context.get()) != 1)
            return false;
         if (EVP_PKEY_CTX_set_signature_md(context.get(), EVP_sha256()) != 1)
            return false;
         return EVP_PKEY_verify(context.get(), signature.data(), signature.size(), digest.data(), digest.size()) == 1;
      }

      // The contents of an extension's OCTET STRING, by OID.
      bool ExtensionValue_(X509 *certificate, const char *oid, std::string &out)
      {
         ASN1_OBJECT *object = OBJ_txt2obj(oid, 1);
         if (!object)
            return false;
         int index = X509_get_ext_by_OBJ(certificate, object, -1);
         ASN1_OBJECT_free(object);
         if (index < 0)
            return false;
         // Const on the way in, and cast away only at the call that will not take
         // it. The two OpenSSLs disagree about this pair: 4.0.2, which the Windows
         // build links, returns a const X509_EXTENSION * here and takes one in
         // X509_EXTENSION_get_data, while 3.5 returns and takes a mutable one.
         // Declaring the variable const is accepted by both - adding const to a
         // mutable pointer is always allowed - and the const_cast below is what
         // the older accessor needs; a const parameter takes the mutable pointer
         // just as happily. Nothing here writes through it either way.
         const X509_EXTENSION *extension = X509_get_ext(certificate, index);
         if (!extension)
            return false;
         const ASN1_OCTET_STRING *data = X509_EXTENSION_get_data(const_cast<X509_EXTENSION *>(extension));
         if (!data)
            return false;
         out.assign((const char *) ASN1_STRING_get0_data(data), (size_t) ASN1_STRING_length(data));
         return true;
      }

      // A DER UTF8String (tag 0x0C, a length, the text) as its text.
      bool DerUtf8String_(const std::string &der, std::string &out)
      {
         if (der.size() < 2 || (unsigned char) der[0] != 0x0C)
            return false;
         size_t position = 1;
         size_t length = (unsigned char) der[position++];
         if (length & 0x80)
         {
            size_t lengthBytes = length & 0x7F;
            if (lengthBytes == 0 || lengthBytes > 4 || position + lengthBytes > der.size())
               return false;
            length = 0;
            for (size_t i = 0; i < lengthBytes; i++)
               length = (length << 8) | (unsigned char) der[position++];
         }
         if (position + length != der.size())
            return false;
         out = der.substr(position, length);
         return true;
      }

      void SanUris_(X509 *certificate, std::vector<std::string> &uris)
      {
         GENERAL_NAMES *names = (GENERAL_NAMES *) X509_get_ext_d2i(certificate, NID_subject_alt_name, nullptr, nullptr);
         if (!names)
            return;
         for (int i = 0; i < sk_GENERAL_NAME_num(names); i++)
         {
            GENERAL_NAME *name = sk_GENERAL_NAME_value(names, i);
            if (name && name->type == GEN_URI)
               uris.push_back(std::string((const char *) ASN1_STRING_get0_data(name->d.uniformResourceIdentifier),
                                          (size_t) ASN1_STRING_length(name->d.uniformResourceIdentifier)));
         }
         GENERAL_NAMES_free(names);
      }

      // RFC 9162 section 2.1.3.2: the leaf's hash, the path and the tree size
      // reproduce the root, or the entry is not in that tree.
      bool VerifyInclusion_(const Bytes &leafHash, __int64 leafIndex, __int64 treeSize, const std::vector<Bytes> &path, const Bytes &root)
      {
         if (leafIndex < 0 || treeSize <= 0 || leafIndex >= treeSize)
            return false;

         unsigned __int64 fn = (unsigned __int64) leafIndex;
         unsigned __int64 sn = (unsigned __int64) treeSize - 1;
         Bytes r = leafHash;

         for (size_t i = 0; i < path.size(); i++)
         {
            if (sn == 0)
               return false;

            const Bytes &p = path[i];
            Bytes node;
            node.reserve(1 + p.size() + r.size());
            node.push_back(0x01);
            if ((fn & 1) == 1 || fn == sn)
            {
               node.insert(node.end(), p.begin(), p.end());
               node.insert(node.end(), r.begin(), r.end());
               r = Sha256_(node);
               if ((fn & 1) == 0)
               {
                  while (fn != 0 && (fn & 1) == 0)
                  {
                     fn >>= 1;
                     sn >>= 1;
                  }
               }
            }
            else
            {
               node.insert(node.end(), r.begin(), r.end());
               node.insert(node.end(), p.begin(), p.end());
               r = Sha256_(node);
            }
            fn >>= 1;
            sn >>= 1;
         }

         return sn == 0 && r == root;
      }

      // A signed note (golang.org/x/mod/sumdb/note, as Rekor writes checkpoints):
      // the body - origin, tree size, root hash, each on its own line - then an
      // empty line, then one "— name base64(keyhint||signature)" line per signer.
      struct Checkpoint
      {
         std::string body;
         std::string origin;
         __int64 size;
         Bytes root;
         std::vector<std::pair<std::string, Bytes> > signatures;   // name, hint||signature
      };

      bool ParseCheckpoint_(const std::string &note, Checkpoint &checkpoint)
      {
         size_t separator = note.find("\n\n");
         if (separator == std::string::npos)
            return false;

         checkpoint.body = note.substr(0, separator + 1);

         std::vector<std::string> lines;
         size_t start = 0;
         while (start < checkpoint.body.size())
         {
            size_t end = checkpoint.body.find('\n', start);
            if (end == std::string::npos)
               end = checkpoint.body.size();
            lines.push_back(checkpoint.body.substr(start, end - start));
            start = end + 1;
         }
         if (lines.size() < 3)
            return false;

         checkpoint.origin = lines[0];
         if (lines[1].empty() || lines[1].find_first_not_of("0123456789") != std::string::npos)
            return false;
#ifdef HM_PLATFORM_POSIX
         // See above: strtoll is _strtoi64 under its standard name.
         checkpoint.size = ::strtoll(lines[1].c_str(), nullptr, 10);
#else
         checkpoint.size = _strtoi64(lines[1].c_str(), nullptr, 10);
#endif
         if (!SigstoreVerifier::Base64Decode(lines[2], checkpoint.root))
            return false;

         const std::string dash = "\xE2\x80\x94 ";
         start = separator + 2;
         while (start < note.size())
         {
            size_t end = note.find('\n', start);
            if (end == std::string::npos)
               end = note.size();
            std::string line = note.substr(start, end - start);
            start = end + 1;
            if (line.compare(0, dash.size(), dash) != 0)
               continue;
            line = line.substr(dash.size());
            size_t space = line.find(' ');
            if (space == std::string::npos)
               continue;
            Bytes blob;
            if (!SigstoreVerifier::Base64Decode(line.substr(space + 1), blob) || blob.size() <= 4)
               continue;
            checkpoint.signatures.push_back(std::make_pair(line.substr(0, space), blob));
         }

         return !checkpoint.signatures.empty();
      }

      bool Fail_(SigstoreVerdict &verdict, const String &why)
      {
         verdict.verified = false;
         verdict.error = why;
         return false;
      }
   }

   SigstoreTrust
   SigstoreVerifier::DefaultTrust()
   {
      SigstoreTrust trust;
      trust.ca_pems.push_back(FULCIO_ROOT_PEM);
      trust.ca_pems.push_back(FULCIO_INTERMEDIATE_PEM);
      trust.log_key_pem = REKOR_PUBLIC_KEY_PEM;
      trust.identity_prefix = RELEASE_IDENTITY_PREFIX;
      trust.issuer = GITHUB_ISSUER;
      trust.repository = REPOSITORY;
      return trust;
   }

   bool
   SigstoreVerifier::ConfiguredTrust(SigstoreTrust &trust, String &error)
   {
      trust = DefaultTrust();
      IniFileSettings *settings = IniFileSettings::Instance();

      String rootsFile = settings->GetUpdateTrustRootsFile();
      if (!rootsFile.IsEmpty())
      {
         if (!FileUtilities::Exists(rootsFile))
         {
            error = Formatter::Format(_T("UpdateTrustRootsFile {0} does not exist."), rootsFile);
            return false;
         }
         std::string pem = Utf8Of_(FileUtilities::ReadCompleteTextFile(rootsFile));
         std::vector<X509Ptr> certificates;
         if (!ParseCertificatesPem_(pem, certificates))
         {
            error = Formatter::Format(_T("UpdateTrustRootsFile {0} carries no PEM certificate."), rootsFile);
            return false;
         }
         trust.ca_pems.clear();
         trust.ca_pems.push_back(pem);
      }

      String keyFile = settings->GetUpdateLogPublicKeyFile();
      if (!keyFile.IsEmpty())
      {
         if (!FileUtilities::Exists(keyFile))
         {
            error = Formatter::Format(_T("UpdateLogPublicKeyFile {0} does not exist."), keyFile);
            return false;
         }
         std::string pem = Utf8Of_(FileUtilities::ReadCompleteTextFile(keyFile));
         if (!ParsePublicKeyPem_(pem))
         {
            error = Formatter::Format(_T("UpdateLogPublicKeyFile {0} is not a PEM public key."), keyFile);
            return false;
         }
         trust.log_key_pem = pem;
      }

      String identity = settings->GetUpdateSigningIdentity();
      if (!identity.IsEmpty())
         trust.identity_prefix = AnsiString(identity);

      String issuer = settings->GetUpdateSigningIssuer();
      if (!issuer.IsEmpty())
         trust.issuer = AnsiString(issuer);

      // The repository check can be switched off with "-"; an empty setting keeps
      // the default, so that the default is what a blank INI gets.
      String repository = settings->GetUpdateSourceRepository();
      if (repository == _T("-"))
         trust.repository = "";
      else if (!repository.IsEmpty())
         trust.repository = AnsiString(repository);

      return true;
   }

   bool
   SigstoreVerifier::Sha256File(const String &path, std::vector<unsigned char> &digest, String &error)
   {
#ifdef HM_PLATFORM_POSIX
      // std::ifstream's constructor from a WIDE C string is a Microsoft extension;
      // the standard one takes a narrow path, which is also what this filesystem's
      // names are. Narrowed the way the rest of the tree narrows a String for a C
      // API - and a path that could not be represented that way could not be
      // opened by any other call here either, which is the failure the test just
      // below already reports by name.
      const AnsiString narrow_path = path;
      std::ifstream file(narrow_path.c_str(), std::ios::binary);
#else
      std::ifstream file(path.c_str(), std::ios::binary);
#endif
      if (!file)
      {
         error = Formatter::Format(_T("{0} could not be opened."), path);
         return false;
      }

      std::unique_ptr<EVP_MD_CTX, MdCtxFree> context(EVP_MD_CTX_new());
      if (!context || EVP_DigestInit_ex(context.get(), EVP_sha256(), nullptr) != 1)
      {
         error = _T("SHA-256 could not be initialised.");
         return false;
      }

      std::vector<char> buffer(1024 * 1024);
      while (file)
      {
         file.read(buffer.data(), (std::streamsize) buffer.size());
         std::streamsize got = file.gcount();
         if (got > 0 && EVP_DigestUpdate(context.get(), buffer.data(), (size_t) got) != 1)
         {
            error = _T("SHA-256 could not be updated.");
            return false;
         }
      }

      digest.resize(EVP_MAX_MD_SIZE);
      unsigned int length = 0;
      if (EVP_DigestFinal_ex(context.get(), digest.data(), &length) != 1)
      {
         error = _T("SHA-256 could not be finished.");
         return false;
      }
      digest.resize(length);
      return true;
   }

   bool
   SigstoreVerifier::Base64Decode(const std::string &text, std::vector<unsigned char> &out)
   {
      static const char *alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
      out.clear();

      unsigned accumulator = 0;
      int bits = 0;
      size_t padding = 0;
      for (size_t i = 0; i < text.size(); i++)
      {
         char c = text[i];
         if (c == '\r' || c == '\n' || c == ' ' || c == '\t')
            continue;
         if (c == '=')
         {
            padding++;
            continue;
         }
         if (padding > 0)
            return false;

         const char *where = strchr(alphabet, c);
         if (!where || c == '\0')
            return false;

         accumulator = (accumulator << 6) | (unsigned) (where - alphabet);
         bits += 6;
         if (bits >= 8)
         {
            bits -= 8;
            out.push_back((unsigned char) ((accumulator >> bits) & 0xFF));
         }
      }

      return padding <= 2;
   }

   std::string
   SigstoreVerifier::Hex(const std::vector<unsigned char> &bytes)
   {
      static const char *digits = "0123456789abcdef";
      std::string result;
      result.reserve(bytes.size() * 2);
      for (size_t i = 0; i < bytes.size(); i++)
      {
         result += digits[bytes[i] >> 4];
         result += digits[bytes[i] & 0x0F];
      }
      return result;
   }

   bool
   SigstoreVerifier::VerifyFile(const String &path, const AnsiString &bundleJson, const SigstoreTrust &trust, SigstoreVerdict &verdict)
   {
      verdict = SigstoreVerdict();

      // The trust first: a verifier with nothing to trust cannot say yes, and it
      // should say why before it reads a byte of the bundle.
      std::vector<X509Ptr> authorities;
      for (size_t i = 0; i < trust.ca_pems.size(); i++)
         ParseCertificatesPem_(trust.ca_pems[i], authorities);
      if (authorities.empty())
         return Fail_(verdict, _T("No certificate authority is trusted; nothing can verify."));

      PkeyPtr logKey = ParsePublicKeyPem_(trust.log_key_pem);
      if (!logKey)
         return Fail_(verdict, _T("The transparency log's public key could not be read; nothing can verify."));
      Bytes logKeyDer;
      if (!PublicKeyDer_(logKey.get(), logKeyDer))
         return Fail_(verdict, _T("The transparency log's public key could not be encoded."));
      Bytes logId = Sha256_(logKeyDer);

      // 1. The bundle, and the digest it is about.
      JsonValue bundle;
      std::string parseError;
      if (!JsonValue::Parse(std::string(bundleJson.c_str()), bundle, parseError))
         return Fail_(verdict, Formatter::Format(_T("The bundle is not JSON: {0}."), String(parseError.c_str())));

      std::string mediaType = bundle.GetString("mediaType");
      const std::string bundleType = "application/vnd.dev.sigstore.bundle";
      if (mediaType.compare(0, bundleType.size(), bundleType) != 0)
         return Fail_(verdict, Formatter::Format(_T("The bundle's media type is {0}, not a Sigstore bundle."), String(mediaType.c_str())));

      const JsonValue *material = bundle.Get("verificationMaterial");
      const JsonValue *message = bundle.Get("messageSignature");
      if (!material || !material->IsObject())
         return Fail_(verdict, _T("The bundle carries no verification material."));
      if (!message || !message->IsObject())
         return Fail_(verdict, _T("The bundle carries no message signature; an installer is signed as a blob, not as an attestation."));

      const JsonValue *messageDigest = message->Get("messageDigest");
      if (!messageDigest || messageDigest->GetString("algorithm") != "SHA2_256")
         return Fail_(verdict, _T("The bundle's digest is not SHA-256."));
      Bytes digest;
      if (!Base64Of_(messageDigest->Get("digest"), digest) || digest.size() != SHA256_DIGEST_LENGTH)
         return Fail_(verdict, _T("The bundle's digest is malformed."));
      Bytes signature;
      if (!Base64Of_(message->Get("signature"), signature) || signature.empty())
         return Fail_(verdict, _T("The bundle's signature is malformed."));

      Bytes fileDigest;
      String hashError;
      if (!Sha256File(path, fileDigest, hashError))
         return Fail_(verdict, hashError);
      if (fileDigest != digest)
         return Fail_(verdict, Formatter::Format(_T("The file's SHA-256 ({0}) is not the one the bundle signs ({1}): this is not the file that was signed."),
            String(Hex(fileDigest).c_str()), String(Hex(digest).c_str())));

      // 2. The certificate: the leaf, and any intermediates an older bundle carries.
      Bytes leafDer;
      std::vector<X509Ptr> untrusted;
      {
         const JsonValue *certificate = material->Get("certificate");
         const JsonValue *chain = material->Get("x509CertificateChain");
         if (certificate && certificate->IsObject())
         {
            if (!Base64Of_(certificate->Get("rawBytes"), leafDer))
               return Fail_(verdict, _T("The bundle's certificate is malformed."));
         }
         else if (chain && chain->IsObject())
         {
            const JsonValue *certificates = chain->Get("certificates");
            if (!certificates || !certificates->IsArray() || certificates->Size() == 0)
               return Fail_(verdict, _T("The bundle's certificate chain is empty."));
            for (size_t i = 0; i < certificates->Size(); i++)
            {
               Bytes der;
               if (!Base64Of_(certificates->At(i)->Get("rawBytes"), der))
                  return Fail_(verdict, _T("The bundle's certificate chain is malformed."));
               if (i == 0)
                  leafDer = der;
               else
               {
                  const unsigned char *cursor = der.data();
                  X509 *parsed = d2i_X509(nullptr, &cursor, (long) der.size());
                  if (parsed)
                     untrusted.push_back(X509Ptr(parsed));
               }
            }
         }
         else
            return Fail_(verdict, _T("The bundle carries no certificate."));
      }

      const unsigned char *cursor = leafDer.data();
      X509Ptr leaf(d2i_X509(nullptr, &cursor, (long) leafDer.size()));
      if (!leaf)
         return Fail_(verdict, _T("The bundle's certificate is not a DER X.509 certificate."));

      // 3. The log's record: the same digest, signature and certificate, and the
      // log's word for it - the signed entry timestamp, or the inclusion proof to a
      // checkpoint the log signed. Either is the log speaking; one is required.
      const JsonValue *entries = material->Get("tlogEntries");
      const JsonValue *entry = entries && entries->IsArray() ? entries->At(0) : nullptr;
      if (!entry || !entry->IsObject())
         return Fail_(verdict, _T("The bundle carries no transparency log entry."));

      __int64 logIndex = 0;
      __int64 integratedTime = 0;
      if (!Int64Of_(entry->Get("logIndex"), logIndex) || !Int64Of_(entry->Get("integratedTime"), integratedTime) || integratedTime <= 0)
         return Fail_(verdict, _T("The log entry's index or time is malformed."));

      Bytes entryLogId;
      const JsonValue *entryLog = entry->Get("logId");
      if (!entryLog || !Base64Of_(entryLog->Get("keyId"), entryLogId))
         return Fail_(verdict, _T("The log entry names no log."));
      if (entryLogId != logId)
         return Fail_(verdict, _T("The log entry was recorded by a log this server does not trust."));

      std::string bodyBase64 = StringOf_(entry->Get("canonicalizedBody"));
      Bytes body;
      if (bodyBase64.empty() || !Base64Decode(bodyBase64, body))
         return Fail_(verdict, _T("The log entry's body is malformed."));

      {
         JsonValue record;
         std::string recordError;
         if (!JsonValue::Parse(std::string(body.begin(), body.end()), record, recordError))
            return Fail_(verdict, _T("The log entry's body is not JSON."));
         if (record.GetString("kind") != "hashedrekord")
            return Fail_(verdict, Formatter::Format(_T("The log entry is a {0}, not a hashedrekord."), String(record.GetString("kind").c_str())));
         const JsonValue *spec = record.Get("spec");
         const JsonValue *data = spec ? spec->Get("data") : nullptr;
         const JsonValue *hash = data ? data->Get("hash") : nullptr;
         const JsonValue *recordSignature = spec ? spec->Get("signature") : nullptr;
         const JsonValue *publicKey = recordSignature ? recordSignature->Get("publicKey") : nullptr;
         if (!hash || hash->GetString("algorithm") != "sha256" || hash->GetString("value") != Hex(digest))
            return Fail_(verdict, _T("The log recorded a different digest from the one the bundle signs."));
         Bytes recordedSignature;
         if (!recordSignature || !Base64Of_(recordSignature->Get("content"), recordedSignature) || recordedSignature != signature)
            return Fail_(verdict, _T("The log recorded a different signature from the one the bundle carries."));
         Bytes recordedKeyPem;
         std::vector<X509Ptr> recorded;
         Bytes recordedDer;
         if (!publicKey || !Base64Of_(publicKey->Get("content"), recordedKeyPem) ||
             !ParseCertificatesPem_(std::string(recordedKeyPem.begin(), recordedKeyPem.end()), recorded) ||
             !CertificateDer_(recorded[0].get(), recordedDer) || recordedDer != leafDer)
            return Fail_(verdict, _T("The log recorded a different certificate from the one the bundle carries."));
      }

      bool vouched = false;

      const JsonValue *promise = entry->Get("inclusionPromise");
      if (promise && promise->IsObject())
      {
         Bytes timestampSignature;
         if (!Base64Of_(promise->Get("signedEntryTimestamp"), timestampSignature))
            return Fail_(verdict, _T("The log's signed entry timestamp is malformed."));

         // RFC 8785 canonical JSON of the entry, which is what the log signed.
         char numbers[64];
         sprintf_s(numbers, sizeof(numbers), "%I64d", integratedTime);
         std::string canonical = "{\"body\":\"" + bodyBase64 + "\",\"integratedTime\":" + numbers;
         sprintf_s(numbers, sizeof(numbers), "%I64d", logIndex);
         canonical += ",\"logID\":\"" + Hex(logId) + "\",\"logIndex\":" + numbers + "}";

         if (!VerifyMessage_(logKey.get(), (const unsigned char *) canonical.data(), canonical.size(), timestampSignature))
            return Fail_(verdict, _T("The log's signed entry timestamp does not verify under the log's key."));
         vouched = true;
      }

      const JsonValue *proof = entry->Get("inclusionProof");
      if (proof && proof->IsObject())
      {
         __int64 proofIndex = 0;
         __int64 treeSize = 0;
         if (!Int64Of_(proof->Get("logIndex"), proofIndex) || !Int64Of_(proof->Get("treeSize"), treeSize))
            return Fail_(verdict, _T("The inclusion proof's index or tree size is malformed."));
         Bytes root;
         if (!Base64Of_(proof->Get("rootHash"), root) || root.size() != SHA256_DIGEST_LENGTH)
            return Fail_(verdict, _T("The inclusion proof's root hash is malformed."));
         std::vector<Bytes> hashes;
         const JsonValue *hashList = proof->Get("hashes");
         if (hashList && hashList->IsArray())
         {
            for (size_t i = 0; i < hashList->Size(); i++)
            {
               Bytes node;
               if (!Base64Of_(hashList->At(i), node) || node.size() != SHA256_DIGEST_LENGTH)
                  return Fail_(verdict, _T("The inclusion proof's path is malformed."));
               hashes.push_back(node);
            }
         }

         Bytes leafInput;
         leafInput.reserve(1 + body.size());
         leafInput.push_back(0x00);
         leafInput.insert(leafInput.end(), body.begin(), body.end());
         if (!VerifyInclusion_(Sha256_(leafInput), proofIndex, treeSize, hashes, root))
            return Fail_(verdict, _T("The inclusion proof does not lead from the entry to the root it claims."));

         const JsonValue *checkpointValue = proof->Get("checkpoint");
         Checkpoint checkpoint;
         if (!checkpointValue || !ParseCheckpoint_(checkpointValue->GetString("envelope"), checkpoint))
            return Fail_(verdict, _T("The inclusion proof carries no signed checkpoint."));
         if (checkpoint.size != treeSize || checkpoint.root != root)
            return Fail_(verdict, _T("The checkpoint describes a different tree from the inclusion proof."));

         bool checkpointSigned = false;
         for (size_t i = 0; i < checkpoint.signatures.size() && !checkpointSigned; i++)
         {
            const Bytes &blob = checkpoint.signatures[i].second;
            if (!std::equal(blob.begin(), blob.begin() + 4, logId.begin()))
               continue;
            Bytes checkpointSignature(blob.begin() + 4, blob.end());
            checkpointSigned = VerifyMessage_(logKey.get(), (const unsigned char *) checkpoint.body.data(), checkpoint.body.size(), checkpointSignature);
         }
         if (!checkpointSigned)
            return Fail_(verdict, _T("The checkpoint is not signed by the log's key."));
         vouched = true;
      }

      if (!vouched)
         return Fail_(verdict, _T("The log entry carries neither a signed entry timestamp nor an inclusion proof, so the log does not vouch for it."));

      // 4. The certificate was valid when the log recorded the signature, chains
      // to the authority, and is for code signing.
      time_t when = (time_t) integratedTime;
      int notBefore = ASN1_TIME_cmp_time_t(X509_get0_notBefore(leaf.get()), when);
      if (notBefore == -2 || notBefore > 0)
         return Fail_(verdict, _T("The log recorded the signature before the certificate became valid."));
      int notAfter = ASN1_TIME_cmp_time_t(X509_get0_notAfter(leaf.get()), when);
      if (notAfter == -2 || notAfter < 0)
         return Fail_(verdict, _T("The log recorded the signature after the certificate had expired."));

      {
         std::unique_ptr<X509_STORE, StoreFree> store(X509_STORE_new());
         std::unique_ptr<X509_STORE_CTX, StoreCtxFree> context(X509_STORE_CTX_new());
         if (!store || !context)
            return Fail_(verdict, _T("The certificate store could not be created."));
         for (size_t i = 0; i < authorities.size(); i++)
            X509_STORE_add_cert(store.get(), authorities[i].get());

         STACK_OF(X509) *extra = sk_X509_new_null();
         for (size_t i = 0; i < untrusted.size(); i++)
            sk_X509_push(extra, untrusted[i].get());

         bool chained = false;
         String reason;
         if (X509_STORE_CTX_init(context.get(), store.get(), leaf.get(), extra) == 1)
         {
            X509_STORE_CTX_set_time(context.get(), 0, when);
            chained = X509_verify_cert(context.get()) == 1;
            if (!chained)
               reason = String(X509_verify_cert_error_string(X509_STORE_CTX_get_error(context.get())));
         }
         sk_X509_free(extra);

         if (!chained)
            return Fail_(verdict, Formatter::Format(_T("The signing certificate does not chain to a trusted authority: {0}."), reason));
      }

      // Explicitly for code signing: a certificate with no extended key usage at
      // all is "any purpose" to OpenSSL and to Go, and Fulcio never issues one, so
      // one here is not Fulcio's.
      X509_check_purpose(leaf.get(), -1, 0);
      if (X509_get_ext_by_NID(leaf.get(), NID_ext_key_usage, -1) < 0 || (X509_get_extended_key_usage(leaf.get()) & XKU_CODE_SIGN) == 0)
         return Fail_(verdict, _T("The signing certificate is not a code-signing certificate."));

      // 5. Whose certificate it is.
      std::vector<std::string> uris;
      SanUris_(leaf.get(), uris);
      std::string prefix = trust.identity_prefix.c_str();
      std::string identity;
      for (size_t i = 0; i < uris.size(); i++)
      {
         if (!prefix.empty() && uris[i].compare(0, prefix.size(), prefix) == 0)
         {
            identity = uris[i];
            break;
         }
      }
      if (identity.empty())
      {
         std::string named = uris.empty() ? std::string("nobody") : uris[0];
         return Fail_(verdict, Formatter::Format(_T("The certificate names {0}, not this repository's release workflow."), String(named.c_str())));
      }

      std::string issuer;
      std::string extension;
      if (ExtensionValue_(leaf.get(), OID_ISSUER_V2, extension))
      {
         if (!DerUtf8String_(extension, issuer))
            return Fail_(verdict, _T("The certificate's issuer extension is malformed."));
      }
      else if (ExtensionValue_(leaf.get(), OID_ISSUER_V1, extension))
         issuer = extension;
      if (issuer != std::string(trust.issuer.c_str()))
         return Fail_(verdict, Formatter::Format(_T("The certificate was issued on a token from {0}, not {1}."),
            String(issuer.empty() ? "no issuer" : issuer.c_str()), String(trust.issuer)));

      if (!trust.repository.IsEmpty())
      {
         std::string repository;
         if (!ExtensionValue_(leaf.get(), OID_SOURCE_REPOSITORY_URI, extension) || !DerUtf8String_(extension, repository))
            return Fail_(verdict, _T("The certificate names no source repository."));
         if (repository != std::string(trust.repository.c_str()))
            return Fail_(verdict, Formatter::Format(_T("The certificate is for {0}, not {1}."), String(repository.c_str()), String(trust.repository)));
      }

      // 6. The signature itself.
      PkeyPtr leafKey(X509_get_pubkey(leaf.get()));
      if (!leafKey)
         return Fail_(verdict, _T("The certificate's public key could not be read."));
      if (!VerifyDigest_(leafKey.get(), digest, signature))
         return Fail_(verdict, _T("The signature does not verify under the certificate's key."));

      verdict.verified = true;
      verdict.identity = identity.c_str();
      verdict.integrated_time = integratedTime;
      return true;
   }
}
