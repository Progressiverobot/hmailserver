// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once


namespace HM
{
   class IPrerequisite
   {
   public:
      
      virtual int GetDatabaseVersion() = 0;
      virtual bool Ensure(std::shared_ptr<DALConnection> connection, String &sErrorMessage) = 0;

   private:


   };
}