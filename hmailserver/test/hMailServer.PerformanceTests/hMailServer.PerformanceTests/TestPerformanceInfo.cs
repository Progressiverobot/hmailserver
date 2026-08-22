// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace hMailServer.PerformanceTests
{
   public static class TestPerformanceInfo
   {
      public static Dictionary<string, TimeSpan> Timings = new Dictionary<string, TimeSpan>(); 
   }
}
