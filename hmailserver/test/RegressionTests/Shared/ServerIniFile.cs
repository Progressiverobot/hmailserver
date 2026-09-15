// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
   ///
   ///    THE FILE IS NO LONGER WHERE A [Settings] VALUE IS CHANGED. From schema 6042 the
   ///    database is the settings store and this section of the file is its copy: a value
   ///    edited into the file is put back at the next start and reported as HM5804, and a
   ///    line removed from the file is written back from the stored row. Until 15 September
   ///    2026 SetSetting still edited the file, so every one of its callers set a value that
   ///    was undone at the restart that was meant to apply it, and a fixture that removed a
   ///    bad value in its teardown left the bad value stored for every test after it - which
   ///    is how DmarcRptSchemaVersion=7 failed three hundred tests of one gate. SetSetting
   ///    now stores the value through Settings.SetIniSetting and DeleteIniSetting, the door
   ///    the Control Panel, the REST API and hmctl use, which writes the row and the file
   ///    together; GetSetting still reads the file, which the server keeps in step. A test
   ///    that is ABOUT an edit to the file losing uses IniFileSetting.WriteFileOnly.
   /// </summary>
   public static class ServerIniFile
   {
      private const string SettingsSection = "[Settings]";

      public static string Path()
      {
         // The server under test need not be in this tree. HMTEST_SERVER_INI names
         // its ini where it is not - the Linux runs put the server in a directory of
         // its own beside the test tree, and there is nothing above the test binary
         // to search. Unset on the Windows bench, where the search below finds the
         // ini of the service running from the repository's own Release output, and
         // this line then does nothing.
         var configured = Environment.GetEnvironmentVariable("HMTEST_SERVER_INI");

         if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

         var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

         while (directory != null)
         {
            var candidate = Paths.Combine(directory.FullName,
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
         if (value == null)
            IniFileSetting.Delete(key);
         else
            IniFileSetting.Write(key, value);
      }

      /// <summary>
      ///    The value of a [Settings] key, or null when it is absent.
      /// </summary>
      public static string GetSetting(string key)
      {
         return File.ReadAllLines(Path())
            .Where(line => line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            .Select(line => line.Substring(key.Length + 1))
            .FirstOrDefault();
      }
   }
}
