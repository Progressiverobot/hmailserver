// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.progrssiverobot.com

#include "StdAfx.h"

#include "SieveParser.h"

// For the upload-time compile check on ':regex' keys. The evaluator matches with
// Boost too (through RuleGuard), so what compiles here is what will run there.
#include <boost/regex.hpp>

// For the ':zone' offset check on the date tests (Time::GetTimeAdjustForTimezone),
// so a zone the evaluator cannot parse is refused at upload.
#include "../Util/Time.h"

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
                lowerTag == _T("content") ||
                lowerTag == _T("zone") ||
                lowerTag == _T("index");
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

      // The date-parts RFC 5260 4.1 defines. Refusing an unknown part at upload is
      // the same courtesy as refusing an unknown extension: "dayofweek" would
      // otherwise be stored and silently never match.
      bool IsKnownDatePart(const String &part)
      {
         static const wchar_t *known[] =
         {
            L"year", L"month", L"day", L"date", L"julian", L"hour", L"minute",
            L"second", L"time", L"iso8601", L"std11", L"zone", L"weekday"
         };

         for (const wchar_t *candidate : known)
         {
            if (part.CompareNoCase(candidate) == 0)
               return true;
         }

         return false;
      }

      // Whether a ':regex' key compiles, using the same construction the evaluator
      // will use (Boost, Perl syntax). Called at upload validation so a bad pattern
      // is refused with a line number instead of silently never matching.
      bool RegexKeyCompiles_(const String &key)
      {
         try
         {
            boost::wregex expression(key);
            return true;
         }
         catch (const std::runtime_error &)
         {
            return false;
         }
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
         // draft-ietf-sieve-regex. Never became an RFC, but Dovecot's Pigeonhole
         // implements it and clients offer it, which is what a capability name is
         // for. The evaluation runs under the same budget-and-suspend breaker the
         // legacy rules engine's regex criterion uses (RuleGuard).
         L"regex",
         // RFC 5463: capability probing as a test, so one script can serve servers
         // with different feature sets. Its validator grant (an ihave-guarded block
         // may use what it tested for) is in NeedExtension_/CollectIhaveGrants_.
         L"ihave",
         // RFC 5183: which server, where, and at what phase a script is running.
         L"environment",
         // RFC 5260: the date/currentdate tests, and :index/:last for selecting
         // among repeated header fields. Two capability names by that RFC's own
         // registration, though they ship together.
         L"date",
         L"index",
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
            if (tag == _T("is") || tag == _T("contains") || tag == _T("matches") ||
                tag == _T("regex"))
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
            else if (tag == _T("originalzone"))
            {
               // date (RFC 5260): parts expressed in the header's own zone.
               result.originalZone = true;
            }
            else if (tag == _T("last"))
            {
               // :index :last (RFC 5260 6): count from the end.
               result.lastGiven = true;
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

         if (tag == _T("index"))
         {
            if (value == nullptr || value->kind != SieveArgument::Kind::Number)
            {
               errorMessage.Format(_T("Line %d: ':index' must be followed by a number."), argument.line);
               return false;
            }

            result.indexValue = static_cast<int>(value->number);
            result.indexGiven = true;
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
         else if (tag == _T("zone"))
         {
            // date / currentdate (RFC 5260).
            result.zone = value->strings[0];
            result.zoneGiven = true;
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
         L"mailboxexists", L"ihave", L"environment", L"date", L"currentdate"
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

      // An enclosing "if ihave ..." naming the extension is as good as a require
      // for the block being validated (RFC 5463): the block only runs when the
      // extension was reported available.
      for (const String &granted : ihave_granted_)
      {
         if (granted.CompareNoCase(extension) == 0)
            return true;
      }

      errorMessage.Format(_T("Line %d: %s requires require \"%s\"."), line, feature.c_str(), extension.c_str());
      return false;
   }

   void
   SieveParser::CollectIhaveGrants_(const std::shared_ptr<SieveTest> &test, std::vector<String> &granted)
   {
      if (!test)
         return;

      String name = test->name;
      name.ToLower();

      if (name == _T("ihave"))
      {
         SieveArgumentSet set;
         String ignored;
         if (SieveParser::SplitArguments(test->arguments, set, ignored) && set.stringLists.size() == 1)
         {
            for (const String &extension : set.stringLists[0])
               granted.push_back(extension);
         }

         return;
      }

      // Only allof: every conjunct must have been true for the block to run. A
      // name inside anyof can be false while the block still runs, and inside
      // not it is true precisely when the block does NOT run.
      if (name == _T("allof"))
      {
         for (const std::shared_ptr<SieveTest> &child : test->tests)
            CollectIhaveGrants_(child, granted);
      }
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
               (set.matchType == _T("contains") || set.matchType == _T("matches") ||
                set.matchType == _T("regex")))
      {
         // i;ascii-numeric only defines equality (RFC 4790 9.1.1).
         errorMessage.Format(_T("Line %d: the \"i;ascii-numeric\" comparator cannot be used with ':%s'."),
            line, set.matchType.c_str());
         return false;
      }

      if (set.matchType == _T("regex"))
      {
         if (!NeedExtension_(_T("regex"), _T("':regex'"), line, errorMessage))
            return false;

         // Compile every key NOW, so the author hears about a bad pattern at upload
         // rather than the test silently never matching at delivery. The keys are
         // the last positional list; the earlier one, when there are two, is the
         // header/part names, which are not patterns.
         if (!set.stringLists.empty())
         {
            for (const String &key : set.stringLists.back())
            {
               if (!RegexKeyCompiles_(key))
               {
                  errorMessage.Format(_T("Line %d: ':regex' key '%s' is not a valid regular expression."),
                     line, key.c_str());
                  return false;
               }
            }
         }
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
         else if (expectedStringLists == 3)
            errorMessage.Format(_T("Line %d: %s takes a header name, a date part and a key list."), line, context.c_str());
         else
            errorMessage.Format(_T("Line %d: %s takes a header/part list and a key list."), line, context.c_str());

         return false;
      }

      return true;
   }

   bool
   SieveParser::ValidateIndexArguments_(const SieveArgumentSet &set, int line, String &errorMessage)
   {
      if (set.indexGiven)
      {
         if (!NeedExtension_(_T("index"), _T("':index'"), line, errorMessage))
            return false;

         if (set.indexValue < 1)
         {
            errorMessage.Format(_T("Line %d: ':index' counts from 1."), line);
            return false;
         }
      }

      if (set.lastGiven && !set.indexGiven)
      {
         errorMessage.Format(_T("Line %d: ':last' is only meaningful together with ':index'."), line);
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

      // if / elsif / else: validate the test and the block, with any ihave grants
      // from the test in scope for the block only.
      if (command->test && !ValidateTest_(command->test, errorMessage))
         return false;

      size_t grantedBefore = ihave_granted_.size();
      if (command->test)
         CollectIhaveGrants_(command->test, ihave_granted_);

      bool blockValid = ValidateCommands_(command->block, errorMessage);

      ihave_granted_.resize(grantedBefore);
      return blockValid;
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
         if (!CheckTags_(set, _T("comparator is contains matches value count regex index last"), _T("'header'"), errorMessage))
            return false;

         if (!ValidateIndexArguments_(set, test->line, errorMessage))
            return false;

         return ValidateMatchArguments_(set, _T("'header'"), test->line, errorMessage);
      }

      if (name == _T("address") || name == _T("envelope"))
      {
         if (name == _T("envelope") &&
             !NeedExtension_(_T("envelope"), _T("the 'envelope' test"), test->line, errorMessage))
            return false;

         // :index/:last on address but not envelope: RFC 5260 6 extends header,
         // address and date - an envelope has one from and one to, so there is
         // nothing to index into.
         if (!CheckTags_(set,
                         name == _T("address")
                            ? _T("comparator is contains matches value count regex all localpart domain user detail index last")
                            : _T("comparator is contains matches value count regex all localpart domain user detail"),
                         _T("'") + name + _T("'"), errorMessage))
            return false;

         if (name == _T("address") && !ValidateIndexArguments_(set, test->line, errorMessage))
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

      if (name == _T("date") || name == _T("currentdate"))
      {
         bool isDate = name == _T("date");

         if (!NeedExtension_(_T("date"), _T("the '") + name + _T("' test"), test->line, errorMessage))
            return false;

         if (!CheckTags_(set,
                         isDate ? _T("comparator is contains matches value count regex zone originalzone index last")
                                : _T("comparator is contains matches value count regex zone"),
                         _T("'") + name + _T("'"), errorMessage))
            return false;

         size_t expectedLists = isDate ? 3u : 2u;
         if (!ValidateMatchArguments_(set, _T("'") + name + _T("'"), test->line, errorMessage, expectedLists))
            return false;

         // The header name (date only) and the date-part are single strings in the
         // grammar, arriving here as one-element lists.
         for (size_t listIndex = 0; listIndex + 1 < expectedLists; listIndex++)
         {
            if (set.stringLists[listIndex].size() != 1)
            {
               errorMessage.Format(isDate
                  ? _T("Line %d: 'date' takes a header name, a date part and a list of keys.")
                  : _T("Line %d: 'currentdate' takes a date part and a list of keys."), test->line);
               return false;
            }
         }

         const String &part = set.stringLists[expectedLists - 2][0];
         if (!IsKnownDatePart(part))
         {
            errorMessage.Format(_T("Line %d: '%s' is not a date part this server knows."), test->line, part.c_str());
            return false;
         }

         if (set.zoneGiven)
         {
            if (set.originalZone)
            {
               errorMessage.Format(_T("Line %d: ':zone' and ':originalzone' contradict each other."), test->line);
               return false;
            }

            int hours = 0, minutes = 0;
            if (!Time::GetTimeAdjustForTimezone(set.zone, hours, minutes))
            {
               errorMessage.Format(_T("Line %d: ':zone' needs an offset of the form \"+hhmm\" or \"-hhmm\"."), test->line);
               return false;
            }
         }

         if (!ValidateIndexArguments_(set, test->line, errorMessage))
            return false;

         return true;
      }

      if (name == _T("ihave"))
      {
         if (!NeedExtension_(_T("ihave"), _T("the 'ihave' test"), test->line, errorMessage))
            return false;

         if (!CheckTags_(set, _T(""), _T("'ihave'"), errorMessage))
            return false;

         if (set.stringLists.size() != 1 || set.stringLists[0].empty())
         {
            errorMessage.Format(_T("Line %d: 'ihave' takes one list of extension names."), test->line);
            return false;
         }

         // Deliberately NOT checked against IsSupportedExtension: testing for an
         // extension this server lacks is the command's whole purpose, and it
         // evaluates false rather than failing the upload.
         return true;
      }

      if (name == _T("environment"))
      {
         if (!NeedExtension_(_T("environment"), _T("the 'environment' test"), test->line, errorMessage))
            return false;

         if (!CheckTags_(set, _T("comparator is contains matches value count regex"),
                         _T("'environment'"), errorMessage))
            return false;

         if (!ValidateMatchArguments_(set, _T("'environment'"), test->line, errorMessage))
            return false;

         // RFC 5183's grammar takes a single item name, then the key list.
         if (set.stringLists[0].size() != 1)
         {
            errorMessage.Format(_T("Line %d: 'environment' takes one item name and a list of keys."), test->line);
            return false;
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

         if (!CheckTags_(set, _T("comparator is contains matches value count regex raw text content"),
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

         if (!CheckTags_(set, _T("comparator is contains matches value count regex"), _T("'hasflag'"), errorMessage))
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
