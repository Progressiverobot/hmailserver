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

      boost::recursive_mutex spam_message_dropped_mutex_;
      boost::recursive_mutex virus_removed_mutex_;
      boost::recursive_mutex tls_handshake_mutex_;
      boost::recursive_mutex authentication_mutex_;
      boost::recursive_mutex message_store_consistency_mutex_;

      ServerState state_;
   };
}
