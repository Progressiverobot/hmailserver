// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "../TCPIP/ProtocolParser.h"
#include "../Util/TransparentTransmissionBuffer.h"
#include "../BO/Message.h"

namespace HM
{
   class ByteBuffer;
   class MessageRecipient;
  
   class OutboundPortConnection : public ProtocolParser  
   {
   public:
      OutboundPortConnection();
      virtual ~OutboundPortConnection();

      void OnCouldNotConnect(const AnsiString &sErrorDescription);

      virtual void ParseData(const AnsiString &Request);

   protected:

      virtual void OnConnected();
      virtual AnsiString GetCommandSeparator() const;

   private:

   };
}
