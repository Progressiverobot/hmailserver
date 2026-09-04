// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RegressionTests.Shared
{
   /// <summary>
   ///    A managed reader and writer for the .ini files the profile-string API
   ///    edits, with the same file semantics: a section or key match is
   ///    case-insensitive, a value of null removes the key, a key of null removes
   ///    the section, a missing section is created at the end, a value read back
   ///    loses one pair of enclosing quotes, every other line is left exactly as it
   ///    was, and the file is UTF-16 only when it starts with the FF FE mark (or
   ///    UTF-8 when it starts with the EF BB BF one) and ANSI otherwise. The
   ///    parameter order matches the Win32 functions, so a caller that used to
   ///    declare them changes only the class name. The Win32 write cache does not
   ///    exist here, so the (null, null, null) flush idiom is a no-op that still
   ///    answers true.
   /// </summary>
   public static class IniFile
   {
      public static bool WritePrivateProfileString(string section, string key, string value, string filePath)
      {
         if (section == null)
            return true;   // the flush idiom: nothing to write, nothing cached

         Encoding encoding;
         string newline;
         List<string> lines = Read_(filePath, out encoding, out newline);

         int sectionStart = FindSection_(lines, section);

         if (key == null)
         {
            if (sectionStart >= 0)
            {
               int end = SectionEnd_(lines, sectionStart);
               lines.RemoveRange(sectionStart, end - sectionStart);
               Write_(filePath, lines, encoding, newline);
            }
            return true;
         }

         if (sectionStart < 0)
         {
            if (value == null)
               return true;

            if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0)
               lines.Add("");
            lines.Add("[" + section + "]");
            lines.Add(key + "=" + value);
            Write_(filePath, lines, encoding, newline);
            return true;
         }

         int sectionEnd = SectionEnd_(lines, sectionStart);
         int keyLine = -1;
         for (int i = sectionStart + 1; i < sectionEnd; i++)
         {
            if (KeyOf_(lines[i]).Equals(key, StringComparison.OrdinalIgnoreCase))
            {
               keyLine = i;
               break;
            }
         }

         if (value == null)
         {
            if (keyLine >= 0)
            {
               lines.RemoveAt(keyLine);
               Write_(filePath, lines, encoding, newline);
            }
            return true;
         }

         if (keyLine >= 0)
         {
            lines[keyLine] = key + "=" + value;
         }
         else
         {
            // Insert before the blank lines that separate this section from the next.
            int insertAt = sectionEnd;
            while (insertAt > sectionStart + 1 && lines[insertAt - 1].Trim().Length == 0)
               insertAt--;
            lines.Insert(insertAt, key + "=" + value);
         }

         Write_(filePath, lines, encoding, newline);
         return true;
      }

      public static uint GetPrivateProfileString(string section, string key, string defaultValue,
                                                 StringBuilder returnedString, uint size, string filePath)
      {
         string value = GetValue(section, key, defaultValue ?? "", filePath);
         if (size > 0 && value.Length >= size)
            value = value.Substring(0, (int) size - 1);   // the API keeps room for its terminator
         returnedString.Length = 0;
         returnedString.Append(value);
         return (uint) value.Length;
      }

      public static string GetValue(string section, string key, string defaultValue, string filePath)
      {
         if (!File.Exists(filePath))
            return defaultValue;

         Encoding encoding;
         string newline;
         List<string> lines = Read_(filePath, out encoding, out newline);

         int sectionStart = FindSection_(lines, section);
         if (sectionStart < 0)
            return defaultValue;

         int sectionEnd = SectionEnd_(lines, sectionStart);
         for (int i = sectionStart + 1; i < sectionEnd; i++)
         {
            string line = lines[i];
            if (!KeyOf_(line).Equals(key, StringComparison.OrdinalIgnoreCase))
               continue;

            int eq = line.IndexOf('=');
            string value = eq < 0 ? "" : line.Substring(eq + 1).Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[value.Length - 1] == value[0])
               value = value.Substring(1, value.Length - 2);
            return value;
         }

         return defaultValue;
      }

      private static string KeyOf_(string line)
      {
         string trimmed = line.TrimStart();
         if (trimmed.StartsWith(";") || trimmed.StartsWith("#") || trimmed.StartsWith("["))
            return "";
         int eq = trimmed.IndexOf('=');
         return eq < 0 ? "" : trimmed.Substring(0, eq).Trim();
      }

      private static int FindSection_(List<string> lines, string section)
      {
         string header = "[" + section + "]";
         for (int i = 0; i < lines.Count; i++)
         {
            if (lines[i].Trim().Equals(header, StringComparison.OrdinalIgnoreCase))
               return i;
         }
         return -1;
      }

      private static int SectionEnd_(List<string> lines, int sectionStart)
      {
         for (int i = sectionStart + 1; i < lines.Count; i++)
         {
            string trimmed = lines[i].Trim();
            if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
               return i;
         }
         return lines.Count;
      }

      private static List<string> Read_(string filePath, out Encoding encoding, out string newline)
      {
         encoding = Encoding.Default;
         newline = "\r\n";

         if (!File.Exists(filePath))
            return new List<string>();

         byte[] bytes = File.ReadAllBytes(filePath);

         if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            encoding = Encoding.Unicode;
         else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            encoding = new UTF8Encoding(true);

         int skip = encoding.GetPreamble().Length;
         string text = encoding.GetString(bytes, skip, bytes.Length - skip);

         if (text.IndexOf("\r\n", StringComparison.Ordinal) < 0 && text.IndexOf('\n') >= 0)
            newline = "\n";

         var lines = new List<string>(text.Split('\n'));
         for (int i = 0; i < lines.Count; i++)
            lines[i] = lines[i].TrimEnd('\r');

         // A trailing newline yields an empty last element; drop it so the
         // rewrite does not grow the file by a line on every write.
         if (lines.Count > 0 && lines[lines.Count - 1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

         return lines;
      }

      private static void Write_(string filePath, List<string> lines, Encoding encoding, string newline)
      {
         byte[] preamble = encoding.GetPreamble();
         byte[] body = encoding.GetBytes(string.Join(newline, lines.ToArray()) + newline);
         byte[] all = new byte[preamble.Length + body.Length];
         Buffer.BlockCopy(preamble, 0, all, 0, preamble.Length);
         Buffer.BlockCopy(body, 0, all, preamble.Length, body.Length);
         File.WriteAllBytes(filePath, all);
      }
   }
}
