// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "TCPConnection.h"


namespace HM
{
   class TCPConnectionFactory
   {
   public:
      virtual std::shared_ptr<TCPConnection> Create(ConnectionSecurity connection_security,
         boost::asio::io_context& io_context, 
         boost::asio::ssl::context& context) = 0;
   };

   class SMTPConnectionFactory : public TCPConnectionFactory
   {
   public:
      virtual std::shared_ptr<TCPConnection> Create(ConnectionSecurity connection_security,
         boost::asio::io_context& io_context, 
         boost::asio::ssl::context& context);
   };

   class POP3ConnectionFactory : public TCPConnectionFactory
   {
   public:
      virtual std::shared_ptr<TCPConnection> Create(ConnectionSecurity connection_security,
         boost::asio::io_context& io_context, 
         boost::asio::ssl::context& context);
   };

   class IMAPConnectionFactory : public TCPConnectionFactory
   {
   public:
      virtual std::shared_ptr<TCPConnection> Create(ConnectionSecurity connection_security,
         boost::asio::io_context& io_context, 
         boost::asio::ssl::context& context);
   };

}