// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "../common/TCPIP/TCPConnection.h"

namespace HM
{
   class FetchAccount;
   class Message;
   class TransparentTransmissionBuffer;
   class MimeHeader;
   class Result;
   class FetchAccountUIDList;

   class POP3ClientConnection : 
      public TCPConnection
   {
   public:
      POP3ClientConnection(std::shared_ptr<FetchAccount> pAccount, 
         ConnectionSecurity connectionSecurity,
         boost::asio::io_context& io_context, 
         boost::asio::ssl::context& context,
         std::shared_ptr<Event> disconnected,
         AnsiString remote_hostname);
      ~POP3ClientConnection(void);

      virtual void ParseData(const AnsiString &Request);
      virtual void ParseData(std::shared_ptr<ByteBuffer> pBuf);

      
      virtual AnsiString GetCommandSeparator() const;

      void OnCouldNotConnect(const AnsiString &sErrorDescription);

   protected:

     virtual void OnConnected();
     virtual void OnHandshakeCompleted();
     virtual void OnHandshakeFailed() {};
     virtual void OnConnectionTimeout();
     virtual void OnExcessiveDataReceived();

      void EnqueueWrite_(const String &sData) ;
   
   // This is temp function to log ETRN client commands to SMTP
      void EnqueueWrite_LogAsSMTP(const String &sData) ;

   private:

      void SendCAPA_();
      bool HandleEtrn_(const String &sRequest, const String &account_name);
      int GetDaysToKeep_(const String &sUID);
      void FireOnExternalAccountDownload_(std::shared_ptr<Message> message, const String &uid);

      void HandlePOP3FinalizationTaskCompleted_();

      void LogFinalizationStage_(const AnsiString &stage, ULONGLONG startTick);

      bool InternalParseData(const String &sRequest);

      void CreateRecipentList_(std::shared_ptr<MimeHeader> pHeader);

      // Checks whether the POP3 command hMailServer sent
      // to the remote server was successful.
      bool CommandIsSuccessfull_(const String &sData);

      // Logs a line in the POP3 log.
      void LogPOP3String_(const String &sLogString, bool bSent);

      // This is temp function to log ETRN client commands to SMTP
      void LogSMTPString_(const String &sLogString, bool bSent);

      void ParseStateConnected_(const String &sData);
      void ParseStateCAPASent_(const String &sData);
      bool ParseStateSTLSSent_(const String &sData);
      void ParseUsernameSent_(const String &sData);
      void ParsePasswordSent_(const String &sData);
      void ParseUIDLResponse_(const String &sData);

      // Parses one "<message-number> <unique-id>" line of a UIDL listing. False for a
      // line that is not of that shape; such a line is ignored rather than guessed at.
      bool ParseUIDLLine_(const String &line, int &messageIndex, String &messageUID);

      // Returns a form of the remote unique-id that fits the 255-character column it is
      // stored in. Normally the id unchanged, since RFC 1939 caps it at 70.
      static String FoldRemoteUID_(const String &uid);

      void ParseRETRResponse_(const String &sData);
      bool ParseQuitResponse_(const String &sData);
      void ParseDELEResponse_(const String &sData);
      bool RequestNextMessage_();

      bool ParseFirstBinary_(std::shared_ptr<ByteBuffer> pBuf);
      void ProcessMIMERecipients_(std::shared_ptr<MimeHeader> pHeader);
      void ProcessReceivedHeaders_(std::shared_ptr<MimeHeader> pHeader);

      void RetrieveReceivedDate_(std::shared_ptr<MimeHeader> pHeader);

      void PrependHeaders_();
      // Adds headers to the beginning of the message.

      void QuitNow_();
      // Sends a QUIT message and switch over to
      // quit-state

      std::shared_ptr<FetchAccountUIDList> GetUIDList_();

      void MarkCurrentMessageAsRead_();
      void ParseMessageHeaders_();
      void SaveMessage_();
      bool DoSpamProtection_();
      void SendUserName_();
      void StartMailboxCleanup_();
      // Triggers a clean up start.

      void MailboxCleanup_();
      // Cleans up the entire mailbox

      bool MessageCleanup_();
      // Cleans up the current message.

      void DeleteUIDsNoLongerOnServer_();
      // Deletes the UID's in the local database if
      // the UID does not exist on the POP3 server.

      void DiscardUnfinishedDownload_();
      // Removes the spool file of a download that never completed, so an interrupted
      // fetch does not leave a file in the data directory that nothing refers to.

      std::shared_ptr<FetchAccount> account_;
      // The current fetch account.

      void RemoveInvalidRecipients_();

      enum State
      {
         StateConnected,
         StateCAPASent,
         StateSTLSSent,
         StateUsernameSent,
         StatePasswordSent,
         StateUIDLRequestSent,
         StateRETRSent,
         StateDELESent,
         StateQUITSent
      };

      enum Limits
      {
         // Most bytes accepted in a single UIDL listing. The listing is accumulated in
         // memory until its terminator arrives, and the peer is a machine the
         // administrator does not control, so it needs a ceiling.
         MaxUIDLResponseBytes = 8 * 1024 * 1024
      };

      State current_state_;

      AnsiString command_buffer_;

      std::map<int ,String> uidlresponse_;
      // The messages on the server (id,UID)

      std::map<int ,String> downloaded_messages_;
      // Messages which have been downloaded from the remote server.

      std::map<int ,String>::iterator cur_message_;

      std::shared_ptr<Message> current_message_;

      // Every unique-id seen in this session's UIDL listing. Used to tell a listing that
      // repeats an id - a server that derives the id from the message content does that
      // for two identical messages - from an id recorded during an earlier session.
      std::set<String> uids_seen_this_session_;

      // The unique-id of the message a DELE has been sent for and not yet acknowledged.
      // The local record of that message is only removed once the server confirms the
      // deletion, so a refused or unanswered DELE cannot lead to a second delivery.
      String pending_delete_uid_;

      // Set when the remote server rejected the RETR command. No message data
      // follows such a response, so no message file must be created for it.
      bool retr_failed_;

      // False from the moment a RETR is sent until the message it produced has been
      // dealt with. If the session ends while it is false there is a partly written
      // message file in the data directory that nothing will ever refer to.
      bool download_finalized_;

      // Tick at which the per-message finalization was handed to the async work
      // queue. The queue is shared with the SMTP acknowledgement path, so the
      // wait before the task starts is as interesting as the work itself.
      ULONGLONG finalization_enqueued_tick_;

      String receiving_account_address_;

      std::shared_ptr<TransparentTransmissionBuffer> transmission_buffer_;

      std::map<String, std::shared_ptr<Result> > event_results_;

      std::shared_ptr<FetchAccountUIDList> fetch_account_uidlist_;

      std::shared_ptr<ByteBuffer> _firstRetrResponseBuffer;
  };
}