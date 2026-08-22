// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include <Boost/optional.hpp>

using boost::asio::ip::tcp;

namespace HM
{
   class SynchronousConnection
   {
   public:
	   SynchronousConnection(int timeoutSeconds);
	   virtual ~SynchronousConnection();

      bool Connect(const AnsiString &hostName, int port);
      bool Write(const AnsiString &data);
      bool Write(const ByteBuffer &buffer);
      bool ReadUntil(const AnsiString &delimiter, AnsiString &readData);
      void Close();

   private:

      bool Write_(const unsigned char *buf, size_t bufSize);

      // Runs the io_context until the pending asynchronous operation completes,
      // enforcing the connection timeout on it.
      bool WaitForCompletion_(boost::optional<boost::system::error_code> &operation_result, const AnsiString &operation_description);

      String GetRemoteName_() const;

      boost::asio::io_context io_context_;
      tcp::socket socket_;
      int seconds_;

      AnsiString remote_host_;
      int remote_port_;
   };


}
