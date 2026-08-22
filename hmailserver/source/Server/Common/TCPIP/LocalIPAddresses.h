// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class LocalIPAddresses : public Singleton<LocalIPAddresses>
   {
   public:
	   LocalIPAddresses();
	   virtual ~LocalIPAddresses();

      void LoadIPAddresses();

      bool IsLocalIPAddress(const IPAddress &address);
      bool IsLocalPort(const IPAddress &address, int port);
      bool IsWithinLoopbackRange(const IPAddress &address);

   private:
      
      std::vector<std::pair<IPAddress, int> > local_ports_;
   };


   class LocalIPAddressesTester
   {
   public :
      LocalIPAddressesTester () {};
      ~LocalIPAddressesTester () {};      

      void Test();
   };



}
