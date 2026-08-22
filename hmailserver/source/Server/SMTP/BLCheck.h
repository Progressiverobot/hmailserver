// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class IPAddress;

   class BLCheck
   {
   public:
      BLCheck(void);
      ~BLCheck(void);

      static bool ClientExistsInDNSBL(const IPAddress &sClientIP, const String &sDNSBLHost, const String &sExpectedResult);

      static String GetRevertedIP(const String &sIP);

      static std::set<String> ExpandAddresses(const String &input);


   };

   class BLCheckTester
   {
   public:
      void Test();

   };
}
