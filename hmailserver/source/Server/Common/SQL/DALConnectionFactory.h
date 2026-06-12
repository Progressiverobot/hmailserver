// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class DatabaseSettings;

   class DALConnectionFactory  
   {
   public:
	   DALConnectionFactory();
	   virtual ~DALConnectionFactory();


      static std::shared_ptr<DALConnection> CreateConnection(std::shared_ptr<DatabaseSettings> pSettings);

   };
}
