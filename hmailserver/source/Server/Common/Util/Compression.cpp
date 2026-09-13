// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "Compression.h"
#include "ProcessLauncher.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{

   Compression::Compression()
   {
      
   }

   Compression::~Compression(void)
   {

   }

   bool
   Compression::AddDirectory(const String &zipFile, const String &directoryToAdd)
   {
      // -r = recurse -t = type 7z -mmt = multithread off -mx1 = lowest compression (safer, faster & less cpu+ram)
      String commandLine = Formatter::Format("\"{0}\" a \"{1}\" \"{2}\" -r -t7z -mmt -mx1  -w\"{3}\"", 
         GetExecutableFullPath_(), zipFile, directoryToAdd, IniFileSettings::Instance()->GetTempDirectory());

      return LaunchCommand_(commandLine);
   }

   bool
   Compression::AddFile(const String &zipFile, const String &fileToAdd)
   {
      // -t = type 7z -mmt = multithread off -mx1 = lowest compression (safer, faster & less cpu+ram)
      String commandLine = Formatter::Format("\"{0}\" a \"{1}\" \"{2}\" -t7z -mmt -mx1 -w\"{3}\"", 
         GetExecutableFullPath_(), zipFile, fileToAdd, IniFileSettings::Instance()->GetTempDirectory());

      return LaunchCommand_(commandLine);
   }

   bool
   Compression::Uncompress(const String &zipFile, const String &targetDirectory)
   {
      return Uncompress(zipFile, targetDirectory, "*");
   }

   bool
   Compression::Uncompress(const String &zipFile, const String &targetDirectory, const String &wildCard)
   {
      String commandLine = Formatter::Format("\"{0}\" x \"{1}\" \"{2}\" -o\"{3}\" -y", 
         GetExecutableFullPath_(), zipFile, wildCard, targetDirectory);

      return LaunchCommand_(commandLine);
   }

   bool 
   Compression::LaunchCommand_(const String &commandLine)
   {
      unsigned int exitCode = 0;
      ProcessLauncher processLauncher(commandLine);

      if (!processLauncher.Launch(exitCode))
         return false;

      if (exitCode != 0 && exitCode != 1)
         return false;

      return true;
   }

   String
   Compression::GetExecutableFullPath_()
   {
#ifdef HM_PLATFORM_POSIX
      // No 7za.exe ships in the Linux packages; the archiver is the system's,
      // under whichever name its package gives it: 7zz (7-Zip's own Linux
      // build), 7z or 7za (p7zip), 7zr (p7zip's reduced build). The first found
      // on PATH is used by its full path, so the log names the binary that ran.
      // With none found, the bare 7z goes to the launcher, whose failure then
      // names what is missing (HM5401) - the package recommends 7zip for this.
      const char *candidates[] = { "7zz", "7z", "7za", "7zr" };
      const char *environment = ::getenv("PATH");
      AnsiString directories = environment && *environment ? environment : "/usr/local/bin:/usr/bin:/bin";

      for (const char *candidate : candidates)
      {
         int start = 0;
         while (start <= directories.GetLength())
         {
            int end = directories.Find(":", start);
            if (end < 0)
               end = directories.GetLength();

            AnsiString directory = directories.Mid(start, end - start);
            start = end + 1;

            if (directory.IsEmpty())
               continue;

            AnsiString full = directory + "/" + candidate;
            if (::access(full.c_str(), X_OK) == 0)
               return String(full);
         }
      }

      return "7z";
#else
      const String ZipExecutable = "7za.exe";
      String binDir = IniFileSettings::Instance()->GetBinDirectory();
      return FileUtilities::Combine(binDir, ZipExecutable);
#endif
   }
}