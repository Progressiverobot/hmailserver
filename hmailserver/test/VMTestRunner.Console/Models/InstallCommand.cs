// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace VMTestRunner.Console
{
   public class InstallCommand
   {
      public InstallCommand(string executable, string parameters)
      {
         Executable = executable;
         Parameters = parameters;
      }

      public string Executable { get; }

      public string Parameters { get; }
   }
}
