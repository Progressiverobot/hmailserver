// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class TLD : public Singleton<TLD>
   {
   public:

      TLD(void);
      ~TLD(void);

      void Initialize();
      bool IsTLD(const String &sName);

      bool GetDomainNameFromHost(String &sHost, bool &bIsIPAddress);

   private:

      std::set<String> tld_;
   };
}