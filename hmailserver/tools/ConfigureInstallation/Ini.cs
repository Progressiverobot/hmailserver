// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ConfigureInstallation
{
   class Ini
   {
      public static void Write(string file, string section, string key, string value)
      {
         IniFile.WritePrivateProfileString(section, key, value, file);
      }

   }
}
