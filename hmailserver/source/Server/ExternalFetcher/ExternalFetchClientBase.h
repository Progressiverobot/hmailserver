// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "../common/TCPIP/TCPConnection.h"
#include <map>

namespace HM
{
   class FetchAccount;
   class Message;
   class TransparentTransmissionBuffer;
   class MimeHeader;
   class Result;
   class FetchAccountUIDList;

   // What every external-account fetcher does with a message once it has it,
   // whatever protocol it came by: the X-hMailServer-ExternalAccount header, the
   // header parsing, the recipients from the envelope or the MIME headers, the
   // received date, spam protection, the OnExternalAccountDownload script event,
   // the save and the hand-off to delivery, the record of the remote UID, and the
   // cleanup of a download the session did not finish. POP3ClientConnection is
   // the first of the fetchers; the code here is its message-handling half,
   // moved unchanged so that an IMAP fetcher gets it too.
   class ExternalFetchClientBase : public TCPConnection
   {
   public:
      ExternalFetchClientBase(std::shared_ptr<FetchAccount> pAccount,
         ConnectionSecurity connectionSecurity,
         boost::asio::io_context& io_context,
         boost::asio::ssl::context& context,
         std::shared_ptr<Event> disconnected,
         AnsiString remote_hostname);
      virtual ~ExternalFetchClientBase(void);

   protected:
      void DiscardUnfinishedDownload_();
      static String FoldRemoteUID_(const String &uid);
      void PrependHeaders_();
      void LogFinalizationStage_(const AnsiString &stage, ULONGLONG startTick);
      bool DoSpamProtection_();
      void ParseMessageHeaders_();
      bool SaveMessage_();
      void MarkCurrentMessageAsRead_(const String &uid);
      void CreateRecipentList_(std::shared_ptr<MimeHeader> pHeader);
      void ProcessMIMERecipients_(std::shared_ptr<MimeHeader> pHeader);
      void RetrieveReceivedDate_(std::shared_ptr<MimeHeader> pHeader);
      void ProcessReceivedHeaders_(std::shared_ptr<MimeHeader> pHeader);
      void RemoveInvalidRecipients_();
      int GetDaysToKeep_(const String &sUID);
      void FireOnExternalAccountDownload_(std::shared_ptr<Message> message, const String &uid);
      std::shared_ptr<FetchAccountUIDList> GetUIDList_();

      std::shared_ptr<FetchAccount> account_;
      std::shared_ptr<Message> current_message_;
      bool download_finalized_;
      String receiving_account_address_;
      std::shared_ptr<TransparentTransmissionBuffer> transmission_buffer_;
      std::map<String, std::shared_ptr<Result> > event_results_;
      std::shared_ptr<FetchAccountUIDList> fetch_account_uidlist_;
   };
}
