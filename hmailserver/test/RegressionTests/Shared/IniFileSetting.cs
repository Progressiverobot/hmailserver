// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.IO;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace RegressionTests.Shared
{
   /// <summary>
   ///    Writes the settings that used to live in hMailServer.ini and now live in
   ///    hm_inisettings, the settings store.
   ///
   ///    The listeners are configured entirely from these - RestApiPort,
   ///    WebServicesHttpsPort, MetricsServerPort and the certificate paths beside
   ///    them - and IniFileSettings latches the lot at InitInstance, so a test that
   ///    changes one still has to call Application.Reinitialize(), which is the only
   ///    thing that re-reads them. Stop()/Start() does not.
   ///
   ///    WHAT CHANGED. Write and Delete used to edit hMailServer.ini directly, and
   ///    the server read the file. From schema 6042 the DATABASE decides: a value
   ///    edited into the file is named in the error log and put back at the next
   ///    start, and a line deleted from the file is written back from the row. So
   ///    these go over COM - Settings.SetIniSetting and DeleteIniSetting - which is
   ///    the same door the Control Panel, the REST API and hmctl use, and which
   ///    writes the store and the file's copy together. Every one of the 180-odd call
   ///    sites in this suite therefore still means what it says, and none of them had
   ///    to change.
   ///
   ///    WriteFileOnly is the deliberate exception, for the handful of tests that are
   ///    ABOUT the file losing: it edits hMailServer.ini and nothing else.
   ///
   ///    Extracted from RestApiApiKeys, which had it privately, once a second fixture
   ///    needed it. The ini is written through WritePrivateProfileString rather than
   ///    by rewriting the file, so a test cannot lose the rest of the section.
   /// </summary>
   public static class IniFileSetting
   {

      /// <summary>
      ///    Every directory an hMailServer.ini may live in for this install. Both are
      ///    tried because the server looks in its own folder and in Bin, and which one
      ///    holds the live file depends on how the install was laid out.
      /// </summary>
      public static string[] CandidateDirectories()
      {
         string programDirectory = SingletonProvider<TestSetup>.Instance.GetApp().Settings.Directories.ProgramDirectory;

         return new[]
         {
            programDirectory,
            Paths.Combine(programDirectory, "Bin"),
         };
      }

      /// <summary>
      ///    The hMailServer.ini files that actually exist, out of the candidates above.
      /// </summary>
      public static IEnumerable<string> ExistingIniFiles()
      {
         return CandidateDirectories()
            .Select(directory => Paths.Combine(directory, "hMailServer.ini"))
            .Where(File.Exists);
      }

      /// <summary>
      ///    Stores one setting, through the same COM call the Control Panel and the
      ///    REST API use. The server writes the row and the file's copy together, so
      ///    a test that goes on to read the file still sees the value.
      /// </summary>
      public static void Write(string key, string value)
      {
         SingletonProvider<TestSetup>.Instance.GetApp().Settings.SetIniSetting(key, value);
      }

      /// <summary>
      ///    The same, for any section. [Directories] is the other one a test has a
      ///    reason to write (InstallationPaths), and it takes a service restart
      ///    rather than a Reinitialize to be read. [Settings] is routed to the store,
      ///    because writing the file for one of those is no longer how it is changed.
      /// </summary>
      public static void Write(string section, string key, string value)
      {
         if (string.Equals(section, "Settings", System.StringComparison.OrdinalIgnoreCase))
         {
            Write(key, value);
            return;
         }

         WriteFileOnly(section, key, value);
      }

      /// <summary>
      ///    Writes a key straight into every hMailServer.ini that exists, WITHOUT
      ///    going near the store, and flushes the cache so the value is on disk
      ///    before the server is asked to re-read it. Fails the test if no ini could
      ///    be found, rather than passing while having changed nothing.
      ///
      ///    For a [Settings] key this is a test writing the LOSING copy on purpose -
      ///    which is what the settings-store fixture is for - and for any other
      ///    section it is simply how that section is written.
      /// </summary>
      public static void WriteFileOnly(string key, string value)
      {
         WriteFileOnly("Settings", key, value);
      }

      public static void WriteFileOnly(string section, string key, string value)
      {
         bool wroteAny = false;

         foreach (string iniPath in ExistingIniFiles())
         {
            Assert.IsTrue(
               IniFile.WritePrivateProfileString(section, key, value, iniPath),
               "Failed to write " + key + " to " + iniPath + ".");

            // The value has to be on disk before the service is reinitialized and
            // reads it; WritePrivateProfileString caches otherwise.
            IniFile.WritePrivateProfileString(null, null, null, iniPath);

            wroteAny = true;
         }

         Assert.IsTrue(wroteAny, "Could not locate an existing hMailServer.ini to update.");
      }

      /// <summary>
      ///    Reads one [Settings] value back, through the same API the server reads it
      ///    with. An absent key returns an empty string, which is what the server sees
      ///    for one too - so "absent" and "present but empty" are deliberately not
      ///    distinguished here, because they are not distinguished by any reader.
      ///
      ///    The first ini that exists wins, matching the order Write uses.
      /// </summary>
      public static string Read(string key)
      {
         return Read("Settings", key);
      }

      public static string Read(string section, string key)
      {
         foreach (string iniPath in ExistingIniFiles())
            return IniFile.GetValue(section, key, string.Empty, iniPath);

         Assert.Fail("Could not locate an existing hMailServer.ini to read.");
         return string.Empty;
      }

      /// <summary>
      ///    Returns one setting to its default, through the same COM call the Control
      ///    Panel and the REST API use: the row is dropped and the key removed from
      ///    the file together.
      ///
      ///    Deleting is not the same as setting an empty string - an absent key falls
      ///    back to the caller's default while "Key=" reads as 0 through
      ///    GetPrivateProfileInt - and it is no longer the same as deleting the line,
      ///    either: with the store in the database the row would write the line
      ///    straight back at the next start.
      /// </summary>
      public static void Delete(string key)
      {
         SingletonProvider<TestSetup>.Instance.GetApp().Settings.DeleteIniSetting(key);
      }

      public static void Delete(string section, string key)
      {
         if (string.Equals(section, "Settings", System.StringComparison.OrdinalIgnoreCase))
         {
            Delete(key);
            return;
         }

         DeleteFileOnly(section, key);
      }

      /// <summary>
      ///    Removes one key from every hMailServer.ini that exists and leaves the
      ///    store alone. Passing a null value to WritePrivateProfileString is what
      ///    deletes the line rather than leaving "Key=" behind, so the null below is
      ///    load-bearing.
      /// </summary>
      public static void DeleteFileOnly(string section, string key)
      {
         bool deletedAny = false;

         foreach (string iniPath in ExistingIniFiles())
         {
            Assert.IsTrue(
               IniFile.WritePrivateProfileString(section, key, null, iniPath),
               "Failed to delete " + key + " from " + iniPath + ".");

            IniFile.WritePrivateProfileString(null, null, null, iniPath);

            deletedAny = true;
         }

         Assert.IsTrue(deletedAny, "Could not locate an existing hMailServer.ini to update.");
      }
   }
}
