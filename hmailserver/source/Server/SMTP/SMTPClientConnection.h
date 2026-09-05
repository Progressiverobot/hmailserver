// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "../common/Util/TransparentTransmissionBuffer.h"
#include "../common/BO/Message.h"
#include "../common/TCPIP/TCPConnection.h"


namespace HM
{
   class ByteBuffer;
   class MessageRecipient;
  
   class SMTPClientConnection : public TCPConnection
   {
   public:
      SMTPClientConnection(ConnectionSecurity connection_security,
         boost::asio::io_context& io_context, 
         boost::asio::ssl::context& context,
         std::shared_ptr<Event> disconnected,
         AnsiString remote_hostname);
      virtual ~SMTPClientConnection();

      void OnCouldNotConnect(const AnsiString &sErrorDescription);

      virtual void ParseData(const AnsiString &Request);
      virtual void ParseData(std::shared_ptr<ByteBuffer> ) {}
      int SetDelivery(std::shared_ptr<Message> pDelMsg, std::vector<std::shared_ptr<MessageRecipient> > &vecRecipients);
           
      void SetAuthInfo(const String &sUsername, const String &sPassword);

      // Makes the AUTH step XOAUTH2 with this bearer token instead of LOGIN with
      // the password. The username still comes from SetAuthInfo - it is the
      // account being relayed as; the token replaces only the proof.
      void SetOAuthBearer(const String &token);
   protected:

      virtual void OnConnected();
      virtual void OnHandshakeCompleted();
      virtual void OnHandshakeFailed();
      virtual AnsiString GetCommandSeparator() const;

      virtual void EnqueueWrite_(const String &sData);
      virtual void OnConnectionTimeout();
      virtual void OnExcessiveDataReceived();
      virtual void OnDataSent();
      virtual void OnReadError(int errorCode);

   private:

      void LogReceivedResponse_(const String &response);

      void ProtocolStateHELOEHLO_(const AnsiString &request);
      void ProtocolSendMailFrom_();
      bool EnvelopeRequiresSmtpUtf8_() const;
      void ProtocolHELOSent_(const AnsiString &request);
      void ProtocolEHLOSent_(int code, const AnsiString &request);
      void ProtocolSTARTTLSSent_(int code);
      void ProtocolMailFromSent_();
      void ProtocolRcptToSent_(int code, const AnsiString &request);
      void ProtocolData_();

      void HandleHandshakeFailed_();
      bool InternalParseData(const AnsiString &Request);
      void ReadAndSend_();
     
      bool IsPositiveCompletion(int iErrorCode);
      bool IsPermanentNegative(int lErrorCode);

      void LogSentCommand_(const String &sData);
      void StartSendFile_(const String &sFilename);
      void FailDeliveryDueToUnreadableFile_(const String &sFilename, const String &sReason, bool permanent);

      void SendQUIT_();

      void ProtocolSendUsername_();
      void ProtocolSendPassword_();

      // enhancedStatusCode is the RFC 3463 status this failure is reported as in
      // the delivery-status notification, and it has no default on purpose: the
      // three-digit reply code is not enough to derive one outside the RCPT TO
      // stage, and a default would silently label every locally-decided failure
      // (no STARTTLS, over the remote's SIZE limit, a timeout) with whatever the
      // default happened to be. DeliveryFailure::EnhancedStatusFromSmtpReply
      // supplies it where the remote server really did answer.
      //
      // responseIsRemoteReply says whether sResponse is the remote server's own
      // words. Only then is it recorded as Diagnostic-Code and only then is a
      // Remote-MTA named - the rest of these messages are this server's own
      // description of why it gave up, and attributing them to the remote host
      // would be a fabricated field in someone else's bounce.
      void UpdateAllRecipientsWithError_(int iErrorCode, const AnsiString &sResponse, bool bPreConnectError, const String &enhancedStatusCode, bool responseIsRemoteReply);
      void UpdateRecipientWithError_(int iErrorCode, const AnsiString &sResponse,std::shared_ptr<MessageRecipient> pRecipient, bool bPreConnectError, const String &enhancedStatusCode, bool responseIsRemoteReply);

      std::shared_ptr<MessageRecipient> GetNextRecipient_();

      bool PipeliningAvailable_() const;
      bool ChunkingAvailable_() const;
      void SendPipelinedEnvelope_();
      void SendDataCommand_(bool pipelined);
      void StartBdat_();
      void ReadAndSendRaw_();
      bool ProtocolEnvelopePipelined_(int code, const AnsiString &request);
      void UpdateSuccessfulRecipients_();

      enum ConnectionState
      {
         HELO = 1,
         HELOSENT = 9,
         EHLOSENT = 10,
         AUTHLOGINSENT = 11,
         USERNAMESENT = 12,
         PASSWORDSENT = 13,
         MAILFROMSENT = 3,
         RCPTTOSENT = 5,
         DATAQUESTION = 6,
         DATACOMMANDSENT = 7,
         SENDINGDATA = 13,
         DATASENT = 8,
         QUITSENT = 14,
         STARTTLSSENT = 15,
         XOAUTH2SENT = 16,

         // RFC 2920: MAIL FROM, every RCPT TO and the data command went out in one
         // flight; the replies are counted back in the same order.
         ENVELOPEPIPELINED = 17,

         // RFC 3030: the BDAT chunk is queued or sent; the next reply is its result.
         BDATSENT = 18,

         // A pipelined DATA was accepted with 354 after MAIL FROM or every RCPT TO
         // had failed: an empty body was sent to end the transaction where the
         // remote expects it to end (RFC 2920 3.2), and whatever it replies, QUIT
         // follows.
         DATAABANDONED = 19
      };

      void SetState_(ConnectionState eCurState);
  
      ConnectionState current_state_;

      std::shared_ptr<Message> delivery_message_;


      // These are the recipients which will hMailServer should
      // try to deliver to.
      std::vector<std::shared_ptr<MessageRecipient> > recipients_;

      // The actual recipients are the recipients we've sent RCPT TO
      // for and the remote server has said OK to.
      std::set<std::shared_ptr<MessageRecipient> > actual_recipients_;

      bool use_smtpauth_;

      // RFC 6531: set when the remote server advertised SMTPUTF8 in its EHLO reply,
      // so an internationalized envelope address can be sent with the SMTPUTF8 mark.
      bool remote_supports_smtputf8_;

      // What the remote's EHLO said about SIZE (RFC 1870): -1 = not advertised,
      // 0 = advertised with no fixed limit, >0 = the advertised maximum.
      __int64 remote_size_limit_ = -1;

      // RFC 3030: whether the remote's EHLO advertised BINARYMIME. With CHUNKING
      // advertised as well (and OutboundChunking on), a message that arrived as
      // BINARYMIME is relayed as it is - BODY=BINARYMIME on MAIL FROM and the raw
      // bytes in a BDAT chunk. Without both, ProtocolSendMailFrom_ refuses it, and
      // this flag makes the refusal honest about why.
      bool remote_supports_binarymime_ = false;

      // RFC 2920 and RFC 3030, read from the EHLO reply. Pipelining sends the whole
      // envelope and the data command in one flight and counts the replies back;
      // chunking replaces DATA - its 354 round trip and its dot-stuffing - with
      // BDAT <size> LAST followed by the message's bytes as they are.
      bool remote_supports_pipelining_ = false;
      bool remote_supports_chunking_ = false;

      // The pipelined envelope: how many replies are owed (MAIL FROM, one per RCPT
      // TO, the data command), how many have arrived, whether MAIL FROM was
      // refused - in which case the RCPT and data replies are consumed and
      // ignored, every recipient already carrying the real error - and the
      // command each reply answers, so a bounce quotes the right one.
      unsigned int pipeline_replies_expected_ = 0;
      unsigned int pipeline_replies_seen_ = 0;
      bool pipeline_mail_failed_ = false;
      std::vector<AnsiString> pipeline_commands_;

      // Whether the data command in flight is BDAT rather than DATA.
      bool using_bdat_ = false;

      String username_;
      String password_;
      String oauth_bearer_;

      unsigned int cur_recipient_;

      bool session_ended_;

      AnsiString last_sent_data_;

      // The host name this connection was opened for. TCPConnection keeps the
      // same value privately for SNI and certificate verification, so it is kept
      // again here rather than widening that class's interface. It is what
      // Remote-MTA names in a delivery-status notification.
      AnsiString remote_host_name_;

      File current_file_;   
      TransparentTransmissionBuffer transmission_buffer_;

      AnsiString multi_line_response_buffer_;
   };
}
