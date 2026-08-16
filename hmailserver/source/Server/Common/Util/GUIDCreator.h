// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class GUIDCreator  
   {
   public:
	   GUIDCreator();
	   virtual ~GUIDCreator();

      static String GetGUID();
   };

}
