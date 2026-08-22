// Copyright (c) 2014 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

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