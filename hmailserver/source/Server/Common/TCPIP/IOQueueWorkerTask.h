// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once


#include "..\Threading\Task.h"

namespace HM
{
   class Socket;
   class SocketCompletionPort;

   class IOCPQueueWorkerTask : public Task
   {
   public:

      IOCPQueueWorkerTask(boost::asio::io_context &io_context);

      virtual void DoWork();
      void DoWorkInner();

   private:

      boost::asio::io_context &io_context_;
   };
}