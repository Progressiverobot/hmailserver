// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "../../TCPIP/TCPConnection.h"

namespace HM
{
   class File;

   class SpamAssassinClient : public TCPConnection
   {
   public:
      SpamAssassinClient(const String &sFile,
         boost::asio::io_context& io_context,
         boost::asio::ssl::context& context,
         std::shared_ptr<Event> disconnected,
         std::shared_ptr<bool> testCompleted);
      ~SpamAssassinClient(void);

      // The User: header of the PROCESS request - the user whose preferences spamd
      // applies to this scan. Empty sends no header. Set before Connect.
      void SetUser(const AnsiString &user) { user_ = user; }

      virtual void ParseData(const AnsiString &Request);
      virtual void ParseData(std::shared_ptr<ByteBuffer> pBuf);

      

      
   protected:

      virtual void OnCouldNotConnect(const AnsiString &sErrorDescription);
      virtual void OnReadError(int errorCode);
      virtual void OnConnected();
      virtual void OnHandshakeCompleted() {};
      virtual void OnHandshakeFailed() {};
      virtual AnsiString GetCommandSeparator() const;
      virtual void OnConnectionTimeout();
      virtual void OnExcessiveDataReceived();

   private:

      void Cleanup_();
      void FinishTesting_();
      void AbortResponse_();
      int ParseFirstBuffer_(std::shared_ptr<ByteBuffer> pBuffer) const;
      bool SendFileContents_(const String &sFilename);

      String command_buffer_;

      String message_file_;
      AnsiString user_;
      __int64 spam_dsize_;      // Content-length spamd reported; < 0 until a valid header is parsed
      __int64 message_size_;
      std::shared_ptr<File> result_;

      // Shared with SpamTestSpamAssassin::RunTest rather than a reference to its
      // stack, so that if RunTest stops waiting (its bounded wait elapsed against
      // a spamd that never answers) and returns, this still-live connection can
      // set the flag without writing through a dangling reference.
      std::shared_ptr<bool> test_completed_;

      __int64 total_result_bytes_written_;
  };
}