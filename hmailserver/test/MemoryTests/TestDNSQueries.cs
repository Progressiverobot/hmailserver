// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Text;

namespace MemoryTests
{
    class TestDNSQueries
    {
        readonly hMailServer.Application application;
        readonly hMailServer.Utilities utilities;

        public TestDNSQueries(hMailServer.Application app)
        {
            application = app;
            utilities = app.Utilities;
        }

        public int MaxIncrease
        {
            // Allow 50 kb increase. 
            get { return 50 * 1024;  }
        }

        public void Prepare()
        {
            for (int i = 0; i < 1000; i++)
            {
                RunIteration();
            }

        }

        public void Run()
        {
            int numberOfQueries = 100000;
            for (int i = 0; i < numberOfQueries; i++)
            {
                RunIteration();
            }
        }

        private void RunIteration()
        {
            utilities.GetMailServer("test@hotmail.com");
        }
    }
}
