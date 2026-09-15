// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
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
      private const string Section = "Settings"; // no-loc

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
               "SELECT PathName FROM Win32_Service WHERE Name='hMailServer'"); // no-loc
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
      /// Reads a [Settings] value: over COM when there is a session, and from the
      /// file otherwise.
      ///
      /// THE ORDER INVERTED when the settings store moved into the database (schema
      /// 6042, Roadmap2 section 13). The file used to be preferred because it was the
      /// copy the server ran on and the table followed it. It is now the other way
      /// round: the table is the store, the file is its cache, and a
      /// [SettingsOverride] entry can make the running server use something that is
      /// in neither. Only the server knows the effective value, so the server is
      /// asked first, and the file is what is left when there is nobody to ask.
      /// </summary>
      public string Read(string key, string defaultValue = "")
      {
         Func<string, string> read = ComReadSetting;

         if (read != null)
         {
            try
            {
               string value = read(key);

               // The server cannot tell "absent" from "empty" either - an ini reader
               // never can - so an empty answer means the caller's default applies,
               // exactly as GetPrivateProfileString would decide below.
               return string.IsNullOrEmpty(value) ? defaultValue : value;
            }
            catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
            {
               // An older server without these members, or a session that has gone
               // away. Fall through to the file, which on the server itself is a
               // complete copy written by the server.
            }
         }

         if (IsAvailable)
            return ProfileApi.ReadString(Section, key, defaultValue, IniPath, 2048);

         return defaultValue;
      }

      public bool ReadBool(string key, bool defaultValue)
         => Read(key, defaultValue ? "1" : "0").Trim() == "1";

      /// <summary>
      /// Writes a [Settings] value over COM, which stores it in hm_inisettings - the
      /// settings store - and brings the file's copy into line in the same call.
      ///
      /// THIS NO LONGER WRITES THE FILE ITSELF, even when the file is right here, and
      /// that is the whole point of the change. While the file was the store, writing
      /// it was the write. Now the table is the store and a line in the file is a
      /// cache entry: writing it directly would produce a value that reads back
      /// correctly on the next visit to this page, is named in the server's error log
      /// as an edit that was ignored, and is put back to the stored value at the next
      /// start. That "saved, and silently reverted" shape is the exact defect this
      /// project keeps removing, so the only write left is the one that goes to the
      /// store.
      ///
      /// Which means a Control Panel with no session cannot change a setting even
      /// sitting on the server, and it says so rather than pretending. Reading still
      /// falls back to the file, because a complete copy of the configuration is
      /// exactly what the file still is.
      /// </summary>
      public void Write(string key, string value)
      {
         Action<string, string> write = ComWriteSetting;

         if (write == null)
            throw new InvalidOperationException(
               "Settings are stored in the server's database, so changing one needs a connection to the server. " +
               "Editing hMailServer.INI by hand does not change a setting: the stored value is used, and the edit is " +
               "reported in the server's error log and undone at the next start.");

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
