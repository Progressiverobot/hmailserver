// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#include "StdAfx.h"

#include "SieveEvaluator.h"

#include "../Util/Parsing/StringParser.h"
#include "../Rules/RuleGuard.h"
#include "../Application/Application.h"
#include "../Application/Configuration.h"
#include "../Util/Time.h"
#include "../AntiSpam/AntiSpamConfiguration.h"
#include "SieveScript.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // The sub-address separator for the subaddress extension (RFC 5233). The
      // separator is site-defined; hMailServer's plus addressing uses a
      // per-domain character, but a Sieve test runs against arbitrary headers
      // whose domains are not ours, so a single conventional '+' is used here.
      const wchar_t *SubAddressSeparator = L"+";

      // Saturation point for the numeric comparator, spelled out rather than
      // taken from a macro so it cannot depend on include order.
      const __int64 MaxNumericValue = 0x7FFFFFFFFFFFFFFF;

      // RFC 4790 9.1.1: i;ascii-numeric reads the leading digits of the string.
      // A string that does not start with a digit is "positive infinity" - equal
      // to every other such string and greater than every number.
      __int64 ParseAsciiNumeric(const String &value, bool &infinite)
      {
         infinite = true;

         __int64 result = 0;
         for (int i = 0; i < value.GetLength(); i++)
         {
            wchar_t ch = value[i];
            if (ch < L'0' || ch > L'9')
               break;

            infinite = false;

            if (result > (MaxNumericValue - 9) / 10)
            {
               // Saturate rather than overflow. A header claiming a 19-digit
               // number is not worth a wrapped comparison.
               result = MaxNumericValue;
               break;
            }

            result = result * 10 + (ch - L'0');
         }

         return result;
      }

      int SignOf(int value)
      {
         if (value < 0)
            return -1;

         return value > 0 ? 1 : 0;
      }

      bool ListContainsNoCase(const std::vector<String> &values, const String &candidate)
      {
         for (const String &value : values)
         {
            if (value.CompareNoCase(candidate) == 0)
               return true;
         }

         return false;
      }
   }

   SieveEvaluator::SieveEvaluator() :
      stopped_(false),
      localDecided_(false),
      flagsTouched_(false),
      pinnedFlagsGiven_(false),
      vacationDecided_(false),
      result_(nullptr)
   {
   }

   void
   SieveEvaluator::SetMailboxExists(std::function<bool(const String &)> callback)
   {
      mailbox_exists_ = callback;
   }

   void
   SieveEvaluator::SetClassifiedAsSpam(bool classified)
   {
      classified_as_spam_ = classified;
   }

   void
   SieveEvaluator::SetDuplicateCheck(std::function<bool(const String &, const String &, __int64, bool)> callback)
   {
      duplicate_check_ = callback;
   }

   void
   SieveEvaluator::SetIncludeFetch(std::function<String(const String &, bool)> callback)
   {
      include_fetch_ = callback;
   }

   bool
   SieveEvaluator::GetEnvironmentItem_(const String &name, String &value)
   {
      // Item names are the RFC 5183 registry's, matched exactly: the RFC defines
      // them in lower case and a name is not header-like text.
      if (name == _T("name"))
      {
         value = _T("hMailServer");
         return true;
      }

      if (name == _T("version"))
      {
         value = Application::Instance()->GetVersionNumber();
         return true;
      }

      if (name == _T("location"))
      {
         // Scripts run in LocalDelivery, after final-delivery routing: the Mail
         // Delivery Agent role in RFC 5183 3's vocabulary.
         value = _T("MDA");
         return true;
      }

      if (name == _T("phase"))
      {
         // Likewise: evaluation happens while the message is being delivered,
         // neither before ("pre") nor from a stored mailbox afterwards ("post").
         value = _T("during");
         return true;
      }

      if (name == _T("host"))
      {
         String hostName = Configuration::Instance()->GetHostName();
         if (hostName.IsEmpty())
            return false;

         value = hostName;
         return true;
      }

      if (name == _T("domain"))
      {
         // The host name with its first label removed, per the RFC's example
         // (host "beta.example.com", domain "example.com"). A single-label host
         // has no meaningful domain to report, and an unanswerable item must be
         // absent rather than empty.
         String hostName = Configuration::Instance()->GetHostName();
         int firstDot = hostName.Find(_T("."));
         if (firstDot < 0 || firstDot + 1 >= hostName.GetLength())
            return false;

         value = hostName.Mid(firstDot + 1);
         return true;
      }

      // remote-host, remote-ip and anything future: the sending client's identity
      // is not carried into the evaluator, so these are honestly unknown rather
      // than guessed at.
      return false;
   }

   String
   SieveEvaluator::Evaluate(const std::vector<std::shared_ptr<SieveCommand>> &commands, const SieveMessage &message)
   {
      SieveEnvelope envelope;
      SieveResult result;

      return Evaluate(commands, message, envelope, result);
   }

   String
   SieveEvaluator::Evaluate(const std::vector<std::shared_ptr<SieveCommand>> &commands,
                            const SieveMessage &message,
                            const SieveEnvelope &envelope,
                            SieveResult &result)
   {
      actions_.clear();
      stopped_ = false;
      localDecided_ = false;
      flags_.clear();
      flagsTouched_ = false;
      pinnedFlags_.clear();
      pinnedFlagsGiven_ = false;
      vacationDecided_ = false;
      variables_enabled_ = false;
      scopes_.clear();
      scopes_.push_back(VariableScope());
      global_variables_.clear();
      include_depth_ = 0;
      included_once_.clear();
      returned_ = false;

      envelope_ = envelope;
      result_ = &result;
      result = SieveResult();

      ExecuteCommands_(commands, message);

      // Implicit keep: the message stays unless an action settled its fate.
      if (!localDecided_)
      {
         actions_.push_back(_T("keep"));
         result.keepLocal = true;
      }

      // The flags the local copy ends up with: an explicit ":flags" on the
      // keep/fileinto that stored it wins over the internal flag variable.
      if (pinnedFlagsGiven_)
      {
         result.flagsGiven = true;
         result.flags = pinnedFlags_;
      }
      else if (flagsTouched_)
      {
         result.flagsGiven = true;
         result.flags = flags_;
      }

      String summary;
      for (size_t i = 0; i < actions_.size(); i++)
      {
         if (i > 0)
            summary += _T(";");
         summary += actions_[i];
      }

      // Trailing side-effect tokens. They are appended rather than interleaved so
      // that a caller which only understands keep/fileinto/discard/redirect reads
      // exactly the same delivery decision it always did.
      if (result.flagsGiven)
      {
         summary += _T(";flags:");
         summary += StringParser::JoinVector(result.flags, _T(" "));
      }

      if (result.vacation.send)
         summary += _T(";vacation");

      result_ = nullptr;

      return summary;
   }

   void
   SieveEvaluator::ExecuteCommands_(const std::vector<std::shared_ptr<SieveCommand>> &commands, const SieveMessage &message)
   {
      size_t i = 0;
      while (i < commands.size())
      {
         if (stopped_ || returned_)
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

      if (name == _T("keep") || name == _T("fileinto"))
      {
         SieveArgumentSet set;
         String ignored;
         if (!SieveParser::SplitArguments(command->arguments, set, ignored))
         {
            // The parser validated this script before it could get here, so the
            // two disagreeing is a defect in this file, not a bad script. Report
            // it and fall back to keeping the message.
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5800, _T("SieveEvaluator::ExecuteCommand_"),
               _T("A Sieve action passed validation but could not be split into arguments: ") + ignored);
            return;
         }

         if (variables_enabled_)
            ExpandArgumentSet_(set);

         if (set.flagsGiven)
         {
            pinnedFlags_ = set.flags;
            pinnedFlagsGiven_ = true;
         }

         // The other half of RFC 5429 2.1.1's incompatibility rule: a delivery
         // action after a reject wins over it. "Delivered but also bounced" tells
         // the sender their mail did not arrive when it did.
         if (result_->rejectGiven)
         {
            LOG_APPLICATION(_T("Sieve: a reject was cancelled because the script then delivered the message - RFC 5429 forbids combining them."));
            result_->rejectGiven = false;
            result_->rejectReason.Empty();
         }

         if (name == _T("keep"))
         {
            actions_.push_back(_T("keep"));
            result_->keepLocal = true;
         }
         else
         {
            String mailbox = FirstString_(set);
            actions_.push_back(_T("fileinto:") + mailbox);
            result_->keepLocal = true;
            result_->fileInto = mailbox;
         }

         localDecided_ = true;
         return;
      }

      if (name == _T("discard"))
      {
         actions_.push_back(_T("discard"));
         result_->keepLocal = false;
         result_->fileInto = _T("");
         localDecided_ = true;
         return;
      }

      if (name == _T("redirect"))
      {
         SieveArgumentSet set;
         String ignored;
         if (!SieveParser::SplitArguments(command->arguments, set, ignored))
         {
            // As above: the validator already accepted this script, so a split
            // failure is a defect here. Redirecting to whatever half-parsed
            // address fell out would be worse than not redirecting at all, so
            // give up on the action and let the implicit keep deliver locally.
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5801, _T("SieveEvaluator::ExecuteCommand_"),
               _T("A Sieve redirect passed validation but could not be split into arguments: ") + ignored);
            return;
         }

         if (variables_enabled_)
            ExpandArgumentSet_(set);

         String target = FirstString_(set);
         actions_.push_back(_T("redirect:") + target);
         result_->redirects.push_back(target);

         // As in keep/fileinto: a delivery action after a reject cancels the
         // reject (RFC 5429 2.1.1's incompatibility, resolved in the direction
         // that never misleads the sender about whether their mail went through).
         if (result_->rejectGiven)
         {
            LOG_APPLICATION(_T("Sieve: a reject was cancelled because the script then redirected the message - RFC 5429 forbids combining them."));
            result_->rejectGiven = false;
            result_->rejectReason.Empty();
         }

         // RFC 3894: ":copy" means "in addition to", so it leaves the implicit
         // keep alone. Without it a redirect cancels the local copy - but only
         // the *implicit* one. RFC 5228 4.2 is explicit that redirect does not
         // cancel a keep the script actually asked for, so an earlier keep or
         // fileinto (which is what localDecided_ records) stands. Clearing
         // keepLocal here unconditionally would throw away the local copy of
         // every "keep; redirect ...;" script.
         if (!set.copy && !localDecided_)
         {
            result_->keepLocal = false;
            localDecided_ = true;
         }

         return;
      }

      if (name == _T("stop"))
      {
         stopped_ = true;
         return;
      }

      if (name == _T("setflag") || name == _T("addflag") || name == _T("removeflag"))
      {
         ExecuteFlagCommand_(name, command);
         return;
      }

      if (name == _T("vacation"))
      {
         ExecuteVacation_(command, message);
         return;
      }

      if (name == _T("addheader") || name == _T("deleteheader"))
      {
         // editheader (RFC 5293). The evaluator only DECIDES; the delivery path
         // rewrites the stored file, in script order, from these records. With no
         // structured result - the COM diagnostic path - the summary token below
         // is still produced, so a test evaluation shows what would happen.
         SieveArgumentSet set;
         String ignored;
         if (!SieveParser::SplitArguments(command->arguments, set, ignored))
            return;

         if (variables_enabled_)
            ExpandArgumentSet_(set);

         SieveHeaderEdit edit;
         edit.isAdd = name == _T("addheader");

         if (edit.isAdd)
         {
            if (set.stringLists.size() != 2 || set.stringLists[0].size() != 1 || set.stringLists[1].size() != 1)
               return;

            edit.name = set.stringLists[0][0];
            edit.value = set.stringLists[1][0];
            edit.addLast = set.lastGiven;
         }
         else
         {
            if (set.stringLists.empty() || set.stringLists[0].size() != 1)
               return;

            edit.name = set.stringLists[0][0];
            edit.indexGiven = set.indexGiven;
            edit.index = set.indexValue;
            edit.indexFromEnd = set.lastGiven && set.indexGiven;

            if (set.stringLists.size() >= 2)
            {
               edit.patternsGiven = true;
               edit.patterns = set.stringLists[1];
               edit.matchType = set.matchTypeGiven ? set.matchType : _T("is");
               edit.caseSensitive = set.comparatorGiven && set.comparator.Compare(_T("i;octet")) == 0;
            }
         }

         if (result_)
            result_->headerEdits.push_back(edit);

         actions_.push_back(name + _T(":") + edit.name);
         return;
      }

      if (name == _T("set"))
      {
         ExecuteSetCommand_(command);
         return;
      }

      if (name == _T("reject") || name == _T("ereject"))
      {
         // RFC 5429. The two commands differ in HOW a refusal should happen -
         // ereject prefers the SMTP protocol level - but by the time a script
         // runs, the transaction has long since been accepted, and the RFC's
         // fallback for both is the same: a non-delivery report, and no local
         // copy. RFC 5429 2.1.1 makes combining a reject with a delivery action
         // an error; the safe half of that rule is implemented here - a reject
         // AFTER the script already stored the message somewhere is ignored, and
         // a delivery action after a reject wins over it (both logged), because
         // "delivered but also bounced" misleads the sender, while "delivered
         // despite the script's contradiction" loses nothing.
         if (localDecided_)
         {
            LOG_APPLICATION(_T("Sieve: a reject was ignored because the script had already delivered the message - RFC 5429 forbids combining them."));
            return;
         }

         SieveArgumentSet set;
         String ignored;
         if (!SieveParser::SplitArguments(command->arguments, set, ignored))
            return;

         if (variables_enabled_)
            ExpandArgumentSet_(set);

         if (set.stringLists.size() != 1 || set.stringLists[0].size() != 1)
            return;

         if (result_)
         {
            result_->rejectGiven = true;
            result_->rejectReason = set.stringLists[0][0];
            result_->keepLocal = false;
         }

         localDecided_ = true;
         actions_.push_back(_T("reject"));
         return;
      }

      if (name == _T("include"))
      {
         ExecuteIncludeCommand_(command, message);
         return;
      }

      if (name == _T("notify"))
      {
         // RFC 5435 8's loop rule, applied at the source: a message that is
         // itself auto-generated is never notified about, or two servers
         // notifying each other's notification addresses ping-pong for ever.
         std::vector<String> autoSubmitted = message.GetHeaderValues(_T("Auto-Submitted"));
         if (!autoSubmitted.empty() && autoSubmitted[0].CompareNoCase(_T("no")) != 0)
         {
            LOG_APPLICATION(_T("Sieve: a notify was skipped because the message is itself auto-submitted."));
            return;
         }

         SieveArgumentSet set;
         String ignored;
         if (!SieveParser::SplitArguments(command->arguments, set, ignored))
            return;

         if (variables_enabled_)
            ExpandArgumentSet_(set);

         if (set.stringLists.size() != 1 || set.stringLists[0].size() != 1)
            return;

         String method = set.stringLists[0][0];
         if (method.Mid(0, 7).CompareNoCase(_T("mailto:")) != 0)
            return;

         SieveNotifyDecision decision;

         // The address is the URI's path; RFC 6068 hfields ("?subject=...") are
         // deliberately not interpreted - ":message" is the RFC 5435 way to name
         // the subject, and silently honouring both invites contradictions.
         String target = method.Mid(7);
         int query = target.Find(_T("?"));
         if (query >= 0)
            target = target.Mid(0, query);

         decision.to = target;
         decision.from = set.fromGiven ? set.fromAddress : String(_T(""));
         decision.importance = set.importanceGiven ? set.importance : 2;

         std::vector<String> fromValues = message.GetHeaderValues(_T("From"));
         std::vector<String> subjectValues = message.GetHeaderValues(_T("Subject"));
         decision.originalFrom = fromValues.empty() ? String(_T("")) : fromValues[0];
         decision.originalSubject = subjectValues.empty() ? String(_T("")) : subjectValues[0];

         decision.subject = set.notifyMessageGiven && !set.notifyMessage.IsEmpty()
            ? set.notifyMessage
            : _T("New message: ") + decision.originalSubject;

         // The generated-mail loop counter, carried the same way the vacation
         // decision carries it.
         std::vector<String> loopCounts = message.GetHeaderValues(_T("X-hMailServer-LoopCount"));
         if (!loopCounts.empty())
            decision.loopCount = _wtoi(loopCounts[0].c_str());

         if (result_)
            result_->notifications.push_back(decision);

         actions_.push_back(_T("notify:") + decision.to);
         return;
      }

      if (name == _T("return"))
      {
         // RFC 6609 3.2: stop the CURRENT script. Inside an include, control goes
         // back to the including script; at the top level it behaves like stop.
         if (include_depth_ > 0)
            returned_ = true;
         else
            stopped_ = true;

         return;
      }

      if (name == _T("global"))
      {
         // RFC 6609 3.4: the listed names refer to the shared namespace in THIS
         // script. Declaration only - values arrive via set.
         SieveArgumentSet set;
         String ignored;
         if (!SieveParser::SplitArguments(command->arguments, set, ignored))
            return;

         if (set.stringLists.size() == 1 && !scopes_.empty())
         {
            for (const String &variableName : set.stringLists[0])
            {
               String lowerName = variableName;
               lowerName.ToLower();
               scopes_.back().globalNames.insert(lowerName);
            }
         }

         return;
      }

      if (name == _T("require"))
      {
         // Almost no run-time behaviour - except that RFC 5229 3 gives "${a}" its
         // meaning ONLY under require "variables", so the one thing evaluation
         // needs from a require line is whether that name is on it.
         for (const SieveArgument &argument : command->arguments)
         {
            if (argument.kind != SieveArgument::Kind::StringList)
               continue;

            for (const String &extension : argument.strings)
            {
               if (extension.CompareNoCase(_T("variables")) == 0)
                  variables_enabled_ = true;
            }
         }

         return;
      }
   }

   void
   SieveEvaluator::ExecuteFlagCommand_(const String &name, const std::shared_ptr<SieveCommand> &command)
   {
      SieveArgumentSet set;
      String ignored;
      if (!SieveParser::SplitArguments(command->arguments, set, ignored) || set.stringLists.empty())
         return;

      if (variables_enabled_)
         ExpandArgumentSet_(set);

      std::vector<String> given = SieveParser::SplitFlagList(set.stringLists[0]);

      flagsTouched_ = true;

      if (name == _T("setflag"))
      {
         flags_ = given;
         return;
      }

      if (name == _T("addflag"))
      {
         for (const String &flag : given)
         {
            if (!ListContainsNoCase(flags_, flag))
               flags_.push_back(flag);
         }

         return;
      }

      // removeflag
      std::vector<String> remaining;
      for (const String &flag : flags_)
      {
         if (!ListContainsNoCase(given, flag))
            remaining.push_back(flag);
      }

      flags_ = remaining;
   }

   void
   SieveEvaluator::ExecuteVacation_(const std::shared_ptr<SieveCommand> &command, const SieveMessage &message)
   {
      // RFC 5230 4.7: at most one vacation response per script evaluation. The
      // flag is set before any of the checks below so that a second vacation
      // command cannot re-decide a reply the first one suppressed.
      if (vacationDecided_)
         return;

      vacationDecided_ = true;

      SieveArgumentSet set;
      String ignored;
      if (!SieveParser::SplitArguments(command->arguments, set, ignored))
      {
         // The validator accepted this script, so the two disagreeing is a defect in
         // this file rather than a bad script - the same reasoning as the
         // keep/fileinto and redirect paths above. Not replying is the safe outcome,
         // so there is nothing to fall back to.
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5895, _T("SieveEvaluator::ExecuteVacation_"),
            _T("A Sieve vacation action passed validation but could not be split into arguments: ") + ignored);
         return;
      }

      if (variables_enabled_)
         ExpandArgumentSet_(set);

      if (set.stringLists.empty() || set.stringLists[0].empty())
         return;

      String sender;
      if (!GetEnvelopeSender_(message, sender))
      {
         LOG_DEBUG("Sieve vacation: no auto-reply, the envelope sender of this message is not known.");
         return;
      }

      if (sender.IsEmpty())
      {
         // A null return path is how bounces and other automatic mail identify
         // themselves. Replying to one is the classic way to build a mail loop.
         LOG_DEBUG("Sieve vacation: no auto-reply, the message has a null return path.");
         return;
      }

      String reason;
      if (SuppressVacation_(message, sender, set, reason))
      {
         LOG_DEBUG(_T("Sieve vacation: no auto-reply, ") + reason);
         return;
      }

      SieveVacationDecision &decision = result_->vacation;
      decision.send = true;
      decision.to = sender;
      decision.reason = set.stringLists[0][0];
      decision.mime = set.mime;

      if (set.subjectGiven)
         decision.subject = set.subject;

      if (set.handleGiven)
         decision.handle = set.handle;

      if (set.fromGiven)
      {
         // Recorded, not judged. Whether this address may actually be used is a
         // question about the account, which the evaluator does not have -
         // SieveVacationResponder checks it against the account's own addresses and
         // falls back to the account address when it does not belong to the user.
         // Deciding it here would mean trusting the script.
         decision.from = set.fromAddress;
      }

      if (set.daysGiven)
         decision.days = set.days;

      if (set.secondsGiven)
      {
         decision.secondsGiven = true;
         decision.seconds = set.seconds;
      }

      // Context for the reply that only the original message can supply.
      std::vector<String> subjects = message.GetHeaderValues(_T("Subject"));
      if (!subjects.empty())
         decision.originalSubject = subjects[0];

      std::vector<String> messageIds = message.GetHeaderValues(_T("Message-ID"));
      if (!messageIds.empty())
         decision.originalMessageId = messageIds[0];

      // hMailServer's own generated-mail loop counter. Carrying it into the reply
      // (and refusing to reply once it is at the configured limit) is what stops a
      // vacation reply, a forward, an account rule auto-reply and a bounce from
      // taking turns forever: each generated message increments it, so any cycle
      // that includes at least one of them terminates.
      std::vector<String> loopCounts = message.GetHeaderValues(_T("X-hMailServer-LoopCount"));
      if (!loopCounts.empty())
         decision.loopCount = _wtoi(loopCounts[0].c_str());

      // No entry is added to actions_ and localDecided_ is left alone: a vacation
      // reply says nothing about whether the message itself is kept.
   }

   bool
   SieveEvaluator::SuppressVacation_(const SieveMessage &message,
                                     const String &sender,
                                     const SieveArgumentSet &set,
                                     String &reason) const
   {
      // A sender that is not an address cannot be replied to. Without this check the
      // reply is composed, the response is recorded as sent, and the recipient parser
      // then produces no recipients - so the reply is silently lost and the
      // suppression window is burnt for a sender who never hears anything. Better to
      // decide here, where the reason can be logged.
      if (!StringParser::IsValidEmailAddress(sender))
      {
         reason = _T("the envelope sender is not a valid email address.");
         return true;
      }

      // RFC 3834 2 / RFC 2142: addresses that identify a robot or a list manager
      // rather than a person. This is the other half of the mailing-list check
      // further down: the List-* headers catch a list that follows RFC 2369, and
      // these local parts catch the older listserv and majordomo shapes that do not -
      // the "-request" and "owner-" conventions predate those headers by a decade and
      // are still what an unsubscribe robot answers on.
      if (IsAutomatedSenderAddress_(sender))
      {
         reason = _T("the envelope sender is a robot or list-management address.");
         return true;
      }

      // RFC 3834 2: an automatic response must not be sent to a message that is
      // itself automatic.
      std::vector<String> autoSubmitted = message.GetHeaderValues(_T("Auto-Submitted"));
      if (!autoSubmitted.empty())
      {
         String value = autoSubmitted[0];
         int semicolon = value.Find(_T(";"));
         if (semicolon >= 0)
            value = value.Mid(0, semicolon);
         value.Trim();

         if (value.CompareNoCase(_T("no")) != 0)
         {
            reason = _T("the message carries an Auto-Submitted header.");
            return true;
         }
      }

      std::vector<String> precedence = message.GetHeaderValues(_T("Precedence"));
      if (!precedence.empty())
      {
         String value = precedence[0];
         value.Trim();

         if (value.CompareNoCase(_T("bulk")) == 0 ||
             value.CompareNoCase(_T("list")) == 0 ||
             value.CompareNoCase(_T("junk")) == 0)
         {
            reason = _T("the message has a bulk, list or junk Precedence.");
            return true;
         }
      }

      // RFC 5230 4.6: no reply to mailing-list traffic. Any of the RFC 2369 /
      // RFC 2919 list headers is enough to identify it.
      static const wchar_t *listHeaders[] =
      {
         L"List-Id", L"List-Help", L"List-Subscribe", L"List-Unsubscribe",
         L"List-Post", L"List-Owner", L"List-Archive"
      };

      for (const wchar_t *listHeader : listHeaders)
      {
         if (message.HasHeader(listHeader))
         {
            reason = _T("the message came from a mailing list.");
            return true;
         }
      }

      // The Microsoft convention for "do not auto-reply to this".
      std::vector<String> suppress = message.GetHeaderValues(_T("X-Auto-Response-Suppress"));
      if (!suppress.empty())
      {
         String value = suppress[0];
         value.ToLower();

         if (value.Find(_T("all")) >= 0 || value.Find(_T("oof")) >= 0 || value.Find(_T("autoreply")) >= 0)
         {
            reason = _T("the message asks that auto-replies be suppressed.");
            return true;
         }
      }

      // The server's own spam verdict. This mirrors the account-level auto-reply's
      // "abort when flagged as spam" guard: answering spam confirms the address is
      // live and can feed a loop through a forged sender.
      std::vector<String> spam = message.GetHeaderValues(_T("X-hMailServer-Spam"));
      if (!spam.empty() && spam[0].CompareNoCase(_T("YES")) == 0)
      {
         reason = _T("the message is flagged as spam.");
         return true;
      }

      // The set of addresses that belong to this user: the envelope recipient plus
      // whatever ":addresses" listed.
      std::vector<String> ownAddresses = set.addresses;

      String recipient;
      if (GetEnvelopeRecipient_(message, recipient) && !recipient.IsEmpty())
         ownAddresses.push_back(recipient);

      // Never reply to yourself, however the message got here.
      if (ListContainsNoCase(ownAddresses, sender))
      {
         reason = _T("the message was sent from one of the user's own addresses.");
         return true;
      }

      if (ownAddresses.empty())
      {
         // RFC 5230 4.5 wants the user's address to appear in a recipient header
         // before replying. Without the envelope recipient and without :addresses
         // there is nothing to look for, so the check is skipped rather than
         // guessed at. The suppression above plus the per-sender rate limit still
         // stand between this and a loop.
         LOG_DEBUG("Sieve vacation: the recipient-header check was skipped, no envelope recipient and no :addresses.");
         return false;
      }

      static const wchar_t *recipientHeaders[] =
      {
         L"To", L"Cc", L"Bcc", L"Resent-To", L"Resent-Cc"
      };

      for (const wchar_t *recipientHeader : recipientHeaders)
      {
         std::vector<String> values = message.GetHeaderValues(recipientHeader);
         for (const String &value : values)
         {
            std::vector<String> addresses = SieveMessage::ExtractAddresses(value, _T("all"));
            for (const String &address : addresses)
            {
               if (ListContainsNoCase(ownAddresses, address))
                  return false;
            }
         }
      }

      reason = _T("none of the user's addresses appear in the message's recipient headers.");
      return true;
   }

   bool
   SieveEvaluator::IsAutomatedSenderAddress_(const String &sender)
   {
      int at = sender.Find(_T("@"));
      String localPart = at >= 0 ? sender.Mid(0, at) : sender;
      localPart.ToLower();

      // Exact matches. "postmaster" and "mailer-daemon" are named by RFC 3834 2
      // itself; the rest are the conventional "this mailbox is not read by a person"
      // names, and a holiday notice sent to one of them is at best thrown away and at
      // worst answered by whatever is behind it.
      static const wchar_t *exactNames[] =
      {
         L"mailer-daemon", L"postmaster", L"listserv", L"majordomo",
         L"no-reply", L"noreply", L"donotreply", L"do-not-reply",
         L"bounce", L"bounces", L"nobody"
      };

      for (const wchar_t *exactName : exactNames)
      {
         if (localPart.Compare(exactName) == 0)
            return true;
      }

      // The list-management conventions. "owner-list" and "list-request" are the
      // pre-RFC-2369 way of addressing a list's administrative side, and the
      // "-bounces" / "-confirm" forms are what Mailman uses for its verification and
      // bounce-processing robots. Answering any of them either reaches nobody or
      // reaches a program that answers back.
      static const wchar_t *prefixes[] = { L"owner-", L"bounce-", L"bounces-", L"listserv-" };
      for (const wchar_t *prefix : prefixes)
      {
         if (localPart.StartsWith(prefix))
            return true;
      }

      static const wchar_t *suffixes[] =
      {
         L"-request", L"-owner", L"-bounce", L"-bounces", L"-confirm",
         L"-subscribe", L"-unsubscribe", L"-help", L"-admin", L"-noreply"
      };

      for (const wchar_t *suffix : suffixes)
      {
         if (localPart.EndsWith(suffix))
            return true;
      }

      return false;
   }

   bool
   SieveEvaluator::GetEnvelopeSender_(const SieveMessage &message, String &sender) const
   {
      if (envelope_.available)
      {
         sender = StripAngleBrackets_(envelope_.from);
         return true;
      }

      std::vector<String> values = message.GetHeaderValues(_T("Return-Path"));
      if (values.empty())
         return false;

      sender = StripAngleBrackets_(values[0]);
      return true;
   }

   bool
   SieveEvaluator::GetEnvelopeRecipient_(const SieveMessage &message, String &recipient) const
   {
      if (envelope_.available)
      {
         recipient = StripAngleBrackets_(envelope_.to);
         return true;
      }

      static const wchar_t *candidates[] = { L"Delivered-To", L"X-Original-To", L"X-Envelope-To" };

      for (const wchar_t *candidate : candidates)
      {
         std::vector<String> values = message.GetHeaderValues(candidate);
         if (values.empty())
            continue;

         recipient = StripAngleBrackets_(values[0]);
         return true;
      }

      return false;
   }

   String
   SieveEvaluator::StripAngleBrackets_(const String &value)
   {
      String result = value;
      result.Trim();

      int lt = result.Find(_T("<"));
      if (lt >= 0)
      {
         int gt = result.Find(_T(">"), lt + 1);
         if (gt > lt)
         {
            result = result.Mid(lt + 1, gt - lt - 1);
            result.Trim();
         }
      }

      return result;
   }

   String
   SieveEvaluator::FirstString_(const SieveArgumentSet &set)
   {
      // The mailbox name for fileinto, the address for redirect. Read out of the
      // already-split argument set rather than re-walked from the raw argument
      // list, because a tag's value is a string too: ":flags" swallows the string
      // after it, and anything that walked the raw list would need its own copy
      // of the "which tags take a value" table. Two copies of that table is one
      // opportunity for them to disagree, and the way they would disagree is by
      // returning a tag's value as the mailbox - filing mail into a folder named
      // "\Seen". SieveParser::SplitArguments is the single answer to that
      // question; this reads its output.
      if (set.stringLists.empty() || set.stringLists[0].empty())
         return _T("");

      return set.stringLists[0][0];
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
         return EvaluateComparisonTest_(test, message, ValueSource::Header);

      if (name == _T("address"))
         return EvaluateComparisonTest_(test, message, ValueSource::Address);

      if (name == _T("envelope"))
         return EvaluateComparisonTest_(test, message, ValueSource::Envelope);

      if (name == _T("hasflag"))
         return EvaluateComparisonTest_(test, message, ValueSource::Flags);

      if (name == _T("body"))
         return EvaluateComparisonTest_(test, message, ValueSource::Body);

      if (name == _T("ihave"))
      {
         // True only when EVERY listed extension is implemented (RFC 5463). The
         // answer comes from the same list `require` validates against, so the two
         // can never disagree about what this server has.
         SieveArgumentSet set;
         String ignored;
         if (!SieveParser::SplitArguments(test->arguments, set, ignored))
            return false;

         if (set.stringLists.size() != 1 || set.stringLists[0].empty())
            return false;

         for (const String &extension : set.stringLists[0])
         {
            if (!SieveParser::IsSupportedExtension(extension))
               return false;
         }

         return true;
      }

      if (name == _T("environment"))
         return EvaluateComparisonTest_(test, message, ValueSource::Environment);

      if (name == _T("spamtest"))
         return EvaluateSpamTest_(test, message);

      if (name == _T("valid_notify_method"))
      {
         // RFC 5435 5: true when every listed URI is a method this server could
         // notify by - here, a mailto with a non-empty address.
         SieveArgumentSet set;
         String ignored;
         if (!SieveParser::SplitArguments(test->arguments, set, ignored))
            return false;

         if (variables_enabled_)
            ExpandArgumentSet_(set);

         if (set.stringLists.size() != 1 || set.stringLists[0].empty())
            return false;

         for (const String &uri : set.stringLists[0])
         {
            if (uri.Mid(0, 7).CompareNoCase(_T("mailto:")) != 0 || uri.GetLength() <= 7)
               return false;
         }

         return true;
      }

      if (name == _T("notify_method_capability"))
      {
         // RFC 5435 6: the one registered item is "online", and mailto's honest
         // answer is "maybe" - a mail server cannot know whether the notified
         // mailbox's owner is looking at it.
         SieveArgumentSet set;
         String ignored;
         if (!SieveParser::SplitArguments(test->arguments, set, ignored))
            return false;

         if (variables_enabled_)
            ExpandArgumentSet_(set);

         if (set.stringLists.size() != 3 || set.stringLists[0].size() != 1 || set.stringLists[1].size() != 1)
            return false;

         const String &uri = set.stringLists[0][0];
         if (uri.Mid(0, 7).CompareNoCase(_T("mailto:")) != 0)
            return false;

         std::vector<String> values;
         if (set.stringLists[1][0].CompareNoCase(_T("online")) == 0)
            values.push_back(_T("maybe"));

         return MatchValuesAgainstKeys_(set, values, set.stringLists[2]);
      }

      if (name == _T("string"))
      {
         // The string test (RFC 5229 5): sources against keys with the ordinary
         // match machinery. Both lists expand - comparing "${subject}" against a
         // pattern is the test's entire purpose.
         SieveArgumentSet set;
         String ignored;
         if (!SieveParser::SplitArguments(test->arguments, set, ignored))
            return false;

         if (variables_enabled_)
            ExpandArgumentSet_(set);

         if (set.stringLists.size() != 2)
            return false;

         return MatchValuesAgainstKeys_(set, set.stringLists[0], set.stringLists[1]);
      }

      if (name == _T("duplicate"))
      {
         // RFC 7352. The identifier is source-tagged before it reaches the store,
         // because the RFC requires that a ":uniqueid" value and a Message-ID of
         // the same text do not collide.
         if (!duplicate_check_)
            return false;

         SieveArgumentSet set;
         String ignored;
         if (!SieveParser::SplitArguments(test->arguments, set, ignored))
            return false;

         if (variables_enabled_)
            ExpandArgumentSet_(set);

         String identifier;

         if (set.uniqueIdGiven)
         {
            identifier = _T("uid:") + set.uniqueId;
         }
         else if (set.duplicateHeaderGiven)
         {
            std::vector<String> values = message.GetHeaderValues(set.duplicateHeader);
            if (values.empty() || values[0].IsEmpty())
               return false;

            String headerName = set.duplicateHeader;
            headerName.ToLower();
            identifier = _T("hdr:") + headerName + _T(":") + values[0];
         }
         else
         {
            std::vector<String> values = message.GetHeaderValues(_T("Message-ID"));
            if (values.empty() || values[0].IsEmpty())
            {
               // No Message-ID: never a duplicate, nothing tracked (RFC 7352 3).
               return false;
            }

            identifier = _T("mid:") + values[0];
         }

         // Seven days unless the script narrows it - the window most
         // implementations default to, long enough to catch a mailing-list
         // duplicate arriving via two routes, short enough that the store turns
         // over.
         __int64 window = set.secondsGiven ? set.seconds : 7LL * 86400LL;

         return duplicate_check_(identifier, set.handleGiven ? set.handle : String(_T("")), window, set.lastGiven);
      }

      if (name == _T("date"))
         return EvaluateDateTest_(test, message, false);

      if (name == _T("currentdate"))
         return EvaluateDateTest_(test, message, true);

      if (name == _T("mailboxexists"))
      {
         // RFC 5490 3.1: true only when EVERY listed mailbox exists and can be
         // delivered into. Without a way to ask - no callback - the answer is
         // false for the same reason an unreadable store would give false: acting
         // on "probably exists" files mail into a guess.
         if (!mailbox_exists_)
            return false;

         SieveArgumentSet set;
         String ignored;
         if (!SieveParser::SplitArguments(test->arguments, set, ignored))
            return false;

         if (variables_enabled_)
            ExpandArgumentSet_(set);

         if (set.stringLists.size() != 1 || set.stringLists[0].empty())
            return false;

         for (const String &mailbox : set.stringLists[0])
         {
            if (!mailbox_exists_(mailbox))
               return false;
         }

         return true;
      }

      if (name == _T("exists"))
         return EvaluateExists_(test, message);

      if (name == _T("size"))
         return EvaluateSize_(test, message);

      // Unknown / unsupported tests evaluate to false.
      return false;
   }

   bool
   SieveEvaluator::EvaluateComparisonTest_(const std::shared_ptr<SieveTest> &test, const SieveMessage &message, ValueSource source)
   {
      SieveArgumentSet set;
      String ignored;
      if (!SieveParser::SplitArguments(test->arguments, set, ignored))
         return false;

      if (variables_enabled_)
         ExpandArgumentSet_(set);

      // "hasflag" and "body" take only the key list; the others take a name list
      // first (the header or envelope-part names to look at).
      size_t expected = (source == ValueSource::Flags || source == ValueSource::Body) ? 1u : 2u;
      if (set.stringLists.size() < expected)
         return false;

      std::vector<String> names;
      if (expected == 2)
         names = set.stringLists[0];

      const std::vector<String> &keys = set.stringLists[expected - 1];

      std::vector<String> values;
      CollectValues_(source, set, names, message, values);

      return MatchValuesAgainstKeys_(set, values, keys);
   }

   namespace
   {
      // Shifts a wall time by a signed number of minutes. DateTimeSpan's fields
      // are taken as written, so the sign is applied by choosing the operator.
      DateTime ShiftMinutes(const DateTime &wall, int minutes)
      {
         DateTimeSpan span;
         span.SetDateTimeSpan(0, 0, minutes < 0 ? -minutes : minutes, 0);

         return minutes < 0 ? wall - span : wall + span;
      }

      // Modified Julian Day of a civil date, by the standard integer formula.
      // MJD 0 is 1858-11-17, which this reproduces.
      __int64 ModifiedJulianDay(int year, int month, int day)
      {
         __int64 a = (14 - month) / 12;
         __int64 y = year + 4800 - a;
         __int64 m = month + 12 * a - 3;

         __int64 julianDayNumber = day + (153 * m + 2) / 5 + 365 * y + y / 4 - y / 100 + y / 400 - 32045;

         return julianDayNumber - 2400001;
      }

      String ZoneString(int zoneMinutes)
      {
         int magnitude = zoneMinutes < 0 ? -zoneMinutes : zoneMinutes;

         String zone;
         zone.Format(_T("%c%02d%02d"), zoneMinutes < 0 ? _T('-') : _T('+'), magnitude / 60, magnitude % 60);
         return zone;
      }
   }

   bool
   SieveEvaluator::ParseHeaderDateTime_(const String &headerValue, DateTime &wall, int &zoneMinutes)
   {
      // Tokenised the way Time::GetDateTimeFromMimeHeader does, and tolerant the
      // same way; the difference is that the zone stays SEPARATE from the wall
      // time instead of being folded into it, because the caller needs to
      // re-express the instant in another zone.
      String date = headerValue;
      date.Replace(_T("  "), _T(" "));
      date.TrimLeft();

      std::vector<String> parts = StringParser::SplitString(date, " ");
      if (parts.size() < 3)
         return false;

      size_t index = 0;
      if (parts[0].Find(_T(",")) >= 0 || Time::GetMonthIndex(parts.size() > 1 ? parts[1] : _T("")) > 0)
      {
         // A day-of-week name leads (with or without its comma); skip it. The
         // second condition catches "Tue 05 Aug ..." where the comma was dropped:
         // if the SECOND token is a month name, the first cannot be the day.
         if (!iswdigit(parts[0].GetLength() > 0 ? parts[0][0] : L'x'))
            index++;
      }

      if (index + 2 >= parts.size())
         return false;

      int day = _ttoi(parts[index].c_str());
      int month = static_cast<int>(Time::GetMonthIndex(parts[index + 1]));
      int year = _ttoi(parts[index + 2].c_str());

      if (day < 1 || day > 31 || month < 1 || month > 12)
         return false;

      if (year >= 50 && year <= 99)
         year += 1900;
      else if (year >= 0 && year <= 49)
         year += 2000;

      int hour = 0, minute = 0, second = 0;
      if (index + 3 < parts.size())
      {
         std::vector<String> time = StringParser::SplitString(parts[index + 3], ":");
         if (time.size() >= 2)
         {
            hour = _ttoi(time[0].c_str());
            minute = _ttoi(time[1].c_str());
            second = time.size() >= 3 ? _ttoi(time[2].c_str()) : 0;
         }
      }

      if (wall.SetDateTime(year, month, day, hour, minute, second) != 0)
         return false;

      // The zone, when one is stated and parseable. A header without one is read
      // as server-local time: the least surprising reading for the mail this
      // server actually stores, and the one the legacy date handling also takes.
      int zoneHours = 0, zoneMinutesPart = 0;
      if (index + 4 < parts.size() && Time::GetTimeAdjustForTimezone(parts[index + 4], zoneHours, zoneMinutesPart))
      {
         // GetTimeAdjustForTimezone answers "what do I ADD to reach UTC" - the
         // NEGATED zone, with both components carrying that flipped sign ("+0230"
         // comes back as -2,-30). The zone offset as the header states it is the
         // negation of their sum. Learned the empirical way: the first build
         // reported "+0200" as "-0200" and converted 15:30 the wrong direction.
         zoneMinutes = -(zoneHours * 60 + zoneMinutesPart);
      }
      else
      {
         zoneMinutes = Time::GetUTCRelationMinutes();
      }

      return true;
   }

   bool
   SieveEvaluator::FormatDatePart_(const DateTime &wall, int zoneMinutes, const String &part, String &value)
   {
      if (part.CompareNoCase(_T("year")) == 0)
      {
         value.Format(_T("%04d"), wall.GetYear());
         return true;
      }

      if (part.CompareNoCase(_T("month")) == 0)
      {
         value.Format(_T("%02d"), wall.GetMonth());
         return true;
      }

      if (part.CompareNoCase(_T("day")) == 0)
      {
         value.Format(_T("%02d"), wall.GetDay());
         return true;
      }

      if (part.CompareNoCase(_T("date")) == 0)
      {
         value.Format(_T("%04d-%02d-%02d"), wall.GetYear(), wall.GetMonth(), wall.GetDay());
         return true;
      }

      if (part.CompareNoCase(_T("julian")) == 0)
      {
         value = StringParser::IntToString(ModifiedJulianDay(wall.GetYear(), wall.GetMonth(), wall.GetDay()));
         return true;
      }

      if (part.CompareNoCase(_T("hour")) == 0)
      {
         value.Format(_T("%02d"), wall.GetHour());
         return true;
      }

      if (part.CompareNoCase(_T("minute")) == 0)
      {
         value.Format(_T("%02d"), wall.GetMinute());
         return true;
      }

      if (part.CompareNoCase(_T("second")) == 0)
      {
         value.Format(_T("%02d"), wall.GetSecond());
         return true;
      }

      if (part.CompareNoCase(_T("time")) == 0)
      {
         value.Format(_T("%02d:%02d:%02d"), wall.GetHour(), wall.GetMinute(), wall.GetSecond());
         return true;
      }

      if (part.CompareNoCase(_T("zone")) == 0)
      {
         value = ZoneString(zoneMinutes);
         return true;
      }

      if (part.CompareNoCase(_T("weekday")) == 0)
      {
         // GetDayOfWeek is 1=Sunday..7=Saturday; RFC 5260 wants 0=Sunday..6.
         value = StringParser::IntToString(wall.GetDayOfWeek() - 1);
         return true;
      }

      if (part.CompareNoCase(_T("iso8601")) == 0)
      {
         int magnitude = zoneMinutes < 0 ? -zoneMinutes : zoneMinutes;
         value.Format(_T("%04d-%02d-%02dT%02d:%02d:%02d%c%02d:%02d"),
            wall.GetYear(), wall.GetMonth(), wall.GetDay(),
            wall.GetHour(), wall.GetMinute(), wall.GetSecond(),
            zoneMinutes < 0 ? _T('-') : _T('+'), magnitude / 60, magnitude % 60);
         return true;
      }

      if (part.CompareNoCase(_T("std11")) == 0)
      {
         static const wchar_t *dayNames[] = { L"Sun", L"Mon", L"Tue", L"Wed", L"Thu", L"Fri", L"Sat" };
         static const wchar_t *monthNames[] = { L"Jan", L"Feb", L"Mar", L"Apr", L"May", L"Jun",
                                                L"Jul", L"Aug", L"Sep", L"Oct", L"Nov", L"Dec" };

         value.Format(_T("%s, %02d %s %04d %02d:%02d:%02d %s"),
            dayNames[wall.GetDayOfWeek() - 1], wall.GetDay(), monthNames[wall.GetMonth() - 1],
            wall.GetYear(), wall.GetHour(), wall.GetMinute(), wall.GetSecond(),
            ZoneString(zoneMinutes).c_str());
         return true;
      }

      return false;
   }

   void
   SieveEvaluator::ApplyIndex_(const SieveArgumentSet &set, std::vector<String> &values)
   {
      if (!set.indexGiven)
         return;

      size_t index = static_cast<size_t>(set.indexValue);

      if (index < 1 || index > values.size())
      {
         // No nth instance: the test has nothing to match, not even "".
         values.clear();
         return;
      }

      String selected = set.lastGiven ? values[values.size() - index] : values[index - 1];
      values.clear();
      values.push_back(selected);
   }

   bool
   SieveEvaluator::EvaluateSpamTest_(const std::shared_ptr<SieveTest> &test, const SieveMessage &message)
   {
      SieveArgumentSet set;
      String ignored;
      if (!SieveParser::SplitArguments(test->arguments, set, ignored))
         return false;

      if (variables_enabled_)
         ExpandArgumentSet_(set);

      if (set.stringLists.size() != 1 || set.stringLists[0].empty())
         return false;

      // The verdict comes from the message's spam FLAG alone, handed in by the
      // delivery path. The verdict HEADERS are deliberately not consulted: a
      // sender can write X-hMailServer-Spam and X-hMailServer-Reason-Score into
      // their own message, and inbound mail is not stripped of them - so a test
      // reading them would let senders steer recipients' filters in both
      // directions, including downgrading a real verdict with a forged low score.
      // The flag cannot be forged from outside; it exists only in this process.
      //
      // This also decides the granularity honestly: the pipeline persists no
      // score for mail it did NOT classify (AddSpamScoreHeaders runs only on
      // classification), so a graded 2..9 midrange has nothing real to be
      // derived from. The answer is 10 (100 under :percent) for classified mail
      // and "0" - the RFC's "no information" - for everything else, including
      // tested-but-clean, which the pipeline records nowhere.
      String value;
      if (!classified_as_spam_)
         value = _T("0");
      else if (set.percentGiven)
         value = _T("100");
      else
         value = _T("10");

      std::vector<String> values;
      values.push_back(value);

      return MatchValuesAgainstKeys_(set, values, set.stringLists[0]);
   }

   bool
   SieveEvaluator::EvaluateDateTest_(const std::shared_ptr<SieveTest> &test, const SieveMessage &message, bool currentDate)
   {
      SieveArgumentSet set;
      String ignored;
      if (!SieveParser::SplitArguments(test->arguments, set, ignored))
         return false;

      if (variables_enabled_)
         ExpandArgumentSet_(set);

      size_t expected = currentDate ? 2u : 3u;
      if (set.stringLists.size() != expected)
         return false;

      for (size_t listIndex = 0; listIndex + 1 < expected; listIndex++)
      {
         if (set.stringLists[listIndex].size() != 1)
            return false;
      }

      const String &part = set.stringLists[expected - 2][0];
      const std::vector<String> &keys = set.stringLists[expected - 1];

      int localZone = Time::GetUTCRelationMinutes();

      int targetZone = localZone;
      if (set.zoneGiven)
      {
         int hours = 0, minutes = 0;
         if (!Time::GetTimeAdjustForTimezone(set.zone, hours, minutes))
            return false;

         // Negated for the same reason as in ParseHeaderDateTime_: the helper
         // answers "what do I add to reach UTC", not the offset as written.
         targetZone = -(hours * 60 + minutes);
      }

      std::vector<String> values;

      if (currentDate)
      {
         // The current LOCAL wall time, re-expressed in the requested zone.
         DateTime wall = DateTime::GetCurrentTime();
         wall = ShiftMinutes(wall, targetZone - localZone);

         String value;
         if (FormatDatePart_(wall, targetZone, part, value))
            values.push_back(value);
      }
      else
      {
         const String &headerName = set.stringLists[0][0];

         std::vector<String> headerValues = message.GetHeaderValues(headerName);
         ApplyIndex_(set, headerValues);

         for (const String &headerValue : headerValues)
         {
            DateTime wall;
            int headerZone = 0;
            if (!ParseHeaderDateTime_(headerValue, wall, headerZone))
               continue;

            int expressedZone = headerZone;
            if (!set.originalZone)
            {
               // Default and :zone both re-express the instant; :originalzone
               // keeps the header's own wall clock.
               expressedZone = targetZone;
               wall = ShiftMinutes(wall, expressedZone - headerZone);
            }

            String value;
            if (FormatDatePart_(wall, expressedZone, part, value))
               values.push_back(value);
         }
      }

      return MatchValuesAgainstKeys_(set, values, keys);
   }

   String
   SieveEvaluator::ExpandString_(const String &input) const
   {
      // RFC 5229 3: "${name}" becomes the variable's value, an unset variable
      // becomes the empty string, and text that LOOKS like a reference but is not
      // a valid one stays verbatim. "${1}".."${9}" and "${0}" are the match
      // variables of the most recent successful :matches.
      if (input.Find(_T("${")) < 0)
         return input;

      String output;
      int length = input.GetLength();

      for (int i = 0; i < length; i++)
      {
         if (input[i] != L'$' || i + 1 >= length || input[i + 1] != L'{')
         {
            output += input[i];
            continue;
         }

         int close = input.Find(_T("}"), i + 2);
         if (close < 0)
         {
            output += input[i];
            continue;
         }

         String name = input.Mid(i + 2, close - (i + 2));

         bool validName = !name.IsEmpty();
         bool allDigits = true;
         for (int n = 0; n < name.GetLength() && validName; n++)
         {
            wchar_t ch = name[n];
            if (ch >= L'0' && ch <= L'9')
               continue;

            allDigits = false;

            bool nameChar = (ch >= L'a' && ch <= L'z') || (ch >= L'A' && ch <= L'Z') || ch == L'_';
            if (!nameChar)
               validName = false;
         }

         if (!validName)
         {
            // Not a variable reference; the text stays as written.
            output += input[i];
            continue;
         }

         if (allDigits)
         {
            // A match variable, always private to the current script level.
            // Leading zeroes are legal ("${01}" is "${1}").
            int index = _ttoi(name.c_str());
            if (!scopes_.empty() && index >= 0 &&
                static_cast<size_t>(index) < scopes_.back().matchVariables.size())
               output += scopes_.back().matchVariables[index];
         }
         else if (!scopes_.empty())
         {
            String lowerName = name;
            lowerName.ToLower();

            // A name this level declared "global" reads from the shared
            // namespace; anything else is the level's own (RFC 6609 3.4).
            if (scopes_.back().globalNames.count(lowerName) > 0)
            {
               auto found = global_variables_.find(lowerName);
               if (found != global_variables_.end())
                  output += found->second;
            }
            else
            {
               auto found = scopes_.back().privateVariables.find(lowerName);
               if (found != scopes_.back().privateVariables.end())
                  output += found->second;
            }
         }

         i = close;
      }

      return output;
   }

   void
   SieveEvaluator::ExpandArgumentSet_(SieveArgumentSet &set) const
   {
      for (std::vector<String> &list : set.stringLists)
      {
         for (String &value : list)
            value = ExpandString_(value);
      }

      // The tag values that carry user text. Deliberately NOT the zone (an offset
      // is not prose), the comparator or the match type (grammar, not data).
      set.subject = ExpandString_(set.subject);
      set.handle = ExpandString_(set.handle);
      set.fromAddress = ExpandString_(set.fromAddress);
      set.uniqueId = ExpandString_(set.uniqueId);
      set.duplicateHeader = ExpandString_(set.duplicateHeader);

      for (String &address : set.addresses)
         address = ExpandString_(address);

      for (String &flag : set.flags)
         flag = ExpandString_(flag);
   }

   bool
   SieveEvaluator::WildcardMatchWithCaptures_(const String &pattern, const String &value,
                                              bool caseSensitive, std::vector<String> &captures)
   {
      // Greedy descent with backtracking, recording the span each * and ? consumed.
      // Small and iterative-recursive: patterns come from scripts, and the
      // backtracking is linear in practice because '*' segments are anchored by the
      // literal text between them.
      String p = pattern;
      String v = value;

      if (!caseSensitive)
      {
         p.ToLower();
         v.ToLower();
      }

      struct Frame
      {
         int patternIndex;
         int valueIndex;
         size_t capturesSize;
      };

      std::vector<std::pair<int, int>> spans; // wildcard position in pattern -> [start,end) in value

      std::function<bool(int, int)> matchFrom = [&](int pi, int vi) -> bool
      {
         while (pi < p.GetLength())
         {
            wchar_t pc = p[pi];

            if (pc == L'\\' && pi + 1 < p.GetLength())
            {
               // RFC 5228 2.7.1: a backslash makes the next character literal.
               if (vi >= v.GetLength() || v[vi] != p[pi + 1])
                  return false;
               pi += 2;
               vi += 1;
               continue;
            }

            if (pc == L'?')
            {
               if (vi >= v.GetLength())
                  return false;
               spans.push_back(std::make_pair(vi, vi + 1));
               if (matchFrom(pi + 1, vi + 1))
                  return true;
               spans.pop_back();
               return false;
            }

            if (pc == L'*')
            {
               // Try the longest tail first: RFC 5229 3.1 wants the match variables
               // from the greedy ("leftmost-longest") interpretation.
               for (int take = v.GetLength() - vi; take >= 0; take--)
               {
                  spans.push_back(std::make_pair(vi, vi + take));
                  if (matchFrom(pi + 1, vi + take))
                     return true;
                  spans.pop_back();
               }
               return false;
            }

            if (vi >= v.GetLength() || v[vi] != pc)
               return false;

            pi++;
            vi++;
         }

         return vi == v.GetLength();
      };

      spans.clear();
      if (!matchFrom(0, 0))
         return false;

      // Captures come from the ORIGINAL (unfolded) value, so a script that files
      // into "${1}" sees the text as it arrived, not lowercased.
      captures.clear();
      captures.push_back(value);
      for (const std::pair<int, int> &span : spans)
         captures.push_back(value.Mid(span.first, span.second - span.first));

      return true;
   }

   void
   SieveEvaluator::ExecuteIncludeCommand_(const std::shared_ptr<SieveCommand> &command, const SieveMessage &message)
   {
      SieveArgumentSet set;
      String ignored;
      if (!SieveParser::SplitArguments(command->arguments, set, ignored))
         return;

      if (set.stringLists.size() != 1 || set.stringLists[0].size() != 1)
         return;

      String scriptName = set.stringLists[0][0];

      bool global = false, once = false, optional = false;
      for (const String &tag : set.tags)
      {
         if (tag == _T("global")) global = true;
         else if (tag == _T("once")) once = true;
         else if (tag == _T("optional")) optional = true;
      }

      // Three levels is past what any real script layering needs, and this
      // recursion runs on the delivery thread; a pair of scripts including each
      // other must cost three fetches, not a stack.
      if (include_depth_ >= 3)
      {
         LOG_APPLICATION(_T("Sieve: an include was skipped because scripts are nested more than three deep - a cycle, most likely."));
         return;
      }

      String onceKey = (global ? _T("g:") : _T("p:")) + scriptName;
      if (once && included_once_.count(onceKey) > 0)
         return;

      String scriptText = include_fetch_ ? include_fetch_(scriptName, global) : String(_T(""));

      if (scriptText.IsEmpty())
      {
         // RFC 6609 3.1: a missing script is an error - unless :optional says it
         // is expected. The fail-safe spelling of "error" at delivery time is the
         // same as for an unparsable active script: say so and carry on, so a
         // renamed helper cannot stop mail.
         if (!optional)
            LOG_APPLICATION(_T("Sieve: the included script \"") + scriptName + _T("\" does not exist; the include was skipped."));

         return;
      }

      SieveScript includedScript;
      String errorMessage;
      if (!includedScript.Parse(scriptText, errorMessage))
      {
         LOG_APPLICATION(_T("Sieve: the included script \"") + scriptName + _T("\" no longer parses and was skipped: ") + errorMessage);
         return;
      }

      if (once)
         included_once_.insert(onceKey);

      // The included script runs in its own variable scope (RFC 6609 3.4): its
      // variables are private unless it declares them global, and its match
      // variables never leak into the includer. "return" unwinds exactly one
      // level.
      scopes_.push_back(VariableScope());
      include_depth_++;

      // Whether "${a}" means anything is per script (RFC 5229 3): a child's
      // require "variables" must not switch expansion on for the parent's
      // remaining commands, so the flag is restored on the way out.
      bool parentVariablesEnabled = variables_enabled_;

      ExecuteCommands_(includedScript.GetCommands(), message);

      variables_enabled_ = parentVariablesEnabled;
      include_depth_--;
      scopes_.pop_back();
      returned_ = false;
   }

   void
   SieveEvaluator::ExecuteSetCommand_(const std::shared_ptr<SieveCommand> &command)
   {
      SieveArgumentSet set;
      String ignored;
      if (!SieveParser::SplitArguments(command->arguments, set, ignored))
         return;

      if (set.stringLists.size() != 2 || set.stringLists[0].size() != 1 || set.stringLists[1].size() != 1)
         return;

      String name = set.stringLists[0][0];
      name.ToLower();

      // The value is expanded first - "set "b" "${a}"" copies - then the modifiers
      // apply in RFC 5229 4.1's precedence order, highest first.
      String value = ExpandString_(set.stringLists[1][0]);

      bool wantsLower = false, wantsUpper = false, wantsLowerFirst = false,
           wantsUpperFirst = false, wantsQuoteWildcard = false, wantsLength = false;

      for (const String &tag : set.tags)
      {
         if (tag == _T("lower")) wantsLower = true;
         else if (tag == _T("upper")) wantsUpper = true;
         else if (tag == _T("lowerfirst")) wantsLowerFirst = true;
         else if (tag == _T("upperfirst")) wantsUpperFirst = true;
         else if (tag == _T("quotewildcard")) wantsQuoteWildcard = true;
         else if (tag == _T("length")) wantsLength = true;
      }

      if (wantsLower)
         value.ToLower();
      else if (wantsUpper)
         value.ToUpper();

      if (!value.IsEmpty())
      {
         if (wantsLowerFirst)
            value.SetAt(0, towlower(value[0]));
         else if (wantsUpperFirst)
            value.SetAt(0, towupper(value[0]));
      }

      if (wantsQuoteWildcard)
      {
         String quoted;
         for (int i = 0; i < value.GetLength(); i++)
         {
            wchar_t ch = value[i];
            if (ch == L'*' || ch == L'?' || ch == L'\\')
               quoted += L'\\';
            quoted += ch;
         }
         value = quoted;
      }

      if (wantsLength)
         value = StringParser::IntToString(static_cast<__int64>(value.GetLength()));

      if (scopes_.empty())
         return;

      if (scopes_.back().globalNames.count(name) > 0)
         global_variables_[name] = value;
      else
         scopes_.back().privateVariables[name] = value;
   }

   bool
   SieveEvaluator::MatchValuesAgainstKeys_(const SieveArgumentSet &set, const std::vector<String> &values, const std::vector<String> &keys)
   {
      if (set.matchType == _T("count"))
      {
         // RFC 5231 4.2: the count is compared numerically. The comparator is
         // forced rather than taken from the script, which the validator has
         // already restricted to i;ascii-numeric.
         String count = StringParser::IntToString(static_cast<__int64>(values.size()));

         for (const String &key : keys)
         {
            if (CompareRelational_(_T("i;ascii-numeric"), set.relation, count, key))
               return true;
         }

         return false;
      }

      for (const String &value : values)
      {
         for (const String &key : keys)
         {
            if (MatchWithArguments_(set, value, key))
               return true;
         }
      }

      return false;
   }

   void
   SieveEvaluator::CollectValues_(ValueSource source,
                                  const SieveArgumentSet &set,
                                  const std::vector<String> &names,
                                  const SieveMessage &message,
                                  std::vector<String> &values) const
   {
      if (source == ValueSource::Flags)
      {
         values = flags_;
         return;
      }

      if (source == ValueSource::Body)
      {
         values = message.GetBodyValues(set.bodyTransform, set.contentTypes);
         return;
      }

      if (source == ValueSource::Environment)
      {
         // RFC 5183: one value per known item; an item this server cannot answer
         // contributes NOTHING, so the test is false for it - including against
         // the empty key, which scripts use to ask "is this item known at all".
         for (const String &name : names)
         {
            String value;
            if (GetEnvironmentItem_(name, value))
               values.push_back(value);
         }

         return;
      }

      if (source == ValueSource::Header)
      {
         for (const String &name : names)
         {
            std::vector<String> headerValues = message.GetHeaderValues(name);

            // :index/:last (RFC 5260 6) select among THIS name's instances.
            ApplyIndex_(set, headerValues);

            for (const String &headerValue : headerValues)
               values.push_back(headerValue);
         }

         return;
      }

      if (source == ValueSource::Address)
      {
         for (const String &name : names)
         {
            std::vector<String> headerValues = message.GetHeaderValues(name);

            ApplyIndex_(set, headerValues);

            for (const String &headerValue : headerValues)
            {
               std::vector<String> addresses = SieveMessage::ExtractAddresses(headerValue, _T("all"));
               for (const String &address : addresses)
               {
                  String part;
                  if (ApplyAddressPart_(address, set.addressPart, part))
                     values.push_back(part);
               }
            }
         }

         return;
      }

      // ValueSource::Envelope. The envelope-part names the validator allows are
      // "from" and "to"; an envelope value that is not known contributes nothing,
      // which is not the same as contributing the empty string (a null return
      // path legitimately compares equal to "").
      for (const String &name : names)
      {
         String address;
         bool known = false;

         if (name.CompareNoCase(_T("from")) == 0)
            known = GetEnvelopeSender_(message, address);
         else if (name.CompareNoCase(_T("to")) == 0)
            known = GetEnvelopeRecipient_(message, address);

         if (!known)
            continue;

         String part;
         if (ApplyAddressPart_(address, set.addressPart, part))
            values.push_back(part);
      }
   }

   bool
   SieveEvaluator::ApplyAddressPart_(const String &address, const String &addressPart, String &part)
   {
      if (addressPart.CompareNoCase(_T("domain")) == 0)
      {
         int at = address.Find(_T("@"));
         part = at >= 0 ? address.Mid(at + 1) : _T("");
         return true;
      }

      if (addressPart.CompareNoCase(_T("all")) == 0)
      {
         part = address;
         return true;
      }

      int at = address.Find(_T("@"));
      String localPart = at >= 0 ? address.Mid(0, at) : address;

      if (addressPart.CompareNoCase(_T("localpart")) == 0)
      {
         part = localPart;
         return true;
      }

      int separator = localPart.Find(SubAddressSeparator);

      if (addressPart.CompareNoCase(_T("user")) == 0)
      {
         // RFC 5233: the whole local part when there is no separator.
         part = separator >= 0 ? localPart.Mid(0, separator) : localPart;
         return true;
      }

      if (addressPart.CompareNoCase(_T("detail")) == 0)
      {
         // With no separator the detail is absent, and an absent detail matches
         // nothing at all - not even the empty string.
         if (separator < 0)
            return false;

         part = localPart.Mid(separator + 1);
         return true;
      }

      part = address;
      return true;
   }

   bool
   SieveEvaluator::EvaluateExists_(const std::shared_ptr<SieveTest> &test, const SieveMessage &message)
   {
      SieveArgumentSet set;
      String ignored;
      if (!SieveParser::SplitArguments(test->arguments, set, ignored) || set.stringLists.empty())
         return false;

      if (variables_enabled_)
         ExpandArgumentSet_(set);

      const std::vector<String> &headerNames = set.stringLists[0];
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
      SieveArgumentSet set;
      String ignored;
      if (!SieveParser::SplitArguments(test->arguments, set, ignored) || !set.hasNumber)
         return false;

      if (set.sizeOver)
         return message.GetSize() > set.number;

      return message.GetSize() < set.number;
   }

   bool
   SieveEvaluator::MatchWithArguments_(const SieveArgumentSet &set, const String &value, const String &key)
   {
      if (set.matchType == _T("value"))
         return CompareRelational_(set.comparator, set.relation, value, key);

      if (set.comparator.Compare(_T("i;ascii-numeric")) == 0)
      {
         // The numeric comparator only defines equality (RFC 4790 9.1.1); the
         // validator has already refused :contains and :matches with it.
         return CompareRelational_(set.comparator, _T("eq"), value, key);
      }

      bool caseSensitive = set.comparator.CompareNoCase(_T("i;octet")) == 0;
      return MatchValue_(set.matchType, caseSensitive, value, key);
   }

   bool
   SieveEvaluator::CompareRelational_(const String &comparator, const String &relation, const String &value, const String &key)
   {
      int comparison;

      if (comparator.Compare(_T("i;ascii-numeric")) == 0)
      {
         bool valueInfinite = false;
         bool keyInfinite = false;
         __int64 left = ParseAsciiNumeric(value, valueInfinite);
         __int64 right = ParseAsciiNumeric(key, keyInfinite);

         if (valueInfinite && keyInfinite)
            comparison = 0;
         else if (valueInfinite)
            comparison = 1;
         else if (keyInfinite)
            comparison = -1;
         else
            comparison = left < right ? -1 : (left > right ? 1 : 0);
      }
      else if (comparator.Compare(_T("i;octet")) == 0)
      {
         comparison = SignOf(value.Compare(key));
      }
      else
      {
         String lowerValue = value;
         String lowerKey = key;
         lowerValue.ToLower();
         lowerKey.ToLower();
         comparison = SignOf(lowerValue.Compare(lowerKey));
      }

      if (relation.CompareNoCase(_T("gt")) == 0)
         return comparison > 0;

      if (relation.CompareNoCase(_T("ge")) == 0)
         return comparison >= 0;

      if (relation.CompareNoCase(_T("lt")) == 0)
         return comparison < 0;

      if (relation.CompareNoCase(_T("le")) == 0)
         return comparison <= 0;

      if (relation.CompareNoCase(_T("ne")) == 0)
         return comparison != 0;

      // "eq", and the safe default for anything the validator let through.
      return comparison == 0;
   }

   bool
   SieveEvaluator::MatchValue_(const String &matchType, bool caseSensitive, const String &value, const String &key)
   {
      if (matchType == _T("regex"))
      {
         // draft-ietf-sieve-regex: a search, case-folded under the default
         // comparator. Runs under RuleGuard's budget-and-suspend breaker, because a
         // script author is exactly as able to write a catastrophic pattern as a
         // rule author, and this is the delivery thread.
         return RuleGuard::SieveRegexMatches(key, value, caseSensitive);
      }

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
         // The capturing matcher honours RFC 5228 2.7.1's backslash escapes -
         // which StringParser::WildcardMatchNoCase never did - and records what
         // each wildcard consumed for RFC 5229's ${1}.. match variables. The
         // recording is gated: without require "variables" the captures have no
         // reader and RFC 5229 3.1 scopes the behaviour to the extension.
         std::vector<String> captures;
         if (!WildcardMatchWithCaptures_(key, value, caseSensitive, captures))
            return false;

         if (variables_enabled_ && !scopes_.empty())
            scopes_.back().matchVariables = captures;

         return true;
      }

      // ":is" (the default): an exact match.
      if (caseSensitive)
         return value.Compare(key) == 0;

      return value.CompareNoCase(key) == 0;
   }
}
