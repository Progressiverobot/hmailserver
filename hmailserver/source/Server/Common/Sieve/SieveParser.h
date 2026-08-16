// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#pragma once

#include <vector>
#include <memory>
#include <set>

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

   // The tags, positional string lists and numeric argument of a single command
   // or test, split apart once.
   //
   // Both the parser's validation pass and the evaluator read this, which is the
   // point: a tag such as :comparator or :days swallows the argument that follows
   // it, so if the two disagreed about which arguments are a tag's value and which
   // are positional they would disagree about what the script says. One splitter
   // means one answer.
   struct SieveArgumentSet
   {
      // Every tag that appeared, lowercased and in order, with the line it was on.
      // The validator checks these against the set the command/test allows.
      std::vector<String> tags;
      std::vector<int> tagLines;

      // ":is" / ":contains" / ":matches", or ":value" / ":count" (relational).
      String matchType = _T("is");
      bool matchTypeGiven = false;

      // The relational operator that :value / :count carries ("gt", "eq", ...).
      String relation;

      String comparator = _T("i;ascii-casemap");
      bool comparatorGiven = false;

      // "all" / "localpart" / "domain", or "user" / "detail" (subaddress).
      String addressPart = _T("all");
      bool addressPartGiven = false;

      // size :over / :under. sizeOver keeps the historical default of :over so
      // that evaluation of an already-stored script cannot change meaning.
      bool sizeOver = true;
      bool sizeGiven = false;

      bool hasNumber = false;
      __int64 number = 0;

      // ":copy" (RFC 3894).
      bool copy = false;

      // ":flags" (RFC 5232), already split into individual flag names.
      bool flagsGiven = false;
      std::vector<String> flags;

      // vacation (RFC 5230) tags.
      bool daysGiven = false;
      __int64 days = 0;
      // ":seconds" (RFC 6131). Held separately from days rather than folded into it,
      // because the two are mutually exclusive and the validator has to be able to
      // say which one the script actually wrote.
      bool secondsGiven = false;
      __int64 seconds = 0;
      bool subjectGiven = false;
      String subject;
      bool handleGiven = false;
      String handle;
      bool fromGiven = false;
      String fromAddress;
      bool addressesGiven = false;
      std::vector<String> addresses;
      bool mime = false;

      // body (RFC 5173). The transform is ":text" unless the script says
      // otherwise; ":content" additionally carries the MIME types to look at.
      String bodyTransform = _T("text");
      bool bodyTransformGiven = false;
      std::vector<String> contentTypes;

      // fileinto :create (RFC 5490).
      bool mailboxCreate = false;

      // Positional string-list arguments, in order.
      std::vector<std::vector<String>> stringLists;
   };

   // Recursive-descent parser that turns a Sieve token stream into an AST,
   // validating the RFC 5228 grammar, the set of supported commands/tests, and
   // - in a second pass - that every extension the script uses was named in a
   // "require" and is one this server can actually honour.
   class SieveParser
   {
   public:
      SieveParser();

      bool Parse(const std::vector<SieveToken> &tokens,
                 std::vector<std::shared_ptr<SieveCommand>> &commands,
                 String &errorMessage);

      // True for the capability names this implementation can honour in a
      // "require". Anything else is refused when the script is uploaded rather
      // than quietly ignored at delivery time, because a script that asks for
      // something we cannot do does not mean what its author thinks it means.
      // Capability names are case-sensitive (RFC 5228 2.10.5).
      static bool IsSupportedExtension(const String &name);

      // Splits an argument vector. Returns false only when a tag that takes a
      // value is missing it; the evaluator treats that as an internal error
      // because the validation pass has already rejected such scripts.
      static bool SplitArguments(const std::vector<SieveArgument> &arguments,
                                 SieveArgumentSet &result,
                                 String &errorMessage);

      // Splits a list of flag strings into individual flag names. A single string
      // may hold several space-separated flags (RFC 5232 2). System flags are
      // canonicalised ("\\seen" -> "\\Seen") and duplicates removed, so that the
      // set handed to the delivery path is directly usable as IMAP flags.
      static std::vector<String> SplitFlagList(const std::vector<String> &values);

      // True for a flag name that may be set on a message. \Recent is excluded:
      // RFC 3501 does not allow a client to set it.
      static bool IsValidFlagName(const String &flag);

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

      bool CollectRequire_(const std::shared_ptr<SieveCommand> &command, String &errorMessage);

      // Second pass: extension gating and per-command/per-test argument checking.
      bool ValidateCommands_(const std::vector<std::shared_ptr<SieveCommand>> &commands, String &errorMessage);
      bool ValidateCommand_(const std::shared_ptr<SieveCommand> &command, String &errorMessage);
      bool ValidateTest_(const std::shared_ptr<SieveTest> &test, String &errorMessage);
      // expectedStringLists is how many positional lists the test takes: two for
      // header/address/envelope (the names, then the keys) and one for body, which
      // has no name list because its subject is the message itself.
      bool ValidateMatchArguments_(const SieveArgumentSet &set, const String &context, int line, String &errorMessage,
                                   size_t expectedStringLists = 2);
      bool ValidateFlagList_(const std::vector<String> &flags, int line, String &errorMessage);

      bool HasExtension_(const String &extension) const;
      bool NeedExtension_(const String &extension, const String &feature, int line, String &errorMessage) const;

      // RFC 5463: extensions named by an "ihave" test are usable inside that
      // branch's block as if they had been required, because the block only runs
      // when the test - "are these available" - was true. Grants are collected
      // from a branch's test (the test itself, or any conjunct of an enclosing
      // "allof"; a name inside "anyof" or "not" guarantees nothing about the
      // block and grants nothing) and are scoped to the block by the caller
      // truncating the vector back afterwards.
      static void CollectIhaveGrants_(const std::shared_ptr<SieveTest> &test, std::vector<String> &granted);
      std::vector<String> ihave_granted_;

      static bool CheckTags_(const SieveArgumentSet &set, const String &allowed, const String &context, String &errorMessage);

      const std::vector<SieveToken> *tokens_;
      size_t index_;
      bool seenNonRequire_;

      // How deep the two recursive descents currently are: blocks
      // (ParseCommands_ -> ParseCommand_ -> ParseCommands_, one level per "{") and
      // tests (ParseTest_ -> ParseTest_, one level per nested anyof/allof/not).
      //
      // Neither had a bound. A Sieve script is supplied by an authenticated user
      // through ManageSieve PUTSCRIPT or CHECKSCRIPT, and the size limit is 1 MB -
      // "if true{" is eight bytes, so a script well inside the limit asks for tens
      // of thousands of levels of recursion and takes the stack with it. A stack
      // overflow is not an exception this can catch: it is the process.
      //
      // The same shape as the MIME nesting defect fixed in the same week, and the
      // same answer - a bound far above anything a person writes, enforced where
      // the recursion happens rather than trusted to the input.
      int block_depth_ = 0;
      int test_depth_ = 0;

      // Real scripts nest a handful deep; generated ones from a rule builder rarely
      // pass ten. Fifty is beyond any legitimate script and thousands below the
      // stack, which is the useful place for a limit like this to sit.
      static const int MaxNestingDepth = 50;

      // Increments a depth counter for the lifetime of one parse level and puts it
      // back however the level is left, including the error returns.
      class DepthGuard
      {
      public:
         explicit DepthGuard(int &depth) : depth_(depth) { depth_++; }
         ~DepthGuard() { depth_--; }
      private:
         DepthGuard(const DepthGuard &) = delete;
         DepthGuard &operator=(const DepthGuard &) = delete;
         int &depth_;
      };

      // Capability names named by the script's "require" statements.
      std::set<String> required_;
   };
}
