// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "ServerStatus.h"

#include "../Application/SessionManager.h"
#include "../Persistence/PersistentMessage.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   ServerStatus::ServerStatus()
   {
      processed_messages_ = 0;
      number_of_spam_messages_detected_ = 0;
      number_of_viruses_removed_ = 0;
      number_of_tls_handshakes_completed_ = 0;
      number_of_tls_handshake_failures_ = 0;
      number_of_authentications_succeeded_ = 0;
      number_of_authentication_failures_ = 0;
      message_store_missing_files_ = 0;
      number_of_messages_delivered_ = 0;
      number_of_messages_deferred_ = 0;
      number_of_messages_bounced_ = 0;
      command_processing_micros_total_ = 0;
      commands_processed_count_ = 0;
      state_ = StateUnknown ;

   }

   ServerStatus::~ServerStatus()
   {

   }

   String 
   ServerStatus::GetUnsortedMessageStatus() const
   {
      // messagetype 3 added for ETRN on GUI Delivery Queue
      SQLCommand command("select messageid, messagecurnooftries, messagecreatetime, messagefrom, messagenexttrytime, messagefilename, messagelocked from hm_messages "
                         " where messagetype = 1 OR messagetype = 3 order by messageid asc");

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!pRS)
         return "";

      String sRetVal;
      String sCreateTime, sFrom, sTo, sNextTryTime, sFileName, sLine;
      __int64 lMessageID;
      int lNoOfTries;
      bool bLocked;
      String dataDirectory = IniFileSettings::Instance()->GetDataDirectory();

      while (!pRS->IsEOF())
      {
         
         lMessageID = pRS->GetInt64Value("messageid");
         lNoOfTries = pRS->GetLongValue("messagecurnooftries");
         sCreateTime = pRS->GetStringValue("messagecreatetime");
         sFrom = pRS->GetStringValue("messagefrom");
         sNextTryTime = pRS->GetStringValue("messagenexttrytime");
         sFileName = pRS->GetStringValue("messagefilename");
         bLocked = pRS->GetLongValue("messagelocked") == 1;
         sTo = ""; // reset between every recipient
         
         // Construct a full path to the file if it's partial.
         if (PersistentMessage::IsPartialPath(sFileName))
            sFileName = FileUtilities::Combine(dataDirectory, sFileName);

         SQLCommand selectCommand("select recipientaddress from hm_messagerecipients where recipientmessageid = @MESSAGEID");
         selectCommand.AddParameter("@MESSAGEID", lMessageID);

         std::shared_ptr<DALRecordset> pRecipientsRS = Application::Instance()->GetDBManager()->OpenRecordset(selectCommand);
         if (!pRecipientsRS)
            return "";

         while (!pRecipientsRS->IsEOF())
         {
            if (!sTo.IsEmpty())
               sTo += ",";

            sTo += pRecipientsRS->GetStringValue("recipientaddress");

            pRecipientsRS->MoveNext();
         }

         sLine.Format(_T("%I64d\t%s\t%s\t%s\t%s\t%s\t%d\t%d"), lMessageID, sCreateTime.c_str(), sFrom.c_str(), sTo.c_str(), sNextTryTime.c_str(), sFileName.c_str(), bLocked, lNoOfTries);
         
         if (!sRetVal.IsEmpty())
            sRetVal += "\r\n";
         
         sRetVal += sLine;

         pRS->MoveNext();
      }

      return sRetVal;
   }

   int 
   ServerStatus::GetNumberOfProcessedMessages() const
   {
      return processed_messages_;
   }

   void
   ServerStatus::OnMessageProcessed()
   {
      // Called by SMTPDeliveryManger which is
      // single threaded, so no synchronization
      // is needed.

      processed_messages_++;
   }

   int 
   ServerStatus::GetNumberOfDetectedSpamMessages() const
   {
      return number_of_spam_messages_detected_;
   }

   void
   ServerStatus::OnSpamMessageDetected()
   {
      // This requires thread synchronization since
      // it's called by the SMTPConnection.

      boost::lock_guard<boost::recursive_mutex> guard(spam_message_dropped_mutex_);
      number_of_spam_messages_detected_++;
   }

   int 
   ServerStatus::GetNumberOfRemovedViruses() const
   {
      return number_of_viruses_removed_;
   }

   void
   ServerStatus::OnVirusRemoved()
   {
      // This requires thread synchronization since
      // it's called by the SMTPConnection.
      boost::lock_guard<boost::recursive_mutex> guard(virus_removed_mutex_);

      number_of_viruses_removed_++;
   }

   int
   ServerStatus::GetNumberOfTlsHandshakesCompleted() const
   {
      return number_of_tls_handshakes_completed_;
   }

   void
   ServerStatus::OnTlsHandshakeCompleted()
   {
      // Called from TCP worker threads when a TLS handshake succeeds.
      boost::lock_guard<boost::recursive_mutex> guard(tls_handshake_mutex_);
      number_of_tls_handshakes_completed_++;
   }

   int
   ServerStatus::GetNumberOfTlsHandshakeFailures() const
   {
      return number_of_tls_handshake_failures_;
   }

   void
   ServerStatus::OnTlsHandshakeFailed()
   {
      // Called from TCP worker threads when a TLS handshake fails.
      boost::lock_guard<boost::recursive_mutex> guard(tls_handshake_mutex_);
      number_of_tls_handshake_failures_++;
   }
   
   int
   ServerStatus::GetNumberOfAuthenticationsSucceeded() const
   {
      return number_of_authentications_succeeded_;
   }

   void
   ServerStatus::OnAuthenticationSucceeded()
   {
      // Called when an account successfully authenticates (any protocol).
      boost::lock_guard<boost::recursive_mutex> guard(authentication_mutex_);
      number_of_authentications_succeeded_++;
   }

   int
   ServerStatus::GetNumberOfAuthenticationFailures() const
   {
      return number_of_authentication_failures_;
   }

   void
   ServerStatus::OnAuthenticationFailed()
   {
      // Called when an authentication attempt fails (any protocol).
      boost::lock_guard<boost::recursive_mutex> guard(authentication_mutex_);
      number_of_authentication_failures_++;
   }

   int
   ServerStatus::GetMessageStoreMissingFiles() const
   {
      return message_store_missing_files_;
   }

   void
   ServerStatus::SetMessageStoreMissingFiles(int count)
   {
      // Updated by the scheduled message-store consistency task.
      boost::lock_guard<boost::recursive_mutex> guard(message_store_consistency_mutex_);
      message_store_missing_files_ = count;
   }
   
   unsigned __int64
   ServerStatus::GetCommandsProcessedCount() const
   {
      return commands_processed_count_;
   }

   unsigned __int64
   ServerStatus::GetCommandProcessingMicrosecondsTotal() const
   {
      return command_processing_micros_total_;
   }

   void
   ServerStatus::OnCommandProcessed(unsigned __int64 microseconds)
   {
      // Called from TCP worker threads after each client command line is parsed.
      boost::lock_guard<boost::recursive_mutex> guard(command_latency_mutex_);
      command_processing_micros_total_ += microseconds;
      commands_processed_count_++;
   }
   
   int
   ServerStatus::GetNumberOfMessagesDelivered() const
   {
      return number_of_messages_delivered_;
   }

   void
   ServerStatus::OnMessageDelivered()
   {
      // Called from the SMTP delivery threads when a message pass completes with
      // no errors and is not rescheduled.
      boost::lock_guard<boost::recursive_mutex> guard(delivery_outcome_mutex_);
      number_of_messages_delivered_++;
   }

   int
   ServerStatus::GetNumberOfMessagesDeferred() const
   {
      return number_of_messages_deferred_;
   }

   void
   ServerStatus::OnMessageDeferred()
   {
      // Called when a message pass is rescheduled for a later delivery attempt
      // (temporary failure).
      boost::lock_guard<boost::recursive_mutex> guard(delivery_outcome_mutex_);
      number_of_messages_deferred_++;
   }

   int
   ServerStatus::GetNumberOfMessagesBounced() const
   {
      return number_of_messages_bounced_;
   }

   void
   ServerStatus::OnMessageBounced()
   {
      // Called when a bounce/NDR is generated for one or more recipients
      // (permanent failure).
      boost::lock_guard<boost::recursive_mutex> guard(delivery_outcome_mutex_);
      number_of_messages_bounced_++;
   }
   
   int
   ServerStatus::GetState() const
   {
      return state_;
   }

   void
   ServerStatus::SetState(ServerState i)
   {
      state_ = i; 
   }

   int 
   ServerStatus::GetNumberOfSessions(int iSessionType)
   {
      return SessionManager::Instance()->GetNumberOfConnections((SessionType) iSessionType);
   }

   int
   ServerStatus::GetThreadID() const
   {
      DWORD dwThreadID = GetCurrentThreadId();
      return dwThreadID;
   }
}
