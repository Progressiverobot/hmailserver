// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;

namespace TranslationExtractor
{
   internal class Program
   {
      private static string TranslationFolder;

      private static string TranslationScript =
         "https://www.hmailserver.com/devnet/translation_getlanguage.php?language={0}";

      private static void Main(string[] args)
      {
          ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

         TranslationFolder = args[0];

         if (Directory.Exists(TranslationFolder) == false)
         {
            Console.WriteLine(string.Format("Translation folder {0} does not exist.", TranslationFolder));
            Environment.Exit(-1);
            return;
         }

         DownloadTranslation("english", TranslationFolder);
         DownloadTranslation("swedish", TranslationFolder);

      }

      private static void DownloadTranslation(string language, string targetDir)
      {
         var url = string.Format(TranslationScript, language);

         using (var client = new HttpClient())
         {
            var content = client.GetStringAsync(url).GetAwaiter().GetResult();

            var targetFile = Path.Combine(targetDir, string.Format("{0}.ini", language));

            File.WriteAllText(targetFile, content, Encoding.UTF8);
         }

      }
   }
}
