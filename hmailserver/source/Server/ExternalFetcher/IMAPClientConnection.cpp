// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "IMAPClientConnection.h"
#include "FetchAccountUIDList.h"

#include "../Common/BO/FetchAccount.h"
#include "../Common/BO/FetchAccountUID.h"
#include "../Common/BO/Message.h"
#include "../Common/Persistence/PersistentMessage.h"
#include "../Common/Util/ByteBuffer.h"
#include "../Common/Util/OutboundOAuth2TokenClient.h"
#include "../Common/Util/TransparentTransmissionBuffer.h"
#include "../Common/Util/Time.h"
#include "../Common/Util/VariantDateTime.h"
#include "../Common/Util/FileUtilities.h"
#include "../Common/Util/Parsing/StringParser.h"
#include "../Common/Util/Strings/Formatter.h"
#include "../Common/Application/Application.h"
#include "../Common/Application/ErrorManager.h"
#include "../Common/Application/IniFileSettings.h"
#include "../Common/Application/TimeoutCalculator.h"
#include "../common/Threading/AsynchronousTask.h"
#include "../common/Threading/WorkQueue.h"

#include <algorithm>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   IMAPClientConnection::IMAPClientConnection(std::shared_ptr<FetchAccount> pAccount,
                                              ConnectionSecurity connectionSecurity,
                                              boost::asio::io_context& io_context,
                                              boost::asio::ssl::context& context,
                                              std::shared_ptr<Event> disconnected,
                                              AnsiString remote_hostname) :
      ExternalFetchClientBase(pAccount, connectionSecurity, io_context, context, disconnected, remote_hostname),
      current_state_(StateGreeting),
      tag_counter_(0),
      uidvalidity_(0),
      next_pending_(0),
      current_uid_(0),
      current_literal_seen_(false),
      literal_remaining_(0),
      finalization_enqueued_tick_(0),
      next_delete_(0)
   {
      // The same idle bounds as the POP3 fetcher: RFC 3501 asks a server for a
      // 30-minute autologout timer, and a client that waits longer than the server
      // is only waiting for a connection that is already gone.
      TimeoutCalculator calculator;
      SetTimeout(calculator.Calculate(IniFileSettings::Instance()->GetPOP3CMinTimeout(), IniFileSettings::Instance()->GetPOP3CMaxTimeout()));
   }

   IMAPClientConnection::~IMAPClientConnection(void)
   {
      try
      {
         DiscardUnfinishedDownload_();
      }
      catch (...)
      {
         // A destructor must not let anything escape.
      }
   }

   void
   IMAPClientConnection::OnConnected()
   {
      if (GetConnectionSecurity() == CSNone ||
          GetConnectionSecurity() == CSSTARTTLSOptional ||
          GetConnectionSecurity() == CSSTARTTLSRequired)
         EnqueueRead();
   }

   void
   IMAPClientConnection::OnHandshakeCompleted()
   {
      if (GetConnectionSecurity() == CSSSL)
      {
         // The greeting comes after the handshake.
         EnqueueRead();
      }
      else if (GetConnectionSecurity() == CSSTARTTLSOptional ||
               GetConnectionSecurity() == CSSTARTTLSRequired)
      {
         SendLogin_();
         EnqueueRead();
      }
   }

   AnsiString
   IMAPClientConnection::GetCommandSeparator() const
   {
      return "\r\n";
   }

   void
   IMAPClientConnection::OnCouldNotConnect(const AnsiString &sErrorDescription)
   {
      LOG_DEBUG("Connection to remote IMAP server failed. Error message: " + String(sErrorDescription));
   }

   void
   IMAPClientConnection::OnConnectionTimeout()
   {
      Send_(_T("LOGOUT"));
   }

   void
   IMAPClientConnection::OnExcessiveDataReceived()
   {
   }

   //---------------------------------------------------------------------------()
   // The line-mode half: every reply but the message literal itself.
   //---------------------------------------------------------------------------()

   void
   IMAPClientConnection::ParseData(const AnsiString &sRequest)
   {
      HandleLine_(String(sRequest));
   }

   void
   IMAPClientConnection::HandleLine_(const String &line)
   {
      Log_(line, false);

      switch (current_state_)
      {
      case StateGreeting:      HandleGreeting_(line); break;
      case StateStartTlsSent:  HandleStartTls_(line); break;
      case StateLoginSent:     HandleLogin_(line); break;
      case StateSelectSent:    HandleSelect_(line); break;
      case StateSearchSent:    HandleSearch_(line); break;
      case StateFetchSent:     HandleFetchLine_(line); break;
      case StateFetchTail:     HandleFetchTail_(line); break;
      case StateStoreSent:     HandleStore_(line); break;
      case StateExpungeSent:   HandleExpunge_(line); break;
      case StateLogoutSent:    HandleLogout_(line); break;
      case StateFetchLiteral:
         // Cannot happen: the literal is read in binary mode. Treated as the tail.
         HandleFetchTail_(line);
         break;
      }
   }

   void
   IMAPClientConnection::HandleGreeting_(const String &line)
   {
      if (!line.StartsWith(_T("* OK")) && !line.StartsWith(_T("* PREAUTH")))
      {
         LOG_DEBUG("IMAP fetch: the remote server did not greet with OK; giving up.");
         EnqueueDisconnect();
         return;
      }

      if (GetConnectionSecurity() == CSSTARTTLSOptional || GetConnectionSecurity() == CSSTARTTLSRequired)
      {
         Send_(_T("STARTTLS"));
         current_state_ = StateStartTlsSent;
      }
      else
      {
         SendLogin_();
      }
      EnqueueRead();
   }

   void
   IMAPClientConnection::HandleStartTls_(const String &line)
   {
      if (!IsTaggedReply_(line))
      {
         EnqueueRead();
         return;
      }

      if (TaggedOk_(line))
      {
         // The TLS handshake starts here; OnHandshakeCompleted continues with LOGIN.
         EnqueueHandshake();
         return;
      }

      if (GetConnectionSecurity() == CSSTARTTLSRequired)
      {
         LOG_DEBUG("IMAP fetch: the remote server refused STARTTLS and the account requires it; giving up.");
         QuitNow_();
         EnqueueRead();
         return;
      }

      SendLogin_();
      EnqueueRead();
   }

   void
   IMAPClientConnection::SendLogin_()
   {
      // XOAUTH2 for a host on FetchOAuth2Hosts, the same rule and the same blob as
      // the POP3 fetcher's - the Microsoft 365 cutover applies to IMAP just the
      // same. Sent as an initial response (RFC 4959 SASL-IR); a server that answers
      // with a continuation is telling us the token was refused.
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
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5906, "IMAPClientConnection::SendLogin_",
               "Could not obtain an OAuth2 token for fetching from " + serverAddress + ": " + tokenError);
            QuitNow_();
            return;
         }

         String blob = _T("user=") + account_->GetUsername() + _T("\x01") +
                       _T("auth=Bearer ") + token + _T("\x01\x01");
         String encoded;
         StringParser::Base64Encode(blob, encoded);
         Send_(_T("AUTHENTICATE XOAUTH2 ") + encoded);
         current_state_ = StateLoginSent;
         return;
      }

      Send_(_T("LOGIN ") + QuoteImapString_(account_->GetUsername()) + _T(" ") + QuoteImapString_(account_->GetPassword()));
      current_state_ = StateLoginSent;
   }

   void
   IMAPClientConnection::HandleLogin_(const String &line)
   {
      if (line.StartsWith(_T("+")))
      {
         // A SASL continuation after the initial response is the server's way of
         // reporting the failure; an empty line lets it finish with the tagged NO.
         EnqueueWrite("\r\n");
         EnqueueRead();
         return;
      }

      if (!IsTaggedReply_(line))
      {
         EnqueueRead();
         return;
      }

      if (!TaggedOk_(line))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5907, "IMAPClientConnection::HandleLogin_",
            Formatter::Format("The remote IMAP server for external account {0} refused the logon: {1}", account_->GetName(), line));
         QuitNow_();
         EnqueueRead();
         return;
      }

      SendSelect_();
      EnqueueRead();
   }

   void
   IMAPClientConnection::SendSelect_()
   {
      Send_(_T("SELECT INBOX"));
      current_state_ = StateSelectSent;
   }

   void
   IMAPClientConnection::HandleSelect_(const String &line)
   {
      // "* OK [UIDVALIDITY 1234] ..." - the number that makes a UID mean the same
      // message next time. It is part of every key this fetcher records.
      int at = line.Find(_T("[UIDVALIDITY "));
      if (at >= 0)
      {
         String number = line.Mid(at + 13);
         uidvalidity_ = (unsigned int) _ttoi64(number.c_str());
      }

      if (!IsTaggedReply_(line))
      {
         EnqueueRead();
         return;
      }

      if (!TaggedOk_(line))
      {
         LOG_DEBUG("IMAP fetch: SELECT INBOX was refused; giving up.");
         QuitNow_();
         EnqueueRead();
         return;
      }

      SendSearch_();
      EnqueueRead();
   }

   void
   IMAPClientConnection::SendSearch_()
   {
      server_uids_.clear();
      Send_(_T("UID SEARCH ALL"));
      current_state_ = StateSearchSent;
   }

   void
   IMAPClientConnection::HandleSearch_(const String &line)
   {
      if (line.StartsWith(_T("* SEARCH")))
      {
         std::vector<String> parts = StringParser::SplitString(line.Mid(8), " ");
         for (const String &part : parts)
         {
            String trimmed = part;
            trimmed.Trim();
            if (trimmed.IsEmpty())
               continue;
            unsigned int uid = (unsigned int) _ttoi64(trimmed.c_str());
            if (uid > 0)
               server_uids_.push_back(uid);
         }
         EnqueueRead();
         return;
      }

      if (!IsTaggedReply_(line))
      {
         EnqueueRead();
         return;
      }

      if (!TaggedOk_(line))
      {
         LOG_DEBUG("IMAP fetch: UID SEARCH was refused; giving up.");
         QuitNow_();
         EnqueueRead();
         return;
      }

      // What is on the server and not yet collected, in the server's order.
      std::shared_ptr<FetchAccountUIDList> uids = GetUIDList_();
      pending_uids_.clear();
      for (unsigned int uid : server_uids_)
      {
         if (!uids->IsUIDInList(RemoteUidKey_(uid)))
            pending_uids_.push_back(uid);
      }
      next_pending_ = 0;

      LOG_DEBUG(Formatter::Format("IMAP fetch: {0} message(s) on the server, {1} not yet collected.",
         (int) server_uids_.size(), (int) pending_uids_.size()));

      RequestNextMessage_();
      EnqueueRead();
   }

   void
   IMAPClientConnection::RequestNextMessage_()
   {
      if (next_pending_ >= pending_uids_.size())
      {
         StartCleanup_();
         return;
      }

      current_uid_ = pending_uids_[next_pending_];
      current_literal_seen_ = false;
      current_message_ = std::shared_ptr<Message>(new Message);
      Send_(Formatter::Format("UID FETCH {0} (BODY.PEEK[])", (int) current_uid_));
      current_state_ = StateFetchSent;
   }

   void
   IMAPClientConnection::SkipCurrentMessage_()
   {
      // Nothing was spooled, so there is nothing to remove; the message stays on
      // the server for the next poll.
      current_message_.reset();
      next_pending_++;
      RequestNextMessage_();
   }

   void
   IMAPClientConnection::HandleFetchLine_(const String &line)
   {
      if (IsTaggedReply_(line))
      {
         // The whole reply came without a literal: the message is gone from the
         // server, or the server refused. Either way, on to the next.
         if (!TaggedOk_(line))
            LOG_DEBUG(Formatter::Format("IMAP fetch: UID FETCH {0} was refused: {1}", (int) current_uid_, line));
         SkipCurrentMessage_();
         EnqueueRead();
         return;
      }

      // "* 12 FETCH (UID 34 BODY[] {56789}" - the literal follows this line.
      if (line.StartsWith(_T("* ")) && line.Find(_T(" FETCH (")) >= 0 && line.EndsWith(_T("}")))
      {
         int open = line.ReverseFind('{');
         if (open >= 0)
         {
            String size = line.Mid(open + 1, line.GetLength() - open - 2);
            literal_remaining_ = _ttoi64(size.c_str());

            if (literal_remaining_ <= 0 || literal_remaining_ > MaxMessageBytes)
            {
               LOG_APPLICATION(Formatter::Format("External account {0}: message UID {1} is {2} bytes and is not collected.",
                  account_->GetName(), (int) current_uid_, (__int64) literal_remaining_));
               // The literal still has to be consumed off the wire before the tagged
               // reply; simplest is to drop the session and try the rest next time.
               QuitNow_();
               EnqueueRead();
               return;
            }

            String fileName = PersistentMessage::GetFileName(current_message_);
            transmission_buffer_ = std::shared_ptr<TransparentTransmissionBuffer>(new TransparentTransmissionBuffer(false));
            transmission_buffer_->SetBinaryMode(true);
            if (!transmission_buffer_->Initialize(fileName))
            {
               LOG_DEBUG("IMAP fetch: could not create the spool file; giving up.");
               transmission_buffer_.reset();
               QuitNow_();
               EnqueueRead();
               return;
            }
            download_finalized_ = false;
            PrependHeaders_();

            current_literal_seen_ = true;
            current_state_ = StateFetchLiteral;
            SetReceiveBinary(true);
            EnqueueReadExact(NextChunk_());
            return;
         }
      }

      // Some other untagged reply (a FLAGS update, an EXISTS): not ours.
      EnqueueRead();
   }

   //---------------------------------------------------------------------------()
   // The binary half: the message literal, straight into the spool file.
   //---------------------------------------------------------------------------()

   void
   IMAPClientConnection::ParseData(std::shared_ptr<ByteBuffer> pBuf)
   {
      if (current_state_ != StateFetchLiteral || !transmission_buffer_)
      {
         // Not expecting binary data; nothing sensible to do with it.
         EnqueueDisconnect();
         return;
      }

      // Each read asked for exactly the next slice of the literal, and the
      // connection keeps whatever arrived behind that slice for the following
      // read - so all of what is here belongs to the message, and less than was
      // asked for means the peer closed part-way through.
      const size_t asked = NextChunk_();
      const size_t available = pBuf ? pBuf->GetSize() : 0;
      if (available != asked)
      {
         LOG_DEBUG("IMAP fetch: the connection closed before the message arrived in full; giving up.");
         EnqueueDisconnect();
         return;
      }

      transmission_buffer_->Append(pBuf->GetBuffer(), available);
      transmission_buffer_->Flush();
      literal_remaining_ -= available;

      if (literal_remaining_ > 0)
      {
         EnqueueReadExact(NextChunk_());
         return;
      }

      // The literal is complete. The closing parenthesis and the tagged reply
      // follow as lines - already in the receive buffer, or on their way.
      transmission_buffer_->MarkTransmissionEnded();
      transmission_buffer_->Flush(true);

      SetReceiveBinary(false);
      current_state_ = StateFetchTail;
      EnqueueRead();
   }

   size_t
   IMAPClientConnection::NextChunk_() const
   {
      return literal_remaining_ > (__int64) ChunkBytes ? (size_t) ChunkBytes : (size_t) literal_remaining_;
   }

   void
   IMAPClientConnection::HandleFetchTail_(const String &line)
   {
      if (!IsTaggedReply_(line))
      {
         // ")" and any other untagged reply.
         EnqueueRead();
         return;
      }

      if (!TaggedOk_(line))
      {
         LOG_DEBUG(Formatter::Format("IMAP fetch: UID FETCH {0} ended with {1}; the spooled copy is discarded.", (int) current_uid_, line));
         DiscardUnfinishedDownload_();
         SkipCurrentMessage_();
         EnqueueRead();
         return;
      }

      FinalizeDownload_();
   }

   void
   IMAPClientConnection::FinalizeDownload_()
   {
      // The spam battery, the script event and the save are slow; they run on the
      // async work queue, exactly as the POP3 fetcher's do, and the read is
      // re-armed when they are done.
      finalization_enqueued_tick_ = GetTickCount64();
      std::shared_ptr<AsynchronousTask<TCPConnection> > finalizationTask =
         std::shared_ptr<AsynchronousTask<TCPConnection> >(new AsynchronousTask<TCPConnection>
         (std::bind(&IMAPClientConnection::HandleFinalizationCompleted_, this), shared_from_this()));

      std::shared_ptr<WorkQueue> asyncQueue = Application::Instance()->GetAsyncWorkQueue();
      if (!asyncQueue)
      {
         LOG_APPLICATION(Formatter::Format("External account {0}: the message being collected was abandoned because the server is shutting down; it is still on the remote server.",
            account_->GetName()));
         EnqueueDisconnect();
         return;
      }

      asyncQueue->AddTask(finalizationTask);
   }

   void
   IMAPClientConnection::HandleFinalizationCompleted_()
   {
      const ULONGLONG queueWaitMs = finalization_enqueued_tick_ > 0 ? GetTickCount64() - finalization_enqueued_tick_ : 0;
      if (queueWaitMs >= 5000)
      {
         LOG_APPLICATION(Formatter::Format("IMAPClientConnection - fetch: waited {0} ms in the async queue before starting (session {1}, external account {2}).",
            (__int64) queueWaitMs, (int) GetSessionID(), account_->GetName()));
      }

      const String uidKey = RemoteUidKey_(current_uid_);
      String fileName = PersistentMessage::GetFileName(current_message_);
      current_message_->SetSize(FileUtilities::FileSize(fileName));

      const bool spoolWriteFailed = transmission_buffer_ && transmission_buffer_->GetWriteFailed();
      if (current_message_->GetSize() == 0 || spoolWriteFailed)
      {
         if (spoolWriteFailed)
            ErrorManager::Instance()->ReportError(ErrorManager::High, 5865, "IMAPClientConnection::HandleFinalizationCompleted_",
               "A write to the spool file failed while collecting a message from an external IMAP account, so it has not been delivered and is still on the remote server.");
         else
            LOG_DEBUG("IMAP External Account: Message is 0 bytes.");

         transmission_buffer_.reset();
         if (!FileUtilities::DeleteFile(fileName))
         {
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5703, "IMAPClientConnection::HandleFinalizationCompleted_",
               "Could not delete the incomplete collected message file: " + fileName);
         }
         download_finalized_ = true;
         QuitNow_();
         EnqueueRead();
         return;
      }

      LOG_DEBUG("IMAPClientConnection - fetch: start header-parsing.");
      ULONGLONG stageTick = GetTickCount64();
      ParseMessageHeaders_();
      LogFinalizationStage_("header-parsing", stageTick);

      LOG_DEBUG("IMAPClientConnection - fetch: start spam-protection.");
      stageTick = GetTickCount64();
      bool messageAccepted = DoSpamProtection_();
      LogFinalizationStage_("spam-protection", stageTick);

      if (messageAccepted)
      {
         LOG_DEBUG("IMAPClientConnection - fetch: start script/save.");
         stageTick = GetTickCount64();

         current_message_->SetFlagVirusScan(account_->GetUseAntiVirus());
         FireOnExternalAccountDownload_(current_message_, uidKey);

         if (!SaveMessage_())
         {
            // SaveMessage_ removed the file and said why. No UID row is written, so
            // the message is collected again on the next poll - it is still on the
            // remote server, which is the whole point of not deleting first.
            download_finalized_ = true;
            QuitNow_();
            EnqueueRead();
            return;
         }

         Application::Instance()->SubmitPendingEmail();
         LogFinalizationStage_("script/save", stageTick);
      }

      // Recorded whether delivered or deliberately discarded, so it is not
      // collected twice; removed again when the message leaves the server.
      MarkCurrentMessageAsRead_(uidKey);
      download_finalized_ = true;
      transmission_buffer_.reset();

      if (GetDaysToKeep_(uidKey) == 0)
         delete_uids_.push_back(current_uid_);

      next_pending_++;
      RequestNextMessage_();
      EnqueueRead();
   }

   //---------------------------------------------------------------------------()
   // The cleanup: what DaysToKeepMessages says goes, and the records of what
   // is no longer there.
   //---------------------------------------------------------------------------()

   void
   IMAPClientConnection::StartCleanup_()
   {
      std::shared_ptr<FetchAccountUIDList> uids = GetUIDList_();

      std::set<String> onServer;
      for (unsigned int uid : server_uids_)
      {
         const String key = RemoteUidKey_(uid);
         onServer.insert(key);

         if (std::find(delete_uids_.begin(), delete_uids_.end(), uid) != delete_uids_.end())
            continue;

         std::shared_ptr<FetchAccountUID> record = uids->GetUID(key);
         if (!record)
            continue;

         // The same rule as the POP3 fetcher's: 0 means the message goes as soon as
         // it has been collected, N means it goes once it has been on record for
         // more than N days, and a negative answer (the script said keep) means it
         // stays.
         const int daysToKeep = GetDaysToKeep_(key);
         if (daysToKeep == 0)
         {
            delete_uids_.push_back(uid);
         }
         else if (daysToKeep > 0)
         {
            DateTime dtCreation = record->GetCreationDate();
            DateTime dtNow = Time::GetDateFromSystemDate(Time::GetCurrentDateTime());
            DateTimeSpan dtSpan = dtNow - dtCreation;
            if (dtSpan.GetNumberOfDays() > daysToKeep)
               delete_uids_.push_back(uid);
         }
      }

      // Records of messages that have left the server by other means are forgotten
      // now; the records of what is deleted below go once the EXPUNGE is acknowledged.
      uids->DeleteUIDsNotInSet(onServer);

      next_delete_ = 0;
      SendNextDelete_();
   }

   void
   IMAPClientConnection::SendNextDelete_()
   {
      if (next_delete_ < delete_uids_.size())
      {
         const unsigned int uid = delete_uids_[next_delete_];
         Send_(Formatter::Format("UID STORE {0} +FLAGS.SILENT (\\Deleted)", (int) uid));
         current_state_ = StateStoreSent;
         return;
      }

      if (!delete_uids_.empty())
      {
         Send_(_T("EXPUNGE"));
         current_state_ = StateExpungeSent;
         return;
      }

      SendLogout_();
   }

   void
   IMAPClientConnection::HandleStore_(const String &line)
   {
      if (!IsTaggedReply_(line))
      {
         EnqueueRead();
         return;
      }

      if (TaggedOk_(line))
         keys_to_forget_.insert(RemoteUidKey_(delete_uids_[next_delete_]));
      else
         LOG_DEBUG(Formatter::Format("IMAP fetch: the remote server refused to flag UID {0} for deletion: {1}", (int) delete_uids_[next_delete_], line));

      next_delete_++;
      SendNextDelete_();
      EnqueueRead();
   }

   void
   IMAPClientConnection::HandleExpunge_(const String &line)
   {
      if (!IsTaggedReply_(line))
      {
         EnqueueRead();
         return;
      }

      if (TaggedOk_(line))
      {
         // Gone from the server, so the records go too - after the fact, the way
         // the POP3 fetcher forgets a UIDL after its DELE is acknowledged.
         std::shared_ptr<FetchAccountUIDList> uids = GetUIDList_();
         for (const String &key : keys_to_forget_)
            uids->DeleteUID(key);
      }
      else
      {
         LOG_DEBUG("IMAP fetch: EXPUNGE was refused; the flagged messages stay on the server and their records are kept.");
      }
      keys_to_forget_.clear();

      SendLogout_();
      EnqueueRead();
   }

   void
   IMAPClientConnection::SendLogout_()
   {
      Send_(_T("LOGOUT"));
      current_state_ = StateLogoutSent;
   }

   void
   IMAPClientConnection::HandleLogout_(const String &line)
   {
      if (IsTaggedReply_(line) || line.StartsWith(_T("* BYE")))
      {
         EnqueueDisconnect();
         return;
      }
      EnqueueRead();
   }

   void
   IMAPClientConnection::QuitNow_()
   {
      SetReceiveBinary(false);
      SendLogout_();
   }

   //---------------------------------------------------------------------------()
   // Helpers.
   //---------------------------------------------------------------------------()

   void
   IMAPClientConnection::Send_(const String &command)
   {
      tag_counter_++;
      current_tag_.Format(_T("A%d"), tag_counter_);
      pending_command_ = command;
      Log_(current_tag_ + _T(" ") + command, true);
      EnqueueWrite(AnsiString(current_tag_ + _T(" ") + command + _T("\r\n")));
   }

   bool
   IMAPClientConnection::IsTaggedReply_(const String &line) const
   {
      return line.StartsWith(current_tag_ + _T(" "));
   }

   bool
   IMAPClientConnection::TaggedOk_(const String &line) const
   {
      return line.StartsWith(current_tag_ + _T(" OK"));
   }

   String
   IMAPClientConnection::QuoteImapString_(const String &value)
   {
      String quoted = value;
      quoted.Replace(_T("\\"), _T("\\\\"));
      quoted.Replace(_T("\""), _T("\\\""));
      return _T("\"") + quoted + _T("\"");
   }

   String
   IMAPClientConnection::RemoteUidKey_(unsigned int uid) const
   {
      return Formatter::Format("{0}:{1}", (__int64) uidvalidity_, (__int64) uid);
   }

   void
   IMAPClientConnection::Log_(const String &text, bool sent)
   {
      String line = text;
      if (sent && pending_command_.StartsWith(_T("LOGIN ")))
         line = current_tag_ + _T(" LOGIN ***");
      else if (sent && pending_command_.StartsWith(_T("AUTHENTICATE ")))
         line = current_tag_ + _T(" AUTHENTICATE XOAUTH2 ***");
      line.TrimRight(_T("\r\n"));
      LOG_DEBUG((sent ? String(_T("IMAP fetch SENT: ")) : String(_T("IMAP fetch RECEIVED: "))) + line);
   }
}
