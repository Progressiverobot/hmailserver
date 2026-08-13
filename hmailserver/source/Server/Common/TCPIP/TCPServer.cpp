// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"
#include "TCPServer.h"
#include "TCPConnection.h"

#include <chrono>
#include <ctime>
#include <iostream>
#include <memory>
#include <string>

#include "../Scripting/ScriptServer.h"
#include "../Scripting/ScriptObjectContainer.h"
#include "../Scripting/Result.h"
#include "../Scripting/ClientInfo.h"
#include "../Persistence/PersistentSecurityRange.h"

#include "../Application/SessionManager.h"

#include "../Util/Encoding/Base64.h"

#include "SslContextInitializer.h"

using boost::asio::ip::tcp;

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      /*
         Owns the session slot that SessionManager::CreateSession has just taken,
         for the short window before the TCPConnection takes over responsibility
         for it.

         That window used to be unguarded, and the slot was handed back on exactly
         one path - the script refusing the connection. Anything else that left the
         function in between kept the slot for good, and FireOnAcceptEvent runs
         administrator-supplied script through the COM scripting host, which is the
         one thing in the accept path that can raise something we did not write.
         A leaked slot never comes back: it counts against MaxSMTPConnections (and
         the POP3 and IMAP equivalents) with no connection open behind it, so enough
         of them and every client is refused with "Blocked either by IP range or by
         connection limit" until the service is restarted.
      */
      class SessionSlot
      {
      public:
         explicit SessionSlot(SessionType session_type) :
            session_type_(session_type),
            held_(true)
         {
         }

         ~SessionSlot()
         {
            if (!held_)
               return;

            try
            {
               SessionManager::Instance()->OnSessionEnded(session_type_);
            }
            catch (...)
            {
               // This can run while an exception is already in flight, and a second
               // one leaving a destructor is std::terminate.
            }
         }

         // Ownership has passed to the TCPConnection, whose destructor releases the
         // slot for any connection that got past StatePendingConnect.
         void Release()
         {
            held_ = false;
         }

         SessionSlot(const SessionSlot&) = delete;
         SessionSlot& operator=(const SessionSlot&) = delete;

      private:
         SessionType session_type_;
         bool held_;
      };
   }

   TCPServer::TCPServer(boost::asio::io_context& io_context, const IPAddress &ipaddress, int port, SessionType sessionType, std::shared_ptr<SSLCertificate> certificate, std::shared_ptr<TCPConnectionFactory> connectionFactory, ConnectionSecurity connection_security) :
      acceptor_(io_context),
      context_(boost::asio::ssl::context::sslv23),
      io_context_(io_context),
      ipaddress_(ipaddress),
      port_(port),
      connection_security_(connection_security)
   {
      sessionType_ = sessionType;
      certificate_ = certificate;
      connectionFactory_ = connectionFactory;
   }

   TCPServer::~TCPServer(void)
   {
      
   }

   bool
   TCPServer::InitAcceptor()
   {
      boost::asio::ip::tcp::endpoint endpoint(ipaddress_.GetAddress(), port_);

      try
      {
         acceptor_.open(endpoint.protocol());
         acceptor_.set_option(boost::asio::ip::tcp::acceptor::reuse_address(false));
      }
      catch (boost::system::system_error error)
      {
         String msg = error.what();

         String sErrorMessage;
         sErrorMessage.Format(_T("Failed to initialize local acceptor. Error: %s. Address: %s, Port: %d."), 
            msg.c_str(), String(ipaddress_.ToString()).c_str(), port_);

         ErrorManager::Instance()->ReportError(ErrorManager::High, 4316, "TCPServer::Run()", sErrorMessage);

         return false;
      }

      try
      {
         acceptor_.bind(endpoint);

      }
      catch (boost::system::system_error error)
      {
         String msg = error.what();

         String sErrorMessage = Formatter::Format("Failed to bind to local port. Error: {0}. Address: {1}, Error code: {2}, Port: {3}. This is often caused by another server listening on the same port."
                                                  "To determine which server is listening on the port, telnet your server on port {4}. Make sure that no other email server is running "
                                                  "and listening on this port, and then restart the hMailServer service.", 
                                                   msg, String(ipaddress_.ToString()), error.code().value(), port_, port_);

         ErrorManager::Instance()->ReportError(ErrorManager::High, 4316, "TCPServer::Run()", sErrorMessage);

         return false;
      }

      try
      {
         acceptor_.listen();
      }
      catch (boost::system::system_error error)
      {
         String msg = error.what();

         String sErrorMessage = Formatter::Format("Failed to listen on local port. Error: {0}. Error code: {1}, Address: {2}, Port: {3}",
                                          msg, error.code().value(), String(ipaddress_.ToString()), port_);

         ErrorManager::Instance()->ReportError(ErrorManager::High, 4317, "TCPServer::Run()", sErrorMessage);

         return false;
      }

      return true;


   }
  

   void
   TCPServer::Run()
   {
      if (connection_security_ == CSSSL ||
          connection_security_ == CSSTARTTLSOptional ||
          connection_security_ == CSSTARTTLSRequired)
      {
         if (!SslContextInitializer::InitServer(context_, certificate_, ipaddress_.ToString(), port_))
            return;
      }

      if (!InitAcceptor())
         return;

      StartAccept();
   }

   void
   TCPServer::StartAccept()
   {
      if (acceptor_.is_open())
      {
        
         std::shared_ptr<TCPConnection> pNewConnection = connectionFactory_->Create(connection_security_, io_context_, context_);

         acceptor_.async_accept(pNewConnection->GetSocket(),
            std::bind(&TCPServer::HandleAccept, this, pNewConnection,
            std::placeholders::_1));
      }
   }
   
   void
   TCPServer::StopAccept()
   {
      // eat any errors thrown by cancel:
      boost::system::error_code error;
      acceptor_.cancel(error);

      acceptor_.close();
   }

   void 
   TCPServer::HandleAccept(std::shared_ptr<TCPConnection> connection,
      const boost::system::error_code &error)
   {
      if (error.value() == 995)
      {
         String sMessage;
         sMessage.Format(_T("TCP - AcceptEx failed. Error code: %d, Message: %s"), error.value(), String(error.message()).c_str());
         LOG_DEBUG(sMessage);

         /*
             995: The I/O operation has been aborted because of either a thread exit or an application request
             
             This happens when the servers are stopped. We shouldn't post any new accepts since we're shutting down.
        */

         return;
      }

      // Post another AcceptEx. We should always have outstanding unless the 
      // server is stopping, which we are taking care of above.
      StartAccept();

      if (!error)
      {
         /*
            Both endpoints are read through the error_code overloads, because the
            peer can be gone before we ever look at the socket we just accepted -
            a connect-scan that resets immediately is enough, and getpeername then
            answers WSAENOTCONN. The throwing overloads used to be called here, and
            this is a boost::asio completion handler: the exception unwinds out of
            io_context::run() onto the IOCP worker, where the barrier reports HM4208
            at High severity and the crash reporter writes a minidump. So one TCP
            connection, repeated, could fill a stock server's error log and burn the
            ten-dump / 100 MB budget that a genuine fault needs.
         */
         boost::system::error_code endpoint_error;

         boost::asio::ip::tcp::endpoint localEndpoint = connection->GetSocket().local_endpoint(endpoint_error);

         boost::asio::ip::tcp::endpoint remoteEndpoint;
         if (!endpoint_error)
            remoteEndpoint = connection->GetSocket().remote_endpoint(endpoint_error);

         if (endpoint_error)
         {
            String sEndpointMessage;
            sEndpointMessage.Format(_T("TCP - A connection on port %d was dropped before its address could be read. Code: %d, Message: %s"),
               port_, endpoint_error.value(), String(endpoint_error.message()).c_str());
            LOG_TCPIP(sEndpointMessage);

            // No session was created, so there is nothing to give back. Letting the
            // connection go closes the socket.
            return;
         }

         IPAddress localAddress (localEndpoint.address());
         IPAddress remoteAddress (remoteEndpoint.address());

         String sMessage = Formatter::Format("TCP - {0} connected to {1}:{2}.", remoteAddress.ToString(), localAddress.ToString(), port_);
         LOG_TCPIP(sMessage);

         std::shared_ptr<SecurityRange> securityRange = PersistentSecurityRange::ReadMatchingIP(remoteAddress);

         bool allow = SessionManager::Instance()->CreateSession(sessionType_, securityRange);
        
         if (!allow)
         {
            // Session creation failed. May not be matching IP range, or enough connections have been created.
            String message;
            message.Format(_T("Client connection from %s was not accepted. Blocked either by IP range or by connection limit."), String(remoteAddress.ToString()).c_str());
            LOG_DEBUG(message);

            // Give option to hold connection for anti-pounding & hopefully minimize DoS
            // NOTE: We really need max connections per IP as well
            int iBlockedIPHoldSeconds = IniFileSettings::Instance()->GetBlockedIPHoldSeconds();

            if (iBlockedIPHoldSeconds > 0)
            {
               /*
                  This was a Sleep() - on the I/O thread that had just accepted the
                  connection. There are only GetTCPIPThreads() of those and SMTP,
                  POP3 and IMAP all share them, so the setting handed the peer it was
                  meant to punish a way to stop the entire server: open one more
                  connection than there are threads, and every worker is asleep at
                  once. No accept, no read and no write for any protocol is
                  dispatched until they wake, and a peer that keeps reconnecting
                  keeps them that way. The anti-pounding value is in holding the
                  socket open, which a timer does just as well while holding nothing
                  else. The connection is captured so it stays open for the duration
                  and is closed when the handler releases the last reference to it.
               */
               std::shared_ptr<boost::asio::steady_timer> hold_timer =
                  std::make_shared<boost::asio::steady_timer>(io_context_);

               hold_timer->expires_after(std::chrono::seconds(iBlockedIPHoldSeconds));

               String held_message;
               held_message.Format(_T("Held connection from %s for %i seconds before dropping."), String(remoteAddress.ToString()).c_str(), iBlockedIPHoldSeconds);

               hold_timer->async_wait([hold_timer, connection, held_message](const boost::system::error_code&)
                  {
                     LOG_DEBUG(held_message);
                  });
            }

            return;
         }

         // The slot taken above belongs to this function until the connection is
         // started. See SessionSlot: the hand-back used to exist on one path only.
         SessionSlot session_slot(sessionType_);

         if (!FireOnAcceptEvent(connection, remoteAddress, localEndpoint.port()))
         {
            // Session has been created, but is now terminated by a custom script. Since we haven't started the
            // TCPConnection yet, we are still responsible for tracking connection count - session_slot gives it back.
            return;
         }

         connection->SetSecurityRange(securityRange);

         // Now TCPConnection is responsible for decreasing the session count when the connection ends:
         // its destructor releases a slot for every connection past StatePendingConnect, which Start()
         // sets before it does anything that can fail.
         session_slot.Release();

         connection->Start();
      }
      else
      {
         if (error.value() == 10009 || error.value() == 995)
         {
            // Not really errors..
            return;
         }

         // The outstanding accept-ex failed. This may or may not be an error. Default to being positive.
         String sMessage;
         sMessage.Format(_T("TCP - AcceptEx failed. Error code: %d, Message: %s"), error.value(), String(error.message()).c_str());
         LOG_TCPIP(sMessage);
      }
   }

   bool
   TCPServer::FireOnAcceptEvent(std::shared_ptr<TCPConnection> connection, const IPAddress &remoteAddress, int port)
   {
      // Fire an event...
      if (!Configuration::Instance()->GetUseScriptServer())
         return true;

      std::shared_ptr<ClientInfo> pCliInfo = std::shared_ptr<ClientInfo>(new ClientInfo);
      pCliInfo->SetIPAddress(remoteAddress.ToString());
      pCliInfo->SetPort(port);
      pCliInfo->SetSessionID(connection->GetSessionID());

      std::shared_ptr<ScriptObjectContainer> pContainer = std::shared_ptr<ScriptObjectContainer>(new ScriptObjectContainer);
      std::shared_ptr<Result> pResult = std::shared_ptr<Result>(new Result);

      pContainer->AddObject("Result", pResult, ScriptObject::OTResult);
      pContainer->AddObject("HMAILSERVER_CLIENT", pCliInfo, ScriptObject::OTClient);

      String sEventCaller;

      String sScriptLanguage = Configuration::Instance()->GetScriptLanguage();

      if (sScriptLanguage == _T("VBScript"))
         sEventCaller.Format(_T("OnClientConnect(HMAILSERVER_CLIENT)"));
      else if (sScriptLanguage == _T("JScript"))
         sEventCaller.Format(_T("OnClientConnect(HMAILSERVER_CLIENT);"));

      ScriptServer::Instance()->FireEvent(ScriptServer::EventOnClientConnect, sEventCaller, pContainer);

      switch (pResult->GetValue())
      {
      case 1:
         {
            // Disconnect the socket immediately.
            return false;
         }
      }

      return true;
   }
}