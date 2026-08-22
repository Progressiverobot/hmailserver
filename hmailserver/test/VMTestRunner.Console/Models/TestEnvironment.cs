// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;

namespace VMTestRunner.Console
{
   public class TestEnvironment
   {
      public TestEnvironment(string operatingSystem, string description, string vmName, string snapshotName)
      {
         VMName = vmName;
         SnapshotName = snapshotName;
         OperatingSystem = operatingSystem;
         Description = description;
      }

      public string OperatingSystem { get; }

      public string Description { get; }

      public string SnapshotName { get; }

      public string VMName { get; }

      public List<InstallCommand> PostInstallCommands { get; } = new List<InstallCommand>();

      public List<FileCopyCommand> PostInstallFileCopy { get; } = new List<FileCopyCommand>();

      public List<FileCopyCommand> PreInstallFileCopy { get; } = new List<FileCopyCommand>();

      public List<InstallCommand> PreInstallCommands { get; } = new List<InstallCommand>();
   }
}
