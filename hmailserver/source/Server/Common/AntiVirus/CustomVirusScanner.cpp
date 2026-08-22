// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "CustomVirusScanner.h"
#include "../../SMTP/SMTPConfiguration.h"
#include "../Util/ProcessLauncher.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   CustomVirusScanner::CustomVirusScanner(void)
   {
   }

   CustomVirusScanner::~CustomVirusScanner(void)
   {

   }
   
   VirusScanningResult 
   CustomVirusScanner::Scan(const String &sFilename)
   {
      AntiVirusConfiguration &pConfig = Configuration::Instance()->GetAntiVirusConfiguration();
      String executablePath = pConfig.GetCustomScannerExecutable();
      int virusReturnCode = pConfig.GetCustomScannerReturnValue();
      return Scan(executablePath, virusReturnCode, sFilename);
   }

   VirusScanningResult 
   CustomVirusScanner::Scan(const String &executablePath, int virusReturnCode, const String &sFilename)
   {
      LOG_DEBUG("Running custom virus scanner...");


      String sPath = FileUtilities::GetFilePath(sFilename);

      String sCommandLine;

      if (executablePath.Find(_T("%FILE%")) >= 0)
      {
         sCommandLine = executablePath;
         sCommandLine.Replace(_T("%FILE%"), sFilename);
      }
      else
      {
         // Quote both the executable and the file path so that spaces in either
         // cannot be misinterpreted by CreateProcess (unquoted-path hijack).
         String sExe = executablePath;
         if (sExe.GetLength() > 0 && sExe.GetAt(0) != _T('"'))
            sExe = _T("\"") + sExe + _T("\"");

         sCommandLine.Format(_T("%s \"%s\""), sExe.c_str(), sFilename.c_str());
      }

      unsigned int exitCode = 0;
      ProcessLauncher launcher(sCommandLine, sPath);
      launcher.SetErrorLogTimeout(20000);

      ProcessLauncher::FailureReason failureReason = ProcessLauncher::FailureReason::None;
      if (!launcher.Launch(exitCode, failureReason))
      {
         // The two failures read oppositely. A timeout means the scanner was
         // reachable and simply too slow - which is exactly what AVFailAction
         // exists to arbitrate - so reporting it as a launch failure would send
         // an administrator to check a path that is perfectly correct.
         if (failureReason == ProcessLauncher::FailureReason::TimedOut)
            return VirusScanningResult("CustomVirusScanner::Scan",
               Formatter::Format("The virus scanner did not finish within the maximum time (ExternalProcessTimeout) and was terminated. Command line: {0}.", sCommandLine));

         return VirusScanningResult("CustomVirusScanner::Scan",
            Formatter::Format("Unable to launch the virus scanner. Check that the executable path is correct and runnable by the service account. Command line: {0}.", sCommandLine));
      }

      String sDebugMessage = Formatter::Format("Scanner: {0}. Return code: {1}", sCommandLine, exitCode);
      LOG_DEBUG(sDebugMessage);

      if (exitCode == virusReturnCode)
         return VirusScanningResult(VirusScanningResult::VirusFound, "Unknown");
      else
         return VirusScanningResult(VirusScanningResult::NoVirusFound, Formatter::Format("Return code: {0}", exitCode));

   }
}