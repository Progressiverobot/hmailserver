// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class Dictionary
   {
   public:
	   Dictionary();
      ~Dictionary();

      static String GetWindowsErrorDescription(int iErrorCode);
   
   };
}
