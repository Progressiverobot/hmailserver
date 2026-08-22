// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;

namespace RegressionTests.Shared
{
   internal static class StringExtensions
   {
      public static int Occurences(string haystack, string needle)
      {
         var count = 0;
         var n = 0;

         if (needle != "")
            while ((n = haystack.IndexOf(needle, n, StringComparison.InvariantCulture)) != -1)
            {
               n += needle.Length;
               count++;
            }

         return count;
      }
   }
}