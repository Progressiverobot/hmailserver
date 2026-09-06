// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// ACME v2 (RFC 8555) client. See AcmeClient.h.
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "AcmeClient.h"
#include "FileUtilities.h"
#include "OtelTracer.h"
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
#include "../TCPIP/SslContextInitializer.h"

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
      const int HttpsTimeoutMilliseconds = 30000;
      const size_t MaxResponseSize = 1024 * 1024;
      const int PollIntervalMilliseconds = 2000;
      const int MaxPollAttempts = 30;

      // How close to expiry a failed renewal stops being a log line and becomes a
      // reported error. Renewal now begins a third of the certificate's lifetime
      // before it expires, so a single failure has weeks of slack on a 90-day
      // certificate and days on a 47-day one; an ERROR entry for that would teach an
      // administrator to ignore the error log. Inside this window the certificate is
      // about to stop working, which is a different thing entirely.
      //
      // Fixed rather than proportional, unlike the renewal decision above it: this is
      // about how long a human needs to react, and that does not shrink because
      // certificates did.
      const int ImminentExpiryDays = 7;

      // Reads the notAfter of the first certificate in a PEM file as a time_t.
      // False when there is no file, it does not parse, or the date does not
      // convert - all of which callers treat as "no usable certificate" rather than
      // as a certificate with a known expiry.
      // Both dates, because the renewal decision needs the certificate's LIFETIME
      // and not just its expiry. notBefore is returned as 0 when it cannot be read,
      // which the caller treats as "assume the historical 90-day lifetime" rather
      // than as a failure - a certificate with an unreadable notBefore still has a
      // perfectly good notAfter to renew against.
      bool ReadCertificateDates(const String &certificateFile, time_t &notBefore, time_t &notAfter)
      {
         notBefore = 0;
         notAfter = 0;

         if (!FileUtilities::Exists(certificateFile))
            return false;

         AnsiString narrowFileName = certificateFile;

         BIO *bio = BIO_new_file(narrowFileName.c_str(), "r");
         if (bio == nullptr)
            return false;

         X509 *certificate = PEM_read_bio_X509(bio, nullptr, nullptr, nullptr);
         BIO_free(bio);

         if (certificate == nullptr)
            return false;

         tm notAfterTm = {};
         bool parsed = false;

         if (ASN1_TIME_to_tm(X509_get0_notAfter(certificate), &notAfterTm) == 1)
         {
            time_t converted = _mkgmtime(&notAfterTm);

            if (converted != -1)
            {
               notAfter = converted;
               parsed = true;
            }
         }

         tm notBeforeTm = {};

         if (ASN1_TIME_to_tm(X509_get0_notBefore(certificate), &notBeforeTm) == 1)
         {
            time_t converted = _mkgmtime(&notBeforeTm);

            if (converted != -1)
               notBefore = converted;
         }

         X509_free(certificate);

         return parsed;
      }

      // Kept for the callers that only care when the certificate dies.
      bool ReadCertificateNotAfter(const String &certificateFile, time_t &notAfter)
      {
         time_t ignoredNotBefore = 0;

         return ReadCertificateDates(certificateFile, ignoredNotBefore, notAfter);
      }

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


   time_t
   AcmeClient::ParseRfc3339(const AnsiString &value)
   {
      // "2026-01-02T15:04:05Z", optionally with fractional seconds before the Z.
      // Deliberately strict about the shape rather than tolerant: this decides when a
      // certificate is renewed, and a timestamp half-understood is worse than one
      // rejected, because rejecting it falls back to a calculation that works.
      if (value.GetLength() < 20)
         return 0;

      tm parsed = {};

      int year = 0, month = 0, day = 0, hour = 0, minute = 0, second = 0;

      if (sscanf_s(value.c_str(), "%4d-%2d-%2dT%2d:%2d:%2d",
                   &year, &month, &day, &hour, &minute, &second) != 6)
         return 0;

      if (year < 1970 || month < 1 || month > 12 || day < 1 || day > 31)
         return 0;

      parsed.tm_year = year - 1900;
      parsed.tm_mon = month - 1;
      parsed.tm_mday = day;
      parsed.tm_hour = hour;
      parsed.tm_min = minute;
      parsed.tm_sec = second;

      time_t converted = _mkgmtime(&parsed);

      return converted == -1 ? 0 : converted;
   }

   AnsiString
   AcmeClient::BuildAriId(const unsigned char *keyIdentifier, int keyIdentifierLength,
                          const unsigned char *serialDer, int serialDerLength)
   {
      if (keyIdentifier == nullptr || keyIdentifierLength <= 0 ||
          serialDer == nullptr || serialDerLength <= 0)
         return "";

      // RFC 9773 4.1: base64url of the AKI keyIdentifier, a dot, base64url of the
      // serial's DER content octets. Unpadded, which Base64Url_ already handles.
      return Base64Url_(keyIdentifier, keyIdentifierLength) + "." +
             Base64Url_(serialDer, serialDerLength);
   }

   bool
   AcmeClient::GetCertificateAriId(const String &certificateFile, AnsiString &ariId)
   {
      ariId = "";

      if (!FileUtilities::Exists(certificateFile))
         return false;

      AnsiString narrowFileName = certificateFile;

      BIO *bio = BIO_new_file(narrowFileName.c_str(), "r");
      if (bio == nullptr)
         return false;

      X509 *certificate = PEM_read_bio_X509(bio, nullptr, nullptr, nullptr);
      BIO_free(bio);

      if (certificate == nullptr)
         return false;

      bool built = false;

      const ASN1_OCTET_STRING *keyIdentifier = X509_get0_authority_key_id(certificate);
      const ASN1_INTEGER *serial = X509_get0_serialNumber(certificate);

      if (keyIdentifier != nullptr && serial != nullptr)
      {
         // The serial has to be its DER CONTENT octets, and that is not the same as
         // the bytes OpenSSL keeps in the ASN1_INTEGER. DER encodes an INTEGER in
         // two's complement, so a positive serial whose top bit is set carries a
         // leading zero byte to keep it positive - RFC 9773's own example is serial
         // 0x8765432101 encoding as 00 87 65 43 21 - while ASN1_STRING_get0_data
         // hands back the magnitude without it. Getting that wrong costs nothing
         // visible: the identifier is simply one the CA has never issued, it answers
         // 404, and this server quietly falls back to deciding for itself. Which is
         // to say it would have been wrong for about half of all certificates and
         // nobody would have noticed. So the rule is applied explicitly below.
         BIGNUM *serialNumber = ASN1_INTEGER_to_BN(serial, nullptr);

         if (serialNumber != nullptr)
         {
            std::vector<unsigned char> serialBytes;

            const int magnitudeLength = BN_num_bytes(serialNumber);

            if (magnitudeLength == 0)
            {
               // Serial zero. Not legal in a public certificate, but DER encodes it
               // as a single zero octet rather than as nothing, and an empty
               // component would produce an identifier with a dot and no serial.
               serialBytes.push_back(0x00);
            }
            else
            {
               serialBytes.resize(magnitudeLength);
               BN_bn2bin(serialNumber, &serialBytes[0]);

               // The rule this exists for: DER writes an INTEGER in two's
               // complement, so a positive value whose top bit is set needs a
               // leading zero octet or it would read as negative.
               if ((serialBytes[0] & 0x80) != 0)
                  serialBytes.insert(serialBytes.begin(), 0x00);
            }

            BN_free(serialNumber);

            ariId = BuildAriId(ASN1_STRING_get0_data(keyIdentifier), ASN1_STRING_length(keyIdentifier),
                               &serialBytes[0], (int) serialBytes.size());

            built = !ariId.IsEmpty();
         }
      }

      X509_free(certificate);

      return built;
   }

   time_t
   AcmeClient::GetRenewalTimeInWindow(const AnsiString &ariId, time_t windowStart, time_t windowEnd)
   {
      if (windowStart <= 0)
         return 0;

      if (windowEnd <= windowStart)
         return windowStart;

      // A stable point in the window, derived from the certificate's identifier.
      // Any cheap spreading function does; this one is FNV-1a because it is four
      // lines and has no dependencies.
      unsigned __int64 hash = 14695981039346656037ULL;

      for (int i = 0; i < ariId.GetLength(); i++)
      {
         hash ^= (unsigned char) ariId.GetAt(i);
         hash *= 1099511628211ULL;
      }

      const time_t span = windowEnd - windowStart;

      return windowStart + (time_t) (hash % (unsigned __int64) span);
   }

   bool
   AcmeClient::FetchSuggestedRenewalTime_(time_t &renewAt)
   {
      renewAt = 0;

      if (url_renewal_info_.IsEmpty())
         return false;

      AnsiString ariId;

      if (!GetCertificateAriId(GetCertificateDirectory() + _T("\\fullchain.pem"), ariId))
         return false;

      AnsiString url = url_renewal_info_;

      if (url.Right(1) != "/")
         url += "/";

      url += ariId;

      HttpResponse response;

      if (!Transact_(url, "GET", "", response) || response.status_code != 200)
         return false;

      time_t windowStart = ParseRfc3339(JsonStringValue_(response.body, "start"));
      time_t windowEnd = ParseRfc3339(JsonStringValue_(response.body, "end"));

      if (windowStart <= 0)
         return false;

      renewAt = GetRenewalTimeInWindow(ariId, windowStart, windowEnd);

      return renewAt > 0;
   }

   time_t
   AcmeClient::GetRenewalTime(time_t notBefore, time_t notAfter)
   {
      if (notAfter <= 0)
         return 0;

      // An unreadable or nonsensical notBefore is treated as the 90-day lifetime
      // this server has always assumed, rather than as a reason to refuse to renew.
      time_t lifetime = (notBefore > 0 && notAfter > notBefore) ? (notAfter - notBefore)
                                                               : static_cast<time_t>(90) * 86400;

      // Two thirds through. The remaining third is the margin, and this server
      // escalates a failing renewal to the error log well inside it.
      time_t renewAt = notBefore > 0 ? notBefore + (lifetime / 3) * 2
                                     : notAfter - lifetime / 3;

      // A floor, for the very short lifetimes the industry is heading towards and
      // for anything experimental below them: never leave less than a day, because a
      // renewal that fails wants at least one more scheduled attempt before the
      // certificate dies, and the task runs hourly.
      time_t latest = notAfter - 86400;

      if (renewAt > latest)
         renewAt = latest;

      // ...and a certificate whose whole life is shorter than that floor is renewed
      // from the moment it is issued, which is the only honest answer.
      if (renewAt < notBefore)
         renewAt = notBefore;

      return renewAt;
   }

   bool
   AcmeClient::ShouldRenewNow()
   {
      time_t notBefore = 0;
      time_t notAfter = 0;

      if (!ReadCertificateDates(GetCertificateDirectory() + _T("\\fullchain.pem"), notBefore, notAfter))
         return true;

      const time_t now = time(nullptr);

      // The CA's opinion, if it has one and will tell us.
      if (FetchDirectory_())
      {
         time_t suggested = 0;

         if (FetchSuggestedRenewalTime_(suggested))
         {
            // Never later than a day before expiry, whatever the CA says. ARI is
            // advisory and the certificate is not: RFC 9773 has the client respect
            // the window but still renew in time, and a CA that returns a window
            // past this certificate's own expiry - through a bug, a stale record or
            // an identifier collision - must not be able to take TLS down here.
            const time_t latest = notAfter - 86400;

            if (suggested > latest)
               suggested = latest;

            return now >= suggested;
         }
      }

      return now >= GetRenewalTime(notBefore, notAfter);
   }

   bool
   AcmeClient::Transact_(const AnsiString &url, const AnsiString &method, const AnsiString &payload, HttpResponse &response)
   {
      // OpenTelemetry: a client span for the outbound request, whose traceparent
      // is emitted with the request headers below. No-op unless OtelEndpoint is
      // configured.
      OtelSpanScope otelSpan("http.request", OtelSpanKindClient, AnsiString());

      try
      {
         AnsiString host;
         AnsiString port;
         AnsiString path;

         if (!ParseHttpsUrl(url, host, port, path))
            return false;

         otelSpan.AddAttribute("http.host", host);

         boost::asio::io_context ioContext;

         boost::asio::ip::tcp::resolver resolver(ioContext);
         boost::asio::ip::tcp::resolver::results_type endpoints =
            resolver.resolve(std::string(host.c_str()), std::string(port.c_str()));

         boost::asio::ssl::context sslContext(boost::asio::ssl::context::tls_client);
         sslContext.set_default_verify_paths();

         // Through the shared client initialiser, for the same reason the optional
         // HTTP listeners were moved onto the shared server one: this context was
         // built here and configured nowhere, so the enabled TLS versions, the
         // cipher list and the key-exchange groups did not apply to it. The
         // connection that fetches this server's own certificate was the least
         // configured TLS in the build. InitClient does not touch the verify mode
         // or the verify callback set below, so peer verification is unaffected.
         SslContextInitializer::InitClient(sslContext);

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

         // W3C trace context: propagate this request's span. Empty (and no
         // header at all) when tracing is disabled.
         AnsiString otelTraceparent = otelSpan.GetTraceparentValue();
         if (!otelTraceparent.IsEmpty())
         {
            request.append("traceparent: ");
            request.append(otelTraceparent);
            request.append("\r\n");
         }

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

   // static
   int
   AcmeClient::FindChallengeOfType(const AnsiString &json, const AnsiString &challengeType)
   {
      // Accepts '"type":"http-01"', '"type": "http-01"' and any other amount
      // of whitespace around the colon. A bare mention of the type string
      // elsewhere - an error detail, some other key's value - does not count:
      // the value must be preceded, across whitespace, by a colon and the
      // "type" key.
      AnsiString typeValue = "\"" + challengeType + "\"";
      const AnsiString typeKey = "\"type\"";

      int searchFrom = 0;
      for (;;)
      {
         int valuePosition = json.Find(typeValue, searchFrom);
         if (valuePosition < 0)
            return -1;

         int position = valuePosition - 1;

         while (position >= 0 && (json[position] == ' ' || json[position] == '\t' ||
                                  json[position] == '\r' || json[position] == '\n'))
            position--;

         if (position >= 0 && json[position] == ':')
         {
            position--;

            while (position >= 0 && (json[position] == ' ' || json[position] == '\t' ||
                                     json[position] == '\r' || json[position] == '\n'))
               position--;

            int keyStart = position - typeKey.GetLength() + 1;
            if (keyStart >= 0 && json.Mid(keyStart, typeKey.GetLength()) == typeKey)
               return valuePosition;
         }

         searchFrom = valuePosition + 1;
      }
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

      // RFC 9773. Optional, and its absence is not an error: a CA that does not
      // implement ARI simply does not advertise it, and this server then decides for
      // itself when to renew exactly as it did before.
      url_renewal_info_ = JsonStringValue_(response.body, "renewalInfo");

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
      // Every failure below says which step failed. They used to return false in
      // silence, so a renewal could fail with nothing at all in the log between
      // "Requesting a new certificate" and the next hourly attempt saying the same
      // thing - and no way for an administrator to tell a DNS problem from a
      // firewalled port 80 from a CA rate limit.
      //
      // Application log rather than reported errors: each of these is one step of
      // one attempt, the attempt is retried in an hour, and the outcome that
      // actually matters - a renewal still failing while the certificate in use is
      // about to expire - is reported once, by AcmeRenewalTask::DoWork.

      // POST-as-GET: empty string payload.
      HttpResponse authorizationResponse;
      if (!SignedPost_(authorizationUrl, "", authorizationResponse) || authorizationResponse.status_code != 200)
      {
         String message;
         message.Format(_T("ACME: Could not read the authorization (HTTP status %d). URL: %s"),
            authorizationResponse.status_code, String(authorizationUrl).c_str());
         LOG_APPLICATION(message);

         return false;
      }

      // The identifier this authorization is for, so every message below can
      // say WHICH domain failed - the log used to leave an administrator with
      // three configured hostnames and no way to tell them apart (issue #34).
      // The identifier object is the first thing in the authorization, so the
      // first "value" key is its value.
      String domain = String(JsonStringValue_(authorizationResponse.body, "value"));
      if (domain.IsEmpty())
         domain = _T("(unknown)");

      AnsiString status = JsonStringValue_(authorizationResponse.body, "status");
      if (status == "valid")
         return true;

      // Locate the http-01 challenge object. Whitespace-tolerantly: boulder
      // pretty-prints its JSON ('"type": "http-01"', with a space), so the
      // compact-form search this replaces never matched a real Let's Encrypt
      // response - every issuance failed right here with a message that blamed
      // the CA (issue #34).
      int challengePosition = FindChallengeOfType(authorizationResponse.body, "http-01");
      if (challengePosition < 0)
      {
         // The raw response is the only evidence of what the CA actually
         // offered; the reporter of issue #34 could not root-cause from the log
         // precisely because it was not included.
         LOG_APPLICATION(Formatter::Format("ACME: Authorization for {0} offers no http-01 challenge. Response: {1}",
            domain, String(authorizationResponse.body.Mid(0, 500))));
         return false;
      }

      AnsiString challengeObject = ExtractEnclosingObject(authorizationResponse.body, challengePosition);

      AnsiString challengeUrl = JsonStringValue_(challengeObject, "url");
      AnsiString token = JsonStringValue_(challengeObject, "token");

      if (challengeUrl.IsEmpty() || token.IsEmpty())
      {
         LOG_APPLICATION(Formatter::Format("ACME: The http-01 challenge for {0} carried no url or no token, so it cannot be answered.", domain));
         return false;
      }

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
      {
         LOG_APPLICATION(Formatter::Format("ACME: Could not ask the CA to validate the http-01 challenge for {0}.", domain));
         return false;
      }

      // Poll the authorization until it leaves the pending state.
      for (int attempt = 0; attempt < MaxPollAttempts; attempt++)
      {
         Sleep(PollIntervalMilliseconds);

         HttpResponse pollResponse;
         if (!SignedPost_(authorizationUrl, "", pollResponse) || pollResponse.status_code != 200)
         {
            String message;
            message.Format(_T("ACME: Could not read the authorization while waiting for validation (HTTP status %d)."),
               pollResponse.status_code);
            LOG_APPLICATION(message);

            return false;
         }

         status = JsonStringValue_(pollResponse.body, "status");

         if (status == "valid")
            return true;

         if (status == "invalid")
         {
            LOG_APPLICATION(Formatter::Format("ACME: Challenge validation failed for {0}: {1}", domain, String(pollResponse.body.Mid(0, 500))));
            return false;
         }
      }

      LOG_APPLICATION(Formatter::Format("ACME: Timed out waiting for challenge validation of {0}.", domain));
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
         LOG_APPLICATION("ACME: Could not build the certificate signing request.");
         EVP_PKEY_free(domainKey);
         return false;
      }

      AnsiString payload = "{\"csr\":\"" + Base64Url_(csrDer.data(), (int) csrDer.size()) + "\"}";

      HttpResponse finalizeResponse;
      if (!SignedPost_(finalizeUrl, payload, finalizeResponse) || finalizeResponse.status_code != 200)
      {
         // The CA's reply body carries the ACME problem document, which is the one
         // thing that says *why* - a rate limit, a CAA record, an unauthorized
         // identifier. It was discarded.
         LOG_APPLICATION(Formatter::Format("ACME: The CA rejected the certificate request (HTTP status {0}). Response: {1}",
            finalizeResponse.status_code, String(finalizeResponse.body.Mid(0, 500))));

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
            LOG_APPLICATION(Formatter::Format("ACME: Could not read the order while waiting for the certificate to be issued (HTTP status {0}).",
               orderResponse.status_code));

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
         // Either the order never left the "processing" state within the polling
         // window, or it went valid without naming a certificate. Both used to be
         // silent.
         LOG_APPLICATION("ACME: The order did not produce a certificate URL within the polling window.");

         EVP_PKEY_free(domainKey);
         return false;
      }

      // Download the certificate chain.
      HttpResponse certificateResponse;
      if (!SignedPost_(certificateUrl, "", certificateResponse) || certificateResponse.status_code != 200)
      {
         LOG_APPLICATION(Formatter::Format("ACME: Could not download the issued certificate (HTTP status {0}).",
            certificateResponse.status_code));

         EVP_PKEY_free(domainKey);
         return false;
      }

      // Both halves of the new pair are written under temporary names and moved
      // into place only once both writes have succeeded.
      //
      // The order used to be privkey.pem straight over the live key and then
      // fullchain.pem, and a failure on the second write - a full disk, a scanner
      // or a backup holding the file open - left the directory holding the *new*
      // private key beside the *previous* certificate. That pair does not match,
      // and a mismatched pair is not a degraded listener: OpenSSL refuses the key,
      // InitServer returns false, and every port using the ACME certificate stops
      // listening at the next restart. A renewal failure that takes SMTP, IMAP and
      // POP3 down is a worse outcome than the expiry it was trying to avoid.
      //
      // Two renames are not one atomic operation, but FileUtilities::Move is a
      // replacing rename - the destination always names either the old file or the
      // new one, never nothing - and a rename does not fail half-way the way a
      // write does. The key is moved first so that the certificate is never the
      // newer of the two: a certificate whose key has not arrived yet is the same
      // mismatch in the other direction.
      String directory = GetCertificateDirectory();
      String keyFile = directory + _T("\\privkey.pem");
      String certificateFile = directory + _T("\\fullchain.pem");
      String pendingKeyFile = keyFile + _T(".new");
      String pendingCertificateFile = certificateFile + _T(".new");

      AnsiString narrowPendingKeyFile = pendingKeyFile;

      bool keyWritten = false;

      BIO *bio = BIO_new_file(narrowPendingKeyFile.c_str(), "w");
      if (bio != nullptr)
      {
         keyWritten = PEM_write_bio_PrivateKey(bio, domainKey, nullptr, nullptr, 0, nullptr, nullptr) == 1;
         BIO_free(bio);
      }

      EVP_PKEY_free(domainKey);

      if (!keyWritten)
      {
         LOG_APPLICATION(Formatter::Format("ACME: Could not write the new private key to {0}. The certificate and key in use are unchanged.", pendingKeyFile));
         return false;
      }

      if (!FileUtilities::WriteToFile(pendingCertificateFile, certificateResponse.body))
      {
         LOG_APPLICATION(Formatter::Format("ACME: Could not write the issued certificate to {0}. The certificate and key in use are unchanged.", pendingCertificateFile));

         // Leaving this behind would not break anything - nothing reads a .new
         // file - but it would be mistaken for a renewal in progress by the next
         // person to look in the directory.
         FileUtilities::DeleteFile(pendingKeyFile);
         return false;
      }

      if (!FileUtilities::Move(pendingKeyFile, keyFile))
      {
         LOG_APPLICATION(Formatter::Format("ACME: Could not replace {0}. The certificate and key in use are unchanged.", keyFile));

         FileUtilities::DeleteFile(pendingKeyFile);
         FileUtilities::DeleteFile(pendingCertificateFile);
         return false;
      }

      if (!FileUtilities::Move(pendingCertificateFile, certificateFile))
      {
         // The one genuinely bad outcome left, and the reason this is an error
         // rather than a log line: the key has been replaced and the certificate
         // has not, so the pair on disk does not match and every listener using it
         // will refuse to start. Say so explicitly, with the file to look at,
         // because the fix is manual.
         ErrorManager::Instance()->ReportError(ErrorManager::High, 5993, "AcmeClient::FinalizeOrder_",
            Formatter::Format("The new private key was installed as {0} but the certificate could not be moved from {1} to {2}. The key and certificate on disk no longer match, and any listener using them will refuse to start. Move {1} into place by hand, or restore the pair from a backup.",
               keyFile, pendingCertificateFile, certificateFile));

         return false;
      }

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

      // The three checks below are the CA answering 201 with a body this client
      // cannot use. Each returned false without a word, which is the worst kind of
      // failure to diagnose remotely: the log said an order had been created and
      // then said nothing at all. The response is included because it is the only
      // evidence of what the CA actually sent.
      if (orderUrl.IsEmpty() || finalizeUrl.IsEmpty())
      {
         LOG_APPLICATION(Formatter::Format("ACME: The new order is missing its location header or its finalize URL. Response: {0}",
            String(orderResponse.body.Mid(0, 500))));
         return false;
      }

      // Complete every authorization in the order.
      int searchPosition = orderResponse.body.Find("\"authorizations\"");
      if (searchPosition < 0)
      {
         LOG_APPLICATION(Formatter::Format("ACME: The new order lists no authorizations. Response: {0}",
            String(orderResponse.body.Mid(0, 500))));
         return false;
      }

      int arrayStart = orderResponse.body.Find("[", searchPosition);
      int arrayEnd = orderResponse.body.Find("]", arrayStart);

      if (arrayStart < 0 || arrayEnd < 0)
      {
         LOG_APPLICATION(Formatter::Format("ACME: The authorization list in the new order could not be parsed. Response: {0}",
            String(orderResponse.body.Mid(0, 500))));
         return false;
      }

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

      // Make the new certificate take effect without manual steps. This runs
      // before the TLSA line below, which it used to follow: the certificate had
      // been issued and written, and the only thing standing between that and the
      // listeners using it was an optional convenience for the minority publishing
      // DANE records. Anything that went wrong in there - and it parses the freshly
      // written PEM with OpenSSL - took the deployment with it, leaving an
      // installation with a valid certificate on disk, no "ACME (automatic)"
      // record, and ports still without a certificate.
      ApplyCertificate_();

      // Publish-ready DANE record for administrators running inbound DANE.
      // Non-fatal by construction: nothing after it depends on it.
      try
      {
         AnsiString spkiHex;
         if (GetCertificateTlsa(GetCertificateDirectory() + _T("\\fullchain.pem"), spkiHex))
            LOG_APPLICATION("ACME: DANE TLSA record for this certificate: _25._tcp.<mx-host>. IN TLSA 3 1 1 " + String(spkiHex));
      }
      catch (...)
      {
         LOG_APPLICATION("ACME: The DANE TLSA record for the new certificate could not be computed. The certificate itself has been issued and applied.");
      }

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

      // X509_get_X509_PUBKEY returns the certificate's SubjectPublicKeyInfo, or a
      // null pointer when the certificate carries none that OpenSSL was able to
      // keep. The result was passed straight into i2d_X509_PUBKEY, which
      // dereferences it - a null dereference inside OpenSSL, so a crash of the
      // whole service rather than an exception, and this runs immediately after a
      // successful ACME issuance.
      X509_PUBKEY *publicKeyInfo = X509_get_X509_PUBKEY(certificate);

      int derLength = publicKeyInfo == nullptr ? 0 : i2d_X509_PUBKEY(publicKeyInfo, nullptr);

      if (derLength > 0)
      {
         std::vector<unsigned char> der(derLength);
         unsigned char *writePointer = der.data();

         // Checked as well: a second encoding that fails leaves the buffer holding
         // whatever it was initialized with, and hashing that produces a TLSA record
         // an administrator would publish and then wonder why DANE validation fails.
         if (i2d_X509_PUBKEY(publicKeyInfo, &writePointer) == derLength)
         {
            unsigned char digest[SHA256_DIGEST_LENGTH];

            if (SHA256(der.data(), derLength, digest) != nullptr)
            {
               for (int i = 0; i < SHA256_DIGEST_LENGTH; i++)
               {
                  AnsiString hexByte;
                  hexByte.Format("%02x", digest[i]);
                  spki_sha256_hex += hexByte;
               }

               success = true;
            }
         }
      }

      X509_free(certificate);

      if (!success)
         spki_sha256_hex = "";

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

         // The save was unchecked and the log line below ran either way, so a failure
         // here wrote "Assigned the certificate to port N" into the application log for
         // a port that still had no certificate - and the renewal that follows restarts
         // the TCP servers, which is exactly when someone reads this log to confirm the
         // new certificate went live. A port left without one accepts no TLS at all.
         if (!PersistentTCPIPPort::SaveObject(port, errorMessage, PersistenceModeNormal))
         {
            ErrorManager::Instance()->ReportError(ErrorManager::High, 6100, "AcmeClient::ApplyCertificate_",
               Formatter::Format("ACME: the certificate could NOT be assigned to port {0} - it has been left without one and will not accept TLS connections. {1}",
                  port->GetPortNumber(), errorMessage));

            continue;
         }

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

      AcmeClient client;

      if (!client.ShouldRenewNow())
         return;

      LOG_APPLICATION("ACME: Certificate is missing or due for renewal. Requesting a new certificate.");

      if (client.RequestCertificate())
         return;

      // Why the return value is looked at at all now: it was discarded. A renewal
      // that failed left exactly one line in the log - the one above, announcing
      // that a renewal was about to be attempted - and nothing anywhere saying it
      // had not happened. An hour later the task ran again and said the same thing,
      // so the log read like a renewal permanently in progress right up to the
      // moment the certificate expired and TLS stopped working. Automatic renewal
      // whose failure is invisible is worse than manual renewal, because nobody is
      // watching the calendar either.
      LOG_APPLICATION("ACME: Certificate renewal FAILED. The failing step is in the lines above; the next attempt is in one hour. The certificate currently installed has not been changed.");

      String certificateFile = AcmeClient::GetCertificateDirectory() + _T("\\fullchain.pem");

      time_t notAfter = 0;

      if (!ReadCertificateNotAfter(certificateFile, notAfter))
      {
         // Nothing usable in the directory at all: no expiry to run out, but also
         // no certificate for the listeners that were pointed at this directory,
         // and ACME is switched on precisely so that there would be one. Reported
         // rather than logged because an operator who enabled ACME will not go
         // looking for the reason it never produced anything.
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5992, "AcmeRenewalTask::DoWork",
            Formatter::Format("ACME is enabled but certificate issuance failed and there is no usable certificate in {0}. Any listener configured to use that pair has no certificate. The failing step is in the hMailServer application log.", certificateFile));

         return;
      }

      // An error, not just a log line, once the certificate this renewal was meant
      // to replace is close enough to expiry that continued failure means TLS
      // stops working. Deliberately not reported for every failure: renewal begins
      // 30 days out, and an ERROR entry for a failure with four weeks of slack left
      // would train an administrator to ignore the error log. Cannot fire on a
      // default installation - AcmeEnabled is 0 and DoWork returns above.
      time_t secondsRemaining = notAfter - time(nullptr);

      if (secondsRemaining > static_cast<time_t>(ImminentExpiryDays) * 86400)
         return;

      if (secondsRemaining <= 0)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 5992, "AcmeRenewalTask::DoWork",
            Formatter::Format("ACME renewal failed and the certificate {0} has already expired. TLS clients are refusing this server's certificate now. The failing step is in the hMailServer application log.", certificateFile));

         return;
      }

      ErrorManager::Instance()->ReportError(ErrorManager::High, 5992, "AcmeRenewalTask::DoWork",
         Formatter::Format("ACME renewal failed and the certificate {0} expires in less than {1} day(s). TLS will stop working when it does. The failing step is in the hMailServer application log.",
            certificateFile, static_cast<int>(secondsRemaining / 86400) + 1));
   }
}
