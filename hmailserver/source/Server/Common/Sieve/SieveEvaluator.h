// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#pragma once

#include <vector>
#include <memory>

#include "SieveParser.h"
#include "SieveMessage.h"

namespace HM
{
   // Evaluates a parsed Sieve AST against a message and produces the resulting
   // action summary. Supports the RFC 5228 control flow (if/elsif/else/stop),
   // the core tests (true/false/not/allof/anyof/header/address/exists/size with
   // :is/:contains/:matches and the default comparator) and the core actions
   // (keep/fileinto/discard/redirect, plus implicit keep).
   class SieveEvaluator
   {
   public:
      SieveEvaluator();

      // Returns a ';'-joined action summary, e.g. "fileinto:Spam", "discard",
      // "redirect:a@b.com;keep" or "keep" (the implicit default).
      String Evaluate(const std::vector<std::shared_ptr<SieveCommand>> &commands, const SieveMessage &message);

   private:
      void ExecuteCommands_(const std::vector<std::shared_ptr<SieveCommand>> &commands, const SieveMessage &message);
      void ExecuteCommand_(const std::shared_ptr<SieveCommand> &command, const SieveMessage &message);

      bool EvaluateTest_(const std::shared_ptr<SieveTest> &test, const SieveMessage &message);
      bool EvaluateHeaderLike_(const std::shared_ptr<SieveTest> &test, const SieveMessage &message, bool isAddress);
      bool EvaluateExists_(const std::shared_ptr<SieveTest> &test, const SieveMessage &message);
      bool EvaluateSize_(const std::shared_ptr<SieveTest> &test, const SieveMessage &message);

      static bool MatchValue_(const String &matchType, bool caseSensitive, const String &value, const String &key);
      static String FirstPositionalString_(const std::shared_ptr<SieveCommand> &command);

      std::vector<String> actions_;
      bool stopped_;
   };
}
