// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#pragma once

#include <vector>
#include <memory>

#include "SieveParser.h"
#include "SieveEvaluator.h"

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
      //
      // This overload has no side effects, which is what makes it safe as the COM
      // "evaluate this script against this message" diagnostic: it decides that a
      // vacation reply is due (the summary carries a "vacation" token) without
      // sending one or recording one as sent.
      static String Evaluate(const String &script, const String &rawMessage);

      // As above, and also fills in the structured result. The delivery path needs
      // this overload rather than the summary string for two reasons: the summary
      // cannot carry a vacation reason or subject (either may contain the ';' the
      // summary is joined with), and the SMTP envelope has to go in for "envelope"
      // tests and the RFC 5230 4.5 recipient check to mean anything - the fallback
      // trace header (Delivered-To) is off in a default install.
      // mailboxExists answers the "mailboxexists" test (RFC 5490) against the
      // recipient's real folder list; pass nothing and that test is false for
      // every mailbox, which is the safe answer when there is no store to ask.
      static String Evaluate(const String &script,
                             const String &rawMessage,
                             const SieveEnvelope &envelope,
                             SieveResult &result,
                             std::function<bool(const String &)> mailboxExists = nullptr,
                             bool classifiedAsSpam = false,
                             std::function<bool(const String &, const String &, __int64, bool)> duplicateCheck = nullptr,
                             std::function<String(const String &, bool)> includeFetch = nullptr);

   private:
      std::vector<std::shared_ptr<SieveCommand>> commands_;
   };
}
