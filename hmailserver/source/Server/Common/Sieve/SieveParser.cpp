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
   SieveParser::IsKnownCommand_(const String &name)
   {
      // Core RFC 5228 control + action commands, plus the commonly required
      // extension commands accepted at the syntactic level.
      static const wchar_t *known[] =
      {
         L"require", L"if", L"elsif", L"else", L"stop", L"keep", L"discard",
         L"fileinto", L"redirect", L"reject", L"ereject", L"vacation",
         L"setflag", L"addflag", L"removeflag", L"notify", L"error", L"return",
         L"include", L"global", L"set"
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
         L"not", L"size", L"true", L"envelope", L"body", L"hasflag",
         L"string", L"date", L"currentdate", L"ihave", L"environment"
      };

      for (const wchar_t *candidate : known)
      {
         if (name.CompareNoCase(candidate) == 0)
            return true;
      }

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

      commands.clear();

      if (!ParseCommands_(commands, true, errorMessage))
         return false;

      if (Current_().type != SieveTokenType::End)
      {
         errorMessage.Format(_T("Line %d: unexpected token after end of script."), Current_().line);
         return false;
      }

      return true;
   }

   bool
   SieveParser::ParseCommands_(std::vector<std::shared_ptr<SieveCommand>> &commands, bool topLevel, String &errorMessage)
   {
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
}
