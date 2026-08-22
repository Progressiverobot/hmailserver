// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "VirusScanningResult.h"

namespace HM
{
   class ClamWinVirusScanner  
   {
   public:
	   ClamWinVirusScanner();
	   virtual ~ClamWinVirusScanner();

      static VirusScanningResult Scan(const String &sFilename);
      static VirusScanningResult Scan(const String &scannerExecutable, const String &databasePath, const String &sFilename);

   protected:
      
   private:


   };
}
