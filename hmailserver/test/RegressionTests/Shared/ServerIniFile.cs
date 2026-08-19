// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace RegressionTests.Shared
{
   /// <summary>
   ///    Reads and writes the hMailServer.ini that the running service actually reads.
   ///
   ///    Two things about that file have each cost an afternoon, so they are stated
   ///    here rather than rediscovered:
   ///
   ///    The server reads the ini from the directory holding the BINARY, not from the
   ///    data directory - and there is an hMailServer.ini in the data directory too, so
   ///    editing the wrong one appears to do nothing at all. It is found by searching
   ///    upwards rather than by counting "..\" segments, so that moving the test
   ///    assembly's output path cannot turn a setting into a silent no-op.
   ///
   ///    A key is written into the FIRST [Settings] section, which is the only one that
   ///    counts: GetPrivateProfileString reads the first section with a given name and
   ///    ignores any later duplicate, so appending "[Settings]" and a key to the end of
   ///    the file - the obvious thing to do - has no effect whatsoever.
   ///
   ///    IniFileSettings caches the file for the life of the process, so a change here
   ///    does nothing until the service restarts. TestFixtureBase.RestartServerAndReacquireCom
   ///    is the primitive for that; Application.Stop()/Start() over COM is NOT, because
   ///    the process keeps running.
   /// </summary>
   public static class ServerIniFile
   {
      private const string SettingsSection = "[Settings]";

      public static string Path()
      {
         var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

         while (directory != null)
         {
            var candidate = System.IO.Path.Combine(directory.FullName,
               @"source\Server\hMailServer\x64\Release\hMailServer.ini");

            if (File.Exists(candidate))
               return candidate;

            directory = directory.Parent;
         }

         Assert.Fail("Could not locate the server's hMailServer.ini by searching upwards from " +
                     AppDomain.CurrentDomain.BaseDirectory);
         return null;
      }

      /// <summary>
      ///    Sets a key in [Settings], or removes it when value is null.
      /// </summary>
      public static void SetSetting(string key, string value)
      {
         var path = Path();
         var lines = new List<string>(File.ReadAllLines(path));

         lines.RemoveAll(line => line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));

         var section = lines.FindIndex(line => line.Trim() == SettingsSection);

         Assert.Greater(section, -1, "hMailServer.ini has no [Settings] section: " + path);

         if (value != null)
            lines.Insert(section + 1, key + "=" + value);

         File.WriteAllLines(path, lines);
      }

      /// <summary>
      ///    The value of a [Settings] key, or null when it is absent.
      /// </summary>
      public static string GetSetting(string key)
      {
         foreach (var line in File.ReadAllLines(Path()))
         {
            if (line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
               return line.Substring(key.Length + 1);
         }

         return null;
      }
   }
}
