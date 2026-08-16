// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

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

      // The largest APPEND this account will be allowed, in bytes - the number
      // CAPABILITY advertises as APPENDLIMIT= and STATUS reports (RFC 7889).
      // Never zero: an "unlimited" configuration still has the hard 2 GB
      // ceiling, and advertising 0 would mean "no APPEND accepted at all".
      static __int64 GetEffectiveAppendLimitBytes(std::shared_ptr<const Account> pAccount);


   private:

      void Finish_(std::shared_ptr<IMAPConnection> pConnection);
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

      ByteBuffer append_buffer_;
      std::shared_ptr<IMAPFolder> destination_folder_;
      std::shared_ptr<Message> current_message_;

      
   };

}
