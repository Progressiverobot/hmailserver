// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class SQLScriptRunner
   {
   public:
      SQLScriptRunner();

      bool ExecuteScript(std::shared_ptr<DALConnection> connectionObject, const String &sFile, String &sErrorMessage);

   private:


   };
}
