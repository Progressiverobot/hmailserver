using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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
      public bool IsAvailable => IniPath != null;

      public IniFeatureStore()
      {
         IniPath = Locate();
      }

      [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
      private static extern int GetPrivateProfileString(string section, string key, string defaultValue,
         StringBuilder result, int size, string filePath);

      [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
      private static extern bool WritePrivateProfileString(string section, string key, string value, string filePath);

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

               string path = Path.Combine(install, "Bin", "hMailServer.INI");
               if (File.Exists(path))
                  return path;

               path = Path.Combine(install, "hMailServer.INI");
               if (File.Exists(path))
                  return path;
            }
            catch
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

               string path = Path.Combine(dir, "hMailServer.ini");
               if (File.Exists(path))
                  return path;
            }
         }
         catch
         {
         }

         return null;
      }

      public string Read(string key, string defaultValue = "")
      {
         if (!IsAvailable)
            return defaultValue;
         var buffer = new StringBuilder(2048);
         GetPrivateProfileString(Section, key, defaultValue, buffer, buffer.Capacity, IniPath);
         return buffer.ToString();
      }

      public bool ReadBool(string key, bool defaultValue)
         => Read(key, defaultValue ? "1" : "0").Trim() == "1";

      public void Write(string key, string value)
      {
         if (!IsAvailable)
            throw new InvalidOperationException("hMailServer.INI was not found on this machine.");
         WritePrivateProfileString(Section, key, value, IniPath);
      }

      public void WriteBool(string key, bool value) => Write(key, value ? "1" : "0");

      /// <summary>Reads the configured log folder from the [Directories] section.</summary>
      public string GetLogFolder()
      {
         if (!IsAvailable)
            return null;
         var buffer = new StringBuilder(1024);
         GetPrivateProfileString("Directories", "LogFolder", "", buffer, buffer.Capacity, IniPath);
         string folder = buffer.ToString();
         return string.IsNullOrWhiteSpace(folder) ? null : folder;
      }
   }
}
