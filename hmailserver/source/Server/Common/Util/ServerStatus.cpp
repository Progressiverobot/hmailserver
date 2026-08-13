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
   namespace
   {
      // The bucket bounds are held as function-local statics and handed out by
      // reference on the observation path. OnCommandProcessed runs once per protocol
      // command line on every connection, so rebuilding a vector there would be a
      // heap allocation per command for no gain.

      const std::vector<double> &CommandLatencyBounds()
      {
         // A protocol command line is normally handled in tens of microseconds, so
         // the useful resolution is at the bottom; the 0.5s and 1s bounds exist so a
         // command that blocked on something shows up as a distinct step rather than
         // disappearing into +Inf.
         static const std::vector<double> bounds
         {
            0.0001, 0.00025, 0.0005, 0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.5, 1.0
         };

         return bounds;
      }

      const std::vector<double> &DatabaseLatencyBounds()
      {
         // A database statement is normally a millisecond or two and the interesting
         // tail runs out to seconds - a locked table, or a backup in progress - so
         // these bounds start a decade above the command bounds and end a decade
         // later.
         static const std::vector<double> bounds
         {
            0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1.0, 5.0, 10.0
         };

         return bounds;
      }

      // Places one observation into a per-bucket count vector: the index of the
      // lowest bound the value is still <= (Prometheus buckets are "le"), or the
      // final +Inf slot when it exceeds every bound.
      void RecordLatencyObservation(std::vector<unsigned __int64> &buckets, const std::vector<double> &bounds, double seconds)
      {
         size_t index = bounds.size();

         for (size_t i = 0; i < bounds.size(); i++)
         {
            if (seconds <= bounds[i])
            {
               index = i;
               break;
            }
         }

         if (index < buckets.size())
            buckets[index]++;
      }

      // Turns per-bucket counts into the cumulative counts the exposition format
      // wants. The last entry is the +Inf bucket and therefore the total.
      std::vector<unsigned __int64> AccumulateLatencyBuckets(const std::vector<unsigned __int64> &buckets)
      {
         std::vector<unsigned __int64> cumulative(buckets.size(), 0);

         unsigned __int64 running = 0;
         for (size_t i = 0; i < buckets.size(); i++)
         {
            running += buckets[i];
            cumulative[i] = running;
         }

         return cumulative;
      }
   }

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
      database_query_micros_total_ = 0;
      database_queries_count_ = 0;
      database_slow_queries_count_ = 0;
      metrics_unauthorized_requests_ = 0;
      state_ = StateUnknown ;

      // One slot per bound, plus the +Inf slot. Sized once here so the observation
      // path never allocates and the snapshot getters can rely on the length.
      command_latency_buckets_.assign(CommandLatencyBounds().size() + 1, 0);
      database_latency_buckets_.assign(DatabaseLatencyBounds().size() + 1, 0);
   }

   std::vector<double>
   ServerStatus::GetCommandLatencyBucketBounds()
   {
      return CommandLatencyBounds();
   }

   std::vector<double>
   ServerStatus::GetDatabaseLatencyBucketBounds()
   {
      return DatabaseLatencyBounds();
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

   unsigned __int64
   ServerStatus::GetMetricsUnauthorizedRequestsCount() const
   {
      boost::lock_guard<boost::recursive_mutex> guard(metrics_request_mutex_);
      return metrics_unauthorized_requests_;
   }

   void
   ServerStatus::OnMetricsRequestUnauthorized()
   {
      // Called only from the metrics listener's single accept thread, which handles
      // one request to completion before reading the next - so there is no
      // concurrency here to protect against today. The lock is taken anyway,
      // because the read side is a 64-bit load that is not atomic on the 32-bit
      // build, and because "this happens to be single-threaded" is the sort of
      // invariant that stops being true when the listener is eventually re-hosted
      // on the shared Boost.Asio stack with a worker pool. It costs one uncontended
      // lock per refused request.
      boost::lock_guard<boost::recursive_mutex> guard(metrics_request_mutex_);
      metrics_unauthorized_requests_++;
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
      // The histogram bucket is picked here, on the observation path, so the metric
      // is a histogram from the very first scrape rather than a shape that changes
      // once traffic arrives. A Prometheus metric family whose declared type changes
      // at runtime is worse than no histogram at all.
      double seconds = static_cast<double>(microseconds) / 1000000.0;

      boost::lock_guard<boost::recursive_mutex> guard(command_latency_mutex_);
      command_processing_micros_total_ += microseconds;
      commands_processed_count_++;
      RecordLatencyObservation(command_latency_buckets_, CommandLatencyBounds(), seconds);
   }

   ServerStatus::LatencySnapshot
   ServerStatus::GetCommandLatencySnapshot() const
   {
      LatencySnapshot snapshot;

      boost::lock_guard<boost::recursive_mutex> guard(command_latency_mutex_);
      snapshot.microseconds_total = command_processing_micros_total_;
      snapshot.count = commands_processed_count_;
      snapshot.cumulative_buckets = AccumulateLatencyBuckets(command_latency_buckets_);

      return snapshot;
   }

   unsigned __int64
   ServerStatus::GetDatabaseQueriesCount() const
   {
      return database_queries_count_;
   }

   unsigned __int64
   ServerStatus::GetDatabaseQueryMicrosecondsTotal() const
   {
      return database_query_micros_total_;
   }

   unsigned __int64
   ServerStatus::GetDatabaseSlowQueriesCount() const
   {
      return database_slow_queries_count_;
   }

   void
   ServerStatus::OnDatabaseQuery(unsigned __int64 microseconds, bool wasSlow)
   {
      // Called from whichever thread ran the statement through the connection
      // manager (TCP workers, delivery threads, scheduled maintenance tasks).
      double seconds = static_cast<double>(microseconds) / 1000000.0;

      boost::lock_guard<boost::recursive_mutex> guard(database_query_mutex_);
      database_query_micros_total_ += microseconds;
      database_queries_count_++;
      RecordLatencyObservation(database_latency_buckets_, DatabaseLatencyBounds(), seconds);

      if (wasSlow)
         database_slow_queries_count_++;
   }

   ServerStatus::LatencySnapshot
   ServerStatus::GetDatabaseLatencySnapshot() const
   {
      LatencySnapshot snapshot;

      boost::lock_guard<boost::recursive_mutex> guard(database_query_mutex_);
      snapshot.microseconds_total = database_query_micros_total_;
      snapshot.count = database_queries_count_;
      snapshot.cumulative_buckets = AccumulateLatencyBuckets(database_latency_buckets_);

      return snapshot;
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
