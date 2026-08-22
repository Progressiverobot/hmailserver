// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "IMAPCommand.h"
#include "../Common/Util/ByteBuffer.h"

namespace HM
{
   class Account;
   class ByteBuffer;
   class IMAPFolder;
   class Domain;

   class IMAPCommandAppend : public IMAPCommand
   {
   public:
	   IMAPCommandAppend();
	   virtual ~IMAPCommandAppend();

      virtual IMAPResult ExecuteCommand(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument);
      void ParseBinary(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<ByteBuffer> pBuf);

      // RFC 8508 (REPLACE): the next REPLACE this handler executes names its
      // target by UID rather than by message sequence number. Set by the UID
      // command handler, consumed by the one ExecuteCommand that follows.
      void SetReplaceUidMode(bool value) { replace_uid_mode_ = value; }

      // The largest APPEND this account will be allowed, in bytes - the number
      // CAPABILITY advertises as APPENDLIMIT= and STATUS reports (RFC 7889).
      // Never zero: an "unlimited" configuration still has the hard 2 GB
      // ceiling, and advertising 0 would mean "no APPEND accepted at all".
      static __int64 GetEffectiveAppendLimitBytes(std::shared_ptr<const Account> pAccount);


   private:

      // RFC 3502 (MULTIAPPEND): one received-but-unsaved message. Database rows
      // are created only when the whole command has succeeded, because a
      // multi-message APPEND is atomic - all stored or none.
      struct PendingMessage
      {
         std::shared_ptr<Message> message;
         String fileName;
      };

      // The command stays in binary mode from the first literal to the final
      // CRLF; between literals the bytes are the rest of the command line.
      enum ReceiveState
      {
         ReceivingLiteral,
         ReceivingContinuationLine
      };

      void CompleteCurrentMessage_(std::shared_ptr<IMAPConnection> pConnection);
      void ParseContinuationLine_(std::shared_ptr<IMAPConnection> pConnection, const AnsiString &line);
      void TerminateWithProtocolError_(std::shared_ptr<IMAPConnection> pConnection, const String &responseAfterTag);
      IMAPResult ValidateAndPrepareMessage_(std::shared_ptr<IMAPConnection> pConnection, __int64 declaredSize);
      void FinalizeCommand_(std::shared_ptr<IMAPConnection> pConnection);
      void FailCommand_(const String &response);
      void CleanupPendingMessages_();

      bool TruncateBuffer_(const std::shared_ptr<IMAPConnection> pConnection );
      bool WriteData_(const std::shared_ptr<IMAPConnection> pConnection, const BYTE *pBuf, size_t WriteLen);
      void KillCurrentMessage_();

      int GetMaxMessageSize_(std::shared_ptr<const Domain> pDomain);

      String current_tag_;
      String flags_to_set_;
      String create_time_to_set_;
      size_t bytes_left_to_receive_;

      String message_file_name_;

      // Set when any part of the literal could not be written to disk. The
      // literal is still consumed to keep the parser in step, but the command
      // must then fail instead of reporting OK for a message that was lost.
      bool write_failed_ = false;

      ReceiveState receive_state_ = ReceivingLiteral;

      // Bytes of the between-literals command line received so far.
      AnsiString continuation_line_;

      // When the command has already failed (a write error, an oversized later
      // message), the remaining literals are consumed to keep the protocol in
      // step but discarded, and this holds the response to send at the end.
      bool command_failed_ = false;
      String failure_response_;

      // RFC 8508 (REPLACE): the message the command replaces. Removed from the
      // selected mailbox only after the replacement is safely stored - a failed
      // append leaves it untouched.
      bool replace_mode_ = false;
      bool replace_uid_mode_ = false;
      std::shared_ptr<Message> replace_target_;

      std::vector<PendingMessage> pending_messages_;

      ByteBuffer append_buffer_;
      std::shared_ptr<IMAPFolder> destination_folder_;
      std::shared_ptr<Message> current_message_;


   };

}
