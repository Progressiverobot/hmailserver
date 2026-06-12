// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// ACME v2 (RFC 8555) client. See AcmeClient.h.

#include "StdAfx.h"

#include "AcmeClient.h"
#include "FileUtilities.h"
#include "WebServicesServer.h"
#include "Encoding/Base64.h"

#include "../Application/Reinitializator.h"
#include "../BO/SSLCertificate.h"
#include "../BO/SSLCertificates.h"
#include "../BO/TCPIPPort.h"
#include "../BO/TCPIPPorts.h"
#include "../Persistence/PersistentSSLCertificate.h"
#include "../Persistence/PersistentTCPIPPort.h"
#include "../TCPIP/CertificateVerifier.h"
#include "../TCPIP/SocketConstants.h"

#include <boost/asio.hpp>
#include <boost/asio/ssl.hpp>

#include <ws2tcpip.h>

#include <openssl/evp.h>
#include <openssl/rsa.h>
#include <openssl/pem.h>
#include <openssl/x509.h>
#include <openssl/x509v3.h>
#include <openssl/core_names.h>
#include <openssl/sha.h>

#include <ctime>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      const int RenewalWindowDays = 30;
      const int HttpsTimeoutMilliseconds = 30000;
      const size_t MaxResponseSize = 1024 * 1024;
      const int PollIntervalMilliseconds = 2000;
      const int MaxPollAttempts = 30;

      // Splits https://host[:port]/path into components.
      bool ParseHttpsUrl(const AnsiString &url, AnsiString &host, AnsiString &port, AnsiString &path)
      {
         AnsiString prefix = "https://";

         if (!AnsiString(url).StartsWith(prefix))
            return false;

         AnsiString remainder = url.Mid(prefix.GetLength());

         int pathPosition = remainder.Find("/");
         AnsiString hostAndPort = pathPosition >= 0 ? remainder.Mid(0, pathPosition) : remainder;
         path = pathPosition >= 0 ? remainder.Mid(pathPosition) : AnsiString("/");

         int portPosition = hostAndPort.Find(":");
         if (portPosition >= 0)
         {
            host = hostAndPort.Mid(0, portPosition);
            port = hostAndPort.Mid(portPosition + 1);
         }
         else
         {
            host = hostAndPort;
            port = "443";
         }

         return !host.IsEmpty();
      }

      AnsiString GetHeaderValue(const std::string &headers, const std::string &headerName)
      {
         std::string lowerHeaders = headers;
         for (size_t i = 0; i < lowerHeaders.size(); i++)
            lowerHeaders[i] = (char) tolower((unsigned char) lowerHeaders[i]);

         std::string needle = "\r\n" + headerName + ":";

         size_t position = lowerHeaders.find(needle);
         if (position == std::string::npos)
            return "";

         size_t valueStart = position + needle.size();
         size_t lineEnd = headers.find("\r\n", valueStart);
         if (lineEnd == std::string::npos)
            return "";

         AnsiString value = headers.substr(valueStart, lineEnd - valueStart).c_str();
         value.Trim();
         return value;
      }

      // Extracts the JSON object that encloses the given position.
      AnsiString ExtractEnclosingObject(const AnsiString &json, int position)
      {
         int start = position;
         int depth = 0;

         while (start >= 0)
         {
            char character = json[start];
            if (character == '}')
               depth++;
            else if (character == '{')
            {
               if (depth == 0)
                  break;
               depth--;
            }

            start--;
         }

         if (start < 0)
            return "";

         int end = position;
         depth = 0;

         while (end < json.GetLength())
         {
            char character = json[end];
            if (character == '{')
               depth++;
            else if (character == '}')
            {
               if (depth == 0)
                  break;
               depth--;
            }

            end++;
         }

         if (end >= json.GetLength())
            return "";

         return json.Mid(start, end - start + 1);
      }
   }

   AcmeClient::AcmeClient() :
      account_key_(nullptr)
   {

   }

   AcmeClient::~AcmeClient()
   {
      if (account_key_ != nullptr)
      {
         EVP_PKEY_free(account_key_);
         account_key_ = nullptr;
      }
   }

   String
   AcmeClient::GetCertificateDirectory()
   {
      String directory = IniFileSettings::Instance()->GetAcmeCertificateDirectory();

      if (directory.IsEmpty())
         directory = IniFileSettings::Instance()->GetDataDirectory() + _T("\\ACME");

      return directory;
   }

   bool
   AcmeClient::RenewalNeeded()
   {
      String certificateFile = GetCertificateDirectory() + _T("\\fullchain.pem");

      if (!FileUtilities::Exists(certificateFile))
         return true;

      AnsiString narrowFileName = certificateFile;

      BIO *bio = BIO_new_file(narrowFileName.c_str(), "r");
      if (bio == nullptr)
         return true;

      X509 *certificate = PEM_read_bio_X509(bio, nullptr, nullptr, nullptr);
      BIO_free(bio);

      if (certificate == nullptr)
         return true;

      time_t cutoff = time(nullptr) + static_cast<time_t>(RenewalWindowDays) * 86400;

      tm notAfterTm = {};
      bool stillValid = false;

      if (ASN1_TIME_to_tm(X509_get0_notAfter(certificate), &notAfterTm) == 1)
      {
         time_t notAfter = _mkgmtime(&notAfterTm);
         stillValid = notAfter != -1 && notAfter > cutoff;
      }

      X509_free(certificate);

      return !stillValid;
   }

   bool
   AcmeClient::Transact_(const AnsiString &url, const AnsiString &method, const AnsiString &payload, HttpResponse &response)
   {
      try
      {
         AnsiString host;
         AnsiString port;
         AnsiString path;

         if (!ParseHttpsUrl(url, host, port, path))
            return false;

         boost::asio::io_context ioContext;

         boost::asio::ip::tcp::resolver resolver(ioContext);
         boost::asio::ip::tcp::resolver::results_type endpoints =
            resolver.resolve(std::string(host.c_str()), std::string(port.c_str()));

         boost::asio::ssl::context sslContext(boost::asio::ssl::context::tls_client);
         sslContext.set_default_verify_paths();

         boost::asio::ssl::stream<boost::asio::ip::tcp::socket> stream(ioContext, sslContext);

         boost::asio::connect(stream.next_layer(), endpoints);

         DWORD timeout = HttpsTimeoutMilliseconds;
         setsockopt(stream.next_layer().native_handle(), SOL_SOCKET, SO_RCVTIMEO, (const char*) &timeout, sizeof(timeout));
         setsockopt(stream.next_layer().native_handle(), SOL_SOCKET, SO_SNDTIMEO, (const char*) &timeout, sizeof(timeout));

         stream.set_verify_mode(boost::asio::ssl::verify_peer);
         stream.set_verify_callback(CertificateVerifier(0, CSSSL, String(host)));

         if (!SSL_set_tlsext_host_name(stream.native_handle(), host.c_str()))
            return false;

         stream.handshake(boost::asio::ssl::stream_base::client);

         AnsiString contentLength;
         contentLength.Format("%d", payload.GetLength());

         AnsiString request;
         request.append(method);
         request.append(" ");
         request.append(path);
         request.append(" HTTP/1.0\r\nHost: ");
         request.append(host);
         request.append("\r\nUser-Agent: hMailServer-ACME\r\n");

         if (method == "POST")
         {
            request.append("Content-Type: application/jose+json\r\nContent-Length: ");
            request.append(contentLength);
            request.append("\r\n");
         }

         request.append("Connection: close\r\n\r\n");
         request.append(payload);

         boost::asio::write(stream, boost::asio::buffer(request.c_str(), request.GetLength()));

         std::string rawResponse;
         char buffer[4096];
         boost::system::error_code errorCode;

         for (;;)
         {
            size_t bytesRead = stream.read_some(boost::asio::buffer(buffer, sizeof(buffer)), errorCode);

            if (bytesRead > 0)
               rawResponse.append(buffer, bytesRead);

            if (errorCode)
               break;

            if (rawResponse.size() > MaxResponseSize)
               break;
         }

         size_t firstLineEnd = rawResponse.find("\r\n");
         size_t headerEnd = rawResponse.find("\r\n\r\n");

         if (firstLineEnd == std::string::npos || headerEnd == std::string::npos)
            return false;

         // Status line: HTTP/1.x NNN ...
         std::string statusLine = rawResponse.substr(0, firstLineEnd);
         size_t spacePosition = statusLine.find(' ');
         if (spacePosition == std::string::npos)
            return false;

         response.status_code = atoi(statusLine.c_str() + spacePosition + 1);

         std::string headers = rawResponse.substr(0, headerEnd);

         response.nonce = GetHeaderValue(headers, "replay-nonce");
         response.location = GetHeaderValue(headers, "location");
         response.body = rawResponse.substr(headerEnd + 4).c_str();

         if (!response.nonce.IsEmpty())
            nonce_ = response.nonce;

         return true;
      }
      catch (...)
      {
         return false;
      }
   }

   AnsiString
   AcmeClient::Base64Url_(const unsigned char *data, int length)
   {
      AnsiString encoded = Base64::Encode((const char*) data, length);

      AnsiString result;
      for (int i = 0; i < encoded.GetLength(); i++)
      {
         char character = encoded[i];

         switch (character)
         {
         case '+':
            result += "-";
            break;
         case '/':
            result += "_";
            break;
         case '=':
         case '\r':
         case '\n':
            break;
         default:
            result += character;
            break;
         }
      }

      return result;
   }

   AnsiString
   AcmeClient::Base64Url_(const AnsiString &data)
   {
      return Base64Url_((const unsigned char*) data.c_str(), data.GetLength());
   }

   AnsiString
   AcmeClient::JsonStringValue_(const AnsiString &json, const AnsiString &key, int searchFrom)
   {
      AnsiString needle = "\"" + key + "\"";

      int keyPosition = json.Find(needle, searchFrom);
      if (keyPosition < 0)
         return "";

      int colonPosition = json.Find(":", keyPosition + needle.GetLength());
      if (colonPosition < 0)
         return "";

      int valueStart = json.Find("\"", colonPosition);
      if (valueStart < 0)
         return "";

      valueStart++;

      int valueEnd = json.Find("\"", valueStart);
      if (valueEnd < 0)
         return "";

      return json.Mid(valueStart, valueEnd - valueStart);
   }

   bool
   AcmeClient::LoadOrCreateAccountKey_()
   {
      String directory = GetCertificateDirectory();
      FileUtilities::CreateDirectory(directory);

      String keyFile = directory + _T("\\account.key");
      AnsiString narrowKeyFile = keyFile;

      if (FileUtilities::Exists(keyFile))
      {
         BIO *bio = BIO_new_file(narrowKeyFile.c_str(), "r");
         if (bio != nullptr)
         {
            account_key_ = PEM_read_bio_PrivateKey(bio, nullptr, nullptr, nullptr);
            BIO_free(bio);
         }

         if (account_key_ != nullptr)
            return true;
      }

      account_key_ = EVP_RSA_gen(2048);
      if (account_key_ == nullptr)
         return false;

      BIO *bio = BIO_new_file(narrowKeyFile.c_str(), "w");
      if (bio == nullptr)
         return false;

      bool written = PEM_write_bio_PrivateKey(bio, account_key_, nullptr, nullptr, 0, nullptr, nullptr) == 1;
      BIO_free(bio);

      return written;
   }

   AnsiString
   AcmeClient::BuildJwk_() const
   {
      // RFC 7638: members in lexicographic order - e, kty, n.
      BIGNUM *modulus = nullptr;
      BIGNUM *exponent = nullptr;

      if (EVP_PKEY_get_bn_param(account_key_, OSSL_PKEY_PARAM_RSA_N, &modulus) != 1 ||
          EVP_PKEY_get_bn_param(account_key_, OSSL_PKEY_PARAM_RSA_E, &exponent) != 1)
      {
         if (modulus != nullptr)
            BN_free(modulus);
         if (exponent != nullptr)
            BN_free(exponent);

         return "";
      }

      std::vector<unsigned char> modulusBytes(BN_num_bytes(modulus));
      std::vector<unsigned char> exponentBytes(BN_num_bytes(exponent));

      BN_bn2bin(modulus, modulusBytes.data());
      BN_bn2bin(exponent, exponentBytes.data());

      BN_free(modulus);
      BN_free(exponent);

      AnsiString jwk;
      jwk += "{\"e\":\"" + Base64Url_(exponentBytes.data(), (int) exponentBytes.size()) + "\",";
      jwk += "\"kty\":\"RSA\",";
      jwk += "\"n\":\"" + Base64Url_(modulusBytes.data(), (int) modulusBytes.size()) + "\"}";

      return jwk;
   }

   AnsiString
   AcmeClient::GetJwkThumbprint_() const
   {
      AnsiString jwk = BuildJwk_();

      unsigned char digest[SHA256_DIGEST_LENGTH];
      SHA256((const unsigned char*) jwk.c_str(), jwk.GetLength(), digest);

      return Base64Url_(digest, SHA256_DIGEST_LENGTH);
   }

   AnsiString
   AcmeClient::SignJws_(const AnsiString &url, const AnsiString &payload, bool useJwk)
   {
      AnsiString protectedHeader;
      protectedHeader += "{\"alg\":\"RS256\",";

      if (useJwk)
         protectedHeader += "\"jwk\":" + BuildJwk_() + ",";
      else
         protectedHeader += "\"kid\":\"" + account_url_ + "\",";

      protectedHeader += "\"nonce\":\"" + nonce_ + "\",";
      protectedHeader += "\"url\":\"" + url + "\"}";

      AnsiString encodedHeader = Base64Url_(protectedHeader);
      AnsiString encodedPayload = Base64Url_(payload);

      AnsiString signingInput = encodedHeader + "." + encodedPayload;

      AnsiString signature;

      EVP_MD_CTX *context = EVP_MD_CTX_new();
      if (context == nullptr)
         return "";

      if (EVP_DigestSignInit(context, nullptr, EVP_sha256(), nullptr, account_key_) == 1)
      {
         size_t signatureLength = 0;
         if (EVP_DigestSign(context, nullptr, &signatureLength,
                (const unsigned char*) signingInput.c_str(), signingInput.GetLength()) == 1)
         {
            std::vector<unsigned char> signatureBytes(signatureLength);

            if (EVP_DigestSign(context, signatureBytes.data(), &signatureLength,
                   (const unsigned char*) signingInput.c_str(), signingInput.GetLength()) == 1)
            {
               signature = Base64Url_(signatureBytes.data(), (int) signatureLength);
            }
         }
      }

      EVP_MD_CTX_free(context);

      if (signature.IsEmpty())
         return "";

      AnsiString jws;
      jws += "{\"protected\":\"" + encodedHeader + "\",";
      jws += "\"payload\":\"" + encodedPayload + "\",";
      jws += "\"signature\":\"" + signature + "\"}";

      return jws;
   }

   bool
   AcmeClient::SignedPost_(const AnsiString &url, const AnsiString &payload, HttpResponse &response)
   {
      for (int attempt = 0; attempt < 2; attempt++)
      {
         if (nonce_.IsEmpty() && !FetchNonce_())
            return false;

         bool useJwk = account_url_.IsEmpty();

         AnsiString jws = SignJws_(url, payload, useJwk);
         nonce_ = ""; // A nonce may only be used once.

         if (jws.IsEmpty())
            return false;

         if (!Transact_(url, "POST", jws, response))
            return false;

         // Retry once with a fresh nonce if the server rejected ours.
         if (response.status_code == 400 && response.body.Find("urn:ietf:params:acme:error:badNonce") >= 0)
            continue;

         return true;
      }

      return false;
   }

   bool
   AcmeClient::FetchDirectory_()
   {
      AnsiString directoryUrl = IniFileSettings::Instance()->GetAcmeDirectoryUrl();

      HttpResponse response;
      if (!Transact_(directoryUrl, "GET", "", response) || response.status_code != 200)
         return false;

      url_new_nonce_ = JsonStringValue_(response.body, "newNonce");
      url_new_account_ = JsonStringValue_(response.body, "newAccount");
      url_new_order_ = JsonStringValue_(response.body, "newOrder");

      return !url_new_nonce_.IsEmpty() && !url_new_account_.IsEmpty() && !url_new_order_.IsEmpty();
   }

   bool
   AcmeClient::FetchNonce_()
   {
      HttpResponse response;
      if (!Transact_(url_new_nonce_, "GET", "", response))
         return false;

      return !nonce_.IsEmpty();
   }

   bool
   AcmeClient::RegisterAccount_()
   {
      AnsiString contactEmail = IniFileSettings::Instance()->GetAcmeContactEmail();

      AnsiString payload = "{\"termsOfServiceAgreed\":true";
      if (!contactEmail.IsEmpty())
         payload += ",\"contact\":[\"mailto:" + contactEmail + "\"]";
      payload += "}";

      HttpResponse response;
      if (!SignedPost_(url_new_account_, payload, response))
         return false;

      if (response.status_code != 200 && response.status_code != 201)
         return false;

      account_url_ = response.location;

      return !account_url_.IsEmpty();
   }

   bool
   AcmeClient::CreateOrder_(const std::vector<AnsiString> &domains, HttpResponse &orderResponse)
   {
      AnsiString payload = "{\"identifiers\":[";

      for (size_t i = 0; i < domains.size(); i++)
      {
         if (i > 0)
            payload += ",";

         payload += "{\"type\":\"dns\",\"value\":\"" + domains[i] + "\"}";
      }

      payload += "]}";

      if (!SignedPost_(url_new_order_, payload, orderResponse))
         return false;

      return orderResponse.status_code == 201;
   }

   bool
   AcmeClient::CompleteAuthorization_(const AnsiString &authorizationUrl)
   {
      // POST-as-GET: empty string payload.
      HttpResponse authorizationResponse;
      if (!SignedPost_(authorizationUrl, "", authorizationResponse) || authorizationResponse.status_code != 200)
         return false;

      AnsiString status = JsonStringValue_(authorizationResponse.body, "status");
      if (status == "valid")
         return true;

      // Locate the http-01 challenge object.
      int challengePosition = authorizationResponse.body.Find("\"type\":\"http-01\"");
      if (challengePosition < 0)
      {
         LOG_APPLICATION("ACME: Authorization offers no http-01 challenge.");
         return false;
      }

      AnsiString challengeObject = ExtractEnclosingObject(authorizationResponse.body, challengePosition);

      AnsiString challengeUrl = JsonStringValue_(challengeObject, "url");
      AnsiString token = JsonStringValue_(challengeObject, "token");

      if (challengeUrl.IsEmpty() || token.IsEmpty())
         return false;

      AnsiString keyAuthorization = token + "." + GetJwkThumbprint_();

      // Make the challenge available to whichever listener answers port 80.
      AcmeChallengeStore::Set(token, keyAuthorization);

      // Serve the challenge on the configured HTTP port. When the web
      // services server already listens there, it serves the challenge
      // from the store and no transient listener is needed.
      AcmeChallengeServer challengeServer;

      bool sharedListener = WebServicesServer::IsListeningOnPort(IniFileSettings::Instance()->GetAcmeHttpPort());

      if (!sharedListener)
      {
         if (!challengeServer.Start(IniFileSettings::Instance()->GetAcmeHttpPort()))
         {
            LOG_APPLICATION("ACME: Failed to start the http-01 challenge listener. Is the port in use?");
            return false;
         }

         challengeServer.SetChallenge(token, keyAuthorization);
      }

      // Tell the CA to validate.
      HttpResponse challengeResponse;
      if (!SignedPost_(challengeUrl, "{}", challengeResponse))
         return false;

      // Poll the authorization until it leaves the pending state.
      for (int attempt = 0; attempt < MaxPollAttempts; attempt++)
      {
         Sleep(PollIntervalMilliseconds);

         HttpResponse pollResponse;
         if (!SignedPost_(authorizationUrl, "", pollResponse) || pollResponse.status_code != 200)
            return false;

         status = JsonStringValue_(pollResponse.body, "status");

         if (status == "valid")
            return true;

         if (status == "invalid")
         {
            LOG_APPLICATION("ACME: Challenge validation failed: " + String(pollResponse.body.Mid(0, 500)));
            return false;
         }
      }

      LOG_APPLICATION("ACME: Timed out waiting for challenge validation.");
      return false;
   }

   bool
   AcmeClient::FinalizeOrder_(const AnsiString &finalizeUrl, const AnsiString &orderUrl, const std::vector<AnsiString> &domains)
   {
      // Reuse the existing certificate key when configured (the default):
      // a stable key keeps published DANE TLSA "3 1 1" records valid
      // across renewals.
      EVP_PKEY *domainKey = nullptr;

      if (IniFileSettings::Instance()->GetAcmeReuseKey())
      {
         AnsiString keyPath = AnsiString(GetCertificateDirectory() + _T("\\privkey.pem"));

         FILE *keyFile = nullptr;
         if (fopen_s(&keyFile, keyPath.c_str(), "rb") == 0 && keyFile != nullptr)
         {
            domainKey = PEM_read_PrivateKey(keyFile, nullptr, nullptr, nullptr);
            fclose(keyFile);

            if (domainKey != nullptr)
               LOG_APPLICATION("ACME: Reusing the existing certificate key (keeps published TLSA records valid).");
         }
      }

      if (domainKey == nullptr)
         domainKey = EVP_RSA_gen(2048);

      if (domainKey == nullptr)
         return false;

      // Build the CSR with all domains as subject alternative names.
      AnsiString sanList;
      for (size_t i = 0; i < domains.size(); i++)
      {
         if (i > 0)
            sanList += ",";
         sanList += "DNS:" + domains[i];
      }

      X509_REQ *request = X509_REQ_new();

      bool csrOk = false;
      std::vector<unsigned char> csrDer;

      if (request != nullptr && X509_REQ_set_pubkey(request, domainKey) == 1)
      {
         X509_EXTENSION *sanExtension = X509V3_EXT_conf_nid(nullptr, nullptr, NID_subject_alt_name, sanList.c_str());

         if (sanExtension != nullptr)
         {
            STACK_OF(X509_EXTENSION) *extensions = sk_X509_EXTENSION_new_null();
            sk_X509_EXTENSION_push(extensions, sanExtension);

            if (X509_REQ_add_extensions(request, extensions) == 1 &&
                X509_REQ_sign(request, domainKey, EVP_sha256()) > 0)
            {
               int derLength = i2d_X509_REQ(request, nullptr);
               if (derLength > 0)
               {
                  csrDer.resize(derLength);
                  unsigned char *writePointer = csrDer.data();
                  i2d_X509_REQ(request, &writePointer);
                  csrOk = true;
               }
            }

            sk_X509_EXTENSION_pop_free(extensions, X509_EXTENSION_free);
         }
      }

      if (request != nullptr)
         X509_REQ_free(request);

      if (!csrOk)
      {
         EVP_PKEY_free(domainKey);
         return false;
      }

      AnsiString payload = "{\"csr\":\"" + Base64Url_(csrDer.data(), (int) csrDer.size()) + "\"}";

      HttpResponse finalizeResponse;
      if (!SignedPost_(finalizeUrl, payload, finalizeResponse) || finalizeResponse.status_code != 200)
      {
         EVP_PKEY_free(domainKey);
         return false;
      }

      // Poll the order until the certificate is issued.
      AnsiString certificateUrl;

      for (int attempt = 0; attempt < MaxPollAttempts; attempt++)
      {
         HttpResponse orderResponse;
         if (!SignedPost_(orderUrl, "", orderResponse) || orderResponse.status_code != 200)
         {
            EVP_PKEY_free(domainKey);
            return false;
         }

         AnsiString status = JsonStringValue_(orderResponse.body, "status");

         if (status == "valid")
         {
            certificateUrl = JsonStringValue_(orderResponse.body, "certificate");
            break;
         }

         if (status == "invalid")
         {
            LOG_APPLICATION("ACME: Order failed: " + String(orderResponse.body.Mid(0, 500)));
            EVP_PKEY_free(domainKey);
            return false;
         }

         Sleep(PollIntervalMilliseconds);
      }

      if (certificateUrl.IsEmpty())
      {
         EVP_PKEY_free(domainKey);
         return false;
      }

      // Download the certificate chain.
      HttpResponse certificateResponse;
      if (!SignedPost_(certificateUrl, "", certificateResponse) || certificateResponse.status_code != 200)
      {
         EVP_PKEY_free(domainKey);
         return false;
      }

      // Persist the private key.
      String directory = GetCertificateDirectory();
      AnsiString narrowKeyFile = directory + _T("\\privkey.pem");

      bool keyWritten = false;

      BIO *bio = BIO_new_file(narrowKeyFile.c_str(), "w");
      if (bio != nullptr)
      {
         keyWritten = PEM_write_bio_PrivateKey(bio, domainKey, nullptr, nullptr, 0, nullptr, nullptr) == 1;
         BIO_free(bio);
      }

      EVP_PKEY_free(domainKey);

      if (!keyWritten)
         return false;

      // Persist the certificate chain.
      if (!FileUtilities::WriteToFile(directory + _T("\\fullchain.pem"), certificateResponse.body))
         return false;

      return true;
   }

   bool
   AcmeClient::RequestCertificate()
   {
      AcmeChallengeStore::Clear();

      AnsiString domainList = IniFileSettings::Instance()->GetAcmeDomains();

      std::vector<AnsiString> domains;
      for (AnsiString domain : StringParser::SplitString(domainList, ","))
      {
         domain.Trim();
         domain.MakeLower();

         if (!domain.IsEmpty())
            domains.push_back(domain);
      }

      if (domains.empty())
      {
         LOG_APPLICATION("ACME: No domains configured. Set AcmeDomains in hMailServer.ini.");
         return false;
      }

      if (!FetchDirectory_())
      {
         LOG_APPLICATION("ACME: Failed to fetch the ACME directory.");
         return false;
      }

      if (!LoadOrCreateAccountKey_())
      {
         LOG_APPLICATION("ACME: Failed to load or create the account key.");
         return false;
      }

      if (!RegisterAccount_())
      {
         LOG_APPLICATION("ACME: Account registration failed.");
         return false;
      }

      HttpResponse orderResponse;
      if (!CreateOrder_(domains, orderResponse))
      {
         LOG_APPLICATION("ACME: Order creation failed.");
         return false;
      }

      AnsiString orderUrl = orderResponse.location;
      AnsiString finalizeUrl = JsonStringValue_(orderResponse.body, "finalize");

      if (orderUrl.IsEmpty() || finalizeUrl.IsEmpty())
         return false;

      // Complete every authorization in the order.
      int searchPosition = orderResponse.body.Find("\"authorizations\"");
      if (searchPosition < 0)
         return false;

      int arrayStart = orderResponse.body.Find("[", searchPosition);
      int arrayEnd = orderResponse.body.Find("]", arrayStart);

      if (arrayStart < 0 || arrayEnd < 0)
         return false;

      AnsiString authorizationArray = orderResponse.body.Mid(arrayStart, arrayEnd - arrayStart + 1);

      int position = 0;
      for (;;)
      {
         int urlStart = authorizationArray.Find("\"", position);
         if (urlStart < 0)
            break;

         int urlEnd = authorizationArray.Find("\"", urlStart + 1);
         if (urlEnd < 0)
            break;

         AnsiString authorizationUrl = authorizationArray.Mid(urlStart + 1, urlEnd - urlStart - 1);
         position = urlEnd + 1;

         if (authorizationUrl.IsEmpty())
            continue;

         if (!CompleteAuthorization_(authorizationUrl))
         {
            LOG_APPLICATION("ACME: Authorization failed for one of the configured domains.");
            return false;
         }
      }

      if (!FinalizeOrder_(finalizeUrl, orderUrl, domains))
      {
         LOG_APPLICATION("ACME: Order finalization failed.");
         return false;
      }

      LOG_APPLICATION("ACME: Certificate issued successfully: " + GetCertificateDirectory() + _T("\\fullchain.pem"));

      // Publish-ready DANE record for administrators running inbound DANE.
      AnsiString spkiHex;
      if (GetCertificateTlsa(GetCertificateDirectory() + _T("\\fullchain.pem"), spkiHex))
         LOG_APPLICATION("ACME: DANE TLSA record for this certificate: _25._tcp.<mx-host>. IN TLSA 3 1 1 " + String(spkiHex));

      // Make the new certificate take effect without manual steps.
      ApplyCertificate_();

      AcmeChallengeStore::Clear();

      return true;
   }

   bool
   AcmeClient::GetCertificateTlsa(const String &certificate_file, AnsiString &spki_sha256_hex)
   {
      spki_sha256_hex = "";

      AnsiString narrowPath = certificate_file;

      FILE *file = nullptr;
      if (fopen_s(&file, narrowPath.c_str(), "rb") != 0 || file == nullptr)
         return false;

      X509 *certificate = PEM_read_X509(file, nullptr, nullptr, nullptr);
      fclose(file);

      if (certificate == nullptr)
         return false;

      bool success = false;

      int derLength = i2d_X509_PUBKEY(X509_get_X509_PUBKEY(certificate), nullptr);
      if (derLength > 0)
      {
         std::vector<unsigned char> der(derLength);
         unsigned char *writePointer = der.data();
         i2d_X509_PUBKEY(X509_get_X509_PUBKEY(certificate), &writePointer);

         unsigned char digest[SHA256_DIGEST_LENGTH];
         SHA256(der.data(), derLength, digest);

         for (int i = 0; i < SHA256_DIGEST_LENGTH; i++)
         {
            AnsiString hexByte;
            hexByte.Format("%02x", digest[i]);
            spki_sha256_hex += hexByte;
         }

         success = true;
      }

      X509_free(certificate);

      return success;
   }

   void
   AcmeClient::ApplyCertificate_()
   {
      const String certificateName = _T("ACME (automatic)");
      String certificateFile = GetCertificateDirectory() + _T("\\fullchain.pem");
      String privateKeyFile = GetCertificateDirectory() + _T("\\privkey.pem");

      // Create or update the SSL certificate record.
      SSLCertificates certificates;
      certificates.Refresh();

      std::shared_ptr<SSLCertificate> certificate = certificates.GetItemByName(certificateName);

      if (!certificate)
      {
         certificate = std::shared_ptr<SSLCertificate>(new SSLCertificate());
         certificate->SetName(certificateName);
      }

      certificate->SetCertificateFile(certificateFile);
      certificate->SetPrivateKeyFile(privateKeyFile);

      if (!PersistentSSLCertificate::SaveObject(certificate))
      {
         LOG_APPLICATION("ACME: Failed to save the SSL certificate record. Configure the certificate manually under Settings -> Advanced -> SSL certificates.");
         return;
      }

      // Assign the certificate to TLS-enabled ports that have none configured.
      TCPIPPorts ports;
      ports.Refresh();

      for (int i = 0; i < ports.GetCount(); i++)
      {
         std::shared_ptr<TCPIPPort> port = ports.GetItem(i);
         if (!port)
            continue;

         if (port->GetConnectionSecurity() == CSNone)
            continue;

         if (port->GetSSLCertificateID() > 0)
            continue;

         port->SetSSLCertificateID((int) certificate->GetID());

         String errorMessage;
         PersistentTCPIPPort::SaveObject(port, errorMessage, PersistenceModeNormal);

         String message;
         message.Format(_T("ACME: Assigned the certificate to port %d (no certificate was configured)."), port->GetPortNumber());
         LOG_APPLICATION(message);
      }

      // Restart the TCP servers so the new certificate is loaded.
      LOG_APPLICATION("ACME: Restarting servers to load the new certificate.");
      Reinitializator::Instance()->ReInitialize();
   }

   // -------------------------------------------------------------------
   // AcmeChallengeStore
   // -------------------------------------------------------------------

   boost::recursive_mutex AcmeChallengeStore::mutex_;
   std::map<AnsiString, AnsiString> AcmeChallengeStore::challenges_;

   void
   AcmeChallengeStore::Set(const AnsiString &token, const AnsiString &key_authorization)
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);
      challenges_[token] = key_authorization;
   }

   bool
   AcmeChallengeStore::Get(const AnsiString &token, AnsiString &key_authorization)
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      auto iterator = challenges_.find(token);
      if (iterator == challenges_.end())
         return false;

      key_authorization = iterator->second;
      return true;
   }

   void
   AcmeChallengeStore::Clear()
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);
      challenges_.clear();
   }

   // -------------------------------------------------------------------
   // AcmeChallengeServer
   // -------------------------------------------------------------------

   AcmeChallengeServer::AcmeChallengeServer() :
      listen_socket_(INVALID_SOCKET),
      running_(false)
   {

   }

   AcmeChallengeServer::~AcmeChallengeServer()
   {
      Stop();
   }

   bool
   AcmeChallengeServer::Start(int port)
   {
      sockaddr_in address = {};
      address.sin_family = AF_INET;
      address.sin_addr.s_addr = INADDR_ANY;
      address.sin_port = htons(static_cast<unsigned short>(port));

      listen_socket_ = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
      if (listen_socket_ == INVALID_SOCKET)
         return false;

      BOOL reuseAddress = TRUE;
      setsockopt(listen_socket_, SOL_SOCKET, SO_REUSEADDR, (const char*) &reuseAddress, sizeof(reuseAddress));

      if (bind(listen_socket_, reinterpret_cast<const sockaddr*>(&address), sizeof(address)) == SOCKET_ERROR ||
          listen(listen_socket_, 5) == SOCKET_ERROR)
      {
         closesocket(listen_socket_);
         listen_socket_ = INVALID_SOCKET;
         return false;
      }

      running_ = true;
      worker_ = std::thread(&AcmeChallengeServer::Run_, this);

      return true;
   }

   void
   AcmeChallengeServer::Stop()
   {
      if (!running_)
         return;

      running_ = false;

      if (listen_socket_ != INVALID_SOCKET)
      {
         closesocket(listen_socket_);
         listen_socket_ = INVALID_SOCKET;
      }

      if (worker_.joinable())
         worker_.join();
   }

   void
   AcmeChallengeServer::SetChallenge(const AnsiString &token, const AnsiString &keyAuthorization)
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);
      challenges_[token] = keyAuthorization;
   }

   void
   AcmeChallengeServer::Run_()
   {
      for (;;)
      {
         SOCKET clientSocket = accept(listen_socket_, nullptr, nullptr);

         if (clientSocket == INVALID_SOCKET)
         {
            if (!running_)
               return;

            continue;
         }

         DWORD timeout = 5000;
         setsockopt(clientSocket, SOL_SOCKET, SO_RCVTIMEO, (const char*) &timeout, sizeof(timeout));
         setsockopt(clientSocket, SOL_SOCKET, SO_SNDTIMEO, (const char*) &timeout, sizeof(timeout));

         char buffer[4096];
         int bytesRead = recv(clientSocket, buffer, sizeof(buffer) - 1, 0);

         AnsiString response = "HTTP/1.0 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

         if (bytesRead > 0)
         {
            buffer[bytesRead] = 0;
            AnsiString request = buffer;

            AnsiString prefix = "GET /.well-known/acme-challenge/";

            if (request.StartsWith(prefix))
            {
               int tokenEnd = request.Find(" ", prefix.GetLength());
               if (tokenEnd > 0)
               {
                  AnsiString token = request.Mid(prefix.GetLength(), tokenEnd - prefix.GetLength());

                  boost::lock_guard<boost::recursive_mutex> guard(mutex_);

                  auto iter = challenges_.find(token);
                  if (iter != challenges_.end())
                  {
                     response.Format("HTTP/1.0 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: %d\r\nConnection: close\r\n\r\n%hs",
                        iter->second.GetLength(), iter->second.c_str());
                  }
               }
            }
         }

         send(clientSocket, response.c_str(), response.GetLength(), 0);
         shutdown(clientSocket, SD_SEND);
         closesocket(clientSocket);
      }
   }

   // -------------------------------------------------------------------
   // AcmeRenewalTask
   // -------------------------------------------------------------------

   AcmeRenewalTask::AcmeRenewalTask()
   {

   }

   AcmeRenewalTask::~AcmeRenewalTask()
   {

   }

   void
   AcmeRenewalTask::DoWork()
   {
      if (!IniFileSettings::Instance()->GetAcmeEnabled())
         return;

      if (!AcmeClient::RenewalNeeded())
         return;

      LOG_APPLICATION("ACME: Certificate is missing or expires soon. Requesting a new certificate.");

      AcmeClient client;
      client.RequestCertificate();
   }
}
