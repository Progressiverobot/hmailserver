// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#pragma once

namespace HM
{
   // Per-account Sieve script storage on disk, under
   // {DataDirectory}\Sieve\{domain}\{localpart}\. A single active script is held
   // in active.sieve; the named-script model required by ManageSieve (RFC 5804)
   // layers on top of this directory later.
   class SieveStorage
   {
   public:
      // Returns the account's active Sieve script, or an empty string when none
      // is configured.
      static String GetActiveScript(const String &accountAddress);

      // Stores the account's active Sieve script. An empty script clears it.
      static bool SetActiveScript(const String &accountAddress, const String &script);

   private:
      static String GetAccountDirectory_(const String &accountAddress);
      static String GetActiveScriptPath_(const String &accountAddress);
      static String SanitizeComponent_(const String &component);
   };
}
