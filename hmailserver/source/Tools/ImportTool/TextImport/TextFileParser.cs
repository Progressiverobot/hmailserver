// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using System.IO;

namespace ImportTool.TextImport
{
   internal class TextAccountEntry
   {
      public string Name;
      public string Password;
      public int MaxSizeMb;
   }

   internal class TextLineError
   {
      public int LineNumber;
      public string Reason;
   }

   /// <summary>
   /// Parses the account import format: one account per line as
   /// "name,password,maxsize-in-MB". Lines starting with # and blank lines are
   /// ignored. Malformed lines are collected as errors rather than aborting.
   /// </summary>
   internal class TextFileParser
   {
      public List<TextAccountEntry> Accounts { get; } = new List<TextAccountEntry>();
      public List<TextLineError> Errors { get; } = new List<TextLineError>();

      public void ParseFile(string path)
      {
         var lineNumber = 0;
         foreach (var rawLine in File.ReadLines(path))
         {
            lineNumber++;
            ParseLine(rawLine, lineNumber);
         }
      }

      internal void ParseLine(string rawLine, int lineNumber)
      {
         var line = rawLine.Trim();

         if (line.Length == 0 || line.StartsWith("#"))
            return;

         var fields = line.Split(',');
         if (fields.Length != 3)
         {
            Errors.Add(new TextLineError
            {
               LineNumber = lineNumber,
               Reason = "Expected 3 comma-separated fields (name, password, max size in MB), found " + fields.Length + "."
            });
            return;
         }

         var name = fields[0].Trim();
         var password = fields[1].Trim();
         var sizeText = fields[2].Trim();

         if (name.Length == 0)
         {
            Errors.Add(new TextLineError { LineNumber = lineNumber, Reason = "Account name is empty." });
            return;
         }

         if (name.Contains("@"))
         {
            Errors.Add(new TextLineError
            {
               LineNumber = lineNumber,
               Reason = "Account name must not include a domain (\"" + name + "\")."
            });
            return;
         }

         int maxSizeMb;
         if (!int.TryParse(sizeText, out maxSizeMb) || maxSizeMb < 0)
         {
            Errors.Add(new TextLineError
            {
               LineNumber = lineNumber,
               Reason = "Max size \"" + sizeText + "\" is not a non-negative integer."
            });
            return;
         }

         Accounts.Add(new TextAccountEntry { Name = name, Password = password, MaxSizeMb = maxSizeMb });
      }
   }
}
