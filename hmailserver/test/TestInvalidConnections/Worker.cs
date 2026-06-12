// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.IO;
using System.Collections;

namespace StressTest
{
    class Worker
    {
        public static void DoWork()
        {
            while (true)
            {
                SMTPSimulator oSimulator = new SMTPSimulator();
                oSimulator.TestConnect();
            }
        }

    }

}
