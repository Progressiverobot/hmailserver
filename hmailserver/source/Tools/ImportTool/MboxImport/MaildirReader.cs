// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ImportTool.MboxImport
{
   /// <summary>
   ///    One message in a Maildir: the file it is, the IMAP folder it belongs in, and the
   ///    flags its file name carries.
   /// </summary>
   internal sealed class MaildirMessage
   {
      public MaildirMessage(string folderName, string path, string flags)
      {
         FolderName = folderName;
         Path = path;
         Flags = flags;
      }

      public string FolderName { get; private set; }
      public string Path { get; private set; }

      /// <summary>IMAP flag names separated by spaces, e.g. "\Seen \Flagged"; empty for a message with none.</summary>
      public string Flags { get; private set; }
   }

   /// <summary>
   ///    Reads a Maildir (Dovecot, Courier, Postfix's local delivery) - the format where
   ///    every message is its own file. A folder is a directory with <c>cur</c>, <c>new</c>
   ///    and <c>tmp</c> beneath it: <c>new</c> holds what has arrived and never been
   ///    seen by a client, <c>cur</c> everything else, and <c>tmp</c> what a writer has not
   ///    finished, which is never read. Folders below the INBOX follow Maildir++: they
   ///    are directories beside <c>cur</c> whose names start with a dot and use a dot as
   ///    the hierarchy separator - <c>.Archive.2025</c> is <c>Archive.2025</c>, a folder
   ///    called 2025 inside Archive - which is also this server's default separator, so
   ///    the names pass through as they are.
   ///    <para />
   ///    A file in <c>cur</c> carries its flags in its name, after <c>:2,</c>: <c>S</c>
   ///    seen, <c>F</c> flagged, <c>R</c> replied, <c>D</c> draft, <c>T</c> trashed
   ///    (marked for deletion, not yet expunged). <c>P</c>, passed, has no IMAP flag. A
   ///    colon is not allowed in a Windows file name, so a Maildir that has been copied
   ///    here usually carries <c>;</c> or <c>!</c> in its place; all three are read.
   /// </summary>
   internal static class MaildirReader
   {
      public static bool IsMaildir(string directory)
      {
         return Directory.Exists(System.IO.Path.Join(directory, "cur")) &&
                Directory.Exists(System.IO.Path.Join(directory, "new"));
      }

      /// <summary>Every folder of the tree, the root first as INBOX, then the Maildir++ subfolders in name order.</summary>
      public static IEnumerable<KeyValuePair<string, string>> Folders(string root)
      {
         yield return new KeyValuePair<string, string>("INBOX", root);

         foreach (var directory in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
         {
            var name = System.IO.Path.GetFileName(directory);
            if (name == null || !name.StartsWith(".") || name == "." || name == "..")
               continue;
            if (!IsMaildir(directory))
               continue;

            yield return new KeyValuePair<string, string>(name.Substring(1), directory);
         }
      }

      /// <summary>The messages of one folder: <c>new</c> first, then <c>cur</c>, each in file-name order (which is arrival order, since the name starts with a timestamp).</summary>
      public static IEnumerable<MaildirMessage> Messages(string folderName, string folderDirectory)
      {
         foreach (var path in new[] { "new", "cur" }.Select(subdirectory => System.IO.Path.Join(folderDirectory, subdirectory)))
         {
            if (!Directory.Exists(path))
               continue;

            foreach (var file in Directory.GetFiles(path).OrderBy(f => f, StringComparer.Ordinal))
            {
               var name = System.IO.Path.GetFileName(file);
               if (name == null || name.StartsWith("."))
                  continue;

               yield return new MaildirMessage(folderName, file, FlagsOf(name));
            }
         }
      }

      /// <summary>How many messages the whole tree holds, for a progress bar.</summary>
      public static int Count(string root)
      {
         return Folders(root).Sum(folder => Messages(folder.Key, folder.Value).Count());
      }

      /// <summary>
      ///    The message's bytes, ready for the server: a Maildir written on Unix ends its
      ///    lines with LF alone, and the server's message store expects CRLF, as everything
      ///    that arrives by SMTP has. A CRLF already there is left alone, so a file written
      ///    with CRLF comes back unchanged.
      /// </summary>
      public static byte[] ReadMessage(string path)
      {
         return WithCrLf(File.ReadAllBytes(path));
      }

      internal static byte[] WithCrLf(byte[] bytes)
      {
         var lone = 0;
         for (var i = 0; i < bytes.Length; i++)
            if (bytes[i] == (byte)'\n' && (i == 0 || bytes[i - 1] != (byte)'\r'))
               lone++;
         if (lone == 0)
            return bytes;

         var result = new byte[bytes.Length + lone];
         var at = 0;
         for (var i = 0; i < bytes.Length; i++)
         {
            if (bytes[i] == (byte)'\n' && (i == 0 || bytes[i - 1] != (byte)'\r'))
               result[at++] = (byte)'\r';
            result[at++] = bytes[i];
         }
         return result;
      }

      internal static string FlagsOf(string fileName)
      {
         // The info part starts at the last "2," preceded by the separator - ':' in the
         // format, ';' or '!' where a colon cannot be in a file name.
         var at = -1;
         foreach (var separator in new[] { ":2,", ";2,", "!2," })
            at = Math.Max(at, fileName.LastIndexOf(separator, StringComparison.Ordinal));
         if (at < 0)
            return "";

         var flags = new List<string>();
         foreach (var letter in fileName.Substring(at + 3))
         {
            switch (letter)
            {
               case 'S': flags.Add("\\Seen"); break;
               case 'F': flags.Add("\\Flagged"); break;
               case 'R': flags.Add("\\Answered"); break;
               case 'D': flags.Add("\\Draft"); break;
               case 'T': flags.Add("\\Deleted"); break;
            }
         }

         return string.Join(" ", flags);
      }
   }
}
