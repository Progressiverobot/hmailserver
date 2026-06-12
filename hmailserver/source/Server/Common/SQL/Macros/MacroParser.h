// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "Macro.h"

namespace HM
{
   class MacroParser
   {
   public:
	   MacroParser(const String &macro);
	   virtual ~MacroParser();

      Macro Parse();

   private:

      String macro_string_;
   };
}
