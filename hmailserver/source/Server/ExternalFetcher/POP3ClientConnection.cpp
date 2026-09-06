// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include ".\POP3ClientConnection.h"

#include "FetchAccountUIDList.h"

#include "../Common/Scripting/Result.h"
#include "../Common/Scripting/Events.h"
#include "../Common/BO/FetchAccount.h"
#include "../Common/BO/Message.h"
#include "../Common/BO/MessageData.h"
#include "../Common/BO/Account.h"
#include "../Common/Persistence/PersistentMessage.h"
#include "../Common/Util/ByteBuffer.h"
#include "../Common/Util/OutboundOAuth2TokenClient.h"
#include "../Common/Application/IniFileSettings.h"
#include "../SMTP/RecipientParser.h"
#include "../Common/Util/Parsing/AddressListParser.h"
#include "../Common/Util/Utilities.h"
#include "../Common/Util/ServerStatus.h"
#include "../Common/Mime/Mime.h"
#include "../Common/BO/FetchAccountUID.h"
#include "../Common/BO/MessageRecipients.h"
#include "../common/util/MessageUtilities.h"
#include "../common/Threading/AsynchronousTask.h"
#include "../common/Threading/WorkQueue.h"

#include "../Common/Util/TransparentTransmissionBuffer.h"

#include "../Common/Application/TimeoutCalculator.h"

#include "../Common/Util/VariantDateTime.h"
#include "../Common/Util/Time.h"
#include "../Common/Cache/CacheContainer.h"
#include "../Common/AntiSpam/AntiSpamConfiguration.h"
#include "../Common/AntiSpam/SpamProtection.h"

#include <boost/algorithm/string.hpp>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   POP3ClientConnection::POP3ClientConnection(std::shared_ptr<FetchAccount> pAccount,
                                              ConnectionSecurity connectionSecurity,
                                              boost::asio::io_context& io_context, 
                                              boost::asio::ssl::context& context,
                                              std::shared_ptr<Event> disconnected,
                                              AnsiString remote_hostname) :
      ExternalFetchClientBase(pAccount, connectionSecurity, io_context, context, disconnected, remote_hostname),
      current_state_(StateConnected),
      retr_failed_(false),
      finalization_enqueued_tick_(0)
   {
      // Neither listing exists yet. Pointing each iterator at its own container's
      // end() is what makes the "nothing left" comparisons below well-defined before
      // the first UIDL response arrives.
      cur_download_ = uidlresponse_.end();
      cur_cleanup_ = downloaded_messages_.end();

      /*
      RFC 1939, Basic Operation
      A POP3 server MAY have an inactivity autologout timer.  Such a timer
      MUST be of at least 10 minutes' duration.

      But since we're a client, we increase this a bit.
      */

      TimeoutCalculator calculator;
      SetTimeout(calculator.Calculate(IniFileSettings::Instance()->GetPOP3CMinTimeout(), IniFileSettings::Instance()->GetPOP3CMaxTimeout()));
   }

   POP3ClientConnection::~POP3ClientConnection(void)
   {
      try
      {
         DiscardUnfinishedDownload_();
      }
      catch (...)
      {
         // A destructor must not let anything escape, and this one runs while the
         // connection is being torn down - possibly during shutdown.
      }
   }

   void
   POP3ClientConnection::OnConnected()
   {
      if (GetConnectionSecurity() == CSNone || 
          GetConnectionSecurity() == CSSTARTTLSOptional ||
          GetConnectionSecurity() == CSSTARTTLSRequired)
         EnqueueRead();
   }

   void
   POP3ClientConnection::OnHandshakeCompleted()
   {
      if (GetConnectionSecurity() == CSSSL)
         EnqueueRead();
      else if (GetConnectionSecurity() == CSSTARTTLSOptional ||
               GetConnectionSecurity() == CSSTARTTLSRequired)
      {
         SendUserName_();
         EnqueueRead();
      }
   }

   AnsiString 
   POP3ClientConnection::GetCommandSeparator() const
   {
      return "\r\n";
   }

   void
   POP3ClientConnection::ParseData(const AnsiString &sRequest)
   {
      command_buffer_.append(sRequest);
      command_buffer_.append("\r\n");

      bool is_awaiting_multiline_response  = current_state_ == StateCAPASent;

      if (is_awaiting_multiline_response)
      {
         bool multiline_response_completed = sRequest == "." ||
                                             !CommandIsSuccessfull_(command_buffer_);

         if (!multiline_response_completed)
         {
            EnqueueRead();
            return;
         }
      }

      bool postReceive = InternalParseData(command_buffer_);

      // The ASCII buffer has been parsed, so we
      // may clear it now.
      command_buffer_.Empty();

      if (postReceive)
         EnqueueRead();
   }

   bool
   POP3ClientConnection::InternalParseData(const String &sRequest)
   {
      // This code is temporary home of ETRN client settings in GUI
      // It checks External Account for ETRN domain.com for name
      // and if found uses that info to perform ETRN client connections
      String sAccountName = account_->GetName();
      if (sAccountName.StartsWith(_T("ETRN")))
      {
         HandleEtrn_(sRequest, sAccountName);
         return true;
      }
      else
      {
          // No sense in indenting code below inward as this is temp
          // and it'd just have to be moved back.
          // **** Don't miss } below when removing the above code! ****

         LogPOP3String_(sRequest, false);

         bool bRetVal = true;
         switch (current_state_)
         {
         case StateConnected:
            ParseStateConnected_(sRequest);
            return true;
         case StateCAPASent:
            ParseStateCAPASent_(sRequest);
            return true;
         case StateSTLSSent:
            return ParseStateSTLSSent_(sRequest);
         case StateUsernameSent:
            ParseUsernameSent_(sRequest);
            return true;
         case StatePasswordSent:
            ParsePasswordSent_(sRequest);
            return true;
         case StateXOAuth2Sent:
            // +OK continues exactly as a password success does; -ERR drops the
            // cached token before quitting, since a revoked token and clock skew
            // both look like this and both are cured by fetching a fresh one.
            if (!CommandIsSuccessfull_(sRequest))
               OutboundOAuth2TokenClient::Instance()->Invalidate();
            ParsePasswordSent_(sRequest);
            return true;
         case StateUIDLRequestSent:
            ParseUIDLResponse_(sRequest);
            return true;
         case StateQUITSent:
            return ParseQuitResponse_(sRequest);
         case StateDELESent:
            ParseDELEResponse_(sRequest);
            return true;
         }
   
         // This will be removed too when ETRN code is moved
       }

      return true;
   }

   bool
   POP3ClientConnection::HandleEtrn_(const String &sRequest, const String &account_name)
   {
      LogSMTPString_(sRequest, false);

      std::vector<String> vecParams = StringParser::SplitString(account_name, " ");
      if (vecParams.size() == 2)
      {
         bool bRetVal = true;
         switch (current_state_)
         {
            // Re-using POP states names for now
         case StateConnected:
            // Realize we shouldn't blindly send but this works for now
            EnqueueWrite_LogAsSMTP("HELO " + vecParams[1]);
            current_state_ = StateUsernameSent;
            return true;
         case StateUsernameSent:
            EnqueueWrite_LogAsSMTP("ETRN " + vecParams[1]);
            Sleep(20);
            current_state_ = StateUIDLRequestSent;
            return true;
         case StateUIDLRequestSent:
            EnqueueWrite_LogAsSMTP("QUIT");
            current_state_ = StateQUITSent;
            Sleep(20);
            return true;

         default:
            return false;
         }
      }
      else
      {
         //We just log error & QUIT because we have no domain to send..
         EnqueueWrite_("NOOP ETRN-Domain not set");
         Sleep(20);
         EnqueueWrite_("QUIT");
         ParseQuitResponse_(sRequest);
         return false;
      }
   }

   void
   POP3ClientConnection::ParseStateConnected_(const String &sData)
   {
      if (CommandIsSuccessfull_(sData))
      {
         if (GetConnectionSecurity() == CSSTARTTLSOptional ||
             GetConnectionSecurity() == CSSTARTTLSRequired)
         {
            SendCAPA_();
            return;
         }
         else
         {
            SendUserName_();
            return;
         }
      }

      // Disconnect immediately.;
      QuitNow_();
      return;
   }

   void
   POP3ClientConnection::SendUserName_()
   {
      // XOAUTH2 first when this server is configured for it (the Microsoft 365
      // Basic-auth cutover: collecting from outlook.office365.com with USER/PASS
      // has been off since 2022). One line, one round trip, same blob shape as
      // the outbound SMTP client's. The password is not a fallback here either -
      // a host on the OAuth list has Basic auth off, and USER/PASS against it is
      // a guaranteed, slower failure.
      String fetchHosts = IniFileSettings::Instance()->GetFetchOAuth2Hosts();
      String serverAddress = account_->GetServerAddress();

      bool oauthApplies = false;
      std::vector<String> hosts = StringParser::SplitString(fetchHosts, ",");
      for (String host : hosts)
      {
         host.TrimLeft();
         host.TrimRight();
         if (!host.IsEmpty() && host.CompareNoCase(serverAddress) == 0)
         {
            oauthApplies = true;
            break;
         }
      }

      if (oauthApplies)
      {
         String token = IniFileSettings::Instance()->GetOutboundOAuth2FixedToken();
         String tokenError;

         if (token.IsEmpty() && !OutboundOAuth2TokenClient::Instance()->GetToken(token, tokenError))
         {
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5905, "POP3ClientConnection::SendUserName_",
               "Could not obtain an OAuth2 token for fetching from " + serverAddress + ": " + tokenError);
            QuitNow_();
            return;
         }

         String blob = _T("user=") + account_->GetUsername() + _T("\x01") +
                       _T("auth=Bearer ") + token + _T("\x01\x01");

         String encoded;
         StringParser::Base64Encode(blob, encoded);

         EnqueueWrite_(_T("AUTH XOAUTH2 ") + encoded);
         current_state_ = StateXOAuth2Sent;
         return;
      }

      // We have connected successfully.
      // Time to send the username.

      String sResponse;
      sResponse.Format(_T("USER %s"), account_->GetUsername().c_str());

      EnqueueWrite_(sResponse);

      current_state_ = StateUsernameSent;
   }

   void
   POP3ClientConnection::SendCAPA_()
   {
      // We have connected successfully.
      // Time to send the username.
      EnqueueWrite_(_T("CAPA"));

      current_state_ = StateCAPASent;
   }

   void
   POP3ClientConnection::ParseStateCAPASent_(const String &sData)
   {
      if (!CommandIsSuccessfull_(sData) || !sData.Contains(_T("STLS")))
      {
         // STLS is not supported.
         if (GetConnectionSecurity() == CSSTARTTLSRequired)
         {
            String message = 
               Formatter::Format("The download of messages from external account {0} failed. The external aAccount is configured to use STARTTLS connection security, but the POP3 server does not support it.", account_->GetName());
            
            LOG_APPLICATION(message)
            QuitNow_();
            return;
         }
         else
         {
            SendUserName_();
            return;
         }
      }

      EnqueueWrite_("STLS");
      current_state_ = StateSTLSSent;
   }

   bool
   POP3ClientConnection::ParseStateSTLSSent_(const String &sData)
   {
      if (CommandIsSuccessfull_(sData))
      {
         EnqueueHandshake();
         return false;
      }

      // Disconnect immediately.
      QuitNow_();
      return true;
   }

   void 
   POP3ClientConnection::ParseUsernameSent_(const String &sData)
   {
      if (CommandIsSuccessfull_(sData))
      {
         // We have connected successfully.
         // Time to send the username.

         String sResponse;
         sResponse.Format(_T("PASS %s"), account_->GetPassword().c_str());

         current_state_ = StatePasswordSent;

         EnqueueWrite_(sResponse);

         return;
      }

      QuitNow_();
      return;
   }

   void 
   POP3ClientConnection::ParsePasswordSent_(const String &sData)
   {
      if (CommandIsSuccessfull_(sData))
      {
         current_state_ = StateUIDLRequestSent;

         SetReceiveBinary(true);

         // We have connected successfully.
         // Time to send the username.
         String sResponse;
         sResponse.Format(_T("UIDL"));
         
         EnqueueWrite_(sResponse);
         return;
      }

      QuitNow_();
      return;
   }

   void
   POP3ClientConnection::ParseUIDLResponse_(const String &sData)
   {
      if (!CommandIsSuccessfull_(sData))
      {
         QuitNow_();
         return;
      }

      std::vector<String> vecLines = StringParser::SplitString(sData, "\r\n");

      if (vecLines.size() < 3)
      {
         // Nothing but the status line and the terminator: an empty mailbox.
         StartMailboxCleanup_();
         return;
      }

      auto iter = vecLines.begin();

      // Move to first line containing a message ID.
      iter++;

      int rejectedLines = 0;
      int foldedUIDs = 0;

      while (iter != vecLines.end())
      {
         String sLine = (*iter);
         iter++;

         if (sLine == _T("."))
            break;

         if (sLine.IsEmpty())
            continue;

         int iMessageIdx = 0;
         String sMessageUID;

         if (!ParseUIDLLine_(sLine, iMessageIdx, sMessageUID))
         {
            rejectedLines++;
            continue;
         }

         String sStorableUID = FoldRemoteUID_(sMessageUID);

         if (sStorableUID != sMessageUID)
            foldedUIDs++;

         uidlresponse_[iMessageIdx] = sStorableUID;
      }

      // Reported at application level rather than debug: a listing this server cannot
      // make sense of means messages it will not collect, and the administrator cannot
      // see that from the outside.
      if (rejectedLines > 0)
      {
         String message =
            Formatter::Format("The remote POP3 server returned {0} unusable line(s) in the UIDL listing of external account {1}. Those messages have been ignored; the rest of the listing is being collected as normal.",
               rejectedLines, account_->GetName());

         LOG_APPLICATION(message);
      }

      if (foldedUIDs > 0)
      {
         String message =
            Formatter::Format("The remote POP3 server returned {0} unique-id(s) longer than 255 characters for external account {1}. RFC 1939 allows 70. They have been shortened for storage, which is what stops the affected messages from being downloaded again on every check.",
               foldedUIDs, account_->GetName());

         LOG_APPLICATION(message);
      }

      cur_download_ = uidlresponse_.begin();

      RequestNextMessage_();
   }

   bool
   POP3ClientConnection::ParseUIDLLine_(const String &line, int &messageIndex, String &messageUID)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Parses one line of a UIDL listing, "<message-number> <unique-id>" (RFC 1939).
   // Returns false for a line that is not of that shape.
   //
   // Rejecting rather than guessing matters because of what the guess used to be. Find
   // returned -1 for a line with no space, Mid(0, -1) range-checked its way to an empty
   // string, _ttoi turned that into 0, and the entire junk line was stored as the
   // unique-id of message number 0. uidlresponse_ is a std::map, which orders by key, so
   // that bogus entry became the FIRST message requested; "RETR 0" is not a message, the
   // server refused it, and a refused RETR used to abandon the whole session. One
   // unparseable line from the remote server therefore meant no mail was ever collected
   // from that account again - on this check and on every check after it.
   //---------------------------------------------------------------------------()
   {
      int spacePosition = line.Find(_T(" "));

      if (spacePosition <= 0)
         return false;

      String indexPart = line.Mid(0, spacePosition);
      String uidPart = line.Mid(spacePosition + 1);

      uidPart.TrimLeft(_T(" \t"));
      uidPart.TrimRight(_T(" \t"));

      if (uidPart.IsEmpty())
         return false;

      // The message number must be a plain positive integer. _ttoi silently answers 0
      // for anything else, and there is no message 0. More than nine digits is refused
      // rather than allowed to overflow into a valid-looking message number.
      const int indexLength = indexPart.GetLength();

      if (indexLength > 9)
         return false;

      for (int i = 0; i < indexLength; i++)
      {
         wchar_t c = indexPart.GetAt(i);

         if (c < '0' || c > '9')
            return false;
      }

      messageIndex = _ttoi(indexPart);

      if (messageIndex <= 0)
         return false;

      messageUID = uidPart;

      return true;
   }

   bool
   POP3ClientConnection::RequestNextMessage_()
   {
      while (cur_download_ != uidlresponse_.end())
      {
         String sCurrentUID = (*cur_download_).second;

         // Check if the current message is already in the list
         // of fetch UID's

         bool bMessageDownloaded = GetUIDList_()->IsUIDInList(sCurrentUID);

         // A unique-id is meant to identify exactly one message, but a server that
         // derives it from the message content gives the same id to two identical
         // messages. Now that the local record is written even for messages that are
         // about to be deleted (see MarkCurrentMessageAsRead_), the second copy would
         // look like one collected during an earlier session and would be silently
         // dropped - so an id appearing twice in the SAME listing is treated as what it
         // is: a second message.
         //
         // Deliberately limited to the delete-immediately case. When messages are left
         // on the server the same listing comes back every session, and downloading the
         // repeated entry each time would deliver an extra copy on every check, for
         // ever. There, collapsing the duplicate is the lesser of the two evils.
         const bool duplicateWithinThisListing =
            uids_seen_this_session_.find(sCurrentUID) != uids_seen_this_session_.end();

         if (bMessageDownloaded && duplicateWithinThisListing && GetDaysToKeep_(sCurrentUID) == -1)
            bMessageDownloaded = false;

         uids_seen_this_session_.insert(sCurrentUID);

         if (bMessageDownloaded)
         {
            // Mark this message as downloaded. This is so that we can
            // drop it later on when purging the mailbox. (We only purge
            // items we have downloaded). And since it was downloaded during
            // a previous session, we can safely drop it..
            int iID = (*cur_download_).first;
            downloaded_messages_[iID] = sCurrentUID;

            // The message has already been downloaded. Give scripts a chance
            // to override the default delete behavior.
            std::shared_ptr<Message> messageEmpty;
            FireOnExternalAccountDownload_(messageEmpty, sCurrentUID);
         }
         else
         {
            // Request message download now.

            current_message_ = std::shared_ptr<Message> (new Message);

            int iMessageIdx = (*cur_download_).first;

            String sResponse;
            sResponse.Format(_T("RETR %d"), iMessageIdx);

            EnqueueWrite_(sResponse);

            current_state_ = StateRETRSent;
            retr_failed_ = false;

            // From here until the finalization completes there is a message part-way
            // through arriving. If the session ends before that, the fragment on disk
            // has to be cleaned up - see DiscardUnfinishedDownload_.
            download_finalized_ = false;

            // Reset the transmission buffer. It will be
            // recreated when we receive binary the next time.

            transmission_buffer_.reset();

            SetReceiveBinary(true);
                          
            return true;
         }
      
         cur_download_++;

      }

      // We reached the end of the message list.
      if (cur_download_ == uidlresponse_.end())
      {
         StartMailboxCleanup_();
      }


      return false;
   }

   void
   POP3ClientConnection::StartMailboxCleanup_()
   {
      cur_cleanup_ = downloaded_messages_.begin();
      SetReceiveBinary(false);

      MailboxCleanup_();
   }

   void
   POP3ClientConnection::MailboxCleanup_()
   {
      while (cur_cleanup_ != downloaded_messages_.end())
      {
         bool bRet = MessageCleanup_();

         cur_cleanup_++;

         if (bRet)
         {
            // MessageCleanup_ said something to the
            // remote server. We have to return here
            // to receive the response.

            return;
         }
      }

      DeleteUIDsNoLongerOnServer_();

      // Cleanup is complete. Time to quit.
      LOG_DEBUG("POP3 External Account: Normal QUIT.");
      QuitNow_();
     
   }

   void
   POP3ClientConnection::DeleteUIDsNoLongerOnServer_()
   {
      // Delete UID's from the database of those
      // messages that no longer exists on the
      // remote POP3 server. This happens if hMailServer
      // has downloaded a message and than the user has
      // deleted it from the POP3 server.
      std::shared_ptr<FetchAccountUIDList> uidList = GetUIDList_();

      // Build a vector with the UID's to keep. All UID's
      // not in this list should be deleted from the database.

      std::set<String> setUIDs;

      auto iter = uidlresponse_.begin();
      auto iterEnd = uidlresponse_.end();

      for (; iter != iterEnd; iter++)
         setUIDs.insert((*iter).second);

      uidList->DeleteUIDsNotInSet(setUIDs);
   }

   void
   POP3ClientConnection::QuitNow_()
   {
      String sResponse;
      sResponse.Format(_T("QUIT"));
   
      EnqueueWrite_(sResponse);

      SetReceiveBinary(false);
      current_state_ = StateQUITSent;
   }

   bool 
   POP3ClientConnection::ParseQuitResponse_(const String &sData)
   {
      if (CommandIsSuccessfull_(sData))
      {
         // We have quitted successfully.
         EnqueueDisconnect();
      }

      // Quit anyway
      return false;
   }

   void
   POP3ClientConnection::ParseRETRResponse_(const String &sData)
   {
      if (CommandIsSuccessfull_(sData))
      {
         // Log that this message has been downloaded.
         int iID = (*cur_download_).first;
         String sCurrentUID = (*cur_download_).second;
         downloaded_messages_[iID] = sCurrentUID;

         return;
      }

      // The server listed this message in its UIDL response and has now refused to send
      // it. That happens for real reasons - another client deleted it between the two
      // commands, or the server cannot read it back - so only this message is given up
      // on. ParseData moves past it and the rest of the listing is still collected.
      //
      // This used to abandon the entire session and go straight to the mailbox cleanup.
      // Because the listing is walked in message-number order, one message that fails
      // permanently and happens to sit at the front of the mailbox stopped every message
      // behind it from being collected - not merely on this check, but on every check
      // after it, silently and for ever. Nothing was written to any log.
      retr_failed_ = true;

      // No message data follows a refused RETR, so there is no fragment to clean up.
      download_finalized_ = true;

      String response = sData;
      response.TrimRight(_T("\r\n"));

      String message =
         Formatter::Format("The remote POP3 server refused to send message {0} of external account {1}, so it has been skipped. The remaining messages are still being collected. Server response: {2}",
            (*cur_download_).first, account_->GetName(), response);

      LOG_APPLICATION(message);
   }


   void
   POP3ClientConnection::ParseDELEResponse_(const String &sData)
   {
      // The response used to be ignored entirely, which meant a refused deletion was
      // treated exactly like a successful one.
      if (!pending_delete_uid_.IsEmpty())
      {
         if (CommandIsSuccessfull_(sData))
         {
            // The message is gone from the remote server, so the local record of having
            // downloaded it has done its job and can go.
            GetUIDList_()->DeleteUID(pending_delete_uid_);
         }
         else
         {
            // The server refused to delete it, so the message is still there. The local
            // record therefore stays as well: it is the only thing that stops the next
            // fetch downloading and delivering the same message again.
            String response = sData;
            response.TrimRight(_T("\r\n"));

            String message =
               Formatter::Format("The remote POP3 server refused to delete a message downloaded from external account {0}. It has been left on the server and will not be downloaded again. Server response: {1}",
                  account_->GetName(), response);

            LOG_APPLICATION(message);
         }

         pending_delete_uid_.Empty();
      }

      // Clean up the next message.
      MailboxCleanup_();

      return;
   }


   bool
   POP3ClientConnection::CommandIsSuccessfull_(const String &sData)
   {
      if (sData.Mid(0,3).CompareNoCase(_T("+OK")) == 0)
         return true;
      else
         return false;
   }

   void
   POP3ClientConnection::OnConnectionTimeout()
   {  
      String sMessage = "QUIT\r\n";
      EnqueueWrite_(sMessage);
   }

   
   void
   POP3ClientConnection::OnExcessiveDataReceived()
   {  

   }


   void
   POP3ClientConnection::EnqueueWrite_(const String &sData) 
   {
      LogPOP3String_(sData, true);

      EnqueueWrite(sData + "\r\n");
   }

   bool 
   POP3ClientConnection::ParseFirstBinary_(std::shared_ptr<ByteBuffer> pBuf)
   {
      // Locate the first line
      const char *pText = pBuf->GetCharBuffer();
      const char *pEndOfLine = StringParser::Search(pText, pBuf->GetSize(), "\r\n");

      if (!pEndOfLine)
      {
         // Wait for more data
         return false;
      }

      // Skip passed the end of the line
      pEndOfLine += 2;

      size_t iLineLength = pEndOfLine - pText;

      // Copy the first line from the binary buffer.
      AnsiString sLine;
      sLine.append(pText, iLineLength);
      
      LogPOP3String_(sLine, false);
      
      ParseRETRResponse_(sLine);

      size_t iRemaining = pBuf->GetSize() - iLineLength;
      pBuf->Empty(iRemaining);

      return true;
   }

   void
   POP3ClientConnection::ParseData(std::shared_ptr<ByteBuffer> pBuf)
   {
      // 
      if (current_state_ == StateUIDLRequestSent)
      {
         command_buffer_.append(pBuf->GetCharBuffer(), pBuf->GetSize());

         pBuf->Empty();

         // A remote server that never terminates its UIDL listing would otherwise grow
         // this buffer without limit: the read below is re-armed for as long as the
         // terminating ".\r\n" has not arrived, there is no cap on how much may arrive,
         // and the machine on the other end is one the administrator does not control.
         // Eight megabytes is roughly a hundred thousand entries at the RFC 1939 maximum
         // unique-id length and several times that for typical ids, which is far more
         // than this fetcher could work through in a session anyway.
         if (command_buffer_.GetLength() > MaxUIDLResponseBytes)
         {
            String message =
               Formatter::Format("The UIDL listing returned by the remote POP3 server for external account {0} exceeded {1} bytes without ending. The fetch has been abandoned; no messages have been deleted from the server.",
                  account_->GetName(), (int) MaxUIDLResponseBytes);

            LOG_APPLICATION(message);

            command_buffer_.clear();
            QuitNow_();
            return;
         }

         if (command_buffer_.StartsWith("-ERR"))
         {
            // The server does not support UIDL. We can't fetch from this server.
            LogPOP3String_(command_buffer_, false);
            QuitNow_();
            return;
         }

         if (command_buffer_ == ".\r\n" || command_buffer_.EndsWith("\r\n.\r\n"))
         {
            ParseUIDLResponse_(command_buffer_);

            command_buffer_.clear();
         }

         EnqueueRead("");
         return;
      }

      // Append message buffer with the binary data we've received.
      if (!transmission_buffer_)
      {
         if (_firstRetrResponseBuffer == nullptr)
            _firstRetrResponseBuffer = std::make_shared<ByteBuffer>();

         _firstRetrResponseBuffer->Add(pBuf);

         if (!ParseFirstBinary_(_firstRetrResponseBuffer))
         {
            EnqueueRead("");
            return;
         }

         pBuf->Empty();
         pBuf->Add(_firstRetrResponseBuffer);
         _firstRetrResponseBuffer->Empty();

         if (retr_failed_)
         {
            // The remote server rejected the RETR, so no message data follows. Creating
            // a message file here would leave a file behind in the data directory which
            // no database row ever refers to.
            pBuf->Empty();

            // Step over the refused message and carry on with the rest of the listing.
            // RequestNextMessage_ clears retr_failed_ when it issues the next RETR, and
            // starts the mailbox cleanup when there is nothing left to ask for. Done here
            // rather than inside ParseRETRResponse_ because that runs before this check:
            // requesting the next message there would clear retr_failed_ too early and
            // this code would then treat the leftover bytes as the start of a message.
            if (cur_download_ != uidlresponse_.end())
               cur_download_++;

            RequestNextMessage_();

            EnqueueRead("");
            return;
         }

         String fileName = PersistentMessage::GetFileName(current_message_);

         // Create a binary buffer for this message. 
         transmission_buffer_ = std::shared_ptr<TransparentTransmissionBuffer>(new TransparentTransmissionBuffer(false));
         if (!transmission_buffer_->Initialize(fileName))
         {
            // We have probably failed to create the file...
            LOG_DEBUG("POP3 External Account: Error creating binary buffer or file.");
            QuitNow_();
            return;
         }

         PrependHeaders_();
      }

      transmission_buffer_->Append(pBuf->GetBuffer(), pBuf->GetSize());
      transmission_buffer_->Flush();

      // Clear the binary buffer.
      pBuf->Empty();

      if (!transmission_buffer_->GetTransmissionEnded())
      {
         EnqueueRead("");
         return;
      }

      // Since this may be a time-consuming task, do it asynchronously
      finalization_enqueued_tick_ = GetTickCount64();
      std::shared_ptr<AsynchronousTask<TCPConnection> > finalizationTask =
         std::shared_ptr<AsynchronousTask<TCPConnection> >(new AsynchronousTask<TCPConnection>
         (std::bind(&POP3ClientConnection::HandlePOP3FinalizationTaskCompleted_, this), shared_from_this()));

      // Checked rather than assumed: the async queue exists only while the servers
      // are running, and an external fetch that is mid-download when they stop
      // reaches here with nothing to post to. Unchecked, that was a null dereference.
      // Nothing is lost by giving up - the message has not been saved and DELE has
      // not been sent, so it is still on the remote server and the next fetch cycle
      // collects it.
      std::shared_ptr<WorkQueue> asyncQueue = Application::Instance()->GetAsyncWorkQueue();

      if (!asyncQueue)
      {
         LOG_APPLICATION(Formatter::Format("External account {0}: the message being downloaded was abandoned because the server is shutting down. It has been left on the remote server and will be fetched again.",
                                           account_ ? account_->GetName() : String(_T("<unknown>"))));

         EnqueueDisconnect();
         return;
      }

      asyncQueue->AddTask(finalizationTask);
   }

   void
   POP3ClientConnection::HandlePOP3FinalizationTaskCompleted_()
   {
      // The spam battery, the download event script and the save below all run on
      // the async work queue that inbound SMTP also uses to acknowledge received
      // mail, so a single slow stage here holds a thread that a sender is waiting
      // on. Time it in stages: a "start" line with no matching "done" line names
      // the stage that stalled, and the queue-wait line shows when the queue itself
      // rather than any one stage is the problem.
      const ULONGLONG queueWaitMs = finalization_enqueued_tick_ > 0
         ? GetTickCount64() - finalization_enqueued_tick_ : 0;

      if (queueWaitMs >= 5000)
      {
         String queueMessage;
         queueMessage.Format(_T("POP3ClientConnection - fetch: waited %I64u ms in the async queue before starting (session %d, external account %s). The async task queue may be saturated."),
            queueWaitMs, (int) GetSessionID(), account_->GetName().c_str());
         LOG_APPLICATION(queueMessage);
      }

      // The entire message has now been downloaded from the
      // remote POP3 server. Save it in the database and deliver
      // it to the account.
      String fileName = PersistentMessage::GetFileName(current_message_);
      current_message_->SetSize(FileUtilities::FileSize(fileName));

      // Read before the buffer is released below, which is also where the file handle
      // goes. The zero-byte test underneath already covered a download that saved
      // nothing at all; this covers the more dangerous shape, where some bytes did land
      // and then a write failed - a full disk part-way through. Without it the
      // truncated file passes the size test, gets saved, is delivered as though
      // complete, and the DELE that follows removes the only intact copy from the
      // remote server. Abandoning the fetch here sends no DELE, so the message is still
      // there to be fetched again once there is room for it.
      const bool spoolWriteFailed = transmission_buffer_ && transmission_buffer_->GetWriteFailed();

      if (current_message_->GetSize() == 0 || spoolWriteFailed)
      {
         // Error handling.
         if (spoolWriteFailed)
            ErrorManager::Instance()->ReportError(ErrorManager::High, 5864, "POP3ClientConnection::HandlePOP3FinalizationTaskCompleted_",
               "A write to the spool file failed while downloading a message from an external POP3 account, so it has not been delivered and has been left on the remote server to be fetched again. The preceding HM5862 or HM5863 gives the underlying reason.");
         else
            LOG_DEBUG("POP3 External Account: Message is 0 bytes.");

         // The file was created before the transfer was abandoned, and the message
         // is never saved, so remove the file rather than leaving it behind in the
         // data directory with no database row referring to it. The buffer is
         // released first since it may still hold the file open.
         transmission_buffer_.reset();

         if (!FileUtilities::DeleteFile(fileName))
         {
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5703, "POP3ClientConnection::HandlePOP3FinalizationTaskCompleted_",
               "Could not delete the incomplete downloaded message file: " + fileName);
         }

         // Dealt with, so the teardown cleanup must not look for it a second time.
         download_finalized_ = true;

         QuitNow_();
         return;
      }

      LOG_DEBUG("POP3ClientConnection - fetch: start header-parsing.");
      ULONGLONG stageTick = GetTickCount64();
      ParseMessageHeaders_();
      LogFinalizationStage_("header-parsing", stageTick);

      LOG_DEBUG("POP3ClientConnection - fetch: start spam-protection.");
      stageTick = GetTickCount64();
      bool messageAccepted = DoSpamProtection_();
      LogFinalizationStage_("spam-protection", stageTick);

      if (messageAccepted)
      {
         // Deliberately no deadline here. A message downloaded over POP3 exists
         // only in this process: the RETR has already completed, so the remote
         // server has committed its download state and many providers (Gmail
         // among them) will not serve it again. Abandoning it to free a worker
         // would destroy the only copy, and by this point the expensive work has
         // already been paid for anyway. Slow fetches are reported by the stage
         // timings above rather than being cut short.
         LOG_DEBUG("POP3ClientConnection - fetch: start script/save.");
         stageTick = GetTickCount64();

         // should we scan this message for virus later on?
         current_message_->SetFlagVirusScan(account_->GetUseAntiVirus());

         FireOnExternalAccountDownload_(current_message_, (*cur_download_).second);

         // the message was not classified as spam which we should delete.
         if (!SaveMessage_())
         {
            // The save failed. SaveMessage_ has removed the file and said why; what
            // matters here is what is *not* done next - MarkCurrentMessageAsRead_ does
            // not run, so no UIDL row is written, and QuitNow_ ends the session without
            // a DELE. The message is still on the remote server and will be collected
            // on the next poll. Same handling as a truncated download above, for the
            // same reason: the remote copy is the only one left.
            download_finalized_ = true;

            QuitNow_();
            return;
         }

         // Notify the SMTP deliverer that there is a new message.
         Application::Instance()->SubmitPendingEmail();

         LogFinalizationStage_("script/save", stageTick);
      }

      if (cur_download_ != uidlresponse_.end())
         MarkCurrentMessageAsRead_((*cur_download_).second);

      // This message is completely dealt with: either delivered, or deliberately
      // discarded. Nothing is left on disk for the teardown cleanup to remove.
      download_finalized_ = true;

      // Switch to ASCII since we're going to request a new message.
      SetReceiveBinary(false);

      // Move on to the next message to download
      cur_download_++;

      RequestNextMessage_();

      EnqueueRead("");
   }

   bool
   POP3ClientConnection::MessageCleanup_()
   {
      int iIndex = (*cur_cleanup_).first;
      String sUID = (*cur_cleanup_).second;

      int iDaysToKeep = GetDaysToKeep_(sUID);

      String sResponse;

      if (iDaysToKeep == 0)
      {
         // Never delete messages
         return false;
      }
      else if (iDaysToKeep > 0)
      {
         // Check wether we should delete this UID.
         std::shared_ptr<FetchAccountUID> pUID = GetUIDList_()->GetUID(sUID);

         // GetUID answers an empty pointer for an id it does not hold, and this used to
         // dereference it unconditionally - an access violation, so the whole service
         // dies, triggered by what the remote server put in its UIDL listing. It is
         // reachable: a server that hands the same unique-id to two message numbers gets
         // the row deleted while the first one is being cleaned up, and the second one
         // then finds nothing. With no creation date there is no way to judge the
         // retention window, so the safe answer is to leave the message on the server.
         if (!pUID)
         {
            String message =
               Formatter::Format("No download date is recorded for a message on external account {0}, so it has been left on the remote server rather than deleted. This happens when the server gives the same unique-id to more than one message.", account_->GetName());

            LOG_APPLICATION(message);
            return false;
         }

         // Get the creation date of the UID.
         DateTime dtCreation = pUID->GetCreationDate();
         DateTime dtNow = Time::GetDateFromSystemDate(Time::GetCurrentDateTime());

         DateTimeSpan dtSpan = dtNow - dtCreation;

         if (dtSpan.GetNumberOfDays() <= iDaysToKeep)
         {
            // No, we should not delete this UID.
            return false;
         }
      }

      // Delete the message.
      sResponse.Format(_T("DELE %d"), iIndex);
      EnqueueWrite_(sResponse);

      current_state_ = StateDELESent;

      // The local record is kept until the server confirms the deletion. Deleting it
      // here, as this used to, threw away the only evidence that the message had already
      // been delivered - so a DELE the server refused, or a connection that died before
      // the response arrived, left the message on the server and nothing to stop the next
      // fetch delivering it a second time.
      pending_delete_uid_ = sUID;

      return true;
   }

   void
   POP3ClientConnection::LogPOP3String_(const String &sLogString, bool bSent)
   {
      String sTemp;

      if (bSent)
      {
         // Check if we should remove the password.
         if (current_state_ == StatePasswordSent)
         {
            // Remove password.
            sTemp = "SENT: ***";
         }
         else
         {
            sTemp = "SENT: " + sLogString;
         }
      }
      else
         sTemp = "RECEIVED: " + sLogString;

      sTemp.TrimRight(_T("\r\n"));

      LOG_POP3_CLIENT(GetSessionID(), GetIPAddressString(), sTemp);
   }
   
   void 
   POP3ClientConnection::OnCouldNotConnect(const AnsiString &sErrorDescription)
   {
      LOG_DEBUG("Connection to remote POP3-server failed. Error message: " + String(sErrorDescription) );
   }

   void
   POP3ClientConnection::LogSMTPString_(const String &sLogString, bool bSent)
   {
      String sTemp;

      if (bSent)
      {
         // Check if we should remove the password.
         if (current_state_ == StatePasswordSent)
         {
            // Remove password.
            sTemp = "SENT: ***";
         }
         else
         {
            sTemp = "SENT: " + sLogString;
         }
      }
      else
         sTemp = "RECEIVED: " + sLogString;

      sTemp.TrimRight(_T("\r\n"));

      LOG_SMTP_CLIENT(GetSessionID(), GetIPAddressString(), sTemp);
   }

   // This is temp function to log ETRN client commands to SMTP
   void
   POP3ClientConnection::EnqueueWrite_LogAsSMTP(const String &sData) 
   {
      LogSMTPString_(sData, true);

      EnqueueWrite(sData + "\r\n");
   }

}
