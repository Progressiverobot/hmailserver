// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "TCPIPPorts.h"

#include "../Persistence/PersistentBlockedAttachment.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   TCPIPPorts::TCPIPPorts()
   {
   }

   TCPIPPorts::~TCPIPPorts(void)
   {
   }

   void 
   TCPIPPorts::Refresh()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Reads all TCP/IP Ports from the database.
   //---------------------------------------------------------------------------()
   {
      String sSQL = "select * from hm_tcpipports order by portaddress1 asc, portaddress2 asc, portnumber asc";
      DBLoad_(sSQL);
   }

   bool
   TCPIPPorts::SetDefault()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Resets all TCP/IP Ports to their default values.
   //---------------------------------------------------------------------------()
   // Every port is deleted before the four defaults are written, and the four writes
   // used to be unchecked - the function even declared error_message, passed it to
   // each save, and then threw away whatever came back in it, four times. A database
   // that failed anywhere in that sequence left the server listening on fewer ports
   // than it started with, possibly none at all, while the COM caller got S_OK and
   // the administration tool said the defaults had been restored.
   //
   // Which is the worst possible moment for silence: restoring the default ports is
   // what an administrator does when connections are already not working.
   {
      Refresh();

      // check if the settings are already default.
      if (vecObjects.size() == 4)
      {
         /* Shouldn't we reset SSL/TLS settings as well?
         https://github.com/hmailserver/hmailserver/pull/441#issuecomment-1221246664
         if (vecObjects[0]->GetPortNumber() == 25 && vecObjects[0]->GetProtocol() == STSMTP &&
            vecObjects[1]->GetPortNumber() == 110 && vecObjects[1]->GetProtocol() == STPOP3 &&
            vecObjects[2]->GetPortNumber() == 143 && vecObjects[2]->GetProtocol() == STIMAP &&
            vecObjects[3]->GetPortNumber() == 587 && vecObjects[3]->GetProtocol() == STSMTP)
         */
         if (vecObjects[0]->GetPortNumber() == 25 && vecObjects[0]->GetProtocol() == STSMTP && vecObjects[0]->GetConnectionSecurity() == CSNone &&
            vecObjects[1]->GetPortNumber() == 110 && vecObjects[1]->GetProtocol() == STPOP3 && vecObjects[1]->GetConnectionSecurity() == CSNone &&
            vecObjects[2]->GetPortNumber() == 143 && vecObjects[2]->GetProtocol() == STIMAP && vecObjects[2]->GetConnectionSecurity() == CSNone &&
            vecObjects[3]->GetPortNumber() == 587 && vecObjects[3]->GetProtocol() == STSMTP && vecObjects[3]->GetConnectionSecurity() == CSNone)
         {
            // no changes are needed.
            return true;
         }
      }

      // Delete all existing ports and then add new ones.
      DeleteAll();

      String first_failure;

      // Every port is attempted even after one has failed, because they are
      // independent rows and three working ports is a better place to leave an
      // administrator than one. The first reason is the one reported - later ones
      // from the same outage say the same thing.
      auto saveDefaultPort = [&first_failure](int portNumber, SessionType protocol)
      {
         std::shared_ptr<TCPIPPort> port = std::shared_ptr<TCPIPPort>(new TCPIPPort);
         port->SetPortNumber(portNumber);
         port->SetProtocol(protocol);

         String error_message;

         if (PersistentTCPIPPort::SaveObject(port, error_message, PersistenceModeNormal))
            return true;

         if (first_failure.IsEmpty())
         {
            first_failure.Format(_T("Port %d could not be saved: %s"), portNumber,
               error_message.IsEmpty() ? _T("no reason was given.") : error_message.c_str());
         }

         return false;
      };

      bool allSaved = true;

      // Written out rather than folded into one expression so that no short-circuit
      // can skip a port.
      if (!saveDefaultPort(25, STSMTP)) allSaved = false;
      if (!saveDefaultPort(110, STPOP3)) allSaved = false;
      if (!saveDefaultPort(143, STIMAP)) allSaved = false;
      if (!saveDefaultPort(587, STSMTP)) allSaved = false;

      Refresh();

      if (!allSaved)
      {
         String message;
         message.Format(_T("Restoring the default TCP/IP ports failed. The existing ports were deleted first, so the server is now listening on %d port(s) rather than the usual 4, and will not accept connections on the rest until this is corrected. %s"),
            (int) vecObjects.size(), first_failure.c_str());

         ErrorManager::Instance()->ReportError(ErrorManager::High, 6079, "TCPIPPorts::SetDefault", message);

         return false;
      }

      return true;
   }

   std::shared_ptr<TCPIPPort> 
   TCPIPPorts::GetPort(const IPAddress &iIPAddress, int iPort)
   {
      auto iter = vecObjects.begin();   
      auto iterEnd = vecObjects.end();   

      std::vector<int> vecResult;
      for (; iter != iterEnd; iter++)
      {
         std::shared_ptr<TCPIPPort> pTCPIPPort = (*iter);
         if (pTCPIPPort->GetPortNumber() == iPort && 
            (pTCPIPPort->GetAddress() == iIPAddress || pTCPIPPort->GetAddress().IsAny()))
         {
            return pTCPIPPort;
         }
      }

      std::shared_ptr<TCPIPPort> pEmpty;
      return pEmpty;
   }

}