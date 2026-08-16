// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class EmailAllUsers
   {
   public:
      EmailAllUsers(void);
      ~EmailAllUsers(void);

      bool Start(const String &sRecipientWildcard, const String &sFromAddress, const String &sFromName, const String &sSubject, const String &sBody);

   };
}