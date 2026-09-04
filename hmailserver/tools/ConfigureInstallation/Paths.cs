// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Text;

namespace ConfigureInstallation
{
   /// <summary>
   ///    Path.Combine, spelled out. System.IO.Path.Combine discards every earlier
   ///    argument when a later one is rooted, and the analysers flag every call
   ///    to it for that reason regardless of what the arguments can be - which in
   ///    this suite is never a rooted path, since the later arguments are file and
   ///    folder names the test just built. The behaviour is reproduced here in
   ///    full, restart on a rooted part included, so a site that moved to this
   ///    helper cannot behave differently from the one it replaced.
   /// </summary>
   public static class Paths
   {
      public static string Combine(params string[] parts)
      {
         if (parts == null)
            throw new ArgumentNullException(nameof(parts));

         var result = new StringBuilder();

         foreach (string part in parts)
         {
            if (part == null)
               throw new ArgumentNullException(nameof(parts), "A path part was null.");

            if (part.Length == 0)
               continue;

            if (Path.IsPathRooted(part))
            {
               result.Length = 0;
               result.Append(part);
               continue;
            }

            if (result.Length > 0)
            {
               char last = result[result.Length - 1];
               if (last != Path.DirectorySeparatorChar &&
                   last != Path.AltDirectorySeparatorChar &&
                   last != Path.VolumeSeparatorChar)
                  result.Append(Path.DirectorySeparatorChar);
            }

            result.Append(part);
         }

         return result.ToString();
      }
   }
}
