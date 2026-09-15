// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// See AlertManager.h.

#include "StdAfx.h"

#include "AlertManager.h"

#include "AuditTrail.h"

#include "FileUtilities.h"
#include "FileInfo.h"
#include "HttpsClient.h"
#include "Time.h"
#include "Hashing/HashCreator.h"

#include "../BO/Message.h"
#include "../BO/MessageRecipients.h"
#include "../BO/SSLCertificate.h"
#include "../BO/SSLCertificates.h"
#include "../Persistence/PersistentMessage.h"
#include "../SQL/DALRecordset.h"
#include "../SQL/SQLCommand.h"
#include "../SQL/SQLStatement.h"

#include "../../SMTP/RecipientParser.h"

#include <openssl/pem.h>
#include <openssl/x509.h>

#include <ctime>

#ifdef HM_PLATFORM_POSIX
namespace
{
   // The same one-line shim AcmeClient.cpp carries, for the same reason:
   // _mkgmtime is Microsoft's name for the inverse of gmtime - a struct tm read
   // as UTC, where mktime would read it as local time - and POSIX spells the
   // same function timegm. Duplicated rather than shared because it exists to
   // keep one call site reading the way it does on Windows, and a header for it
   // would be a header for three lines.
   inline time_t _mkgmtime(struct tm *parts)
   {
      return ::timegm(parts);
   }
}
#endif

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   const wchar_t *AlertManager::ConditionBackupFailed = L"backup.failed";
   const wchar_t *AlertManager::ConditionCertificateExpiring = L"certificate.expiring";
   const wchar_t *AlertManager::ConditionDiskLow = L"disk.low";
   const wchar_t *AlertManager::ConditionQueueStalled = L"queue.stalled";
   const wchar_t *AlertManager::ConditionAutoBanStorm = L"autoban.storm";
   const wchar_t *AlertManager::ConditionMinidumpWritten = L"minidump.written";

   namespace
   {
      const int MaxSummaryLength = 250;
      const int MaxDetailLength = 4000;

      String OneLine(const String &value)
      {
         String result = value;

         for (int i = 0; i < result.GetLength(); i++)
         {
            if (result[i] == '\r' || result[i] == '\n' || result[i] == '\t')
               result[i] = ' ';
         }

         return result;
      }

      String Shorten(const String &value, int maximum)
      {
         if (value.GetLength() <= maximum)
            return value;

         // The ellipsis counts towards the maximum, so the result is never longer
         // than asked for: 4,000 meant 4,003 before, one past what SQL Server
         // Compact takes in a string parameter.
         if (maximum <= 3)
            return value.Mid(0, maximum);

         return value.Mid(0, maximum - 3) + _T("...");
      }

      AnsiString JsonEscape(const AnsiString &value)
      {
         AnsiString result;
         result.reserve(value.GetLength() + 8);

         for (int i = 0; i < value.GetLength(); i++)
         {
            char character = value[i];

            switch (character)
            {
            case '\"':
               result += "\\\"";
               break;
            case '\\':
               result += "\\\\";
               break;
            case '\r':
               result += "\\r";
               break;
            case '\n':
               result += "\\n";
               break;
            case '\t':
               result += "\\t";
               break;
            default:
               if (static_cast<unsigned char>(character) >= 0x20)
                  result += character;
               break;
            }
         }

         return result;
      }

      String SeverityWord(int severity)
      {
         switch (severity)
         {
         case ErrorManager::Critical: return _T("critical");
         case ErrorManager::High:     return _T("high");
         case ErrorManager::Medium:   return _T("medium");
         default:                     return _T("low");
         }
      }

      String TimeText(__int64 seconds)
      {
         time_t value = (time_t) seconds;

         tm utc = {};
         gmtime_s(&utc, &value);

         String text;
         text.Format(_T("%04d-%02d-%02d %02d:%02d:%02dZ"), utc.tm_year + 1900, utc.tm_mon + 1, utc.tm_mday,
            utc.tm_hour, utc.tm_min, utc.tm_sec);

         return text;
      }

      // A condition that is an occurrence rather than a state. Kept here rather
      // than in the table because it is a property of what the condition MEANS,
      // not of how an administrator has configured it: there is no "the backup
      // is still failing", there is only "a backup failed".
      bool IsOccurrenceCondition(const String &condition)
      {
         return condition == AlertManager::ConditionBackupFailed ||
                condition == AlertManager::ConditionMinidumpWritten;
      }
   }

   AlertManager::AlertManager()
   {

   }

   AlertManager::~AlertManager()
   {

   }

   AnsiString
   AlertManager::SignWebhook(const AnsiString &secret, __int64 timestamp, const AnsiString &body)
   {
      AnsiString signed_payload;
      signed_payload.Format("%I64d.", timestamp);
      signed_payload += body;

      return HashCreator::ComputeHMACSHA256Hex(secret, signed_payload);
   }

   int
   AlertManager::WebhookBackoffMinutes(int attemptsSoFar)
   {
      switch (attemptsSoFar)
      {
      // The first retry is on the next pass rather than after a wait: an
      // endpoint that was restarting, or a name that had not resolved yet, is
      // the commonest reason a first attempt fails, and a minute is already the
      // shortest delay AlertTask can offer.
      case 0:  return 0;
      case 1:  return 1;
      case 2:  return 5;
      case 3:  return 15;
      default: return 60;
      }
   }

   AlertManager::Rule
   AlertManager::ReadRule_(const std::shared_ptr<DALRecordset> &recordset)
   {
      Rule rule;

      rule.id = recordset->GetInt64Value("alertruleid");
      rule.condition = recordset->GetStringValue("alertrulecondition");
      rule.enabled = recordset->GetLongValue("alertruleenabled") != 0;
      rule.threshold = recordset->GetLongValue("alertrulethreshold");
      rule.actions = recordset->GetLongValue("alertruleactions");
      rule.address = recordset->GetStringValue("alertruleaddress");
      rule.webhook = recordset->GetStringValue("alertrulewebhook");
      rule.secret = recordset->GetStringValue("alertrulesecret");
      rule.cooldown = recordset->GetLongValue("alertrulecooldown");
      rule.digest = recordset->GetLongValue("alertruledigest") != 0;

      return rule;
   }

   AlertManager::Event
   AlertManager::ReadEvent_(const std::shared_ptr<DALRecordset> &recordset)
   {
      Event event;

      event.id = recordset->GetInt64Value("alerteventid");
      event.condition = recordset->GetStringValue("alerteventcondition");
      event.severity = recordset->GetLongValue("alerteventseverity");
      event.state = recordset->GetLongValue("alerteventstate");
      event.time = recordset->GetInt64Value("alerteventtime");
      event.summary = recordset->GetStringValue("alerteventsummary");
      event.detail = recordset->GetStringValue("alerteventdetail");
      event.notified = recordset->GetLongValue("alerteventnotified") != 0;
      event.digested = recordset->GetLongValue("alerteventdigested") != 0;
      event.hook_state = recordset->GetLongValue("alerteventhookstate");
      event.hook_tries = recordset->GetLongValue("alerteventhooktries");
      event.hook_next = recordset->GetInt64Value("alerteventhooknext");

      return event;
   }

   bool
   AlertManager::GetRules(std::vector<Rule> &rules)
   {
      rules.clear();

      SQLCommand command(_T("select * from hm_alertrules order by alertrulecondition asc"));

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!recordset)
         return false;

      while (!recordset->IsEOF())
      {
         rules.push_back(ReadRule_(recordset));
         recordset->MoveNext();
      }

      return true;
   }

   bool
   AlertManager::GetRule(const String &condition, Rule &rule)
   {
      // alertrulecondition is nvarchar(64), and the condition comes from a REST path.
      if (condition.IsEmpty() || condition.GetLength() > 64)
         return false;

      SQLCommand command(_T("select * from hm_alertrules where alertrulecondition = @CONDITION"));
      command.AddParameter("@CONDITION", condition);

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!recordset || recordset->IsEOF())
         return false;

      rule = ReadRule_(recordset);
      return true;
   }

   bool
   AlertManager::SaveRule(const Rule &rule, String &refusal)
   {
      refusal = _T("");

      String condition = rule.condition;
      condition.Trim();

      // Conditions are lower-case constants. PostgreSQL compares = case-sensitively,
      // so a rule saved as "Disk.Low" would sit beside the shipped "disk.low" there
      // and never be the one the evaluator reads; the other three backends would
      // have matched it. One case, on every backend.
      condition.MakeLower();

      if (condition.IsEmpty() || condition.GetLength() > 64)
      {
         refusal = _T("a rule's condition is between one and sixty-four characters");
         return false;
      }

      if ((rule.actions & (ActionMail | ActionWebhook)) == 0)
      {
         refusal = _T("a rule does at least one of mail (1) and webhook (2)");
         return false;
      }

      if ((rule.actions & ActionWebhook) != 0)
      {
         if (rule.webhook.IsEmpty())
         {
            refusal = _T("a rule that calls a webhook needs the address of one");
            return false;
         }

         // A signature nobody can check is not a signature, and an unsigned body
         // arriving at a URL somebody found in a configuration file is an alert
         // anyone can forge.
         if (rule.secret.IsEmpty())
         {
            refusal = _T("a rule that calls a webhook needs a shared secret, so the body it sends can be verified");
            return false;
         }
      }

      if (rule.cooldown < 0 || rule.cooldown > 10080)
      {
         refusal = _T("a cool-down is between 0 minutes and a week");
         return false;
      }

      Rule existing;
      bool exists = GetRule(condition, existing);

      SQLStatement statement(exists ? SQLStatement::STUpdate : SQLStatement::STInsert, _T("hm_alertrules"));

      if (!exists)
         statement.AddColumn(_T("alertrulecondition"), condition, 64);

      statement.AddColumn(_T("alertruleenabled"), (long) (rule.enabled ? 1 : 0));
      statement.AddColumn(_T("alertrulethreshold"), (long) rule.threshold);
      statement.AddColumn(_T("alertruleactions"), (long) rule.actions);
      statement.AddColumn(_T("alertruleaddress"), rule.address, 255);
      statement.AddColumn(_T("alertrulewebhook"), rule.webhook, 1024);
      statement.AddColumn(_T("alertrulesecret"), rule.secret, 255);
      statement.AddColumn(_T("alertrulecooldown"), (long) rule.cooldown);
      statement.AddColumn(_T("alertruledigest"), (long) (rule.digest ? 1 : 0));

      if (exists)
         statement.AddWhereClauseColumn(_T("alertrulecondition"), condition);
      else
         statement.SetIdentityColumn(_T("alertruleid"));

      String errorMessage;
      if (!Application::Instance()->GetDBManager()->Execute(statement.GetCommand(), nullptr, 0, errorMessage))
      {
         refusal = _T("the rule could not be saved: ") + errorMessage;
         return false;
      }

      return true;
   }

   bool
   AlertManager::ReadNewestEvent_(const String &condition, Event &event)
   {
      // Two statements, not one with a subquery: SQL Server Compact refuses a
      // scalar subquery compared with '=', and this runs for every condition on
      // every evaluation, so on that backend it logged an error a minute for as
      // long as the server was up. A max() with no matching row is one row
      // holding NULL, which is "never raised".
      SQLCommand newestCommand(_T("select max(alerteventid) as newestid from hm_alertevents where alerteventcondition = @CONDITION"));
      newestCommand.AddParameter("@CONDITION", condition);

      std::shared_ptr<DALRecordset> newest = Application::Instance()->GetDBManager()->OpenRecordset(newestCommand);
      if (!newest || newest->IsEOF() || newest->GetIsNull("newestid"))
         return false;

      SQLCommand command(_T("select * from hm_alertevents where alerteventid = @EVENTID"));
      command.AddParameter("@EVENTID", newest->GetInt64Value("newestid"));

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!recordset || recordset->IsEOF())
         return false;

      event = ReadEvent_(recordset);
      return true;
   }

   bool
   AlertManager::IsRaised(const String &condition)
   {
      Event event;
      if (!ReadNewestEvent_(condition, event))
         return false;

      return event.state == StateRaised;
   }

   int
   AlertManager::CountNotifiedSince_(__int64 since)
   {
      SQLCommand command(_T("select count(*) as alertcount from hm_alertevents where alerteventnotified = 1 and alerteventdigested = 0 and alerteventtime >= @SINCE"));
      command.AddParameter("@SINCE", since);

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!recordset || recordset->IsEOF())
         return 0;

      return recordset->GetLongValue("alertcount");
   }

   /*
      One event row. Whether it will be sent at once or held for the digest is
      decided HERE and stored, rather than being worked out again at delivery
      time: the cool-down and the ceiling are questions about the moment the
      condition fired, and a minute later - when AlertTask runs - the answer
      would be different.
   */
   bool
   AlertManager::WriteEvent_(const Rule &rule, int severity, int state, const String &summary, const String &detail)
   {
      __int64 now = (__int64) time(nullptr);

      bool digested = rule.digest && Configuration::Instance()->GetAlertDigestEnabled();

      if (!digested && rule.cooldown > 0)
      {
         // Inside the cool-down: recorded, but it waits for the digest rather
         // than becoming a second message about the same thing.
         SQLCommand command(_T("select max(alerteventtime) as lasttime from hm_alertevents ")
                            _T("where alerteventcondition = @CONDITION and alerteventnotified = 1 and alerteventdigested = 0"));
         command.AddParameter("@CONDITION", rule.condition);

         std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
         if (recordset && !recordset->IsEOF() && !recordset->GetIsNull("lasttime"))
         {
            __int64 lastTime = recordset->GetInt64Value("lasttime");

            if (lastTime > 0 && now - lastTime < (__int64) rule.cooldown * 60)
               digested = true;
         }
      }

      if (!digested)
      {
         // The ceiling, across every condition. Past it everything goes to the
         // digest - which is the difference between an alerting system an
         // administrator keeps and one they switch off in the first hour.
         int maximum = Configuration::Instance()->GetAlertMaxPerHour();

         if (CountNotifiedSince_(now - 3600) >= maximum)
         {
            digested = true;

            LOG_APPLICATION(Formatter::Format("Alerts: {0} alerts have been sent in the last hour, which is the ceiling (AlertMaxPerHour). Everything that fires from now until the hour is out goes into the digest instead.", maximum));
         }
      }

      SQLStatement statement(SQLStatement::STInsert, _T("hm_alertevents"));
      statement.AddColumn(_T("alerteventcondition"), rule.condition, 64);
      statement.AddColumn(_T("alerteventseverity"), (long) severity);
      statement.AddColumn(_T("alerteventstate"), (long) state);
      statement.AddColumnInt64(_T("alerteventtime"), now);
      statement.AddColumn(_T("alerteventsummary"), Shorten(OneLine(summary), MaxSummaryLength), 255);
      statement.AddColumn(_T("alerteventdetail"), Shorten(detail, MaxDetailLength));
      statement.AddColumn(_T("alerteventnotified"), (long) 0);
      statement.AddColumn(_T("alerteventdigested"), (long) (digested ? 1 : 0));
      statement.AddColumn(_T("alerteventhookstate"),
         (long) (((rule.actions & ActionWebhook) != 0 && !rule.webhook.IsEmpty()) ? HookPending : HookNotRequired));
      statement.AddColumn(_T("alerteventhooktries"), (long) 0);
      statement.AddColumnInt64(_T("alerteventhooknext"), now);
      statement.SetIdentityColumn(_T("alerteventid"));

      String errorMessage;
      if (!Application::Instance()->GetDBManager()->Execute(statement.GetCommand(), nullptr, 0, errorMessage))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 6544, "AlertManager::WriteEvent_",
            "An alert could not be recorded and will therefore not be sent: " + errorMessage);
         return false;
      }

      return true;
   }

   bool
   AlertManager::Raise(const String &condition, int severity, const String &summary, const String &detail)
   {
      if (!Configuration::Instance()->GetAlertsEnabled())
         return false;

      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      Rule rule;
      if (!GetRule(condition, rule) || !rule.enabled)
         return false;

      // A condition that is already true does not become true again. This is the
      // whole of "a condition that is true for an hour sends once".
      Event newest;
      if (ReadNewestEvent_(condition, newest) && newest.state == StateRaised)
         return false;

      return WriteEvent_(rule, severity, StateRaised, summary, detail);
   }

   bool
   AlertManager::RaiseOccurrence(const String &condition, int severity, const String &summary, const String &detail)
   {
      if (!Configuration::Instance()->GetAlertsEnabled())
         return false;

      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      Rule rule;
      if (!GetRule(condition, rule) || !rule.enabled)
         return false;

      return WriteEvent_(rule, severity, StateRaised, summary, detail);
   }

   bool
   AlertManager::Clear(const String &condition, const String &summary)
   {
      if (!Configuration::Instance()->GetAlertsEnabled())
         return false;

      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      Rule rule;
      if (!GetRule(condition, rule) || !rule.enabled)
         return false;

      if (IsOccurrenceCondition(condition))
         return false;

      Event newest;
      if (!ReadNewestEvent_(condition, newest) || newest.state != StateRaised)
         return false;

      // A clear that follows a raise nobody was told about tells nobody either:
      // "the disk is no longer full" is meaningless to an administrator who was
      // never told it was.
      Rule clearRule = rule;
      if (newest.digested)
         clearRule.digest = true;

      return WriteEvent_(clearRule, ErrorManager::Low, StateCleared, summary, _T(""));
   }

   bool
   AlertManager::GetEvents(int limit, std::vector<Event> &events)
   {
      events.clear();

      if (limit < 1)
         limit = 1;
      if (limit > 500)
         limit = 500;

      // One statement per backend, for the same reason AuditTrail::Query has one:
      // SQL Server Compact has no LIMIT and PostgreSQL has no TOP.
      DatabaseSettings::SQLDBType type = IniFileSettings::Instance()->GetDatabaseType();

      String sql;
      if (type == DatabaseSettings::TypeMSSQLServer || type == DatabaseSettings::TypeMSSQLCompactEdition)
         // TOP (n): SQL Server Compact requires the parentheses.
         sql.Format(_T("select top (%d) * from hm_alertevents order by alerteventid desc"), limit);
      else
         sql.Format(_T("select * from hm_alertevents order by alerteventid desc limit %d"), limit);

      SQLCommand command(sql);

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!recordset)
         return false;

      while (!recordset->IsEOF())
      {
         events.push_back(ReadEvent_(recordset));
         recordset->MoveNext();
      }

      return true;
   }

   bool
   AlertManager::MarkNotified_(__int64 eventId)
   {
      SQLCommand command(_T("update hm_alertevents set alerteventnotified = 1 where alerteventid = @ID"));
      command.AddParameter("@ID", eventId);

      return Application::Instance()->GetDBManager()->Execute(command);
   }

   bool
   AlertManager::UpdateHook_(__int64 eventId, int state, int tries, __int64 next)
   {
      SQLCommand command(_T("update hm_alertevents set alerteventhookstate = @STATE, alerteventhooktries = @TRIES, alerteventhooknext = @NEXT where alerteventid = @ID"));
      command.AddParameter("@STATE", state);
      command.AddParameter("@TRIES", tries);
      command.AddParameter("@NEXT", next);
      command.AddParameter("@ID", eventId);

      return Application::Instance()->GetDBManager()->Execute(command);
   }

   AnsiString
   AlertManager::BuildEventJson(const Event &event)
   {
      AnsiString json;
      json += "{";
      json += "\"condition\":\"" + JsonEscape(AnsiString(event.condition)) + "\",";
      json += "\"state\":\"" + AnsiString(event.state == StateCleared ? "cleared" : "raised") + "\",";
      json += "\"severity\":\"" + JsonEscape(AnsiString(SeverityWord(event.severity))) + "\",";

      AnsiString time;
      time.Format("\"time\":%I64d,", event.time);
      json += time;

      json += "\"time_utc\":\"" + JsonEscape(AnsiString(TimeText(event.time))) + "\",";
      json += "\"summary\":\"" + JsonEscape(AnsiString(event.summary)) + "\",";
      json += "\"detail\":\"" + JsonEscape(AnsiString(event.detail)) + "\",";
      json += "\"server\":\"" + JsonEscape(AnsiString(Configuration::Instance()->GetHostName())) + "\"";
      json += "}";

      return json;
   }

   /*
      One message, through the server's own submission path: the file first, the
      row second, and SubmitPendingEmail to wake the delivery queue - exactly
      what the TLS-RPT reporter does, and for the same reason. Nothing here waits
      on delivery, so an alert about the delivery queue cannot wait on the
      delivery queue.
   */
   bool
   AlertManager::SendMail_(const String &subject, const AnsiString &body, const String &recipient)
   {
      String from = Configuration::Instance()->GetAlertSenderAddress();

      if (from.IsEmpty())
      {
         // No sender, no mail. Said once per attempt at Application level rather
         // than as an error: a server with no AlertRecipient and no
         // AlertSenderAddress is a correctly configured stock install.
         return false;
      }

      if (recipient.IsEmpty())
         return false;

      AnsiString content;
      content += "From: <" + AnsiString(from) + ">\r\n";
      content += "To: <" + AnsiString(recipient) + ">\r\n";
      content += "Subject: " + AnsiString(OneLine(subject)) + "\r\n";
      content += "Date: " + AnsiString(Time::GetCurrentMimeDate()) + "\r\n";
      content += "Auto-Submitted: auto-generated\r\n";
      content += "X-hMailServer-Alert: 1\r\n";
      content += "MIME-Version: 1.0\r\n";
      content += "Content-Type: text/plain; charset=\"utf-8\"\r\n";
      content += "Content-Transfer-Encoding: 8bit\r\n";
      content += "\r\n";
      content += body;

      std::shared_ptr<Message> message = std::shared_ptr<Message>(new Message());
      message->SetState(Message::Delivering);
      message->SetFromAddress(from);

      const String fileName = PersistentMessage::GetFileName(message);

      if (!FileUtilities::WriteToFile(fileName, content))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6542, "AlertManager::SendMail_",
            "An alert message could not be written to the message store and has not been sent.");
         return false;
      }

      message->SetSize(FileUtilities::FileSize(fileName));

      RecipientParser recipientParser;
      bool recipientOk = false;
      recipientParser.CreateMessageRecipientList(recipient, message->GetRecipients(), recipientOk);

      if (message->GetRecipients()->GetCount() == 0)
      {
         FileUtilities::DeleteFile(fileName);

         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6542, "AlertManager::SendMail_",
            "An alert message could not be addressed to " + recipient + " and has not been sent. Check AlertRecipient.");
         return false;
      }

      if (!PersistentMessage::SaveObject(message))
      {
         FileUtilities::DeleteFile(fileName);

         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6542, "AlertManager::SendMail_",
            "An alert message could not be saved and has NOT been sent.");
         return false;
      }

      Application::Instance()->SubmitPendingEmail();

      return true;
   }

   int
   AlertManager::SendPendingNotifications()
   {
      if (!Configuration::Instance()->GetAlertsEnabled())
         return 0;

      SQLCommand command(_T("select * from hm_alertevents where alerteventnotified = 0 and alerteventdigested = 0 order by alerteventid asc"));

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!recordset)
         return 0;

      std::vector<Event> pending;
      while (!recordset->IsEOF())
      {
         pending.push_back(ReadEvent_(recordset));
         recordset->MoveNext();
      }

      String serverWideRecipient = Configuration::Instance()->GetAlertRecipient();

      int sent = 0;

      for (const Event &event : pending)
      {
         Rule rule;
         bool haveRule = GetRule(event.condition, rule);

         if (haveRule && (rule.actions & ActionMail) != 0)
         {
            String recipient = rule.address.IsEmpty() ? serverWideRecipient : rule.address;

            String subject;
            subject.Format(_T("[hMailServer %s] %s: %s"),
               Configuration::Instance()->GetHostName().c_str(),
               event.state == StateCleared ? _T("cleared") : SeverityWord(event.severity).c_str(),
               Shorten(event.summary, 120).c_str());

            AnsiString body;
            body += AnsiString(event.summary) + "\r\n\r\n";
            if (!event.detail.IsEmpty())
               body += AnsiString(event.detail) + "\r\n\r\n";
            body += "Condition: " + AnsiString(event.condition) + "\r\n";
            body += "State:     " + AnsiString(event.state == StateCleared ? "cleared" : "raised") + "\r\n";
            body += "Severity:  " + AnsiString(SeverityWord(event.severity)) + "\r\n";
            body += "When:      " + AnsiString(TimeText(event.time)) + "\r\n";
            body += "Server:    " + AnsiString(Configuration::Instance()->GetHostName()) + "\r\n";
            body += "\r\nThis condition can be turned off, sent somewhere else, or moved into the daily digest: PUT /api/v1/alerts/rules/" + AnsiString(event.condition) + "\r\n";

            SendMail_(subject, body, recipient);
         }

         // Marked either way: an alert that could not be mailed - no recipient,
         // no sender - must not be retried every minute for the life of the
         // server. The digest still carries nothing extra, because the digest
         // reads the events that were HELD, and this one was not.
         MarkNotified_(event.id);
         sent++;
      }

      return sent;
   }

   int
   AlertManager::SendDigestIfDue()
   {
      if (!Configuration::Instance()->GetAlertsEnabled())
         return 0;

      if (!Configuration::Instance()->GetAlertDigestEnabled())
         return 0;

      time_t now = time(nullptr);
      tm utc = {};
      gmtime_s(&utc, &now);

      if (utc.tm_hour != Configuration::Instance()->GetAlertDigestHour())
         return 0;

      SQLCommand command(_T("select * from hm_alertevents where alerteventnotified = 0 and alerteventdigested = 1 order by alerteventid asc"));

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!recordset)
         return 0;

      std::vector<Event> held;
      while (!recordset->IsEOF())
      {
         held.push_back(ReadEvent_(recordset));
         recordset->MoveNext();
      }

      // Nothing happened. A digest that says so every morning is the first thing
      // an administrator filters into a folder they never open.
      if (held.empty())
         return 0;

      AnsiString body;
      body += "These conditions were recorded on " + AnsiString(Configuration::Instance()->GetHostName()) + " since the last digest.\r\n\r\n";

      for (const Event &event : held)
      {
         body += AnsiString(TimeText(event.time)) + "  " +
                 AnsiString(event.state == StateCleared ? "cleared" : "raised ") + "  " +
                 AnsiString(event.condition) + "\r\n";
         body += "    " + AnsiString(event.summary) + "\r\n";

         if (!event.detail.IsEmpty())
            body += "    " + AnsiString(OneLine(event.detail)) + "\r\n";

         body += "\r\n";
      }

      body += "Each of these can be sent at once instead of waiting for the digest, or switched off altogether: PUT /api/v1/alerts/rules/<condition>.\r\n";

      String subject;
      subject.Format(_T("[hMailServer %s] Daily alert digest: %d event(s)"),
         Configuration::Instance()->GetHostName().c_str(), (int) held.size());

      SendMail_(subject, body, Configuration::Instance()->GetAlertRecipient());

      for (const Event &event : held)
         MarkNotified_(event.id);

      return (int) held.size();
   }

   bool
   AlertManager::DeliverWebhook_(const Rule &rule, const Event &event, String &error)
   {
      AnsiString body = BuildEventJson(event);
      __int64 timestamp = (__int64) time(nullptr);

      AnsiString signature = SignWebhook(AnsiString(rule.secret), timestamp, body);

      std::vector<AnsiString> headers;

      AnsiString timestampHeader;
      timestampHeader.Format("X-hMailServer-Timestamp: %I64d", timestamp);
      headers.push_back(timestampHeader);
      headers.push_back("X-hMailServer-Signature: sha256=" + signature);
      headers.push_back("X-hMailServer-Event: " + AnsiString(rule.condition));
      headers.push_back("User-Agent: hMailServer");

      HttpsClient::Response response;

      if (!HttpsClient::Request("POST", AnsiString(rule.webhook), headers, "application/json", body, response, error))
         return false;

      if (response.status_code < 200 || response.status_code > 299)
      {
         error.Format(_T("the endpoint answered %d"), response.status_code);
         return false;
      }

      return true;
   }

   int
   AlertManager::RunWebhookDeliveries()
   {
      if (!Configuration::Instance()->GetAlertsEnabled())
         return 0;

      __int64 now = (__int64) time(nullptr);

      SQLCommand command(_T("select * from hm_alertevents where alerteventhookstate = 1 and alerteventhooknext <= @NOW order by alerteventid asc"));
      command.AddParameter("@NOW", now);

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!recordset)
         return 0;

      std::vector<Event> due;
      while (!recordset->IsEOF())
      {
         due.push_back(ReadEvent_(recordset));
         recordset->MoveNext();
      }

      int maximumAttempts = Configuration::Instance()->GetAlertWebhookMaxAttempts();
      int delivered = 0;

      for (const Event &event : due)
      {
         Rule rule;
         if (!GetRule(event.condition, rule) || (rule.actions & ActionWebhook) == 0 || rule.webhook.IsEmpty())
         {
            // The rule stopped asking for a webhook between the event and now.
            UpdateHook_(event.id, HookNotRequired, event.hook_tries, 0);
            continue;
         }

         String error;
         if (DeliverWebhook_(rule, event, error))
         {
            UpdateHook_(event.id, HookDelivered, event.hook_tries + 1, 0);
            delivered++;
            continue;
         }

         int tries = event.hook_tries + 1;

         if (tries >= maximumAttempts)
         {
            // The dead letter. The row stays, with the reason, so the alert is
            // not lost - it simply stops being retried, and the administrator is
            // told once that an endpoint of theirs is not answering.
            UpdateHook_(event.id, HookDeadLettered, tries, 0);

            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6543, "AlertManager::RunWebhookDeliveries",
               Formatter::Format("Alerts: the webhook for condition {0} was not delivered after {1} attempts and has been dead-lettered. The last failure was: {2}. The event itself is still recorded and readable at /api/v1/alerts/events.",
                  event.condition, tries, error));
            continue;
         }

         // The backoff is keyed on the attempts made BEFORE this one, which is what
         // its table says: none before means no wait, so the first retry is on the
         // next pass. Passing the count including this attempt put a minute's wait
         // before the first retry, so an administrator's "run now" straight after a
         // failure did nothing and a two-attempt dead letter took two minutes.
         UpdateHook_(event.id, HookPending, tries, now + (__int64) WebhookBackoffMinutes(tries - 1) * 60);
      }

      return delivered;
   }

   void
   AlertManager::NoteAutoBan()
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      __int64 now = (__int64) time(nullptr);

      auto_bans_.push_back(now);

      // Only the last hour matters, so only the last hour is kept: this is a
      // counter, not a history, and the bans themselves are rows already.
      while (!auto_bans_.empty() && now - auto_bans_.front() > 3600)
         auto_bans_.erase(auto_bans_.begin());
   }

   int
   AlertManager::EvaluateAutoBans_()
   {
      Rule rule;
      if (!GetRule(ConditionAutoBanStorm, rule) || !rule.enabled)
         return 0;

      int threshold = rule.threshold > 0 ? rule.threshold : 25;

      int count = 0;
      {
         boost::lock_guard<boost::recursive_mutex> guard(mutex_);

         __int64 now = (__int64) time(nullptr);
         while (!auto_bans_.empty() && now - auto_bans_.front() > 3600)
            auto_bans_.erase(auto_bans_.begin());

         count = (int) auto_bans_.size();
      }

      if (count >= threshold)
      {
         Raise(ConditionAutoBanStorm, ErrorManager::Medium,
            Formatter::Format("{0} addresses have been auto-banned in the last hour, which is at or above the threshold of {1}.", count, threshold),
            _T("A run of auto-bans is either a distributed password-guessing attempt or a mail client of your own with a stale password. The banned addresses are in the IP ranges list, each with the account name that was tried."));
         return 1;
      }

      Clear(ConditionAutoBanStorm,
         Formatter::Format("Auto-bans have fallen back to {0} in the last hour, below the threshold of {1}.", count, threshold));

      return 0;
   }

   int
   AlertManager::EvaluateCertificates_()
   {
      Rule rule;
      if (!GetRule(ConditionCertificateExpiring, rule) || !rule.enabled)
         return 0;

      int days = rule.threshold > 0 ? rule.threshold : 7;

      std::shared_ptr<SSLCertificates> certificates = Configuration::Instance()->GetSSLCertificates();
      if (!certificates)
         return 0;

      time_t now = time(nullptr);

      String worst;
      __int64 worstRemaining = 0;
      bool found = false;

      for (int i = 0; i < certificates->GetCount(); i++)
      {
         std::shared_ptr<SSLCertificate> certificate = certificates->GetItem(i);
         if (!certificate)
            continue;

         String file = certificate->GetCertificateFile();
         if (file.IsEmpty() || !FileUtilities::Exists(file))
            continue;

         AnsiString narrowFile = file;

         BIO *bio = BIO_new_file(narrowFile.c_str(), "r");
         if (bio == nullptr)
            continue;

         X509 *x509 = PEM_read_bio_X509(bio, nullptr, nullptr, nullptr);
         BIO_free(bio);

         if (x509 == nullptr)
            continue;

         tm notAfter = {};
         bool parsed = ASN1_TIME_to_tm(X509_get0_notAfter(x509), &notAfter) == 1;

         X509_free(x509);

         if (!parsed)
            continue;

         time_t expires = _mkgmtime(&notAfter);
         if (expires == -1)
            continue;

         __int64 remaining = (__int64) expires - (__int64) now;

         if (remaining > (__int64) days * 24 * 60 * 60)
            continue;

         if (!found || remaining < worstRemaining)
         {
            found = true;
            worst = certificate->GetName();
            worstRemaining = remaining;
         }
      }

      if (!found)
      {
         Clear(ConditionCertificateExpiring,
            Formatter::Format("No configured certificate expires within {0} day(s).", days));
         return 0;
      }

      String summary;
      if (worstRemaining <= 0)
         summary = Formatter::Format("The certificate {0} has EXPIRED. Clients are being offered a certificate they will refuse.", worst);
      else
         summary = Formatter::Format("The certificate {0} expires in {1} day(s).", worst, (int) (worstRemaining / (24 * 60 * 60)));

      Raise(ConditionCertificateExpiring, worstRemaining <= 0 ? ErrorManager::High : ErrorManager::Medium, summary,
         _T("Replace the certificate file on the SSL certificates page, or let ACME renew it if this host is reachable on port 80. A listener keeps serving an expired certificate; it does not stop."));

      return 1;
   }

   int
   AlertManager::EvaluateMinidumps_()
   {
      Rule rule;
      if (!GetRule(ConditionMinidumpWritten, rule) || !rule.enabled)
         return 0;

      String directory = IniFileSettings::Instance()->GetLogDirectory();
      if (directory.IsEmpty())
         return 0;

      // The newest dump on disk, against the newest one already recorded: no
      // state is kept anywhere, so a restart neither loses a dump nor reports
      // one twice. Asked here rather than from the crash path on purpose - the
      // faulting thread has a dump to write and a process to leave behind, and a
      // database insert is not what it should be doing.
      std::vector<FileInfo> files = FileUtilities::GetFilesInDirectory(directory, _T("^minidump_.*$"));

      if (files.empty())
         return 0;

      FileInfo newest = files[0];

      for (FileInfo &file : files)
      {
         if (file.GetCreateTime() > newest.GetCreateTime())
            newest = file;
      }

      String newestName = newest.GetName();

      // The dump's own file name is the marker: it carries the moment the dump
      // was written, so "have I already said this one" is answered by the last
      // event's summary and nothing has to be remembered across a restart.
      Event event;
      if (ReadNewestEvent_(ConditionMinidumpWritten, event) && event.summary.Find(newestName.c_str()) >= 0)
         return 0;

      RaiseOccurrence(ConditionMinidumpWritten, ErrorManager::Critical,
         Formatter::Format("A crash dump has been written: {0}", newestName),
         _T("hMailServer caught an unhandled exception and wrote a minidump to its log directory. The ERROR log records what the server was doing. Send the dump with the log if you report this."));

      return 1;
   }

   int
   AlertManager::EvaluateScheduledConditions()
   {
      if (!Configuration::Instance()->GetAlertsEnabled())
         return 0;

      int raised = 0;

      raised += EvaluateCertificates_();
      raised += EvaluateAutoBans_();
      raised += EvaluateMinidumps_();

      return raised;
   }

   AlertTask::AlertTask()
   {

   }

   AlertTask::~AlertTask()
   {

   }

   void
   AlertTask::DoWork()
   {
      AlertManager::Instance()->EvaluateScheduledConditions();
      AlertManager::Instance()->SendPendingNotifications();
      AlertManager::Instance()->RunWebhookDeliveries();
      AlertManager::Instance()->SendDigestIfDue();

      // Retention, here rather than in a task of its own: it is one statement a
      // minute at most, and it needs the same "is the database up" precondition.
      AuditTrail::Instance()->PurgeOlderThan(Configuration::Instance()->GetAuditRetentionDays());
   }
}
