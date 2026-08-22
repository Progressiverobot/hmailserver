// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "SMTPClientConnection.h"

#include "../Common/BO/MessageRecipient.h"
#include "../common/Util/Utilities.h"
#include "../common/Util/File.h"
#include "../common/Util/ByteBuffer.h"
#include "../Common/Util/TransparentTransmissionBuffer.h"
#include "../Common/Util/OutboundOAuth2TokenClient.h"
#include "../Common/Util/BATV.h"
#include "../Common/Util/SRS.h"
#include "../Common/Persistence/PersistentMessage.h"
#include "../common/Cache/CacheContainer.h"
#include "../Common/BO/Domain.h"

#include "../Common/Application/TimeoutCalculator.h"

#include "DeliveryFailure.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{

   SMTPClientConnection::SMTPClientConnection(ConnectionSecurity connection_security,
      boost::asio::io_context& io_context, 
      boost::asio::ssl::context& context,
      std::shared_ptr<Event> disconnected,
      AnsiString expected_remote_hostname) :
      TCPConnection(connection_security, io_context, context, disconnected, expected_remote_hostname),
      current_state_(HELO),
      use_smtpauth_(false),
      cur_recipient_(-1),
      session_ended_(false),
      remote_supports_smtputf8_(false),
      remote_host_name_(expected_remote_hostname),
      transmission_buffer_(true)
   {
      
      /* RFC 2821:    
         An SMTP server SHOULD have a timeout of at least 5 minutes while it
         is awaiting the next command from the sender. 

         Since the DATA command has a timeout on 10 minutes, we can just
         as well set the entire timeout to 10.
      */
      
      TimeoutCalculator calculator;
      SetTimeout(calculator.Calculate(IniFileSettings::Instance()->GetSMTPCMinTimeout(), IniFileSettings::Instance()->GetSMTPCMaxTimeout()));

      // That timeout is an idle deadline, pushed forward on every read. A remote
      // MX that answers each command with a single byte just before it expires
      // keeps this session alive indefinitely - and ExternalDelivery blocks one of
      // a small pool of delivery threads until this connection is destroyed, so a
      // handful of such hosts stops outbound mail entirely. The ceiling is
      // generous enough for a large message over a genuinely slow link.
      SetSessionCeiling(IniFileSettings::Instance()->GetClientSessionCeiling());
   }

   SMTPClientConnection::~SMTPClientConnection()
   {
      
   }

   void
   SMTPClientConnection::OnConnected()
   {
      if (GetConnectionSecurity() == CSNone || 
          GetConnectionSecurity() == CSSTARTTLSRequired ||
          GetConnectionSecurity() == CSSTARTTLSOptional)
      {
         EnqueueRead();
      }
   }

   void
   SMTPClientConnection::OnHandshakeCompleted()
   {
      if (GetConnectionSecurity() == CSSSL)
         EnqueueRead();
      else if (GetConnectionSecurity() == CSSTARTTLSRequired ||
               GetConnectionSecurity() == CSSTARTTLSOptional)
      {
         ProtocolStateHELOEHLO_("");
         EnqueueRead();
      }
   }

   void
   SMTPClientConnection::OnHandshakeFailed()
   {
      HandleHandshakeFailed_();
   }

   void
   SMTPClientConnection::HandleHandshakeFailed_() 
   {
      if (GetConnectionSecurity() == CSSTARTTLSOptional)
      {
         for(std::shared_ptr<MessageRecipient> recipient : recipients_)
            recipient->SetDeliveryResult(MessageRecipient::ResultOptionalHandshakeFailed);
      }
   }

   AnsiString 
   SMTPClientConnection::GetCommandSeparator() const
   {
      return "\r\n";
   }

   int
   SMTPClientConnection::SetDelivery(std::shared_ptr<Message> pDelMsg, std::vector<std::shared_ptr<MessageRecipient> > &vecRecipients)
   {
      delivery_message_ = pDelMsg;
      recipients_ = vecRecipients;
      session_ended_ = false;
      
      return 0;
   }

   void
   SMTPClientConnection::ParseData(const AnsiString &Request)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Parses a server SMTP cmmand.
   //---------------------------------------------------------------------------()
   {
      multi_line_response_buffer_ += Request;

      if (multi_line_response_buffer_.GetLength() > 10000)
      {
         // RFC 3463 X.5.2 "Syntax error": what came back could not be interpreted
         // as an SMTP reply at all. Our judgement, not the remote's, so no
         // Diagnostic-Code.
         UpdateAllRecipientsWithError_(500, "Unexpected response from server (too long).", false, _T("5.5.2"), false);
         return;
      }

      if (Request.GetLength() > 3 && (Request.GetAt(3) == '-'))
      {
         // Multi-line response. Wait for full buffer.
         multi_line_response_buffer_ += "\r\n";
         EnqueueRead();
         return;
      }

      bool postReceive = InternalParseData(multi_line_response_buffer_);

      multi_line_response_buffer_.Empty();

      if (postReceive)
         EnqueueRead();
   }

   void 
   SMTPClientConnection::LogReceivedResponse_(const String &response)
   {
      String log_data = "RECEIVED: " + response;

      log_data.TrimRight(_T("\r\n"));

      LOG_SMTP_CLIENT(GetSessionID(), GetRemoteEndpointAddress().ToString(), log_data);
   }

   bool
   SMTPClientConnection::InternalParseData(const AnsiString &Request)
   {
      LogReceivedResponse_(Request);

      int lFirstSpace = Request.Find(" ");
   
      AnsiString sFirstWordTemp;
      if (lFirstSpace < 0)
         sFirstWordTemp = Request;
      else
         sFirstWordTemp = Request.Mid(0, lFirstSpace);

      sFirstWordTemp.MakeUpper();
      int iCode = atoi(sFirstWordTemp);
   
      bool ifFailureFailAllRecipientsAndQuit =
         current_state_ == HELO ||
         current_state_ == HELOSENT ||
         current_state_ == AUTHLOGINSENT ||
         current_state_ == USERNAMESENT ||
         current_state_ == PASSWORDSENT ||
         current_state_ == XOAUTH2SENT ||
         current_state_ == MAILFROMSENT ||
         current_state_ == DATACOMMANDSENT ||
         current_state_ == DATASENT ||
         current_state_ == PASSWORDSENT;

      if (ifFailureFailAllRecipientsAndQuit)
      {
         if (!IsPositiveCompletion(iCode))
         {
            // A refused XOAUTH2 drops the cached token before the retry logic
            // ever sees this delivery again: a revoked token and one expired by
            // clock skew both look like this, and both are cured by fetching,
            // not by presenting the same token to the same refusal. (M365's
            // 334-with-error-JSON challenge also lands here - 334 is not a
            // completion - and the JSON, base64 in the reply, travels into the
            // recipient error where an administrator can read it.)
            if (current_state_ == XOAUTH2SENT)
               OutboundOAuth2TokenClient::Instance()->Invalidate();

            // A real reply from the remote server, so its own enhanced code is
            // preferred over anything derived here. Not the RCPT TO stage - this
            // branch covers HELO/AUTH/MAIL FROM/DATA - so the reply code alone
            // says nothing address-specific and the fallback stays undefined for
            // the class.
            UpdateAllRecipientsWithError_(iCode, Request, false,
               DeliveryFailure::EnhancedStatusFromSmtpReply(iCode, Request, false), true);
            SendQUIT_();
            return true;
         }
      }

      switch (current_state_)
      {
      case HELO:
         ProtocolStateHELOEHLO_(Request);  
         return true;
      case HELOSENT:
         ProtocolHELOSent_(Request);
         return true;
      case EHLOSENT:
         ProtocolEHLOSent_(iCode, Request);
         return true;
      case STARTTLSSENT:
         ProtocolSTARTTLSSent_(iCode);
         return false;
      case AUTHLOGINSENT:
         ProtocolSendUsername_();
         return true;
      case USERNAMESENT:
         ProtocolSendPassword_();
         return true;
      case PASSWORDSENT:
         ProtocolSendMailFrom_();
         return true;
      case XOAUTH2SENT:
         // 235: authenticated in one round trip - the initial response carried
         // everything.
         ProtocolSendMailFrom_();
         return true;
      case MAILFROMSENT:
         ProtocolMailFromSent_();
         return true;
      case DATACOMMANDSENT:
         ProtocolData_();
         return false;
      case DATASENT:
         SendQUIT_();
         UpdateSuccessfulRecipients_();
         return true;
      case RCPTTOSENT:
         ProtocolRcptToSent_(iCode, Request);
         return true;
      case QUITSENT:
         // We just received a reply on our QUIT. Time to disconnect.
         EnqueueDisconnect();
         return false;
      }

      return true;
   }

   void
   SMTPClientConnection::ProtocolStateHELOEHLO_(const AnsiString &request)
   {
      // Always open with EHLO (RFC 5321 section 3.2): a plain delivery that
      // opened with HELO never learned the remote's capabilities, so SIZE,
      // SMTPUTF8 and the rest were only ever used on routes needing TLS or
      // authentication - the ordinary MX-to-MX case got none of them. A server
      // that rejects EHLO gets the HELO fallback in ProtocolEHLOSent_.
      String computer_name = Utilities::ComputerName();

      EnqueueWrite_("EHLO " + computer_name);
      SetState_(EHLOSENT);
   }

   void
   SMTPClientConnection::ProtocolMailFromSent_()
   {
      std::shared_ptr<MessageRecipient> pRecipient = GetNextRecipient_();
      if (!pRecipient) 
      {
         SendQUIT_();
         return;
      }

      String sRecipient = pRecipient->GetAddress();
      String sData = "RCPT TO:<" + sRecipient + ">";
      EnqueueWrite_(sData);
      current_state_ = RCPTTOSENT;
   }
   
   void
   SMTPClientConnection::ProtocolRcptToSent_(int code, const AnsiString &request)
   {
      if (cur_recipient_ < recipients_.size())
      {
         if (IsPositiveCompletion(code))
         {
            actual_recipients_.insert(recipients_[cur_recipient_]);
         }
         else
         {
            // The one place in the session where the three-digit code is about
            // THIS address (RFC 5321 4.2.3), so the reply-code mapping is
            // allowed to be recipient-specific here and nowhere else.
            UpdateRecipientWithError_(code, request, recipients_[cur_recipient_], false,
               DeliveryFailure::EnhancedStatusFromSmtpReply(code, request, true), true);
         }
      }

      std::shared_ptr<MessageRecipient> pRecipient = GetNextRecipient_();
      if (pRecipient)
      {
         // Send next recipient.
         EnqueueWrite_("RCPT TO:<" + pRecipient->GetAddress() + ">");
         current_state_ = RCPTTOSENT;
      }
      else
      {
         if (actual_recipients_.size() == 0)
         {
            SendQUIT_();
         }
         else
         {
            EnqueueWrite_("DATA");
            current_state_ = DATACOMMANDSENT;
         }
         
      }
   }


   void
   SMTPClientConnection::ProtocolData_()
   {
      // Send the data!
      const String fileName = PersistentMessage::GetFileName(delivery_message_);
      StartSendFile_(fileName);
   }

   void
   SMTPClientConnection::ProtocolHELOSent_(const AnsiString &request)
   {
      ProtocolSendMailFrom_();
   }

   void
   SMTPClientConnection::ProtocolEHLOSent_(int code, const AnsiString &request)
   {
      if (!IsPositiveCompletion(code))
      {
         bool ehlo_required = GetConnectionSecurity() == CSSTARTTLSRequired ||
                              use_smtpauth_;

         if (ehlo_required)
         {
            // hMailServer is configured to require EHLO, but the remote server does not support it.
            // RFC 3463 X.5.1 "Invalid command": the command this server must use
            // is one the remote does not support.
            UpdateAllRecipientsWithError_(500, "Server does not support EHLO command.", false, _T("5.5.1"), false);
            SendQUIT_();
         }
         else
         {
            // Server does not support EHLO, but we do not require it. Switch to HELO.
            String computer_name = Utilities::ComputerName(); 
            EnqueueWrite_("HELO " + computer_name);
            SetState_(HELOSENT);
         }

         return;
      }

      // Remember whether the remote server can accept internationalized (UTF-8)
      // envelope addresses (RFC 6531), so MAIL FROM can carry the SMTPUTF8 mark.
      remote_supports_smtputf8_ = request.Contains("SMTPUTF8");

      // RFC 1870: remember the remote's SIZE advertisement, so MAIL FROM can
      // declare this message's size and an oversized message can be refused
      // BEFORE its bytes are transferred rather than after.
      remote_size_limit_ = -1;
      remote_supports_binarymime_ = false;
      std::vector<AnsiString> ehloLines = StringParser::SplitString(request, "\r\n");
      for (const AnsiString &line : ehloLines)
      {
         // RFC 3030: an exact-keyword match ("250-BINARYMIME" / "250 BINARYMIME"),
         // not a substring scan, so an unrelated capability containing the word
         // cannot count as support. Informational only - see the member.
         if (line.GetLength() >= 4)
         {
            AnsiString keyword = line.Mid(4);
            keyword.Trim();
            if (keyword.CompareNoCase("BINARYMIME") == 0)
               remote_supports_binarymime_ = true;
         }

         // A capability line is "250-SIZE 52428800" or "250 SIZE" - four digits,
         // one separator, then the keyword. Only the first SIZE line counts (no
         // "break" here: this loop keeps scanning for the other capabilities
         // above, so breaking on SIZE would hide any keyword listed after it).
         if (remote_size_limit_ >= 0 || line.GetLength() < 8 || line.Mid(4, 4).CompareNoCase("SIZE") != 0)
            continue;

         AnsiString value = line.Mid(8);
         value.TrimLeft();
         remote_size_limit_ = value.IsEmpty() ? 0 : _atoi64(value.c_str());
      }

      if (GetConnectionSecurity() == CSSTARTTLSRequired || 
          GetConnectionSecurity() == CSSTARTTLSOptional)
      {
         if (!IsSSLConnection())
         {
            if (request.Contains("STARTTLS"))
            {
               EnqueueWrite_("STARTTLS");
               SetState_(STARTTLSSENT);
               return;
            }
            else
            {
               // Remote server does not support STARTTLS
               if (GetConnectionSecurity() == CSSTARTTLSRequired)
               {
                  // RFC 3463 X.7.0 "Other or undefined security status". The
                  // message is refused for a security reason of this server's
                  // choosing; RFC 3463 registers no code for "encryption was
                  // required and not offered", and the undefined security code is
                  // the honest cover rather than a more specific one that means
                  // something else.
                  UpdateAllRecipientsWithError_(500, "Server does not support STARTTLS.", false, _T("5.7.0"), false);
                  SendQUIT_();
                  return;
               }
            }
         }
      }

      if (use_smtpauth_)
      {
         if (!oauth_bearer_.IsEmpty())
         {
            // XOAUTH2 (the Microsoft variant - Exchange Online implements this
            // and not RFC 7628 OAUTHBEARER), with the initial response on the
            // AUTH line: base64 of "user=<user>^Aauth=Bearer <token>^A^A". The
            // password is deliberately not a fallback; a provider configured
            // for OAuth has Basic auth off, and a quiet downgrade would send a
            // password to a server that no longer wants one.
            String blob = _T("user=") + username_ + _T("\x01") +
                          _T("auth=Bearer ") + oauth_bearer_ + _T("\x01\x01");

            String encoded;
            StringParser::Base64Encode(blob, encoded);

            EnqueueWrite_("AUTH XOAUTH2 " + encoded);
            SetState_(XOAUTH2SENT);
         }
         else
         {
            // Ask the server to initiate login process.
            EnqueueWrite_("AUTH LOGIN");
            SetState_(AUTHLOGINSENT);
         }
      }
      else
      {
         ProtocolSendMailFrom_();
      }
   }

   void
   SMTPClientConnection::ProtocolSTARTTLSSent_(int code)
   {
      if (IsPositiveCompletion(code))
      {
         EnqueueHandshake();
      }
      else
      {
         HandleHandshakeFailed_();
      }
   }

   void
   SMTPClientConnection::ProtocolSendMailFrom_()
   {
      // RFC 3030 section 4: a message received with BODY=BINARYMIME may hold bare
      // CR, bare LF and NUL octets, and MUST NOT be sent to a server that has not
      // advertised BINARYMIME. This client cannot send one to ANY server: it
      // transmits message content via DATA only (line-oriented, dot-stuffed,
      // CRLF.CRLF-terminated), which such content does not survive, and
      // down-conversion (re-encoding the binary parts as base64) is not
      // implemented. Every path to this point funnels through here - EHLO, the
      // HELO fallback, and the AUTH flows - so this is the one choke point, and
      // the refusal is permanent: 554 with 5.6.3 (conversion required but not
      // supported, RFC 3463), which DSNs back to the sender.
      //
      // Directly-relayed recipients are normally refused at RCPT time by
      // SMTPConnection, so what reaches this point is an indirect route outward:
      // a local distribution list with an external member, a local alias
      // resolving to an external address, a route whose recipients are treated
      // as local, a forward, a rule or a mirror.
      if (delivery_message_ && delivery_message_->GetBinaryMime())
      {
         String errorMessage = remote_supports_binarymime_
            ? _T("This message was received as BINARYMIME (raw binary content, RFC 3030) and cannot be relayed: the receiving server does advertise BINARYMIME, but this server's delivery client transmits via DATA only and cannot send binary content. Conversion required but not supported.")
            : _T("This message was received as BINARYMIME (raw binary content, RFC 3030) and cannot be relayed: the receiving server did not advertise BINARYMIME, and converting the content to a 7-bit-safe encoding is not supported. Conversion required but not supported.");

         // Loud on purpose (HM6340): every occurrence is a permanently bounced
         // message, and the administrator reading the error log should not have
         // to reconstruct the cause from the DSN alone.
         String logMessage;
         logMessage.Format(_T("Message %I64d was received with BODY=BINARYMIME and had a recipient requiring onward relay (via a distribution list, forward, rule or mirror). It was not transmitted and a DSN (554 5.6.3) was generated. This server cannot relay binary messages; the sender must re-encode the content."),
            delivery_message_->GetID());
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6340, "SMTPClientConnection::ProtocolSendMailFrom_", logMessage);

         UpdateAllRecipientsWithError_(554, errorMessage, false, _T("5.6.3"), false);
         SendQUIT_();
         return;
      }

      String sFrom = delivery_message_->GetFromAddress();

      // BATV (prvs): sign the envelope return-path of locally-originated outbound
      // mail so that any bounce sent back to it can be validated (backscatter
      // protection). Only non-empty senders at one of our own local domains are
      // signed, and an already-tagged address (SRS0 or prvs) is never re-signed.
      // The signature is applied to the wire envelope only; the stored message is
      // left untouched, and the envelope domain is preserved so SPF stays aligned.
      if (!sFrom.IsEmpty() &&
          IniFileSettings::Instance()->GetBATVEnabled() &&
          !SRS::IsSRS0(sFrom) && !BATV::IsPrvs(sFrom))
      {
         String fromDomain = StringParser::ExtractDomain(sFrom);
         if (CacheContainer::Instance()->GetDomain(fromDomain))
         {
            String tagged = BATV::Sign(sFrom, IniFileSettings::Instance()->GetBATVSecret());
            if (!tagged.IsEmpty())
               sFrom = tagged;
         }
      }

      String sData = "MAIL FROM:<" + sFrom + ">";

      // RFC 6531: if the envelope contains an internationalized (UTF-8) address and
      // the remote server advertised SMTPUTF8, mark the transaction accordingly.
      if (remote_supports_smtputf8_ && EnvelopeRequiresSmtpUtf8_())
         sData += " SMTPUTF8";

      // RFC 1870: declare the size when the remote advertised SIZE - and when it
      // also advertised a limit this message exceeds, do not send the message at
      // all. Failing here costs one round trip; failing after DATA costs the
      // whole transfer, and some servers only enforce their limit at that point.
      if (remote_size_limit_ >= 0)
      {
         __int64 messageSize = delivery_message_->GetSize();

         if (remote_size_limit_ > 0 && messageSize > remote_size_limit_)
         {
            String errorMessage;
            errorMessage.Format(_T("The message is %I64d bytes and the receiving server accepts at most %I64d (its EHLO SIZE limit). It was not transferred."),
               messageSize, remote_size_limit_);

            // RFC 3463 X.3.4 "Message too big for system" - and this one is known
            // exactly, because the remote published the limit in its own EHLO.
            UpdateAllRecipientsWithError_(552, errorMessage, false, _T("5.3.4"), false);
            SendQUIT_();
            return;
         }

         String sizeParameter;
         sizeParameter.Format(_T(" SIZE=%I64d"), messageSize);
         sData += sizeParameter;
      }

      EnqueueWrite_(sData);
      current_state_ = MAILFROMSENT;
   }

   bool
   SMTPClientConnection::EnvelopeRequiresSmtpUtf8_() const
   {
      if (StringParser::ContainsNonAscii(delivery_message_->GetFromAddress()))
         return true;

      for (std::shared_ptr<MessageRecipient> recipient : recipients_)
      {
         if (StringParser::ContainsNonAscii(recipient->GetAddress()))
            return true;
      }

      return false;
   }

   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Updates all the actual recipients with 'success'. This is done after the
   // message delivery has been accepted by the remote server.
   //---------------------------------------------------------------------------()
   void
   SMTPClientConnection::UpdateSuccessfulRecipients_()
   {
      for(std::shared_ptr<MessageRecipient> actualRecipient : actual_recipients_)
         actualRecipient->SetDeliveryResult(MessageRecipient::ResultOK);

   }

   void
   SMTPClientConnection::LogSentCommand_(const String &sData)
   {
      if (!(Logger::Instance()->GetLogMask() & Logger::LSSMTP))
         return;

      String sLogData = sData;

      if (current_state_ == PASSWORDSENT)
      {
         // Password has been sent. Remove from log.
         sLogData = "***";
      }

      // Append
      sLogData = "SENT: " + sLogData;
      
      sLogData.TrimRight(_T("\r\n"));

      LOG_SMTP_CLIENT(GetSessionID(), GetIPAddressString(), sLogData);
   }

   void
   SMTPClientConnection::EnqueueWrite_(const String &sData)
   {
      LogSentCommand_(sData);

      if (current_state_ == PASSWORDSENT)
         last_sent_data_ = _T("<Password removed>");
      else
      {
         last_sent_data_ = sData;         
      }

      EnqueueWrite(sData + "\r\n");
   }

   void
   SMTPClientConnection::OnConnectionTimeout()
   {
      if (session_ended_)
      {       
         LOG_DEBUG("Session has ended, but there was a timeout while waiting for a response from the remote server.");
         return; // The session has already ended, so any error which takes place now is not interesting.
      }

      // RFC 3463 X.4.2 "Bad connection", whose text names time-out explicitly:
      // the connection was established but the transaction could not complete.
      UpdateAllRecipientsWithError_(0, "There was a timeout while talking to the remote server.", false, _T("4.4.2"), false);
   }


   void
   SMTPClientConnection::OnExcessiveDataReceived()
   {
      if (session_ended_)
         return; // The session has ended, so any error which takes place now is not interesting.

      // RFC 3463 X.5.0 "Other or undefined protocol status": the remote is not
      // behaving like an SMTP server, but nothing here says it will not later.
      UpdateAllRecipientsWithError_(0, "Excessive amount of data sent to server.", false, _T("4.5.0"), false);
   }
   
   void
   SMTPClientConnection::OnReadError(int errorCode)
   {
      if (session_ended_)
         return; // The session has ended, so any error which takes place now is not interesting.

      // RFC 3463 X.4.2 "Bad connection": established, then unable to complete.
      UpdateAllRecipientsWithError_(0, "Remote server closed connection.", false, _T("4.4.2"), false);
   }

   bool 
   SMTPClientConnection::IsPermanentNegative(int lErrorCode)
   {
      if (lErrorCode >= 500 && lErrorCode <= 599)
         return true;
      else
         return false;
   }

   bool 
   SMTPClientConnection::IsPositiveCompletion(int lErrorCode)
   {
      if ((lErrorCode >= 200 && lErrorCode <= 299 ||
           lErrorCode >= 300 && lErrorCode <= 399))
         return true;
      else
         return false;
   }

   void 
   SMTPClientConnection::UpdateAllRecipientsWithError_(int iErrorCode, const AnsiString &sResponse, bool bPreConnectError, const String &enhancedStatusCode, bool responseIsRemoteReply)
   {
      auto iterRecipient = recipients_.begin();
      while (iterRecipient != recipients_.end())
      {
         UpdateRecipientWithError_(iErrorCode, sResponse, (*iterRecipient), bPreConnectError, enhancedStatusCode, responseIsRemoteReply);
         iterRecipient++;
      }

   }

   void
   SMTPClientConnection::UpdateRecipientWithError_(int iErrorCode, const AnsiString &sResponse, std::shared_ptr<MessageRecipient> pRecipient, bool bPreConnectError, const String &enhancedStatusCode, bool responseIsRemoteReply)
   {
      if (pRecipient->GetDeliveryResult() == MessageRecipient::ResultFatalError)
      {
         // This recipient is already marked as fatally failed. No need to
         // update it's status since it can't change from fatal to nonfatal.
         return;
      }

      bool bIsFatalError = IsPermanentNegative(iErrorCode);
      
      if (pRecipient->GetDeliveryResult() == MessageRecipient::ResultNonFatalError)
      {
         // If we have a non-fatal error,  but the new error is fatal, we should
         // update it's status. But if the error level is the same as before, we
         // should not. We should only update the delivery result if the result
         // has gotten worse than before. Overwriting with the same status may
         // remove useful error messages.

         if (!bIsFatalError)	
         {
            // No this is the same error level.
            return;
         }
      }

      // Update the delivery status
	   pRecipient->SetDeliveryResult(bIsFatalError ? MessageRecipient::ResultFatalError : MessageRecipient::ResultNonFatalError);

      last_sent_data_.TrimLeft("\r\n");
      last_sent_data_.TrimRight("\r\n");

      String sData;

      if (bPreConnectError)
      {
         sData.Format(_T("   Error Type: SMTP\r\n")
            _T("   Connection to recipients server failed.\r\n")
            _T("   Error: %s\r\n"),
            String(sResponse).c_str());
      }
      else
      {
         sData.Format(_T("   Error Type: SMTP\r\n")
            _T("   Remote server (%s) issued an error.\r\n")
            _T("   hMailServer sent: %s\r\n")
            _T("   Remote server replied: %s\r\n"),
            String(GetIPAddressString()).c_str(), String(last_sent_data_).c_str(), String(sResponse).c_str());

      }

      pRecipient->SetDeliveryError(sData, enhancedStatusCode);

      // RFC 3464 2.3.6/2.3.7. Diagnostic-Code is defined as the diagnostic given
      // by the OTHER MTA, so it is only ever filled from what the remote really
      // replied - never from the sentences above, which are this server's own
      // account of the session. Same for the host: naming a Remote-MTA for a
      // connection that was never established would be an invented field.
      if (responseIsRemoteReply)
      {
         pRecipient->SetRemoteSmtpReply(sResponse);

         // A route or MX target configured as an IP literal has no hostname, and
         // the address this connection actually went to is then the honest name
         // for it - omitting Remote-MTA for a session that plainly happened would
         // be withholding, not caution.
         if (!remote_host_name_.IsEmpty())
            pRecipient->SetRemoteMta(remote_host_name_);
         else
            pRecipient->SetRemoteMta(GetIPAddressString());
      }
   }

   void 
   SMTPClientConnection::SetAuthInfo(const String &sUsername, const String &sPassword)
   {
      username_ = sUsername;
      password_ = sPassword;
      use_smtpauth_ = true;
   }

   void
   SMTPClientConnection::SetOAuthBearer(const String &token)
   {
      oauth_bearer_ = token;
      use_smtpauth_ = true;
   }
   
   void 
   SMTPClientConnection::SetState_(ConnectionState eState)
   {
      current_state_ = eState;
   }

   void
   SMTPClientConnection::ProtocolSendUsername_()
   {
      String sOut;
      StringParser::Base64Encode(username_, sOut);      
      EnqueueWrite_(sOut);
      
      SetState_(USERNAMESENT);
   }

   void
   SMTPClientConnection::ProtocolSendPassword_()
   {
      String sOut;
      StringParser::Base64Encode(password_, sOut);      
      
      SetState_(PASSWORDSENT);
      EnqueueWrite_(sOut);
   }

   void
   SMTPClientConnection::SendQUIT_()
   {
      // Disconnect from the remote SMTP server.
      session_ended_ = true;

      EnqueueWrite_("QUIT");
      SetState_(QUITSENT);
   }

   std::shared_ptr<MessageRecipient> 
   SMTPClientConnection::GetNextRecipient_()
   {
      while (1)
      {
         cur_recipient_ ++;

         if (cur_recipient_ < recipients_.size())
         {
            return recipients_[cur_recipient_];
         }
         else
         {
            std::shared_ptr<MessageRecipient> pEmpty;
            return pEmpty;
         }
      }
   }

   void
   SMTPClientConnection::StartSendFile_(const String &sFilename)
   {
      bool file_opened = false;

      try
      {
         // File::Open reports a missing file by returning false rather than by
         // throwing, so the return value must be checked.
         file_opened = current_file_.Open(sFilename, File::OTReadOnly);
      }
      catch (...)
      {
         file_opened = false;
      }

      if (!file_opened)
      {
         // File::Open reports every failure the same way, so a file held open by
         // an on-access virus scanner or a backup, and a process that has run out
         // of file handles, look identical to a file that is gone. Only the last
         // of those is hopeless - and SMTPDeliverer has already confirmed the file
         // existed when this attempt started, so it is also the least likely. Any
         // failure other than the file having disappeared is left retryable.
         bool missing = !FileUtilities::Exists(sFilename);

         FailDeliveryDueToUnreadableFile_(sFilename,
            missing ? _T("does not exist") : _T("could not be opened"), missing);
         return;
      }

      transmission_buffer_.Initialize(shared_from_this());

      std::shared_ptr<ByteBuffer> pBuf = current_file_.ReadChunk(GetBufferSize());

      if (pBuf->GetSize() == 0)
      {
         // An empty message file is more likely to be one still being written, or
         // one whose contents another process is holding back, than one that will
         // never be readable - so this is retried rather than bounced.
         FailDeliveryDueToUnreadableFile_(sFilename, _T("is empty"), false);
         return;
      }

      BYTE *pSendBuffer = (BYTE*) pBuf->GetBuffer();
      size_t iSendBufferSize = pBuf->GetSize();

      // Append the transmission buffer
      transmission_buffer_.Append(pSendBuffer, iSendBufferSize);
      
	  ReadAndSend_();
   }

   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Called when the message file cannot be transmitted during the DATA phase.
   // Without this, nothing at all would be sent after the DATA command and the
   // session would sit idle until the connection timed out - up to ten minutes -
   // before the message was re-queued and the whole attempt repeated.
   //
   // permanent says whether a later attempt could ever succeed. A file that has
   // gone is hopeless; one that could not be opened or read right now is not, and
   // must stay in the queue, because a fatal result here deletes the recipients
   // and bounces the message to the sender.
   //---------------------------------------------------------------------------()
   void
   SMTPClientConnection::FailDeliveryDueToUnreadableFile_(const String &sFilename, const String &sReason, bool permanent)
   {
      current_file_.Close();

      String sErrorMsg;
      sErrorMsg.Format(_T("Could not send file %s via socket since it %s."), sFilename.c_str(), sReason.c_str());

      ErrorManager::Instance()->ReportError(ErrorManager::High, 5019, "SMTPClientConnection::StartSendFile_", sErrorMsg);

      // The file name is deliberately kept out of this text. It ends up in the
      // delivery-failure notification, which for relayed or forwarded mail is sent
      // to a sender outside this server - and it would tell them the installation
      // path and the spool naming scheme. The error reported above is where the
      // path belongs.
      String sBounceMessage;
      sBounceMessage.Format(_T("   Error Type: SMTP\r\n")
         _T("   Error Description: The message file %s on the sending server, so the message could not be transmitted.\r\n\r\n"),
         sReason.c_str());

      for(std::shared_ptr<MessageRecipient> recipient : recipients_)
      {
         if (recipient->GetDeliveryResult() == MessageRecipient::ResultFatalError)
            continue;

         recipient->SetDeliveryResult(permanent ? MessageRecipient::ResultFatalError : MessageRecipient::ResultNonFatalError);

         // RFC 3463 X.3.0 "Other or undefined mail system status" - the mail
         // system at fault being this one. The class follows the same judgement
         // the delivery result does: a file that has gone is hopeless (5), a file
         // that could not be read right now is not (4).
         recipient->SetDeliveryError(sBounceMessage, permanent ? _T("5.3.0") : _T("4.3.0"));
      }

      // The remote server has answered 354 and is reading message data, so a QUIT
      // sent now would be taken as message content rather than a command and no
      // reply would ever come. RFC 5321 4.1.1.4: closing the connection before the
      // terminating "." is how a sender abandons a message in progress, and the
      // receiver discards what it has. session_ended_ keeps the resulting read
      // error from overwriting the delivery results set above.
      session_ended_ = true;
      EnqueueDisconnect();
   }

   void
   SMTPClientConnection::ReadAndSend_()
   {
      // Continue sending the file..
      int bufferSize = GetBufferSize();
      std::shared_ptr<ByteBuffer> pBuffer = current_file_.ReadChunk(bufferSize);

      while (pBuffer->GetSize() > 0)
      {
         transmission_buffer_.Append(pBuffer->GetBuffer(), pBuffer->GetSize());

         if (transmission_buffer_.Flush())
         {
            // Data was sent. We'll wait with sending more data until
            // the current data has been sent.
            return; 
         }

         pBuffer = current_file_.ReadChunk(bufferSize);
      }

      // We're done sending!
      current_file_.Close();

      // No more data to send. Make sure all buffered data is flushed.
      transmission_buffer_.Flush(true);

      // We're ready to receive the Message accepted-response.
      // No \r\n on end because EnqueueWrite adds
      EnqueueWrite_("\r\n.");

      EnqueueRead();

      // State change moved to AFTER crlf.crlf to help with race condition
      current_state_ = DATASENT;
   }

   void
   SMTPClientConnection::OnDataSent()
   {
      // Are we currently sending a file to the client?
      if (!current_file_.IsOpen())
         return;

      ReadAndSend_();
   }


   void 
   SMTPClientConnection::OnCouldNotConnect(const AnsiString &sErrorDescription)
   {
      // RFC 3463 X.4.1 "No answer from host": the connection attempt itself did
      // not succeed, so there is no remote MTA to name and nothing it said.
      UpdateAllRecipientsWithError_(0, sErrorDescription, true, _T("4.4.1"), false);

      LOG_DEBUG("SMTPDeliverer - Message " + StringParser::IntToString(delivery_message_->GetID()) + " - Connection failed: " + String(sErrorDescription) );
   }
}

