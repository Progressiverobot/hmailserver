// Copyright (c) 2005 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd


#include "stdafx.h"

#include <Boost/Regex.hpp>

#include "../common/bo/MessageData.h"

#include "../common/Cache/CacheContainer.h"
#include "../common/Util/PasswordValidator.h"
#include "../common/Util/AccountLogon.h"
#include "../common/Util/OAuth2TokenValidator.h"
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

#include "../Common/AntiSpam/AntiSpamConfiguration.h"
#include "../Common/AntiSpam/SpamProtection.h"

#include "../Common/Application/TimeoutCalculator.h"
#include "../Common/Scripting/ScriptServer.h"
#include "../Common/Scripting/ScriptObjectContainer.h"
#include "../Common/Scripting/Result.h"

#include "../Common/Application/IniFileSettings.h"

#include "../Common/Util/CrashSimulation.h"
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
      bdat_active_(false),
      bdat_last_(false),
      bdat_discard_(false),
      bdat_chunk_size_(0),
      bdat_chunk_remaining_(0),
      ptr_lookup_completed_(false),
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

      std::vector<String> results;
      DNSResolver dns_resolver;
      if (dns_resolver.GetPTRRecords(GetIPAddressString(), results) && results.size() > 0)
      {
         ptr_host = results[0];
      }
      else
      {
         LOG_DEBUG("Could not retrieve PTR record for IP (false)! " + GetIPAddressString());
      }

      boost::lock_guard<boost::mutex> guard(ptr_result_mutex_);
      ptr_record_host_ = ptr_host;
      ptr_lookup_completed_ = true;
   }

   String
   SMTPConnection::GetPtrRecordHost_()
   {
      boost::lock_guard<boost::mutex> guard(ptr_result_mutex_);

      // If the lookup is still in flight, fall back to "Unknown" rather than wait:
      // the Received header is being generated on the I/O thread.
      if (!ptr_lookup_completed_)
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

         EnqueueRead();
      }
   }

   void 
   SMTPConnection::SendBanner_()
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

      EnqueueWrite_(sData);

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

      if (sRequest.GetLength() > 510)
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
      size_t iEstimatedMessageSize = 0;
      while (iterParam != vecParams.end())
      {
         String parameter = (*iterParam);
         if (parameter.Left(4).CompareNoCase(_T("SIZE")) == 0)
            iEstimatedMessageSize = _ttoi(parameter.Mid(5));
         else if (parameter.Left(4).CompareNoCase(_T("AUTH")) == 0)
            sAuthParam = parameter.Mid(5);
         else if (parameter.CompareNoCase(_T("BODY=7BIT")) == 0 ||
                  parameter.CompareNoCase(_T("BODY=8BITMIME")) == 0)
         {
            // 8BITMIME (RFC 6152): the transmission channel is 8-bit clean,
            // so both body types are accepted as-is.
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
          iEstimatedMessageSize / 1024 > max_message_size_kb_)
      {
         // Message too big. Reject it. 5.3.4 = message too big for system (RFC 3463).
         String sMessage;
         sMessage.Format(_T("Message size exceeds fixed maximum message size. Size: %d KB, Max size: %d KB"),
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
         
      std::shared_ptr<const Account> pAccount = PasswordValidator::ValidatePassword(username_, password_);
      
      if (pAccount)
         return true;
         
      // Reset login credentials
      ResetLoginCredentials_();      

      SendErrorResponse_(550, "Login credentials no longer valid. Please re-authenticate.");                      
      
      return false;
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

         dp = recipientParser_.CheckDeliveryPossibility(isAuthenticated_, current_message_->GetFromAddress(), sRecipientAddress, sErrMsg, localDelivery, 0);

         if (dp != RecipientParser::DP_Possible && DatabaseUnavailableMarker::IsMarked())
         {
            AWStats::LogDeliveryFailure(GetIPAddressString(), current_message_->GetFromAddress(), sRecipientAddress, 451);

            // The enhanced code belongs in its own field, not inside the text: passing
            // it as text made an ESMTP session receive "451 4.3.0 4.3.2 Unable...".
            SendResponse_(451, _T("4.3.2"), _T("Unable to verify the recipient at the moment. Please retry later."));
            return;
         }
      }

      if (dp != RecipientParser::DP_Possible)
      {
         AWStats::LogDeliveryFailure(GetIPAddressString(), current_message_->GetFromAddress(), sRecipientAddress, 550);

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
         AWStats::LogDeliveryFailure(GetIPAddressString(), current_message_->GetFromAddress(), sRecipientAddress, 550);
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
         AWStats::LogDeliveryFailure(GetIPAddressString(), current_message_->GetFromAddress(), sRecipientAddress, 530);
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
               AWStats::LogDeliveryFailure(GetIPAddressString(), current_message_->GetFromAddress(), sRecipientAddress, 550);
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

      if (spType == SPPreTransmission)
      {
         std::set<std::shared_ptr<SpamTestResult> > setResult = 
            SpamProtection::Instance()->RunPreTransmissionTests(sFromAddress, lIPAddress, GetRemoteEndpointAddress(), hostName);

         spam_test_results_.insert(setResult.begin(), setResult.end());
      }
      else if (spType == SPPostTransmission)
      {
         std::set<std::shared_ptr<SpamTestResult> > setResult = 
            SpamProtection::Instance()->RunPostTransmissionTests(sFromAddress, lIPAddress, GetRemoteEndpointAddress(), current_message_);

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

         SMTPMessageHeaderCreator header_creator(username_, GetIPAddressString(), isAuthenticated_, helo_host_, original_headers, current_message_, GetSessionID());

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

      // Since this may be a time-consuming task, do it asynchronously
      finalization_enqueued_tick_ = GetTickCount64();
      std::shared_ptr<AsynchronousTask<TCPConnection> > finalizationTask =
         std::shared_ptr<AsynchronousTask<TCPConnection> >(new AsynchronousTask<TCPConnection>
            (std::bind(&SMTPConnection::HandleSMTPFinalizationTaskCompleted_, this), shared_from_this(),
             Formatter::Format("SMTP-accept session={0} ip={1}", GetSessionID(), GetIPAddressString())));

      Application::Instance()->GetAsyncWorkQueue()->AddTask(finalizationTask);
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
         SetReceiveBinary(false);
         EnqueueRead();
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

      SetReceiveBinary(false);
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
      SetReceiveBinary(false);
      EnqueueRead();

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

      if (pMsgData)
         pMsgData->Write(PersistentMessage::GetFileName(current_message_));

      // RFC 2369 / RFC 2919 List-* headers for postings to local distribution lists.
      //
      // Position is load-bearing in both directions. It must come *after* the Write
      // above, which re-serialises the whole file from a MessageData loaded earlier in
      // this function - running before it would have that write discard the List-*
      // headers. And it must stay *inside* this function, because the caller re-reads
      // the file size from disk immediately afterwards, which is what makes the
      // rewritten size authoritative.
      DistributionListSender::AddListHeaders(current_message_);
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
         sMessage.Format(_T("Rejected - Message size exceeds fixed maximum message size. Size: %d KB, Max size: %d KB"),
            transmission_buffer_->GetSize() / 1024, max_message_size_kb_);
         SendResponse_(554, _T("5.3.4"), sMessage);
         LogAwstatsMessageRejected_();
         return false;
      }

      // Check for bare LF's.
      if (!Configuration::Instance()->GetSMTPConfiguration()->GetAllowIncorrectLineEndings())
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

      sender_domain_.reset();
      sender_account_.reset();

      spam_test_results_.clear();

      // Reset the number of RCPT TO's for this 
      // message.
      cur_no_of_rcptto_ = 0;

      // Reset per-transaction ESMTP parameters (SMTPUTF8 / DSN).
      smtputf8_requested_ = false;
      dsn_envid_.Empty();
      dsn_ret_.Empty();

      // Reset per-transaction RFC 3030 (CHUNKING/BDAT) state.
      bdat_active_ = false;
      bdat_last_ = false;
      bdat_discard_ = false;
      bdat_chunk_size_ = 0;
      bdat_chunk_remaining_ = 0;

      // Switch back to normal ASCII mode and start of session, in
      // case we are in binary transmission mode.
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

      // SMTPUTF8 (RFC 6531): accept internationalized (UTF-8) envelope addresses.
      sData += "\r\n250-SMTPUTF8";

      // ENHANCEDSTATUSCODES (RFC 2034): responses carry an RFC 3463 status code.
      sData += "\r\n250-ENHANCEDSTATUSCODES";

      // DSN (RFC 3461): accept RET/ENVID on MAIL FROM and NOTIFY/ORCPT on RCPT TO,
      // and honour NOTIFY=NEVER when generating delivery-failure notifications.
      sData += "\r\n250-DSN";

      if (!IsSSLConnection())
      {
         if (GetConnectionSecurity() == CSSTARTTLSOptional ||
             GetConnectionSecurity() == CSSTARTTLSRequired)
         {
            sData += "\r\n250-STARTTLS";
         }
      }

      if (GetAuthIsEnabled_() && (IsSSLConnection() || GetConnectionSecurity() != CSSTARTTLSRequired))
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

      EnqueueWrite_("211 DATA HELO EHLO MAIL NOOP QUIT RCPT RSET SAML TURN VRFY");
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

      Application::Instance()->GetAsyncWorkQueue()->AddTask(finalizationTask);
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

      // Feed the per-IP auto-ban accounting, then apply the per-connection cap.
      AccountLogon accountLogon;
      bool disconnect = false;
      accountLogon.RegisterFailedLogin(GetRemoteEndpointAddress(), sLoginName, disconnect);

      authentication_failure_count_++;

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
   SMTPConnection::ResetLoginCredentials_()
   {
      requestedAuthenticationType_ = AUTH_NONE;
      isAuthenticated_ = false;

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

      FireOnClientLogon_(sUsername, true);

      SendResponse_(235, _T("2.7.0"), _T("authenticated."));
      current_state_ = HEADER;
   }

   void
   SMTPConnection::ScramAuthFailed_()
   {
      String sUsername = scram_session_ ? String(scram_session_->GetUsername()) : username_;
      scram_session_.reset();

      // Feed the per-IP auto-ban accounting (parity with the LOGIN/PLAIN path).
      AccountLogon accountLogon;
      bool disconnect = false;
      accountLogon.RegisterFailedLogin(GetRemoteEndpointAddress(), sUsername, disconnect);

      authentication_failure_count_++;

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
      if (IniFileSettings::Instance()->GetMinimumAcceptedHashAlgorithm() > Crypt::ETPBKDF2)
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
            // Disconnect
            EnqueueWrite_("Too many invalid commands. Bye!");
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
