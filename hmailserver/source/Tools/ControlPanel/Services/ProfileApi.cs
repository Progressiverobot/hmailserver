// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Runtime.InteropServices;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// The kernel32 private-profile functions, declared once with source-generated
   /// marshalling instead of the five runtime-marshalled copies the pages used to
   /// carry. The Control Panel keeps editing hMailServer.INI through the same Win32
   /// API the server reads it with, so the write-through cache, the flush idiom (a
   /// null section) and the file's ANSI-unless-UTF-16 handling stay exactly what the
   /// server expects. Only the declarations changed: LibraryImport generates the
   /// marshalling at compile time, which is why the project allows unsafe code.
   /// </summary>
   internal static partial class ProfileApi
   {
      [LibraryImport("kernel32.dll", EntryPoint = "GetPrivateProfileStringW",
         StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
      private static partial int GetPrivateProfileString(string section, string key, string defaultValue,
         Span<char> result, int size, string filePath);

      [LibraryImport("kernel32.dll", EntryPoint = "WritePrivateProfileStringW",
         StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
      [return: MarshalAs(UnmanagedType.Bool)]
      private static partial bool WritePrivateProfileString(string section, string key, string value, string filePath);

      [LibraryImport("kernel32.dll", EntryPoint = "GetPrivateProfileSectionW",
         StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
      private static partial int GetPrivateProfileSection(string section, Span<char> result, int size, string filePath);

      [LibraryImport("kernel32.dll", EntryPoint = "WritePrivateProfileSectionW",
         StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
      [return: MarshalAs(UnmanagedType.Bool)]
      private static partial bool WritePrivateProfileSection(string section, string value, string filePath);

      /// <summary>
      /// One value, read into a buffer of <paramref name="capacity"/> characters. A
      /// longer value is truncated to the buffer, which is deliberate: the callers
      /// pass the size the server's own reader uses so the page shows exactly the
      /// value - and exactly the truncation - the server sees.
      /// </summary>
      public static string ReadString(string section, string key, string defaultValue, string filePath, int capacity)
      {
         var buffer = new char[capacity];
         int copied = GetPrivateProfileString(section, key, defaultValue, buffer, capacity, filePath);
         return new string(buffer, 0, Math.Max(0, Math.Min(copied, capacity)));
      }

      public static bool WriteString(string section, string key, string value, string filePath)
      {
         return WritePrivateProfileString(section, key, value, filePath);
      }

      /// <summary>Removes a whole section, which is how the API deletes one.</summary>
      public static bool DeleteSection(string section, string filePath)
      {
         return WritePrivateProfileString(section, null, null, filePath);
      }

      /// <summary>
      /// Flushes the cache the API keeps, so a value is on disk before the server
      /// next compares the file's write time.
      /// </summary>
      public static bool Flush(string filePath)
      {
         return WritePrivateProfileString(null, null, null, filePath);
      }

      /// <summary>The key=value lines of one section, in file order.</summary>
      public static List<string> ReadSectionLines(string section, string filePath)
      {
         var lines = new List<string>();

         // 32767 characters is the documented ceiling for this API.
         var buffer = new char[32767];
         int copied = GetPrivateProfileSection(section, buffer, buffer.Length, filePath);

         int start = 0;
         for (int i = 0; i < copied; i++)
         {
            if (buffer[i] != '\0')
               continue;
            if (i > start)
               lines.Add(new string(buffer, start, i - start));
            start = i + 1;
         }

         return lines;
      }

      /// <summary>Replaces one section's lines wholesale (an empty list clears it).</summary>
      public static bool WriteSectionLines(string section, IReadOnlyList<string> lines, string filePath)
      {
         // The API wants "line\0line\0\0"; the marshaller appends one terminator,
         // so an explicit trailing '\0' completes the pair. An empty section is a
         // lone '\0', which the marshaller turns into the required double
         // terminator.
         string joined = lines.Count == 0 ? "\0" : string.Join("\0", lines) + "\0";
         return WritePrivateProfileSection(section, joined, filePath);
      }
   }
}
