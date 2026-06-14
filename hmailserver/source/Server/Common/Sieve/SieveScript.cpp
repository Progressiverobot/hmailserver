// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#include "StdAfx.h"

#include "SieveScript.h"

#include "SieveLexer.h"
#include "SieveParser.h"

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
}
