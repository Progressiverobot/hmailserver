// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"
#include "SslContextInitializer.h"

#include "../BO/SSLCertificate.h"
#include "../Util/Encoding/Base64.h"
#include "../Util/Utilities.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   const int CertificateAlreadyInStore = 185057381;

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
         context.set_password_callback(std::bind(&SslContextInitializer::GetPassword_));
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


      return true;
   }

   bool
   SslContextInitializer::InitClient(boost::asio::ssl::context& context)
   { 
      SetContextOptions_(context);
         
      SetCipherList_(context);

      return true;
   }

   std::string 
   SslContextInitializer::GetPassword_()
   {
      ErrorManager::Instance()->ReportError(ErrorManager::High, 5143, "TCPServer::GetPassword()", "The private key file has a password. hMailServer does not support this.");
      return "";
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
   SslContextInitializer::SetContextOptions_(boost::asio::ssl::context& context)
   {
      bool tlsv10 = Configuration::Instance()->GetSslVersionEnabled(TlsVersion10);
      bool tlsv11 = Configuration::Instance()->GetSslVersionEnabled(TlsVersion11);
      bool tlsv12 = Configuration::Instance()->GetSslVersionEnabled(TlsVersion12);
      bool tlsv13 = Configuration::Instance()->GetSslVersionEnabled(TlsVersion13);

      bool tlsPreferServerCiphers = Configuration::Instance()->GetTlsOptionEnabled(TlsOptionPreferServerCiphers);
      bool tlsPrioritizeChaCha = Configuration::Instance()->GetTlsOptionEnabled(TlsOptionPrioritizeChaCha);

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