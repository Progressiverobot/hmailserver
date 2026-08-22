// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace VMTestRunner.Console
{
   public class FileCopyCommand
   {
      public FileCopyCommand(string fromHost, string toGuest)
      {
         From = fromHost;
         To = toGuest;
      }

      public string From { get; }

      public string To { get; }
   }
}
