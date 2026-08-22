// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include ".\ExternalFetch.h"
#include "..\Common\BO\FetchAccount.h"
#include "../common/Util/Event.h"
#include "../Common/TCPIP/IOService.h"
#include "../Common/TCPIP/DNSResolver.h"
#include "../common/TCPIP/TCPConnection.h"
#include "POP3ClientConnection.h"


#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   ExternalFetch::ExternalFetch(void)
   {
      
   }

   ExternalFetch::~ExternalFetch(void)
   {

   }

   void 
   ExternalFetch::Start(std::shared_ptr<FetchAccount> pFA)
   {  
      LOG_DEBUG(Formatter::Format("Retrieving messages from external account {0}", pFA->GetName()));
      
      std::shared_ptr<IOService> pIOService = Application::Instance()->GetIOService();

      std::shared_ptr<Event> disconnectEvent = std::shared_ptr<Event>(new Event()) ;
      std::shared_ptr<POP3ClientConnection> pClientConnection = std::shared_ptr<POP3ClientConnection> 
         (new POP3ClientConnection(pFA, 
                                   pFA->GetConnectionSecurity(), 
                                   pIOService->GetIOContext(), 
                                   pIOService->GetClientContext(), 
                                   disconnectEvent,
                                   pFA->GetServerAddress()));

      DNSResolver resolver;

      std::vector<String> ip_addresses;
      resolver.GetIpAddresses(pFA->GetServerAddress(), ip_addresses, true);

      String ip_address;
      if (ip_addresses.size())
      {
         ip_address = *(ip_addresses.begin());
      }
      else
      {
         String error_message = Formatter::Format("The IP address for external account {0} could not be resolved. Aborting fetch.", pFA->GetName());
         LOG_APPLICATION(error_message);
         return;
      }

      if (pClientConnection->Connect(ip_address, pFA->GetPort(), IPAddress()))
      {
         // Keep a WEAK reference before dropping the strong one. The strong reference has
         // to go, as it always did, so the connection can be torn down whenever it likes;
         // the weak one exists solely so the ceiling below can tell it to.
         std::weak_ptr<POP3ClientConnection> weakConnection = pClientConnection;

         pClientConnection.reset();

         // This wait had no ceiling at all, and disconnectEvent is set only by
         // ~TCPConnection. POP3ClientConnection sets an idle timeout and no absolute
         // bound, and an idle timeout is not a ceiling because it re-arms on every byte -
         // so a remote server that sends one byte before each expiry held this work-queue
         // thread for as long as it cared to. MaxNumberOfExternalFetchThreads of those and
         // external fetching stops for every account on the server.
         //
         // The ceiling is deliberately generous: a large mailbox over a slow link is a
         // legitimate hour, and this is a backstop for the pathological case rather than a
         // performance bound.
         const boost::chrono::minutes fetchCeiling(60);

         if (!disconnectEvent->WaitFor(fetchCeiling))
         {
            // Timing out is not enough on its own. The session is still running, and
            // returning here releases the fetch account for the next cycle - which would
            // then collect the same mailbox concurrently with the session we walked away
            // from, and that is how the same message gets delivered twice. So the
            // abandoned connection is disconnected before we let go of it.
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6004, "ExternalFetch::Start",
               Formatter::Format("Fetching from external account {0} was still running after {1} minutes and has been abandoned. The connection has been closed. The remote server was sending just enough data to keep the session alive without completing it.",
                  pFA->GetName(), (int) fetchCeiling.count()));

            // EnqueueDisconnect rather than Disconnect: the latter is private, and it is
            // private for a good reason - this is not the connection's own thread, and the
            // enqueued form posts the shutdown onto the strand that owns the socket.
            if (auto connection = weakConnection.lock())
               connection->EnqueueDisconnect();
         }
      }

      LOG_DEBUG("Completed retrieval of messages from external account.");
   }


};