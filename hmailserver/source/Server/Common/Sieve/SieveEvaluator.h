// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#pragma once

#include <vector>
#include <memory>

#include "SieveParser.h"
#include "SieveMessage.h"

namespace HM
{
   // The SMTP envelope of the delivery being filtered.
   //
   // The evaluator can work without it - it falls back to the Return-Path and
   // Delivered-To trace headers that local delivery writes - but "envelope" "to"
   // and the RFC 5230 4.5 vacation recipient check need the real RCPT TO, and
   // Delivered-To is off in the shipped configuration. A caller that has the
   // envelope should pass it.
   struct SieveEnvelope
   {
      bool available = false;
      String from;   // MAIL FROM, empty string for a null return path
      String to;     // the RCPT TO this particular delivery is for
   };

   // A vacation (RFC 5230) auto-reply that the script asked for and that passed
   // every loop-prevention check. Everything needed to send it is here; the
   // sending itself belongs to the delivery path, which owns the account and the
   // message.
   struct SieveVacationDecision
   {
      bool send = false;
      String to;         // the envelope sender the reply goes back to
      String subject;    // ":subject"; empty means "build a Re: from the original"
      String reason;     // the reply body
      String handle;     // ":handle", empty when the script gave none
      __int64 days = 7;  // ":days" - the per-sender suppression window
      bool mime = false; // ":mime" - reason is a complete MIME entity, not text
   };

   // Everything a script decided, for callers that need more than the ';'-joined
   // summary string.
   struct SieveResult
   {
      bool keepLocal = true;         // store a local copy of the message
      String fileInto;               // mailbox for the local copy; empty means INBOX
      bool flagsGiven = false;       // the script set the local copy's IMAP flags
      std::vector<String> flags;     // those flags, canonicalised and de-duplicated
      std::vector<String> redirects; // addresses to send a copy to
      SieveVacationDecision vacation;
   };

   // Evaluates a parsed Sieve AST against a message and produces the resulting
   // action summary. Supports the RFC 5228 control flow (if/elsif/else/stop), the
   // core tests (true/false/not/allof/anyof/header/address/exists/size with
   // :is/:contains/:matches and the default comparator), the core actions
   // (keep/fileinto/discard/redirect, plus implicit keep), and the extensions
   // vacation (RFC 5230), imap4flags (RFC 5232), envelope (RFC 5228 5.4),
   // copy (RFC 3894), relational (RFC 5231) and subaddress (RFC 5233).
   class SieveEvaluator
   {
   public:
      SieveEvaluator();

      // Returns a ';'-joined action summary, e.g. "fileinto:Spam", "discard",
      // "redirect:a@b.com;keep" or "keep" (the implicit default). Side-effect
      // actions add trailing tokens: "flags:\Seen \Flagged" for imap4flags and
      // "vacation" when an auto-reply is due. Those tokens never replace the
      // keep/fileinto/discard/redirect decision, so a caller that does not
      // understand them still delivers the message correctly.
      String Evaluate(const std::vector<std::shared_ptr<SieveCommand>> &commands, const SieveMessage &message);

      // As above, and also fills in the structured result. This is the entry
      // point for the delivery path: the summary string cannot carry a vacation
      // reason or subject safely (either may contain a ';').
      String Evaluate(const std::vector<std::shared_ptr<SieveCommand>> &commands,
                      const SieveMessage &message,
                      const SieveEnvelope &envelope,
                      SieveResult &result);

   private:
      // Where the values a test compares against come from.
      enum class ValueSource { Header, Address, Envelope, Flags };

      void ExecuteCommands_(const std::vector<std::shared_ptr<SieveCommand>> &commands, const SieveMessage &message);
      void ExecuteCommand_(const std::shared_ptr<SieveCommand> &command, const SieveMessage &message);
      void ExecuteFlagCommand_(const String &name, const std::shared_ptr<SieveCommand> &command);
      void ExecuteVacation_(const std::shared_ptr<SieveCommand> &command, const SieveMessage &message);

      bool EvaluateTest_(const std::shared_ptr<SieveTest> &test, const SieveMessage &message);
      bool EvaluateComparisonTest_(const std::shared_ptr<SieveTest> &test, const SieveMessage &message, ValueSource source);
      bool EvaluateExists_(const std::shared_ptr<SieveTest> &test, const SieveMessage &message);
      bool EvaluateSize_(const std::shared_ptr<SieveTest> &test, const SieveMessage &message);

      void CollectValues_(ValueSource source,
                          const SieveArgumentSet &set,
                          const std::vector<String> &names,
                          const SieveMessage &message,
                          std::vector<String> &values) const;

      // The envelope sender / recipient, from the envelope when the caller supplied
      // one and from the trace headers otherwise. False means "not known", which is
      // not the same as the empty string a null return path produces.
      bool GetEnvelopeSender_(const SieveMessage &message, String &sender) const;
      bool GetEnvelopeRecipient_(const SieveMessage &message, String &recipient) const;

      // The RFC 5230 4.6 / RFC 3834 loop-prevention checks. Returns true when no
      // auto-reply may be sent, with reason describing which check stopped it.
      bool SuppressVacation_(const SieveMessage &message,
                             const String &sender,
                             const SieveArgumentSet &set,
                             String &reason) const;

      static bool MatchValue_(const String &matchType, bool caseSensitive, const String &value, const String &key);
      static bool MatchWithArguments_(const SieveArgumentSet &set, const String &value, const String &key);
      static bool CompareRelational_(const String &comparator, const String &relation, const String &value, const String &key);
      static bool ApplyAddressPart_(const String &address, const String &addressPart, String &part);
      static String StripAngleBrackets_(const String &value);
      static String FirstString_(const SieveArgumentSet &set);

      std::vector<String> actions_;
      bool stopped_;

      // True once an action has settled what happens to the local copy, which is
      // what suppresses the implicit keep. Deliberately separate from
      // actions_.empty(): a side-effect action such as addflag or vacation adds an
      // entry without cancelling the implicit keep, and treating it as if it did
      // would silently discard the message.
      bool localDecided_;

      // The imap4flags internal variable, and the flag set pinned by an explicit
      // ":flags" on keep/fileinto.
      std::vector<String> flags_;
      bool flagsTouched_;
      std::vector<String> pinnedFlags_;
      bool pinnedFlagsGiven_;

      bool vacationDecided_;

      SieveEnvelope envelope_;
      SieveResult *result_;
   };
}
