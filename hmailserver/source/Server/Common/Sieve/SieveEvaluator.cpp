// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#include "StdAfx.h"

#include "SieveEvaluator.h"

#include "../Util/Parsing/StringParser.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // Splits a test's argument list into the tags, positional string-lists and
      // a single numeric argument that the core tests consume.
      struct TestArguments
      {
         String matchType = _T("is");
         String comparator = _T("i;ascii-casemap");
         String addressPart = _T("all");
         bool sizeOver = true;
         bool hasNumber = false;
         __int64 number = 0;
         std::vector<std::vector<String>> stringLists;
      };

      TestArguments SplitArguments(const std::vector<SieveArgument> &arguments)
      {
         TestArguments result;

         for (size_t i = 0; i < arguments.size(); i++)
         {
            const SieveArgument &argument = arguments[i];

            if (argument.kind == SieveArgument::Kind::Tag)
            {
               String tag = argument.tag;
               tag.ToLower();

               if (tag == _T("comparator"))
               {
                  // The comparator value is the following string argument.
                  if (i + 1 < arguments.size() && arguments[i + 1].kind == SieveArgument::Kind::StringList &&
                      !arguments[i + 1].strings.empty())
                  {
                     result.comparator = arguments[i + 1].strings[0];
                     i++;
                  }
               }
               else if (tag == _T("is") || tag == _T("contains") || tag == _T("matches"))
               {
                  result.matchType = tag;
               }
               else if (tag == _T("all") || tag == _T("localpart") || tag == _T("domain"))
               {
                  result.addressPart = tag;
               }
               else if (tag == _T("over"))
               {
                  result.sizeOver = true;
               }
               else if (tag == _T("under"))
               {
                  result.sizeOver = false;
               }
            }
            else if (argument.kind == SieveArgument::Kind::Number)
            {
               result.number = argument.number;
               result.hasNumber = true;
            }
            else
            {
               result.stringLists.push_back(argument.strings);
            }
         }

         return result;
      }
   }

   SieveEvaluator::SieveEvaluator() :
      stopped_(false)
   {
   }

   String
   SieveEvaluator::Evaluate(const std::vector<std::shared_ptr<SieveCommand>> &commands, const SieveMessage &message)
   {
      actions_.clear();
      stopped_ = false;

      ExecuteCommands_(commands, message);

      // Implicit keep when the script took no action.
      if (actions_.empty())
         actions_.push_back(_T("keep"));

      String summary;
      for (size_t i = 0; i < actions_.size(); i++)
      {
         if (i > 0)
            summary += _T(";");
         summary += actions_[i];
      }

      return summary;
   }

   void
   SieveEvaluator::ExecuteCommands_(const std::vector<std::shared_ptr<SieveCommand>> &commands, const SieveMessage &message)
   {
      size_t i = 0;
      while (i < commands.size())
      {
         if (stopped_)
            return;

         const std::shared_ptr<SieveCommand> &command = commands[i];

         if (command->name.CompareNoCase(_T("if")) == 0)
         {
            bool taken = false;
            if (command->test && EvaluateTest_(command->test, message))
            {
               ExecuteCommands_(command->block, message);
               taken = true;
            }

            i++;

            // Consume the matching elsif/else chain.
            while (i < commands.size() &&
                   (commands[i]->name.CompareNoCase(_T("elsif")) == 0 ||
                    commands[i]->name.CompareNoCase(_T("else")) == 0))
            {
               const std::shared_ptr<SieveCommand> &branch = commands[i];

               if (!taken && !stopped_)
               {
                  if (branch->name.CompareNoCase(_T("elsif")) == 0)
                  {
                     if (branch->test && EvaluateTest_(branch->test, message))
                     {
                        ExecuteCommands_(branch->block, message);
                        taken = true;
                     }
                  }
                  else
                  {
                     ExecuteCommands_(branch->block, message);
                     taken = true;
                  }
               }

               i++;
            }

            continue;
         }

         ExecuteCommand_(command, message);
         i++;
      }
   }

   void
   SieveEvaluator::ExecuteCommand_(const std::shared_ptr<SieveCommand> &command, const SieveMessage &message)
   {
      String name = command->name;
      name.ToLower();

      if (name == _T("keep"))
      {
         actions_.push_back(_T("keep"));
      }
      else if (name == _T("discard"))
      {
         actions_.push_back(_T("discard"));
      }
      else if (name == _T("fileinto"))
      {
         actions_.push_back(_T("fileinto:") + FirstPositionalString_(command));
      }
      else if (name == _T("redirect"))
      {
         actions_.push_back(_T("redirect:") + FirstPositionalString_(command));
      }
      else if (name == _T("stop"))
      {
         stopped_ = true;
      }
      // require / setflag / other commands are no-ops for action evaluation.
   }

   String
   SieveEvaluator::FirstPositionalString_(const std::shared_ptr<SieveCommand> &command)
   {
      for (const SieveArgument &argument : command->arguments)
      {
         if (argument.kind == SieveArgument::Kind::StringList && !argument.strings.empty())
            return argument.strings[0];
      }

      return _T("");
   }

   bool
   SieveEvaluator::EvaluateTest_(const std::shared_ptr<SieveTest> &test, const SieveMessage &message)
   {
      if (!test)
         return false;

      String name = test->name;
      name.ToLower();

      if (name == _T("true"))
         return true;

      if (name == _T("false"))
         return false;

      if (name == _T("not"))
      {
         if (test->tests.empty())
            return false;

         return !EvaluateTest_(test->tests[0], message);
      }

      if (name == _T("allof"))
      {
         for (const std::shared_ptr<SieveTest> &child : test->tests)
         {
            if (!EvaluateTest_(child, message))
               return false;
         }
         return true;
      }

      if (name == _T("anyof"))
      {
         for (const std::shared_ptr<SieveTest> &child : test->tests)
         {
            if (EvaluateTest_(child, message))
               return true;
         }
         return false;
      }

      if (name == _T("header"))
         return EvaluateHeaderLike_(test, message, false);

      if (name == _T("address"))
         return EvaluateHeaderLike_(test, message, true);

      if (name == _T("exists"))
         return EvaluateExists_(test, message);

      if (name == _T("size"))
         return EvaluateSize_(test, message);

      // Unknown / unsupported tests evaluate to false.
      return false;
   }

   bool
   SieveEvaluator::EvaluateHeaderLike_(const std::shared_ptr<SieveTest> &test, const SieveMessage &message, bool isAddress)
   {
      TestArguments args = SplitArguments(test->arguments);
      if (args.stringLists.size() < 2)
         return false;

      bool caseSensitive = args.comparator.CompareNoCase(_T("i;octet")) == 0;

      const std::vector<String> &headerNames = args.stringLists[0];
      const std::vector<String> &keys = args.stringLists[1];

      for (const String &headerName : headerNames)
      {
         std::vector<String> values = message.GetHeaderValues(headerName);

         for (const String &value : values)
         {
            std::vector<String> candidates;
            if (isAddress)
               candidates = SieveMessage::ExtractAddresses(value, args.addressPart);
            else
               candidates.push_back(value);

            for (const String &candidate : candidates)
            {
               for (const String &key : keys)
               {
                  if (MatchValue_(args.matchType, caseSensitive, candidate, key))
                     return true;
               }
            }
         }
      }

      return false;
   }

   bool
   SieveEvaluator::EvaluateExists_(const std::shared_ptr<SieveTest> &test, const SieveMessage &message)
   {
      TestArguments args = SplitArguments(test->arguments);
      if (args.stringLists.empty())
         return false;

      const std::vector<String> &headerNames = args.stringLists[0];
      if (headerNames.empty())
         return false;

      for (const String &headerName : headerNames)
      {
         if (!message.HasHeader(headerName))
            return false;
      }

      return true;
   }

   bool
   SieveEvaluator::EvaluateSize_(const std::shared_ptr<SieveTest> &test, const SieveMessage &message)
   {
      TestArguments args = SplitArguments(test->arguments);
      if (!args.hasNumber)
         return false;

      if (args.sizeOver)
         return message.GetSize() > args.number;

      return message.GetSize() < args.number;
   }

   bool
   SieveEvaluator::MatchValue_(const String &matchType, bool caseSensitive, const String &value, const String &key)
   {
      if (matchType == _T("contains"))
      {
         if (caseSensitive)
            return value.Find(key) >= 0;

         String lowerValue = value;
         String lowerKey = key;
         lowerValue.ToLower();
         lowerKey.ToLower();
         return lowerValue.Find(lowerKey) >= 0;
      }

      if (matchType == _T("matches"))
      {
         // The default comparator (i;ascii-casemap) is case-insensitive.
         return StringParser::WildcardMatchNoCase(key, value);
      }

      // ":is" (the default): an exact match.
      if (caseSensitive)
         return value.Compare(key) == 0;

      return value.CompareNoCase(key) == 0;
   }
}
