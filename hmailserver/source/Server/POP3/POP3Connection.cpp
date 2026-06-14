// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
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
      sasl_plain_pending_(false)
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
      if (sasl_plain_pending_)
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
      if (command == _T("STLS"))
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
      // http://www.hmailserver.com/forum/viewtopic.php?f=7&t=22361
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

      if (IsSSLConnection() || GetConnectionSecurity() != CSSTARTTLSRequired)
      {
         capabilities+="USER\r\n";
         // RFC 5034: advertise the SASL mechanisms available for the AUTH command.
         capabilities+="SASL PLAIN SCRAM-SHA-256\r\n";
      }

      if (GetConnectionSecurity() == CSSTARTTLSOptional ||
          GetConnectionSecurity() == CSSTARTTLSRequired)
         capabilities+="STLS\r\n";

      String response = "+OK CAPA list follows\r\n" + capabilities + ".";
      EnqueueWrite_(response);
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

      // Apply domain aliases to the user name.
      std::shared_ptr<DomainAliases> pDA = ObjectCache::Instance()->GetDomainAliases();
      username_ = pDA->ApplyAliasesOnAddress(Parameter);
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
         EnqueueWrite_("-ERR Invalid user name or password. Too many invalid logon attempts.");
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
            EnqueueWrite_("-ERR Invalid user name or password. Too many invalid logon attempts.");
            return ResultDisconnect;
         }

         if (username_.Find(_T("@")) == -1)
            EnqueueWrite_("-ERR Invalid user name or password. Please use full email address as user name.");
         else
            EnqueueWrite_("-ERR Invalid user name or password.");

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
         EnqueueWrite_("-ERR Your mailbox is already locked");
         return ResultNormalResponse; 
      }
  
      if (!Application::Instance()->GetFolderManager()->GetInboxMessages((int) account_->GetID(), messages_))
      {
         EnqueueWrite_("+ERR Server error: Failed to fetch messages in Inbox.");
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
         // RFC 5034: list the supported SASL mechanisms.
         EnqueueWrite_("+OK List of SASL mechanisms follows\r\nPLAIN\r\nSCRAM-SHA-256\r\n.");
         return ResultNormalResponse;
      }

      std::vector<String> parts = StringParser::SplitString(sParameter, " ");
      String mechanism = parts[0];
      mechanism.MakeUpper();

      // RFC 4959 SASL-IR: a single "=" means an empty initial response.
      bool hasInitialResponse = parts.size() >= 2 && parts[1] != _T("=");

      if (mechanism == _T("PLAIN"))
      {
         if (hasInitialResponse)
            return ProcessAuthPlain_(parts[1]);

         sasl_plain_pending_ = true;
         EnqueueWrite_("+ ");
         return ResultNormalResponse;
      }

      if (mechanism == _T("SCRAM-SHA-256"))
      {
         scram_session_ = std::make_shared<ScramSha256>();

         if (hasInitialResponse)
            return ProcessScramClientFirst_(parts[1]);

         EnqueueWrite_("+ ");
         return ResultNormalResponse;
      }

      EnqueueWrite_("-ERR Unsupported authentication mechanism.");
      return ResultNormalResponse;
   }

   POP3Connection::ParseResult
   POP3Connection::ProcessAuthPlain_(const String &sBase64)
   {
      String sDecoded;
      StringParser::Base64Decode(sBase64, sDecoded);

      // SASL PLAIN = authzid NUL authcid NUL passwd. Base64Decode maps NUL -> tab.
      std::vector<String> parts = StringParser::SplitString(sDecoded, "\t");
      if (parts.size() != 3 || parts[1].IsEmpty())
      {
         EnqueueWrite_("-ERR Invalid SASL PLAIN response.");
         return ResultNormalResponse;
      }

      // Apply domain aliases to the user name (parity with the USER command).
      std::shared_ptr<DomainAliases> pDA = ObjectCache::Instance()->GetDomainAliases();
      username_ = pDA->ApplyAliasesOnAddress(parts[1]);
      password_ = parts[2];

      return FinishPasswordLogin_();
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

      // Feed the per-IP auto-ban accounting (parity with the USER/PASS path).
      AccountLogon accountLogon;
      bool disconnect = false;
      accountLogon.RegisterFailedLogin(GetRemoteEndpointAddress(), sUsername, disconnect);

      authentication_failure_count_++;

      // Per-connection brute-force cap (effective even when auto-ban is disabled).
      if (disconnect || authentication_failure_count_ >= 10)
      {
         EnqueueWrite_("-ERR Invalid user name or password. Too many invalid logon attempts.");
         return ResultDisconnect;
      }

      EnqueueWrite_("-ERR Invalid user name or password.");
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

      if (pAccount->GetPasswordEncryption() != Crypt::ETPBKDF2)
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
      if (message)
      {
         message->SetFlagDeleted(true);
         EnqueueWrite_("+OK msg deleted"); 
      }
      else
         EnqueueWrite_("-ERR No such message");

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

         if (pMessage)
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

         if (pMessage)
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

      StartSendFile_(pMessage);

      return ResultStartSendMessage;
   }

   void 
   POP3Connection::StartSendFile_(std::shared_ptr<Message> message)
   {  
      String fileName = PersistentMessage::GetFileName(account_, message);

      transmission_buffer_.Initialize(shared_from_this());
      
      try
      {
         current_file_.Open(fileName, File::OTReadOnly);
      }
      catch (...)
      {
         String sErrorMessage;
         sErrorMessage.Format(_T("Could not send file %s via socket since it does not exist."), fileName.c_str());
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5063, "POP3Connection::_SendFile", sErrorMessage);
         return;
      }

      String responseTemp;
      responseTemp.Format(_T("+OK %d octets\r\n"), message->GetSize());
      AnsiString responseString = responseTemp;

      transmission_buffer_.Append((BYTE*) responseString.GetBuffer(), responseString.GetLength());
      
	  ReadAndSend_();
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
         Msg = Parameter.Mid(0, Parameter.Find(_T(" ")));

         String sNoOfLines;
         sNoOfLines = Parameter.Mid(iSpacePos + 1);

         if (!sNoOfLines.IsEmpty())
         {
            iNoOfLines = _ttoi(sNoOfLines);
         }
      }
      else
         Msg = Parameter;

      bool bMsgFound = false;

      int iRequestedMessageIndex = _tstol(Msg);

      std::shared_ptr<Message> pMessage = GetMessage_(iRequestedMessageIndex);

      if (!pMessage || pMessage->GetFlagDeleted())
      {
         EnqueueWrite_("-ERR No such message");
      }
      else
      {
         String fileName = PersistentMessage::GetFileName(account_, pMessage);

         String sResponse;
         sResponse.Format(_T("+OK %d octets"), pMessage->GetSize());
         EnqueueWrite_(sResponse);

         // --- Send the file to the recipient.
         SendFileHeader_(fileName, iNoOfLines);
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

      POP3Sessions::Instance()->Unlock(account_->GetID());
      account_.reset();
   }

   bool
   POP3Connection::SendFileHeader_(const String &sFilename, int iNoOfLines)
   {
      File file;
      try
      {
         file.Open(sFilename, File::OTReadOnly);
      }
      catch (...)
      {
         return false;
      }

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

            current_body_line_count++;

            if (current_body_line_count == iNoOfLines)
               break;
         }
         else
         {
            if (line == "\r\n")
               header_sent = true;

            output_buffer += line;
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