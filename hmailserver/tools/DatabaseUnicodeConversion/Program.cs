// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Text;

namespace DatabaseUnicodeConversion
{
    class Program
    {
        static void Main(string[] args)
        {
            Parser p = new Parser();

            p.Run(args[0], args[1]);

        }
    }
}
