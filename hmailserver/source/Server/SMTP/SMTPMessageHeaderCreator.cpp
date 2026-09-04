// Copyright (c) Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "SMTPMessageHeaderCreator.h"

#include "../Common/BO/Message.h"
#include "../Common/BO/MessageRecipients.h"
#include "../Common/BO/MessageRecipient.h"

#include "../Common/TCPIP/CipherInfo.h"

#include "../Common/Util/Utilities.h"
#include "../Common/Util/Time.h"
#include "../Common/Util/OtelTraceContext.h"
#include "../Common/Mime/Mime.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   SMTPMessageHeaderCreator::SMTPMessageHeaderCreator(const String &username, const AnsiString &remote_ip_address, bool is_authenticated, bool is_message_submission, String helo_host, std::shared_ptr<MimeHeader> original_headers, std::shared_ptr<Message> message, int session_id) :
      username_(username),
      remote_ip_address_(remote_ip_address),
      is_authenticated_(is_authenticated),
      is_message_submission_(is_message_submission),
      original_headers_(original_headers),
      helo_host_(helo_host),
      ptr_host_("Unknown"),
      is_tls_(false),
      message_(message),
      session_id_(session_id)
   {

   }

   void
   SMTPMessageHeaderCreator::SetCipherInfo(const CipherInfo &cipher_info)
   {
      cipher_info_ = cipher_info;
      is_tls_ = true;
   }

   void
   SMTPMessageHeaderCreator::SetPtrHost(const String &ptr_host)
   {
      ptr_host_ = ptr_host.IsEmpty() ? String("Unknown") : ptr_host;
   }


   AnsiString
   SMTPMessageHeaderCreator::Create()
   {
      // Add received by tag.
      String auth_replacement_ip = IniFileSettings::Instance()->GetAuthUserReplacementIP();
      bool add_x_auth_user_ip = IniFileSettings::Instance()->GetAddXAuthUserIP();
      
      // If sender is logged in and replace IP is enabled use it
      String overriden_received_ip_address;
      String overriden_authenticated_ip_address;
      if (!username_.IsEmpty() && !auth_replacement_ip.empty())
      {
         overriden_received_ip_address = auth_replacement_ip;
         overriden_authenticated_ip_address = remote_ip_address_;
      }
      else
      {
         overriden_received_ip_address = remote_ip_address_;
         overriden_authenticated_ip_address = overriden_received_ip_address;
      }

      String new_header_lines;
      new_header_lines += GenerateReceivedHeader_(overriden_received_ip_address);

      String sComputerName = Utilities::ComputerName();

      // Add a Message-ID if the message has none - but only when this server is
      // acting as the message's submission server (RFC 6409 section 8.1): the client
      // authenticated, or it sends as one of our own domains from an IP range that
      // does not require it to. Under the default ranges that second case is the
      // server itself - web sites, scripts and the COM API submitting over localhost,
      // most of which never generate an id of their own.
      //
      // When another server relays a message to us we are a relay, not a submission
      // server, and add trace fields only. Inserting an id there hid from every check
      // downstream that the message arrived without one: SpamAssassin's MISSING_MID
      // could never fire here, because this ran before the post-transmission spam
      // tests. Every consumer of the header in this tree - the Sieve duplicate test,
      // IMAP THREAD, ENVELOPE, DKIM and ARC signing - already treats an absent
      // Message-ID as the routine case it is on the wider Internet. Upstream #552.
      if (is_message_submission_ && !original_headers_->FieldExists("Message-ID"))
      {
         String sTemp;
         sTemp.Format(_T("Message-ID: %s\r\n"), Utilities::GenerateMessageID().c_str());
         new_header_lines += sTemp;
      }

      // Add X-AuthUser header if it does not exist.
      if (IniFileSettings::Instance()->GetAddXAuthUserHeader() && !username_.IsEmpty())
      {
         if (!original_headers_->FieldExists("X-AuthUser"))
            new_header_lines += "X-AuthUser: " + username_ + "\r\n";
      }

      if (IniFileSettings::Instance()->GetAddXOriginalRcptToHeader() && message_)
      {
         auto recipients = message_->GetRecipients()->GetVector();

         std::set<String> originalLocalAddresses;

         for (auto recipientIter = recipients.begin(); recipientIter != recipients.end(); ++recipientIter)
         {
             auto recipient = *recipientIter;

             if (recipient->GetIsLocalName())
             {
                 auto originalAddress = recipient->GetOriginalAddress();

                 if (!originalAddress.IsEmpty())
                 {
                     originalLocalAddresses.insert(originalAddress);
                 }
             }
         }

         if (originalLocalAddresses.size() > 0)
         {
            String header = "X-Original-Rcpt-To: ";

            String sRcptToAddresses = JoinWithFolding_(originalLocalAddresses, ",", header.GetLength());
            new_header_lines += header + sRcptToAddresses + "\r\n";
         }
      }

      // Now add x- header for AUTH user if enabled since it was replaced above if so
      // Likely would be good idea for this to be optional at some point
      if (!username_.IsEmpty() && !auth_replacement_ip.empty() && add_x_auth_user_ip)
      {
         if (!original_headers_->FieldExists("X-AuthUserIP"))
            new_header_lines += "X-AuthUserIP: " + overriden_authenticated_ip_address + "\r\n";
      }

      // W3C trace context: continue or start this message's trace and prepend
      // this hop's traceparent, the way the Received header above is prepended.
      // Returns nothing (and the message is byte-for-byte untouched) unless
      // OtelEndpoint is configured. See Common/Util/OtelTraceContext.h.
      new_header_lines += String(OtelTraceContext::CreateReceptionHeaders(original_headers_, session_id_));

      AnsiString new_header_lines_ansi = new_header_lines;
      return new_header_lines_ansi;
   }

   String
   SMTPMessageHeaderCreator::GenerateReceivedHeader_(const String &overriden_received_ip)
   {
      String local_computer_name = Utilities::ComputerName();

      // The PTR host gives spam filters such as SpamAssassin reverse-DNS context in
      // the Received header. It is resolved ahead of time on a worker thread and
      // injected via SetPtrHost - never looked up here, because this code runs on
      // the network I/O thread where a slow DNS query stalls the whole session.
      String ptr_record_host = ptr_host_;

      String remote_hostname = helo_host_.IsEmpty() ? remote_ip_address_ : helo_host_;

      String esmtp_additions;

      if (is_tls_)
         esmtp_additions += "S";

      if (is_authenticated_)
         esmtp_additions += "A";

      String cipher_line;

      if (is_tls_)
         cipher_line.Format(_T("\t(version=%s cipher=%s bits=%d)\r\n"), String(cipher_info_.GetVersion()).c_str(), String(cipher_info_.GetName()).c_str(), cipher_info_.GetBits());

      String sResult;
      sResult.Format(_T("Received: from %s (%s [%s])\r\n")
         _T("\tby %s with ESMTP%s id %d\r\n")
         _T("%s")
         _T("\t; %s\r\n"),
         remote_hostname.c_str(),
         ptr_record_host.c_str(),
         overriden_received_ip.c_str(),
         local_computer_name.c_str(),
         esmtp_additions.c_str(),
         session_id_,
         cipher_line.c_str(),
         Time::GetCurrentMimeDate().c_str());

      return sResult;

   }

   String
   SMTPMessageHeaderCreator::JoinWithFolding_(const std::set<String> &items, const String &separator, int initialLineLength)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Joins contents of a vector into a string, with a separator. If the join string exceeds the max
   // line length, it will be split across multiple lines.
   //---------------------------------------------------------------------------()
   {
      String result;

      const int maxLineLength = 70;
      int currentLineLength = initialLineLength;

      for (auto iterVec = items.begin(); iterVec != items.end(); iterVec++)
      {
         if (!result.IsEmpty())
         {
             result += separator;
             currentLineLength += separator.GetLength();
         }

         String value = (*iterVec);
         int valueLength = value.GetLength();

         if (result.GetLength() > 0 && currentLineLength + value.GetLength() > maxLineLength)
         {
            // Break the line
            result += "\r\n\t";
            currentLineLength = 1;
         }

         result += value;
         currentLineLength += valueLength;
      }

      return result;
   }

}