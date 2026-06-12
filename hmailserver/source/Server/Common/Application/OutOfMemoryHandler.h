// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class OutOfMemoryHandler
   {
   public:
      OutOfMemoryHandler(void);
      ~OutOfMemoryHandler(void);

      static void Initialize();
      static void Terminate();

   private:

      static _PNH pOriginalNewHandler;
   };
}