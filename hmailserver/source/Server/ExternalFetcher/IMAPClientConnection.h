// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "ExternalFetchClientBase.h"
#include <set>
#include <vector>

namespace HM
{
   // Collects mail from an external IMAP mailbox into a local account - the
   // second kind of external account, beside POP3, and the one that can leave a
   // mailbox intact on the far side: IMAP names messages by UID, and the UIDs
   // already collected are remembered (as "<UIDVALIDITY>:<UID>", in the same
   // table the POP3 fetcher keeps its UIDLs in), so a message is fetched once
   // however long it stays on the server. The INBOX only, in this first form;
   // other folders are not mirrored.
   //
   // The conversation: greeting, STARTTLS when the account asks for it, LOGIN
   // (or AUTHENTICATE XOAUTH2 for a host on FetchOAuth2Hosts, the same rule as
   // POP3), SELECT INBOX, UID SEARCH ALL, one UID FETCH BODY.PEEK[] per message
   // not yet collected - the literal goes to the spool through the same
   // transmission buffer the POP3 fetcher uses, in its byte-transparent mode -
   // then the deletion the account's DaysToKeepMessages asks for (0: delete what
   // was just collected; N: delete what was collected more than N days ago) as
   // UID STORE \Deleted and one EXPUNGE, then LOGOUT. Everything that happens to
   // a collected message afterwards is ExternalFetchClientBase's and shared with
   // POP3: headers, recipients, spam protection, the script event, the save and
   // the hand-off to delivery.
   class IMAPClientConnection : public ExternalFetchClientBase
   {
   public:
      IMAPClientConnection(std::shared_ptr<FetchAccount> pAccount,
         ConnectionSecurity connectionSecurity,
         boost::asio::io_context& io_context,
         boost::asio::ssl::context& context,
         std::shared_ptr<Event> disconnected,
         AnsiString remote_hostname);
      ~IMAPClientConnection(void);

      virtual void ParseData(const AnsiString &Request);
      virtual void ParseData(std::shared_ptr<ByteBuffer> pBuf);
      virtual AnsiString GetCommandSeparator() const;

      void OnCouldNotConnect(const AnsiString &sErrorDescription);

   protected:
      virtual void OnConnected();
      virtual void OnHandshakeCompleted();
      virtual void OnHandshakeFailed() {};
      virtual void OnConnectionTimeout();
      virtual void OnExcessiveDataReceived();

   private:
      enum State
      {
         StateGreeting,
         StateStartTlsSent,
         StateLoginSent,
         StateSelectSent,
         StateSearchSent,
         StateFetchSent,
         StateFetchLiteral,
         StateFetchTail,
         StateStoreSent,
         StateExpungeSent,
         StateLogoutSent
      };

      enum Limits
      {
         // The largest message accepted from the remote server. A literal announced
         // above this is skipped rather than spooled: the machine on the other end
         // is one the administrator does not control.
         MaxMessageBytes = 512 * 1024 * 1024,
         // The literal is read in slices of this size - the connection's own buffer
         // size - so a large message never sits in memory whole.
         ChunkBytes = 60000
      };

      void HandleLine_(const String &line);
      void HandleGreeting_(const String &line);
      void HandleStartTls_(const String &line);
      void HandleLogin_(const String &line);
      void HandleSelect_(const String &line);
      void HandleSearch_(const String &line);
      void HandleFetchLine_(const String &line);
      void HandleFetchTail_(const String &line);
      void HandleStore_(const String &line);
      void HandleExpunge_(const String &line);
      void HandleLogout_(const String &line);

      void SendLogin_();
      void SendSelect_();
      void SendSearch_();
      void RequestNextMessage_();
      void SkipCurrentMessage_();
      void FinalizeDownload_();
      void HandleFinalizationCompleted_();
      void StartCleanup_();
      void SendNextDelete_();
      void SendLogout_();
      void QuitNow_();

      // Tags the command (A1, A2, ...), logs it and sends it.
      void Send_(const String &command);
      bool IsTaggedReply_(const String &line) const;
      bool TaggedOk_(const String &line) const;
      static String QuoteImapString_(const String &value);
      String RemoteUidKey_(unsigned int uid) const;
      size_t NextChunk_() const;
      void Log_(const String &text, bool sent);

      State current_state_;
      int tag_counter_;
      String current_tag_;
      String pending_command_;

      unsigned int uidvalidity_;
      std::vector<unsigned int> server_uids_;
      std::vector<unsigned int> pending_uids_;
      size_t next_pending_;
      unsigned int current_uid_;
      bool current_literal_seen_;

      __int64 literal_remaining_;
      ULONGLONG finalization_enqueued_tick_;

      std::vector<unsigned int> delete_uids_;
      size_t next_delete_;
      std::set<String> keys_to_forget_;
   };
}
