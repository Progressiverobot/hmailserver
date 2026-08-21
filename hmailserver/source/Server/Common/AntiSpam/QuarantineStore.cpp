// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"
#include "QuarantineStore.h"

#include "../Application/Application.h"
#include "../Application/IniFileSettings.h"
#include "../BO/Message.h"
#include "../BO/MessageRecipient.h"
#include "../BO/MessageRecipients.h"
#include "../Persistence/PersistentMessage.h"
#include "../SQL/DALRecordset.h"
#include "../SQL/SQLCommand.h"
#include "../SQL/SQLStatement.h"
#include "../SQL/DatabaseConnectionManager.h"
#include "../Util/FileUtilities.h"
#include "../Util/GUIDCreator.h"
#include "../Util/Time.h"
#include "../Util/Utilities.h"
#include "../Mime/Mime.h"
#include <ctime>
#include "../../SMTP/RecipientParser.h"
#include "../Util/Parsing/StringParser.h"
#include "../BO/MessageData.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   bool
   QuarantineStore::GetEnabled()
   {
      return IniFileSettings::Instance()->GetQuarantineEnabled();
   }

   String
   QuarantineStore::GetQuarantineDirectory()
   {
      // Not created here. FileUtilities::Copy makes missing directories on demand,
      // so the store comes into existence the first time something is quarantined -
      // and a server that never quarantines anything never grows an empty directory
      // it did not ask for.
      return IniFileSettings::Instance()->GetDataDirectory() + "\\Quarantine";
   }

   String
   QuarantineStore::BuildRelativePath_()
   {
      // A day-per-directory layout. A flat directory is fine until the day somebody
      // points this at a mail stream that quarantines thousands a day, at which point
      // enumerating it becomes the slowest thing in the server - and the retention
      // sweep gets to delete whole directories rather than walking every file.
      String day = Time::GetCurrentDateTime().Mid(0, 10);   // YYYY-MM-DD

      return day + "\\" + GUIDCreator::GetGUID() + ".eml";
   }

   bool
   QuarantineStore::Quarantine(std::shared_ptr<Message> message, const String &reason, int score)
   {
      if (!message)
         return false;

      return Quarantine(message, reason, score, PersistentMessage::GetFileName(message));
   }

   bool
   QuarantineStore::Quarantine(std::shared_ptr<Message> message, const String &reason, int score, const String &sourceFileName)
   {
      try
      {
         if (!message)
            return false;

         String sourceFile = sourceFileName;

         if (sourceFile.IsEmpty() || !FileUtilities::Exists(sourceFile))
            return false;

         String relativePath = BuildRelativePath_();
         String targetFile = GetQuarantineDirectory() + "\\" + relativePath;

         // Copied rather than moved: the caller still owns the original and will
         // delete it as part of not delivering the message. A move would leave the
         // caller's cleanup deleting a file that is now the quarantine's only copy.
         //
         // The third argument creates the day directory, so the layout needs no
         // separate setup step.
         if (!FileUtilities::Copy(sourceFile, targetFile, true))
            return false;

         String recipients;

         if (message->GetRecipients())
         {
            for (auto recipient : message->GetRecipients()->GetVector())
            {
               if (!recipients.IsEmpty())
                  recipients += ",";

               recipients += recipient->GetAddress();
            }
         }

         // The subject is read from the stored file rather than carried in: at this
         // point in the SMTP conversation the message object holds envelope data, and
         // a review queue that cannot show a subject is not reviewable.
         String subject;

         {
            MessageData messageData;
            if (messageData.LoadFromMessage(targetFile, message))
               subject = messageData.GetSubject();
         }

         SQLStatement statement;

         statement.SetTable("hm_quarantine");
         statement.AddColumn("quarantinefilename", relativePath);
         statement.AddColumn("quarantinesender", message->GetFromAddress());
         statement.AddColumn("quarantinerecipients", recipients.Mid(0, 1000));
         statement.AddColumn("quarantinesubject", subject.Mid(0, 255));
         statement.AddColumn("quarantinereason", reason.Mid(0, 255));
         statement.AddColumnInt64("quarantinescore", score);
         statement.AddColumnInt64("quarantinesize", FileUtilities::FileSize(targetFile));
         statement.AddColumnDate("quarantinecreated", Time::GetDateFromSystemDate(Time::GetCurrentDateTime()));
         statement.SetStatementType(SQLStatement::STInsert);
         statement.SetIdentityColumn("quarantineid");

         __int64 dbid = 0;

         if (!Application::Instance()->GetDBManager()->Execute(statement, &dbid))
         {
            // The row is the index; a file with no row is invisible and will never be
            // swept. Remove it rather than leave litter that grows for ever.
            FileUtilities::DeleteFile(targetFile);
            return false;
         }

         return true;
      }
      catch (...)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6200, "QuarantineStore::Quarantine",
            "An unexpected error occurred while quarantining a message. The message was not quarantined.");

         return false;
      }
   }

   bool
   QuarantineStore::ReadRow_(std::shared_ptr<DALRecordset> recordset, QuarantinedMessage &out_message)
   {
      if (!recordset || recordset->IsEOF())
         return false;

      out_message.id = recordset->GetInt64Value("quarantineid");
      out_message.file_name = recordset->GetStringValue("quarantinefilename");
      out_message.sender = recordset->GetStringValue("quarantinesender");
      out_message.recipients = recordset->GetStringValue("quarantinerecipients");
      out_message.subject = recordset->GetStringValue("quarantinesubject");
      out_message.reason = recordset->GetStringValue("quarantinereason");
      out_message.score = (int) recordset->GetLongValue("quarantinescore");
      out_message.size = (int) recordset->GetLongValue("quarantinesize");
      out_message.created = recordset->GetStringValue("quarantinecreated");

      return true;
   }

   std::vector<QuarantinedMessage>
   QuarantineStore::List(int maxCount)
   {
      std::vector<QuarantinedMessage> result;

      // Newest first, and bounded. A review queue is worked from the top, and an
      // unbounded select on a table nobody has pruned is how an administration tool
      // hangs on the one server that most needs looking at.
      SQLCommand command("select * from hm_quarantine order by quarantinecreated desc");

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);

      if (!recordset)
         return result;

      while (!recordset->IsEOF() && (int) result.size() < maxCount)
      {
         QuarantinedMessage message;

         if (ReadRow_(recordset, message))
            result.push_back(message);

         recordset->MoveNext();
      }

      return result;
   }

   bool
   QuarantineStore::GetById(__int64 id, QuarantinedMessage &out_message)
   {
      SQLCommand command("select * from hm_quarantine where quarantineid = @ID");
      command.AddParameter("@ID", id);

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);

      return ReadRow_(recordset, out_message);
   }

   int
   QuarantineStore::GetCount()
   {
      SQLCommand command("select count(*) as quarantinecount from hm_quarantine");

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);

      if (!recordset)
         return 0;

      return (int) recordset->GetLongValue("quarantinecount");
   }

   bool
   QuarantineStore::Delete(__int64 id)
   {
      QuarantinedMessage message;

      if (!GetById(id, message))
         return false;

      // The row goes first. A row with no file is a broken entry an administrator can
      // see and remove; a file with no row is invisible, and nothing will ever sweep
      // it - so if only one of the two can succeed, losing the file is the better
      // failure.
      SQLCommand command("delete from hm_quarantine where quarantineid = @ID");
      command.AddParameter("@ID", id);

      if (!Application::Instance()->GetDBManager()->Execute(command))
         return false;

      String file = GetQuarantineDirectory() + "\\" + message.file_name;

      if (FileUtilities::Exists(file))
         FileUtilities::DeleteFile(file);

      return true;
   }

   bool
   QuarantineStore::Release(__int64 id, String &out_error)
   {
      QuarantinedMessage quarantined;

      if (!GetById(id, quarantined))
      {
         out_error = "No quarantined message with that id.";
         return false;
      }

      String sourceFile = GetQuarantineDirectory() + "\\" + quarantined.file_name;

      if (!FileUtilities::Exists(sourceFile))
      {
         out_error = "The quarantined message file is missing, so there is nothing to release. "
                     "Delete the entry to clear it.";
         return false;
      }

      if (quarantined.recipients.IsEmpty())
      {
         out_error = "The quarantined message has no recorded recipients, so there is nowhere to release it to.";
         return false;
      }

      // Re-injected into the delivery queue rather than written straight into a
      // mailbox, so that the things which SHOULD still apply do: rules, forwarding,
      // quota, the account actually existing. What it deliberately skips is the spam
      // filtering, and that skip is free rather than argued for - the verdict that
      // quarantined this message was reached during the SMTP conversation, and a
      // message entering at the queue never goes near it. Releasing therefore cannot
      // re-quarantine, which would otherwise be an infinite and very confusing loop.
      std::shared_ptr<Message> message = std::shared_ptr<Message>(new Message());
      message->SetState(Message::Delivering);
      message->SetFromAddress(quarantined.sender);

      const String targetFile = PersistentMessage::GetFileName(message);

      if (!FileUtilities::Copy(sourceFile, targetFile, true))
      {
         out_error = "The quarantined message could not be copied into the delivery queue.";
         return false;
      }

      message->SetSize(FileUtilities::FileSize(targetFile));

      RecipientParser recipientParser;

      for (const String &address : StringParser::SplitString(quarantined.recipients, ","))
      {
         bool recipientOk = false;
         recipientParser.CreateMessageRecipientList(address, message->GetRecipients(), recipientOk);
      }

      if (message->GetRecipients()->GetCount() == 0)
      {
         FileUtilities::DeleteFile(targetFile);
         out_error = "None of the recorded recipients could be resolved, so the message was not released.";
         return false;
      }

      if (!PersistentMessage::SaveObject(message))
      {
         FileUtilities::DeleteFile(targetFile);
         out_error = "The released message could not be saved to the delivery queue.";
         return false;
      }

      Application::Instance()->SubmitPendingEmail();

      // Removed only after the copy is safely queued. The other order loses the
      // message entirely if the save fails, which is the one thing a quarantine must
      // never do - an administrator reached for it precisely because it mattered.
      Delete(id);

      LOG_APPLICATION(Formatter::Format("Quarantine: released message {0} from {1} to {2}.",
         id, quarantined.sender, quarantined.recipients));

      return true;
   }

   int
   QuarantineStore::DeleteExpired()
   {
      int retentionDays = IniFileSettings::Instance()->GetQuarantineRetentionDays();

      if (retentionDays <= 0)
         return 0;

      // Every row is read and the cutoff applied in C++ rather than in the WHERE
      // clause. Date arithmetic and date literals are the least portable corner of
      // SQL and this server supports four engines; a sweep that silently deletes
      // nothing on one of them - or everything - is a worse outcome than reading a
      // table that the sweep itself keeps small.
      //
      // One row at a time, too, because each owns a file. A set-based DELETE would
      // strip the index and leave the messages on disk for ever.
      SQLCommand command("select quarantineid, quarantinecreated from hm_quarantine");

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);

      if (!recordset)
         return 0;

      // Compared as timestamp strings, which sounds lazy and is actually the most
      // robust option here. The stored format is "YYYY-MM-DD HH:MM:SS", where
      // lexicographic order IS chronological order, and doing the comparison in C++
      // keeps four SQL dialects' date arithmetic out of a sweep that deletes mail.
      // DateTime looks like the obvious type for this and is not: its operator DATE
      // is declared but never implemented, so the natural expression does not link.
      std::time_t cutoffTime = std::time(0) - (static_cast<std::time_t>(retentionDays) * 24 * 60 * 60);

      struct tm cutoffTm;

      if (localtime_s(&cutoffTm, &cutoffTime) != 0)
         return 0;

      char cutoffText[32];

      if (strftime(cutoffText, sizeof(cutoffText), "%Y-%m-%d %H:%M:%S", &cutoffTm) == 0)
         return 0;

      // Local time on both sides: the stored value came from Time::GetCurrentDateTime,
      // which is local, so a UTC cutoff would be right for most of the year and wrong
      // by an hour twice.
      const String cutoff = cutoffText;

      std::vector<__int64> expired;

      while (!recordset->IsEOF())
      {
         String created = recordset->GetStringValue("quarantinecreated");

         // An unreadable stamp is left alone rather than swept. A row whose date
         // cannot be read is already odd, and deleting the message it points at -
         // which is the only copy - on the strength of that would be the wrong way to
         // resolve the oddity.
         if (created.GetLength() >= 19 && created.Compare(cutoff) < 0)
            expired.push_back(recordset->GetInt64Value("quarantineid"));

         recordset->MoveNext();
      }

      int deleted = 0;

      for (__int64 id : expired)
      {
         if (Delete(id))
            deleted++;
      }

      return deleted;
   }
}
