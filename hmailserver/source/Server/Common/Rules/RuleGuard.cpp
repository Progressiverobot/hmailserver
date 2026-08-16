// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#include "StdAfx.h"

#include "RuleGuard.h"

#include "../BO/Account.h"
#include "../BO/Message.h"
#include "../BO/MessageData.h"

#include "../Persistence/PersistentMessage.h"

#include "../Util/FileUtilities.h"
#include "../Util/Strings/Formatter.h"

// Common reaching into Server/SMTP for the configured loop limit. Not ideal layering,
// and not new: MessageUtilities.cpp and SieveVacationResponder.cpp both read
// SMTPConfiguration exactly this way. The limit belongs to the SMTP configuration
// because that is where an administrator sets it, and copying it somewhere tidier
// would give us two numbers that can disagree.
#include "../../SMTP/SMTPConfiguration.h"

// The criterion is evaluated here rather than through
// RegularExpression::TestExactMatch because that function cannot tell us which of the
// two failures happened - see the RegexCriteriaMatches comment in the header. The
// construction and the match are otherwise character for character what TestExactMatch
// does, so a criterion that matched before still matches.
#include <boost/regex.hpp>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // How long one criterion evaluation may take before the pattern is suspended.
      //
      // Local delivery of one message is normally single-digit milliseconds end to end,
      // so a quarter of a second inside ONE comparison is already three orders of
      // magnitude out and cannot be anything but pathological. It is also short enough
      // that a sender feeding crafted subjects cannot accumulate much cost before the
      // breaker trips - which is the point, because the cost that matters is the
      // aggregate over a stream of messages rather than the cost of any one of them.
      const unsigned int EvaluationBudgetMilliseconds = 250;

      // How long a pattern stays suspended after failing to compile or blowing its
      // budget.
      //
      // Long enough that a flood of messages costs one evaluation rather than one per
      // message; short enough that an administrator watching the error log sees the
      // situation recur rather than assuming it went away. Being wrong in either
      // direction is cheap, because a suspended regex criterion only ever means "this
      // rule does not fire", never "this message is discarded". Note that suspension is
      // keyed on the pattern text: a corrected pattern is a different key and is tried
      // at once, so the window never delays a fix.
      const unsigned int SuspensionSeconds = 300;

      // How many distinct patterns may be held in the suspension table.
      //
      // The key comes from the rule set, not from the message, so this table cannot be
      // grown by sending mail. The cap is here because an installation with thousands
      // of rules would otherwise accumulate an entry per pattern for no benefit, and an
      // unbounded map on a delivery path is the kind of thing that gets found two years
      // later. When the table is full and pruning freed nothing we decline to suspend
      // rather than evict: evicting would silently un-suspend a pattern already known
      // to be expensive, and re-paying its cost is the exact failure this mechanism
      // exists to stop.
      const size_t MaxSuspendedPatterns = 256;

      // The longest pattern that will appear in a log line. A criterion is at most 255
      // characters (hm_rule_criterias.criteriamatchvalue is nvarchar(255)), so this
      // truncates almost nothing; it exists so that the size of an error entry stays a
      // property of this code rather than of the rule set.
      const int MaxDescribedPatternLength = 120;

      boost::recursive_mutex regex_state_mutex_;

      // pattern -> GetTickCount64 value at which it may be tried again. Presence in
      // this map also means "already reported", which is what keeps a hostile stream
      // from turning the breaker into a log flood: HM6042 and HM6043 are emitted on
      // insertion, never on a lookup that hits.
      std::map<String, ULONGLONG> suspended_patterns_;

      // Caller must hold regex_state_mutex_.
      void PruneExpiredSuspensions_(ULONGLONG now)
      {
         auto iter = suspended_patterns_.begin();
         while (iter != suspended_patterns_.end())
         {
            if (iter->second <= now)
               iter = suspended_patterns_.erase(iter);
            else
               iter++;
         }
      }

      // The pattern as it should appear in a log line: bounded in length, and with the
      // control characters that would otherwise let it forge extra log lines replaced.
      // A rule set is administrator-supplied and so is not hostile in the way a message
      // is, but a log line is a log line and being careful here costs nothing.
      String DescribePattern_(const String &pattern)
      {
         String described = pattern.Left(MaxDescribedPatternLength);

         described.Replace(_T("\r"), _T(" "));
         described.Replace(_T("\n"), _T(" "));
         described.Replace(_T("\t"), _T(" "));

         if (pattern.GetLength() > MaxDescribedPatternLength)
            described += _T("...");

         return described;
      }

      // True when the pattern is currently suspended. Prunes as it goes, so there is no
      // separate sweep to schedule.
      bool PatternIsSuspended_(const String &pattern)
      {
         boost::lock_guard<boost::recursive_mutex> guard(regex_state_mutex_);

         const ULONGLONG now = ::GetTickCount64();
         PruneExpiredSuspensions_(now);

         auto iter = suspended_patterns_.find(pattern);
         if (iter == suspended_patterns_.end())
            return false;

         return iter->second > now;
      }

      // Suspends the pattern, returning true only if this call is the one that did it -
      // and therefore the one that should report. A second delivery thread arriving with
      // the same pattern inside the window gets false and stays quiet.
      bool SuspendPatternAndClaimReport_(const String &pattern)
      {
         boost::lock_guard<boost::recursive_mutex> guard(regex_state_mutex_);

         const ULONGLONG now = ::GetTickCount64();
         PruneExpiredSuspensions_(now);

         auto iter = suspended_patterns_.find(pattern);
         if (iter != suspended_patterns_.end() && iter->second > now)
            return false;

         if (iter == suspended_patterns_.end() && suspended_patterns_.size() >= MaxSuspendedPatterns)
         {
            // Full, and the prune above already removed everything it could. Declining
            // to suspend means the pattern is evaluated again next time, which is the
            // behaviour we had before this unit existed: worse than suspending, and
            // strictly better than evicting a live entry.
            LOG_DEBUG(_T("RuleGuard: the regex suspension table is full; pattern not suspended."));
            return false;
         }

         suspended_patterns_[pattern] = now + (static_cast<ULONGLONG>(SuspensionSeconds) * 1000ULL);
         return true;
      }
   }

   bool
   RuleGuard::MessageIsUsableForRules(bool loadSucceeded,
                                      std::shared_ptr<const Account> account,
                                      std::shared_ptr<Message> message)
   {
      if (!message)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 6040, "RuleGuard::MessageIsUsableForRules",
            "Rules were not applied: no message was supplied to the rule engine.");
         return false;
      }

      // The filename is recomputed here rather than taken from the MessageData, which
      // keeps it private with no accessor. That is not a workaround, it is the point:
      // this has to be the same derivation LoadFromMessage used, so it is the same
      // (account, message) pair through the same PersistentMessage::GetFileName
      // overload. If the two ever disagree, they disagree together.
      const String fileName = PersistentMessage::GetFileName(account, message);

      const String scope = account ? account->GetAddress() : String(_T("global rules"));

      if (!loadSucceeded)
      {
         // LoadFromMessage said no: a file over the 80 MB parser limit, or one whose
         // MIME parse threw - in which case it has already been copied to "Problematic
         // messages" and reported as HM4218. Either way the MessageData holds nothing,
         // and nothing is not what the rule set was written against.
         String errorMessage = Formatter::Format("Rules were not applied to message {0} ({1}) because its contents could not be read. The message is being delivered unfiltered; a rule matching on an empty subject, body or header would otherwise have acted on it. File: {2}",
            message->GetID(), scope, fileName);

         ErrorManager::Instance()->ReportError(ErrorManager::High, 6040, "RuleGuard::MessageIsUsableForRules", errorMessage);
         return false;
      }

      // LoadFromMessage said yes, which is not the same as "there was a message there".
      // Its bNewMessage branch reports success for a file it could not open, because it
      // cannot tell an unreadable existing message from a message being composed from
      // nothing - both arrive as LoadFromFile returning false. So the file is checked
      // directly: a message this server has accepted always has a file, and that file
      // always has bytes in it.
      //
      // FileSize and not Exists, deliberately. FileUtilities::FileSize passes an
      // error_code to boost::filesystem and answers 0 on any failure, so it covers
      // "missing" and "empty" in one call and cannot throw. FileUtilities::Exists calls
      // boost::filesystem::exists with no error_code, which throws - and an exception
      // escaping a guard on the delivery path would be a worse defect than the one the
      // guard is here to prevent. The two cases are worth telling apart diagnostically
      // and are not worth a throwing call to tell apart.
      if (FileUtilities::FileSize(fileName) <= 0)
      {
         String errorMessage = Formatter::Format("Rules were not applied to message {0} ({1}) because its file is missing or empty, even though the message data reported loading successfully. The message is being delivered unfiltered. File: {2}",
            message->GetID(), scope, fileName);

         ErrorManager::Instance()->ReportError(ErrorManager::High, 6040, "RuleGuard::MessageIsUsableForRules", errorMessage);
         return false;
      }

      return true;
   }

   int
   RuleGuard::LoopCountForGeneratedMessage(std::shared_ptr<MessageData> source)
   {
      if (!source)
      {
         // No source means the depth of the chain cannot be established. Returning the
         // configured limit rather than 0 is deliberate: an unknown depth must not be
         // read as "no hops used", or a lost source would hand the generated message the
         // whole budget - which is this function's own defect arrived at from the other
         // side.
         return Configuration::Instance()->GetSMTPConfiguration()->GetRuleLoopLimit();
      }

      return source->GetRuleLoopCount() + 1;
   }

   void
   RuleGuard::CarryLoopCountForward(std::shared_ptr<MessageData> source,
                                    std::shared_ptr<MessageData> generated)
   {
      if (!generated)
         return;

      generated->SetRuleLoopCount(LoopCountForGeneratedMessage(source));
   }

   bool
   RuleGuard::RegexCriteriaMatches(const String &pattern, const String &subject)
   {
      if (PatternIsSuspended_(pattern))
         return false;

      // Distinguishes which side of the try threw. Boost raises the same
      // std::runtime_error family for a pattern it cannot compile and for a match it
      // gives up on, and those are different problems with different answers.
      bool compiled = false;

      try
      {
         // Constructed from the String itself, not from c_str(), because that is what
         // RegularExpression::TestExactMatch does and the whole point is that a working
         // criterion keeps working. Default flags, i.e. Perl syntax.
         boost::wregex expression(pattern);
         compiled = true;

         const ULONGLONG startTick = ::GetTickCount64();

         // regex_match, not regex_search: the whole value must match. Unchanged.
         const bool matched = boost::regex_match(subject, expression);

         const ULONGLONG elapsed = ::GetTickCount64() - startTick;

         // The expensive-but-successful case. Boost never complains about it, so a
         // wall-clock measurement is the only thing that catches it.
         if (elapsed >= static_cast<ULONGLONG>(EvaluationBudgetMilliseconds) &&
             SuspendPatternAndClaimReport_(pattern))
         {
            String errorMessage = Formatter::Format("A rule regular-expression criterion took {0} ms against a {1} character value, over the {2} ms budget, and will not be evaluated again for {3} seconds. Any sender can make this cost recur once per message, so it is being paid once per pattern instead. Pattern begins: {4}",
               elapsed, subject.GetLength(), EvaluationBudgetMilliseconds, SuspensionSeconds, DescribePattern_(pattern));

            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6043, "RuleGuard::RegexCriteriaMatches", errorMessage);
         }

         return matched;
      }
      catch (const std::runtime_error &)
      {
         // Boost's regex_error derives from std::runtime_error, and this is the only
         // exception type either the constructor or the matcher raises. Catching the
         // base rather than boost::regex_error keeps this identical in reach to the
         // catch in RegularExpression::TestExactMatch that it replaces.
         if (!compiled)
         {
            // The criterion is not a regular expression. Before this it silently never
            // matched, for ever, on an Active rule - which is the hardest kind of
            // configuration fault to find, because everything looks correct.
            if (SuspendPatternAndClaimReport_(pattern))
            {
               String errorMessage = Formatter::Format("A rule regular-expression criterion is not a valid regular expression and can never match. The rule containing it is active and doing nothing. Pattern: {0}",
                  DescribePattern_(pattern));

               ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6042, "RuleGuard::RegexCriteriaMatches", errorMessage);
            }

            return false;
         }

         // The matcher gave up: Boost's state-count heuristic (error_complexity) or its
         // stack guard. Treated as no match, which is what happened before - the
         // difference is that it is now said out loud and not re-attempted on the next
         // message.
         if (SuspendPatternAndClaimReport_(pattern))
         {
            String errorMessage = Formatter::Format("A rule regular-expression criterion was abandoned as too complex against a {0} character value, and will not be evaluated again for {1} seconds. Treated as no match. Pattern begins: {2}",
               subject.GetLength(), SuspensionSeconds, DescribePattern_(pattern));

            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6043, "RuleGuard::RegexCriteriaMatches", errorMessage);
         }

         return false;
      }

      // Unreachable: both the try and the only catch return. It is here because /WX
      // turns "not all control paths return a value" into a build failure, and whether
      // the compiler can see through a try/catch to prove otherwise is not something
      // worth depending on.
      return false;
   }

   bool
   RuleGuard::SieveRegexMatches(const String &pattern, const String &subject, bool caseSensitive)
   {
      if (PatternIsSuspended_(pattern))
         return false;

      bool compiled = false;

      try
      {
         // Perl syntax, as for a rule criterion; icase is what the script's default
         // comparator (i;ascii-casemap) means, and i;octet turns it off.
         boost::wregex expression(pattern,
            caseSensitive ? boost::regex::normal : (boost::regex::normal | boost::regex::icase));
         compiled = true;

         const ULONGLONG startTick = ::GetTickCount64();

         // regex_search, not regex_match: draft-ietf-sieve-regex matches anywhere in
         // the value, and the script anchors with ^ and $ when it means the whole.
         const bool matched = boost::regex_search(subject, expression);

         const ULONGLONG elapsed = ::GetTickCount64() - startTick;

         if (elapsed >= static_cast<ULONGLONG>(EvaluationBudgetMilliseconds) &&
             SuspendPatternAndClaimReport_(pattern))
         {
            String errorMessage = Formatter::Format("A Sieve ':regex' match took {0} ms against a {1} character value, over the {2} ms budget, and will not be evaluated again for {3} seconds. Any sender can make this cost recur once per message, so it is being paid once per pattern instead. Pattern begins: {4}",
               elapsed, subject.GetLength(), EvaluationBudgetMilliseconds, SuspensionSeconds, DescribePattern_(pattern));

            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6043, "RuleGuard::SieveRegexMatches", errorMessage);
         }

         return matched;
      }
      catch (const std::runtime_error &)
      {
         if (!compiled)
         {
            // Upload validation compiles every ':regex' key, so reaching this means a
            // script stored before that check existed, or edited on disk. Saying so
            // matters for the same reason it does for a rule: a test that can never
            // match looks exactly like a test that happens not to.
            if (SuspendPatternAndClaimReport_(pattern))
            {
               String errorMessage = Formatter::Format("A Sieve ':regex' key is not a valid regular expression and can never match. The script containing it is active and that test is doing nothing. Pattern: {0}",
                  DescribePattern_(pattern));

               ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6042, "RuleGuard::SieveRegexMatches", errorMessage);
            }

            return false;
         }

         if (SuspendPatternAndClaimReport_(pattern))
         {
            String errorMessage = Formatter::Format("A Sieve ':regex' match was abandoned as too complex against a {0} character value, and will not be evaluated again for {1} seconds. Treated as no match. Pattern begins: {2}",
               subject.GetLength(), SuspensionSeconds, DescribePattern_(pattern));

            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6043, "RuleGuard::SieveRegexMatches", errorMessage);
         }

         return false;
      }

      // Unreachable, for the same /WX reason as in RegexCriteriaMatches.
      return false;
   }

   void
   RuleGuard::ReportActionFailed(const String &actionName, const String &detail)
   {
      String errorMessage = Formatter::Format("The rule action {0} failed. The message file may have been left partly rewritten, and delivery is continuing with it. Detail: {1}",
         actionName, detail);

      ErrorManager::Instance()->ReportError(ErrorManager::High, 6041, "RuleGuard::ReportActionFailed", errorMessage);
   }

   unsigned int
   RuleGuard::RegexBudgetMilliseconds()
   {
      return EvaluationBudgetMilliseconds;
   }

   unsigned int
   RuleGuard::RegexSuspensionSeconds()
   {
      return SuspensionSeconds;
   }
}
