// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{


   class HTTPClient  
   {
   public:
	   HTTPClient();
	   virtual ~HTTPClient();
      
      bool ExecuteScript(const String &sServer, const String &sPage, AnsiString &output) const;

   private:


   };

}