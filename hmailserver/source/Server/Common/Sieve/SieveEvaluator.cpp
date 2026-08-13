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

         if (set.flagsGiven)
         {
            pinnedFlags_ = set.flags;
            pinnedFlagsGiven_ = true;
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

         String target = FirstString_(set);
         actions_.push_back(_T("redirect:") + target);
         result_->redirects.push_back(target);

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

      // "require" carries no run-time behaviour.
   }

   void
   SieveEvaluator::ExecuteFlagCommand_(const String &name, const std::shared_ptr<SieveCommand> &command)
   {
      SieveArgumentSet set;
      String ignored;
      if (!SieveParser::SplitArguments(command->arguments, set, ignored) || set.stringLists.empty())
         return;

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

      // "hasflag" takes only the key list; the others take a name list first.
      size_t expected = (source == ValueSource::Flags) ? 1u : 2u;
      if (set.stringLists.size() < expected)
         return false;

      std::vector<String> names;
      if (expected == 2)
         names = set.stringLists[0];

      const std::vector<String> &keys = set.stringLists[expected - 1];

      std::vector<String> values;
      CollectValues_(source, set, names, message, values);

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

      if (source == ValueSource::Header)
      {
         for (const String &name : names)
         {
            std::vector<String> headerValues = message.GetHeaderValues(name);
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
