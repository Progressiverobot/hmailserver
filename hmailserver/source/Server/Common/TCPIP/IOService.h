// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include <Boost\function.hpp>
#include "..\Application\SessionManager.h"
#include "..\Threading\Task.h"
#include "..\Util\Event.h"

#include "SocketConstants.h"



namespace HM
{
   class TCPServer;

   class IOService : public Task
   {
   public:
      IOService(void);
      ~IOService(void);

      void DoWork();

      void Initialize();

      // Session types
      bool RegisterSessionType(SessionType st);

      boost::asio::io_context &GetIOContext();
      boost::asio::ssl::context &GetClientContext();
   private:

      const String asynchronous_tasks_queue_;

      std::set<SessionType> session_types_;
      boost::asio::io_context io_context_;

      /*
         Keeps the I/O context alive across moments when it has no outstanding work.

         io_context::run() returns as soon as the last operation completes, and the
         worker task treats that return as "we are done" and exits the thread for
         good. On a server where every listener failed to bind - another mail server
         already on port 25 is the ordinary way to get there - or where SMTP, POP3
         and IMAP are all switched off, there is no outstanding accept from the
         start, so every TCP/IP thread exits within moments of being created.

         Nothing announces that, and the listeners are not the only user of this
         context: outbound SMTP delivery, the external POP3 fetcher, the
         SpamAssassin client and the connectivity diagnostics all post to it and
         then block on a disconnect event that only the connection's own destructor
         sets. With no thread left to run the connect, that event is never set and
         the delivery thread waits on it forever. Mail simply stops leaving, with
         the bind failures in the error log as the only clue and nothing at all
         linking the two.
      */
      boost::asio::executor_work_guard<boost::asio::io_context::executor_type> work_guard_;

      std::vector<std::shared_ptr<TCPServer> > tcp_servers_;

      boost::condition_variable do_work_dummy;

      boost::asio::ssl::context client_context_;
   };


}