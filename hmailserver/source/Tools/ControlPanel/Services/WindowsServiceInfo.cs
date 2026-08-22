// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Management;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// What Windows itself says about the hMailServer service: the account it runs
   /// under, how it starts, and where its binary is.
   ///
   /// This exists because <c>ServiceAccountName</c> in hMailServer.INI is not a
   /// setting the running service reads - it is an instruction consumed once, by
   /// <c>ServiceManager::RegisterService</c>, when the service is registered or
   /// re-registered. Between typing a value and running that registration, the INI
   /// says one thing and the service runs as another, and the INI is the half an
   /// administrator can see. Reading the Service Control Manager is how the Control
   /// Panel can show the state rather than the request.
   ///
   /// Read-only, and through WMI rather than a new P/Invoke because
   /// <see cref="IniFeatureStore"/> already queries Win32_Service the same way, so
   /// this adds no dependency. Querying a service's configuration needs no
   /// elevation; changing it does, which is exactly why the GUI reports and does
   /// not attempt the change.
   /// </summary>
   public sealed class WindowsServiceInfo
   {
      private WindowsServiceInfo()
      {
      }

      /// <summary>True when Windows knows of a service by this name at all.</summary>
      public bool Exists { get; private set; }

      /// <summary>
      /// The account the service logs on as, verbatim from the SCM:
      /// "LocalSystem", "NT AUTHORITY\NetworkService", "NT SERVICE\hMailServer",
      /// ".\hmail" or "DOMAIN\user". Empty when unknown.
      /// </summary>
      public string StartName { get; private set; } = "";

      /// <summary>"Auto", "Manual", "Disabled", … or empty when unknown.</summary>
      public string StartMode { get; private set; } = "";

      /// <summary>"Running", "Stopped", … or empty when unknown.</summary>
      public string State { get; private set; } = "";

      /// <summary>The registered command line, including arguments. Empty when unknown.</summary>
      public string PathName { get; private set; } = "";

      /// <summary>Why the query failed, or null when it did not.</summary>
      public string Error { get; private set; }

      /// <summary>
      /// Queries the SCM. Never throws: a machine where WMI is unavailable, or an
      /// account without the rights to enumerate services, must produce "cannot
      /// tell" rather than an exception in the middle of a settings page.
      /// </summary>
      public static WindowsServiceInfo Query(string serviceName = "hMailServer")
      {
         var info = new WindowsServiceInfo();

         try
         {
            // The name is a literal here, never user input, but it is escaped
            // anyway so that a caller passing something odd produces an empty
            // result rather than a malformed query.
            string escaped = (serviceName ?? "").Replace("\\", "\\\\").Replace("'", "\\'");

            using var searcher = new ManagementObjectSearcher(
               "SELECT Name, StartName, StartMode, State, PathName FROM Win32_Service WHERE Name='" + escaped + "'");

            foreach (ManagementObject service in searcher.Get())
            {
               info.Exists = true;
               info.StartName = (service["StartName"] as string ?? "").Trim();
               info.StartMode = (service["StartMode"] as string ?? "").Trim();
               info.State = (service["State"] as string ?? "").Trim();
               info.PathName = (service["PathName"] as string ?? "").Trim();
               break;
            }
         }
         catch (Exception ex)
         {
            info.Error = ex.Message;
         }

         return info;
      }

      /// <summary>
      /// True when the two names identify the same Windows account.
      ///
      /// String equality is not enough, because the SCM and an administrator spell
      /// the same account differently. An empty <c>ServiceAccountName</c> means
      /// "leave it alone", which at registration time becomes LocalSystem, and the
      /// SCM spells LocalSystem three ways. A local account may be written
      /// "hmail", ".\hmail" or "MACHINE\hmail", and the SCM normalises to the
      /// second. Getting this wrong in either direction is worse than not checking:
      /// a false mismatch tells an administrator to re-run a registration they do
      /// not need, and a false match hides the one they do.
      /// </summary>
      public static bool SameAccount(string configured, string actual, string machineName)
      {
         string a = Canonical(configured, machineName);
         string b = Canonical(actual, machineName);

         return a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
      }

      /// <summary>
      /// One account name reduced to a comparable form. Returns "" for a name that
      /// says nothing, so that "unknown" never compares equal to "unknown".
      /// </summary>
      public static string Canonical(string name, string machineName)
      {
         string value = (name ?? "").Trim();
         if (value.Length == 0)
            return "";

         // The three spellings of LocalSystem, which is what an empty
         // ServiceAccountName produces at registration.
         if (string.Equals(value, "LocalSystem", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(value, ".\\LocalSystem", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(value, "NT AUTHORITY\\System", StringComparison.OrdinalIgnoreCase))
         {
            return "localsystem";
         }

         // A bare name, or one qualified with this machine, is the same local
         // account the SCM writes as ".\name".
         int slash = value.IndexOf('\\');
         if (slash < 0)
            return "." + "\\" + value.ToLowerInvariant();

         string authority = value.Substring(0, slash);
         string account = value.Substring(slash + 1);

         if (!string.IsNullOrEmpty(machineName) &&
             string.Equals(authority, machineName, StringComparison.OrdinalIgnoreCase))
         {
            authority = ".";
         }

         return (authority + "\\" + account).ToLowerInvariant();
      }

      /// <summary>
      /// The executable out of a registered service's command line.
      ///
      /// <see cref="PathName"/> holds the binary AND its arguments - hMailServer
      /// registers itself as <c>"C:\...\hMailServer.exe" RunAsService</c> - so
      /// quoting the whole string into a /Register command would name a path that
      /// does not exist.
      ///
      /// The unquoted branch is a guess and is treated as one. Splitting on the
      /// first space is right for <c>C:\hMailServer\hMailServer.exe RunAsService</c>
      /// and wrong for an unquoted path that contains a space, where it would
      /// truncate to "C:\Program" - so the split only happens when what precedes
      /// the space actually looks like the executable. Windows does start unquoted
      /// paths with spaces (it probes each prefix), so this case is reachable on a
      /// hand-registered service, and printing a truncated path in an instruction
      /// an administrator is told to paste into an elevated prompt is worse than
      /// printing a slightly long one.
      /// </summary>
      public static string ExecutableFrom(string pathName)
      {
         string value = (pathName ?? "").Trim();
         if (value.Length == 0)
            return "hMailServer.exe";

         if (value.StartsWith("\""))
         {
            int close = value.IndexOf('"', 1);
            return close > 1 ? value.Substring(1, close - 1) : value.Trim('"');
         }

         // Prefer the last ".exe" boundary: it is the only marker in an unquoted
         // command line that says where the path ends.
         int exe = value.LastIndexOf(".exe", StringComparison.OrdinalIgnoreCase);
         if (exe > 0)
            return value.Substring(0, exe + 4);

         int space = value.IndexOf(' ');
         return space > 0 ? value.Substring(0, space) : value;
      }

      /// <summary>
      /// How to describe the running account to an administrator, spelling out the
      /// two cases where the SCM's own wording under-states what the account can do.
      /// </summary>
      public static string DescribeAccount(string startName)
      {
         string value = (startName ?? "").Trim();
         if (value.Length == 0)
            return "an account Windows did not report";

         if (string.Equals(Canonical(value, null), "localsystem", StringComparison.OrdinalIgnoreCase))
            return "LocalSystem, which is the most privileged account on this machine";

         if (value.StartsWith("NT SERVICE\\", StringComparison.OrdinalIgnoreCase))
            return value + ", a virtual service account with no password";

         return value;
      }
   }
}
