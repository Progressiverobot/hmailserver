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
    class Tests
    {
        public void Run()
        {
            hMailServer.Application applicaiton = new hMailServer.Application();
            applicaiton.Authenticate("Administrator", "testar");

            // Run DNS query tests.
            TestDNSQueries test = new TestDNSQueries(applicaiton);
            test.Prepare();
            int iMemoryUsageBefore = Utilities.GetMemoryUsage();
            test.Run();
            int iMemoryUsageAfter = Utilities.GetMemoryUsage();
            int iBytesDiff = iMemoryUsageAfter - iMemoryUsageBefore;
            if (iBytesDiff > test.MaxIncrease)
                throw new Exception("Memory leak found: " + iBytesDiff + " bytes leaked");
        }        
    }
}
