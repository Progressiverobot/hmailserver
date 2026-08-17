// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"

#include "../common/BO/Account.h"
#include "../common/BO/SecurityRange.h"
#include "../common/BO/Message.h"
#include "../common/util/file.h"
#include "../common/Util/AccountLogon.h"
#include "../common/util/ByteBuffer.h"
#include "../common/Util/Crypt.h"
#include "../common/Util/Hashing/ScramSha256.h"
#include "../common/Util/Parsing/StringParser.h"
#include "../common/Util/OAuth2TokenValidator.h"
#include "../common/Application/IniFileSettings.h"
#include "../Common/Application/TimeoutCalculator.h"

#include "../Common/Application/FolderManager.h"
#include "../Common/Application/ObjectCache.h"
#include "../Common/Application/DefaultDomain.h"
#include "../Common/Application/SessionManager.h"
#include "../Common/Cache/CacheContainer.h"
#include "../Common/BO/DomainAliases.h"
#include "../Common/BO/Domain.h"
#include "../Common/Persistence/PersistentMessage.h"

#include "POP3Sessions.h"
#include "POP3Connection.h"
#include "POP3Configuration.h"
#include "../Common/Util/PasswordRemover.h"

#include "../Common/Util/TransparentTransmissionBuffer.h"

#include "../common/Scripting/ClientInfo.h"
#include "../Common/Scripting/ScriptServer.h"
#include "../Common/Scripting/ScriptObjectContainer.h"

#include "../Common/TCPIP/CipherInfo.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{

   POP3Connection::POP3Connection(ConnectionSecurity connection_security,
      boost::asio::io_context& io_context, 
      boost::asio::ssl::context& context) :
      TCPConnection(connection_security, io_context, context, std::shared_ptr<Event>(), ""),
      current_state_(AUTHORIZATION),
      transmission_buffer_(true),
      pending_disconnect_(false),
      authentication_failure_count_(0),
      sasl_plain_pending_(false),
      sasl_bearer_pending_(false),
      utf8_enabled_(false),
      mailbox_locked_(false)
   {

      /*
        RFC 1939, Basic Operation
        A POP3 server MAY have an inactivity autologout timer.  Such a timer
        MUST be of at least 10 minutes' duration.

        Under very high load, the timeout will decrease below the values specified
        by the RFC. The reasoning is that it's better to disconnect slow clients
        than it is to disconnect everyone.
      */

      TimeoutCalculator calculator;
      SetTimeout(calculator.Calculate(IniFileSettings::Instance()->GetPOP3DMinTimeout(), IniFileSettings::Instance()->GetPOP3DMaxTimeout()));
   }

   POP3Connection::~POP3Connection()
   {
      try
      {
         OnDisconnect();

         if (GetConnectionState() != StatePendingConnect)
            SessionManager::Instance()->OnSessionEnded(STPOP3);
      }
      catch (...)
      {

      }
   }

   void
   POP3Connection::OnConnected()
   {
      if (GetConnectionSecurity() == CSNone ||
          GetConnectionSecurity() == CSSTARTTLSOptional ||
          GetConnectionSecurity() == CSSTARTTLSRequired)
         SendBanner_();
   }

   void
   POP3Connection::OnHandshakeCompleted()
   {
      if (GetConnectionSecurity() == CSSSL)
         SendBanner_();
      else if (GetConnectionSecurity() == CSSTARTTLSOptional ||
               GetConnectionSecurity() == CSSTARTTLSRequired)
      {
         EnqueueRead();
      }
   }

   void 
   POP3Connection::SendBanner_()
   {
      String sData;

      String sWelcome = Configuration::Instance()->GetPOP3Configuration()->GetWelcomeMessage();

      sData += "+OK ";
      if (sWelcome.IsEmpty())
         sData += _T("POP3");
      else
         sData += sWelcome;

      EnqueueWrite_(sData);

      EnqueueRead();
   }

   AnsiString 
   POP3Connection::GetCommandSeparator() const
   {
      return "\r\n";
   }


   void 
   POP3Connection::LogClientCommand_(const String &sClientData)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Logs one client command.
   //---------------------------------------------------------------------------()
   {
      if (!Logger::Instance()->GetLogPOP3())
         return;

      String sLogData = sClientData;

      // A SASL PLAIN response line carries the password in base64, so never log it.
      if (sasl_plain_pending_ || sasl_bearer_pending_)
         sLogData = "***";
      else
         PasswordRemover::Remove(PasswordRemover::PRPOP3, sLogData);

      // Append
      sLogData = "RECEIVED: " + sLogData;

      LOG_POP3(GetSessionID(), GetIPAddressString(), sLogData);      
   }

   POP3Connection::POP3Command 
   POP3Connection::GetCommand(ConnectionState currentState, String command)
   {
      command.MakeUpper();

      POP3Command resolvedCommand = INVALID;

      if (command == _T("NOOP"))
         resolvedCommand = NOOP;
      else if (command == _T("STLS"))
         resolvedCommand = STLS;
      else if (command == _T("USER"))
         resolvedCommand = USER;
      else if (command == _T("PASS"))
         resolvedCommand = PASS;
      else if (command == _T("HELP"))
         resolvedCommand = HELP;
      else if (command == _T("QUIT"))
         resolvedCommand = QUIT;
      else if (command == _T("STAT"))
         resolvedCommand = STAT;
      else if (command == _T("LIST"))
         resolvedCommand = LIST;
      else if (command == _T("RETR"))
         resolvedCommand = RETR;
      else if (command == _T("TOP"))
         resolvedCommand = TOP;
      else if (command == _T("RSET"))
         resolvedCommand = RSET;
      else if (command == _T("DELE"))
         resolvedCommand = DELE;
      else if (command == _T("UIDL"))
         resolvedCommand = UIDL;
      else if (command == _T("CAPA"))
         resolvedCommand = CAPA;
      else if (command == _T("AUTH"))
         resolvedCommand = AUTH;
      else if (command == _T("UTF8"))
         resolvedCommand = UTF8;
      
      // Some commands are always allowed, regardless of state.
      if (resolvedCommand == NOOP || resolvedCommand == HELP || resolvedCommand == QUIT || resolvedCommand == CAPA)
         return resolvedCommand;

      switch (currentState)
      {
      case AUTHORIZATION:
         {
            switch (resolvedCommand)
            {
            case USER:
            case PASS:
            case AUTH:
            case STLS:
            case UTF8:
               return resolvedCommand;
            default:
               return INVALID;
            }
         }
      case TRANSACTION:
         {
            switch (resolvedCommand)
            {
            case STAT:
            case LIST:
            case RETR:
            case TOP:
            case RSET:
            case DELE:
            case UIDL:
               return resolvedCommand;
            default:
               return INVALID;
            }
         }
      case UPDATE:
         {
           // Commands are not allowed within the UPDATE state.
           return INVALID;
         }
      }

      // What state are we in?
      assert(0);
      return INVALID;
   }

   void
   POP3Connection::ParseData(const AnsiString &Request)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Parses a client POP3 command.
   //---------------------------------------------------------------------------()
   {
      // If we're currently processing a command, we should queue up the new command.
      ParseResult result = InternalParseData(Request);

      switch (result)
      {
      case ResultNormalResponse:
         EnqueueRead();
         break;
      case ResultStartSendMessage:
      case ResultStartTls:
         break;
      case ResultDisconnect:
         EnqueueDisconnect();
         break;
      }

   }

   POP3Connection::ParseResult
   POP3Connection::InternalParseData(const AnsiString &Request)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Parses a client POP3 command.
   //---------------------------------------------------------------------------()
   {
      LogClientCommand_(Request);

      if (Request.GetLength() > 500)
      {
         // This line is too long... is this an evil user?
         EnqueueWrite_("-ERR Line too long.");
         return ResultNormalResponse;
      }

      // A SASL exchange in progress consumes the next line(s) as continuation data
      // rather than as a POP3 command.
      if (scram_session_)
         return ContinueScram_(Request);

      if (sasl_plain_pending_)
      {
         sasl_plain_pending_ = false;
         if (Request == "*")
         {
            EnqueueWrite_("-ERR Authentication cancelled.");
            return ResultNormalResponse;
         }
         return ProcessAuthPlain_(Request);
      }

      if (sasl_bearer_pending_)
      {
         sasl_bearer_pending_ = false;
         if (Request == "*")
         {
            EnqueueWrite_("-ERR Authentication cancelled.");
            return ResultNormalResponse;
         }
         return ProcessAuthBearer_(Request);
      }
      
      String sCommand;
      String sParameter;

      if (Request.Find(" ")>-1)
      {
         sCommand = Request.Left(Request.Find(" "));
         sParameter = Request.Mid(Request.Find(" ") + 1);
      }
      else
         sCommand = Request;

      POP3Command command = GetCommand(current_state_, sCommand);

      switch (command)
      {
         case NOOP:
            EnqueueWrite_("+OK");
            return ResultNormalResponse;
         case STLS:
            if (ProtocolSTLS_())
               return ResultStartTls;
            else
               return ResultNormalResponse;
         case HELP:
            EnqueueWrite_("+OK Normal POP3 commands allowed");
            return ResultNormalResponse;
         case USER:
            ProtocolUSER_(sParameter);
            return ResultNormalResponse;
         case PASS:
            return ProtocolPASS_(sParameter);
         case AUTH:
            return ProtocolAUTH_(sParameter);
         case STAT:
            ProtocolSTAT_(sParameter);
            return ResultNormalResponse;
         case LIST:
            ProtocolLIST_(sParameter);
            return ResultNormalResponse;
         case RETR:
            return ProtocolRETR_(sParameter);
         case DELE:
            ProtocolDELE_ (sParameter);
            return ResultNormalResponse;
         case TOP:
            ProtocolTOP_(sParameter);
            return ResultNormalResponse;
         case RSET:
            ProtocolRSET_();
            return ResultNormalResponse;   
         case UIDL:
            ProtocolUIDL_(sParameter);
            return ResultNormalResponse;   
         case QUIT:
            ProtocolQUIT_();
            return ResultDisconnect;
         case CAPA:
            ProtocolCAPA_();
            return ResultNormalResponse; 
         case UTF8:
            ProtocolUTF8_();
            return ResultNormalResponse;
         case INVALID:
            EnqueueWrite_("-ERR Invalid command in current state." );
            return ResultNormalResponse;  
         default:
            assert(0); // What command is this?
            EnqueueWrite_("-ERR Invalid command in current state." );
            return ResultNormalResponse;  
      }
   }

   void
   POP3Connection::OnConnectionTimeout()
   {
      // Fix for mailbox remailing locked even after timeout
      // https://www.progressiverobot.com/forum/viewtopic.php?f=7&t=22361
      UnlockMailbox_();

      String sMessage = "-ERR Autologout; idle too long\r\n";
      EnqueueWrite(sMessage);
   }

   void
   POP3Connection::OnExcessiveDataReceived()
   {
      String sMessage = "-ERR Excessive amount of data sent to server.\r\n";
      EnqueueWrite(sMessage);
   }
   
   void
   POP3Connection::ProtocolRSET_()
   {
      // Every other TRANSACTION-state handler checks this and RSET did not. The
      // autologout path (OnConnectionTimeout -> UnlockMailbox_) releases the lock and
      // drops account_ while current_state_ is still TRANSACTION, so a command still
      // in flight when the timer fires reaches a handler with no account. Untested
      // defence in depth: it needs a race with the idle timer to provoke.
      if (!account_)
      {
         EnqueueWrite_("-ERR Message list not loaded");
         return;
      }

      if (Application::Instance()->GetFolderManager()->GetInboxMessages((int) account_->GetID(), messages_))
      {
         ResetMailbox_();
         EnqueueWrite_("+OK 0");
      }
      else
         EnqueueWrite_("-ERR Unable to access mailbox.");


   }

   void
   POP3Connection::ProtocolCAPA_()
   {
      String capabilities = "UIDL\r\nTOP\r\n";

      // RFC 2449 section 5: CAPA must not list a capability the server will not honour
      // on this connection. Authentication is refused on a cleartext connection in two
      // cases, not one - the port being CSSTARTTLSRequired, and the connecting IP range
      // setting RequireTLSForAuth - and only the first was reflected here. In the second
      // case CAPA offered USER and SASL and then ProtocolUSER_/ProtocolAUTH_ refused
      // them, which is worse than untidy: a client that takes up the SASL offer sends
      // "AUTH PLAIN <base64 authcid NUL passwd>" and only then learns the connection is
      // unacceptable, so the password has already crossed the wire in the clear.
      const bool authRefusedOnCleartext =
         GetConnectionSecurity() == CSSTARTTLSRequired ||
         GetSecurityRange()->GetRequireTLSForAuth();

      if (IsSSLConnection() || !authRefusedOnCleartext)
      {
         capabilities+="USER\r\n";
         // RFC 5034: advertise the SASL mechanisms available for the AUTH command.
         // SCRAM-SHA-256-PLUS (RFC 5802 + RFC 5929 tls-server-end-point) binds the
         // exchange to the TLS channel, so it is only offered on a TLS connection.
         if (IsSSLConnection())
            capabilities+="SASL PLAIN SCRAM-SHA-256 SCRAM-SHA-256-PLUS";
         else
            capabilities+="SASL PLAIN SCRAM-SHA-256";

         // OAuth2 bearer mechanisms, advertised only when enabled and (by default)
         // only over TLS.
         if (OAuth2TokenValidator::IsEnabled() && (!OAuth2TokenValidator::RequireTLS() || IsSSLConnection()))
            capabilities+=" XOAUTH2 OAUTHBEARER";

         capabilities+="\r\n";
      }

      // STLS is only honoured while the connection is still cleartext: once TLS is
      // active ProtocolSTLS_ answers "-ERR Command not permitted when TLS active". It
      // was advertised in both cases, so the CAPA a client is told to re-issue after the
      // handshake (RFC 2595) still offered a command that could only fail.
      if ((GetConnectionSecurity() == CSSTARTTLSOptional ||
           GetConnectionSecurity() == CSSTARTTLSRequired) &&
          !IsSSLConnection())
         capabilities+="STLS\r\n";

      // RFC 2449 section 8: declare that an -ERR may carry an extended response code in
      // brackets. The server emits [IN-USE] when another session holds the maildrop, and
      // a client is not entitled to interpret the bracketed text unless this is present.
      capabilities+="RESP-CODES\r\n";

      // RFC 3206: authentication failures carry [AUTH] and system failures [SYS/TEMP],
      // so a client can tell "your password is wrong" (reprompt the user) from "the
      // server is having trouble" (retry silently later) without parsing prose. The
      // distinction matters most to clients polling on a schedule: without it, a
      // transient database problem makes them nag the user for a password that was
      // never wrong.
      capabilities+="AUTH-RESP-CODE\r\n";

      // RFC 2449 section 6.9. Deliberately without a version: the string exists so an
      // implementation-specific workaround can be keyed on it, which the name alone
      // enables, while the exact patch level would hand an attacker a lookup key into
      // fixed-in-version security advisories for the cost of one CAPA command.
      capabilities+="IMPLEMENTATION hMailServer\r\n";

      // RFC 6856: advertise UTF-8 support so clients may issue the UTF8 command.
      capabilities+="UTF8\r\n";

      String response = "+OK CAPA list follows\r\n" + capabilities + ".";
      EnqueueWrite_(response);
   }

   void
   POP3Connection::ProtocolUTF8_()
   {
      // RFC 6856: the UTF8 command is only valid in the AUTHORIZATION state, before
      // a mailbox is selected. It tells the server the client can handle UTF-8 in
      // responses (and may send UTF-8 in the USER name). hMailServer already stores
      // and transmits message data verbatim as bytes, so enabling the mode simply
      // records the client's intent and acknowledges it.
      if (current_state_ != AUTHORIZATION)
      {
         EnqueueWrite_("-ERR UTF8 command only valid in AUTHORIZATION state.");
         return;
      }

      utf8_enabled_ = true;
      EnqueueWrite_("+OK UTF8 enabled");
   }

   void
   POP3Connection::ProtocolQUIT_()
   {
      // NEED FOR BUG FIX: Unlock should not be allowed unless user was auth'd
      // because next pop check is allowed even if in-use!
      // Problem is people have grown accustomed to this & could cause more
      // issues if fixed.
      SaveMailboxChanges_();
      UnlockMailbox_();

      EnqueueWrite_("+OK POP3 server saying goodbye...");
   }

   void
   POP3Connection::ProtocolUSER_(const String &Parameter)
   {
      if (GetConnectionSecurity() == CSSTARTTLSRequired &&
          !IsSSLConnection())
      {
         EnqueueWrite_("-ERR STLS is required.");
         return;
      }

      if (GetSecurityRange()->GetRequireTLSForAuth() && !IsSSLConnection())
      {
         EnqueueWrite_("-ERR A SSL/TLS-connection is required for authentication.");
         return;
      }

      // RFC 1939: the argument to USER is mandatory. An empty or all-whitespace one was
      // accepted and answered "+OK Send your password", and the PASS that followed then
      // spent one of this connection's ten logon attempts - and an auto-ban strike
      // against the client's IP - on a name the client never supplied. Trimming before
      // the test also means "USER  name" resolves, where the second space used to become
      // part of the address and the logon simply failed.
      String sName = Parameter;
      sName.TrimLeft();
      sName.TrimRight();

      if (sName.IsEmpty())
      {
         EnqueueWrite_("-ERR Missing user name.");
         return;
      }

      // Apply domain aliases to the user name.
      std::shared_ptr<DomainAliases> pDA = ObjectCache::Instance()->GetDomainAliases();
      username_ = pDA->ApplyAliasesOnAddress(sName);
      EnqueueWrite_("+OK Send your password" );
   }

   bool
   POP3Connection::ProtocolSTLS_()
   {
      if (GetConnectionSecurity() == CSSTARTTLSOptional ||
         GetConnectionSecurity() == CSSTARTTLSRequired)
      {
         if (IsSSLConnection())
         {
            EnqueueWrite_("-ERR Command not permitted when TLS active");
            return false;
         }

         // Discard anything the client told us before the handshake, in the same spirit
         // as RFC 3207 section 4.2 for SMTP STARTTLS. A man in the middle can inject
         // cleartext commands ahead of the client's STLS; TCPConnection clears the
         // receive buffer when the handshake completes, so injected lines are not
         // executed afterwards, but a USER command that had already been parsed left its
         // name in username_. A PASS arriving after the handshake would then have
         // completed a logon for an identity the genuine client never named. Clients
         // issue STLS before USER (that is the point of STLS), so nothing legitimate is
         // relying on the name surviving.
         username_.Empty();
         password_.Empty();

         EnqueueWrite_("+OK Begin TLS negotiation");
         EnqueueHandshake();
         return true;
      }
      else
      {
         EnqueueWrite_("-ERR Invalid command in current state.");
         return false;
      }
   }

   POP3Connection::ParseResult
   POP3Connection::ProtocolPASS_(const String &Parameter)
   {
      // RFC 1939: PASS is only valid after a successful USER. A bare PASS used to run a
      // full logon against an empty user name, and losing that logon has three side
      // effects that a command carrying no identity has not earned: OnClientLogon fires
      // with a blank username, AccountLogon::Logon registers a failed login against the
      // client's IP and so feeds the auto-ban, and one of this connection's ten attempts
      // is spent. Refuse it and leave all three alone.
      if (username_.IsEmpty())
      {
         EnqueueWrite_("-ERR Send USER first.");
         return ResultNormalResponse;
      }

      password_ = Parameter;
      return FinishPasswordLogin_();
   }

   void
   POP3Connection::FireOnClientLogon_(const String &sUsername, bool isAuthenticated)
   {
      if (!Configuration::Instance()->GetUseScriptServer())
         return;

      std::shared_ptr<ScriptObjectContainer> pContainer = std::shared_ptr<ScriptObjectContainer>(new ScriptObjectContainer);
      std::shared_ptr<ClientInfo> pClientInfo = std::shared_ptr<ClientInfo>(new ClientInfo);

      pClientInfo->SetUsername(sUsername);
      pClientInfo->SetIPAddress(GetIPAddressString());
      pClientInfo->SetPort(GetLocalEndpointPort());
      pClientInfo->SetSessionID(GetSessionID());
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

   POP3Connection::ParseResult
   POP3Connection::FinishPasswordLogin_()
   {
      AccountLogon accountLogon;
      bool disconnect = false;
      String sUsername = username_;
      account_ = accountLogon.Logon(GetRemoteEndpointAddress(), username_, password_, disconnect);

      if (disconnect)
      {
         EnqueueWrite_("-ERR [AUTH] Invalid user name or password. Too many invalid logon attempts.");
         return ResultDisconnect;
      }

      const bool isAuthenticated = account_ != nullptr;

      FireOnClientLogon_(sUsername, isAuthenticated);

      if (!account_)
      {
         // Defense-in-depth on top of the per-IP auto-ban: never let a single
         // connection make an unbounded number of authentication attempts, even
         // when the auto-ban feature is disabled.
         authentication_failure_count_++;
         if (authentication_failure_count_ >= 10)
         {
            EnqueueWrite_("-ERR [AUTH] Invalid user name or password. Too many invalid logon attempts.");
            return ResultDisconnect;
         }

         if (username_.Find(_T("@")) == -1)
            EnqueueWrite_("-ERR [AUTH] Invalid user name or password. Please use full email address as user name.");
         else
            EnqueueWrite_("-ERR [AUTH] Invalid user name or password.");

         return ResultNormalResponse;
      }

      return HandleSuccessfulLogin_();
   }

   POP3Connection::ParseResult
   POP3Connection::HandleSuccessfulLogin_()
   {
      // Try to lock mailbox.
      if (!POP3Sessions::Instance()->Lock(account_->GetID()))
      {
         // Another session owns the lock. Drop our account reference so the
         // disconnect path cannot release a lock this session never held.
         account_.reset();

         // RFC 2449 extended response code [IN-USE] (advertised as RESP-CODES in CAPA):
         // the credentials were accepted and only the maildrop was unavailable, which is
         // transient and worth retrying. Without the code this reply is indistinguishable
         // from a rejected password to a client that reads only "-ERR", which is why a
         // second mail client on the same account reports a wrong password.
         EnqueueWrite_("-ERR [IN-USE] Your mailbox is already locked");
         return ResultNormalResponse;
      }

      mailbox_locked_ = true;

      if (!Application::Instance()->GetFolderManager()->GetInboxMessages((int) account_->GetID(), messages_))
      {
         // "-ERR" is the only failure indicator in POP3; "+ERR" was read as
         // success by clients that only test the leading character.
         EnqueueWrite_("-ERR [SYS/TEMP] Server error: Failed to fetch messages in Inbox.");
         return ResultNormalResponse;
      }

      ResetMailbox_();

      EnqueueWrite_("+OK Mailbox locked and ready" );
      current_state_ = TRANSACTION;
      
      return ResultNormalResponse;
   }

   POP3Connection::ParseResult
   POP3Connection::ProtocolAUTH_(const String &sParameter)
   {
      if (GetConnectionSecurity() == CSSTARTTLSRequired && !IsSSLConnection())
      {
         EnqueueWrite_("-ERR STLS is required.");
         return ResultNormalResponse;
      }

      if (GetSecurityRange()->GetRequireTLSForAuth() && !IsSSLConnection())
      {
         EnqueueWrite_("-ERR A SSL/TLS-connection is required for authentication.");
         return ResultNormalResponse;
      }

      if (sParameter.IsEmpty())
      {
         // RFC 5034: list the supported SASL mechanisms. SCRAM-SHA-256-PLUS is only
         // offered on a TLS connection, where channel binding is meaningful.
         String sMechanisms = "PLAIN\r\nSCRAM-SHA-256\r\n";
         if (IsSSLConnection())
            sMechanisms += "SCRAM-SHA-256-PLUS\r\n";

         // OAuth2 bearer mechanisms are advertised only when enabled and (by default)
         // only over TLS, since a bearer token is a directly replayable credential.
         if (OAuth2TokenValidator::IsEnabled() && (!OAuth2TokenValidator::RequireTLS() || IsSSLConnection()))
            sMechanisms += "XOAUTH2\r\nOAUTHBEARER\r\n";

         EnqueueWrite_("+OK List of SASL mechanisms follows\r\n" + sMechanisms + ".");
         return ResultNormalResponse;
      }

      std::vector<String> parts = StringParser::SplitString(sParameter, " ");
      String mechanism = parts[0];
      mechanism.MakeUpper();

      // RFC 4959 SASL-IR: a second token is an initial response. A single "=" is the
      // wire form of an initial response that is *empty* - it is still a response, so
      // it must be processed (and fail as an empty credential) rather than be mistaken
      // for "no initial response". Treating "=" as absent made the server send a "+"
      // continuation the client was not waiting for, and the session then desynchronised:
      // the client's next real command was consumed as SASL continuation data.
      bool hasInitialResponse = parts.size() >= 2;
      String initialResponse;
      if (hasInitialResponse && parts[1] != _T("="))
         initialResponse = parts[1];

      if (mechanism == _T("PLAIN"))
      {
         if (hasInitialResponse)
            return ProcessAuthPlain_(initialResponse);

         sasl_plain_pending_ = true;
         EnqueueWrite_("+ ");
         return ResultNormalResponse;
      }

      if (mechanism == _T("SCRAM-SHA-256-PLUS"))
      {
         // Channel binding only has meaning over TLS; the mechanism is advertised
         // (and accepted) only on a TLS connection.
         if (!IsSSLConnection())
         {
            EnqueueWrite_("-ERR SCRAM-SHA-256-PLUS requires a TLS connection.");
            return ResultNormalResponse;
         }

         // Bind the exchange to this TLS channel via the server certificate
         // (RFC 5929 tls-server-end-point).
         std::vector<unsigned char> cbindData;
         if (!GetTlsServerEndPoint(cbindData))
         {
            EnqueueWrite_("-ERR Channel binding is not available on this connection.");
            return ResultNormalResponse;
         }

         scram_session_ = std::make_shared<ScramSha256>();
         scram_session_->SetChannelBinding(cbindData);

         if (hasInitialResponse)
            return ProcessScramClientFirst_(initialResponse);

         EnqueueWrite_("+ ");
         return ResultNormalResponse;
      }

      if (mechanism == _T("SCRAM-SHA-256"))
      {
         scram_session_ = std::make_shared<ScramSha256>();

         // On a TLS connection the server also advertises SCRAM-SHA-256-PLUS, so a
         // non-PLUS client that sends the 'y' gs2 flag is signalling a stripped-PLUS
         // downgrade and is rejected (RFC 5802 section 6).
         if (IsSSLConnection())
            scram_session_->SetServerSupportsChannelBinding();

         if (hasInitialResponse)
            return ProcessScramClientFirst_(initialResponse);

         EnqueueWrite_("+ ");
         return ResultNormalResponse;
      }

      if (mechanism == _T("XOAUTH2") || mechanism == _T("OAUTHBEARER"))
      {
         if (!OAuth2TokenValidator::IsEnabled())
         {
            EnqueueWrite_("-ERR Unsupported authentication mechanism.");
            return ResultNormalResponse;
         }

         if (OAuth2TokenValidator::RequireTLS() && !IsSSLConnection())
         {
            EnqueueWrite_("-ERR A SSL/TLS-connection is required for OAuth2 authentication.");
            return ResultNormalResponse;
         }

         if (hasInitialResponse)
            return ProcessAuthBearer_(initialResponse);

         sasl_bearer_pending_ = true;
         EnqueueWrite_("+ ");
         return ResultNormalResponse;
      }

      // Everything else is refused on purpose, and the roster of absentees is a
      // set of decisions rather than a backlog. APOP and CRAM-MD5 both require
      // the server to hold a cleartext-equivalent secret (APOP hashes the
      // password with a banner timestamp; CRAM-MD5 HMACs it), which this
      // server's Argon2id/SCRAM password store rightly cannot produce.
      // DIGEST-MD5 was moved to Historic by RFC 6331 for its documented
      // defects. NTLM is proprietary legacy with its own downgrade problems.
      // EXTERNAL could not work today: the inbound TLS contexts do not request
      // a client certificate. The modern replacements are all above this line -
      // SCRAM-SHA-256(-PLUS) and the OAuth2 bearers - so a mechanism arriving
      // here is one nobody should enable in 2026, not one waiting for code.
      EnqueueWrite_("-ERR Unsupported authentication mechanism.");
      return ResultNormalResponse;
   }

   POP3Connection::ParseResult
   POP3Connection::ProcessAuthPlain_(const String &sBase64)
   {
      // SASL PLAIN (RFC 4616): authzid NUL authcid NUL passwd, UTF-8 encoded.
      String sAuthzid, sUser, sPassword;
      if (!StringParser::DecodeSaslPlain(sBase64, sAuthzid, sUser, sPassword) || sUser.IsEmpty())
      {
         EnqueueWrite_("-ERR Invalid SASL PLAIN response.");
         return ResultNormalResponse;
      }

      // RFC 4422/4013: prepare the authcid (username) with SASLprep before lookup.
      String sPreppedUser;
      if (!StringParser::SaslPrep(sUser, sPreppedUser))
      {
         EnqueueWrite_("-ERR Invalid SASL PLAIN response.");
         return ResultNormalResponse;
      }

      // Apply domain aliases to the user name (parity with the USER command).
      std::shared_ptr<DomainAliases> pDA = ObjectCache::Instance()->GetDomainAliases();
      username_ = pDA->ApplyAliasesOnAddress(sPreppedUser);
      password_ = sPassword;

      return FinishPasswordLogin_();
   }

   POP3Connection::ParseResult
   POP3Connection::ProcessAuthBearer_(const String &sBase64)
   {
      // The SASL XOAUTH2 / OAUTHBEARER client response is ASCII (user=/auth=Bearer and
      // a base64url JWT), so the standard base64 decode is sufficient here.
      String sDecodedW;
      StringParser::Base64Decode(sBase64, sDecodedW);
      AnsiString sDecoded = sDecodedW;

      AnsiString sIdentity, sToken;
      String sTokenUser;
      bool authenticated = false;

      if (OAuth2TokenValidator::ParseSaslBearer(sDecoded, sIdentity, sToken))
      {
         AnsiString sError;
         if (OAuth2TokenValidator::ValidateBearerToken(sToken, sTokenUser, sError))
         {
            // The login identity is the validated token's username claim. When the
            // client also asserts an identity (user=/a=), it must match the token.
            AnsiString sTokenUserA = sTokenUser;
            if (sIdentity.IsEmpty() || sIdentity.CompareNoCase(sTokenUserA) == 0)
               authenticated = true;
         }
      }

      std::shared_ptr<const Account> pAccount;
      String sAddress = sTokenUser;
      if (authenticated)
      {
         std::shared_ptr<DomainAliases> pDA = ObjectCache::Instance()->GetDomainAliases();
         sAddress = pDA->ApplyAliasesOnAddress(sTokenUser);
         pAccount = LookupActiveAccount_(sAddress);
      }

      username_ = sAddress;

      FireOnClientLogon_(sAddress, pAccount != nullptr);

      if (!pAccount)
      {
         // Feed the per-IP auto-ban accounting and the per-connection brute-force cap,
         // exactly as the password and SCRAM paths do.
         AccountLogon accountLogon;
         bool disconnect = false;
         accountLogon.RegisterFailedLogin(GetRemoteEndpointAddress(), sAddress, disconnect);

         authentication_failure_count_++;
         if (disconnect || authentication_failure_count_ >= 10)
         {
            EnqueueWrite_("-ERR [AUTH] Invalid authentication token. Too many invalid logon attempts.");
            return ResultDisconnect;
         }

         EnqueueWrite_("-ERR [AUTH] Invalid authentication token.");
         return ResultNormalResponse;
      }

      account_ = pAccount;
      return HandleSuccessfulLogin_();
   }

   POP3Connection::ParseResult
   POP3Connection::ContinueScram_(const String &sRequest)
   {

      // A bare "*" cancels the SASL exchange (RFC 4954 / RFC 5034).
      if (sRequest == _T("*"))
      {
         scram_session_.reset();
         EnqueueWrite_("-ERR Authentication cancelled.");
         return ResultNormalResponse;
      }

      switch (scram_session_->GetState())
      {
      case ScramSha256::NeedClientFirst:
         return ProcessScramClientFirst_(sRequest);
      case ScramSha256::NeedClientFinal:
         return ProcessScramClientFinal_(sRequest);
      case ScramSha256::NeedAck:
         return ProcessScramAck_();
      default:
         scram_session_.reset();
         EnqueueWrite_("-ERR Invalid authentication state.");
         return ResultNormalResponse;
      }
   }

   POP3Connection::ParseResult
   POP3Connection::ProcessScramClientFirst_(const String &sBase64)
   {
      String sDecoded;
      StringParser::Base64Decode(sBase64, sDecoded);
      AnsiString clientFirst = sDecoded;

      AnsiString username;
      if (!ScramSha256::ExtractUsername(clientFirst, username))
      {
         scram_session_.reset();
         EnqueueWrite_("-ERR Invalid SCRAM client-first message.");
         return ResultNormalResponse;
      }

      // Canonicalize the user name the same way the IMAP/SMTP SCRAM paths do.
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
         EnqueueWrite_("-ERR Invalid SCRAM client-first message.");
         return ResultNormalResponse;
      }

      String sServerFirst = serverFirst;
      String sEncoded;
      StringParser::Base64Encode(sServerFirst, sEncoded);

      EnqueueWrite_("+ " + sEncoded);
      return ResultNormalResponse;
   }

   POP3Connection::ParseResult
   POP3Connection::ProcessScramClientFinal_(const String &sBase64)
   {
      String sDecoded;
      StringParser::Base64Decode(sBase64, sDecoded);
      AnsiString clientFinal = sDecoded;

      AnsiString serverFinal;
      if (!scram_session_->ProcessClientFinal(clientFinal, serverFinal))
         return ScramAuthFailed_();

      String sServerFinal = serverFinal;
      String sEncoded;
      StringParser::Base64Encode(sServerFinal, sEncoded);

      // Send the server-final (v=...) as a challenge; the client acknowledges with an
      // empty line, then we complete the authentication.
      EnqueueWrite_("+ " + sEncoded);
      return ResultNormalResponse;
   }

   POP3Connection::ParseResult
   POP3Connection::ProcessScramAck_()
   {
      std::shared_ptr<const Account> pAccount = scram_session_ ? scram_session_->GetAccount() : nullptr;
      String sUsername = scram_session_ ? String(scram_session_->GetUsername()) : username_;

      scram_session_.reset();

      if (!pAccount)
         return ScramAuthFailed_();

      account_ = pAccount;

      FireOnClientLogon_(sUsername, true);

      return HandleSuccessfulLogin_();
   }

   POP3Connection::ParseResult
   POP3Connection::ScramAuthFailed_()
   {
      String sUsername = scram_session_ ? String(scram_session_->GetUsername()) : username_;
      scram_session_.reset();

      // Report the failed attempt to the script server with Client.Authenticated =
      // False, exactly as the USER/PASS and bearer paths do. Without this, a SCRAM
      // brute-force attempt was invisible to OnClientLogon, so script-based lockout
      // and auditing saw nothing at all.
      FireOnClientLogon_(sUsername, false);

      // Feed the per-IP auto-ban accounting (parity with the USER/PASS path).
      AccountLogon accountLogon;
      bool disconnect = false;
      accountLogon.RegisterFailedLogin(GetRemoteEndpointAddress(), sUsername, disconnect);

      authentication_failure_count_++;

      // Per-connection brute-force cap (effective even when auto-ban is disabled).
      if (disconnect || authentication_failure_count_ >= 10)
      {
         EnqueueWrite_("-ERR [AUTH] Invalid user name or password. Too many invalid logon attempts.");
         return ResultDisconnect;
      }

      EnqueueWrite_("-ERR [AUTH] Invalid user name or password.");
      return ResultNormalResponse;
   }

   std::shared_ptr<const Account>
   POP3Connection::LookupPbkdf2Account_(const String &sAddress)
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

   std::shared_ptr<const Account>
   POP3Connection::LookupActiveAccount_(const String &sAddress)
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

   bool
   POP3Connection::ProtocolDELE_(const String &Parameter)
   {
      String sInputParam;
      sInputParam = Parameter;
      sInputParam.TrimLeft();
      sInputParam.TrimRight();
      long lMessageID = _ttol(sInputParam);

      if (!account_)
      {
         EnqueueWrite_("-ERR No such message (messages not loaded)");
         return true;
      }

      std::shared_ptr<Message> message = GetMessage_(lMessageID);
      if (!message)
      {
         EnqueueWrite_("-ERR No such message");
         return true;
      }

      // RFC 1939 DELE: a message already marked deleted must be refused, not marked
      // again. STAT, RETR, TOP, the scan listings and - since the audit fixes - LIST n
      // and UIDL n all treat a flagged message as absent; this was the one command left
      // where a deleted message still answered as though it were present, so a client
      // resyncing after a lost response could not tell its DELE had already been taken.
      //
      // The text deliberately keeps the "No such message" prefix: Shared/
      // POP3ClientSimulator.cs waits for one of two literal replies and lives outside
      // this developer's files, so an entirely new string would hang every test that
      // calls its DELE helper.
      if (message->GetFlagDeleted())
      {
         EnqueueWrite_("-ERR No such message (already deleted)");
         return true;
      }

      message->SetFlagDeleted(true);
      EnqueueWrite_("+OK msg deleted");

      return true;
   }

   bool
   POP3Connection::ProtocolLIST_(const String &sParameter)
   {
      if (!account_)
      {
         EnqueueWrite_("-ERR Message list not loaded");
         return true;
      }

      String sResponse;

      if (sParameter.IsEmpty())
      {
         int iMessageCount = 0;
         __int64 iTotalBytes = 0;
         GetMailboxContents_(iMessageCount, iTotalBytes);

         // preallocate memory to speed up a bit.
         sResponse.reserve(iMessageCount * 10);

         sResponse.Format(_T("+OK %d messages (%I64d octets)"), iMessageCount, iTotalBytes);
         EnqueueWrite_(sResponse);
      
      
         sResponse = "";
         String sRow;

         int index = 0;
         for(std::shared_ptr<Message> pMessage : messages_)
         {
            index++;
            if (!pMessage->GetFlagDeleted())
            {
               sRow.Format(_T("%d %d\r\n"), index, pMessage->GetSize());
               sResponse.append(sRow);
            }
         }

         sResponse += ".";
         
         // --- Send data to client.
         EnqueueWrite_(sResponse);

      }
      else
      {
         // List only one single message.
         long messageIndex = _ttol(sParameter);

         std::shared_ptr<Message> pMessage = GetMessage_(messageIndex);

         // RFC 1939: a message marked as deleted must be treated as if it no longer
         // exists, so the single-message form has to answer "-ERR" for it just as the
         // scan listing above leaves it out. Reporting a size for a message the client
         // has already deleted made LIST and LIST n disagree within one session, and
         // clients that trust LIST n went on to RETR a message RETR refuses.
         if (pMessage && !pMessage->GetFlagDeleted())
            sResponse.Format(_T("+OK %d %d"), messageIndex, pMessage->GetSize());
         else
            sResponse = "-ERR No such message";

         EnqueueWrite_(sResponse);
      }

      return true;
   }

   bool
   POP3Connection::ProtocolUIDL_(const String &Parameter)
   {
      // Display a list of all the messages (index and size)
      if (!account_)
      {
         EnqueueWrite_("-ERR Message list not loaded");
         return true;
      }

      String sResponse;
      String sRow;
      if (Parameter.IsEmpty())
      {
         int iMessageCount = 0;
         __int64 iTotalBytes = 0;
         GetMailboxContents_(iMessageCount, iTotalBytes);

         // Allocate a reasonable size...
         sResponse.SetBuf(messages_.size() * 10);

         // Send number of messages.
         sResponse.Format(_T("+OK %d messages (%I64d octets)\r\n"), iMessageCount, iTotalBytes);
      

         // List the actual messages.
         int index = 0;
         for(std::shared_ptr<Message> pMessage : messages_)
         {
            index++;
            if (!pMessage->GetFlagDeleted())
            {
               sRow.Format(_T("%d %u\r\n"), index, pMessage->GetUID());
               sResponse.append(sRow);
            }
         }

         sResponse += ".";
      }
      else
      {  
         // List only one single message.
         long messageIndex = _ttol(Parameter);
         std::shared_ptr<Message> pMessage = GetMessage_(messageIndex);

         // RFC 1939: a message marked as deleted is treated as absent, so the
         // single-message form answers "-ERR" for it - the same rule the scan listing
         // above already applies.
         if (pMessage && !pMessage->GetFlagDeleted())
            sResponse.Format(_T("+OK %d %u"), messageIndex, pMessage->GetUID());
         else
            sResponse = "-ERR No such message";
      }

      // --- Send data to client.
      EnqueueWrite_(sResponse);

      return true;   
   }

   POP3Connection::ParseResult
   POP3Connection::ProtocolRETR_(const String &Parameter)
   {
      if (!account_)
      {
         EnqueueWrite_("-ERR Message list not loaded");
         return ResultNormalResponse;
      }

      int iRequestedMessageIndex = _tstol(Parameter);

      std::shared_ptr<Message> pMessage = GetMessage_(iRequestedMessageIndex);

      if (!pMessage || pMessage->GetFlagDeleted())
      {
         EnqueueWrite_("-ERR No such message");
         return ResultNormalResponse;
      }

      PersistentMessage::EnsureFileExistance(account_, pMessage);

      if (!StartSendFile_(pMessage))
      {
         // The file could not be opened. Answer with an error rather than
         // starting a transfer the client will never receive - previously the
         // session was left with no response at all.
         EnqueueWrite_("-ERR Unable to read the message file");
         return ResultNormalResponse;
      }

      return ResultStartSendMessage;
   }

   bool
   POP3Connection::StartSendFile_(std::shared_ptr<Message> message)
   {
      String fileName = PersistentMessage::GetFileName(account_, message);

      transmission_buffer_.Initialize(shared_from_this());

      bool opened = false;
      try
      {
         // Open returns false for a missing or locked file; it only throws when
         // the File object is already open. Both have to be handled, or the
         // code below streams a file that was never opened.
         opened = current_file_.Open(fileName, File::OTReadOnly);
      }
      catch (...)
      {
         opened = false;
      }

      if (!opened)
      {
         String sErrorMessage;
         sErrorMessage.Format(_T("Could not send file %s via socket since it could not be opened."), fileName.c_str());
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5063, "POP3Connection::_SendFile", sErrorMessage);
         return false;
      }

      String responseTemp;
      responseTemp.Format(_T("+OK %d octets\r\n"), message->GetSize());
      AnsiString responseString = responseTemp;

      transmission_buffer_.Append((BYTE*) responseString.GetBuffer(), responseString.GetLength());

	  ReadAndSend_();

      return true;
   }

   void 
   POP3Connection::ReadAndSend_()
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

         // No data was sent. Send some more...
         pBuffer = current_file_.ReadChunk(bufferSize);
      }

      // We're done. Cleanup...
      current_file_.Close();

      // No more data to send. Make sure all buffered data is flushed.
      transmission_buffer_.Flush(true);

      if (!transmission_buffer_.GetLastSendEndedWithNewline())
         EnqueueWrite_(""); // Send a newline character now.

      EnqueueWrite_(".");

      // Request new data from the client now.
      EnqueueRead();
   }
   
   void
   POP3Connection::OnDataSent()
   {
      // Are we currently sending a file to the client?
      if (!current_file_.IsOpen())
      {
         // No. Nothing to do now.
         return;
      }

      ReadAndSend_();
   }


   bool
   POP3Connection::ProtocolTOP_(const String &Parameter)
   {
      if (!account_)
      {
         EnqueueWrite_("-ERR Message list not loaded");
         return true;
      }

      String Msg;

      long iNoOfLines = 0;
      int iSpacePos = Parameter.Find(_T(" "));
      if (iSpacePos >= 0)
      {
         Msg = Parameter.Mid(0, iSpacePos);

         String sNoOfLines;
         sNoOfLines = Parameter.Mid(iSpacePos + 1);
         sNoOfLines.TrimLeft();
         sNoOfLines.TrimRight();

         if (!sNoOfLines.IsEmpty())
         {
            // RFC 1939 defines the second argument as "a non-negative number of lines".
            // A negative or non-numeric count fell out of _ttoi as 0, so "TOP 1 -5" and
            // "TOP 1 rubbish" were quietly answered as if the client had asked for the
            // headers alone - the server obeyed a command that had not been given.
            // Refused here, before the maildrop is touched, so nothing changes state.
            if (!StringParser::IsNumeric(sNoOfLines))
            {
               EnqueueWrite_("-ERR Invalid number of lines");
               return true;
            }

            iNoOfLines = _ttol(sNoOfLines);
         }
      }
      else
         Msg = Parameter;

      // A count that is absent rather than invalid stays permissive (headers only). That
      // is long-standing behaviour here, and Shared/POP3ClientSimulator.cs - owned
      // elsewhere - sends "TOP n" with no count whenever a test asks for zero lines.

      int iRequestedMessageIndex = _tstol(Msg);

      std::shared_ptr<Message> pMessage = GetMessage_(iRequestedMessageIndex);

      if (!pMessage || pMessage->GetFlagDeleted())
      {
         EnqueueWrite_("-ERR No such message");
      }
      else
      {
         // Recreate a missing file the same way RETR does, so a database that is
         // out of step with the message store answers with an error rather than
         // an empty body.
         PersistentMessage::EnsureFileExistance(account_, pMessage);

         String fileName = PersistentMessage::GetFileName(account_, pMessage);

         String sResponse;
         sResponse.Format(_T("+OK %d octets"), pMessage->GetSize());

         bool endedWithNewline = true;
         if (!SendFileHeader_(fileName, iNoOfLines, sResponse, endedWithNewline))
         {
            EnqueueWrite_("-ERR Unable to read the message file");
            return true;
         }

         // RFC 1939: a multi-line response ends CRLF "." CRLF. "\r\n." was written
         // unconditionally, and a stored message already ends with CRLF, so TOP handed
         // the client one blank line of body that RETR does not - the same message
         // fetched the two ways differed. The newline is still needed when the file's
         // last line has none, which is the case RETR covers with
         // GetLastSendEndedWithNewline.
         if (endedWithNewline)
            EnqueueWrite_(".");
         else
            EnqueueWrite_("\r\n.");
      }
        

      return true;
   }

   void
   POP3Connection::OnDisconnect()
   {
      UnlockMailbox_();   
   }


   void
   POP3Connection::UnlockMailbox_()
   {
      if (!account_)
         return;

      // Only release the lock if this session actually acquired it.
      if (mailbox_locked_)
      {
         POP3Sessions::Instance()->Unlock(account_->GetID());
         mailbox_locked_ = false;
      }

      account_.reset();
   }

   bool
   POP3Connection::SendFileHeader_(const String &sFilename, int iNoOfLines, const String &responseOnceOpen, bool &endedWithNewline)
   {
      // An unreadable file, or one with no lines at all, needs no newline of its own
      // before the terminating ".".
      endedWithNewline = true;

      File file;
      bool opened = false;
      try
      {
         // Open returns false for a missing or unreadable file and only throws
         // when the File object is already open; handle both.
         opened = file.Open(sFilename, File::OTReadOnly);
      }
      catch (...)
      {
         opened = false;
      }

      if (!opened)
         return false;

      // The success response is sent from here so that it is only sent once the
      // file is known to be readable.
      if (!responseOnceOpen.IsEmpty())
         EnqueueWrite_(responseOnceOpen);

      int current_body_line_count = 0;
      bool header_sent = false;
      AnsiString line;
      
      AnsiString output_buffer;

      while (file.ReadLine(line))
      {
         // Perform dot stuffing
         if (line.StartsWith("."))
            line = "." + line;

         if (header_sent)
         {
            if (iNoOfLines <= 0)
               break;

            output_buffer += line;
            endedWithNewline = line.EndsWith("\n");

            current_body_line_count++;

            if (current_body_line_count == iNoOfLines)
               break;
         }
         else
         {
            if (line == "\r\n")
               header_sent = true;

            output_buffer += line;
            endedWithNewline = line.EndsWith("\n");
         }

         if (output_buffer.size() > 10000)
         {
            EnqueueWrite(output_buffer);
            output_buffer.Empty();
         }
      }

      if (!output_buffer.IsEmpty())
      {
         EnqueueWrite(output_buffer);
      }

      return true;
   
   }

   void
   POP3Connection::EnqueueWrite_(const String &sData)
   {
      if (Logger::Instance()->GetLogPOP3())
      {
         String sLogData = "SENT: " + sData;
         sLogData.TrimRight(_T("\r\n"));

         LOG_POP3(GetSessionID(),GetIPAddressString(), sLogData);
      }

      EnqueueWrite(sData + "\r\n");
   }

   void
   POP3Connection::SaveMailboxChanges_()
   {
      // Delete messages that has the delete flag on.
      if (account_)
      {
         std::set<int> messagesToDelete;
         for(std::shared_ptr<Message> message : messages_)
         {
            if (message->GetFlagDeleted())
               messagesToDelete.insert(message->GetUID());
         }

         Application::Instance()->GetFolderManager()->DeleteInboxMessages((int) account_->GetID(), messagesToDelete, std::bind(&POP3Connection::UpdateAutoLogoutTimer, this));
      }
   }

   bool 
   POP3Connection::ProtocolSTAT_(const String &sParameter)
   {
      int iMessageCount = 0;
      // Fix for negative STAT results when box over 2GB
      __int64 iTotalSize = 0;

      auto iter = messages_.begin();
      auto iterEnd = messages_.end();

      for (; iter != iterEnd; iter++)
      {
         std::shared_ptr<Message> pMessage = (*iter);

         if (!pMessage->GetFlagDeleted())
         {
            iMessageCount++;
            iTotalSize += pMessage->GetSize();
         }
      }

      // --- Display basic status (number of messages and total mailbox size)
      String sResponse; 
      sResponse.Format(_T("+OK %d %I64d"), iMessageCount, iTotalSize);

      EnqueueWrite_(sResponse);

      return true;

   }

   void
   POP3Connection::GetMailboxContents_(int &iNoOfMessages, __int64 &iTotalBytes)
   {
      iNoOfMessages = 0;
      iTotalBytes = 0;

      auto iter = messages_.begin();
      auto iterEnd = messages_.end();

      for (; iter != iterEnd; iter++)
      {
         std::shared_ptr<Message> pMessage = (*iter);

         if (!pMessage->GetFlagDeleted())
         {
            iNoOfMessages++;
            iTotalBytes += pMessage->GetSize();
         }
      }
   }

   std::shared_ptr<Message>
   POP3Connection::GetMessage_(unsigned int index)
   {
      std::shared_ptr<Message> result;
      if (index >= 1 && index <= messages_.size())
         result = messages_[index-1];

      return result;
   }

   void
   POP3Connection::ResetMailbox_()
   {
         /* 
         Reset the mailbox state. The POP3 rfc specifies the following:

            After the POP3 server has opened the maildrop, it assigns a message-
            number to each message, and notes the size of each message in octets.
            The first message in the maildrop is assigned a message-number of
            "1", the second is assigned "2", and so on, so that the nth message
            in a maildrop is assigned a message-number of "n". 

         If there are messages marked for deletion in the mailbox, this woulnd't work.

      */
      for(std::shared_ptr<Message> message : messages_)
         message->SetFlagDeleted(false);
   }
}