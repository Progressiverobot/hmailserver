// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class ProcessLauncher
   {
   public:
      // Why a Launch that returned false failed. The two are diagnosed
      // oppositely - a bad path is the administrator's to fix, a timeout is the
      // scanner's own behaviour - so a caller that says "unable to launch" for a
      // process that launched perfectly and was then killed for running too long
      // sends the reader looking in exactly the wrong place.
      enum class FailureReason
      {
         None,           // Launch returned true.
         StartFailed,    // CreateProcess itself failed - bad path, permissions.
         TimedOut        // Ran past ExternalProcessTimeout and was terminated.
      };

      ProcessLauncher(const String &commandLine, const String &workingDirectory);
      ProcessLauncher(const String &commandLine);
      ~ProcessLauncher(void);

      bool Launch(unsigned int &exitCode);
      bool Launch(unsigned int &exitCode, FailureReason &failureReason);

      void SetErrorLogTimeout(unsigned int milliseconds);

   private:

      unsigned int error_log_timeout_;

      String command_line_;
      String working_directory_;
   };
}