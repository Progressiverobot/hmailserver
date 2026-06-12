// Copyright (c) 2014 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class DisconnectedException : public std::exception
   {
   public:

      virtual const char* what() const
      {
         return "The client has been disconnected.";
      }


   private:

   };
}