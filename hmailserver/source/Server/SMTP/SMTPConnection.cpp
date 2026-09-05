// Copyright (c) 2005 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later


#include "stdafx.h"

#include <Boost/Regex.hpp>

#include "../common/bo/MessageData.h"

#include "../common/Cache/CacheContainer.h"
#include "../common/Util/PasswordValidator.h"
#include "../common/Util/AccountLogon.h"
#include "../common/Util/AccountLockout.h"
#include "../Common/AntiSpam/QuarantineStore.h"
#include "../common/Util/OAuth2TokenValidator.h"
#include "../common/Util/ClientCertificateIdentity.h"
#include "../common/Util/Crypt.h"
#include "../common/Util/Hashing/ScramSha256.h"
#include "../common/persistence/PersistentMessage.h"
#include "../common/BO/Message.h"
#include "../common/BO/SecurityRange.h"
#include "../common/Mime/Mime.h"
#include "../common/util/MessageUtilities.h"
#include "../common/util/Utilities.h"
#include "../common/util/File.h"
#include "../common/Scripting/ClientInfo.h"
#include "../common/AntiSpam/SpamTestResult.h"
#include "../Common/UTil/Math.h"
#include "../Common/UTil/SignatureAdder.h"
#include "../common/BO/Routes.h"
#include "../common/BO/RouteAddresses.h"
#include "../common/BO/MessageRecipient.h"
#include "../common/BO/MessageRecipients.h"
#include "../Common/Util/ByteBuffer.h"
#include "../Common/Util/ServerStatus.h"
#include "../Common/Util/AWstats.h"
#include "../Common/Util/TransparentTransmissionBuffer.h"
#include "../Common/Application/ObjectCache.h"
#include "../Common/Application/DefaultDomain.h"
#include "../Common/Application/SessionManager.h"
#include "../Common/Cache/MessageCache.h"
#include "../Common/BO/DomainAliases.h"
#include "../Common/BO/Account.h"
#include "../Common/BO/Domain.h"

#include "../Common/BO/Collection.h"

#include "../common/Threading/AsynchronousTask.h"
#include "../common/Threading/WorkQueue.h"

#include "../Common/TCPIP/DNSResolver.h"
#include "../Common/TCPIP/ProxyProtocol.h"
#include "../common/persistence/PersistentSecurityRange.h"

#include "../Common/AntiSpam/AntiSpamConfiguration.h"
#include "../Common/AntiSpam/SpamProtection.h"
#include "../Common/AntiSpam/AuthenticationResults.h"
#include "../Common/AntiSpam/AuthenticationResultsWriter.h"

#include "../Common/Application/TimeoutCalculator.h"
#include "../Common/Scripting/ScriptServer.h"
#include "../Common/Scripting/ScriptObjectContainer.h"
#include "../Common/Scripting/Result.h"

#include "../Common/Application/IniFileSettings.h"

#include "../Common/Util/CrashSimulation.h"
#include "../Common/Util/DiskSpace.h"
#include "../Common/Util/RateLimiter.h"
#include "../Common/SQL/DatabaseUnavailableMarker.h"

#include "SMTPConnection.h"
#include "SMTPConfiguration.h"
#include "SMTPMessageHeaderCreator.h"
#include "DistributionListSender.h"

#include "../Common/TCPIP/CipherInfo.h"

using namespace std;

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   SMTPConnection::SMTPConnection(ConnectionSecurity connection_security,
      boost::asio::io_context& io_context, 
      boost::asio::ssl::context& context) :  
      TCPConnection(connection_security, io_context, context, std::shared_ptr<Event>(), ""),
      rejected_by_delayed_grey_listing_(false),
      current_state_(INITIAL),
      trace_headers_written_(true),
      message_submission_(false),
      requestedAuthenticationType_(AUTH_NONE),
      max_message_size_kb_(0),
      cur_no_of_rcptto_(0),
      cur_no_of_invalid_commands_(0),
      re_authenticate_user_(false),
      type_(SPNone),
      pending_disconnect_(false),
      isAuthenticated_(false),
      authentication_failure_count_(0),
      esmtp_session_(false),
      smtputf8_requested_(false),
      binarymime_requested_(false),
      bdat_active_(false),
      bdat_last_(false),
      bdat_discard_(false),
      bdat_chunk_size_(0),
      bdat_chunk_remaining_(0),
      ptr_lookup_completed_(false),
      authenticated_by_xclient_(false),
      start_tls_used_(false)
   {
      smtpconf_ = Configuration::Instance()->GetSMTPConfiguration();

      /* RFC 2821:    
         An SMTP server SHOULD have a timeout of at least 5 minutes while it
         is awaiting the next command from the sender. 

         Since the DATA command has a timeout on 10 minutes, we can just
         as well set the entire timeout to 10.

         Under very high load, the timeout will decrease below the values specified
         by the RFC. The reasoning is that it's better to disconnect slow clients
         than it is to disconnect everyone.
      */

      TimeoutCalculator calculator;
      SetTimeout(calculator.Calculate(IniFileSettings::Instance()->GetSMTPDMinTimeout(), IniFileSettings::Instance()->GetSMTPDMaxTimeout()));
   }

   const String CONST_UNKNOWN_USER = "Unknown user";

   SMTPConnection::~SMTPConnection()
   {
      try
      {
         ResetCurrentMessage_();

         if (GetConnectionState() != StatePendingConnect)
            SessionManager::Instance()->OnSessionEnded(STSMTP);
      }
      catch (...)
      {

      }
   }


   void
   SMTPConnection::OnConnected()
   {
      // Start resolving the client's PTR record on a worker thread now, so the
      // result is (normally) ready by the time the Received header is generated.
      // See PrefetchPtrRecord_ for why this must not run on the I/O thread.
      std::shared_ptr<AsynchronousTask<TCPConnection> > ptrTask =
         std::shared_ptr<AsynchronousTask<TCPConnection> >(new AsynchronousTask<TCPConnection>
            (std::bind(&SMTPConnection::PrefetchPtrRecord_, this), shared_from_this()));

      // Dedicated queue: a slow or unanswered reverse lookup must never occupy a
      // thread that message finalization (the final "250 OK") needs.
      std::shared_ptr<WorkQueue> lookupQueue = Application::Instance()->GetNameLookupWorkQueue();
      if (lookupQueue)
         lookupQueue->AddTask(ptrTask);

      if (GetConnectionSecurity() == CSNone ||
          GetConnectionSecurity() == CSSTARTTLSOptional ||
          GetConnectionSecurity() == CSSTARTTLSRequired)
      {
         SendBanner_();
      }

   }

   void
   SMTPConnection::PrefetchPtrRecord_()
   {
      // Runs on a worker thread: DnsQuery blocks without a timeout parameter, and an
      // address without a reverse zone (e.g. an internal relay on an RFC 1918 IP)
      // fails only after seconds of retries. On the I/O thread that stall wedged the
      // session between "354 OK" and the first spool write, until the sending MTA -
      // Postfix in the reported case - timed out and abandoned the message.
      String ptr_host = "Unknown";

      // Captured once: the effective client address can change mid-session
      // (XCLIENT), and the result below is stamped with the address it was
      // resolved for so a stale lookup can never be attributed to a new one.
      AnsiString lookup_address = GetIPAddressString();

      std::vector<String> results;
      DNSResolver dns_resolver;
      if (dns_resolver.GetPTRRecords(lookup_address, results) && results.size() > 0)
      {
         ptr_host = results[0];
      }
      else
      {
         LOG_DEBUG("Could not retrieve PTR record for IP (false)! " + lookup_address);
      }

      boost::lock_guard<boost::mutex> guard(ptr_result_mutex_);
      ptr_record_host_ = ptr_host;
      ptr_record_for_ip_ = lookup_address;
      ptr_lookup_completed_ = true;
   }

   void
   SMTPConnection::RestartPtrPrefetch_()
   {
      // Same worker-thread rules as the prefetch in OnConnected: the blocking
      // DnsQuery must never run on the network I/O thread.
      std::shared_ptr<AsynchronousTask<TCPConnection> > ptrTask =
         std::shared_ptr<AsynchronousTask<TCPConnection> >(new AsynchronousTask<TCPConnection>
            (std::bind(&SMTPConnection::PrefetchPtrRecord_, this), shared_from_this()));

      std::shared_ptr<WorkQueue> lookupQueue = Application::Instance()->GetNameLookupWorkQueue();
      if (lookupQueue)
         lookupQueue->AddTask(ptrTask);
   }

   String
   SMTPConnection::GetPtrRecordHost_()
   {
      boost::lock_guard<boost::mutex> guard(ptr_result_mutex_);

      // If the lookup is still in flight, fall back to "Unknown" rather than wait:
      // the Received header is being generated on the I/O thread.
      if (!ptr_lookup_completed_)
         return "Unknown";

      // A result for an address that is no longer the session's effective
      // client address (XCLIENT rewrote it while a lookup for the old address
      // was in flight) is stale; the header must not name the upstream's host
      // as the client's.
      if (ptr_record_for_ip_ != GetIPAddressString())
         return "Unknown";

      return ptr_record_host_;
   }

   void
   SMTPConnection::OnHandshakeCompleted()
   {
      if (GetConnectionSecurity() == CSSSL)
      {
         SendBanner_();
      }
      else if (GetConnectionSecurity() == CSSTARTTLSOptional ||
               GetConnectionSecurity() == CSSTARTTLSRequired)
      {
         /*
           Upon completion of the TLS handshake, the SMTP protocol is reset to
           the initial state (the state in SMTP after a server issues a 220
           service ready greeting). The server MUST discard any knowledge
           obtained from the client, such as the argument to the EHLO command,
           which was not obtained from the TLS negotiation itself.
         */

         helo_host_.Empty();
         start_tls_used_ = true;
         ResetLoginCredentials_();
         ResetCurrentMessage_();

         // Back to the just-greeted state, not merely a clean transaction: the
         // comment above says the greeting is discarded, but until now the
         // state machine stayed in HEADER, so MAIL FROM was accepted on the
         // encrypted session with helo_host_ empty - which skipped the HELO
         // host spam test and the OnHELO/OnEHLO script events for that session.
         // RFC 3207 section 4.2 requires the client to EHLO again (upstream #605).
         current_state_ = INITIAL;

         EnqueueRead();
      }
   }

   String
   SMTPConnection::GetBannerText_()
   {
      String sWelcome = Configuration::Instance()->GetSMTPConfiguration()->GetWelcomeMessage();

      String sESMTP = " ESMTP";

      String sData = "220 ";

      if (sWelcome.IsEmpty())
         sData += Utilities::ComputerName() + sESMTP;
      else if (!sWelcome.EndsWith(sESMTP))
         sData += sWelcome + sESMTP;
      else
         sData += sWelcome;

      return sData;
   }

   void
   SMTPConnection::SendBanner_()
   {
      EnqueueWrite_(GetBannerText_());

      EnqueueRead();
   }

   AnsiString 
   SMTPConnection::GetCommandSeparator() const
   {
      return "\r\n";
   }

   eSMTPCommandTypes
   SMTPConnection::GetCommandType_(const String &sFirstWord)
   {
      if (sFirstWord == _T("HELO"))
         return SMTP_COMMAND_HELO;
      else if (sFirstWord == _T("HELP"))
         return SMTP_COMMAND_HELP;
      else if (sFirstWord == _T("QUIT"))
         return SMTP_COMMAND_QUIT;
      else if (sFirstWord == _T("EHLO"))
         return SMTP_COMMAND_EHLO;
      else if (sFirstWord == _T("AUTH"))
         return SMTP_COMMAND_AUTH;
      else if (sFirstWord == _T("MAIL"))
         return SMTP_COMMAND_MAIL;
      else if (sFirstWord == _T("RCPT"))
         return SMTP_COMMAND_RCPT;
      else if (sFirstWord == _T("TURN"))
         return SMTP_COMMAND_TURN;
      else if (sFirstWord == _T("VRFY"))
         return SMTP_COMMAND_VRFY;
      else if (sFirstWord == _T("DATA"))
         return SMTP_COMMAND_DATA;
      else if (sFirstWord == _T("BDAT"))
         return SMTP_COMMAND_BDAT;
      else if (sFirstWord == _T("RSET"))
         return SMTP_COMMAND_RSET;
      else if (sFirstWord == _T("NOOP"))
         return SMTP_COMMAND_NOOP;
      else if (sFirstWord == _T("ETRN"))
         return SMTP_COMMAND_ETRN;
      else if (sFirstWord == _T("STARTTLS"))
         return SMTP_COMMAND_STARTTLS;
      else if (sFirstWord == _T("XCLIENT"))
         return SMTP_COMMAND_XCLIENT;

      return SMTP_COMMAND_UNKNOWN;
   }


   void 
   SMTPConnection::LogClientCommand_(const String &sClientData)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Logs one client command.
   //---------------------------------------------------------------------------()
   {
      if (Logger::Instance()->GetLogSMTP())
      {

         String sLogData = sClientData;

         String sRegex = "^(?>AUTH PLAIN )((?:[A-Z\\d+/]{4})*(?:[A-Z\\d+/]{3}=|[A-Z\\d+/]{2}==)?)$";
         boost::wregex expression(sRegex, boost::wregex::icase);
         boost::wsmatch matches;
         // AUTH PLAIN command and both user name and password in line. 
         if (current_state_ == HEADER && boost::regex_match(sLogData, matches, expression))
         {
            if (matches.size() > 0)
            {
               // Both user name and password in line.
               String sAuthentication;
               String sBase64Encoded = matches[1];
               StringParser::Base64Decode(sBase64Encoded, sAuthentication);

               // Extract the username from the decoded string.
               int iSecondTab = sAuthentication.Find(_T("\t"), 1);
               if (iSecondTab > 0)
               {
                  String username = sAuthentication.Mid(1, iSecondTab - 1);
                  //sLogData = "AUTH PLAIN " + username + " ***";
                  String usernameBase64Encoded;
                  StringParser::Base64Encode(username, usernameBase64Encoded);
                  sLogData = "AUTH PLAIN " + usernameBase64Encoded + " ***";
               }
               else
               {
                  sLogData = "AUTH PLAIN ***";
               }
            }
         }
         else if (current_state_ == SMTPUSERNAME && requestedAuthenticationType_ == AUTH_PLAIN)
         {
            // Both user name and password in line.
            String sAuthentication;
            StringParser::Base64Decode(sClientData, sAuthentication);

            // Extract the username from the decoded string.
            int iSecondTab = sAuthentication.Find(_T("\t"), 1);
            if (iSecondTab > 0)
            {
               String username = sAuthentication.Mid(1, iSecondTab - 1);
               //sLogData = username + " ***";
               String usernameBase64Encoded;
               StringParser::Base64Encode(username, usernameBase64Encoded);
               sLogData = usernameBase64Encoded + " ***";
            }
            else 
            {
               sLogData = "***";
            }
         }
         else if (current_state_ == SMTPUPASSWORD)
         {
            sLogData = "***";
         }         
         
         // Append
         sLogData = "RECEIVED: " + sLogData;

         LOG_SMTP(GetSessionID(), GetIPAddressString(), sLogData);      
      }
   }

   void
   SMTPConnection::ParseData(const AnsiString &sRequest)
   {
      InternalParseData(sRequest);

      if (pending_disconnect_ == false)
      {
         switch (current_state_)
         {
         case DATA:
            EnqueueRead("");
            break;
         case BDATDATA:
            // RFC 3030: the byte-counted chunk read has already been enqueued by
            // ProtocolBDAT_ (or finalization is in progress); do not start a line read.
            break;
         case STARTTLS:
            break;
         default:
            EnqueueRead();
            break;
         }
      }
   }

   void
   SMTPConnection::InternalParseData(const AnsiString &sRequest)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Parses a clients SMTP command in ASCII mode.
   //---------------------------------------------------------------------------()
   {
      LogClientCommand_(sRequest);

      // 510 octets is RFC 5321's command line, and it is enough for every command but
      // AUTH: a SASL initial response or continuation is as long as the mechanism
      // makes it (RFC 4954 section 4 says so in as many words), and an RS256 bearer
      // token from a real identity provider is over a kilobyte before XOAUTH2
      // base64-encodes it again - so the limit that protected the parser from an evil
      // user also refused every such token with "Line too long". The AUTH command and
      // the states that are waiting for a SASL response get 12288 octets, RFC 5034's
      // figure for the same line on POP3.
      const bool saslLine =
         current_state_ == SMTPUSERNAME || current_state_ == SMTPUPASSWORD ||
         current_state_ == SMTPSCRAMFIRST || current_state_ == SMTPSCRAMFINAL || current_state_ == SMTPSCRAMACK ||
         current_state_ == SMTPBEARERRESPONSE || current_state_ == SMTPEXTERNALRESPONSE ||
         (sRequest.GetLength() >= 5 && sRequest.Left(5).CompareNoCase("AUTH ") == 0);
      const int maxLength = saslLine ? 12288 : 510;

      if (sRequest.GetLength() > maxLength)
      {
         // This line is too long... is this an evil user?
         SendResponse_(500, _T("5.5.2"), _T("Line too long."));
         return;
      }

      int lFirstSpace = sRequest.Find(" ");
      
      String sFirstWord;
      if (lFirstSpace > -1)
         sFirstWord = sRequest.Mid(0,lFirstSpace);
      else
         sFirstWord = sRequest;

      sFirstWord.MakeUpper();

      // A PROXY protocol header arriving as an SMTP command means a peer that
      // was NOT authorised to assert a client address is trying to anyway - a
      // trusted proxy's header is consumed before the banner and never reaches
      // the command parser. Per the PROXY protocol specification an invalid
      // header must not be answered; the connection is dropped, because
      // "ignore and continue" would leave the real client's commands being
      // read as though the proxy had sent them. Only active while the feature
      // is enabled - switched off, the verb stays the unknown command it
      // always was, and nothing changes.
      if (sFirstWord == _T("PROXY") &&
          IniFileSettings::Instance()->GetSMTPProxyProtocolEnabled())
      {
         String message;
         message.Format(_T("SMTP - A PROXY protocol header was received from the untrusted peer %s. The connection has been closed. Session: %d"),
            String(GetTrueRemoteEndpointAddress().ToString()).c_str(), GetSessionID());
         LOG_APPLICATION(message);

         pending_disconnect_ = true;
         EnqueueDisconnect();
         return;
      }

      eSMTPCommandTypes eCommandType = GetCommandType_(sFirstWord);

      // The following commands are available regardless of of state.
      switch (eCommandType)
      {
         case SMTP_COMMAND_HELP: ProtocolHELP_(); return;
         case SMTP_COMMAND_EHLO: ProtocolEHLO_(sRequest); return;
         case SMTP_COMMAND_HELO: ProtocolHELO_(sRequest); return;
         case SMTP_COMMAND_QUIT: ProtocolQUIT_(); return;
         case SMTP_COMMAND_NOOP: ProtocolNOOP_(); return;
         case SMTP_COMMAND_RSET: ProtocolRSET_(); return;
      }

      switch (current_state_)
      {
         case INITIAL:
            {
               // XCLIENT is permitted before EHLO (Postfix allows it at any
               // point outside a mail transaction), and after a successful
               // XCLIENT the state returns here so the upstream re-EHLOs.
               if (eCommandType == SMTP_COMMAND_XCLIENT)
               {
                  ProtocolXCLIENT_(sRequest);
                  break;
               }

               requestedAuthenticationType_ = AUTH_NONE;
               SendErrorResponse_(503, "Bad sequence of commands");
               break;
            }
         case HEADER:
            {
               switch (eCommandType)
               {
                  case SMTP_COMMAND_STARTTLS: ProtocolSTARTTLS_(sRequest); break;
                  case SMTP_COMMAND_AUTH: ProtocolAUTH_(sRequest); break;
                  case SMTP_COMMAND_MAIL: ProtocolMAIL_(sRequest); break;
                  case SMTP_COMMAND_RCPT: ProtocolRCPT_(sRequest); break;
                  // TURN and VRFY are answered 502 in every case; the STARTTLS gate is
                  // applied to them anyway so that no command other than NOOP, EHLO,
                  // STARTTLS and QUIT is answered before TLS on a required port.
                  case SMTP_COMMAND_TURN:
                     if (CheckStartTlsRequired_())
                        SendResponse_(502, _T("5.5.1"), _T("TURN disallowed."));
                     break;
                  case SMTP_COMMAND_ETRN: ProtocolETRN_(sRequest); break;
                  case SMTP_COMMAND_VRFY:
                     if (CheckStartTlsRequired_())
                        SendResponse_(502, _T("5.5.1"), _T("VRFY disallowed."));
                     break;
                  case SMTP_COMMAND_DATA: ProtocolDATA_(); break;
                  case SMTP_COMMAND_BDAT: ProtocolBDAT_(sRequest); break;
                  case SMTP_COMMAND_XCLIENT: ProtocolXCLIENT_(sRequest); break;
                  default:
                     SendErrorResponse_(503, "Bad sequence of commands");
               }
               break;
            }
         case SMTPUSERNAME:
            {
               if (requestedAuthenticationType_ == AUTH_LOGIN)
               {
                  ProtocolUsername_(sRequest);
               }
               else
               {
                  AuthenticateUsingPLAIN_(sRequest);
               }

               break;
            }
         case SMTPUPASSWORD:
            {
               ProtocolPassword_(sRequest);
               break;
            }
         case SMTPSCRAMFIRST:
            {
               ProtocolScramClientFirst_(sRequest);
               break;
            }
         case SMTPSCRAMFINAL:
            {
               ProtocolScramClientFinal_(sRequest);
               break;
            }
         case SMTPSCRAMACK:
            {
               FinishScramAuth_();
               break;
            }
         case SMTPBEARERRESPONSE:
            {
               AuthenticateUsingBearer_(sRequest);
               break;
            }
         case SMTPEXTERNALRESPONSE:
            {
               AuthenticateUsingExternal_(sRequest);
               break;
            }
         default:
            {
               ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5500, "SMTPConnection::InternalParseData", 
                  Formatter::Format(_T("Received unexpected string data: {0}"), sRequest));
            }
      }
 
      return;
   }

   void 
   SMTPConnection::InitializeSpamProtectionType_(const String &sFromAddress)
   {
      // Check if spam protection is enabled for this IP address.
      if (!GetSecurityRange()->GetSpamProtection() ||
           SpamProtection::IsWhiteListed(sFromAddress, GetRemoteEndpointAddress()))
      {
         type_ = SPNone;
         return;
      }

      std::shared_ptr<IncomingRelays> incomingRelays = Configuration::Instance()->GetSMTPConfiguration()->GetIncomingRelays();
      // Check if we should do it before or after data transfer
      if (incomingRelays->IsIncomingRelay(GetRemoteEndpointAddress()))
         type_ = SPPostTransmission;
      else 
         type_ = SPPreTransmission;
   }

   void 
   SMTPConnection::ProtocolNOOP_()
   {
      SendResponse_(250, _T("2.0.0"), _T("OK"));
   }

   void
   SMTPConnection::ProtocolRSET_()
   {
      // 530 Must issue STARTTLS first
      // to every command other than NOOP, EHLO, STARTTLS, or QUIT.
      if (!CheckStartTlsRequired_())
         return;

      ResetCurrentMessage_();

      SendResponse_(250, _T("2.0.0"), _T("OK"));

      return;
   }

   void
   SMTPConnection::ProtocolMAIL_(const String &Request)
   {
      // 530 Must issue STARTTLS first
      // to every command other than NOOP, EHLO, STARTTLS, or QUIT.
      if (!CheckStartTlsRequired_())
         return;

      if (current_message_)
      {
         SendResponse_(503, _T("5.5.1"), _T("Issue a reset if you want to start over"));
         return;
      }

      // Free-space precondition, at the start of the transaction rather than at
      // DATA. Refusing here means no recipient is ever validated, no spool file is
      // opened and nothing is written for a message that is not going to be kept -
      // and MAIL is the one command RFC 5321 4.1.1.2 lists 452 among the replies
      // for, which DATA is not.
      //
      // 452 4.3.1 - "insufficient system storage" / "mail system full" - and not
      // any 5xx. A permanent rejection here would destroy mail that a five-minute
      // cleanup would have delivered; a sending server holds a 4xx for days. 452
      // also leaves the session open, so a client with other mail for a different
      // server is not disconnected.
      //
      // Inert unless MinimumFreeDiskSpaceMB says otherwise, and answered from a
      // cached reading rather than a syscall per message - see DiskSpace.
      if (!DiskSpace::DataDirectoryHasRoomForMail())
      {
         SendResponse_(452, _T("4.3.1"), _T("Insufficient system storage. Please try again later."));
         return;
      }

      // Start of a fresh transaction: clear any RFC 3030 (BDAT) state left over from a
      // previously completed message (the finalization path does not reset it).
      bdat_active_ = false;
      bdat_last_ = false;
      bdat_discard_ = false;
      bdat_chunk_size_ = 0;
      bdat_chunk_remaining_ = 0;
     
      if (Request.GetLength() < 10)
      {
         SendErrorResponse_(550, "Invalid syntax. Syntax should be MAIL FROM:<mailbox@domain>[crlf]");
         return;
      }

      if (!Request.StartsWith(_T("MAIL FROM:")))
      {
         SendErrorResponse_(550, "Invalid syntax. Syntax should be MAIL FROM:<mailbox@domain>[crlf]");
         return;
      }

      // Parse the contents of the MAIL FROM: command
      String sMailFromArguments = Request.Mid(10).Trim();
      
      String sFromAddress;
      String sParameters;
      if (!ParseAddressWithExtensions_(sMailFromArguments, sFromAddress, sParameters))
      {
         SendErrorResponse_(550, "Invalid syntax. Syntax should be MAIL FROM:<mailbox@domain>[crlf]");
         return;

      }

      sFromAddress = DefaultDomain::ApplyDefaultDomain(sFromAddress);

      // Detect the SMTPUTF8 parameter (RFC 6531) before validating the sender so an
      // internationalized (UTF-8) address is accepted.
      smtputf8_requested_ = false;
      binarymime_requested_ = false;
      for (const String &peekParam : StringParser::SplitString(sParameters, " "))
      {
         if (peekParam.CompareNoCase(_T("SMTPUTF8")) == 0)
            smtputf8_requested_ = true;
      }

      if (!CheckIfValidSenderAddress(sFromAddress))
         return;

      // Per-IP submission rate shaping (anti-abuse). A configured [Settings]
      // MaxSubmissionsPerIPPerMinute caps how many message transactions a single
      // source IP may start per minute; 0 = unlimited (default, no-op).
      int maxSubmissionsPerIp = IniFileSettings::Instance()->GetMaxSubmissionsPerIPPerMinute();
      if (maxSubmissionsPerIp > 0)
      {
         String remoteIp = String(GetIPAddressString());
         if (!RateLimiter::Instance()->TryConsume(_T("smtp-submit:") + remoteIp, maxSubmissionsPerIp))
         {
            SendErrorResponse_(421, "Too many messages from your IP address. Please slow down and try again later.");
            return;
         }
      }

      // Parse the extensions 
      std::vector<String> vecParams = StringParser::SplitString(sParameters, " ");
      std::vector<String>::iterator iterParam = vecParams.begin();

      String sAuthParam;
      unsigned __int64 iEstimatedMessageSize = 0;
      while (iterParam != vecParams.end())
      {
         String parameter = (*iterParam);
         // The keyword tests below are deliberately against "SIZE=" and "AUTH=", not
         // the first four characters. Matching on Left(4) meant every parameter whose
         // name merely STARTS with one of them was swallowed: "SIZEX=1" was read as a
         // size declaration of 1 (Mid(5) skips the X), a bare "SIZE" with no value was
         // accepted as "no size", and both went through as if understood. An extension
         // the server does not implement has to be refused (RFC 5321 section 4.1.1.11),
         // and every other parameter here - BODY=, SMTPUTF8, RET=, ENVID= - was already
         // matched exactly, so this was the odd one out rather than a deliberate
         // tolerance.
         if (parameter.Left(5).CompareNoCase(_T("SIZE=")) == 0)
         {
            // RFC 1870: a decimal octet count. It used to be read with _ttoi, which
            // returns 0 for anything non-numeric - so "SIZE=abc" was quietly treated as
            // "no size declared" and the one thing the extension exists for, refusing
            // an oversized transaction before its octets are sent, was skipped. _ttoi
            // also saturates at INT_MAX, so a declaration above 2 GB was compared
            // against the wrong number.
            if (!ParseSizeParameter_(parameter.Mid(5), iEstimatedMessageSize))
            {
               SendErrorResponse_(501, "Syntax error in SIZE parameter (must be a decimal octet count).");
               return;
            }
         }
         else if (parameter.Left(5).CompareNoCase(_T("AUTH=")) == 0)
            sAuthParam = parameter.Mid(5);
         else if (parameter.CompareNoCase(_T("BODY=7BIT")) == 0 ||
                  parameter.CompareNoCase(_T("BODY=8BITMIME")) == 0)
         {
            // 8BITMIME (RFC 6152): the transmission channel is 8-bit clean,
            // so both body types are accepted as-is. Neither declaration is
            // tracked beyond this transaction - deliberately, and BINARYMIME
            // below must not copy that precedent: an 8-bit body survives the
            // line-oriented DATA path unharmed, a binary one does not.
         }
         else if (parameter.CompareNoCase(_T("BODY=BINARYMIME")) == 0)
         {
            // BINARYMIME (RFC 3030): the message content is raw binary - bare CR,
            // bare LF and NUL octets are all legal in it. It therefore MUST be
            // transmitted with BDAT (byte-counted, byte-transparent) rather than
            // DATA; ProtocolDATA_ answers 503 for this transaction. The flag also
            // drives the two relay refusals (RCPT below, and the delivery client),
            // because a binary message must never travel down a line-oriented
            // DATA transmission.
            binarymime_requested_ = true;
         }
         else if (parameter.CompareNoCase(_T("SMTPUTF8")) == 0)
         {
            // SMTPUTF8 (RFC 6531): already handled above; accept the parameter so
            // it is not reported as an unsupported extension.
         }
         else if (parameter.Left(4).CompareNoCase(_T("RET=")) == 0)
         {
            // DSN (RFC 3461): controls whether the full message or only its
            // headers are returned in a delivery-status notification.
            String retValue = parameter.Mid(4);
            if (retValue.CompareNoCase(_T("FULL")) != 0 && retValue.CompareNoCase(_T("HDRS")) != 0)
            {
               SendErrorResponse_(501, "Syntax error in RET parameter (must be FULL or HDRS).");
               return;
            }
            dsn_ret_ = retValue;
         }
         else if (parameter.Left(6).CompareNoCase(_T("ENVID=")) == 0)
         {
            // DSN (RFC 3461): an envelope identifier echoed back in any DSN.
            String envId = parameter.Mid(6);
            if (envId.IsEmpty() || envId.GetLength() > 100 || !IsValidXtext_(envId))
            {
               SendErrorResponse_(501, "Syntax error in ENVID parameter.");
               return;
            }
            dsn_envid_ = envId;
         }
         else
         {
            ReportUnsupportedEsmtpExtension_(parameter);
            return;
         }

         iterParam++;
      }

      // Initialize spam protection now when we know the sender address.
      InitializeSpamProtectionType_(sFromAddress);

      // Apply domain name aliases to this domain name.
      std::shared_ptr<DomainAliases> pDA = ObjectCache::Instance()->GetDomainAliases();
      const String sAccountAddress = pDA->ApplyAliasesOnAddress(sFromAddress);

      // Pre-transmission spam protection.
      if (type_ == SPPreTransmission)
      {
         if (IniFileSettings::Instance()->GetDNSBLChecksAfterMailFrom())
         {
            // The message is not arriving from a white listed host or a host
            // which is configured to be a forwarding relay. This means that
            // we can start spam protection now.

            if (!DoSpamProtection_(SPPreTransmission, sFromAddress, helo_host_, GetRemoteEndpointAddress()))
               return;
         }
      }

      sender_domain_ = CacheContainer::Instance()->GetDomain(StringParser::ExtractDomain(sAccountAddress));
      sender_account_ = CacheContainer::Instance()->GetAccount(sAccountAddress);

      // Check the max size
      max_message_size_kb_ = GetMaxMessageSize_(sender_domain_);

      // Check if estimated message size exceedes our
      // maximum message size (according to RFC1653)
      if (max_message_size_kb_ > 0 &&
          iEstimatedMessageSize / 1024 > (unsigned __int64) max_message_size_kb_)
      {
         // Message too big. Reject it. 5.3.4 = message too big for system (RFC 3463).
         String sMessage;
         sMessage.Format(_T("Message size exceeds fixed maximum message size. Size: %I64u KB, Max size: %Iu KB"),
               iEstimatedMessageSize / 1024, max_message_size_kb_);
         SendResponse_(552, _T("5.3.4"), sMessage);
         return ;
      }
      
      if (re_authenticate_user_ && !ReAuthenticateUser())
         return;

      // Next time we do a mail from, we should re-authenticate the login credentials
      re_authenticate_user_ = true;

      // SECURITY: per-account outbound sending ceiling. Placed here, after the
      // credentials have been re-validated and before the transaction exists, so a
      // session whose password has just been revoked never consumes budget.
      if (!CheckAccountSendingQuotaAtMailFrom_())
         return;

         current_message_ = std::shared_ptr<Message> (new Message);
      current_message_->SetFromAddress(sFromAddress);
      current_message_->SetState(Message::Delivering);

      // RFC 3030: carry the BODY=BINARYMIME declaration on the message object so
      // the delivery client can refuse to put this content down a line-oriented
      // DATA transmission (see Message::SetBinaryMime for why this mark is
      // in-memory only for now).
      if (binarymime_requested_)
         current_message_->SetBinaryMime(true);
      
      // 2.1.0 = originator (sender) address is valid (RFC 3463).
      SendResponse_(250, _T("2.1.0"), _T("OK")); 
   }

   bool 
   SMTPConnection::ReAuthenticateUser()
   {
      if (!isAuthenticated_)
      {
         // Nothing to re-authenticate
         return true;
      }

      if (authenticated_by_xclient_)
      {
         // The authenticated state was asserted by a trusted upstream via
         // XCLIENT LOGIN: the client authenticated with the upstream, and no
         // password ever crossed this connection, so there is nothing to
         // re-validate here.
         return true;
      }

      std::shared_ptr<const Account> pAccount = PasswordValidator::ValidatePassword(username_, password_);
      
      if (pAccount)
         return true;
         
      // Reset login credentials
      ResetLoginCredentials_();      

      SendErrorResponse_(550, "Login credentials no longer valid. Please re-authenticate.");                      
      
      return false;
   }

   void
   SMTPConnection::CountMessageAgainstLocalDomains_()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Counts one accepted message against the local domains it belongs to: the
   // sender's domain if this server hosts it, and every local recipient domain.
   //
   // A message from one hosted domain to another counts once on each side, which is
   // right - it is one message sent and one message received, and an operator
   // reporting on either domain would expect to see it.
   //---------------------------------------------------------------------------()
   {
      if (!IniFileSettings::Instance()->GetMetricsPerDomainEnabled())
         return;

      if (!current_message_)
         return;

      String senderDomain = StringParser::ExtractDomain(current_message_->GetFromAddress());
      senderDomain.MakeLower();

      // Only ever a domain this server hosts. That is what bounds the label set:
      // the sender domain of an arbitrary inbound message is attacker-chosen and
      // unbounded, and labelling it would let anyone create time series here.
      if (!senderDomain.IsEmpty() && CacheContainer::Instance()->GetDomain(senderDomain))
         ServerStatus::Instance()->OnDomainMessageSent(senderDomain);

      std::set<String> countedRecipientDomains;

      for (std::shared_ptr<MessageRecipient> recipient : current_message_->GetRecipients()->GetVector())
      {
         String recipientDomain = StringParser::ExtractDomain(recipient->GetAddress());
         recipientDomain.MakeLower();

         if (recipientDomain.IsEmpty())
            continue;

         // Once per domain, not once per recipient: a message to four people at one
         // domain is one message that domain received, and counting it four times
         // would make the number disagree with everything else the operator can see.
         if (countedRecipientDomains.find(recipientDomain) != countedRecipientDomains.end())
            continue;

         if (!CacheContainer::Instance()->GetDomain(recipientDomain))
            continue;

         countedRecipientDomains.insert(recipientDomain);

         ServerStatus::Instance()->OnDomainMessageReceived(recipientDomain);
      }
   }

   bool
   SMTPConnection::CheckAccountSendingQuotaAtMailFrom_()
   {
      // A fresh transaction: forget whatever the previous one was limited by.
      quota_account_ = _T("");
      quota_limits_ = AccountSendingLimits();

      // Only an authenticated sender is subject to the ceiling. Inbound mail from
      // other servers is not, and must not be: this is a control on what an account
      // of ours can send, not on what we accept.
      if (!isAuthenticated_ || username_.IsEmpty())
         return true;

      // Key on the authenticated account, not on MAIL FROM. A compromised account
      // can put any address in MAIL FROM, but it cannot change whose credentials it
      // presented, so that is the only identity worth counting against.
      quota_account_ = DefaultDomain::ApplyDefaultDomain(username_);
      quota_limits_ = RateLimiter::Instance()->GetAccountLimits(quota_account_);

      if (!quota_limits_.IsEnabled())
         return true;

      AccountQuotaResult quotaResult = RateLimiter::Instance()->TryConsumeAccountMessage(quota_account_, quota_limits_);
      if (quotaResult == AccountQuotaResult::QuotaAllowed)
         return true;

      RefuseForAccountSendingQuota_(quotaResult);
      return false;
   }

   bool
   SMTPConnection::CheckAccountSendingQuotaAtRcptTo_()
   {
      // quota_account_ is only non-empty for an authenticated session that reached
      // MAIL FROM, and the limits were resolved there - do not re-read them per
      // recipient, a message may carry thousands.
      if (quota_account_.IsEmpty() || !quota_limits_.IsEnabled())
         return true;

      AccountQuotaResult quotaResult = RateLimiter::Instance()->TryConsumeAccountRecipients(quota_account_, quota_limits_, 1);
      if (quotaResult == AccountQuotaResult::QuotaAllowed)
         return true;

      RefuseForAccountSendingQuota_(quotaResult);
      return false;
   }

   void
   SMTPConnection::RefuseForAccountSendingQuota_(AccountQuotaResult quotaResult)
   {
      String enhancedCode;
      String text;
      String reason;

      switch (quotaResult)
      {
      case AccountQuotaResult::QuotaAllowed:
         // Not a refusal; nothing to answer.
         return;
      case AccountQuotaResult::QuotaMessageLimitReached:
         // 4.7.0 = "other or undefined security status" (RFC 3463). A sending
         // ceiling is a policy control, and there is no more specific 4.7.x code
         // for one.
         enhancedCode = _T("4.7.0");
         text = _T("Sending limit for this account has been reached. Please try again later.");
         reason = _T("message limit");
         break;
      case AccountQuotaResult::QuotaRecipientLimitReached:
         // 4.5.3 = "too many recipients" (RFC 3463), which is exactly this case.
         enhancedCode = _T("4.5.3");
         text = _T("Recipient sending limit for this account has been reached. Please try again later.");
         reason = _T("recipient limit");
         break;
      }

      // 452, deliberately not 5xx: a legitimate user who reaches a daily ceiling
      // should have their client retry once the period rolls over, not have the
      // message bounced back at them. 452 also leaves the session open, unlike the
      // 421 used for the per-IP limit.
      SendResponse_(452, enhancedCode, text);

      int messages = 0;
      int recipients = 0;
      RateLimiter::Instance()->GetAccountUsage(quota_account_, messages, recipients);

      String logMessage;
      logMessage.Format(_T("hMailServer SendingLimit refused submission (Account: %s, IP: %s, Reason: per-account %s reached, ")
                        _T("Usage: %d message(s) and %d recipient(s) this period, Limits: %d/%d)"),
         quota_account_.c_str(),
         String(GetIPAddressString()).c_str(),
         reason.c_str(),
         messages,
         recipients,
         quota_limits_.max_messages,
         quota_limits_.max_recipients);

      // LOG_APPLICATION, not ErrorManager: reaching a ceiling an administrator
      // configured is an event, not a server fault, and a legitimate user hitting
      // their daily cap must not fill the ERROR log.
      LOG_APPLICATION(logMessage);
   }

   bool
   SMTPConnection::CheckIfValidSenderAddress(const String &sFromAddress)
   {
      if (sFromAddress.IsEmpty())
      {
         // The user is trying to send an e-mail without
         // specifying an email address. Should we allow this?
         if (!smtpconf_->GetAllowMailFromNull())
         {
            // Nope, we should'nt... We send the below text even
            // though RFC 822 tells us not to...
            SendErrorResponse_(550, "Sender address must be specified.");             
            return false;
         }
      }
      else
      {
         if (!StringParser::IsValidEmailAddress(sFromAddress, smtputf8_requested_))
         {
            // The address is not valid...
            SendErrorResponse_(550, "The address is not valid.");
            return false;
         }
      }

      return true;
   }

   void
   SMTPConnection::ProtocolRCPT_(const String &Request)
   {
      cur_no_of_rcptto_ ++;

      // Before any reply below, so that every answer past the count is held -
      // refusals included, since a refused recipient is what a dictionary attack
      // mostly gets.
      TarpitRecipient_();

      // 530 Must issue STARTTLS first
      // to every command other than NOOP, EHLO, STARTTLS, or QUIT.
      if (!CheckStartTlsRequired_())
         return;

      if (!current_message_) 
      {
         SendResponse_(503, _T("5.5.1"), _T("Must have sender first."));
         return;
      }

      if (!Request.StartsWith(_T("RCPT TO:")))
      {
         SendErrorResponse_(550, "Invalid syntax. Syntax should be RCPT TO:<mailbox@domain>[crlf]");
         return;
      }

      // Parse the contents of the RCPT TO: command
      String sRcptToArguments = Request.Mid(8).Trim();

      String sRecipientAddress;
      String sParameters;
      
      if (!ParseAddressWithExtensions_(sRcptToArguments, sRecipientAddress, sParameters))
      {
         SendErrorResponse_(550, "Invalid syntax. Syntax should be RCPT TO:<mailbox@domain>[crlf]");
         return;
      }

      std::vector<String> vecParams = StringParser::SplitString(sParameters, " ");
      auto iterParam = vecParams.begin();

      // Parse the extensions. DSN (RFC 3461) NOTIFY/ORCPT are accepted here; any
      // other parameter is reported as an unsupported ESMTP extension.
      int recipientNotify = MessageRecipient::DSNNotifyDefault;
      while (iterParam != vecParams.end())
      {
         String parameter = *iterParam;

         if (parameter.Left(7).CompareNoCase(_T("NOTIFY=")) == 0)
         {
            if (!ParseDsnNotify_(parameter.Mid(7), recipientNotify))
            {
               SendErrorResponse_(501, "Syntax error in NOTIFY parameter.");
               return;
            }
         }
         else if (parameter.Left(6).CompareNoCase(_T("ORCPT=")) == 0)
         {
            // ORCPT carries the original recipient address; validate its syntax
            // (addr-type ";" xtext) but it is not otherwise retained.
            if (!IsValidOrcpt_(parameter.Mid(6)))
            {
               SendErrorResponse_(501, "Syntax error in ORCPT parameter.");
               return;
            }
         }
         else
         {
            ReportUnsupportedEsmtpExtension_(parameter);
            return;
         }

         iterParam++;
      }

      sRecipientAddress = DefaultDomain::ApplyDefaultDomain(sRecipientAddress);

      if (!StringParser::IsValidEmailAddress(sRecipientAddress, smtputf8_requested_))
      {
         // The address is not valid...
         SendErrorResponse_(550, "A valid address is required.");
         return;
      }

      if (current_message_->GetRecipients()->GetCount() >= MaxNumberOfRecipients)
      {
         // The user has added too many recipients for this message. Let's not try
         // to deliver it.
         SendErrorResponse_(550, "Too many recipients.");
         return;
      }

      // SECURITY: the per-account recipient ceiling is applied here, as recipients
      // accumulate, and not only at MAIL FROM - otherwise one message addressed to
      // thousands of recipients would cost a single message from the budget. Checked
      // before the delivery-possibility lookups so a compromised account spraying
      // addresses is stopped before it costs us database work.
      if (!CheckAccountSendingQuotaAtRcptTo_())
         return;

      String sErrMsg = "";
      bool localDelivery = false;
      
      RecipientParser::DeliveryPossibility dp = RecipientParser::DP_Possible;

      {
         // This decides whether the address is deliverable at all, using the same
         // domain and account lookups as below, so it needs the same protection: a
         // database that cannot answer must not be reported as a permanently
         // undeliverable address.
         DatabaseUnavailableMarker::Scope databaseScope;

         dp = recipientParser_.CheckDeliveryPossibility(isAuthenticated_, current_message_->GetFromAddress(), sRecipientAddress, sErrMsg, localDelivery, 0, true);

         if (dp != RecipientParser::DP_Possible && DatabaseUnavailableMarker::IsMarked())
         {
            AWStats::LogDeliveryFailure(GetIPAddressString(), current_message_->GetFromAddress(), sRecipientAddress, 451, current_message_->GetID());

            // The enhanced code belongs in its own field, not inside the text: passing
            // it as text made an ESMTP session receive "451 4.3.0 4.3.2 Unable...".
            SendResponse_(451, _T("4.3.2"), _T("Unable to verify the recipient at the moment. Please retry later."));
            return;
         }
      }

      // A full mailbox is a TEMPORARY refusal, and the distinction is the whole
      // point of making it here. 452 with the enhanced code 4.2.2 (RFC 3463) tells
      // the connecting server to hold the message and try again, so a legitimate
      // sender's mail waits in ITS queue until the mailbox is emptied, and is
      // eventually reported to the real sender by the machine that actually knows
      // who that is. A 550 here would destroy the message on the spot; accepting it
      // and bouncing, which is what happened before, sends a report to whatever the
      // envelope sender claimed to be.
      if (dp == RecipientParser::DP_MailboxFull)
      {
         AWStats::LogDeliveryFailure(GetIPAddressString(), current_message_->GetFromAddress(), sRecipientAddress, 452, current_message_->GetID());

         SendResponse_(452, _T("4.2.2"), sErrMsg);
         return;
      }

      if (dp != RecipientParser::DP_Possible)
      {
         AWStats::LogDeliveryFailure(GetIPAddressString(), current_message_->GetFromAddress(), sRecipientAddress, 550, current_message_->GetID());

         SendErrorResponse_(550, sErrMsg);
         return;
      }

      bool localSender = GetIsLocalSender_();

      int iRelayOption = 0;
      if (localSender && localDelivery)
         iRelayOption = SecurityRange::IPRANGE_RELAY_LOCAL_TO_LOCAL;
      else if (localSender && !localDelivery)
         iRelayOption = SecurityRange::IPRANGE_RELAY_LOCAL_TO_REMOTE;
      else if (!localSender && localDelivery)
         iRelayOption = SecurityRange::IPRANGE_RELAY_REMOTE_TO_LOCAL;
      else if (!localSender && !localDelivery)
         iRelayOption = SecurityRange::IPRANGE_RELAY_REMOTE_TO_REMOTE;

      bool bAllowRelay = GetSecurityRange()->GetAllowOption(iRelayOption);
         
      if (bAllowRelay == false)
      {
         // User is not allowed to send this email.
         SendErrorResponse_(550, "Delivery is not allowed to this address.");
         AWStats::LogDeliveryFailure(GetIPAddressString(), current_message_->GetFromAddress(), sRecipientAddress, 550, current_message_->GetID());
         return;
      }

      bool authenticationRequired = true;
      if (localSender && localDelivery)
         authenticationRequired = GetSecurityRange()->GetRequireSMTPAuthLocalToLocal();
      else if (localSender && !localDelivery)
         authenticationRequired = GetSecurityRange()->GetRequireSMTPAuthLocalToExternal();
      else if (!localSender && localDelivery)
         authenticationRequired = GetSecurityRange()->GetRequireSMTPAuthExternalToLocal();
      else if (!localSender && !localDelivery)
         authenticationRequired = GetSecurityRange()->GetRequireSMTPAuthExternalToExternal();

      // If the user is local but not authenticated, maybe we should do SMTP authentication.
      if (authenticationRequired && !isAuthenticated_)
      {
         // Authentication is required, but the user hasn't authenticated.
         SendErrorResponse_(530, "SMTP authentication is required.");
         AWStats::LogDeliveryFailure(GetIPAddressString(), current_message_->GetFromAddress(), sRecipientAddress, 530, current_message_->GetID());
         return;
      }

      // This server is the submission server (RFC 6409) for the message if the client
      // has authenticated, or if it sends as one of our own domains from a range that
      // does not require authentication to do so - by default, only the server itself.
      // For everyone else it is a relay, and a relay adds trace fields only. Decided
      // here because this is where that determination is already made. Upstream #552.
      if (isAuthenticated_ || (localSender && !authenticationRequired))
         message_submission_ = true;

      // RFC 3030 section 4: a BINARYMIME message must not be sent to a server that
      // has not advertised BINARYMIME, and this server's delivery client cannot
      // send one to any server - it transmits via DATA only, which cannot carry
      // bare CR, bare LF or NUL octets, and down-conversion (re-encoding binary
      // parts to base64) is not implemented. So a recipient that would require
      // onward relay is refused HERE, synchronously, with the RFC's own permanent
      // code: 554 5.6.3, conversion required but not supported. Refusing at RCPT
      // is strictly kinder than the alternative the RFC also allows
      // (accept-then-bounce): the sending client learns before transferring a
      // single content octet, and no DSN backscatter is generated. Local
      // recipients in the same transaction are unaffected and deliver normally.
      //
      // Known gap, stated plainly: an address treated as local HERE (so accepted)
      // can still route outward later - a distribution list with external members,
      // a local alias resolving to an external address, a route whose recipients
      // are treated as local, an account forward, a rule or mirror. Those are
      // refused at delivery time by SMTPClientConnection with the same 554 5.6.3
      // (as a DSN), for as long as the message's binary mark survives - see
      // Message::SetBinaryMime for the persistence limitation.
      if (binarymime_requested_ && !localDelivery)
      {
         AWStats::LogDeliveryFailure(GetIPAddressString(), current_message_->GetFromAddress(), sRecipientAddress, 554, current_message_->GetID());

         SendResponse_(554, _T("5.6.3"),
            _T("Conversion required but not supported. This server accepts BODY=BINARYMIME for local delivery only; it cannot relay a binary message onward. Re-encode the content (for example as base64) and send it without BODY=BINARYMIME."));
         return;
      }

      // Pre-transmission spam protection.
      if (type_ == SPPreTransmission)
      {
         if (!IniFileSettings::Instance()->GetDNSBLChecksAfterMailFrom())
         {
            // The message is not arriving from a white listed host or a host
            // which is configured to be a forwarding relay. This means that
            // we can start spam protection now.

            if (!DoSpamProtection_(SPPreTransmission, current_message_->GetFromAddress(), helo_host_, GetRemoteEndpointAddress()))
            {
               AWStats::LogDeliveryFailure(GetIPAddressString(), current_message_->GetFromAddress(), sRecipientAddress, 550, current_message_->GetID());
               return;
            }
         }
      }

      if (GetDoSpamProtection_())
      {
         std::shared_ptr<DomainAliases> pDA = ObjectCache::Instance()->GetDomainAliases();
         const String sToAddress = pDA->ApplyAliasesOnAddress(sRecipientAddress);

         if (!SpamProtection::Instance()->PerformGreyListing(current_message_, spam_test_results_, sToAddress, GetRemoteEndpointAddress()))
         {
            if (current_message_->GetFromAddress().IsEmpty())
            {
               // We got a message with an empty sender address.
               // When this happens, we should delay the greylist-reject 
               // until after the DATA command. The reason for this is
               // that this may be a SMTP callback from another server
               // that is veriying that the recipient exists, using the
               // RCPT TO command. And we don't want to delay that.
               rejected_by_delayed_grey_listing_ = true;
            }
            else
            {
               // The sender is greylisted. We don't log to awstats here,
               // since we tell the client to try again later.
               SendErrorResponse_(451, "Please try again later.");
               return;
            }
         }
      }

      // OK, the recipient is acceptable.
      std::shared_ptr<MessageRecipients> pRecipients = current_message_->GetRecipients();
      size_t recipientCountBefore = pRecipients->GetVector().size();
      bool recipientOK = false;

      {
         // The lookup below reports an address it could not resolve and an address
         // that does not exist identically. Telling them apart matters: 550 is
         // permanent, so answering it because the database was locked by a backup
         // would bounce mail for a valid mailbox and the sender would never retry.
         DatabaseUnavailableMarker::Scope databaseScope;

         recipientParser_.CreateMessageRecipientList(sRecipientAddress, pRecipients, recipientOK);

         if (!recipientOK && DatabaseUnavailableMarker::IsMarked())
         {
            SendResponse_(451, _T("4.3.2"), _T("Unable to verify the recipient at the moment. Please retry later."));
            return;
         }
      }

      if (!recipientOK)
      {
         SendErrorResponse_(550, CONST_UNKNOWN_USER);
         return;
      }

      // Apply the DSN NOTIFY value (RFC 3461) to the recipient(s) just added for
      // this RCPT TO command.
      if (recipientNotify != MessageRecipient::DSNNotifyDefault)
      {
         std::vector<std::shared_ptr<MessageRecipient> > &vecAllRecipients = pRecipients->GetVector();
         for (size_t i = recipientCountBefore; i < vecAllRecipients.size(); i++)
            vecAllRecipients[i]->SetDSNNotify(recipientNotify);
      }
   
      // 2.1.5 = destination (recipient) address is valid (RFC 3463).
      SendResponse_(250, _T("2.1.5"), _T("OK"));
   }

   bool
   SMTPConnection::DoSpamProtection_(SpamProtectionType spType, const String &sFromAddress, const String &hostName, const IPAddress & lIPAddress)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Does IP based spam protection. Returns true if we should
   // continue delivery, false otherwise.   
   //---------------------------------------------------------------------------()
   {
      if (!GetDoSpamProtection_())
         return true;

      // Created on first use, and only when a header is actually going to be written.
      // With both settings off this stays null for the whole session and every
      // recording site skips itself.
      if (!authentication_results_ &&
          (IniFileSettings::Instance()->GetAuthenticationResultsEnabled() ||
           IniFileSettings::Instance()->GetReceivedSpfHeaderEnabled()))
      {
         authentication_results_ = std::shared_ptr<AuthenticationResults>(new AuthenticationResults);
      }

      if (spType == SPPreTransmission)
      {
         std::set<std::shared_ptr<SpamTestResult> > setResult =
            SpamProtection::Instance()->RunPreTransmissionTests(sFromAddress, lIPAddress, GetRemoteEndpointAddress(), hostName, authentication_results_);

         spam_test_results_.insert(setResult.begin(), setResult.end());
      }
      else if (spType == SPPostTransmission)
      {
         std::set<std::shared_ptr<SpamTestResult> > setResult =
            SpamProtection::Instance()->RunPostTransmissionTests(sFromAddress, lIPAddress, GetRemoteEndpointAddress(), current_message_, authentication_results_);

         spam_test_results_.insert(setResult.begin(), setResult.end());

      }

      int iTotalSpamScore = SpamProtection::CalculateTotalSpamScore(spam_test_results_);
      int iSpamDeleteThreshold = Configuration::Instance()->GetAntiSpamConfiguration().GetSpamDeleteThreshold();
      int iSpamMarkThreshold = Configuration::Instance()->GetAntiSpamConfiguration().GetSpamMarkThreshold();

      if (iSpamDeleteThreshold > 0 && iTotalSpamScore >= iSpamDeleteThreshold)
      {
         // Increase the spam-counter
         ServerStatus::Instance()->OnSpamMessageDetected();

         // Generate a text string to send to the client.
         String messageText = GetSpamTestResultMessage_(spam_test_results_);

         // Quarantine, when it is switched on and there is actually a message to
         // hold. A pre-transmission verdict is reached before DATA - there is no
         // message yet - so those stay refusals, which is also strictly cheaper for
         // both ends since the body never crosses the wire.
         //
         // The reply changes with the outcome, and that is the whole point rather
         // than a detail: a refused message is answered 550/554 and the sender knows,
         // while a quarantined one is answered 250 and the sender believes it was
         // delivered. Accepting is what makes a false positive recoverable without
         // backscatter, and it is also what makes the review queue the only place the
         // message now exists - which is why a quarantine that fails to store falls
         // through to refusing rather than accepting. Silently accepting mail that
         // was not stored would turn a spam refusal into silent deletion.
         if (spType == SPPostTransmission &&
             QuarantineStore::GetEnabled() &&
             QuarantineStore::Quarantine(current_message_, messageText, iTotalSpamScore))
         {
            SendResponse_(250, _T("2.0.0"), _T("Queued for delivery"));

            String quarantineLog;
            quarantineLog.Format(_T("hMailServer SpamProtection quarantined a message (Sender: %s, IP: %s, Score: %d, Reason: %s)"),
               sFromAddress.c_str(), String(GetIPAddressString()).c_str(), iTotalSpamScore, messageText.c_str());
            LOG_APPLICATION(quarantineLog);

            return false;
         }

         // 5.7.1 = delivery not authorized, message refused (RFC 3463).
         if (spType == SPPreTransmission)
            SendResponse_(550, _T("5.7.1"), messageText);
         else
            SendResponse_(554, _T("5.7.1"), messageText);

         String sLogMessage;
         sLogMessage.Format(_T("hMailServer SpamProtection rejected RCPT (Sender: %s, IP:%s, Reason: %s)"), sFromAddress.c_str(), String(GetIPAddressString()).c_str(), messageText.c_str());
         LOG_APPLICATION(sLogMessage);

         return false;
      }
      else if (iSpamMarkThreshold > 0 && iTotalSpamScore >= iSpamMarkThreshold)
      {
         // This message is spam, but we shouldn't delete it. Instead, we will add spam headers to it.
         return true;
      }

      return true;
   }

   String 
   SMTPConnection::GetSpamTestResultMessage_(std::set<std::shared_ptr<SpamTestResult> > testResults) const
   {
      for(std::shared_ptr<SpamTestResult> result : testResults)
      {
         if (result->GetResult() == SpamTestResult::Fail)
            return result->GetMessage();
      }

      return "";
   }

   void
   SMTPConnection::ProtocolQUIT_()
   {
      // Reset the message here in case a message transmission has started, 
      // but hasn't ended. This can happen if the client sends DATA and then
      // the actual email message in the same buffer (which would be a RFC-violation).
      ResetCurrentMessage_();

      EnqueueWrite_("221 goodbye");
      
      pending_disconnect_ = true;
      EnqueueDisconnect();
   }

   void 
   SMTPConnection::AppendMessageHeaders_()
   {
      if (trace_headers_written_)
      {
         std::shared_ptr<MimeHeader> original_headers = Utilities::GetMimeHeader(transmission_buffer_->GetBuffer()->GetBuffer(), transmission_buffer_->GetBuffer()->GetSize());

         SMTPMessageHeaderCreator header_creator(username_, GetIPAddressString(), isAuthenticated_, message_submission_, helo_host_, original_headers, current_message_, GetSessionID());

         header_creator.SetPtrHost(GetPtrRecordHost_());

         if (IsSSLConnection())
            header_creator.SetCipherInfo(GetCipherInfo());

         AnsiString new_headers = header_creator.Create();
         
         std::shared_ptr<ByteBuffer> new_data = std::shared_ptr<ByteBuffer>(new ByteBuffer);
         new_data->Add((BYTE*)new_headers.GetBuffer(), new_headers.GetLength());
         new_data->Add(transmission_buffer_->GetBuffer()->GetBuffer(), transmission_buffer_->GetBuffer()->GetSize());
        
         // Add to the original buffer
         transmission_buffer_->GetBuffer()->Empty();
         transmission_buffer_->GetBuffer()->Add(new_data);

         trace_headers_written_ = false;
      }
   }

   void
   SMTPConnection::ParseData(std::shared_ptr<ByteBuffer> pBuf)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Parses a clients SMTP command in Binary mode.
   //---------------------------------------------------------------------------()
   {
      // RFC 3030 CHUNKING: the byte-counted payload of a BDAT chunk is handled by a
      // dedicated path (byte-transparent, no dot-unstuffing / no end-of-data scan).
      if (current_state_ == BDATDATA)
      {
         HandleBdatChunkData_(pBuf);
         return;
      }

      // Move the data from the incoming buffer to the transparent transmission buffer.
      // If we've received more data than the max message size, don't save it.

      const size_t incoming_bytes = pBuf->GetSize();

      transmission_buffer_->Append(pBuf->GetBuffer(), incoming_bytes);

      // We need current message size in KB
      size_t iBufSizeKB = transmission_buffer_->GetSize() / 1024;

      // Clear the old buffer
      pBuf->Empty();

      // Message reception is otherwise silent between "354" and the final reply,
      // so a session that stalls mid-transfer leaves nothing in the log to say
      // whether data was still arriving, whether end-of-data was recognised, or
      // whether the delay was in the accept/save stage that follows.
      if (Logger::Instance()->GetLogDebug())
      {
         String debug_message;
         debug_message.Format(_T("SMTPConnection - DATA: received %Iu bytes, %Iu buffered, end-of-data %s."),
            incoming_bytes, transmission_buffer_->GetSize(),
            transmission_buffer_->GetTransmissionEnded() ? _T("detected") : _T("not yet seen"));

         LOG_DEBUG(debug_message);
      }

      // Check if it's time to flush.
      if (transmission_buffer_->GetRequiresFlush())
      {
         // We need to prepend the transmission buffer
         // with the headers...
         AppendMessageHeaders_();
      }

      // Flush the transmission buffer
      transmission_buffer_->Flush();

      if (!transmission_buffer_->GetTransmissionEnded())
      {

         String sLogData;
         size_t iMaxSizeDrop = IniFileSettings::Instance()->GetSMTPDMaxSizeDrop();
         if (iMaxSizeDrop > 0 && iBufSizeKB >= iMaxSizeDrop) 
         {
            sLogData.Format(_T("Size: %d KB, Max size: %d KB - DROP!!"), 
            iBufSizeKB, iMaxSizeDrop);
            LOG_SMTP(GetSessionID(), GetIPAddressString(), sLogData);      
            String sMessage;
            sMessage.Format(_T("Message size exceeds the drop maximum message size. Size: %d KB, Max size: %d KB - DROP!"),
                iBufSizeKB, iMaxSizeDrop);
            SendResponse_(552, _T("5.3.4"), sMessage);
            LogAwstatsMessageRejected_();
            ResetCurrentMessage_();
            SetReceiveBinary(false);
            pending_disconnect_ = true;
            EnqueueDisconnect();
            return;

         }
         else 
         {
            // We need more data.
            EnqueueRead("");
            return;
         }
      }

      // The message ended. If it ended on a bare-LF spelling of the marker - accepted only
      // because this server was configured to tolerate incorrect line endings - then
      // anything the peer has already sent behind it is thrown away instead of being
      // parsed as SMTP commands.
      //
      // This is the CVE-2023-51764 mitigation for the tolerance, and without it the fix
      // for the hang would have opened a smuggling hole: a relay that does not accept
      // "\n.\r\n" as end-of-data forwards one message, and a server that accepts it and
      // then executes the lines behind it has been made to inject a second message the
      // relay never saw. Legitimate senders lose nothing, because RFC 2920 requires a
      // client to wait for the reply after end-of-data.
      if (transmission_buffer_->GetEndedOnNonStandardMarker())
         DiscardBufferedInput();
      else
      {
         // Behind the STANDARD marker, bytes in the same read are the client's next
         // pipelined commands - Postfix sends "...\r\n.\r\nQUIT\r\n" in one segment as
         // a matter of course. Captured here, before the async accept stage can reset
         // the transmission buffer, and handed back to the parser when command mode
         // resumes.
         pipelined_input_after_data_ = transmission_buffer_->GetSurplusAfterTerminator();
      }

      // Since this may be a time-consuming task, do it asynchronously
      finalization_enqueued_tick_ = GetTickCount64();
      std::shared_ptr<AsynchronousTask<TCPConnection> > finalizationTask =
         std::shared_ptr<AsynchronousTask<TCPConnection> >(new AsynchronousTask<TCPConnection>
            (std::bind(&SMTPConnection::HandleSMTPFinalizationTaskCompleted_, this), shared_from_this(),
             Formatter::Format("SMTP-accept session={0} ip={1}", GetSessionID(), GetIPAddressString())));

      // TaskMayBlock, which is what this task has always been in fact and never in
      // declaration. It runs the script and virus-scanner stages, so it blocks on
      // things outside this process - and every task on this queue was TaskNormal, so
      // AsyncQueueReservedThreads reserved nothing here at all. Its only user in the
      // tree was BackupManager, on a different queue. A wedged scanner or a slow script
      // could therefore take every thread in the async pool, which is precisely what
      // the reservation exists to prevent and what the roadmap row claimed it did.
      //
      // The two caller constraints on TaskMayBlock were checked before making this
      // change, because getting them wrong deadlocks mail rather than slowing it:
      // HandleSMTPFinalizationTaskCompleted_ neither posts to nor waits on another task
      // on this queue, and nothing anywhere waits on this task's GetIsStartedEvent() -
      // the only references to finalizationTask are its construction and this call.
      //
      // Checked, because the queue genuinely can be absent: it exists only while the
      // servers are running, and this runs on a session. Unchecked, "the servers
      // stopped between end-of-data and here" was a null dereference in the accept
      // path. The message is spooled but not accepted, so the honest answer is a
      // temporary failure that leaves the sender holding it.
      std::shared_ptr<WorkQueue> asyncQueue = Application::Instance()->GetAsyncWorkQueue();

      if (!asyncQueue)
      {
         SendResponse_(451, _T("4.3.2"), _T("The server is shutting down and cannot accept the message. Please retry later."));
         return;
      }

      asyncQueue->AddTask(finalizationTask, WorkQueue::TaskMayBlock);
   }

   void
   SMTPConnection::HandleSMTPFinalizationTaskCompleted_()
   {
      // The accept/save work below runs on the shared async queue and holds the
      // thread that sends the "250" for this message. Time it in stages: a "start"
      // line with no matching "done" line names the stage that stalled, and the
      // queue-wait line shows when the queue itself (not any one stage) is the
      // problem. See discussion #18. Timings are relative to end-of-data, the
      // instant the sending MTA started its own data-done timeout.
      const ULONGLONG queueWaitMs = finalization_enqueued_tick_ > 0
         ? GetTickCount64() - finalization_enqueued_tick_ : 0;

      if (queueWaitMs >= 5000)
      {
         String msg;
         msg.Format(_T("SMTPConnection - Accept/save waited %I64u ms in the async queue before starting (session %d). The async task queue may be saturated."),
            queueWaitMs, (int) GetSessionID());
         LOG_APPLICATION(msg);
      }

      // A finalization that races past the sending MTA's data-done timeout produces
      // discussion #18's exact symptom: reception looks complete but no reply is
      // ever sent. Rather than let that happen silently, give up before the point
      // of no return (nothing has been saved yet) with a temporary 451 so the MTA
      // retries cleanly. Checked here (queue starvation) and again before the save.
      if (FinalizationDeadlineExceeded_(queueWaitMs))
         return;

      LOG_DEBUG("SMTPConnection - accept: start spam-protection.");
      ULONGLONG stageTick = GetTickCount64();

      if (!DoPreAcceptSpamProtection_())
      {
         // This message was stopped by spam protection. The user either needs
         // to quit or start from rset again.
         LogAwstatsMessageRejected_();

         ResetCurrentMessage_();
         ResumeCommandModeAfterData_();
         return;
      }

      LogFinalizationStage_("spam-protection", stageTick);

      stageTick = GetTickCount64();
      DoPreAcceptMessageModifications_();
      LogFinalizationStage_("message-modifications", stageTick);

      // Transmission has ended.
      current_message_->SetSize(FileUtilities::FileSize(PersistentMessage::GetFileName(current_message_)));


      // Let's archive message we just received
      String sArchiveDir = IniFileSettings::Instance()->GetArchiveDir();

      if (!sArchiveDir.empty()) 
      {
         LOG_SMTP(GetSessionID(), GetIPAddressString(), "Archiving..");      

         bool bArchiveHardlinks = IniFileSettings::Instance()->GetArchiveHardlinks();
         String _messageFileName;
         String sFileNameExclPath;
         String sMessageArchivePath;
         String sFromAddress1 = current_message_->GetFromAddress();
         std::vector<String> vecParams1 = StringParser::SplitString(sFromAddress1,  "@");

         // We need exactly 2 or not an email address
         if (vecParams1.size() == 2)
         {
            String sResponse;
            String sSenderName = vecParams1[0];
            sSenderName = sSenderName.ToLower();
            String sSenderDomain = vecParams1[1];
            sSenderDomain = sSenderDomain.ToLower();
            bool blocalSender1 = GetIsLocalSender_();

            if (blocalSender1)
            {
               // First copy goes to local sender
               _messageFileName = PersistentMessage::GetFileName(current_message_);
               sFileNameExclPath = FileUtilities::GetFileNameFromFullPath(_messageFileName);
               sMessageArchivePath = sArchiveDir + "\\" + sSenderDomain + "\\" + sSenderName + "\\Sent-" + sFileNameExclPath;

               LOG_SMTP(GetSessionID(), GetIPAddressString(), "Local sender: " + sFromAddress1 + ". Putting in user folder: " + sMessageArchivePath);      

               FileUtilities::Copy(_messageFileName, sMessageArchivePath, true);
            }
            else
            {
               LOG_SMTP(GetSessionID(), GetIPAddressString(), "Non local sender, putting in common Inbound folder..");      

               // First copy goes to common archive folder instead
               _messageFileName = PersistentMessage::GetFileName(current_message_);
               sFileNameExclPath = FileUtilities::GetFileNameFromFullPath(_messageFileName);
               sMessageArchivePath = sArchiveDir + "\\Inbound\\" + sFileNameExclPath;

               FileUtilities::Copy(_messageFileName, sMessageArchivePath, true);
            }

            String sMessageArchivePath2;

            // Now create hardlink/copy for each *local* recipient
            std::shared_ptr<const Domain> pDomaintmp;
            bool bDomainIsLocal = false;

            const std::vector<std::shared_ptr<MessageRecipient> > vecRecipients = current_message_->GetRecipients()->GetVector();
            std::vector<std::shared_ptr<MessageRecipient> >::const_iterator iterRecipient = vecRecipients.begin();
            while (iterRecipient != vecRecipients.end())
            {
               String sRecipientAddress = (*iterRecipient)->GetAddress();
               vecParams1 = StringParser::SplitString(sRecipientAddress,  "@");

               // We need exactly 2 or not an email address
               if (vecParams1.size() == 2)
               {
                  String sResponse;
                  String sSenderName = vecParams1[0];
                  sSenderName = sSenderName.ToLower();
                  String sSenderDomain = vecParams1[1];
                  sSenderDomain = sSenderDomain.ToLower();

                  pDomaintmp = CacheContainer::Instance()->GetDomain(sSenderDomain);
                  bDomainIsLocal = pDomaintmp ? true : false;

                  if (bDomainIsLocal)
                  {
                     sMessageArchivePath2 = sArchiveDir + "\\" + sSenderDomain + "\\" + sSenderName + "\\" + sFileNameExclPath;
                     LOG_SMTP(GetSessionID(), GetIPAddressString(), "Local recipient: " + sRecipientAddress + ". Putting in user folder: " + sMessageArchivePath2);      

                     if (bArchiveHardlinks) 
                     {
                        FileUtilities::CreateDirectory(sArchiveDir + "\\" + sSenderDomain + "\\" + sSenderName);
                        // This function call is odd in that original is 2nd anc destination is 1st..
                        BOOL fCreatedLink = CreateHardLink( sMessageArchivePath2, sMessageArchivePath, NULL ); // Last is reserved, must be NULL

                        if ( fCreatedLink == FALSE )
                        {
                           // If error try normal copy
                           FileUtilities::Copy(sMessageArchivePath, sMessageArchivePath2, true);
                           LOG_SMTP(GetSessionID(), GetIPAddressString(), "HardLink failed.. Falling back to Copy.");      
                        }
                        else
                        {
                           LOG_SMTP(GetSessionID(), GetIPAddressString(), "HardLink succeeded.");      
                        }
                     }
                     else
                     {
                        FileUtilities::Copy(sMessageArchivePath, sMessageArchivePath2, true);
                     }
                  }
               }

               iterRecipient++;
            }
         }
         else
         {
            // Sender is either null/blank (ie <>) or some other odd thing happed so we'll save in Error folder
            // either way as failsafe.
            LOG_SMTP(GetSessionID(), GetIPAddressString(), "Sender is NULL or invalid. Saving to Error folder.");      

            _messageFileName = PersistentMessage::GetFileName(current_message_);
            sFileNameExclPath = FileUtilities::GetFileNameFromFullPath(_messageFileName);
            sMessageArchivePath = sArchiveDir + "\\Error\\" + sFileNameExclPath;
            FileUtilities::Copy(_messageFileName, sMessageArchivePath, true);
         }
      }   

      float dTime = ((float) GetTickCount() - (float) message_start_tc_) / (float) 1000;
      double dTCDiff = Math::Round(dTime ,3);

      // Last chance to bail before anything is delivered. Past OnPreAcceptTransfer_
      // and SaveObject the message is queued for delivery, so a late 451 here would
      // race a "250" and duplicate the mail; before it, a 451 is clean.
      if (FinalizationDeadlineExceeded_(finalization_enqueued_tick_ > 0 ? GetTickCount64() - finalization_enqueued_tick_ : 0))
         return;

      LOG_DEBUG("SMTPConnection - accept: start script/save.");
      ULONGLONG saveTick = GetTickCount64();

      if (OnPreAcceptTransfer_())
      {
         // Add the message to the database.
         if (PersistentMessage::SaveObject(current_message_))
         {
            // Make sure the transmission buffer has released the handle
            // to the file.
            if (transmission_buffer_)
               transmission_buffer_.reset();

            // Per-domain counters, before the message is released below. Counted
            // here, at acceptance, rather than at delivery: this answers "how much
            // mail did this domain handle", and a message that was accepted counts
            // whether or not it later bounced - which is the same thing the global
            // processed-messages counter has always meant.
            CountMessageAgainstLocalDomains_();

            // Add this message to the delivery queue cache. This way,
            // we won't have to read it from the database.
            MessageCache::Instance()->AddMessage(current_message_);

            // Free the message, so we don't access it the same time
            // as the SMTP delivery manager.
            current_message_.reset();

            // Tell the deliverer that a new message is pending. This
            // will cause the SMTP delivery manager to start a new delivery
            // thread and deliver the message.
            Application::Instance()->SubmitPendingEmail();

            // Reset the spam protection results.
            spam_test_results_.clear();

            // And the authentication verdicts with them. A client may send several
            // messages down one connection, and carrying a previous message's SPF or
            // DKIM result into the next one would put a verdict on mail that check
            // never ran against.
            authentication_results_.reset();

            // Tell the client that everything went fine. This
            // will cause the client to either disconnect or to
            // start a new message.
            String sQueuedText;
            sQueuedText.Format(_T("Queued (%.3f seconds)"), dTCDiff);
            SendResponse_(250, _T("2.0.0"), sQueuedText);

            // The message delivery is complete, or
            // it has failed. Any way, we should start
            // a new message.
            current_state_ = HEADER;
         }
         else
         {
            // The delivery of the message failed. This may happen if tables are
            // corrupt in the database. We now return an error message to the sender. 
            // Hopefully, the sending server will retry later. 
            // 451, not 554. The text has always said "retry later", but 554 is a
            // permanent failure: the sending server bounces the message to its
            // originator and does not try again, while the local copy is deleted
            // just below. Everything that reaches here - a database that is down,
            // locked by a backup, or out of pooled connections - is transient, so
            // the sender must be told to come back.
            SendResponse_(451, _T("4.3.0"), _T("Your message was received but it could not be saved. Please retry later."));

            // Delete the file now since we could not save it in the database.
            ResetCurrentMessage_();
            
         }
      }
      else
      {
         // The message was rejected by _OnPreAcceptTransfer. For example
         // this may happen if the message was rejected by a script subscribing
         // to OnAcceptMessage.
         ResetCurrentMessage_();
      }

      LogFinalizationStage_("script/save", saveTick);

      ResumeCommandModeAfterData_();
   }

   void
   SMTPConnection::ResumeCommandModeAfterData_()
   {
      SetReceiveBinary(false);

      if (pipelined_input_after_data_ && pipelined_input_after_data_->GetSize() > 0)
      {
         // The injection happens between two reads - none is armed until the
         // EnqueueRead below - so the receive buffer is exclusively ours here, and
         // ordering is preserved: these bytes arrived before anything the socket
         // still holds.
         InjectPipelinedBytes(pipelined_input_after_data_->GetBuffer(),
            pipelined_input_after_data_->GetSize());
      }

      pipelined_input_after_data_.reset();

      EnqueueRead();
   }

   void
   SMTPConnection::LogFinalizationStage_(const AnsiString &stage, ULONGLONG startTick)
   {
      const ULONGLONG elapsed = GetTickCount64() - startTick;

      String msg;
      msg.Format(_T("SMTPConnection - accept: done %s in %I64u ms (session %d)."),
         String(stage).c_str(), elapsed, (int) GetSessionID());

      // A stage that runs long is exactly what makes a relayed message time out on
      // the sender, so surface a slow one in the normal log rather than only under
      // debug. 10s is well short of any MTA's data-done timeout.
      if (elapsed >= 10000)
      {
         LOG_APPLICATION(msg);
      }
      else
      {
         LOG_DEBUG(msg);
      }
   }

   bool
   SMTPConnection::FinalizationDeadlineExceeded_(ULONGLONG elapsedMs)
   {
      const int deadlineSeconds = IniFileSettings::Instance()->GetFinalizationTimeout();

      // 0 disables the deadline (keeps the old unbounded behaviour for anyone who
      // wants it).
      if (deadlineSeconds <= 0 || elapsedMs < (ULONGLONG) deadlineSeconds * 1000)
         return false;

      String msg;
      msg.Format(_T("SMTPConnection - Accept/save for session %d exceeded the finalization deadline of %d s (%I64u ms elapsed since end-of-data). Returning a temporary 451 so the sender retries; the message was not accepted. A scanner, DNS lookup, event script or the async queue itself is the likely cause - the per-stage timings above identify which."),
         (int) GetSessionID(), deadlineSeconds, elapsedMs);
      ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5525, "SMTPConnection::HandleSMTPFinalizationTaskCompleted_", msg);

      SendResponse_(451, _T("4.3.1"), _T("Server temporarily overloaded while accepting the message; please retry."));

      LogAwstatsMessageRejected_();
      ResetCurrentMessage_();
      ResumeCommandModeAfterData_();

      return true;
   }

   void
   SMTPConnection::DoPreAcceptMessageModifications_()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Make changes to the message before it's accepted for delivery. This is
   // for example where message signature and spam-headers are added.
   //---------------------------------------------------------------------------()
   {
      std::shared_ptr<MessageData> pMsgData;

      // Check if we should add a spam header.
      int iTotalSpamScore = SpamProtection::CalculateTotalSpamScore(spam_test_results_);
      int iSpamMarkThreshold = Configuration::Instance()->GetAntiSpamConfiguration().GetSpamMarkThreshold();

      bool classifiedAsSpam = iSpamMarkThreshold > 0 && iTotalSpamScore >= iSpamMarkThreshold;
      
      if (classifiedAsSpam) 
      {
         // Set message SPAM Flag
         current_message_->SetFlagSpam(classifiedAsSpam);

         pMsgData = SpamProtection::AddSpamScoreHeaders(current_message_, spam_test_results_, classifiedAsSpam);

         // Increase the spam-counter
         ServerStatus::Instance()->OnSpamMessageDetected();
      }

      SetMessageSignature_(pMsgData);

      if (pMsgData && !pMsgData->WriteReported(PersistentMessage::GetFileName(current_message_), "The message signature"))
      {
         // WriteReported has already put the failure itself in the error log; what
         // belongs here is the consequence, because it is what an administrator
         // will actually be diagnosing: the file on disk is left as it arrived, so
         // the message is delivered without its spam-score headers and signature,
         // and a rule matching those headers will not fire for it. Refusing the
         // message instead would turn a failed header edit into refused mail,
         // which is the worse trade - the spam FLAG set above still travels on
         // the message object either way.
         String sWriteFailed;
         sWriteFailed.Format(_T("Message %I64d was delivered without its spam-score headers or signature - the rewrite of the message file failed."), current_message_->GetID());
         LOG_APPLICATION(sWriteFailed);
      }

      // RFC 2369 / RFC 2919 List-* headers for postings to local distribution lists.
      //
      // Position is load-bearing in both directions. It must come *after* the Write
      // above, which re-serialises the whole file from a MessageData loaded earlier in
      // this function - running before it would have that write discard the List-*
      // headers. And it must stay *inside* this function, because the caller re-reads
      // the file size from disk immediately afterwards, which is what makes the
      // rewritten size authoritative.
      DistributionListSender::AddListHeaders(current_message_);

      // Last, and for the same two reasons the List-* call gives: it must come after
      // every rewrite that re-serialises the file from a MessageData loaded earlier,
      // or that write would discard these fields, and it must stay inside this
      // function because the caller re-reads the file size from disk immediately
      // afterwards.
      //
      // Being last also puts our fields at the very top of the header block, above the
      // Received line this server wrote, which is where a reader expects the most
      // recent trace information to be.
      AddAuthenticationResultHeaders_();
   }

   void
   SMTPConnection::AddAuthenticationResultHeaders_()
   {
      if (!authentication_results_)
         return;

      if (!IniFileSettings::Instance()->GetAuthenticationResultsEnabled() &&
          !IniFileSettings::Instance()->GetReceivedSpfHeaderEnabled())
         return;

      // Only for mail arriving from outside. On a submission this server authenticated,
      // there is no third party whose claims need recording, and the account is already
      // named by X-AuthUser - whereas writing our own authserv-id onto a message a
      // local user hands us would put a verdict there that no check actually produced.
      if (isAuthenticated_)
         return;

      AuthenticationResultsWriter::Write(PersistentMessage::GetFileName(current_message_),
                                         current_message_,
                                         authentication_results_);
   }

   void
   SMTPConnection::SetMessageSignature_(std::shared_ptr<MessageData> &pMessageData)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Sets the signature of the message, based on the signature in the account
   // settings and domain settings.
   //---------------------------------------------------------------------------()
   {
      std::shared_ptr<SignatureAdder> pSignatureAdder = std::shared_ptr<SignatureAdder>(new SignatureAdder);
      pSignatureAdder->SetSignature(current_message_, sender_domain_, sender_account_, pMessageData);
   }

   bool
   SMTPConnection::OnPreAcceptTransfer_()
   {
      if (transmission_buffer_->GetCancelTransmission())
      {
         SendResponse_(554, _T("5.7.1"), transmission_buffer_->GetCancelMessage());
         LogAwstatsMessageRejected_();
         return false;
      }

      const String fileName = PersistentMessage::GetFileName(current_message_);

      // Before the Exists check below, because a failed write leaves the file very much
      // in existence - just not holding the message that was sent. Until this was
      // added, SaveToFile_ dropped the result of every write and returned true
      // regardless, so a full disk during DATA produced a truncated spool file and a
      // 250: the sender believed the message was delivered and there was nothing left
      // to retry. 451 is deliberate - a disk that is full now may not be in ten
      // minutes, and the existing handler already reports it as HM5019.
      if (transmission_buffer_->GetWriteFailed())
      {
         HandleUnableToSaveMessageDataFile_(fileName);
         return false;
      }

      if (!FileUtilities::Exists(fileName))
      {
         HandleUnableToSaveMessageDataFile_(fileName);
         return false;
      }


      // Check so that message isn't to big. Max message
      // size is specified in KB.
      if (max_message_size_kb_ > 0 && (transmission_buffer_->GetSize() / 1024) > max_message_size_kb_)
      {
         String sMessage;
         sMessage.Format(_T("Rejected - Message size exceeds fixed maximum message size. Size: %Iu KB, Max size: %Iu KB"),
            transmission_buffer_->GetSize() / 1024, max_message_size_kb_);

         // 552, not 554. RFC 1870 section 5 names 552 as the reply for a message that
         // exceeds the fixed maximum, whether the excess is discovered from the SIZE=
         // declaration or from the octets themselves, and the server's own other two
         // size refusals (the MAIL FROM estimate and the SMTPDMaxSizeDrop ceiling)
         // already answer 552. This one said 554 "transaction failed", so the same
         // condition was reported with two different codes depending on whether the
         // client had declared a size - and a sender looking at 554 5.3.4 cannot tell a
         // size refusal from any other transaction failure.
         SendResponse_(552, _T("5.3.4"), sMessage);
         LogAwstatsMessageRejected_();
         return false;
      }

      // Check for bare LF's.
      //
      // Except in a BINARYMIME transaction, where the check would be checking
      // binary content for line discipline it never claimed to have: RFC 3030
      // says BINARYMIME data is not line-oriented, and bare CR, bare LF and NUL
      // are all legal in it. The header block is still required to be textual by
      // the same RFC, but refusing a message because its binary BODY contains
      // the byte 0x0A is refusing the exact content the extension exists to
      // carry. This surfaced on the feature's own first test: the fixture's
      // all-256-octet payload was refused 554 5.6.0 by this check.
      if (!binarymime_requested_ &&
          !Configuration::Instance()->GetSMTPConfiguration()->GetAllowIncorrectLineEndings())
      {
         if (!CheckLineEndings_())
         {
            // 5.6.0 = other or undefined media (message content) error (RFC 3463).
            SendResponse_(554, _T("5.6.0"), _T("Rejected - Message containing bare LF's."));
            LogAwstatsMessageRejected_();
            return false;
         }

      }

      if (Configuration::Instance()->GetUseScriptServer())
      {
         std::shared_ptr<ScriptObjectContainer> pContainer = std::shared_ptr<ScriptObjectContainer>(new ScriptObjectContainer);
         std::shared_ptr<Result> pResult = std::shared_ptr<Result>(new Result);
         std::shared_ptr<ClientInfo> pClientInfo = std::shared_ptr<ClientInfo>(new ClientInfo);

         pClientInfo->SetUsername(username_);
         pClientInfo->SetIPAddress(GetIPAddressString());
         pClientInfo->SetPort(GetLocalEndpointPort());
         pClientInfo->SetSessionID(GetSessionID());
         pClientInfo->SetHELO(helo_host_);
         pClientInfo->SetIsAuthenticated(isAuthenticated_);
         pClientInfo->SetIsEncryptedConnection(IsSSLConnection());
         if (IsSSLConnection())
         {
            auto cipher_info = GetCipherInfo();
            pClientInfo->SetCipherVersion(cipher_info.GetVersion().c_str());
            pClientInfo->SetCipherName(cipher_info.GetName().c_str());
            pClientInfo->SetCipherBits(cipher_info.GetBits());
         }

         pContainer->AddObject("HMAILSERVER_MESSAGE", current_message_, ScriptObject::OTMessage);
         pContainer->AddObject("HMAILSERVER_CLIENT", pClientInfo, ScriptObject::OTClient);
         pContainer->AddObject("Result", pResult, ScriptObject::OTResult);

         String sEventCaller = "OnAcceptMessage(HMAILSERVER_CLIENT, HMAILSERVER_MESSAGE)";
         ScriptServer::Instance()->FireEvent(ScriptServer::EventOnAcceptMessage, sEventCaller, pContainer);

         switch (pResult->GetValue())
         {
         case 1:
            {
               // 5.7.1 / 4.7.0: rejected by local policy (a script), permanently or
               // temporarily. The numeric codes are unchanged.
               SendResponse_(554, _T("5.7.1"), _T("Rejected"));
               LogAwstatsMessageRejected_();
               return false;
            }
         case 2:
            {
               SendResponse_(554, _T("5.7.1"), pResult->GetMessage());
               LogAwstatsMessageRejected_();
               return false;
            }
         case 3:
            {
               SendResponse_(453, _T("4.7.0"), pResult->GetMessage());
               LogAwstatsMessageRejected_();
               return false;
            }
         }
      }      

      if (GetSecurityRange()->GetVirusProtection())
      {
         current_message_->SetFlagVirusScan(true);
      }

      return true;
   }

   void 
   SMTPConnection::HandleUnableToSaveMessageDataFile_(const String &file_name)
   {
      String sErrorMsg = Formatter::Format("Rejected message because no mail data has been saved in file {0}", file_name);
      ErrorManager::Instance()->ReportError(ErrorManager::Critical, 5019, "SMTPConnectionSMTPConnection::HandleUnableToSaveMessage_", sErrorMsg);

      SendResponse_(451, _T("4.3.0"), _T("Rejected - No data saved."));
      LogAwstatsMessageRejected_();
   }

   bool 
   SMTPConnection::CheckLineEndings_() const
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Checks if the message contains any CR with missing LF, or any LF with
   // missing CR.
   //---------------------------------------------------------------------------()
   {
      if (!current_message_)
         return false;

      const String fileName = PersistentMessage::GetFileName(current_message_);

      File oFile;


      std::shared_ptr<ByteBuffer> pBuffer;

      const int iChunkSize = 10000;

      try
      {
         oFile.Open(fileName, File::OTReadOnly);

         pBuffer = oFile.ReadChunk(iChunkSize);
      }
      catch (...)
      {
         return false;
      }

      char prevChar = 0;  // last byte of the previous chunk (0 = none yet)

      while (pBuffer->GetSize() > 0)
      {
         const char *pChar = pBuffer->GetCharBuffer();
         size_t iBufferSize = pBuffer->GetSize();

         for (size_t i = 0; i < iBufferSize; i++)
         {
            char currentChar = pChar[i];
            char prev = (i == 0) ? prevChar : pChar[i - 1];

            // \r must be immediately followed by \n
            if (prev == '\r' && currentChar != '\n')
               return false;

            // \n must be immediately preceded by \r
            if (currentChar == '\n' && prev != '\r')
               return false;
         }

         prevChar = pChar[iBufferSize - 1];

         // Read next chunk
         try
         {
            pBuffer = oFile.ReadChunk(iChunkSize);
         }
         catch (...)
         {
            return false;
         }
      }

      // A trailing \r with nothing after it is a bare CR
      if (prevChar == '\r')
         return false;

      return true;
   }

   void 
   SMTPConnection::LogAwstatsMessageRejected_()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // If awstats logging is enabled, this function goes through all the recipients
   // of the message, and logs to the awstats log that they have been rejected.
   // This is used if a message is rejected after it has been transferred from the
   // client to the server.
   //---------------------------------------------------------------------------()
   {
      // Check that message exists, and that the awstats log is enabled.
      if (!current_message_ || !AWStats::GetEnabled())
         return;

      // Go through the recipients and log one row for each of them.
      String sFromAddress = current_message_->GetFromAddress();

      const std::vector<std::shared_ptr<MessageRecipient> > vecRecipients = current_message_->GetRecipients()->GetVector();
      std::vector<std::shared_ptr<MessageRecipient> >::const_iterator iterRecipient = vecRecipients.begin();
      while (iterRecipient != vecRecipients.end())
      {
         String sRecipientAddress = (*iterRecipient)->GetAddress();

         // Log the error message
         AWStats::LogDeliveryFailure(GetIPAddressString(), sFromAddress, sRecipientAddress, 554);

         iterRecipient++;
      }

   }

   void
   SMTPConnection::ResetCurrentMessage_()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Reset the transmission buffer to free
   // any handles it has opened to the message
   // file
   //---------------------------------------------------------------------------()
   {
      if (transmission_buffer_)
      {
         transmission_buffer_.reset();
      }

      // Reset the current message.
      if (current_message_)
      {
         // This message isn't complete, so we should delete it from disk now.
         std::shared_ptr<Account> emptyAccount;

         PersistentMessage::DeleteFile(emptyAccount, current_message_);

         // Reset message object
         current_message_.reset();
      }

      rejected_by_delayed_grey_listing_ = false;

      message_submission_ = false;

      sender_domain_.reset();
      sender_account_.reset();

      spam_test_results_.clear();
      authentication_results_.reset();

      // Reset the number of RCPT TO's for this
      // message.
      cur_no_of_rcptto_ = 0;

      // Reset per-transaction ESMTP parameters (SMTPUTF8 / BINARYMIME / DSN).
      smtputf8_requested_ = false;
      binarymime_requested_ = false;
      dsn_envid_.Empty();
      dsn_ret_.Empty();

      // Reset per-transaction RFC 3030 (CHUNKING/BDAT) state.
      bdat_active_ = false;
      bdat_last_ = false;
      bdat_discard_ = false;
      bdat_chunk_size_ = 0;
      bdat_chunk_remaining_ = 0;

      // Switch back to normal ASCII mode and the start of a transaction, in
      // case we are in binary transmission mode - but never promote a session
      // that has not been greeted. RSET is valid before EHLO (RFC 5321 section
      // 4.1.4) and used to move the state to HEADER on its own, so RSET then
      // MAIL FROM skipped the greeting entirely, with the same consequences as
      // the STARTTLS case in OnHandshakeCompleted (upstream #604/#605).
      if (current_state_ != INITIAL)
         current_state_ = HEADER;
   }


   bool
   SMTPConnection::SendEHLOKeywords_()
   {
      String sComputerName = Utilities::ComputerName(); 

      String sData = "250-" + sComputerName;
      
      // Append size keyword
      {
         String sSizeKeyword;
         __int64 iMaxSize = (__int64) smtpconf_->GetMaxMessageSize() * 1024;
         if (iMaxSize > 0)
            sSizeKeyword.Format(_T("\r\n250-SIZE %I64d"), iMaxSize);
         else
            sSizeKeyword.Format(_T("\r\n250-SIZE"));
         sData += sSizeKeyword;
      }

      // The message transmission path is 8-bit clean (RFC 6152).
      sData += "\r\n250-8BITMIME";

      // PIPELINING (RFC 2920): the command reader processes batched commands
      // line by line and enqueues every reply in order, so a client may stream
      // a group of commands without waiting for each intermediate response.
      sData += "\r\n250-PIPELINING";

      // CHUNKING (RFC 3030): accept message data via one or more BDAT commands,
      // each carrying an explicit octet count, terminated by "BDAT <n> LAST".
      sData += "\r\n250-CHUNKING";

      // BINARYMIME (RFC 3030, requires CHUNKING above): accept BODY=BINARYMIME -
      // raw binary message content, delivered byte-for-byte via BDAT. Honest
      // scope: local mailbox delivery only. A recipient that would require onward
      // relay is refused synchronously at RCPT TO with 554 5.6.3, because the
      // delivery client transmits via DATA only and down-conversion is not
      // implemented - see ProtocolRCPT_ and SMTPClientConnection.
      sData += "\r\n250-BINARYMIME";

      // SMTPUTF8 (RFC 6531): accept internationalized (UTF-8) envelope addresses.
      sData += "\r\n250-SMTPUTF8";

      // ENHANCEDSTATUSCODES (RFC 2034): responses carry an RFC 3463 status code.
      sData += "\r\n250-ENHANCEDSTATUSCODES";

      // DSN (RFC 3461): accept RET/ENVID on MAIL FROM and NOTIFY/ORCPT on RCPT TO,
      // and honour NOTIFY=NEVER when generating delivery-failure notifications.
      sData += "\r\n250-DSN";

      // XCLIENT (Postfix): advertised ONLY to a configured trusted upstream,
      // decided against the REAL TCP peer address. Advertising it to anyone
      // else both invites the attempt and leaks the deployment shape, so an
      // untrusted peer never learns the verb exists (and gets 550 if it tries
      // anyway).
      if (XClientPermitted_())
         sData += "\r\n250-XCLIENT ADDR NAME PORT PROTO HELO LOGIN";

      if (!IsSSLConnection())
      {
         if (GetConnectionSecurity() == CSSTARTTLSOptional ||
             GetConnectionSecurity() == CSSTARTTLSRequired)
         {
            sData += "\r\n250-STARTTLS";
         }
      }

      // RFC 4954 section 4: a server must not announce a mechanism it will not accept on
      // this connection. AUTH is refused on a cleartext connection in two cases, not one
      // - the port being CSSTARTTLSRequired, and the connecting IP range setting
      // RequireTLSForAuth (see the 530 in ProtocolAUTH_) - and only the first was
      // reflected here.
      //
      // In the second case EHLO offered "AUTH LOGIN PLAIN SCRAM-SHA-256" and the AUTH
      // that followed was answered 530, by which time the client had already sent
      // base64(authzid NUL authcid NUL password) for PLAIN, or the base64 user name and
      // password for LOGIN. The credential is on the wire in the clear before the
      // refusal, which is the exact thing RequireTLSForAuth exists to prevent.
      //
      // Fourth protocol with this defect and the most exposed of them: POP3 CAPA and the
      // ManageSieve capability response were fixed earlier the same week, IMAP CAPABILITY
      // in the same change as this one. The condition is written the same way in all
      // four so they read as one decision rather than four that happen to agree.
      const bool authRefusedOnCleartext =
         GetConnectionSecurity() == CSSTARTTLSRequired ||
         GetSecurityRange()->GetRequireTLSForAuth();

      if (GetAuthIsEnabled_() && (IsSSLConnection() || !authRefusedOnCleartext))
      {
         String sAuth = "\r\n250-AUTH LOGIN";

         if (smtpconf_->GetAuthAllowPlainText())
            sAuth += " PLAIN";

         // SCRAM-SHA-256 (RFC 7677) never transmits the password, so it is offered
         // whenever AUTH is enabled, independent of the plain-text setting.
         sAuth += " SCRAM-SHA-256";

         // SCRAM-SHA-256-PLUS (RFC 5802 + RFC 5929 tls-server-end-point) binds the
         // exchange to the TLS channel, so it is only offered on a TLS connection.
         if (IsSSLConnection())
            sAuth += " SCRAM-SHA-256-PLUS";

         // OAuth2 bearer mechanisms (RFC 7628), advertised only when enabled and (by
         // default) only over TLS.
         if (OAuth2TokenValidator::IsEnabled() && (!OAuth2TokenValidator::RequireTLS() || IsSSLConnection()))
            sAuth += " XOAUTH2 OAUTHBEARER";

         // EXTERNAL (RFC 4422 Appendix A): the client's proof is the certificate the
         // handshake verified against this port's CA, so the mechanism exists on a
         // connection exactly when such a certificate names an address. Offered to
         // nobody else - there is nothing they could answer with.
         if (!GetVerifiedClientCertificateIdentities().empty())
            sAuth += " EXTERNAL";

         sData += sAuth;
      }

      sData += "\r\n250 HELP";

      EnqueueWrite_(sData);
   
      return true;
   }

   void
   SMTPConnection::OnConnectionTimeout()
   {
      // 4.4.2 = bad connection (RFC 3463).
      SendResponse_(421, _T("4.4.2"), _T("Connection timeout."));
   }

   void
   SMTPConnection::OnExcessiveDataReceived()
   {
      ResetCurrentMessage_();

      // 4.7.0 = other or undefined security status (RFC 3463).
      SendResponse_(421, _T("4.7.0"), _T("Excessive amounts of data sent to server."));
   }

   void 
   SMTPConnection::EnqueueWrite_(const String &sData)
   {
      if (Logger::Instance()->GetLogSMTP())
      {
         String sLogData = "SENT: " + sData;

         sLogData.TrimRight(_T("\r\n"));

         LOG_SMTP(GetSessionID(), GetIPAddressString(), sLogData);
      }

      EnqueueWrite(sData + "\r\n");
   }

   bool
   SMTPConnection::ReadDomainAddressFromHelo_(const  String &sRequest)
   {
      int iFirstSpace = sRequest.Find(_T(" "));
      
      if (iFirstSpace == -1)
      {
         // No host name has been specified => RFC violation
         return false;
      }

      // Cut out the string after the space.
      helo_host_ = sRequest.Mid(iFirstSpace + 1);

      // Trim it incase of leading or trailing spaces.
      helo_host_ = helo_host_.Trim();

      if (helo_host_.IsEmpty())
         return false;

      return true;

   }

   void
   SMTPConnection::ProtocolEHLO_(const String &sRequest)
   {

      if (!ReadDomainAddressFromHelo_(sRequest))
      {
         // The client did not supply a parameter to
         // the helo command which is syntaxically
         // incorrect. Reject.
         SendErrorResponse_(501, "EHLO Invalid domain address.");
         return;
      }

      //
      // Event OnHELO
      //
      if (Configuration::Instance()->GetUseScriptServer())
      {
         std::shared_ptr<ScriptObjectContainer> pContainer = std::shared_ptr<ScriptObjectContainer>(new ScriptObjectContainer);
         std::shared_ptr<Result> pResult = std::shared_ptr<Result>(new Result);
         std::shared_ptr<ClientInfo> pClientInfo = std::shared_ptr<ClientInfo>(new ClientInfo);

         pClientInfo->SetIPAddress(GetIPAddressString());
         pClientInfo->SetPort(GetLocalEndpointPort());
         pClientInfo->SetSessionID(GetSessionID());
         pClientInfo->SetHELO(helo_host_);
         pClientInfo->SetIsEncryptedConnection(IsSSLConnection());
         if (IsSSLConnection())
         {
            auto cipher_info = GetCipherInfo();
            pClientInfo->SetCipherVersion(cipher_info.GetVersion().c_str());
            pClientInfo->SetCipherName(cipher_info.GetName().c_str());
            pClientInfo->SetCipherBits(cipher_info.GetBits());
         }

         pContainer->AddObject("HMAILSERVER_CLIENT", pClientInfo, ScriptObject::OTClient);
         pContainer->AddObject("Result", pResult, ScriptObject::OTResult);

         String sEventCaller = "OnHELO(HMAILSERVER_CLIENT)";
         ScriptServer::Instance()->FireEvent(ScriptServer::EventOnHELO, sEventCaller, pContainer);

         switch (pResult->GetValue())
         {
         case 1:
         {
            // 5.7.1 / 4.7.0: rejected by local policy (a script). Note that
            // esmtp_session_ is not set until the EHLO reply has been sent, so a
            // rejection here is still answered without an enhanced code.
            SendResponse_(554, _T("5.7.1"), _T("Rejected"));
            LogAwstatsMessageRejected_();
            return;
         }
         case 2:
         {
            SendResponse_(554, _T("5.7.1"), pResult->GetMessage());
            LogAwstatsMessageRejected_();
            return;
         }
         case 3:
         {
            SendResponse_(453, _T("4.7.0"), pResult->GetMessage());
            LogAwstatsMessageRejected_();
            return;
         }
         }
      }

      SendEHLOKeywords_();

      // The client greeted with EHLO, so it is an ESMTP session and may receive
      // RFC 2034 enhanced status codes.
      esmtp_session_ = true;

      if (current_state_ == INITIAL)
         current_state_ = HEADER;
   }
   
   void
   SMTPConnection::ProtocolHELO_(const String &sRequest)
   {
      if (!ReadDomainAddressFromHelo_(sRequest))
      {
         // The client did not supply a parameter to
         // the helo command which is syntaxically
         // incorrect. Reject.
         SendErrorResponse_(501, "HELO Invalid domain address.");
         return;
      }

      //
      // Event OnHELO
      //
      if (Configuration::Instance()->GetUseScriptServer())
      {
         std::shared_ptr<ScriptObjectContainer> pContainer = std::shared_ptr<ScriptObjectContainer>(new ScriptObjectContainer);
         std::shared_ptr<Result> pResult = std::shared_ptr<Result>(new Result);
         std::shared_ptr<ClientInfo> pClientInfo = std::shared_ptr<ClientInfo>(new ClientInfo);

         pClientInfo->SetIPAddress(GetIPAddressString());
         pClientInfo->SetPort(GetLocalEndpointPort());
         pClientInfo->SetSessionID(GetSessionID());
         pClientInfo->SetHELO(helo_host_);
         pClientInfo->SetIsEncryptedConnection(IsSSLConnection());
         if (IsSSLConnection())
         {
            auto cipher_info = GetCipherInfo();
            pClientInfo->SetCipherVersion(cipher_info.GetVersion().c_str());
            pClientInfo->SetCipherName(cipher_info.GetName().c_str());
            pClientInfo->SetCipherBits(cipher_info.GetBits());
         }

         pContainer->AddObject("HMAILSERVER_CLIENT", pClientInfo, ScriptObject::OTClient);
         pContainer->AddObject("Result", pResult, ScriptObject::OTResult);

         String sEventCaller = "OnHELO(HMAILSERVER_CLIENT)";
         ScriptServer::Instance()->FireEvent(ScriptServer::EventOnHELO, sEventCaller, pContainer);

         switch (pResult->GetValue())
         {
         case 1:
         {
            // 5.7.1 / 4.7.0: rejected by local policy (a script).
            SendResponse_(554, _T("5.7.1"), _T("Rejected"));
            LogAwstatsMessageRejected_();
            return;
         }
         case 2:
         {
            SendResponse_(554, _T("5.7.1"), pResult->GetMessage());
            LogAwstatsMessageRejected_();
            return;
         }
         case 3:
         {
            SendResponse_(453, _T("4.7.0"), pResult->GetMessage());
            LogAwstatsMessageRejected_();
            return;
         }
         }
      }

      EnqueueWrite_("250 Hello.");

      // HELO selects basic SMTP, so RFC 2034 enhanced status codes are not used
      // (even if the client previously issued EHLO on this connection).
      esmtp_session_ = false;

      if (current_state_ == INITIAL)
         current_state_ = HEADER;

   }

   bool
   SMTPConnection::XClientPermitted_()
   {
      if (!IniFileSettings::Instance()->GetSMTPXClientEnabled())
         return false;

      // Decided against the REAL TCP peer - the socket's remote endpoint -
      // never against the effective (possibly rewritten) client address. A
      // PROXY protocol rewrite earlier in the session must not let the
      // FORWARDED client inherit the upstream's XCLIENT authority, and an
      // XCLIENT rewrite must not chain into further trust: whatever addresses
      // have been asserted, the entity on the other end of this socket is the
      // one being authorised.
      return TrustedProxyList::Matches(
         IniFileSettings::Instance()->GetSMTPXClientTrustedIPs(),
         GetTrueRemoteEndpointAddress());
   }

   void
   SMTPConnection::ProtocolXCLIENT_(const String &sRequest)
   {
      // Postfix XCLIENT: a trusted upstream forwards the identity of the
      // client it relays for; on success the server answers with a fresh 220
      // greeting and the upstream re-issues EHLO, after which the session
      // behaves as if the asserted client had connected directly.

      // Authorisation first, before any syntax checking, so an untrusted peer
      // learns nothing but the refusal. The verb is not advertised to
      // untrusted peers either (see SendEHLOKeywords_), so reaching this is a
      // probe or a misconfigured upstream. Postfix's wording.
      if (!XClientPermitted_())
      {
         SendResponse_(550, _T("5.7.0"), _T("Error: insufficient authorization."));
         return;
      }

      // On a STARTTLS-required port the upstream must negotiate TLS before
      // asserting identities, the same rule every other verb follows.
      if (!CheckStartTlsRequired_())
         return;

      if (current_message_)
      {
         SendResponse_(503, _T("5.5.1"), _T("XCLIENT is not permitted inside a mail transaction."));
         return;
      }

      String sAttributes = sRequest.Mid(7).Trim();

      if (sAttributes.IsEmpty())
      {
         SendResponse_(501, _T("5.5.4"), _T("Bad command parameter syntax."));
         return;
      }

      // Validate every attribute before applying any: a command that is
      // half-good must not leave the session half-rewritten.
      bool haveAddr = false;
      IPAddress newAddress;
      bool havePort = false;
      unsigned int newPort = 0;
      bool haveName = false;
      String newName;
      bool haveHelo = false;
      String newHelo;
      bool haveProto = false;
      bool newEsmtp = false;
      bool haveLogin = false;
      String newLogin;

      std::vector<String> attributes = StringParser::SplitString(sAttributes, " ");

      for (String attribute : attributes)
      {
         attribute = attribute.Trim();
         if (attribute.IsEmpty())
            continue;

         int equalsIndex = attribute.Find(_T("="));
         if (equalsIndex <= 0 || equalsIndex == attribute.GetLength() - 1)
         {
            SendResponse_(501, _T("5.5.4"), _T("Bad command parameter syntax."));
            return;
         }

         String name = attribute.Left(equalsIndex);
         name.MakeUpper();
         String value = attribute.Mid(equalsIndex + 1);

         // The two defined "don't know" values (the brackets are literal).
         // [UNAVAILABLE] = the upstream does not have this datum;
         // [TEMPUNAVAIL] = it could not obtain it right now. Neither carries
         // an address or name to apply.
         bool unavailable = value.CompareNoCase(_T("[UNAVAILABLE]")) == 0 ||
                            value.CompareNoCase(_T("[TEMPUNAVAIL]")) == 0;

         if (name == _T("ADDR"))
         {
            if (unavailable)
               continue; // "don't know" must not rewrite the address.

            String addressValue = value;

            // Postfix sends IPv6 addresses with an "IPV6:" prefix.
            if (addressValue.Left(5).CompareNoCase(_T("IPV6:")) == 0)
               addressValue = addressValue.Mid(5);

            if (!newAddress.TryParse(addressValue, false))
            {
               SendResponse_(501, _T("5.5.4"), _T("Bad ADDR syntax."));
               return;
            }

            haveAddr = true;
         }
         else if (name == _T("PORT"))
         {
            if (unavailable)
               continue;

            bool valid = !value.IsEmpty() && value.GetLength() <= 5;
            unsigned int parsedPort = 0;

            if (valid)
            {
               for (int i = 0; i < value.GetLength(); i++)
               {
                  wchar_t c = value.GetAt(i);
                  if (c < '0' || c > '9')
                  {
                     valid = false;
                     break;
                  }
                  parsedPort = parsedPort * 10 + (c - '0');
               }
            }

            if (!valid || parsedPort > 65535)
            {
               SendResponse_(501, _T("5.5.4"), _T("Bad PORT syntax."));
               return;
            }

            havePort = true;
            newPort = parsedPort;
         }
         else if (name == _T("NAME"))
         {
            haveName = true;
            newName = unavailable ? String(_T("Unknown")) : value;
         }
         else if (name == _T("HELO"))
         {
            haveHelo = true;
            newHelo = unavailable ? String(_T("")) : value;
         }
         else if (name == _T("PROTO"))
         {
            if (value.CompareNoCase(_T("SMTP")) == 0)
               newEsmtp = false;
            else if (value.CompareNoCase(_T("ESMTP")) == 0)
               newEsmtp = true;
            else
            {
               SendResponse_(501, _T("5.5.4"), _T("Bad PROTO syntax."));
               return;
            }

            haveProto = true;
         }
         else if (name == _T("LOGIN"))
         {
            haveLogin = true;
            newLogin = unavailable ? String(_T("")) : value;
         }
         else
         {
            // Only the attributes advertised in EHLO are within the contract.
            SendResponse_(501, _T("5.5.4"), _T("Bad command parameter syntax."));
            return;
         }
      }

      if (haveAddr)
      {
         // The rewritten client must still be allowed to connect at all: the
         // IP-range check that ran at accept time saw the upstream's address,
         // not this one. A client that would have been refused at the door -
         // including by an auto-ban range - is refused here.
         std::shared_ptr<SecurityRange> securityRange = PersistentSecurityRange::ReadMatchingIP(newAddress);

         if (!securityRange || !securityRange->GetAllowSMTP())
         {
            String message;
            message.Format(_T("SMTP - XCLIENT from %s: the asserted client address %s is blocked by the IP range configuration. The connection has been closed. Session: %d"),
               String(GetTrueRemoteEndpointAddress().ToString()).c_str(),
               String(newAddress.ToString()).c_str(), GetSessionID());
            LOG_APPLICATION(message);

            SendResponse_(550, _T("5.7.1"), _T("Client host rejected by IP range policy."));
            pending_disconnect_ = true;
            EnqueueDisconnect();
            return;
         }

         String message;
         message.Format(_T("SMTP - XCLIENT from %s: the session's client address is now %s. Session: %d"),
            String(GetTrueRemoteEndpointAddress().ToString()).c_str(),
            String(newAddress.ToString()).c_str(), GetSessionID());
         LOG_TCPIP(message);

         // Applied before any of the checks that consume the client address
         // can run again: DNSBL/SPF/greylisting run at MAIL FROM / RCPT TO,
         // auto-ban registration at AUTH, the Received header at DATA - all
         // later than this point, and all read GetRemoteEndpointAddress().
         SetRemoteAddressOverride(newAddress, havePort ? newPort : 0);
         SetSecurityRange(securityRange);

         // Whatever PTR was prefetched belongs to the upstream's address.
         {
            boost::lock_guard<boost::mutex> guard(ptr_result_mutex_);
            ptr_lookup_completed_ = false;
            ptr_record_host_.Empty();
            ptr_record_for_ip_.Empty();
         }

         if (!haveName)
            RestartPtrPrefetch_();
      }

      if (haveName)
      {
         boost::lock_guard<boost::mutex> guard(ptr_result_mutex_);
         ptr_record_host_ = newName;
         ptr_record_for_ip_ = GetIPAddressString();
         ptr_lookup_completed_ = true;
      }

      if (haveHelo)
         helo_host_ = newHelo;

      if (haveProto)
         esmtp_session_ = newEsmtp;

      if (haveLogin)
      {
         if (newLogin.IsEmpty())
         {
            // LOGIN=[UNAVAILABLE]: the upstream says the client is not
            // authenticated.
            isAuthenticated_ = false;
            authenticated_by_xclient_ = false;
            username_ = "";
         }
         else
         {
            // The trusted upstream vouches that the client authenticated with
            // it under this name. No password crossed this connection;
            // ReAuthenticateUser knows not to demand one.
            username_ = newLogin;
            password_ = "";
            isAuthenticated_ = true;
            authenticated_by_xclient_ = true;
         }
      }

      // XCLIENT begins a new SMTP session: transaction state is forgotten,
      // the state machine returns to the just-connected position, and the
      // reply is a fresh 220 greeting, after which the upstream re-issues
      // EHLO. (ParseData arms the next command read after this returns.)
      ResetCurrentMessage_();
      current_state_ = INITIAL;

      EnqueueWrite_(GetBannerText_());
   }

   void
   SMTPConnection::ProtocolHELP_()
   {
      // 530 Must issue STARTTLS first
      // to every command other than NOOP, EHLO, STARTTLS, or QUIT.
      if (!CheckStartTlsRequired_())
         return;

      // The following code is to test the error handling in production environments.
      // Crash simulation mode can be enabled in hMailServer.ini. 
      int crash_simulation_mode = Configuration::Instance()->GetCrashSimulationMode();
      if (crash_simulation_mode > 0)
         CrashSimulation::Execute(crash_simulation_mode);

      // The list used to be "DATA HELO EHLO MAIL NOOP QUIT RCPT RSET SAML TURN VRFY".
      // Three of those were wrong in both directions. SAML is not implemented at all -
      // GetCommandType_ does not know the verb, so a client that took the advice got
      // "503 Bad sequence of commands" - while TURN and VRFY are recognised only in
      // order to answer 502, so naming them invites the two requests the server always
      // refuses. Meanwhile AUTH, BDAT and STARTTLS, which are implemented and
      // advertised, were missing. HELP is not a capability announcement, but a list
      // that names commands the server does not have and omits the ones it does is
      // worse than no list.
      //
      // AUTH and STARTTLS are conditional for the same reason they are conditional in
      // the EHLO response: on a port where AUTH is disabled, or a connection which is
      // already encrypted, offering them would be untrue. ETRN stays out deliberately -
      // it is implemented but not advertised.
      String helpCommands = "DATA EHLO HELO HELP MAIL NOOP QUIT RCPT RSET BDAT";

      if (GetAuthIsEnabled_() && (IsSSLConnection() || GetConnectionSecurity() != CSSTARTTLSRequired))
         helpCommands += " AUTH";

      if (!IsSSLConnection() &&
          (GetConnectionSecurity() == CSSTARTTLSOptional || GetConnectionSecurity() == CSSTARTTLSRequired))
         helpCommands += " STARTTLS";

      SendResponse_(211, _T("2.0.0"), helpCommands);
   }

   void
   SMTPConnection::ProtocolDATA_()
   {
      // 530 Must issue STARTTLS first
      // to every command other than NOOP, EHLO, STARTTLS, or QUIT.
      if (!CheckStartTlsRequired_())
         return;

      // RFC 3030: once a transaction has started using BDAT it must be completed
      // with BDAT; mixing in a DATA command is illegal.
      if (bdat_active_)
      {
         SendResponse_(503, _T("5.5.1"), _T("Bad sequence of commands: BDAT already used in this transaction."));
         return;
      }

      if (!current_message_)
      {
         // User tried to send a mail without specifying a correct mail from or rcpt to.
         SendResponse_(503, _T("5.5.1"), _T("Must have sender and recipient first."));

         return;
      }
      else if ( current_message_->GetRecipients()->GetCount() == 0)
      {
         // User tried to send a mail without specifying a correct mail from or rcpt to.
         SendResponse_(503, _T("5.5.1"), _T("Must have sender and recipient first."));

         return;
      }

      // RFC 3030 section 3: a message declared BODY=BINARYMIME MUST be sent with
      // BDAT - its content may hold bare CR, bare LF and NUL octets, none of which
      // survive the line-oriented, dot-terminated DATA path (a NUL-free guarantee
      // is not even the client's to make once it declared binary). The RFC
      // prescribes 503 for a DATA in this situation. The transaction itself stays
      // intact: the client may continue with BDAT.
      if (binarymime_requested_)
      {
         SendResponse_(503, _T("5.5.1"), _T("Bad sequence of commands: DATA is not permitted with BODY=BINARYMIME; use BDAT (RFC 3030)."));
         return;
      }

      // Let's add an event call on DATA so we can act on reception during SMTP conversation..
      if (Configuration::Instance()->GetUseScriptServer())
      {
         std::shared_ptr<ScriptObjectContainer> pContainer = std::shared_ptr<ScriptObjectContainer>(new ScriptObjectContainer);
         std::shared_ptr<Result> pResult = std::shared_ptr<Result>(new Result);
         std::shared_ptr<ClientInfo> pClientInfo = std::shared_ptr<ClientInfo>(new ClientInfo);

         pClientInfo->SetUsername(username_);
         pClientInfo->SetIPAddress(GetIPAddressString());
         pClientInfo->SetPort(GetLocalEndpointPort());
         pClientInfo->SetSessionID(GetSessionID());
         pClientInfo->SetHELO(helo_host_);
         pClientInfo->SetIsAuthenticated(isAuthenticated_);
         pClientInfo->SetIsEncryptedConnection(IsSSLConnection());
         if (IsSSLConnection())
         {
            auto cipher_info = GetCipherInfo();
            pClientInfo->SetCipherVersion(cipher_info.GetVersion().c_str());
            pClientInfo->SetCipherName(cipher_info.GetName().c_str());
            pClientInfo->SetCipherBits(cipher_info.GetBits());
         }

         pContainer->AddObject("HMAILSERVER_MESSAGE", current_message_, ScriptObject::OTMessage);
         pContainer->AddObject("HMAILSERVER_CLIENT", pClientInfo, ScriptObject::OTClient);
         pContainer->AddObject("Result", pResult, ScriptObject::OTResult);

         String sEventCaller = "OnSMTPData(HMAILSERVER_CLIENT, HMAILSERVER_MESSAGE)";
         ScriptServer::Instance()->FireEvent(ScriptServer::EventOnSMTPData, sEventCaller, pContainer);

         switch (pResult->GetValue())
         {
         case 1:
            {
               // 5.7.1 / 4.7.0: rejected by local policy (a script).
               SendResponse_(554, _T("5.7.1"), _T("Rejected"));
               LogAwstatsMessageRejected_();
               return;
            }
         case 2:
            {
               SendResponse_(554, _T("5.7.1"), pResult->GetMessage());
               LogAwstatsMessageRejected_();
               return;
            }
         case 3:
            {
               SendResponse_(453, _T("4.7.0"), pResult->GetMessage());
               LogAwstatsMessageRejected_();
               return;
            }
         }
      }      

      transmission_buffer_ = std::shared_ptr<TransparentTransmissionBuffer>(new TransparentTransmissionBuffer(false));
      if (!transmission_buffer_->Initialize(PersistentMessage::GetFileName(current_message_)))
      {
         // Stay in command mode: the client gets the 451 instead of a 354, so no
         // message payload follows. Entering DATA state here would make the next
         // line read misparse the never-sent body as SMTP commands.
         HandleUnableToSaveMessageDataFile_(PersistentMessage::GetFileName(current_message_));
         return;
      }

      transmission_buffer_->SetMaxSizeKB(max_message_size_kb_);

      current_state_ = DATA;
      SetReceiveBinary(true);
      trace_headers_written_ = true;
      message_start_tc_ = GetTickCount();

      EnqueueWrite_("354 OK, send.");
   }

   void
   SMTPConnection::ProtocolBDAT_(const String &sRequest)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // RFC 3030 CHUNKING. Handles "BDAT <chunk-size> [LAST]". The chunk-size octets
   // that follow the command line are read verbatim (byte-transparent: no SMTP
   // dot-unstuffing and no \r\n.\r\n end-of-data sequence) and appended to the same
   // spool file across all chunks of the transaction. "LAST" finalizes the message.
   //---------------------------------------------------------------------------()
   {
      // Parse "BDAT <chunk-size> [LAST]" before any rejection check: the client sends
      // the chunk payload without waiting for our reply (RFC 3030), so a rejected
      // command with a parseable size must still consume the payload to stay
      // synchronized, and a command whose size cannot be determined leaves the
      // session unrecoverable (the only safe action is to close it).
      std::vector<String> tokens = StringParser::SplitString(sRequest, " ");
      if (tokens.size() < 2 || tokens.size() > 3)
      {
         SendResponse_(501, _T("5.5.4"), _T("Syntax: BDAT chunk-size [LAST]"));
         pending_disconnect_ = true;
         EnqueueDisconnect();
         return;
      }

      String sizeToken = tokens[1].Trim();
      if (sizeToken.IsEmpty() || sizeToken.GetLength() > 18)
      {
         // Empty or implausibly large (> 10^18) chunk size.
         SendResponse_(501, _T("5.5.4"), _T("Syntax error: invalid BDAT chunk-size."));
         pending_disconnect_ = true;
         EnqueueDisconnect();
         return;
      }

      unsigned __int64 chunkSize = 0;
      for (int i = 0; i < sizeToken.GetLength(); i++)
      {
         TCHAR c = sizeToken[i];
         if (c < '0' || c > '9')
         {
            SendResponse_(501, _T("5.5.4"), _T("Syntax error: BDAT chunk-size must be a non-negative integer."));
            pending_disconnect_ = true;
            EnqueueDisconnect();
            return;
         }
         chunkSize = chunkSize * 10 + (unsigned __int64)(c - '0');
      }

      bool isLast = false;
      if (tokens.size() == 3)
      {
         String lastToken = tokens[2].Trim();
         lastToken.MakeUpper();
         if (lastToken != _T("LAST"))
         {
            SendResponse_(501, _T("5.5.4"), _T("Syntax: BDAT chunk-size [LAST]"));
            pending_disconnect_ = true;
            EnqueueDisconnect();
            return;
         }
         isLast = true;
      }

      // From here on the chunk size is known, so every rejection discards the
      // in-flight payload instead of letting it be parsed as SMTP commands.

      // 530 Must issue STARTTLS first
      // to every command other than NOOP, EHLO, STARTTLS, or QUIT.
      if (!CheckStartTlsRequired_())
      {
         StartBdatDiscard_((size_t)chunkSize);
         return;
      }

      if (!current_message_ || current_message_->GetRecipients()->GetCount() == 0)
      {
         // BDAT issued without a sender and at least one recipient.
         SendResponse_(503, _T("5.5.1"), _T("Must have sender and recipient first."));
         StartBdatDiscard_((size_t)chunkSize);
         return;
      }

      // On the first BDAT of the transaction, open the spool file in byte-transparent
      // (binary) mode.
      if (!bdat_active_)
      {
         transmission_buffer_ = std::shared_ptr<TransparentTransmissionBuffer>(new TransparentTransmissionBuffer(false));
         transmission_buffer_->SetBinaryMode(true);

         if (!transmission_buffer_->Initialize(PersistentMessage::GetFileName(current_message_)))
         {
            HandleUnableToSaveMessageDataFile_(PersistentMessage::GetFileName(current_message_));
            StartBdatDiscard_((size_t)chunkSize);
            return;
         }

         transmission_buffer_->SetMaxSizeKB(max_message_size_kb_);
         trace_headers_written_ = true;
         message_start_tc_ = GetTickCount();
         bdat_active_ = true;
      }

      bdat_last_ = isLast;
      bdat_chunk_size_ = (size_t)chunkSize;
      bdat_chunk_remaining_ = (size_t)chunkSize;

      if (chunkSize == 0)
      {
         // Zero-length chunk: there is no payload to read.
         if (isLast)
         {
            // End of message. Suppress the line read in ParseData() and finalize.
            current_state_ = BDATDATA;
            CompleteBdatMessage_();
         }
         else
         {
            SendResponse_(250, _T("2.0.0"), _T("0 octets received"));
         }
         return;
      }

      // Read the chunk payload, byte-for-byte, in memory-bounded pieces.
      current_state_ = BDATDATA;
      SetReceiveBinary(true);

      size_t toRead = bdat_chunk_remaining_ < BDAT_READ_PIECE ? bdat_chunk_remaining_ : BDAT_READ_PIECE;
      EnqueueReadExact(toRead);
   }

   void
   SMTPConnection::StartBdatDiscard_(size_t chunkSize)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // The BDAT command was rejected (its response is already enqueued), but the
   // client is streaming the announced chunk regardless (RFC 3030). Consume and
   // discard exactly that many octets so the payload is never parsed as SMTP
   // commands, then return to command mode.
   //---------------------------------------------------------------------------()
   {
      if (chunkSize == 0)
      {
         // Zero-length chunk: no payload follows; stay in command mode.
         return;
      }

      bdat_discard_ = true;
      bdat_chunk_remaining_ = chunkSize;

      current_state_ = BDATDATA;
      SetReceiveBinary(true);

      size_t toRead = chunkSize < BDAT_READ_PIECE ? chunkSize : BDAT_READ_PIECE;
      EnqueueReadExact(toRead);
   }

   void
   SMTPConnection::HandleBdatChunkData_(std::shared_ptr<ByteBuffer> pBuf)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Consumes the octets of the current BDAT chunk (delivered exactly chunk-by-piece
   // by the binary read path) and, when the chunk completes, either acknowledges it
   // (non-final) or finalizes the message (LAST).
   //---------------------------------------------------------------------------()
   {
      size_t received = pBuf->GetSize();

      if (bdat_discard_)
      {
         // Chunk of a rejected BDAT command: throw the octets away. The command's
         // error response has already been sent.
         pBuf->Empty();

         if (received >= bdat_chunk_remaining_)
            bdat_chunk_remaining_ = 0;
         else
            bdat_chunk_remaining_ -= received;

         if (bdat_chunk_remaining_ > 0)
         {
            size_t toRead = bdat_chunk_remaining_ < BDAT_READ_PIECE ? bdat_chunk_remaining_ : BDAT_READ_PIECE;
            EnqueueReadExact(toRead);
            return;
         }

         bdat_discard_ = false;
         SetReceiveBinary(false);
         current_state_ = HEADER;
         EnqueueRead();
         return;
      }

      transmission_buffer_->Append(pBuf->GetBuffer(), received);
      pBuf->Empty();

      if (received >= bdat_chunk_remaining_)
         bdat_chunk_remaining_ = 0;
      else
         bdat_chunk_remaining_ -= received;

      // Enforce the same hard drop ceiling used by the DATA path.
      size_t iBufSizeKB = transmission_buffer_->GetSize() / 1024;
      size_t iMaxSizeDrop = IniFileSettings::Instance()->GetSMTPDMaxSizeDrop();
      if (iMaxSizeDrop > 0 && iBufSizeKB >= iMaxSizeDrop)
      {
         String sLogData;
         sLogData.Format(_T("Size: %d KB, Max size: %d KB - DROP!!"), iBufSizeKB, iMaxSizeDrop);
         LOG_SMTP(GetSessionID(), GetIPAddressString(), sLogData);

         String sMessage;
         sMessage.Format(_T("Message size exceeds the drop maximum message size. Size: %d KB, Max size: %d KB - DROP!"),
            iBufSizeKB, iMaxSizeDrop);
         SendResponse_(552, _T("5.3.4"), sMessage);
         LogAwstatsMessageRejected_();
         ResetCurrentMessage_();
         SetReceiveBinary(false);
         pending_disconnect_ = true;
         EnqueueDisconnect();
         return;
      }

      // Bound memory: flush to the spool file as the buffer grows, prepending the
      // trace headers on the first flush (exactly as the DATA path does).
      if (transmission_buffer_->GetRequiresFlush())
      {
         AppendMessageHeaders_();
         transmission_buffer_->Flush();
      }

      if (bdat_chunk_remaining_ > 0)
      {
         // More of the current chunk is still to be received.
         size_t toRead = bdat_chunk_remaining_ < BDAT_READ_PIECE ? bdat_chunk_remaining_ : BDAT_READ_PIECE;
         EnqueueReadExact(toRead);
         return;
      }

      // The current chunk has been fully received.
      if (bdat_last_)
      {
         CompleteBdatMessage_();
         return;
      }

      // Non-final chunk: acknowledge it and return to command (line) mode so the next
      // BDAT (or RSET) can be read.
      String sResp;
      sResp.Format(_T("%I64u octets received"), (unsigned __int64)bdat_chunk_size_);
      SendResponse_(250, _T("2.0.0"), sResp);

      SetReceiveBinary(false);
      current_state_ = HEADER;
      EnqueueRead();
   }

   void
   SMTPConnection::CompleteBdatMessage_()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Finalizes a BDAT (CHUNKING) message after the LAST chunk has been received:
   // prepends the trace headers (if not already written), closes the spool file and
   // runs the shared accept/save/queue pipeline (which emits the final 250 reply).
   //---------------------------------------------------------------------------()
   {
      // Ensure the message trace headers are prepended before the spool is closed.
      AppendMessageHeaders_();

      transmission_buffer_->MarkTransmissionEnded();
      transmission_buffer_->Flush(true);

      // Run the (potentially time-consuming) accept/save/queue work asynchronously.
      finalization_enqueued_tick_ = GetTickCount64();
      std::shared_ptr<AsynchronousTask<TCPConnection> > finalizationTask =
         std::shared_ptr<AsynchronousTask<TCPConnection> >(new AsynchronousTask<TCPConnection>
            (std::bind(&SMTPConnection::HandleSMTPFinalizationTaskCompleted_, this), shared_from_this(),
             Formatter::Format("SMTP-accept session={0} ip={1}", GetSessionID(), GetIPAddressString())));

      // TaskMayBlock - the BDAT path into the same handler. See the equivalent above for
      // why, for the two caller constraints that were checked first, and for why the
      // queue is checked for rather than assumed.
      std::shared_ptr<WorkQueue> asyncQueue = Application::Instance()->GetAsyncWorkQueue();

      if (!asyncQueue)
      {
         SendResponse_(451, _T("4.3.2"), _T("The server is shutting down and cannot accept the message. Please retry later."));
         return;
      }

      asyncQueue->AddTask(finalizationTask, WorkQueue::TaskMayBlock);
   }

   bool
   SMTPConnection::CheckStartTlsRequired_()
   {
      if (GetConnectionSecurity() == CSSTARTTLSRequired &&
          !IsSSLConnection())
      {
         SendErrorResponse_(530, "Must issue STARTTLS first.");
         return false;
      }

      return true;
   }

   void 
   SMTPConnection::ProtocolAUTH_(const String &sRequest)
   {
      // 530 Must issue STARTTLS first
      // to every command other than NOOP, EHLO, STARTTLS, or QUIT.
      if (!CheckStartTlsRequired_())
         return;

      if (!GetAuthIsEnabled_())
      {
         SendErrorResponse_(504, "Authentication not enabled.");
         return;
      }

      if (GetSecurityRange()->GetRequireTLSForAuth() && !IsSSLConnection())
      {
         SendErrorResponse_(530, "A SSL/TLS-connection is required for authentication.");
         return;
      }
	  
      // rfc4954 restrictions, After a successful AUTH command completes, 
      // a server MUST reject any further AUTH commands with a 503 reply.
      if (isAuthenticated_) 
      {
         SendErrorResponse_(503, "Already authenticated.");
         return;
      }

      requestedAuthenticationType_ = AUTH_NONE;

      std::vector<String> vecParams = StringParser::SplitString(sRequest,  " ");

      if (vecParams.size() == 1)
      {
         SendErrorResponse_(504, "Authentication type not specified.");
         return;
      }
      
      String sAuthenticationType = vecParams[1];
      sAuthenticationType.MakeUpper();

      if (sAuthenticationType == _T("LOGIN"))
      {
         requestedAuthenticationType_ = AUTH_LOGIN;

         String sResponse;

         if (vecParams.size() == 3)
         {
            // Fetch username from third parameter.
            StringParser::Base64Decode(vecParams[2], username_);
            current_state_ = SMTPUPASSWORD;

            StringParser::Base64Encode("Password:", sResponse);
         }
         else
         {
            current_state_ = SMTPUSERNAME;
            StringParser::Base64Encode("Username:", sResponse);
         }

         EnqueueWrite_("334 " + sResponse);
         return;

      }
      else if (sAuthenticationType == _T("PLAIN") && 
               smtpconf_->GetAuthAllowPlainText())
      {
         requestedAuthenticationType_ = AUTH_PLAIN;

         // Stupid user has selected plain text authentication.
         if (vecParams.size() == 3)
         {
            // Fetch username and password directly from command.
            AuthenticateUsingPLAIN_(vecParams[2]);
         }
         else
         {
            EnqueueWrite_("334 Log on");
            current_state_ = SMTPUSERNAME;
         }

         return;
      }
      else if (sAuthenticationType == _T("SCRAM-SHA-256-PLUS"))
      {
         // Channel binding only has meaning over TLS; the mechanism is advertised
         // (and accepted) only on a TLS connection.
         if (!IsSSLConnection())
         {
            SendErrorResponse_(504, "SCRAM-SHA-256-PLUS requires a TLS connection.");
            return;
         }

         // Bind the exchange to this TLS channel via the server certificate
         // (RFC 5929 tls-server-end-point).
         std::vector<unsigned char> cbindData;
         if (!GetTlsServerEndPoint(cbindData))
         {
            SendErrorResponse_(504, "Channel binding is not available on this connection.");
            return;
         }

         requestedAuthenticationType_ = AUTH_SCRAM_SHA256;

         scram_session_ = std::make_shared<ScramSha256>();
         scram_session_->SetChannelBinding(cbindData);

         // RFC 4954: a SASL-IR of "=" means an empty initial response. SCRAM never
         // sends an empty client-first, so treat "=" as "no initial response".
         if (vecParams.size() >= 3 && vecParams[2] != _T("="))
         {
            ProtocolScramClientFirst_(vecParams[2]);
         }
         else
         {
            // Empty server challenge asks the client for the client-first message.
            EnqueueWrite_("334 ");
            current_state_ = SMTPSCRAMFIRST;
         }

         return;
      }
      else if (sAuthenticationType == _T("SCRAM-SHA-256"))
      {
         requestedAuthenticationType_ = AUTH_SCRAM_SHA256;

         scram_session_ = std::make_shared<ScramSha256>();

         // On a TLS connection the server also advertises SCRAM-SHA-256-PLUS, so a
         // non-PLUS client that sends the 'y' gs2 flag is signalling a stripped-PLUS
         // downgrade and is rejected (RFC 5802 section 6).
         if (IsSSLConnection())
            scram_session_->SetServerSupportsChannelBinding();

         // RFC 4954: a SASL-IR of "=" means an empty initial response. SCRAM never
         // sends an empty client-first, so treat "=" as "no initial response".
         if (vecParams.size() >= 3 && vecParams[2] != _T("="))
         {
            ProtocolScramClientFirst_(vecParams[2]);
         }
         else
         {
            // Empty server challenge asks the client for the client-first message.
            EnqueueWrite_("334 ");
            current_state_ = SMTPSCRAMFIRST;
         }

         return;
      }

      if (sAuthenticationType == _T("XOAUTH2") || sAuthenticationType == _T("OAUTHBEARER"))
      {
         if (!OAuth2TokenValidator::IsEnabled())
         {
            SendErrorResponse_(504, "Authentication mechanism not supported.");
            return;
         }

         if (OAuth2TokenValidator::RequireTLS() && !IsSSLConnection())
         {
            SendErrorResponse_(530, "A SSL/TLS-connection is required for authentication.");
            return;
         }

         requestedAuthenticationType_ = AUTH_BEARER;

         if (vecParams.size() >= 3)
         {
            // Initial response supplied inline with the AUTH command.
            AuthenticateUsingBearer_(vecParams[2]);
         }
         else
         {
            // Empty server challenge asks the client for the SASL message.
            EnqueueWrite_("334 ");
            current_state_ = SMTPBEARERRESPONSE;
         }

         return;
      }

      if (sAuthenticationType == _T("EXTERNAL"))
      {
         // Offered only on a connection whose client certificate verified and names an
         // address (see the EHLO response); asked for on any other, there is nothing
         // to authenticate with, and the answer is the one every unoffered mechanism
         // gets.
         if (GetVerifiedClientCertificateIdentities().empty())
         {
            SendErrorResponse_(504, "Authentication mechanism not supported.");
            return;
         }

         requestedAuthenticationType_ = AUTH_EXTERNAL;

         if (vecParams.size() >= 3)
         {
            // Initial response supplied inline with the AUTH command - "=" for an
            // empty one (RFC 4954 section 4).
            AuthenticateUsingExternal_(vecParams[2]);
         }
         else
         {
            EnqueueWrite_("334 ");
            current_state_ = SMTPEXTERNALRESPONSE;
         }

         return;
      }

      SendErrorResponse_(504, "Authentication mechanism not supported.");
   }

   void 
   SMTPConnection::ProtocolUsername_(const String &sRequest)
   {
      StringParser::Base64Decode(sRequest, username_);
      String sEncoded;
      StringParser::Base64Encode("Password:", sEncoded);
      EnqueueWrite_("334 " + sEncoded);
      current_state_ = SMTPUPASSWORD;

   }

   void 
   SMTPConnection::ProtocolPassword_(const String &sRequest)
   {
      if (requestedAuthenticationType_ == AUTH_LOGIN)
         StringParser::Base64Decode(sRequest, password_);
      else if (requestedAuthenticationType_ == AUTH_PLAIN)
         password_ = sRequest;

      Authenticate_();
   }

   void 
   SMTPConnection::ProtocolSTARTTLS_(const String &sRequest)
   {
      if (GetConnectionSecurity() == CSSTARTTLSOptional ||
          GetConnectionSecurity() == CSSTARTTLSRequired)
      {
         // RFC 3207 section 4: "A client MUST NOT attempt to start a TLS session if a
         // TLS session is already active", and the server has to refuse if one does.
         // The EHLO half of that rule was already honoured - SendEHLOKeywords_ omits
         // STARTTLS once IsSSLConnection() is true - but the command itself was still
         // accepted, and start_tls_used_ was set for this purpose and then never read.
         //
         // Accepting it does not merely breach the RFC, it wedges the session: the
         // server answers 220, sets current_state_ = STARTTLS (so ParseData enqueues no
         // read) and starts a second handshake INSIDE the established one. OpenSSL then
         // tries to parse the client's next application bytes as a ClientHello, the
         // handshake fails, and SMTPConnection::OnHandshakeFailed is empty - so nothing
         // re-arms a read and nothing disconnects. The socket and its session slot are
         // held until the idle timeout expires, which is up to ten minutes, for the
         // cost of one command.
         if (start_tls_used_ || IsSSLConnection())
         {
            SendErrorResponse_(503, "STARTTLS is not allowed: a TLS session is already active.");
            return;
         }

         const int commandLength = 8;

         auto trimmedRequest = sRequest;
         trimmedRequest.Trim();

         bool hasParameters = trimmedRequest.GetLength() > commandLength;

         if (hasParameters)
         {
            SendErrorResponse_(501, "Syntax error (no parameters allowed)");
            return;
         }

         EnqueueWrite_("220 Ready to start TLS");

         current_state_ = STARTTLS;

         EnqueueHandshake();
      }
      else
      {
         SendErrorResponse_(503, "Bad sequence of commands");
      }
   }

   bool
   SMTPConnection::RelayToRemotePermittedWithoutAuth_()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // True when the connecting IP is in a security range which both permits relaying
   // to a remote destination and does not require SMTP authentication for it. This
   // is the same pair of decisions RCPT TO makes (GetAllowOption + the matching
   // RequireSMTPAuth* flag), just without a concrete envelope to classify.
   //---------------------------------------------------------------------------()
   {
      std::shared_ptr<SecurityRange> securityRange = GetSecurityRange();

      // No matching range means nothing has granted this client anything: deny.
      if (!securityRange)
         return false;

      if (securityRange->GetAllowOption(SecurityRange::IPRANGE_RELAY_REMOTE_TO_REMOTE) &&
          !securityRange->GetRequireSMTPAuthExternalToExternal())
         return true;

      if (securityRange->GetAllowOption(SecurityRange::IPRANGE_RELAY_LOCAL_TO_REMOTE) &&
          !securityRange->GetRequireSMTPAuthLocalToExternal())
         return true;

      return false;
   }

   void
   SMTPConnection::ProtocolETRN_(const String &sRequest)
   {
      // RFC ETRN Codes
      //   250 OK, queuing for node <x> started
      //   251 OK, no messages waiting for node <x>
      //   252 OK, pending messages for node <x> started
      //   253 OK, <n> pending messages for node <x> started
      //   458 Unable to queue messages for node <x>
      //   459 Node <x> not allowed: <reason>
      //   500 Syntax Error
      //   501 Syntax Error in Parameters

      // 530 Must issue STARTTLS first
      // to every command other than NOOP, EHLO, STARTTLS, or QUIT. ETRN used to be
      // missing from this list, so on a STARTTLS-required port a remote party could
      // trigger a queue flush before the session was encrypted.
      if (!CheckStartTlsRequired_())
         return;

      // ETRN makes the server release queued mail towards a route, i.e. it asks for
      // relaying to be performed on the client's behalf. Before anything happens the
      // client must have earned that right: it has either authenticated, or it
      // connects from a security range which is allowed to relay to a remote
      // destination without authenticating. Previously ETRN performed no check at
      // all, so any anonymous client could flush the queue at will.
      if (!isAuthenticated_ && !RelayToRemotePermittedWithoutAuth_())
      {
         SendErrorResponse_(530, "SMTP authentication is required.");
         LOG_SMTP(GetSessionID(), GetIPAddressString(), "SMTPDeliverer - ETRN - 530 Client is neither authenticated nor permitted to relay.");
         return;
      }

      std::vector<String> vecParams = StringParser::SplitString(sRequest,  " ");

      // We need at least 1 parameter. ETRN alone results in error
      if (vecParams.size() == 1)
      {
         SendErrorResponse_(500, "Syntax Error: No domain parameter included");
         LOG_SMTP(GetSessionID(), GetIPAddressString(), "SMTPDeliverer - ETRN - No domain parameter included");      
         return;
      }
      
      String sResponse;
      String sETRNDomain = vecParams[1];
      String sETRNDomain2 = sETRNDomain.ToLower();
      String sLogData;

      bool bIsRouteDomain = false;
      std::shared_ptr<Route> route = Configuration::Instance()->GetSMTPConfiguration()->GetRoutes()->GetItemByNameWithWildcardMatch(sETRNDomain.ToLower());

      // See if sender supplied param matches one of our domains
      if (route && route->GetName() == sETRNDomain2)
      {
         LOG_SMTP(GetSessionID(), GetIPAddressString(), "SMTPDeliverer - ETRN - Route found, continuing..");      

         std::shared_ptr<Routes> pRoutes = Configuration::Instance()->GetSMTPConfiguration()->GetRoutes();
         std::shared_ptr<Route> pRoute = pRoutes->GetItemByNameWithWildcardMatch(sETRNDomain.ToLower());

         if (pRoute)
         {
            __int64 iRouteID = pRoute->GetID();

            LOG_SMTP(GetSessionID(), GetIPAddressString(), "SMTPDeliverer - ETRN - Route settings read successfully.");      

            int lTmpNoOfRetries = pRoute->NumberOfTries();
            int lTmpMinutesBetween = pRoute->MinutesBetweenTry();

            // Here we change ID back to 0, type back to 1 & next try to ASAP for Route ID
            // Special 1901-01-01 00:00:01 tells admin it is HOLD
            SQLCommand command("update hm_messages set messageaccountid = 0, messagetype = 1, messagenexttrytime = '1901-01-01 00:00:00' where messagetype = 3 and messageaccountid = @ROUTEID");
            command.AddParameter("@ROUTEID", iRouteID);
            if (Application::Instance()->GetDBManager()->Execute(command))
            {
               // Need to tell hmail to reload the settings
               //Configuration::Instance()->Load();
               SendResponse_(250, _T("2.0.0"), _T("OK, message queuing started for ") + sETRNDomain.ToLower());
               LOG_SMTP(GetSessionID(), GetIPAddressString(), "SMTPDeliverer - ETRN - 250 OK, message queuing started.");      
            }
            else
            {
               SendResponse_(458, _T("4.3.0"), _T("Unable to queue messages for ") + sETRNDomain.ToLower());
               LOG_SMTP(GetSessionID(), GetIPAddressString(), "SMTPDeliverer - ETRN - 458 Unable to queue messages");      
            }
         return;

       }
       else
       {
          // Send that we don't accept ETRN for that domain or invalid param
          SendResponse_(458, _T("4.3.0"), _T("Error getting info for ") + sETRNDomain.ToLower());
          LOG_SMTP(GetSessionID(), GetIPAddressString(), "SMTPDeliverer - ETRN - Could not get Route values");      
          return;
       }
     }
     else
     {
         // Send that we don't accept ETRN for that domain or invalid param
         SendResponse_(501, _T("5.5.4"), _T("ETRN not supported for ") + sETRNDomain.ToLower());
         LOG_SMTP(GetSessionID(), GetIPAddressString(), "SMTPDeliverer - ETRN - Domain is not Route");      
         return;
     }
   }

   void
   SMTPConnection::AuthenticateUsingPLAIN_(const String &sLine)
   {
      // SASL PLAIN (RFC 4616): authzid NUL authcid NUL passwd, UTF-8 encoded.
      String sAuthzid;
      if (!StringParser::DecodeSaslPlain(sLine, sAuthzid, username_, password_))
      {
         RestartAuthentication_();
         return;
      }

      // RFC 4422/4013: prepare the authcid (username) with SASLprep before lookup.
      String sPreppedUser;
      if (StringParser::SaslPrep(username_, sPreppedUser))
         username_ = sPreppedUser;

      // Authenticate the user.
      Authenticate_();      
   }

   void
   SMTPConnection::AuthenticateUsingBearer_(const String &sLine)
   {
      // A bare "*" cancels the SASL exchange (RFC 4954).
      if (sLine == _T("*"))
      {
         ResetLoginCredentials_();
         SendErrorResponse_(501, "Authentication cancelled.");
         return;
      }

      // The XOAUTH2 / OAUTHBEARER client response is ASCII, so the standard base64
      // decode is sufficient here.
      String sDecodedW;
      StringParser::Base64Decode(sLine, sDecodedW);
      AnsiString sDecoded = sDecodedW;

      AnsiString sIdentity, sToken;
      String sTokenUser;
      bool authenticated = false;
      if (OAuth2TokenValidator::ParseSaslBearer(sDecoded, sIdentity, sToken))
      {
         AnsiString sError;
         if (OAuth2TokenValidator::ValidateBearerToken(sToken, sTokenUser, sError))
         {
            // When the client also asserts an identity it must match the token.
            AnsiString sTokenUserA = sTokenUser;
            if (sIdentity.IsEmpty() || sIdentity.CompareNoCase(sTokenUserA) == 0)
               authenticated = true;
         }
      }

      std::shared_ptr<const Account> pAccount;
      String sLoginName = sTokenUser;
      if (authenticated)
      {
         std::shared_ptr<DomainAliases> pDA = ObjectCache::Instance()->GetDomainAliases();
         String sAddress = pDA->ApplyAliasesOnAddress(sTokenUser);
         sAddress = DefaultDomain::ApplyDefaultDomain(sAddress);
         sLoginName = sAddress;
         pAccount = LookupActiveAccount_(sAddress);
      }

      username_ = sLoginName;

      isAuthenticated_ = pAccount != nullptr;

      FireOnClientLogon_(sLoginName, isAuthenticated_);

      if (pAccount)
      {
         SendResponse_(235, _T("2.7.0"), _T("authenticated."));
         current_state_ = HEADER;
         return;
      }

      // Feed the per-IP auto-ban accounting, then apply the per-connection cap -
      // deliberately not the per-name lockout: a bearer token is not guessable,
      // and a client looping on an expired one would lock its own user out of
      // every password-based client. See AccountLogon.h.
      AccountLogon accountLogon;
      bool disconnect = false;
      accountLogon.RegisterFailedLogin(GetRemoteEndpointAddress(), sLoginName, disconnect, false);

      authentication_failure_count_++;
      TarpitFailedLogon_();

      if (disconnect || authentication_failure_count_ >= 10)
      {
         SendErrorResponse_(535, "Authentication failed. Too many invalid logon attempts.");
         pending_disconnect_ = true;
         EnqueueDisconnect();
         return;
      }

      RestartAuthentication_();
   }

   void
   SMTPConnection::AuthenticateUsingExternal_(const String &sLine)
   {
      // A bare "*" cancels the SASL exchange (RFC 4954).
      if (sLine == _T("*"))
      {
         ResetLoginCredentials_();
         SendErrorResponse_(501, "Authentication cancelled.");
         return;
      }

      // The response is the authorization identity the client wants, base64, or
      // nothing at all - an empty continuation line, or "=" as the inline form of an
      // empty initial response - for "whoever the certificate says".
      String sAuthzid;
      if (!sLine.IsEmpty() && sLine != _T("="))
         StringParser::Base64Decode(sLine, sAuthzid);

      String sLoginName;
      bool disconnect = false;
      std::shared_ptr<const Account> pAccount = ClientCertificateIdentity::Logon(
         GetVerifiedClientCertificateIdentities(), sAuthzid, GetRemoteEndpointAddress(), sLoginName, disconnect);

      username_ = sLoginName;
      isAuthenticated_ = pAccount != nullptr;

      FireOnClientLogon_(sLoginName, isAuthenticated_);

      if (pAccount)
      {
         SendResponse_(235, _T("2.7.0"), _T("authenticated."));
         current_state_ = HEADER;
         return;
      }

      // The per-IP accounting has been fed by Logon; this is the per-connection cap,
      // the same one every other mechanism applies.
      authentication_failure_count_++;
      TarpitFailedLogon_();

      if (disconnect || authentication_failure_count_ >= 10)
      {
         SendErrorResponse_(535, "Authentication failed. Too many invalid logon attempts.");
         pending_disconnect_ = true;
         EnqueueDisconnect();
         return;
      }

      RestartAuthentication_();
   }

   std::shared_ptr<const Account>
   SMTPConnection::LookupActiveAccount_(const String &sAddress)
   {
      // OAuth2 bearer login: the token is the proof of identity, so any active account
      // in an active domain is eligible regardless of its stored password hash type.
      String sAccountAddress = DefaultDomain::ApplyDefaultDomain(sAddress);

      std::shared_ptr<const Account> pAccount = CacheContainer::Instance()->GetAccount(sAccountAddress);
      if (!pAccount || !pAccount->GetActive())
         return std::shared_ptr<const Account>();

      String sDomain = StringParser::ExtractDomain(sAccountAddress);
      std::shared_ptr<const Domain> pDomain = CacheContainer::Instance()->GetDomain(sDomain);
      if (!pDomain || !pDomain->GetIsActive())
         return std::shared_ptr<const Account>();

      return pAccount;
   }

   void
   SMTPConnection::Authenticate_()
   {
      AccountLogon accountLogon;
      bool disconnect;
	  String sUsername = username_;

      std::shared_ptr<const Account> pAccount = accountLogon.Logon(GetRemoteEndpointAddress(), username_, password_, disconnect);
         
      if (disconnect)
      {
         SendErrorResponse_(535, "Authentication failed. Too many invalid logon attempts.");
         pending_disconnect_ = true;
         EnqueueDisconnect();
         return;
      }

      isAuthenticated_ = pAccount != nullptr;

      FireOnClientLogon_(sUsername, isAuthenticated_);
     
      if (pAccount)
      {
         SendResponse_(235, _T("2.7.0"), _T("authenticated."));
         current_state_ = HEADER;
      }
      else
      {
         authentication_failure_count_++;
         TarpitFailedLogon_();

         // Defense-in-depth on top of the per-IP auto-ban: never let a single
         // connection make an unbounded number of authentication attempts.
         if (authentication_failure_count_ >= 10)
         {
            SendErrorResponse_(535, "Authentication failed. Too many invalid logon attempts.");
            pending_disconnect_ = true;
            EnqueueDisconnect();
            return;
         }

         RestartAuthentication_();
      }
   }

   void
   SMTPConnection::FireOnClientLogon_(const String &sUsername, bool isAuthenticated)
   {
      if (Configuration::Instance()->GetUseScriptServer())
      {
         std::shared_ptr<ScriptObjectContainer> pContainer = std::shared_ptr<ScriptObjectContainer>(new ScriptObjectContainer);
         std::shared_ptr<ClientInfo> pClientInfo = std::shared_ptr<ClientInfo>(new ClientInfo);

         pClientInfo->SetUsername(sUsername);
         pClientInfo->SetIPAddress(GetIPAddressString());
         pClientInfo->SetPort(GetLocalEndpointPort());
         pClientInfo->SetSessionID(GetSessionID());
         pClientInfo->SetHELO(helo_host_);
         pClientInfo->SetIsAuthenticated(isAuthenticated);
         pClientInfo->SetIsEncryptedConnection(IsSSLConnection());
         if (IsSSLConnection())
         {
            auto cipher_info = GetCipherInfo();
            pClientInfo->SetCipherVersion(cipher_info.GetVersion().c_str());
            pClientInfo->SetCipherName(cipher_info.GetName().c_str());
            pClientInfo->SetCipherBits(cipher_info.GetBits());
         }

         pContainer->AddObject("HMAILSERVER_CLIENT", pClientInfo, ScriptObject::OTClient);

         String sEventCaller = "OnClientLogon(HMAILSERVER_CLIENT)";
         ScriptServer::Instance()->FireEvent(ScriptServer::EventOnClientLogon, sEventCaller, pContainer);
      }
   }

   void
   SMTPConnection::RestartAuthentication_()
   {
      ResetLoginCredentials_();

      SendErrorResponse_(535, "Authentication failed. Restarting authentication process.");
   }

   void
   SMTPConnection::TarpitFailedLogon_()
   {
      // Queued, not slept: the refusal that follows, and the read after it, wait
      // behind the connection's own timer. See AccountLogon::TarpitDelaySeconds.
      EnqueueDelay(AccountLogon::TarpitDelaySeconds(authentication_failure_count_));
   }

   void
   SMTPConnection::TarpitRecipient_()
   {
      // The recipient tarpit: an unauthenticated session that keeps adding
      // recipients past SmtpTarpitCount has each further RCPT reply held for
      // SmtpTarpitDelaySeconds. Dictionary attacks and spam runs are long
      // recipient lists from strangers; a legitimate MTA sends a handful and is
      // never past the count. Sessions the range exempts from spam protection are
      // exempt from this too, as they are from every other anti-spam measure.
      if (isAuthenticated_)
         return;

      std::shared_ptr<SecurityRange> range = GetSecurityRange();
      if (range && !range->GetSpamProtection())
         return;

      const int count = IniFileSettings::Instance()->GetSmtpTarpitCount();
      const int delay = IniFileSettings::Instance()->GetSmtpTarpitDelaySeconds();

      if (count <= 0 || delay <= 0 || cur_no_of_rcptto_ <= count)
         return;

      EnqueueDelay(delay);
   }

   void 
   SMTPConnection::ResetLoginCredentials_()
   {
      requestedAuthenticationType_ = AUTH_NONE;
      isAuthenticated_ = false;
      authenticated_by_xclient_ = false;

      scram_session_.reset();

      current_state_ = HEADER;
      username_ = "";
      re_authenticate_user_ = false;
   }

   void
   SMTPConnection::ProtocolScramClientFirst_(const String &sRequest)
   {
      // A bare "*" cancels the SASL exchange (RFC 4954).
      if (sRequest == _T("*"))
      {
         scram_session_.reset();
         SendErrorResponse_(501, "Authentication cancelled.");
         current_state_ = HEADER;
         return;
      }

      String sDecoded;
      StringParser::Base64Decode(sRequest, sDecoded);
      AnsiString clientFirst = sDecoded;

      AnsiString username;
      if (!ScramSha256::ExtractUsername(clientFirst, username))
      {
         scram_session_.reset();
         SendErrorResponse_(501, "Invalid SCRAM client-first message.");
         current_state_ = HEADER;
         return;
      }

      // Canonicalize the user name the same way the PLAIN path does.
      String sUsername = username;
      if (sUsername.Find(_T("@")) == -1)
      {
         String sDefaultDomain = Configuration::Instance()->GetDefaultDomain();
         if (!sDefaultDomain.IsEmpty())
            sUsername = DefaultDomain::ApplyDefaultDomain(sUsername);
      }
      scram_session_->SetUsername(sUsername);
      username_ = sUsername;

      // Only a PBKDF2-hashed account can serve SCRAM (its stored key is the SCRAM
      // SaltedPassword). For any other account the helper runs a forced-failure
      // exchange so the protocol does not reveal whether the account exists.
      AnsiString storedHash = "";
      std::shared_ptr<const Account> pAccount = LookupPbkdf2Account_(sUsername);
      if (pAccount)
      {
         scram_session_->SetAccount(pAccount);
         storedHash = pAccount->GetPassword();
      }

      AnsiString serverFirst;
      if (!scram_session_->ProcessClientFirst(clientFirst, storedHash, serverFirst))
      {
         scram_session_.reset();
         SendErrorResponse_(501, "Invalid SCRAM client-first message.");
         current_state_ = HEADER;
         return;
      }

      String sServerFirst = serverFirst;
      String sEncoded;
      StringParser::Base64Encode(sServerFirst, sEncoded);

      EnqueueWrite_("334 " + sEncoded);
      current_state_ = SMTPSCRAMFINAL;
   }

   void
   SMTPConnection::ProtocolScramClientFinal_(const String &sRequest)
   {
      // A bare "*" cancels the SASL exchange (RFC 4954).
      if (sRequest == _T("*"))
      {
         scram_session_.reset();
         SendErrorResponse_(501, "Authentication cancelled.");
         current_state_ = HEADER;
         return;
      }

      String sDecoded;
      StringParser::Base64Decode(sRequest, sDecoded);
      AnsiString clientFinal = sDecoded;

      AnsiString serverFinal;
      if (!scram_session_->ProcessClientFinal(clientFinal, serverFinal))
      {
         ScramAuthFailed_();
         return;
      }

      String sServerFinal = serverFinal;
      String sEncoded;
      StringParser::Base64Encode(sServerFinal, sEncoded);

      // Send the server-final (v=...) as a challenge; the client acknowledges with an
      // empty line, then we complete the authentication with a 235 reply.
      EnqueueWrite_("334 " + sEncoded);
      current_state_ = SMTPSCRAMACK;
   }

   void
   SMTPConnection::FinishScramAuth_()
   {
      std::shared_ptr<const Account> pAccount;
      if (scram_session_)
         pAccount = scram_session_->GetAccount();

      String sUsername = scram_session_ ? String(scram_session_->GetUsername()) : username_;

      scram_session_.reset();

      if (!pAccount)
      {
         // Should not be reachable (we only enter the ack state on a verified proof).
         ScramAuthFailed_();
         return;
      }

      isAuthenticated_ = true;

      // The name authenticated, so its failure counters go - the same clearing
      // AccountLogon::Logon performs on the PLAIN/LOGIN path. Without it a user
      // who mistypes twice and then succeeds over SCRAM carries those failures
      // for ever, and one later slip locks them out.
      AccountLockout::Instance()->RecordSuccess(sUsername);

      FireOnClientLogon_(sUsername, true);

      SendResponse_(235, _T("2.7.0"), _T("authenticated."));
      current_state_ = HEADER;
   }

   void
   SMTPConnection::ScramAuthFailed_()
   {
      String sUsername = scram_session_ ? String(scram_session_->GetUsername()) : username_;

      // Whether there was ever a key to verify against. When the helper returned
      // no account - an unknown name, an Argon2id or Active Directory account, a
      // hash policy that excludes PBKDF2, or a name already locked - the exchange
      // was a forced failure that no password could have passed, so it is not a
      // password guess and must not count towards the per-name lockout. See the
      // fuller note in POP3Connection::ScramAuthFailed_.
      const bool verifiableAccount = scram_session_ && scram_session_->GetAccount() != nullptr;

      scram_session_.reset();

      // Feed the per-IP auto-ban accounting (parity with the LOGIN/PLAIN path) -
      // unless the exchange was doomed by the lock itself, in which case nothing
      // is reported, exactly as AccountLogon::Logon's locked branch does: the
      // owner retrying a locked name with the CORRECT password must not get their
      // address banned for everyone behind it. Not extended to the other
      // forced-failure causes (unknown name, Argon2id/AD account, hash policy),
      // which would let an attacker spray names over SCRAM for free. See the
      // fuller note in POP3Connection::ScramAuthFailed_.
      bool disconnect = false;

      if (!AccountLockout::Instance()->IsLockedOut(sUsername))
      {
         AccountLogon accountLogon;
         accountLogon.RegisterFailedLogin(GetRemoteEndpointAddress(), sUsername, disconnect, verifiableAccount);
      }

      authentication_failure_count_++;
      TarpitFailedLogon_();

      // Per-connection brute-force cap (effective even when auto-ban is disabled).
      if (disconnect || authentication_failure_count_ >= 10)
      {
         SendErrorResponse_(535, "Authentication failed. Too many invalid logon attempts.");
         pending_disconnect_ = true;
         EnqueueDisconnect();
         return;
      }

      SendErrorResponse_(535, "Authentication failed.");
      current_state_ = HEADER;
   }

   std::shared_ptr<const Account>
   SMTPConnection::LookupPbkdf2Account_(const String &sAddress)
   {
      std::shared_ptr<DomainAliases> pDA = ObjectCache::Instance()->GetDomainAliases();
      String sAccountAddress = pDA->ApplyAliasesOnAddress(sAddress);
      sAccountAddress = DefaultDomain::ApplyDefaultDomain(sAccountAddress);

      // A locked name is treated exactly as an account that cannot serve SCRAM:
      // the empty handle runs the forced-failure exchange documented at the call
      // site, so the lock is enforced here without a second refusal shape for an
      // attacker to tell apart. Without it the lock bound only the PLAIN/LOGIN
      // path, so an attacker chose SCRAM and guessed freely while their attempts
      // still locked the victim out of every password client.
      if (AccountLockout::Instance()->IsLockedOut(sAccountAddress))
         return std::shared_ptr<const Account>();

      std::shared_ptr<const Account> pAccount = CacheContainer::Instance()->GetAccount(sAccountAddress);
      if (!pAccount || !pAccount->GetActive())
         return std::shared_ptr<const Account>();

      // Active Directory accounts authenticate via SSPI, not a stored hash.
      if (pAccount->GetIsAD())
         return std::shared_ptr<const Account>();

      String sDomain = StringParser::ExtractDomain(sAccountAddress);
      std::shared_ptr<const Domain> pDomain = CacheContainer::Instance()->GetDomain(sDomain);
      if (!pDomain || !pDomain->GetIsActive())
         return std::shared_ptr<const Account>();

      // Honour the MinimumAcceptedHashAlgorithm policy: SCRAM can only be served from a
      // PBKDF2 hash, so when the administrator requires a stronger hash type than PBKDF2
      // no account is eligible. Returning an empty handle makes the exchange a forced
      // failure (the same as an unknown account) rather than revealing the policy.
      if (Crypt::StrengthRank(IniFileSettings::Instance()->GetMinimumAcceptedHashAlgorithm()) > Crypt::StrengthRank(Crypt::ETPBKDF2))
         return std::shared_ptr<const Account>();

      if (pAccount->GetPasswordEncryption() != Crypt::ETPBKDF2)
         return std::shared_ptr<const Account>();

      return pAccount;
   }


   int 
   SMTPConnection::GetMaxMessageSize_(std::shared_ptr<const Domain> pDomain)
   {
      int iMaxMessageSizeKB = smtpconf_->GetMaxMessageSize();
      
      if (pDomain)
      {
         int iDomainMaxSizeKB = pDomain->GetMaxMessageSize(); 
         if (iDomainMaxSizeKB > 0)
         {
            if (iMaxMessageSizeKB == 0 || iMaxMessageSizeKB > iDomainMaxSizeKB)
               iMaxMessageSizeKB = iDomainMaxSizeKB;
         }
      }

      return iMaxMessageSizeKB;
   }

   void 
   SMTPConnection::SendErrorResponse_(int iErrorCode, const String &sResponse)
   {
      if (iErrorCode >= 500 && iErrorCode <= 599)
      {
         cur_no_of_invalid_commands_++;

         if (Configuration::Instance()->GetDisconnectInvalidClients() &&
            cur_no_of_invalid_commands_ > Configuration::Instance()->GetMaximumIncorrectCommands())
         {
            // Disconnect. 421 is mandatory, not decoration: RFC 5321 section 4.2
            // requires every reply to begin with a three-digit code, and this line
            // was sent as bare text - "Too many invalid commands. Bye!" with no code
            // at all. A client reading it gets a line it cannot parse as a reply
            // immediately before the channel closes, which is indistinguishable from
            // a truncated response or a hijacked session; the sending MTA reports a
            // protocol error rather than "the server hung up on me for sending
            // rubbish". 421 is the code RFC 5321 reserves for "service not available,
            // closing transmission channel", which is exactly what happens next.
            SendResponse_(421, _T("4.7.0"), _T("Too many invalid commands. Bye!"));
            pending_disconnect_ = true;
            EnqueueDisconnect();

            if (Configuration::Instance()->GetUseScriptServer())
            {
               std::shared_ptr<ScriptObjectContainer> pContainer = std::shared_ptr<ScriptObjectContainer>(new ScriptObjectContainer);
               std::shared_ptr<ClientInfo> pClientInfo = std::shared_ptr<ClientInfo>(new ClientInfo);

               pClientInfo->SetUsername(username_);
               pClientInfo->SetIPAddress(GetIPAddressString());
               pClientInfo->SetPort(GetLocalEndpointPort());
               pClientInfo->SetSessionID(GetSessionID());
               pClientInfo->SetHELO(helo_host_);
               pClientInfo->SetIsAuthenticated(isAuthenticated_);
               pClientInfo->SetIsEncryptedConnection(IsSSLConnection());
               if (IsSSLConnection())
               {
                  auto cipher_info = GetCipherInfo();
                  pClientInfo->SetCipherVersion(cipher_info.GetVersion().c_str());
                  pClientInfo->SetCipherName(cipher_info.GetName().c_str());
                  pClientInfo->SetCipherBits(cipher_info.GetBits());
               }

               pContainer->AddObject("HMAILSERVER_MESSAGE", current_message_, ScriptObject::OTMessage);
               pContainer->AddObject("HMAILSERVER_CLIENT", pClientInfo, ScriptObject::OTClient);

               String sEventCaller = "OnTooManyInvalidCommands(HMAILSERVER_CLIENT, HMAILSERVER_MESSAGE)";
               ScriptServer::Instance()->FireEvent(ScriptServer::EventOnTooManyInvalidCommands, sEventCaller, pContainer);
            }

            return;
         }
         else
         {
            if (!sResponse.compare(CONST_UNKNOWN_USER))
            {
               if (Configuration::Instance()->GetUseScriptServer())
               {
                  std::shared_ptr<ScriptObjectContainer> pContainer = std::shared_ptr<ScriptObjectContainer>(new ScriptObjectContainer);
                  std::shared_ptr<ClientInfo> pClientInfo = std::shared_ptr<ClientInfo>(new ClientInfo);

                  pClientInfo->SetUsername(username_);
                  pClientInfo->SetIPAddress(GetIPAddressString());
                  pClientInfo->SetPort(GetLocalEndpointPort());
                  pClientInfo->SetSessionID(GetSessionID());
                  pClientInfo->SetHELO(helo_host_);
                  pClientInfo->SetIsAuthenticated(isAuthenticated_);
                  pClientInfo->SetIsEncryptedConnection(IsSSLConnection());
                  if (IsSSLConnection())
                  {
                     auto cipher_info = GetCipherInfo();
                     pClientInfo->SetCipherVersion(cipher_info.GetVersion().c_str());
                     pClientInfo->SetCipherName(cipher_info.GetName().c_str());
                     pClientInfo->SetCipherBits(cipher_info.GetBits());
                  }

                  pContainer->AddObject("HMAILSERVER_MESSAGE", current_message_, ScriptObject::OTMessage);
                  pContainer->AddObject("HMAILSERVER_CLIENT", pClientInfo, ScriptObject::OTClient);

                  String sEventCaller = "OnRecipientUnknown(HMAILSERVER_CLIENT, HMAILSERVER_MESSAGE)";
                  ScriptServer::Instance()->FireEvent(ScriptServer::EventOnRecipientUnknown, sEventCaller, pContainer);
               }
            }
         }
      }

      String sData;
      String enhancedCode = DeriveEnhancedStatusCode_(iErrorCode);
      if (esmtp_session_ && !enhancedCode.IsEmpty())
         sData.Format(_T("%d %s %s"), iErrorCode, enhancedCode.c_str(), sResponse.c_str());
      else
         sData.Format(_T("%d %s"), iErrorCode, sResponse.c_str());
      
      EnqueueWrite_(sData);
     
   }

   void
   SMTPConnection::SendResponse_(int code, const String &enhancedCode, const String &text)
   {
      String sData;
      if (esmtp_session_ && !enhancedCode.IsEmpty())
         sData.Format(_T("%d %s %s"), code, enhancedCode.c_str(), text.c_str());
      else
         sData.Format(_T("%d %s"), code, text.c_str());

      EnqueueWrite_(sData);
   }

   String
   SMTPConnection::DeriveEnhancedStatusCode_(int code)
   {
      // RFC 3463 status codes for the SMTP replies hMailServer emits. Replies that
      // RFC 2034 does not decorate (1xx/2xx greetings, 220/221, 354) return empty.
      switch (code)
      {
      case 235: return _T("2.7.0");
      case 250: return _T("2.0.0");
      case 251:
      case 252: return _T("2.1.5");
      case 421: return _T("4.3.2");
      case 450: return _T("4.2.0");
      case 451: return _T("4.3.0");
      case 452: return _T("4.3.1");
      case 454: return _T("4.7.0");
      case 500: return _T("5.5.2");
      case 501: return _T("5.5.4");
      case 502:
      case 503: return _T("5.5.1");
      case 504: return _T("5.5.4");
      case 530: return _T("5.7.0");
      case 535: return _T("5.7.8");
      case 550: return _T("5.7.1");
      case 551: return _T("5.1.6");
      case 552: return _T("5.3.4");
      case 553: return _T("5.1.3");
      case 554: return _T("5.5.0");
      }

      // Fall back to a generic class-based code (x.0.0 = "other/undefined").
      if (code >= 200 && code < 300) return _T("2.0.0");
      if (code >= 400 && code < 500) return _T("4.0.0");
      if (code >= 500 && code < 600) return _T("5.0.0");

      return _T("");
   }

   bool
   SMTPConnection::ParseSizeParameter_(const String &value, unsigned __int64 &size)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // RFC 1870 SIZE=<n>: a non-empty run of decimal digits and nothing else. A value
   // that cannot be represented is refused rather than wrapped or saturated - a size
   // the server cannot compare is not a size it may quietly ignore, because ignoring
   // it means the transaction proceeds and the octets arrive.
   //---------------------------------------------------------------------------()
   {
      size = 0;

      const int length = value.GetLength();

      // 19 digits is the widest decimal value that always fits an unsigned 64-bit
      // integer, so the accumulation below cannot overflow.
      if (length == 0 || length > 19)
         return false;

      for (int i = 0; i < length; i++)
      {
         wchar_t c = value.GetAt(i);
         if (c < '0' || c > '9')
            return false;

         size = size * 10 + (unsigned __int64) (c - '0');
      }

      return true;
   }

   bool
   SMTPConnection::IsValidXtext_(const String &value)
   {
      // xtext (RFC 3461 section 4): printable ASCII (33-126) where '+' and '='
      // must be encoded as "+" followed by two upper-case hexadecimal digits.
      int length = value.GetLength();
      for (int i = 0; i < length; i++)
      {
         wchar_t c = value.GetAt(i);
         if (c < 33 || c > 126)
            return false;

         if (c == '=')
            return false;

         if (c == '+')
         {
            if (i + 2 >= length)
               return false;

            for (int j = 1; j <= 2; j++)
            {
               wchar_t h = value.GetAt(i + j);
               bool isHex = (h >= '0' && h <= '9') || (h >= 'A' && h <= 'F');
               if (!isHex)
                  return false;
            }

            i += 2;
         }
      }

      return true;
   }

   bool
   SMTPConnection::IsValidOrcpt_(const String &value)
   {
      // ORCPT (RFC 3461) = addr-type ";" xtext.
      int separator = value.Find(_T(";"));
      if (separator <= 0 || separator == value.GetLength() - 1)
         return false;

      String addrType = value.Mid(0, separator);
      String addrValue = value.Mid(separator + 1);

      // addr-type is an atom of printable ASCII (no ';' or whitespace).
      for (int i = 0; i < addrType.GetLength(); i++)
      {
         wchar_t c = addrType.GetAt(i);
         if (c <= 32 || c >= 127)
            return false;
      }

      return IsValidXtext_(addrValue);
   }

   bool
   SMTPConnection::ParseDsnNotify_(const String &value, int &notify)
   {
      // NOTIFY (RFC 3461) = "NEVER" / 1#("SUCCESS" / "FAILURE" / "DELAY").
      // NEVER may not be combined with any other keyword.
      notify = MessageRecipient::DSNNotifyDefault;

      if (value.IsEmpty())
         return false;

      std::vector<String> keywords = StringParser::SplitString(value, ",");
      bool neverSeen = false;
      bool otherSeen = false;

      for (const String &keyword : keywords)
      {
         if (keyword.CompareNoCase(_T("NEVER")) == 0)
         {
            neverSeen = true;
            notify |= MessageRecipient::DSNNotifyNever;
         }
         else if (keyword.CompareNoCase(_T("SUCCESS")) == 0)
         {
            otherSeen = true;
            notify |= MessageRecipient::DSNNotifySuccess;
         }
         else if (keyword.CompareNoCase(_T("FAILURE")) == 0)
         {
            otherSeen = true;
            notify |= MessageRecipient::DSNNotifyFailure;
         }
         else if (keyword.CompareNoCase(_T("DELAY")) == 0)
         {
            otherSeen = true;
            notify |= MessageRecipient::DSNNotifyDelay;
         }
         else
         {
            return false;
         }
      }

      // NEVER is mutually exclusive with the other keywords.
      if (neverSeen && otherSeen)
         return false;

      return true;
   }

   bool
   SMTPConnection::DoPreAcceptSpamProtection_()
   {
      if (rejected_by_delayed_grey_listing_)
      {
         SendErrorResponse_(450, "Please try again later.");
         // Don't log to awstats here, since we tell the client to try again later.
         return false;
      }

      // Check if we should do pre-transmissions tests after transmission. This
      // happens if the message is delivered from a forwarding relay server.
      if (type_ == SPPostTransmission)
      {
         // Do all spam proteciton now. It has been delayed since we trust the 
         // server which has forwarded to us.
         // Retrieve the IP address from the message headers.
         IPAddress iIPAddress;
         String hostName;
         
         MessageUtilities::RetrieveOriginatingAddress(current_message_, hostName, iIPAddress);
      
         // Do spam protection now using the IP address in the header.
         if (!DoSpamProtection_(SPPreTransmission, current_message_->GetFromAddress(), hostName, iIPAddress))
         {
            // We should stop the message delivery.
            return false;
         }

         if (!DoSpamProtection_(SPPostTransmission, current_message_->GetFromAddress(), hostName, iIPAddress))
         {
            // We should stop the message delivery.
            return false;
         }
         
      }
      else
      {
         // Do normal post transmission spam protection. (typically SURBL)
         if (!DoSpamProtection_(SPPostTransmission, current_message_->GetFromAddress(), helo_host_, GetRemoteEndpointAddress()))
         {
            // We should stop message delivery
            return false;
         }
      }

      // The message should be delivered.
      return true;
   }


   bool 
   SMTPConnection::GetDoSpamProtection_()
   {
      if (isAuthenticated_)
         return false;

      if (!GetSecurityRange()->GetSpamProtection())
         return false;

      if (current_message_)
      {
         if (SpamProtection::IsWhiteListed(current_message_->GetFromAddress(), GetRemoteEndpointAddress()))
            return false;
      }

      return true;
   }

   /*
      Returns true if 
      - the domain-part of the email matches an active local domain.
      - the sender address matches a route address.
   */
   bool
   SMTPConnection::GetIsLocalSender_()
   {
       if (sender_domain_ && sender_domain_->GetIsActive())
          return true;

       const String senderAddress = current_message_->GetFromAddress();

       String senderDomainName = StringParser::ExtractDomain(senderAddress);
       std::shared_ptr<Route> route = Configuration::Instance()->GetSMTPConfiguration()->GetRoutes()->GetItemByNameWithWildcardMatch(senderDomainName);

       if (route)
       {
          if (route->ToAllAddresses() || route->GetAddresses()->GetItemByName(senderAddress))
          {
             if (route->GetTreatSenderAsLocalDomain())
                return true;
          }
       }       

       // Does not match a local domain or route.
       return false;
   }

   bool
   SMTPConnection::ParseAddressWithExtensions_(String mailFrom, String &address, String &parameters)
   {
      // Variants:
      // (empty)
      // example
      // example@example.com
      // <example@example.com>
      // <example> param1=value1 param2=value2
      // example param1=value1 param2=value2
      // example@example.com param1=value1 param2=value2
      // <example@example.com> param1=value1 param2=value2
      // "a b"@example.com> param1=value1 param2=value2
      
      // Parameters always comes after the first space, except for when the mailbox part is quoted,
      // in which case it's after the first space after the last quote.

      int parameterStartPosition = 0;

      int firstQuotePosition = mailFrom.Find(_T("\""));
      if (firstQuotePosition >= 0)
      {
         int lastQuotePosition = mailFrom.ReverseFind(_T("\""));

         if (firstQuotePosition == lastQuotePosition)
            return false;

         parameterStartPosition = mailFrom.Find(_T(" "), lastQuotePosition);
      }
      else
      {
         parameterStartPosition = mailFrom.Find(_T(" "));
      }

      int emailAddressEndPosition = 0;

      if (parameterStartPosition >= 0)
      {
         emailAddressEndPosition = parameterStartPosition;
      }
      else
      {
         emailAddressEndPosition = mailFrom.GetLength();
      }

      address = mailFrom.Left(emailAddressEndPosition);

      parameters = parameterStartPosition > 0 ? mailFrom.Mid(parameterStartPosition) : _T("");

      if (address.StartsWith(_T("<")))
      {
         if (!address.EndsWith(_T(">")))
            return false;

         address.TrimLeft('<');
         address.TrimRight('>');

         address.TrimLeft();
         address.TrimRight();
      }

      parameters.TrimLeft();
      parameters.TrimRight();

      return true;
   }


   bool
   SMTPConnection::GetAuthIsEnabled_()
   {
      const auto authDisabledOnPorts = IniFileSettings::Instance()->GetAuthDisabledOnPorts();
      return authDisabledOnPorts.find(GetLocalEndpointPort()) == authDisabledOnPorts.end();
   }

   void 
   SMTPConnection::ReportUnsupportedEsmtpExtension_(const String& parameter)
   {
      SendErrorResponse_(550, Formatter::Format("Unsupported ESMTP extension: {0}", parameter));

   }
}
