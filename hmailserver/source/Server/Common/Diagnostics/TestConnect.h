// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{

   class TestConnect
   {
   public:

      bool PerformTest(ConnectionSecurity connection_security, const String &localAddressStr, const String &server, int port, String &result);

   };


}
