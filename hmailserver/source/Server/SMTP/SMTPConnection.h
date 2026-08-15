
// Copyright (c) 2005 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once


#include "../common/TCPIP/TCPConnection.h"
#include "../Common/Util/RateLimiter.h"

#include "RecipientParser.h"

namespace HM
{

   class SMTPConfiguration;
   class Message;
   class Messages;
   class TransparentTransmissionBuffer;
   class MessageData;
   class Domain;
   class SpamTestResult;
   class AuthenticationResults;
   class Account;
   class ScramSha256;

   enum eSMTPCommandTypes
   {
      SMTP_COMMAND_UNKNOWN = 0,
      SMTP_COMMAND_HELO = 1001,  
      SMTP_COMMAND_HELP = 1002,  
      SMTP_COMMAND_QUIT = 1003,  
      SMTP_COMMAND_EHLO = 1004,  
      SMTP_COMMAND_AUTH = 1005,  
      SMTP_COMMAND_MAIL = 1006,  
      SMTP_COMMAND_RCPT = 1007,  
      SMTP_COMMAND_TURN = 1008,  
      SMTP_COMMAND_VRFY = 1009,
      SMTP_COMMAND_DATA = 1010,
      SMTP_COMMAND_RSET = 1011,
      SMTP_COMMAND_NOOP = 1012,
      SMTP_COMMAND_ETRN = 1013,
      SMTP_COMMAND_STARTTLS = 1014,
      SMTP_COMMAND_BDAT = 1015   // RFC 3030 CHUNKING
   };

   class SMTPConnection : public TCPConnection
   {
   public:
      SMTPConnection(ConnectionSecurity connection_security,
         boost::asio::io_context& io_context, 
         boost::asio::ssl::context& context);
	   virtual ~SMTPConnection();
      
   protected:

      virtual void OnConnected();
      virtual void OnHandshakeCompleted();
      virtual void OnHandshakeFailed() {};
      virtual AnsiString GetCommandSeparator() const;

      virtual void ParseData(const AnsiString &sRequest);
      virtual void ParseData(std::shared_ptr<ByteBuffer> pBuf);

      virtual void OnConnectionTimeout();
      virtual void OnExcessiveDataReceived();

   private:

      bool CheckStartTlsRequired_();
      void EnqueueWrite_(const String &sData);
      void SendBanner_();

      bool ParseAddressWithExtensions_(String mailFrom, String &address, String &parameters);
      void HandleSMTPFinalizationTaskCompleted_();
      void LogFinalizationStage_(const AnsiString &stage, ULONGLONG startTick);
      // Returns true (and sends a temporary 451, discarding the message) when the
      // accept/save work has run longer than the configured deadline since
      // end-of-data. See #18.
      bool FinalizationDeadlineExceeded_(ULONGLONG elapsedMs);

      // Leaves binary (DATA) mode and re-arms command reading, first handing back
      // any bytes the client pipelined behind the standard end-of-data marker so
      // they are parsed as the commands they are. Every post-DATA continuation
      // path must use this rather than SetReceiveBinary(false)+EnqueueRead(),
      // or a pipelined QUIT / next MAIL FROM silently disappears.
      void ResumeCommandModeAfterData_();

      virtual void InternalParseData(const AnsiString &sRequest);

      enum SpamProtectionType
      {
         SPNone = 0,
         SPPreTransmission = 1,
         SPPostTransmission = 2
      };
         
      enum Constants
      {
         MaxNumberOfRecipients = 50000,
         // RFC 3030: upper bound on the octets read from the socket per BDAT
         // sub-read, so a large chunk is spooled in bounded, flushable pieces.
         BDAT_READ_PIECE = 40000
      };

      void InitializeSpamProtectionType_(const String &sFromAddress);

      bool CheckLineEndings_() const;

      void LogClientCommand_(const String &sClientData);

      void LogAwstatsMessageRejected_();
      
      bool DoSpamProtection_(SpamProtectionType spType, const String &sFromAddress, const String &hostName, const IPAddress &lIPAddress);
      // Does IP based spam protection. Returns true if we should
      // continue delivery, false otherwise.

      void ResetCurrentMessage_();
      bool CheckIfValidSenderAddress(const String &sFromAddress);
      
      bool ReAuthenticateUser();

      void AppendMessageHeaders_();

      eSMTPCommandTypes GetCommandType_(const String &sType);

      void DoPreAcceptMessageModifications_();

      // Writes the RFC 8601 Authentication-Results and RFC 7208 9.1 Received-SPF
      // fields, and removes any Authentication-Results field the message arrived with
      // that claims our own authserv-id. Does nothing unless one of the two settings
      // is on.
      void AddAuthenticationResultHeaders_();
      // Make changes to the message before it's accepted for delivery. This is
      // for example where message signature and spam-headers are added.

      void SetMessageSignature_(std::shared_ptr<MessageData> &pMessageData);
      // Sets the signature of the message, based on the signature in the account
      // settings and domain settings.

      bool OnPreAcceptTransfer_();
      bool DoPreAcceptSpamProtection_();

      void ProtocolEHLO_(const String &sRequest);
      void ProtocolHELO_(const String &sRequest);
      void ProtocolAUTH_(const String &sRequest);
      void ProtocolNOOP_();
      void ProtocolRSET_();

      bool LookupRoute_(const String &sToAddress, bool &bDomainIsRemote);

      // SECURITY: per-account outbound sending ceiling (RateLimiter). Establishes
      // the account and its limits for the transaction about to start and consumes
      // one message from the budget. Returns false when the transaction must be
      // refused, having already answered the client.
      bool CheckAccountSendingQuotaAtMailFrom_();
      // Consumes one envelope recipient from the same budget. Returns false when
      // the recipient must be refused, having already answered the client.
      bool CheckAccountSendingQuotaAtRcptTo_();
      // Answers a 4xx (never 5xx - a legitimate user who reaches a daily ceiling
      // should have their client retry, not have the mail bounced back) and logs
      // the refusal.
      void RefuseForAccountSendingQuota_(AccountQuotaResult quotaResult);

      void ProtocolMAIL_(const String &Request);
      void ProtocolQUIT_();
      void ProtocolHELP_();
      void ProtocolRCPT_(const String &Request);
      void ProtocolETRN_(const String &sRequest);
      // True when the client's security range permits relaying to a remote
      // destination without authentication. ETRN asks the server to flush its queue
      // towards a route, so it is gated on this (or on an authenticated session).
      bool RelayToRemotePermittedWithoutAuth_();
      void ProtocolSTARTTLS_(const String &sRequest);
      void ProtocolUsername_(const String &sRequest);
      void ProtocolPassword_(const String &sRequest);
      void ProtocolDATA_();

      // RFC 3030 CHUNKING: handle a "BDAT <size> [LAST]" command and the byte-counted
      // chunk payload that follows it.
      void ProtocolBDAT_(const String &sRequest);
      void HandleBdatChunkData_(std::shared_ptr<ByteBuffer> pBuf);
      void CompleteBdatMessage_();
      // Resolves the client's PTR record on a worker thread (started at connection
      // time) and reads the result when the Received header is generated. The
      // blocking DnsQuery must never run on the network I/O thread: an unresolvable
      // PTR (internal relay, no reverse zone) can take many seconds of retries,
      // during which the whole session - and its I/O thread - would stall mid-DATA.
      void PrefetchPtrRecord_();
      String GetPtrRecordHost_();
      // Consumes and throws away the payload of a rejected BDAT command. The client
      // sends the chunk without waiting for our reply (RFC 3030), so the octets must
      // be read off the wire to keep the session synchronized.
      void StartBdatDiscard_(size_t chunkSize);

      void ReportUnsupportedEsmtpExtension_(const String &parameter);

      void AuthenticateUsingPLAIN_(const String &sLine);
      // Authenticates using a PLAIN line.

      void AuthenticateUsingBearer_(const String &sLine);
      // Authenticates using a SASL XOAUTH2 / OAUTHBEARER (RFC 7628) client response.

      std::shared_ptr<const Account> LookupActiveAccount_(const String &sAddress);
      // Looks up an active account in an active domain (OAuth2 bearer login).

      // SCRAM-SHA-256 (RFC 5802 / RFC 7677) SASL exchange over SMTP (RFC 4954).
      // State lives on the connection (scram_session_); each base64 SASL message is
      // exchanged via 334 continuations and the lines are routed by current_state_.
      void ProtocolScramClientFirst_(const String &sRequest);
      void ProtocolScramClientFinal_(const String &sRequest);
      void FinishScramAuth_();
      void ScramAuthFailed_();
      std::shared_ptr<const Account> LookupPbkdf2Account_(const String &sAddress);

      void FireOnClientLogon_(const String &sUsername, bool isAuthenticated);
      // Fires the OnClientLogon script event (shared by the password and SCRAM paths).

      void Authenticate_();
      // validates the username and password.

      void RestartAuthentication_();
      // restarts the authentication process.
      
      void ResetLoginCredentials_();
      // restarts the authentication process.

      bool SendEHLOKeywords_();

      int GetMaxMessageSize_(std::shared_ptr<const Domain> pDomain);

      bool ReadDomainAddressFromHelo_(const String &sRequest);

      void SendErrorResponse_(int iErrorCode, const String &sResponse);
      // RFC 2034: enqueue a response, prefixing the RFC 3463 enhanced status code
      // when the session is ESMTP (the client greeted with EHLO).
      void SendResponse_(int code, const String &enhancedCode, const String &text);
      static String DeriveEnhancedStatusCode_(int code);
      // SIZE (RFC 1870) parameter parsing. Returns false for anything that is not a
      // representable decimal octet count, so the caller can answer 501 instead of
      // silently treating a malformed declaration as "no size given".
      static bool ParseSizeParameter_(const String &value, unsigned __int64 &size);
      // DSN (RFC 3461) parameter validation helpers.
      static bool IsValidXtext_(const String &value);
      static bool IsValidOrcpt_(const String &value);
      static bool ParseDsnNotify_(const String &value, int &notify);
      bool GetDoSpamProtection_();

      bool GetIsLocalSender_();

      bool GetAuthIsEnabled_();

      void HandleUnableToSaveMessageDataFile_(const String &file_name);

      String GetSpamTestResultMessage_(std::set<std::shared_ptr<SpamTestResult> > testResult) const;



      enum ConnectionState
      {
         INITIAL = 1,
         SMTPUSERNAME = 3,
         SMTPUPASSWORD = 4,
         HEADER = 5,
         DATA = 6,
         STARTTLS = 7,
         SMTPSCRAMFIRST = 8,   // awaiting the SCRAM client-first message
         SMTPSCRAMFINAL = 9,   // awaiting the SCRAM client-final message
         SMTPSCRAMACK = 10,    // awaiting the empty ack after the server-final message
         SMTPBEARERRESPONSE = 11, // awaiting the SASL XOAUTH2 / OAUTHBEARER client response
         BDATDATA = 12         // RFC 3030: receiving the octets of a BDAT chunk
      };
  
      enum AuthenticationType
      {
         AUTH_NONE = 0,
         AUTH_PLAIN = 2,
         AUTH_LOGIN = 3,
         AUTH_SCRAM_SHA256 = 4,
         AUTH_BEARER = 5,
      };

      

      ConnectionState current_state_;

      std::shared_ptr<Message> current_message_;

      bool trace_headers_written_;

      String username_;
      String password_;

      std::shared_ptr<SMTPConfiguration> smtpconf_;
   
      AuthenticationType requestedAuthenticationType_;

      std::shared_ptr<ScramSha256> scram_session_;
      
      DWORD message_start_tc_;

      // When the final dot / last BDAT chunk was received and the accept/save work
      // was handed to the async queue. The client (a relaying MTA) starts its
      // data-done timeout at this instant, so this is the clock the accept path
      // races - used for the queue-wait log and the finalization deadline.
      ULONGLONG finalization_enqueued_tick_ = 0;

      // Bytes the client pipelined behind the standard end-of-data marker, captured
      // from the transmission buffer the moment the marker was recognised and
      // injected back into the read stream when command mode resumes. Postfix puts
      // QUIT - or the next MAIL FROM - there routinely.
      std::shared_ptr<ByteBuffer> pipelined_input_after_data_;

      size_t max_message_size_kb_;
      // Maximum message size in KB.

      String helo_host_;

      std::shared_ptr<TransparentTransmissionBuffer> transmission_buffer_;

      // Spam detection 
      bool rejected_by_delayed_grey_listing_;
      int cur_no_of_rcptto_;
      int cur_no_of_invalid_commands_;
      
      std::shared_ptr<const Domain> sender_domain_;
      std::shared_ptr<const Account> sender_account_;

      std::set<std::shared_ptr<SpamTestResult> > spam_test_results_;

      // What the spam tests concluded about this message's authentication, for the
      // RFC 8601 header. Owned by the connection because SPF runs pre-transmission
      // while DKIM and DMARC run post-transmission, so nothing scoped to one phase
      // could hold the whole answer.
      //
      // Null unless AuthenticationResultsEnabled or ReceivedSpfHeaderEnabled is on, so
      // by default this costs one null check per message and nothing else.
      std::shared_ptr<AuthenticationResults> authentication_results_;

      bool re_authenticate_user_;
      bool pending_disconnect_;
      bool isAuthenticated_;
      int authentication_failure_count_;
      SpamProtectionType type_;

      // True once the client greeted with EHLO (ESMTP). Used to decide whether to
      // emit RFC 2034 enhanced status codes in responses.
      bool esmtp_session_;

      // RFC 6531 (SMTPUTF8): set for the current transaction when the client added
      // the SMTPUTF8 parameter to MAIL FROM, relaxing address validation to UTF-8.
      bool smtputf8_requested_;

      // RFC 3461 (DSN) per-transaction parameters from MAIL FROM.
      String dsn_envid_;
      String dsn_ret_;

      // RFC 3030 (CHUNKING/BDAT) per-transaction state.
      bool bdat_active_;            // a BDAT chunk has been received in this transaction
      bool bdat_last_;              // the chunk currently being received carries LAST
      bool bdat_discard_;           // the chunk being received belongs to a rejected BDAT: consume, don't store
      size_t bdat_chunk_size_;      // declared octet count of the current chunk
      size_t bdat_chunk_remaining_; // octets of the current chunk still to be received

      boost::mutex ptr_result_mutex_;
      bool ptr_lookup_completed_;   // PrefetchPtrRecord_ has stored a result
      String ptr_record_host_;      // PTR host for the Received header ("Unknown" when unresolvable)

      // SECURITY: the per-account outbound sending ceiling in force for the current
      // transaction. Established at MAIL FROM: quota_account_ is the authenticated
      // account (empty for an unauthenticated session, which is never limited -
      // inbound mail must not be affected), and quota_limits_ is unlimited unless
      // an administrator has configured [SendingLimits] in hMailServer.ini.
      String quota_account_;
      AccountSendingLimits quota_limits_;

      RecipientParser recipientParser_;
      bool start_tls_used_;
   };
}
