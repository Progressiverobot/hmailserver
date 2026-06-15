// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class ServerStatus : public Singleton<ServerStatus>
   {
   public:
	   ServerStatus();
	   virtual ~ServerStatus();

      enum ServerState
      {
         StateUnknown = 0,
         StateStopped = 1,
         StateStarting = 2,
         StateRunning = 3,
         StateStopping = 4
      };

      String GetUnsortedMessageStatus()  const;

      void OnMessageProcessed();
      int GetNumberOfProcessedMessages()  const;

      void OnSpamMessageDetected();
      int GetNumberOfDetectedSpamMessages() const;
      
      void OnVirusRemoved();
      int GetNumberOfRemovedViruses() const;

      void OnTlsHandshakeCompleted();
      int GetNumberOfTlsHandshakesCompleted() const;

      void OnTlsHandshakeFailed();
      int GetNumberOfTlsHandshakeFailures() const;

      void OnAuthenticationSucceeded();
      int GetNumberOfAuthenticationsSucceeded() const;

      void OnAuthenticationFailed();
      int GetNumberOfAuthenticationFailures() const;

      // Outbound/delivery outcome counters, incremented from the SMTP delivery
      // threads as each queued message reaches a terminal outcome for a pass.
      void OnMessageDelivered();
      int GetNumberOfMessagesDelivered() const;

      void OnMessageDeferred();
      int GetNumberOfMessagesDeferred() const;

      void OnMessageBounced();
      int GetNumberOfMessagesBounced() const;

      // Aggregate processing latency of client protocol command lines (SMTP/IMAP/
      // POP3), recorded as a Prometheus-style summary (running microsecond sum +
      // count) so average command latency can be derived without per-command
      // histograms.
      void OnCommandProcessed(unsigned __int64 microseconds);
      unsigned __int64 GetCommandProcessingMicrosecondsTotal() const;
      unsigned __int64 GetCommandsProcessedCount() const;

      // Aggregate execution latency of every database statement run through the
      // DatabaseConnectionManager chokepoint, recorded as a Prometheus-style
      // summary (running microsecond sum + count), plus a count of statements that
      // exceeded the configured slow-query threshold. Backend-agnostic (MySQL/
      // PostgreSQL/MSSQL/SQL CE all route through the same chokepoint).
      void OnDatabaseQuery(unsigned __int64 microseconds, bool wasSlow);
      unsigned __int64 GetDatabaseQueryMicrosecondsTotal() const;
      unsigned __int64 GetDatabaseQueriesCount() const;
      unsigned __int64 GetDatabaseSlowQueriesCount() const;

      // Last result of the message-store consistency check: the number of message
      // rows whose backing file was missing on disk. Updated by the scheduled
      // MessageStoreConsistencyTask; 0 when the store is consistent (or the check
      // is disabled).
      void SetMessageStoreMissingFiles(int count);
      int GetMessageStoreMissingFiles() const;

      void SetState(ServerState i);
      int GetState() const;

      int GetNumberOfSessions(int iSessionType);

      int GetThreadID() const;

   private:

      int processed_messages_;
      int number_of_spam_messages_detected_;
      int number_of_viruses_removed_;
      int number_of_tls_handshakes_completed_;
      int number_of_tls_handshake_failures_;
      int number_of_authentications_succeeded_;
      int number_of_authentication_failures_;
      int message_store_missing_files_;
      int number_of_messages_delivered_;
      int number_of_messages_deferred_;
      int number_of_messages_bounced_;
      unsigned __int64 command_processing_micros_total_;
      unsigned __int64 commands_processed_count_;
      unsigned __int64 database_query_micros_total_;
      unsigned __int64 database_queries_count_;
      unsigned __int64 database_slow_queries_count_;

      boost::recursive_mutex spam_message_dropped_mutex_;
      boost::recursive_mutex virus_removed_mutex_;
      boost::recursive_mutex tls_handshake_mutex_;
      boost::recursive_mutex authentication_mutex_;
      boost::recursive_mutex message_store_consistency_mutex_;
      boost::recursive_mutex command_latency_mutex_;
      boost::recursive_mutex delivery_outcome_mutex_;
      boost::recursive_mutex database_query_mutex_;

      ServerState state_;
   };
}
