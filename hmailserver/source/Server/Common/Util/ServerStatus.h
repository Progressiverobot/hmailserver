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

      // Requests to /metrics that the metrics listener refused because they carried
      // no credential or the wrong one. Kept here rather than inside MetricsServer
      // so that it survives the listener being stopped and restarted by a
      // configuration reload - which is exactly when an operator most wants to know
      // that something has been probing the port - and so that it is exposed through
      // the same path as every other counter rather than through a second mechanism.
      //
      // Deliberately separate from OnAuthenticationFailed: that counter means "an
      // account failed to log on", and folding a refused monitoring scrape into it
      // would corrupt the one number people build brute-force alerts on. For the
      // same reason nothing on the metrics listener touches the TLS handshake
      // counters either.
      void OnMetricsRequestUnauthorized();
      unsigned __int64 GetMetricsUnauthorizedRequestsCount() const;

      // Outbound/delivery outcome counters, incremented from the SMTP delivery
      // threads as each queued message reaches a terminal outcome for a pass.
      void OnMessageDelivered();
      int GetNumberOfMessagesDelivered() const;

      void OnMessageDeferred();
      int GetNumberOfMessagesDeferred() const;

      void OnMessageBounced();
      int GetNumberOfMessagesBounced() const;

      // A consistent snapshot of one latency family. All three parts are read under
      // the same lock, so the +Inf bucket always equals the count and a scrape can
      // never expose a histogram whose bucket total disagrees with its _count.
      struct LatencySnapshot
      {
         LatencySnapshot() :
            microseconds_total(0),
            count(0)
         {
         }

         unsigned __int64 microseconds_total;
         unsigned __int64 count;

         // Cumulative ("le") counts: one entry per bucket bound, in ascending order,
         // plus a final entry for +Inf which equals count.
         std::vector<unsigned __int64> cumulative_buckets;
      };

      // Fixed histogram bucket layout, in seconds, for the two latency families
      // below. The bounds are compiled in rather than configurable on purpose: a
      // Prometheus histogram's bucket bounds are part of the series identity, so a
      // layout that varied between restarts would make histogram_quantile()
      // interpolate across two incompatible sets of buckets. The two families get
      // different bounds because they measure different scales - a protocol command
      // line is normally tens of microseconds, a database statement normally
      // milliseconds.
      static std::vector<double> GetCommandLatencyBucketBounds();
      static std::vector<double> GetDatabaseLatencyBucketBounds();

      // Processing latency of client protocol command lines (SMTP/IMAP/POP3),
      // recorded as a Prometheus histogram: a running microsecond sum, a count, and
      // fixed cumulative buckets, so a p95/p99 is computable and not only a mean.
      void OnCommandProcessed(unsigned __int64 microseconds);
      unsigned __int64 GetCommandProcessingMicrosecondsTotal() const;
      unsigned __int64 GetCommandsProcessedCount() const;
      LatencySnapshot GetCommandLatencySnapshot() const;

      // Execution latency of every database statement run through the
      // DatabaseConnectionManager chokepoint, recorded as a Prometheus histogram
      // (microsecond sum + count + fixed cumulative buckets), plus a count of
      // statements that exceeded the configured slow-query threshold.
      // Backend-agnostic (MySQL/PostgreSQL/MSSQL/SQL CE all route through the same
      // chokepoint).
      void OnDatabaseQuery(unsigned __int64 microseconds, bool wasSlow);
      unsigned __int64 GetDatabaseQueryMicrosecondsTotal() const;
      unsigned __int64 GetDatabaseQueriesCount() const;
      unsigned __int64 GetDatabaseSlowQueriesCount() const;
      LatencySnapshot GetDatabaseLatencySnapshot() const;

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
      unsigned __int64 metrics_unauthorized_requests_;

      // Per-bucket (not cumulative) observation counts, sized to the bucket bounds
      // plus one slot for +Inf. The snapshot getters accumulate them.
      std::vector<unsigned __int64> command_latency_buckets_;
      std::vector<unsigned __int64> database_latency_buckets_;

      boost::recursive_mutex spam_message_dropped_mutex_;
      boost::recursive_mutex virus_removed_mutex_;
      boost::recursive_mutex tls_handshake_mutex_;
      boost::recursive_mutex authentication_mutex_;
      boost::recursive_mutex message_store_consistency_mutex_;
      boost::recursive_mutex delivery_outcome_mutex_;

      // Mutable because the latency snapshot getters are logically const reads that
      // still have to take the lock to avoid a torn read across sum, count and
      // buckets.
      mutable boost::recursive_mutex command_latency_mutex_;
      mutable boost::recursive_mutex database_query_mutex_;

      // Mutable for the same reason: the getter is a logically const read of a
      // 64-bit value, and a 64-bit read is not atomic on the 32-bit build, so it has
      // to take the lock or risk a torn value. The other 64-bit counters in this
      // class read unlocked, which is a latent hazard on that build; this one does
      // not repeat it.
      mutable boost::recursive_mutex metrics_request_mutex_;

      ServerState state_;
   };
}
