// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{

   class TestConnect
   {
   public:

      bool PerformTest(ConnectionSecurity connection_security, const String &localAddressStr, const String &server, int port, String &result);

   };


}
