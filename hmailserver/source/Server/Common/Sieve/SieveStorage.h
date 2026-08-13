// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#pragma once

#include <vector>

namespace HM
{
   // Per-account Sieve script storage on disk, under
   // {DataDirectory}\Sieve\{domain}\{localpart}\. A single active script is held
   // in active.sieve (read by delivery and the COM property); the named-script
   // model required by ManageSieve (RFC 5804) stores scripts under scripts\ and
   // records the active script's name in active.name.
   class SieveStorage
   {
   public:
      // Returns the account's active Sieve script, or an empty string when none
      // is configured.
      static String GetActiveScript(const String &accountAddress);

      // Stores the account's active Sieve script. An empty script clears it.
      static bool SetActiveScript(const String &accountAddress, const String &script);

      // --- Named-script model (ManageSieve, RFC 5804) ---------------------------

      // True when the name is acceptable as a script file name.
      static bool IsValidScriptName(const String &name);

      // Lists the names of the account's stored scripts.
      static std::vector<String> ListScriptNames(const String &accountAddress);

      static bool ScriptExists(const String &accountAddress, const String &name);
      static String GetScript(const String &accountAddress, const String &name);
      static bool PutScript(const String &accountAddress, const String &name, const String &content);
      static bool DeleteScript(const String &accountAddress, const String &name);

      // The name of the active script ("" when none is active).
      static String GetActiveScriptName(const String &accountAddress);
      // Activates the named script (mirroring its content to active.sieve). An
      // empty name deactivates Sieve filtering for the account.
      static bool SetActiveScriptName(const String &accountAddress, const String &name);

      // Where SieveVacationTracker keeps the account's RFC 5230 4.7 response
      // records. It lives here rather than in the tracker so that the layout of an
      // account's Sieve directory is described in exactly one place - the
      // sanitisation that stops an unusual local-part escaping that directory is the
      // part that must not be reimplemented.
      static String GetVacationStorePath(const String &accountAddress);

   private:
      static String GetAccountDirectory_(const String &accountAddress);
      static String GetActiveScriptPath_(const String &accountAddress);
      static String GetScriptsDirectory_(const String &accountAddress);
      static String GetScriptPath_(const String &accountAddress, const String &name);
      static String GetActiveNamePath_(const String &accountAddress);
      static String SanitizeComponent_(const String &component);
   };
}
