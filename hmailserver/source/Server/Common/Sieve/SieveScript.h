// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#pragma once

#include <vector>
#include <memory>

#include "SieveParser.h"

namespace HM
{
   // Front door to the Sieve (RFC 5228) engine. Parses a script into an AST and
   // exposes syntax checking. Execution against a message is layered on top of
   // the AST in a separate component.
   class SieveScript
   {
   public:
      SieveScript();

      // Parses the script into the AST. Returns true on success; on failure
      // errorMessage describes the problem (with a line number).
      bool Parse(const String &script, String &errorMessage);

      const std::vector<std::shared_ptr<SieveCommand>> &GetCommands() const { return commands_; }

      // Convenience: returns an empty string when the script is syntactically
      // valid, otherwise a human-readable error message.
      static String CheckSyntax(const String &script);

      // Convenience: parses the script and evaluates it against a raw RFC 822
      // message, returning the ';'-joined action summary (e.g. "fileinto:Spam",
      // "discard", "keep") or "error: <message>" when the script does not parse.
      static String Evaluate(const String &script, const String &rawMessage);

   private:
      std::vector<std::shared_ptr<SieveCommand>> commands_;
   };
}
