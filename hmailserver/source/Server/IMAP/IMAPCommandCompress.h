// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "IMAPCommand.h"

namespace HM
{
   // RFC 4978: COMPRESS DEFLATE. The response "OK DEFLATE active" is the last
   // thing sent plain; from then on both directions are raw DEFLATE, inflated and
   // deflated by the connection itself (TCPConnection::EnableCompression), so
   // nothing above this line knows the difference. Any state; once per connection.
   class IMAPCommandCompress : public IMAPCommand
   {
   public:
      IMAPCommandCompress();
      virtual ~IMAPCommandCompress();

      virtual IMAPResult ExecuteCommand(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument);
   };
}
