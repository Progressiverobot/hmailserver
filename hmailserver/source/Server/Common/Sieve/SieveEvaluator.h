// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#pragma once

#include <vector>
#include <memory>
#include <functional>
#include <map>

#include "SieveParser.h"
#include "SieveMessage.h"
#include "../Util/VariantDateTime.h"

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
   // every loop-prevention check the evaluator can apply. Everything needed to
   // send it is here; the sending itself belongs to SieveVacationResponder, which
   // owns the account and the suppression store.
   //
   // The fields that describe the original message (subject, message id, loop
   // count) are filled in here rather than re-read from the message file by the
   // responder. That is deliberate: the evaluator has the parsed message in front
   // of it already, and a second read would be a second chance for the two to
   // disagree about which message this reply answers - the message file moves into
   // the user's IMAP folder shortly after evaluation, so "read it again later" is
   // not even reliably possible.
   struct SieveVacationDecision
   {
      bool send = false;
      String to;         // the envelope sender the reply goes back to
      String subject;    // ":subject"; empty means "build one from the original"
      String reason;     // the reply body
      String handle;     // ":handle", empty when the script gave none
      String from;       // ":from"; empty means "the account's own address"
      __int64 days = 7;  // ":days" - the per-sender suppression window

      // ":seconds" (RFC 6131), which replaces the days-based window when present.
      // Zero is a legal value and means "reply to every qualifying message", so the
      // flag is what distinguishes "the script said 0" from "the script said
      // nothing".
      bool secondsGiven = false;
      __int64 seconds = 0;

      bool mime = false; // ":mime" - reason is a complete MIME entity, not text

      // Context taken from the message being replied to. Held raw: decoding a
      // MIME-encoded subject needs the MIME machinery, which belongs to the
      // responder that formats the reply.
      String originalSubject;
      String originalMessageId;
      int loopCount = 0;
   };

   // One addheader or deleteheader (RFC 5293), in script order. Order matters:
   // "deleteheader X" then "addheader X" is a replace, the other way round is a
   // no-op plus an add, and the RFC has both meanings.
   struct SieveHeaderEdit
   {
      bool isAdd = false;

      String name;

      // addheader: the value to add; :last appends below existing instances
      // instead of prepending above them.
      String value;
      bool addLast = false;

      // deleteheader: which instances go. indexGiven selects the nth (from the
      // end under indexFromEnd); with patterns given, only instances whose value
      // matches one of them (with the recorded match type / comparator / case
      // rules already resolved to a simple triple here, so the delivery side does
      // not need the evaluator).
      bool indexGiven = false;
      int index = 0;
      bool indexFromEnd = false;
      bool patternsGiven = false;
      std::vector<String> patterns;
      String matchType;              // "is", "contains" or "matches"
      bool caseSensitive = false;    // i;octet
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
      std::vector<SieveHeaderEdit> headerEdits; // editheader (RFC 5293), in script order
      SieveVacationDecision vacation;

      // reject / ereject (RFC 5429). Evaluation runs after the SMTP transaction
      // accepted the message, so a protocol-level refusal is no longer possible;
      // the RFC's mandated fallback is a non-delivery report, which the delivery
      // path sends through the same machinery as a quota bounce - with that
      // machinery's guards (no bounce to a bounce, none to a null or
      // auto-submitted sender) applying unchanged.
      bool rejectGiven = false;
      String rejectReason;
   };

   // Evaluates a parsed Sieve AST against a message and produces the resulting
   // action summary. Supports the RFC 5228 control flow (if/elsif/else/stop), the
   // core tests (true/false/not/allof/anyof/header/address/exists/size with
   // :is/:contains/:matches and the default comparator), the core actions
   // (keep/fileinto/discard/redirect, plus implicit keep), and the extensions
   // vacation (RFC 5230), vacation-seconds (RFC 6131), imap4flags (RFC 5232),
   // envelope (RFC 5228 5.4), copy (RFC 3894), relational (RFC 5231) and
   // subaddress (RFC 5233).
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

      // How the "mailboxexists" test (RFC 5490) asks the mail store a question the
      // Sieve engine cannot answer itself. The delivery path supplies a callback
      // over the recipient's real folder list; a caller that supplies none - the
      // COM test evaluator, which has no account - gets the safe answer: with no
      // way to know, every mailbox is reported as not existing, so scripts fall
      // through to their non-conditional branches rather than acting on a guess.
      void SetMailboxExists(std::function<bool(const String &)> callback);

      // The server's own spam classification for the message being delivered, from
      // the delivery path (Message::GetFlagSpam). The spamtest value can also be
      // derived from the verdict headers when the administrator has them on; this
      // flag is the channel that works when they are off.
      void SetClassifiedAsSpam(bool classified);

      // How the "duplicate" test (RFC 7352) asks its seen-store a question and
      // records the answer's premise: (identifier, handle, windowSeconds,
      // refreshOnSeen) -> was it seen before. The delivery path binds the
      // recipient account's store; a caller that supplies none - the COM test
      // evaluator - gets false for everything, the direction that cannot discard
      // mail.
      void SetDuplicateCheck(std::function<bool(const String &, const String &, __int64, bool)> callback);

   private:
      // Where the values a test compares against come from.
      enum class ValueSource { Header, Address, Envelope, Flags, Body, Environment };

      void ExecuteCommands_(const std::vector<std::shared_ptr<SieveCommand>> &commands, const SieveMessage &message);
      void ExecuteCommand_(const std::shared_ptr<SieveCommand> &command, const SieveMessage &message);
      void ExecuteFlagCommand_(const String &name, const std::shared_ptr<SieveCommand> &command);
      void ExecuteVacation_(const std::shared_ptr<SieveCommand> &command, const SieveMessage &message);

      bool EvaluateTest_(const std::shared_ptr<SieveTest> &test, const SieveMessage &message);

      // The "environment" items (RFC 5183) this server can answer. Returns false
      // for an item it cannot - the caller then contributes no value, making any
      // match against that item false, which is the RFC's unknown-item behaviour.
      static bool GetEnvironmentItem_(const String &name, String &value);

      // spamtest (RFC 3685 / RFC 5235).
      bool EvaluateSpamTest_(const std::shared_ptr<SieveTest> &test, const SieveMessage &message);

      // date / currentdate (RFC 5260).
      bool EvaluateDateTest_(const std::shared_ptr<SieveTest> &test, const SieveMessage &message, bool currentDate);

      // Splits an RFC 5322 date header into the WALL time it states and the zone
      // it states it in, kept apart because the two date tests need to re-express
      // the same instant in a different zone - which Time::GetDateTimeFromMimeHeader
      // cannot do, having already folded its zone into the value. A header with no
      // usable zone is read as server-local time.
      static bool ParseHeaderDateTime_(const String &headerValue, DateTime &wall, int &zoneMinutes);

      // One RFC 5260 4.1 date-part of a wall time in a zone. False for a part
      // this code does not know, which the validator should have refused.
      static bool FormatDatePart_(const DateTime &wall, int zoneMinutes, const String &part, String &value);

      // Applies :index/:last (RFC 5260 6) to the values collected for one header
      // name: the nth instance, counted from the front or the back, or nothing at
      // all when there is no nth - a missing instance must not match even "".
      static void ApplyIndex_(const SieveArgumentSet &set, std::vector<String> &values);

      // The match tail shared by every comparison-shaped test: :count against the
      // number of values, otherwise every value against every key.
      bool MatchValuesAgainstKeys_(const SieveArgumentSet &set, const std::vector<String> &values, const std::vector<String> &keys);

      // variables (RFC 5229). Expansion happens HERE, at evaluation, on the
      // arguments as each command or test consumes them - never in the parser,
      // where ${name} is just text and must stay storable.
      void ExecuteSetCommand_(const std::shared_ptr<SieveCommand> &command);
      String ExpandString_(const String &input) const;
      void ExpandArgumentSet_(SieveArgumentSet &set) const;

      // Wildcard match with RFC 5228 2.7.1 semantics - backslash escapes a
      // following *, ? or backslash - and, on success, the spans each * and ?
      // consumed, in order, for RFC 5229's match variables (${1}..). ${0} is the
      // whole value. Case folds unless caseSensitive.
      static bool WildcardMatchWithCaptures_(const String &pattern, const String &value,
                                             bool caseSensitive, std::vector<String> &captures);

      // The variables (RFC 5229) state: script variables set by "set" and the
      // match variables the most recent successful :matches recorded. Names are
      // stored lowercased - RFC 5229 3 makes them case-insensitive. The enabled
      // flag records whether the script required "variables": without it, "${a}"
      // has no special meaning (RFC 5229 3) and must pass through verbatim, so
      // every expansion site is gated on it.
      bool variables_enabled_ = false;
      std::map<String, String> variables_;
      std::vector<String> match_variables_;
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

      // True for an envelope sender that must never be auto-replied to because of
      // what its address says about it: the RFC 2142 / RFC 3834 robot and
      // list-management local parts. This catches the list traffic that the List-*
      // header check misses.
      static bool IsAutomatedSenderAddress_(const String &sender);

      // An instance member since variables (RFC 5229): a successful :matches
      // records what its wildcards consumed into match_variables_.
      bool MatchValue_(const String &matchType, bool caseSensitive, const String &value, const String &key);
      // Non-static since variables (RFC 5229): a :matches inside records its
      // captures on the instance.
      bool MatchWithArguments_(const SieveArgumentSet &set, const String &value, const String &key);
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

      std::function<bool(const String &)> mailbox_exists_;
      bool classified_as_spam_ = false;
      std::function<bool(const String &, const String &, __int64, bool)> duplicate_check_;
   };
}
