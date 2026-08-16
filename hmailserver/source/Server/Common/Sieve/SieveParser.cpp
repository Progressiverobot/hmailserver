// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#include "StdAfx.h"

#include "SieveParser.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // Tags that swallow the argument which follows them. Every one of these is
      // a tag=value pair in the grammar, so the string or number after it is the
      // tag's value and must not be counted as a positional argument.
      bool TagTakesValue(const String &lowerTag)
      {
         return lowerTag == _T("comparator") ||
                lowerTag == _T("value") ||
                lowerTag == _T("count") ||
                lowerTag == _T("flags") ||
                lowerTag == _T("days") ||
                lowerTag == _T("seconds") ||
                lowerTag == _T("subject") ||
                lowerTag == _T("handle") ||
                lowerTag == _T("from") ||
                lowerTag == _T("addresses") ||
                lowerTag == _T("content");
      }

      bool IsRelation(const String &relation)
      {
         return relation.CompareNoCase(_T("gt")) == 0 ||
                relation.CompareNoCase(_T("ge")) == 0 ||
                relation.CompareNoCase(_T("lt")) == 0 ||
                relation.CompareNoCase(_T("le")) == 0 ||
                relation.CompareNoCase(_T("eq")) == 0 ||
                relation.CompareNoCase(_T("ne")) == 0;
      }

      bool IsKnownComparator(const String &comparator)
      {
         return comparator.Compare(_T("i;ascii-casemap")) == 0 ||
                comparator.Compare(_T("i;octet")) == 0 ||
                comparator.Compare(_T("i;ascii-numeric")) == 0;
      }
   }

   SieveParser::SieveParser() :
      tokens_(nullptr),
      index_(0),
      seenNonRequire_(false)
   {
   }

   const SieveToken &
   SieveParser::Current_() const
   {
      return (*tokens_)[index_];
   }

   const SieveToken &
   SieveParser::Peek_(int offset) const
   {
      size_t target = index_ + offset;
      if (target >= tokens_->size())
         return tokens_->back();

      return (*tokens_)[target];
   }

   void
   SieveParser::Advance_()
   {
      if (index_ + 1 < tokens_->size())
         index_++;
   }

   bool
   SieveParser::IsSupportedExtension(const String &name)
   {
      // The complete set of extensions this server implements. ManageSieve's
      // "SIEVE" capability line is a subset of this - see the comment on
      // AdvertisedSieveExtensions in ManageSieveServer.cpp for why.
      static const wchar_t *supported[] =
      {
         L"fileinto",
         L"envelope",
         L"imap4flags",
         L"body",
         L"mailbox",
         L"vacation",
         // RFC 6131. Naming it separately from "vacation" is the point of the
         // extension: a script that says ":seconds" must be refused outright by a
         // server that would round it up to a day, because "reply again after two
         // minutes" and "reply again tomorrow" are not the same instruction.
         L"vacation-seconds",
         L"copy",
         L"relational",
         L"subaddress",
         L"comparator-i;ascii-casemap",
         L"comparator-i;octet",
         L"comparator-i;ascii-numeric"
      };

      for (const wchar_t *candidate : supported)
      {
         if (name.Compare(candidate) == 0)
            return true;
      }

      return false;
   }

   bool
   SieveParser::IsValidFlagName(const String &flag)
   {
      if (flag.IsEmpty())
         return false;

      if (flag[0] == L'\\')
      {
         // Only the system flags a client is allowed to store. \Recent is
         // deliberately absent: RFC 3501 forbids setting it.
         return flag.CompareNoCase(_T("\\Seen")) == 0 ||
                flag.CompareNoCase(_T("\\Answered")) == 0 ||
                flag.CompareNoCase(_T("\\Flagged")) == 0 ||
                flag.CompareNoCase(_T("\\Deleted")) == 0 ||
                flag.CompareNoCase(_T("\\Draft")) == 0;
      }

      // An IMAP keyword: an atom, so none of the specials and nothing below
      // space.
      for (int i = 0; i < flag.GetLength(); i++)
      {
         wchar_t ch = flag[i];
         if (ch <= L' ' || ch >= 127)
            return false;

         if (ch == L'(' || ch == L')' || ch == L'{' || ch == L'}' || ch == L'[' || ch == L']' ||
             ch == L'%' || ch == L'*' || ch == L'"' || ch == L'\\')
            return false;
      }

      return true;
   }

   std::vector<String>
   SieveParser::SplitFlagList(const std::vector<String> &values)
   {
      std::vector<String> result;

      for (const String &value : values)
      {
         int start = 0;
         int length = value.GetLength();

         for (int i = 0; i <= length; i++)
         {
            if (i != length && value[i] != L' ' && value[i] != L'\t')
               continue;

            if (i > start)
            {
               String flag = value.Mid(start, i - start);

               // Canonicalise the system flags so that "\\seen" and "\\Seen"
               // are one flag both here and in the IMAP folder.
               static const wchar_t *systemFlags[] = { L"\\Seen", L"\\Answered", L"\\Flagged", L"\\Deleted", L"\\Draft" };
               for (const wchar_t *systemFlag : systemFlags)
               {
                  if (flag.CompareNoCase(systemFlag) == 0)
                  {
                     flag = systemFlag;
                     break;
                  }
               }

               bool duplicate = false;
               for (const String &existing : result)
               {
                  if (existing.CompareNoCase(flag) == 0)
                  {
                     duplicate = true;
                     break;
                  }
               }

               if (!duplicate)
                  result.push_back(flag);
            }

            start = i + 1;
         }
      }

      return result;
   }

   bool
   SieveParser::SplitArguments(const std::vector<SieveArgument> &arguments, SieveArgumentSet &result, String &errorMessage)
   {
      for (size_t i = 0; i < arguments.size(); i++)
      {
         const SieveArgument &argument = arguments[i];

         if (argument.kind == SieveArgument::Kind::Number)
         {
            result.number = argument.number;
            result.hasNumber = true;
            continue;
         }

         if (argument.kind == SieveArgument::Kind::StringList)
         {
            result.stringLists.push_back(argument.strings);
            continue;
         }

         String tag = argument.tag;
         tag.ToLower();

         result.tags.push_back(tag);
         result.tagLines.push_back(argument.line);

         if (!TagTakesValue(tag))
         {
            if (tag == _T("is") || tag == _T("contains") || tag == _T("matches"))
            {
               result.matchType = tag;
               result.matchTypeGiven = true;
            }
            else if (tag == _T("all") || tag == _T("localpart") || tag == _T("domain") ||
                     tag == _T("user") || tag == _T("detail"))
            {
               result.addressPart = tag;
               result.addressPartGiven = true;
            }
            else if (tag == _T("over"))
            {
               result.sizeOver = true;
               result.sizeGiven = true;
            }
            else if (tag == _T("under"))
            {
               result.sizeOver = false;
               result.sizeGiven = true;
            }
            else if (tag == _T("copy"))
            {
               result.copy = true;
            }
            else if (tag == _T("mime"))
            {
               result.mime = true;
            }
            else if (tag == _T("raw") || tag == _T("text"))
            {
               // body (RFC 5173) transforms that carry no argument.
               result.bodyTransform = tag;
               result.bodyTransformGiven = true;
            }
            else if (tag == _T("create"))
            {
               // fileinto :create (RFC 5490).
               result.mailboxCreate = true;
            }

            continue;
         }

         // A tag=value pair. The value is the next argument.
         const SieveArgument *value = (i + 1 < arguments.size()) ? &arguments[i + 1] : nullptr;

         if (tag == _T("days"))
         {
            if (value == nullptr || value->kind != SieveArgument::Kind::Number)
            {
               errorMessage.Format(_T("Line %d: ':days' must be followed by a number."), argument.line);
               return false;
            }

            result.days = value->number;
            result.daysGiven = true;
            i++;
            continue;
         }

         if (tag == _T("seconds"))
         {
            if (value == nullptr || value->kind != SieveArgument::Kind::Number)
            {
               errorMessage.Format(_T("Line %d: ':seconds' must be followed by a number."), argument.line);
               return false;
            }

            result.seconds = value->number;
            result.secondsGiven = true;
            i++;
            continue;
         }

         if (value == nullptr || value->kind != SieveArgument::Kind::StringList || value->strings.empty())
         {
            errorMessage.Format(_T("Line %d: ':%s' must be followed by a string."), argument.line, tag.c_str());
            return false;
         }

         if (tag == _T("comparator"))
         {
            result.comparator = value->strings[0];
            result.comparatorGiven = true;
         }
         else if (tag == _T("value") || tag == _T("count"))
         {
            result.matchType = tag;
            result.matchTypeGiven = true;
            result.relation = value->strings[0];
         }
         else if (tag == _T("flags"))
         {
            result.flags = SplitFlagList(value->strings);
            result.flagsGiven = true;
         }
         else if (tag == _T("subject"))
         {
            result.subject = value->strings[0];
            result.subjectGiven = true;
         }
         else if (tag == _T("handle"))
         {
            result.handle = value->strings[0];
            result.handleGiven = true;
         }
         else if (tag == _T("from"))
         {
            result.fromAddress = value->strings[0];
            result.fromGiven = true;
         }
         else if (tag == _T("addresses"))
         {
            result.addresses = value->strings;
            result.addressesGiven = true;
         }
         else if (tag == _T("content"))
         {
            // body :content (RFC 5173): the MIME types whose parts are looked at.
            result.bodyTransform = _T("content");
            result.bodyTransformGiven = true;
            result.contentTypes = value->strings;
         }

         i++;
      }

      return true;
   }

   bool
   SieveParser::IsKnownCommand_(const String &name)
   {
      // Commands this server can carry out. Anything outside this set is refused
      // when the script is uploaded: a command we would silently skip changes
      // what the script does, and the author has no way of finding that out.
      static const wchar_t *known[] =
      {
         L"require", L"if", L"elsif", L"else", L"stop", L"keep", L"discard",
         L"fileinto", L"redirect", L"vacation",
         L"setflag", L"addflag", L"removeflag"
      };

      for (const wchar_t *candidate : known)
      {
         if (name.CompareNoCase(candidate) == 0)
            return true;
      }

      return false;
   }

   bool
   SieveParser::IsKnownTest_(const String &name)
   {
      static const wchar_t *known[] =
      {
         L"address", L"allof", L"anyof", L"exists", L"false", L"header",
         L"not", L"size", L"true", L"envelope", L"hasflag", L"body",
         L"mailboxexists"
      };

      for (const wchar_t *candidate : known)
      {
         if (name.CompareNoCase(candidate) == 0)
            return true;
      }

      return false;
   }

   bool
   SieveParser::HasExtension_(const String &extension) const
   {
      return required_.find(extension) != required_.end();
   }

   bool
   SieveParser::NeedExtension_(const String &extension, const String &feature, int line, String &errorMessage) const
   {
      if (HasExtension_(extension))
         return true;

      errorMessage.Format(_T("Line %d: %s requires require \"%s\"."), line, feature.c_str(), extension.c_str());
      return false;
   }

   bool
   SieveParser::Parse(const std::vector<SieveToken> &tokens,
                      std::vector<std::shared_ptr<SieveCommand>> &commands,
                      String &errorMessage)
   {
      tokens_ = &tokens;
      index_ = 0;
      seenNonRequire_ = false;
      required_.clear();

      // DepthGuard unwinds these on every path including the error returns, so they
      // are already zero here. Reset anyway, with the rest of the per-parse state: a
      // parser instance can be reused, and a counter that only stays correct because
      // nothing has gone wrong yet is the kind that stops being correct quietly.
      block_depth_ = 0;
      test_depth_ = 0;

      commands.clear();

      if (!ParseCommands_(commands, true, errorMessage))
         return false;

      if (Current_().type != SieveTokenType::End)
      {
         errorMessage.Format(_T("Line %d: unexpected token after end of script."), Current_().line);
         return false;
      }

      // Everything above is grammar. This second pass is semantics: it is where a
      // script that uses an extension it did not require, or that uses one we
      // cannot honour, is turned down - at upload time, which is the only moment
      // anyone is listening.
      return ValidateCommands_(commands, errorMessage);
   }

   bool
   SieveParser::ParseCommands_(std::vector<std::shared_ptr<SieveCommand>> &commands, bool topLevel, String &errorMessage)
   {
      // One level per "{": ParseCommand_ calls back here for a command's block. See
      // the comment on block_depth_ - this recursion had no bound at all, and the
      // script is supplied by an authenticated user.
      DepthGuard guard(block_depth_);

      if (block_depth_ > MaxNestingDepth)
      {
         errorMessage.Format(_T("Line %d: the script nests blocks more than %d deep. This is refused rather than parsed: the parser recurses once per level and a script nested far enough would exhaust the stack."),
                             Current_().line, MaxNestingDepth);
         return false;
      }

      while (Current_().type == SieveTokenType::Identifier)
      {
         std::shared_ptr<SieveCommand> command;
         if (!ParseCommand_(command, topLevel, errorMessage))
            return false;

         commands.push_back(command);
      }

      return true;
   }

   bool
   SieveParser::ParseCommand_(std::shared_ptr<SieveCommand> &command, bool /*requireAllowed*/, String &errorMessage)
   {
      const SieveToken &nameToken = Current_();

      if (!IsKnownCommand_(nameToken.value))
      {
         errorMessage.Format(_T("Line %d: unknown command '%s'."), nameToken.line, nameToken.value.c_str());
         return false;
      }

      bool isRequire = nameToken.value.CompareNoCase(_T("require")) == 0;
      if (isRequire && seenNonRequire_)
      {
         errorMessage.Format(_T("Line %d: 'require' must appear before any other command."), nameToken.line);
         return false;
      }

      if (!isRequire)
         seenNonRequire_ = true;

      command = std::shared_ptr<SieveCommand>(new SieveCommand());
      command->name = nameToken.value;
      command->line = nameToken.line;

      Advance_();

      if (!ParseArguments_(command->arguments, command->test, errorMessage))
         return false;

      // The required capabilities have to be known before the validation pass
      // runs, and they are all declared up front, so collect them here.
      if (isRequire && !CollectRequire_(command, errorMessage))
         return false;

      if (Current_().type == SieveTokenType::LeftBrace)
      {
         Advance_();

         command->hasBlock = true;
         if (!ParseCommands_(command->block, false, errorMessage))
            return false;

         if (Current_().type != SieveTokenType::RightBrace)
         {
            errorMessage.Format(_T("Line %d: expected '}' to close command block."), Current_().line);
            return false;
         }

         Advance_();
      }
      else if (Current_().type == SieveTokenType::Semicolon)
      {
         Advance_();
      }
      else
      {
         errorMessage.Format(_T("Line %d: expected ';' or '{' after command '%s'."), Current_().line, command->name.c_str());
         return false;
      }

      return true;
   }

   bool
   SieveParser::CollectRequire_(const std::shared_ptr<SieveCommand> &command, String &errorMessage)
   {
      bool sawCapability = false;

      for (const SieveArgument &argument : command->arguments)
      {
         if (argument.kind != SieveArgument::Kind::StringList)
         {
            errorMessage.Format(_T("Line %d: 'require' takes a string or a list of strings."), command->line);
            return false;
         }

         for (const String &name : argument.strings)
         {
            if (!IsSupportedExtension(name))
            {
               errorMessage.Format(_T("Line %d: the '%s' extension is not supported by this server."),
                  command->line, name.c_str());
               return false;
            }

            required_.insert(name);

            // RFC 6131 3: "vacation-seconds" implies "vacation". A script that
            // requires only the former and then uses a plain "vacation :days" is
            // legal, so the implication has to be recorded here rather than demanded
            // of the author.
            if (name.Compare(_T("vacation-seconds")) == 0)
               required_.insert(_T("vacation"));

            sawCapability = true;
         }
      }

      if (!sawCapability)
      {
         errorMessage.Format(_T("Line %d: 'require' needs at least one capability name."), command->line);
         return false;
      }

      return true;
   }

   bool
   SieveParser::ParseArguments_(std::vector<SieveArgument> &arguments, std::shared_ptr<SieveTest> &test, String &errorMessage)
   {
      // Positional/tagged/numeric/string-list arguments.
      while (true)
      {
         SieveTokenType type = Current_().type;
         if (type == SieveTokenType::Tag ||
             type == SieveTokenType::Number ||
             type == SieveTokenType::QuotedString ||
             type == SieveTokenType::MultiLineString ||
             type == SieveTokenType::LeftBracket)
         {
            SieveArgument argument;
            if (!ParseArgument_(argument, errorMessage))
               return false;

            arguments.push_back(argument);
            continue;
         }

         break;
      }

      // Optional trailing test or test-list.
      if (Current_().type == SieveTokenType::Identifier || Current_().type == SieveTokenType::LeftParen)
      {
         if (!ParseTest_(test, errorMessage))
            return false;
      }

      return true;
   }

   bool
   SieveParser::ParseArgument_(SieveArgument &argument, String &errorMessage)
   {
      const SieveToken &token = Current_();
      argument.line = token.line;

      switch (token.type)
      {
      case SieveTokenType::Tag:
         argument.kind = SieveArgument::Kind::Tag;
         argument.tag = token.value;
         Advance_();
         return true;

      case SieveTokenType::Number:
         argument.kind = SieveArgument::Kind::Number;
         argument.number = token.number;
         Advance_();
         return true;

      case SieveTokenType::QuotedString:
      case SieveTokenType::MultiLineString:
         argument.kind = SieveArgument::Kind::StringList;
         argument.strings.push_back(token.value);
         Advance_();
         return true;

      case SieveTokenType::LeftBracket:
         return ParseStringList_(argument, errorMessage);

      default:
         errorMessage.Format(_T("Line %d: expected an argument."), token.line);
         return false;
      }
   }

   bool
   SieveParser::ParseStringList_(SieveArgument &argument, String &errorMessage)
   {
      argument.kind = SieveArgument::Kind::StringList;

      // Consume '['.
      Advance_();

      if (Current_().type == SieveTokenType::RightBracket)
      {
         errorMessage.Format(_T("Line %d: string list must contain at least one string."), Current_().line);
         return false;
      }

      while (true)
      {
         const SieveToken &token = Current_();
         if (token.type != SieveTokenType::QuotedString && token.type != SieveTokenType::MultiLineString)
         {
            errorMessage.Format(_T("Line %d: expected a string inside string list."), token.line);
            return false;
         }

         argument.strings.push_back(token.value);
         Advance_();

         if (Current_().type == SieveTokenType::Comma)
         {
            Advance_();
            continue;
         }

         break;
      }

      if (Current_().type != SieveTokenType::RightBracket)
      {
         errorMessage.Format(_T("Line %d: expected ']' to close string list."), Current_().line);
         return false;
      }

      Advance_();
      return true;
   }

   bool
   SieveParser::ParseTest_(std::shared_ptr<SieveTest> &test, String &errorMessage)
   {
      // One level per nested test - a parenthesised test list, or anyof/allof/not
      // wrapping another test. Unbounded before this, exactly as the block recursion
      // above was.
      DepthGuard guard(test_depth_);

      if (test_depth_ > MaxNestingDepth)
      {
         errorMessage.Format(_T("Line %d: the script nests tests more than %d deep. This is refused rather than parsed: the parser recurses once per level and a script nested far enough would exhaust the stack."),
                             Current_().line, MaxNestingDepth);
         return false;
      }

      if (Current_().type == SieveTokenType::LeftParen)
      {
         // Test list: ( test *( "," test ) ). Represented as a synthetic
         // "anyof"-style container holding the child tests.
         int parenLine = Current_().line;
         Advance_();

         test = std::shared_ptr<SieveTest>(new SieveTest());
         test->name = _T("");
         test->line = parenLine;

         while (true)
         {
            std::shared_ptr<SieveTest> child;
            if (!ParseTest_(child, errorMessage))
               return false;

            test->tests.push_back(child);

            if (Current_().type == SieveTokenType::Comma)
            {
               Advance_();
               continue;
            }

            break;
         }

         if (Current_().type != SieveTokenType::RightParen)
         {
            errorMessage.Format(_T("Line %d: expected ')' to close test list."), Current_().line);
            return false;
         }

         Advance_();
         return true;
      }

      if (Current_().type != SieveTokenType::Identifier)
      {
         errorMessage.Format(_T("Line %d: expected a test."), Current_().line);
         return false;
      }

      const SieveToken &nameToken = Current_();
      if (!IsKnownTest_(nameToken.value))
      {
         errorMessage.Format(_T("Line %d: unknown test '%s'."), nameToken.line, nameToken.value.c_str());
         return false;
      }

      test = std::shared_ptr<SieveTest>(new SieveTest());
      test->name = nameToken.value;
      test->line = nameToken.line;

      Advance_();

      // A test takes arguments which may themselves contain nested tests
      // (allof/anyof/not). Reuse the argument parser, capturing one nested test.
      std::shared_ptr<SieveTest> nested;
      if (!ParseArguments_(test->arguments, nested, errorMessage))
         return false;

      if (nested)
      {
         // A bare test-list comes back as an anonymous container; flatten its
         // children directly into this test so allof/anyof/not evaluate cleanly.
         if (nested->name.IsEmpty() && !nested->tests.empty())
         {
            for (const std::shared_ptr<SieveTest> &child : nested->tests)
               test->tests.push_back(child);
         }
         else
         {
            test->tests.push_back(nested);
         }
      }

      return true;
   }

   bool
   SieveParser::CheckTags_(const SieveArgumentSet &set, const String &allowed, const String &context, String &errorMessage)
   {
      String haystack = _T(" ") + allowed + _T(" ");

      for (size_t i = 0; i < set.tags.size(); i++)
      {
         String needle = _T(" ") + set.tags[i] + _T(" ");
         if (haystack.Find(needle) >= 0)
            continue;

         errorMessage.Format(_T("Line %d: ':%s' is not a valid argument to %s."),
            set.tagLines[i], set.tags[i].c_str(), context.c_str());
         return false;
      }

      return true;
   }

   bool
   SieveParser::ValidateMatchArguments_(const SieveArgumentSet &set, const String &context, int line, String &errorMessage,
                                        size_t expectedStringLists)
   {
      if (set.comparatorGiven)
      {
         if (!IsKnownComparator(set.comparator))
         {
            errorMessage.Format(_T("Line %d: comparator '%s' is not supported by this server."), line, set.comparator.c_str());
            return false;
         }

         if (set.comparator.Compare(_T("i;ascii-numeric")) == 0 &&
             !NeedExtension_(_T("comparator-i;ascii-numeric"), _T("the 'i;ascii-numeric' comparator"), line, errorMessage))
            return false;
      }

      if (set.matchType == _T("value") || set.matchType == _T("count"))
      {
         if (!NeedExtension_(_T("relational"), _T("':") + set.matchType + _T("'"), line, errorMessage))
            return false;

         if (!IsRelation(set.relation))
         {
            errorMessage.Format(_T("Line %d: ':%s' needs one of \"gt\", \"ge\", \"lt\", \"le\", \"eq\" or \"ne\"."),
               line, set.matchType.c_str());
            return false;
         }

         // A count is a number, so comparing it with a text comparator is
         // meaningless. RFC 5231 4.2 requires a numeric comparator; when none was
         // given the evaluator uses i;ascii-numeric anyway.
         if (set.matchType == _T("count") && set.comparatorGiven &&
             set.comparator.Compare(_T("i;ascii-numeric")) != 0)
         {
            errorMessage.Format(_T("Line %d: ':count' can only be used with the \"i;ascii-numeric\" comparator."), line);
            return false;
         }
      }
      else if (set.comparatorGiven && set.comparator.Compare(_T("i;ascii-numeric")) == 0 &&
               (set.matchType == _T("contains") || set.matchType == _T("matches")))
      {
         // i;ascii-numeric only defines equality (RFC 4790 9.1.1).
         errorMessage.Format(_T("Line %d: the \"i;ascii-numeric\" comparator cannot be used with ':%s'."),
            line, set.matchType.c_str());
         return false;
      }

      if (set.addressPart == _T("user") || set.addressPart == _T("detail"))
      {
         if (!NeedExtension_(_T("subaddress"), _T("':") + set.addressPart + _T("'"), line, errorMessage))
            return false;
      }

      if (set.stringLists.size() != expectedStringLists)
      {
         if (expectedStringLists == 1)
            errorMessage.Format(_T("Line %d: %s takes one list of keys."), line, context.c_str());
         else
            errorMessage.Format(_T("Line %d: %s takes a header/part list and a key list."), line, context.c_str());

         return false;
      }

      return true;
   }

   bool
   SieveParser::ValidateFlagList_(const std::vector<String> &flags, int line, String &errorMessage)
   {
      if (flags.empty())
      {
         // Clearing the flag set is legal: setflag "" removes every flag.
         return true;
      }

      for (const String &flag : flags)
      {
         if (IsValidFlagName(flag))
            continue;

         errorMessage.Format(_T("Line %d: '%s' is not a flag this server can set."), line, flag.c_str());
         return false;
      }

      return true;
   }

   bool
   SieveParser::ValidateCommands_(const std::vector<std::shared_ptr<SieveCommand>> &commands, String &errorMessage)
   {
      bool previousWasConditional = false;

      for (const std::shared_ptr<SieveCommand> &command : commands)
      {
         String name = command->name;
         name.ToLower();

         bool isBranch = (name == _T("elsif") || name == _T("else"));
         if (isBranch && !previousWasConditional)
         {
            errorMessage.Format(_T("Line %d: '%s' must follow an 'if' or 'elsif'."), command->line, name.c_str());
            return false;
         }

         if (!ValidateCommand_(command, errorMessage))
            return false;

         previousWasConditional = (name == _T("if") || name == _T("elsif"));
      }

      return true;
   }

   bool
   SieveParser::ValidateCommand_(const std::shared_ptr<SieveCommand> &command, String &errorMessage)
   {
      String name = command->name;
      name.ToLower();

      bool isControl = (name == _T("if") || name == _T("elsif") || name == _T("else"));
      bool needsTest = (name == _T("if") || name == _T("elsif"));

      if (needsTest && !command->test)
      {
         errorMessage.Format(_T("Line %d: '%s' needs a test."), command->line, name.c_str());
         return false;
      }

      if (!needsTest && command->test)
      {
         errorMessage.Format(_T("Line %d: '%s' does not take a test."), command->line, name.c_str());
         return false;
      }

      if (isControl && !command->hasBlock)
      {
         errorMessage.Format(_T("Line %d: '%s' needs a command block."), command->line, name.c_str());
         return false;
      }

      if (!isControl && command->hasBlock)
      {
         errorMessage.Format(_T("Line %d: '%s' does not take a command block."), command->line, name.c_str());
         return false;
      }

      SieveArgumentSet set;
      if (!isControl && name != _T("require") && !SplitArguments(command->arguments, set, errorMessage))
         return false;

      if (name == _T("keep") || name == _T("fileinto"))
      {
         if (!CheckTags_(set, name == _T("fileinto") ? _T("copy flags create") : _T("flags"), _T("'") + name + _T("'"), errorMessage))
            return false;

         if (set.mailboxCreate)
         {
            if (!NeedExtension_(_T("mailbox"), _T("':create'"), command->line, errorMessage))
               return false;
         }

         if (set.flagsGiven)
         {
            if (!NeedExtension_(_T("imap4flags"), _T("':flags'"), command->line, errorMessage))
               return false;

            if (!ValidateFlagList_(set.flags, command->line, errorMessage))
               return false;
         }

         if (set.copy)
         {
            if (!NeedExtension_(_T("copy"), _T("':copy'"), command->line, errorMessage))
               return false;

            // hMailServer stores exactly one local copy of a message, so there is
            // nowhere to put the second copy that "fileinto :copy" asks for.
            // Refusing the script is the honest answer: silently dropping the
            // extra copy would leave the author believing the message had been
            // put in two places. ":copy" on "redirect" is supported.
            errorMessage.Format(_T("Line %d: 'fileinto :copy' is not supported by this server, ")
               _T("which stores one local copy of a message. Use 'fileinto' on its own, ")
               _T("or ':copy' on 'redirect'."), command->line);
            return false;
         }

         if (name == _T("fileinto") && set.stringLists.size() != 1)
         {
            errorMessage.Format(_T("Line %d: 'fileinto' takes one mailbox name."), command->line);
            return false;
         }

         if (name == _T("keep") && !set.stringLists.empty())
         {
            errorMessage.Format(_T("Line %d: 'keep' does not take a positional argument."), command->line);
            return false;
         }

         return true;
      }

      if (name == _T("redirect"))
      {
         if (!CheckTags_(set, _T("copy"), _T("'redirect'"), errorMessage))
            return false;

         if (set.copy && !NeedExtension_(_T("copy"), _T("':copy'"), command->line, errorMessage))
            return false;

         if (set.stringLists.size() != 1 || set.stringLists[0].size() != 1)
         {
            errorMessage.Format(_T("Line %d: 'redirect' takes one address."), command->line);
            return false;
         }

         return true;
      }

      if (name == _T("discard") || name == _T("stop"))
      {
         if (!set.tags.empty() || !set.stringLists.empty() || set.hasNumber)
         {
            errorMessage.Format(_T("Line %d: '%s' takes no arguments."), command->line, name.c_str());
            return false;
         }

         return true;
      }

      if (name == _T("setflag") || name == _T("addflag") || name == _T("removeflag"))
      {
         if (!NeedExtension_(_T("imap4flags"), _T("'") + name + _T("'"), command->line, errorMessage))
            return false;

         if (!set.tags.empty())
         {
            errorMessage.Format(_T("Line %d: '%s' takes no tagged arguments."), command->line, name.c_str());
            return false;
         }

         // Two positional arguments means the "variables" extension form
         // (setflag <variable> <flags>), which this server does not implement.
         if (set.stringLists.size() != 1)
         {
            errorMessage.Format(_T("Line %d: '%s' takes one list of flags."), command->line, name.c_str());
            return false;
         }

         return ValidateFlagList_(SplitFlagList(set.stringLists[0]), command->line, errorMessage);
      }

      if (name == _T("vacation"))
      {
         if (!NeedExtension_(_T("vacation"), _T("the 'vacation' action"), command->line, errorMessage))
            return false;

         if (!CheckTags_(set, _T("days seconds subject addresses mime handle from"), _T("'vacation'"), errorMessage))
            return false;

         if (set.secondsGiven && !NeedExtension_(_T("vacation-seconds"), _T("':seconds'"), command->line, errorMessage))
            return false;

         if (set.daysGiven && set.secondsGiven)
         {
            // RFC 6131 3: the two are mutually exclusive. Silently preferring one
            // would make the suppression window depend on which branch of the
            // evaluator the reader happened to look at, and the author would have no
            // way of finding out which they got.
            errorMessage.Format(_T("Line %d: 'vacation' takes either ':days' or ':seconds', not both."), command->line);
            return false;
         }

         if (set.daysGiven && set.days < 1)
         {
            errorMessage.Format(_T("Line %d: 'vacation :days' must be at least 1."), command->line);
            return false;
         }

         if (set.secondsGiven && set.seconds < 0)
         {
            // Zero is legal and meaningful (RFC 6131: respond to every message);
            // negative is not, and would arrive at the tracker looking exactly like
            // zero, which is not what the author wrote.
            errorMessage.Format(_T("Line %d: 'vacation :seconds' cannot be negative."), command->line);
            return false;
         }

         // ":from" and ":addresses" both name addresses, and both feed a decision
         // about who a reply may be sent as and to. A malformed entry is therefore
         // caught at upload time rather than shrugged off at delivery time: an
         // ":addresses" entry that does not parse silently contributes nothing to the
         // RFC 5230 4.5 recipient check, which is a loop guard, and the author would
         // never know. ":from" is accepted here and checked again at delivery time,
         // where the account is known and "is this one of the user's own addresses?"
         // can actually be answered.
         if (set.fromGiven && !StringParser::IsValidEmailAddress(set.fromAddress))
         {
            errorMessage.Format(_T("Line %d: 'vacation :from' is not a valid email address: '%s'."),
               command->line, set.fromAddress.c_str());
            return false;
         }

         for (const String &address : set.addresses)
         {
            if (!StringParser::IsValidEmailAddress(address))
            {
               errorMessage.Format(_T("Line %d: 'vacation :addresses' contains something that is not an email address: '%s'."),
                  command->line, address.c_str());
               return false;
            }
         }

         if (set.stringLists.size() != 1 || set.stringLists[0].size() != 1)
         {
            errorMessage.Format(_T("Line %d: 'vacation' takes one reason string."), command->line);
            return false;
         }

         return true;
      }

      if (name == _T("require"))
      {
         // Already validated while parsing, where the capability names had to be
         // known before anything else could be checked.
         return true;
      }

      // if / elsif / else: validate the test and the block.
      if (command->test && !ValidateTest_(command->test, errorMessage))
         return false;

      return ValidateCommands_(command->block, errorMessage);
   }

   bool
   SieveParser::ValidateTest_(const std::shared_ptr<SieveTest> &test, String &errorMessage)
   {
      if (!test)
         return true;

      String name = test->name;
      name.ToLower();

      if (name.IsEmpty())
      {
         // A bare parenthesised test list that was not attached to allof/anyof/not.
         errorMessage.Format(_T("Line %d: a test list is only allowed as the argument of allof, anyof or not."), test->line);
         return false;
      }

      if (name == _T("true") || name == _T("false"))
      {
         if (!test->arguments.empty() || !test->tests.empty())
         {
            errorMessage.Format(_T("Line %d: '%s' takes no arguments."), test->line, name.c_str());
            return false;
         }

         return true;
      }

      if (name == _T("not") || name == _T("allof") || name == _T("anyof"))
      {
         if (!test->arguments.empty())
         {
            errorMessage.Format(_T("Line %d: '%s' takes tests, not values."), test->line, name.c_str());
            return false;
         }

         if (test->tests.empty() || (name == _T("not") && test->tests.size() != 1))
         {
            errorMessage.Format(_T("Line %d: '%s' needs %s."), test->line, name.c_str(),
               name == _T("not") ? _T("exactly one test") : _T("at least one test"));
            return false;
         }

         for (const std::shared_ptr<SieveTest> &child : test->tests)
         {
            if (!ValidateTest_(child, errorMessage))
               return false;
         }

         return true;
      }

      if (!test->tests.empty())
      {
         errorMessage.Format(_T("Line %d: '%s' does not take a nested test."), test->line, name.c_str());
         return false;
      }

      SieveArgumentSet set;
      if (!SplitArguments(test->arguments, set, errorMessage))
         return false;

      if (name == _T("size"))
      {
         if (!CheckTags_(set, _T("over under"), _T("'size'"), errorMessage))
            return false;

         if (!set.sizeGiven || !set.hasNumber || !set.stringLists.empty())
         {
            errorMessage.Format(_T("Line %d: 'size' takes ':over' or ':under' and a number."), test->line);
            return false;
         }

         return true;
      }

      if (name == _T("exists"))
      {
         if (!set.tags.empty() || set.stringLists.size() != 1 || set.hasNumber)
         {
            errorMessage.Format(_T("Line %d: 'exists' takes one list of header names."), test->line);
            return false;
         }

         return true;
      }

      if (name == _T("header"))
      {
         if (!CheckTags_(set, _T("comparator is contains matches value count"), _T("'header'"), errorMessage))
            return false;

         return ValidateMatchArguments_(set, _T("'header'"), test->line, errorMessage);
      }

      if (name == _T("address") || name == _T("envelope"))
      {
         if (name == _T("envelope") &&
             !NeedExtension_(_T("envelope"), _T("the 'envelope' test"), test->line, errorMessage))
            return false;

         if (!CheckTags_(set, _T("comparator is contains matches value count all localpart domain user detail"),
                         _T("'") + name + _T("'"), errorMessage))
            return false;

         if (!ValidateMatchArguments_(set, _T("'") + name + _T("'"), test->line, errorMessage))
            return false;

         if (name == _T("envelope"))
         {
            for (const String &part : set.stringLists[0])
            {
               if (part.CompareNoCase(_T("from")) == 0 || part.CompareNoCase(_T("to")) == 0)
                  continue;

               errorMessage.Format(_T("Line %d: '%s' is not an envelope part this server can test; use \"from\" or \"to\"."),
                  test->line, part.c_str());
               return false;
            }
         }

         return true;
      }

      if (name == _T("mailboxexists"))
      {
         if (!NeedExtension_(_T("mailbox"), _T("the 'mailboxexists' test"), test->line, errorMessage))
            return false;

         // No tags at all: mailboxexists is not a match test, so a comparator or
         // match type on it is an error rather than something to ignore.
         if (!CheckTags_(set, _T(""), _T("'mailboxexists'"), errorMessage))
            return false;

         if (set.stringLists.size() != 1 || set.stringLists[0].empty())
         {
            errorMessage.Format(_T("Line %d: 'mailboxexists' takes one list of mailbox names."), test->line);
            return false;
         }

         return true;
      }

      if (name == _T("body"))
      {
         if (!NeedExtension_(_T("body"), _T("the 'body' test"), test->line, errorMessage))
            return false;

         if (!CheckTags_(set, _T("comparator is contains matches value count raw text content"),
                         _T("'body'"), errorMessage))
            return false;

         if (!ValidateMatchArguments_(set, _T("'body'"), test->line, errorMessage, 1))
            return false;

         // RFC 5173 5: the transforms are mutually exclusive. Accepting two would
         // mean silently honouring one of them, and the author has no way to see
         // which.
         int transforms = 0;
         for (const String &tag : set.tags)
         {
            if (tag == _T("raw") || tag == _T("text") || tag == _T("content"))
               transforms++;
         }

         if (transforms > 1)
         {
            errorMessage.Format(_T("Line %d: 'body' takes at most one of ':raw', ':text' and ':content'."), test->line);
            return false;
         }

         return true;
      }

      if (name == _T("hasflag"))
      {
         if (!NeedExtension_(_T("imap4flags"), _T("the 'hasflag' test"), test->line, errorMessage))
            return false;

         if (!CheckTags_(set, _T("comparator is contains matches value count"), _T("'hasflag'"), errorMessage))
            return false;

         if (set.comparatorGiven && !IsKnownComparator(set.comparator))
         {
            errorMessage.Format(_T("Line %d: comparator '%s' is not supported by this server."), test->line, set.comparator.c_str());
            return false;
         }

         if ((set.matchType == _T("value") || set.matchType == _T("count")) &&
             !NeedExtension_(_T("relational"), _T("':") + set.matchType + _T("'"), test->line, errorMessage))
            return false;

         // One positional argument. Two would be the "variables" form, which this
         // server does not implement.
         if (set.stringLists.size() != 1)
         {
            errorMessage.Format(_T("Line %d: 'hasflag' takes one list of flags."), test->line);
            return false;
         }

         return true;
      }

      errorMessage.Format(_T("Line %d: unknown test '%s'."), test->line, name.c_str());
      return false;
   }
}
