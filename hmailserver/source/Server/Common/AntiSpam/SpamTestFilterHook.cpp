// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#include "StdAfx.h"
#include "SpamTestFilterHook.h"

#include "FilterHookClient.h"
#include "SpamTestData.h"
#include "SpamTestResult.h"

#include "../Application/IniFileSettings.h"
#include "../BO/Message.h"
#include "../BO/MessageData.h"
#include "../BO/MessageRecipient.h"
#include "../BO/MessageRecipients.h"
#include "../Persistence/PersistentMessage.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   String
   SpamTestFilterHook::GetName() const
   {
      return "SpamTestFilterHook";
   }

   bool
   SpamTestFilterHook::GetIsEnabled()
   {
      return !IniFileSettings::Instance()->GetFilterHookUrl().IsEmpty();
   }

   SpamTest::SpamTestType
   SpamTestFilterHook::GetTestType()
   {
      // Post-transmission: the engine is sent the message, so there has to be one.
      return SpamTest::PostTransmission;
   }

   std::set<std::shared_ptr<SpamTestResult> >
   SpamTestFilterHook::RunTest(std::shared_ptr<SpamTestData> pTestData)
   {
      std::set<std::shared_ptr<SpamTestResult> > results;

      const String url = IniFileSettings::Instance()->GetFilterHookUrl();

      if (url.IsEmpty())
         return results;

      std::shared_ptr<MessageData> messageData = pTestData->GetMessageData();

      if (!messageData)
         return results;

      std::shared_ptr<Message> message = messageData->GetMessage();

      if (!message)
         return results;

      const String fileName = PersistentMessage::GetFileName(message);
      const __int64 messageSize = FileUtilities::FileSize(fileName);

      // A ceiling on what is worth sending. The engine is being asked a question
      // about a message, and posting a fifty-megabyte attachment across the network
      // to be told it is not spam costs more than the answer is worth - so an
      // oversized message is passed rather than delayed. Zero means no ceiling, for
      // an engine on the same machine where the copy is cheap.
      const int maxSizeKB = IniFileSettings::Instance()->GetFilterHookMaxMessageSizeKB();

      if (maxSizeKB > 0 && messageSize > (__int64) maxSizeKB * 1024)
      {
         LOG_DEBUG(Formatter::Format("Filter hook: message is {0} bytes, above the {1} KB ceiling. Not sent.",
                                     messageSize, maxSizeKB));
         return results;
      }

      std::vector<AnsiString> recipients;

      if (message->GetRecipients())
      {
         for (std::shared_ptr<MessageRecipient> recipient : message->GetRecipients()->GetVector())
            recipients.push_back(AnsiString(recipient->GetAddress()));
      }

      const IPAddress &originating = pTestData->GetOriginatingIP();

      FilterHookClient::Verdict verdict =
         FilterHookClient::Check(url,
                                 fileName,
                                 AnsiString(pTestData->GetEnvelopeFrom()),
                                 recipients,
                                 originating.IsAny() ? AnsiString("") : AnsiString(originating.ToString()),
                                 AnsiString(pTestData->GetHeloHost()),
                                 IniFileSettings::Instance()->GetFilterHookTimeoutSeconds());

      if (!verdict.answered)
      {
         // The engine did not answer. Whether that stops the message is the one
         // decision an administrator has to make about this feature, and it is not
         // one this server should make for them: fail open and a filter outage lets
         // spam through, fail closed and a filter outage stops the mail.
         //
         // Open is the default because the failure it produces is recoverable and
         // the other one is not - a message deferred while nobody is watching is a
         // message that eventually bounces.
         if (!IniFileSettings::Instance()->GetFilterHookFailClosed())
         {
            LOG_DEBUG("Filter hook: no answer from the filtering engine. Message accepted (FilterHookFailClosed is off).");
            return results;
         }

         String reason = "Blocked by the filter hook: the filtering engine did not answer, and FilterHookFailClosed is on.";

         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5541, "SpamTestFilterHook::RunTest",
            "The filtering engine at " + url + " did not answer. FilterHookFailClosed is on, so the message was "
            "treated as spam. Mail will keep being refused until the engine responds.");

         results.insert(std::shared_ptr<SpamTestResult>(new SpamTestResult(
            GetName(), SpamTestResult::Fail, IniFileSettings::Instance()->GetFilterHookRejectScore(), reason)));

         return results;
      }

      // "reject" is the engine asking for the message to be stopped. It is worth a
      // configured score rather than a special case, so that it still passes through
      // the administrator's thresholds - somebody who has set a very high delete
      // threshold has said something about how much they trust their filters, and
      // this should not be the one place that ignores it.
      double score = verdict.score;

      if (verdict.action == "reject" || verdict.action == "soft reject")
         score = IniFileSettings::Instance()->GetFilterHookRejectScore();

      if (score == 0.0)
      {
         LOG_DEBUG("Filter hook: engine returned action '" + verdict.action + "' with no score. Nothing added.");
         return results;
      }

      String reason = verdict.reason.IsEmpty()
         ? String("Blocked by the filter hook (" + verdict.action + ").")
         : String(verdict.reason);

      // The score is an int here because that is what the spam framework counts in.
      // Rounded rather than truncated: an engine that answers 4.6 means something
      // closer to 5 than to 4, and truncation would quietly make every verdict
      // slightly kinder than the engine intended.
      const int roundedScore = (int) (score < 0 ? score - 0.5 : score + 0.5);

      results.insert(std::shared_ptr<SpamTestResult>(new SpamTestResult(
         GetName(), SpamTestResult::Fail, roundedScore, reason)));

      return results;
   }
}
