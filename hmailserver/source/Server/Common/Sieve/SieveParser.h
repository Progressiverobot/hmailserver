// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#pragma once

#include <vector>
#include <memory>

#include "SieveLexer.h"

namespace HM
{
   // A positional/tagged/numeric argument to a command or test.
   struct SieveArgument
   {
      enum class Kind { StringList, Number, Tag };

      Kind kind = Kind::StringList;
      std::vector<String> strings;  // one or more entries for a string-list argument
      __int64 number = 0;           // value for a Number argument
      String tag;                   // tag name (without ':') for a Tag argument
      int line = 0;
   };

   // A Sieve test (e.g. header :contains "Subject" "hello", or allof(...)).
   struct SieveTest
   {
      String name;
      std::vector<SieveArgument> arguments;
      std::vector<std::shared_ptr<SieveTest>> tests; // nested tests (allof/anyof/not)
      int line = 0;
   };

   // A Sieve command (e.g. if, require, fileinto, keep).
   struct SieveCommand
   {
      String name;
      std::vector<SieveArgument> arguments;
      std::shared_ptr<SieveTest> test;                  // optional test (if/elsif)
      std::vector<std::shared_ptr<SieveCommand>> block; // optional command block
      bool hasBlock = false;
      int line = 0;
   };

   // Recursive-descent parser that turns a Sieve token stream into an AST,
   // validating the RFC 5228 grammar and the set of supported commands/tests.
   class SieveParser
   {
   public:
      SieveParser();

      bool Parse(const std::vector<SieveToken> &tokens,
                 std::vector<std::shared_ptr<SieveCommand>> &commands,
                 String &errorMessage);

   private:
      const SieveToken &Current_() const;
      const SieveToken &Peek_(int offset) const;
      void Advance_();

      bool ParseCommands_(std::vector<std::shared_ptr<SieveCommand>> &commands, bool topLevel, String &errorMessage);
      bool ParseCommand_(std::shared_ptr<SieveCommand> &command, bool requireAllowed, String &errorMessage);
      bool ParseArguments_(std::vector<SieveArgument> &arguments, std::shared_ptr<SieveTest> &test, String &errorMessage);
      bool ParseArgument_(SieveArgument &argument, String &errorMessage);
      bool ParseStringList_(SieveArgument &argument, String &errorMessage);
      bool ParseTest_(std::shared_ptr<SieveTest> &test, String &errorMessage);

      static bool IsKnownCommand_(const String &name);
      static bool IsKnownTest_(const String &name);

      const std::vector<SieveToken> *tokens_;
      size_t index_;
      bool seenNonRequire_;
   };
}
