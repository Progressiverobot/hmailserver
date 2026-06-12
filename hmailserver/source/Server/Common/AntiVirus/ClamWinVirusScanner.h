// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

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
