// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using Microsoft.Win32;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// Reads and writes feature switches in hMailServer.INI ([Settings]
   /// section). Available when running on the server machine itself.
   /// </summary>
   public class IniFeatureStore
   {
      private const string Section = "Settings";

      public string IniPath { get; }

      /// <summary>
      /// Whether hMailServer.INI is on THIS machine. Unchanged in meaning: callers
      /// that need the file itself - ApiKeyStore locating hMailServerApiKeys.ini
      /// beside it, the log-folder card, anything reading [Database] or
      /// [Directories] - still depend on this and still get the honest answer.
      /// </summary>
      public bool IsAvailable => IniPath != null;

      /// <summary>
      /// How to read and write the [Settings] section over COM, for when the file is
      /// not on this machine. Supplied by the application once a session exists.
      ///
      /// Injected rather than called directly, and that is not ceremony: this file is
      /// compiled straight into ControlPanel.Tests, and reaching ServerSession from
      /// here would drag COM and System.ServiceProcess into the test assembly behind
      /// it - the same trap CertificateInspector's expiry constant fell into. Null in
      /// the tests, and null before a connection exists, which is why every use is
      /// guarded.
      ///
      /// [Settings] only. [Database] and [Directories] are deliberately NOT reachable
      /// this way: they are the bootstrap that says where the database IS, and a
      /// server whose database location could be changed through the database it is
      /// currently using is a server that can be pointed somewhere else by anyone who
      /// reaches the one it is on.
      /// </summary>
      public static Func<string, string> ComReadSetting;

      /// <summary>Writes a [Settings] value over COM. An empty value DELETES the key - see Write.</summary>
      public static Action<string, string> ComWriteSetting;

      /// <summary>
      /// Whether the [Settings] section can be reached at all, by either route. This
      /// is what a settings page should ask before deciding it cannot edit anything.
      /// </summary>
      public bool SettingsReachable => IsAvailable || ComReadSetting != null;

      public IniFeatureStore()
      {
         IniPath = Locate();
      }

      private static string Locate()
      {
         // Installed server: registry InstallLocation.
         foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
         {
            try
            {
               using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
               using RegistryKey key = baseKey.OpenSubKey(@"SOFTWARE\hMailServer");
               string install = key?.GetValue("InstallLocation") as string;
               if (string.IsNullOrEmpty(install))
                  continue;

               string path = Path.Join(install, "Bin", "hMailServer.INI");
               if (File.Exists(path))
                  return path;

               path = Path.Join(install, "hMailServer.INI");
               if (File.Exists(path))
                  return path;
            }
            catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
            {
               // Try the next view.
            }
         }

         // Development tree: the INI next to the running service binary.
         try
         {
            using var searcher = new System.Management.ManagementObjectSearcher(
               "SELECT PathName FROM Win32_Service WHERE Name='hMailServer'");
            foreach (System.Management.ManagementObject service in searcher.Get())
            {
               string pathName = service["PathName"] as string;
               if (string.IsNullOrEmpty(pathName))
                  continue;

               string exe = pathName.Trim();
               if (exe.StartsWith("\""))
                  exe = exe.Substring(1, exe.IndexOf('"', 1) - 1);
               else if (exe.Contains(' '))
                  exe = exe.Substring(0, exe.IndexOf(' '));

               string dir = Path.GetDirectoryName(exe);
               if (dir == null)
                  continue;

               string path = Path.Join(dir, "hMailServer.ini");
               if (File.Exists(path))
                  return path;
            }
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
         }

         return null;
      }

      /// <summary>
      /// Reads a [Settings] value: from the file when it is on this machine, and over
      /// COM otherwise.
      ///
      /// The file is preferred where both are possible, because it is the copy the
      /// server itself runs on - the mirror follows it, not the other way round - so
      /// reading it cannot show a value the running server disagrees with.
      /// </summary>
      public string Read(string key, string defaultValue = "")
      {
         if (IsAvailable)
            return ProfileApi.ReadString(Section, key, defaultValue, IniPath, 2048);

         Func<string, string> read = ComReadSetting;

         if (read == null)
            return defaultValue;

         try
         {
            string value = read(key);

            // The server cannot tell "absent" from "empty" either - an ini reader
            // never can - so an empty answer means the caller's default applies,
            // exactly as GetPrivateProfileString would have decided above.
            return string.IsNullOrEmpty(value) ? defaultValue : value;
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            // An older server without these members, or a session that has gone
            // away. The default is the same answer this returned before the COM
            // route existed, so nothing that worked stops working.
            return defaultValue;
         }
      }

      public bool ReadBool(string key, bool defaultValue)
         => Read(key, defaultValue ? "1" : "0").Trim() == "1";

      /// <summary>
      /// Writes a [Settings] value: to the file when it is on this machine, and over
      /// COM otherwise.
      ///
      /// A COM write also updates the database mirror in the same call, because the
      /// server does both; a local file write is picked up by the merge on the next
      /// start. Either way the file ends up holding the value, which is what keeps
      /// /Register, hmconfig.ps1 and the DAV redirects correct.
      /// </summary>
      public void Write(string key, string value)
      {
         if (IsAvailable)
         {
            ProfileApi.WriteString(Section, key, value, IniPath);
            return;
         }

         Action<string, string> write = ComWriteSetting;

         if (write == null)
            throw new InvalidOperationException(
               "hMailServer.INI is not on this machine and the server did not offer the settings API, so this value cannot be changed from here.");

         write(key, value);
      }

      public void WriteBool(string key, bool value) => Write(key, value ? "1" : "0");

      /// <summary>
      /// Reads a value from a named section rather than from [Settings].
      ///
      /// Most of hMailServer.INI is [Settings], which is why this class defaults to
      /// it - but not all of it, and a page that edits [Database] or [Directories]
      /// through Read() above would write keys of the right name into a section the
      /// server never looks at. That is the worst shape of bug this project has:
      /// the value appears saved, reads back correctly on the next visit, and does
      /// nothing.
      /// </summary>
      public string ReadFrom(string section, string key, string defaultValue = "")
      {
         if (!IsAvailable)
            return defaultValue;
         return ProfileApi.ReadString(section, key, defaultValue, IniPath, 2048);
      }

      /// <summary>Writes a value to a named section. See <see cref="ReadFrom"/>.</summary>
      public void WriteTo(string section, string key, string value)
      {
         if (!IsAvailable)
            throw new InvalidOperationException("hMailServer.INI was not found on this machine.");
         ProfileApi.WriteString(section, key, value, IniPath);
      }

      /// <summary>Reads the configured log folder from the [Directories] section.</summary>
      public string GetLogFolder()
      {
         string folder = ReadFrom("Directories", "LogFolder", "");
         return string.IsNullOrWhiteSpace(folder) ? null : folder;
      }
   }
}
