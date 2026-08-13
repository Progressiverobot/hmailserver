// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   // Active Directory account validation. All static: there is nothing to construct.
   //
   // The constructor and virtual destructor that used to be declared here were never
   // defined in the .cpp, so the first line of code that tried to instantiate this
   // class - or take its address for a unit test - would have failed at link time with
   // an unresolved external rather than at the point of the mistake. Deleting them
   // makes the intent explicit and the trap impossible.
   class SSPIValidation
   {
   public:
      SSPIValidation() = delete;
      ~SSPIValidation() = delete;

      static bool ValidateUser(const String &sDomain, const String &sUsername, const String &sPassword);
   };

}
