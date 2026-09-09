// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.IO;
using System.Reflection;

namespace RegressionTests
{
   /// <summary>
   ///    The two members of the Windows suite's TestResources that a linked fixture
   ///    reads (AntiSpam/DKIM/Verification). The Windows project generates the class
   ///    from TestResources.resx, whose file references are written in lower case -
   ///    resources\messagewithinvaliddkim.eml - against files committed as
   ///    Resources\MessageWithInvalidDkim.eml. Windows does not mind and Linux does,
   ///    so this project embeds the two files by their real names and reads them
   ///    here rather than compiling the .resx.
   /// </summary>
   internal static class TestResources
   {
      internal static string MessageWithInvalidDkim => Read("MessageWithInvalidDkim.eml");

      internal static string MessageWithValidDkim => Read("MessageWithValidDkim.eml");

      private static string Read(string name)
      {
         var assembly = Assembly.GetExecutingAssembly();

         using (var stream = assembly.GetManifestResourceStream("RegressionTests.Resources." + name))
         {
            if (stream == null)
               throw new FileNotFoundException("Embedded resource RegressionTests.Resources." + name + " is not in " +
                                               assembly.GetName().Name);

            using (var reader = new StreamReader(stream))
               return reader.ReadToEnd();
         }
      }
   }
}
