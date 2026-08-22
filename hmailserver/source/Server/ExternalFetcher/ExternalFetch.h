// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace boost
{
   namespace system
   {
      class error_code;
   }
}
namespace HM
{
   class FetchAccount;
   class ClientInfo;

   class ExternalFetch
   {
   public:
      ExternalFetch(void);
      ~ExternalFetch(void);

      void Start(std::shared_ptr<FetchAccount> pFA);
   };
}