// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ConfigureInstallation
{
   class Ini
   {
      [DllImport("kernel32")]
      private static extern long WritePrivateProfileString(string section,
          string key, string val, string filePath);

      public static void Write(string file, string section, string key, string value)
      {
         WritePrivateProfileString(section, key, value, file);
      }

   }
}
