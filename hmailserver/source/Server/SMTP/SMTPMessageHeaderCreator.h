// Copyright (c) 2014 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "../Common/TCPIP/CipherInfo.h"

namespace HM
{
   class CipherInfo;
   class MimeHeader;
   class Message;

   class SMTPMessageHeaderCreator
   {
   public:
      
      SMTPMessageHeaderCreator(const String &username, const AnsiString &remote_ip_address, bool is_authenticated, bool is_message_submission, String helo_host, std::shared_ptr<MimeHeader> original_headers, std::shared_ptr<Message> message, int session_id);

      AnsiString Create();

      void SetCipherInfo(const CipherInfo &cipher_info);

      // The client's PTR (reverse DNS) host for the Received header. Resolved by the
      // caller on a worker thread (see SMTPConnection::PrefetchPtrRecord_); this class
      // must not perform DNS lookups itself, since Create() runs on the I/O thread.
      void SetPtrHost(const String &ptr_host);

   private:

      String GenerateReceivedHeader_(const String &overriden_received_ip);
      String JoinWithFolding_(const std::set<String> &items, const String &separator, int initialLineLength);

      String username_;
      AnsiString remote_ip_address_;
      AnsiString helo_host_;
      String ptr_host_;
      std::shared_ptr<MimeHeader> original_headers_;
      std::shared_ptr<Message> message_;
      CipherInfo cipher_info_;
      bool is_tls_;
      bool is_authenticated_;
      bool is_message_submission_;
      int session_id_;

   };
}