// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace RegressionTests.Shared
{
   /// <summary>
   ///    Writes settings that live in hMailServer.ini rather than in the database.
   ///
   ///    Most settings are reachable over COM through Settings, but the optional
   ///    listeners are configured entirely from the [Settings] section of
   ///    hMailServer.ini - RestApiPort, WebServicesHttpsPort, MetricsServerPort and
   ///    the certificate paths beside them - and IniFileSettings caches the whole
   ///    section at InitInstance. So a test that wants to change one has to write the
   ///    file and then call Application.Reinitialize(), which is the only thing that
   ///    re-reads it. Stop()/Start() does not.
   ///
   ///    Extracted from RestApiApiKeys, which had it privately, once a second fixture
   ///    needed it. The ini is written through WritePrivateProfileString rather than
   ///    by rewriting the file, so a test cannot lose the rest of the section.
   /// </summary>
   public static class IniFileSetting
   {
      [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
      private static extern bool WritePrivateProfileString(string section, string key, string value, string filePath);

      [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
      private static extern uint GetPrivateProfileString(string section, string key, string defaultValue,
                                                         System.Text.StringBuilder returnedString, uint size,
                                                         string filePath);

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
            Path.Combine(programDirectory, "Bin"),
         };
      }

      /// <summary>
      ///    Writes one [Settings] value to every hMailServer.ini that exists, and
      ///    flushes the cache so the value is on disk before the server is asked to
      ///    re-read it. Fails the test if no ini could be found, rather than passing
      ///    while having changed nothing.
      /// </summary>
      public static void Write(string key, string value)
      {
         bool wroteAny = false;

         foreach (string directory in CandidateDirectories())
         {
            string iniPath = Path.Combine(directory, "hMailServer.ini");

            if (!File.Exists(iniPath))
               continue;

            Assert.IsTrue(
               WritePrivateProfileString("Settings", key, value, iniPath),
               "Failed to write " + key + " to " + iniPath + ".");

            // The value has to be on disk before the service is reinitialized and
            // reads it; WritePrivateProfileString caches otherwise.
            WritePrivateProfileString(null, null, null, iniPath);

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
         foreach (string directory in CandidateDirectories())
         {
            string iniPath = Path.Combine(directory, "hMailServer.ini");

            if (!File.Exists(iniPath))
               continue;

            var buffer = new System.Text.StringBuilder(4096);
            GetPrivateProfileString("Settings", key, string.Empty, buffer, (uint) buffer.Capacity, iniPath);

            return buffer.ToString();
         }

         Assert.Fail("Could not locate an existing hMailServer.ini to read.");
         return string.Empty;
      }

      /// <summary>
      ///    Removes one [Settings] key from every hMailServer.ini that exists.
      ///
      ///    Removing a key is not the same as setting it to an empty string: it is how
      ///    a setting is returned to its default, and the database mirror behind these
      ///    settings has to see the difference. Passing a null value to
      ///    WritePrivateProfileString is what deletes the line rather than leaving
      ///    "Key=" behind, so the null here is load-bearing.
      /// </summary>
      public static void Delete(string key)
      {
         bool deletedAny = false;

         foreach (string directory in CandidateDirectories())
         {
            string iniPath = Path.Combine(directory, "hMailServer.ini");

            if (!File.Exists(iniPath))
               continue;

            Assert.IsTrue(
               WritePrivateProfileString("Settings", key, null, iniPath),
               "Failed to delete " + key + " from " + iniPath + ".");

            WritePrivateProfileString(null, null, null, iniPath);

            deletedAny = true;
         }

         Assert.IsTrue(deletedAny, "Could not locate an existing hMailServer.ini to update.");
      }
   }
}
