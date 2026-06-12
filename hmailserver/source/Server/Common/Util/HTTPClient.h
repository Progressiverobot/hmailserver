// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

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