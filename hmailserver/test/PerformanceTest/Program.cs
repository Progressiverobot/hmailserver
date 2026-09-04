// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;
using UnitTest;

namespace PerformanceTest
{
    class Program
    {
        static void Main(string[] args)
        {
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            // Determine message count
            POP3Simulator pop3sim = new POP3Simulator();
            int count = pop3sim.GetMessageCount("test@example.test", "test");

            // Fetch them..
            pop3sim.ConnectAndLogon("test@example.test","test");

            for (int i = 1; i <= count; i++)
            {
                pop3sim.RETR(i);
            }

            for (int i = 1; i <= count; i++)
            {
                pop3sim.DELE(i);
            }

            pop3sim.QUIT();

            // The measurement is the RETR and DELE work, so the clock stops here. The
            // original tool slept for an hour at this point before stopping it, which
            // made the printed time the sleep rather than the work.
            stopwatch.Stop();

            Console.WriteLine("Passed time: " + stopwatch.Elapsed.TotalSeconds);
        }
    }
}
