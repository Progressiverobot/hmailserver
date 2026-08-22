// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

using boost::asio::ip::tcp;

#include "VirusScanningResult.h"

namespace HM
{
   class ClamAVVirusScanner
   {
   public:
      ClamAVVirusScanner(void);
      ~ClamAVVirusScanner(void);

      static VirusScanningResult Scan(const String &sFilename);
      static VirusScanningResult Scan(const String &hostName, int primaryPort, const String &sFilename);
   };

}