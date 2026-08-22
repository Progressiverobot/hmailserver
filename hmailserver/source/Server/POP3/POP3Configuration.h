// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class POP3Configuration
   {
   public:

      POP3Configuration();
	   virtual ~POP3Configuration();

      long GetMaxPOP3Connections() const;
      void SetMaxPOP3Connections(int newVal);

      String GetWelcomeMessage() const;
      void SetWelcomeMessage(const String &sMessage);

   private:
      std::shared_ptr<PropertySet> GetSettings_() const;

   };

}
