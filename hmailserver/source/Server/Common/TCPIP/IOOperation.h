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
         BCTHandshake
      };

      IOOperation(OperationType type, std::shared_ptr<ByteBuffer> buffer);
      IOOperation(OperationType type, const AnsiString &string);
      ~IOOperation(void);

      OperationType GetType() {return type_; }
      std::shared_ptr<ByteBuffer> GetBuffer() {return buffer_; }
      AnsiString GetString() {return string_; }

   private:

      OperationType type_;
      AnsiString string_;
      std::shared_ptr<ByteBuffer> buffer_;

   };
}