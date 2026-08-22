// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>One message row whose backing file is missing from the store.</summary>
   public class MessageStoreConsistencyEntry
   {
      public string MessageId { get; set; } = "";
      public string Account { get; set; } = "";
      public string ExpectedPath { get; set; } = "";
   }

   /// <summary>
   /// Reads the recovery report the server's message-store consistency task
   /// writes to the log folder. The server exposes the scan result only as this
   /// file (plus a Prometheus gauge and an APPLICATION log line) - there is no
   /// COM surface for it - so parsing the file is the only way the Control
   /// Panel can show what the scan found.
   ///
   /// Format written by MessageStoreConsistencyTask::WriteReport_:
   ///
   ///   # hMailServer message-store consistency report
   ///   # Generated: &lt;timestamp&gt;
   ///   # Missing files: &lt;count&gt;
   ///   # Columns: messageid&lt;TAB&gt;account&lt;TAB&gt;expected-path
   ///   &lt;id&gt;\t&lt;account&gt;\t&lt;path&gt;
   ///   ...
   ///
   /// The file is rewritten on every run, so it always describes the most
   /// recent scan, and it is written even when nothing is missing.
   /// </summary>
   public class MessageStoreConsistencyReport
   {
      /// <summary>Name of the report file inside the configured log folder.</summary>
      public const string FileName = "hMailServer_messagestore_consistency.report";

      private const string GeneratedPrefix = "# Generated:";
      private const string MissingPrefix = "# Missing files:";

      /// <summary>Timestamp string from the report header, or null when absent.</summary>
      public string Generated { get; private set; }

      /// <summary>
      /// The count the server put in the header, or null when the header is
      /// missing or unreadable. This can legitimately differ from
      /// <see cref="Entries"/>.Count if the file was truncated mid-write, which
      /// is worth telling the administrator about rather than hiding.
      /// </summary>
      public int? ReportedMissingCount { get; private set; }

      /// <summary>The individual missing messages listed in the report.</summary>
      public IReadOnlyList<MessageStoreConsistencyEntry> Entries => entries_;

      private readonly List<MessageStoreConsistencyEntry> entries_ = new();

      /// <summary>True when the header count and the listed rows disagree.</summary>
      public bool IsTruncated => ReportedMissingCount.HasValue && ReportedMissingCount.Value != entries_.Count;

      /// <summary>Best available count: the header value, else the listed rows.</summary>
      public int MissingCount => ReportedMissingCount ?? entries_.Count;

      public static MessageStoreConsistencyReport Parse(string text)
      {
         var report = new MessageStoreConsistencyReport();
         if (string.IsNullOrEmpty(text))
            return report;

         foreach (string line in text.Split('\n').Select(raw => raw.TrimEnd('\r')).Where(l => l.Length != 0))
         {
            if (line[0] == '#')
            {
               if (line.StartsWith(GeneratedPrefix, StringComparison.OrdinalIgnoreCase))
               {
                  string value = line.Substring(GeneratedPrefix.Length).Trim();
                  if (value.Length > 0)
                     report.Generated = value;
               }
               else if (line.StartsWith(MissingPrefix, StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(line.Substring(MissingPrefix.Length).Trim(), out int count)
                        && count >= 0)
               {
                  report.ReportedMissingCount = count;
               }

               continue;
            }

            // messageid<TAB>account<TAB>expected-path. The account is empty when
            // the message row has no matching account (the server left-joins it),
            // and a path can in principle contain anything except a tab, so only
            // split into the three documented fields.
            string[] fields = line.Split('\t', 3);
            report.entries_.Add(new MessageStoreConsistencyEntry
            {
               MessageId = fields[0].Trim(),
               Account = fields.Length > 1 ? fields[1].Trim() : "",
               ExpectedPath = fields.Length > 2 ? fields[2].Trim() : ""
            });
         }

         return report;
      }
   }
}
