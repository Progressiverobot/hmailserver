// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class SpamAssassinTestConnect
   {
   public:

      bool TestConnect(const String &hostName, int port, String &message);

   private:

   };
}