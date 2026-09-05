// Copyright (c) 2005 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Created 2005-07-21
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class ByteBuffer;

   class IOOperation
   {
   public:

      enum OperationType
      {
         BCTWrite,
         BCTRead,
         BCTShutdownSend,
         BCTDisconnect,
         BCTHandshake,

         // A pause in the connection's own queue: nothing behind it - the reply it
         // guards, the read after that - starts until the timer has run, and no
         // thread waits meanwhile. This is what makes a tarpit safe to offer: a
         // delay slept on a worker thread would be a self-inflicted denial of
         // service that costs an attacker one failed logon per thread.
         BCTDelay
      };

      IOOperation(OperationType type, std::shared_ptr<ByteBuffer> buffer);
      IOOperation(OperationType type, const AnsiString &string);
      IOOperation(OperationType type, int delaySeconds);
      ~IOOperation(void);

      OperationType GetType() {return type_; }
      std::shared_ptr<ByteBuffer> GetBuffer() {return buffer_; }
      AnsiString GetString() {return string_; }
      int GetDelaySeconds() const {return delay_seconds_; }

   private:

      OperationType type_;
      AnsiString string_;
      std::shared_ptr<ByteBuffer> buffer_;
      int delay_seconds_ = 0;

   };
}
