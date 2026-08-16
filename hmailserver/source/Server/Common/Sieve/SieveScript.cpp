// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#include "StdAfx.h"

#include "SieveScript.h"

#include "SieveLexer.h"
#include "SieveParser.h"
#include "SieveMessage.h"
#include "SieveEvaluator.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   SieveScript::SieveScript()
   {
   }

   bool
   SieveScript::Parse(const String &script, String &errorMessage)
   {
      errorMessage = _T("");
      commands_.clear();

      std::vector<SieveToken> tokens;

      SieveLexer lexer(script);
      if (!lexer.Tokenize(tokens, errorMessage))
         return false;

      SieveParser parser;
      if (!parser.Parse(tokens, commands_, errorMessage))
         return false;

      return true;
   }

   String
   SieveScript::CheckSyntax(const String &script)
   {
      String errorMessage;

      SieveScript sieveScript;
      sieveScript.Parse(script, errorMessage);

      return errorMessage;
   }

   String
   SieveScript::Evaluate(const String &script, const String &rawMessage)
   {
      String errorMessage;

      SieveScript sieveScript;
      if (!sieveScript.Parse(script, errorMessage))
         return _T("error: ") + errorMessage;

      SieveMessage message(rawMessage);
      SieveEvaluator evaluator;
      return evaluator.Evaluate(sieveScript.GetCommands(), message);
   }

   String
   SieveScript::Evaluate(const String &script,
                         const String &rawMessage,
                         const SieveEnvelope &envelope,
                         SieveResult &result,
                         std::function<bool(const String &)> mailboxExists,
                         bool classifiedAsSpam,
                         std::function<bool(const String &, const String &, __int64, bool)> duplicateCheck,
                         std::function<String(const String &, bool)> includeFetch)
   {
      String errorMessage;

      SieveScript sieveScript;
      if (!sieveScript.Parse(script, errorMessage))
      {
         // Leave the result at its defaults, which keep the message. A script that no
         // longer parses - because it was hand-edited on disk, or because a later
         // version of this server tightened the grammar - must not be able to stop
         // mail being delivered.
         result = SieveResult();
         return _T("error: ") + errorMessage;
      }

      SieveMessage message(rawMessage);
      SieveEvaluator evaluator;
      evaluator.SetMailboxExists(mailboxExists);
      evaluator.SetClassifiedAsSpam(classifiedAsSpam);
      evaluator.SetDuplicateCheck(duplicateCheck);
      evaluator.SetIncludeFetch(includeFetch);
      return evaluator.Evaluate(sieveScript.GetCommands(), message, envelope, result);
   }
}
