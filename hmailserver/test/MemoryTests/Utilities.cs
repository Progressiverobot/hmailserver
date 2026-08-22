// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;

namespace MemoryTests
{
    class Utilities
    {
        public static int GetMemoryUsage()
        {
            Process[] processes = Process.GetProcessesByName("hMailServer");
            if (processes.Length == 0)
                throw new Exception("hMailServer is not running");

            Process process = processes[0];

            return process.WorkingSet;            
        }
    }
}
