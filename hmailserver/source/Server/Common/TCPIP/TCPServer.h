// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include <ctime>
#include <iostream>

#include "SocketConstants.h"

#include "../BO/SSLCertificates.h"
#include "../BO/SSLCertificate.h"

#include "TCPConnectionFactory.h"

using boost::asio::ip::tcp;

namespace HM
{
   class TCPConnection;

   class TCPServer
   {
   public:
      TCPServer(boost::asio::io_context& io_context, const IPAddress &ipaddress, int port, SessionType sessionType, std::shared_ptr<SSLCertificate> certificate, std::shared_ptr<TCPConnectionFactory> connectionFactory, ConnectionSecurity connection_security,
         ClientCertificatePolicy client_certificate_policy, const String &client_certificate_ca_file);
      ~TCPServer(void);

      void Run();
      void StopAccept();

   private:
      
      std::string GetPassword() const;

      bool InitAcceptor();
      void StartAccept();
      void HandleAccept(std::shared_ptr<TCPConnection> connection, const boost::system::error_code &error);

      bool FireOnAcceptEvent(std::shared_ptr<TCPConnection> connection, const IPAddress &remoteAddress, int port);

      // Loads the port's client-CA bundle into the SSL context so that OpenSSL
      // can verify inbound client certificates against it. Returns false when
      // the listener must not start; see the definition for when that is.
      bool InitInboundClientCertificateVerification_();

      std::shared_ptr<TCPConnectionFactory> connectionFactory_;

      boost::asio::ip::tcp::acceptor acceptor_;
      boost::asio::ssl::context context_;
      boost::asio::io_context& io_context_;
      SessionType sessionType_;
      std::shared_ptr<SSLCertificate> certificate_;

      IPAddress ipaddress_;
      int port_;

      ConnectionSecurity connection_security_;

      // Mutual-TLS settings from the TCPIPPort this listener was built from.
      // The policy is handed to every accepted connection; the CA bundle is
      // loaded once into context_, which every connection's ssl stream shares.
      ClientCertificatePolicy client_certificate_policy_;
      String client_certificate_ca_file_;
   };
}