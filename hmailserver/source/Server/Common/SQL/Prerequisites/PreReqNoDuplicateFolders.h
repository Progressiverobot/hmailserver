// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "IPrerequisite.h"

namespace HM
{
   class PreReqNoDuplicateFolders : public IPrerequisite
   {
   public:
      PreReqNoDuplicateFolders(void);
      ~PreReqNoDuplicateFolders(void);

      int GetDatabaseVersion() {return 5200; }
      bool Ensure(std::shared_ptr<DALConnection> connection, String &sErrorMessage);


   private:
      

   };
}