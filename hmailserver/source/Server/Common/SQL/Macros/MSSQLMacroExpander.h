// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "IMacroExpander.h"

namespace HM
{
   class Macro;

   class MSSQLMacroExpander : public IMacroExpander
   {
   public:

      bool ProcessMacro(std::shared_ptr<DALConnection> connection, const Macro &macro, String &sErrorMessage);

   private:
   };
}
