// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.IO;

namespace ImportTool.MboxImport
{
   /// <summary>
   /// Splits an mbox file into individual messages, streaming so that large
   /// mailboxes do not need to fit in memory.
   ///
   /// A message starts at a line beginning "From " (the mbox envelope line) at
   /// the start of the file or directly after a blank line, which covers the
   /// classic mbox variants as well as Thunderbird's "From - <date>" form. Both
   /// LF and CRLF line endings are handled, the final message is emitted, and
   /// mboxrd-style ">From " quoting is reversed. Message bytes are otherwise
   /// preserved exactly; no dot-stuffing or trailing separators are added.
   /// </summary>
   internal static class MboxParser
   {
      private static readonly byte[] FromMarker = { (byte)'F', (byte)'r', (byte)'o', (byte)'m', (byte)' ' };

      /// <summary>
      /// Enumerates the messages in the given mbox file. Each message is handed
      /// to <paramref name="onMessage"/> as its raw bytes (without the envelope
      /// "From " line). <paramref name="onProgress"/> receives the number of
      /// bytes consumed so far.
      /// </summary>
      public static void Parse(string path, Action<byte[]> onMessage, Action<long> onProgress)
      {
         using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
         {
            var current = new MemoryStream();
            var messageStarted = false;
            var previousLineBlank = true;   // start of file counts as a boundary

            long consumed = 0;
            byte[] line;
            while ((line = ReadLine(stream)) != null)
            {
               consumed += line.Length;

               if (previousLineBlank && StartsWithFromMarker(line))
               {
                  // Envelope line: flush the previous message and skip the line.
                  if (messageStarted)
                  {
                     onMessage(TrimTrailingBlankLine(current));
                     current = new MemoryStream();
                  }

                  messageStarted = true;
                  previousLineBlank = false;

                  if (onProgress != null)
                     onProgress(consumed);

                  continue;
               }

               previousLineBlank = IsBlank(line);

               if (!messageStarted)
                  continue;   // preamble before the first envelope line

               WriteBodyLine(current, line);
            }

            if (messageStarted)
               onMessage(TrimTrailingBlankLine(current));

            if (onProgress != null)
               onProgress(consumed);
         }
      }

      /// <summary>
      /// Reads one line including its terminator. Returns null at end of file.
      /// </summary>
      private static byte[] ReadLine(Stream stream)
      {
         var buffer = new MemoryStream();
         int value;
         while ((value = stream.ReadByte()) != -1)
         {
            buffer.WriteByte((byte)value);
            if (value == '\n')
               break;
         }

         return buffer.Length == 0 ? null : buffer.ToArray();
      }

      private static bool StartsWithFromMarker(byte[] line)
      {
         if (line.Length < FromMarker.Length)
            return false;

         for (int i = 0; i < FromMarker.Length; i++)
         {
            if (line[i] != FromMarker[i])
               return false;
         }

         return true;
      }

      private static bool IsBlank(byte[] line)
      {
         foreach (var b in line)
         {
            if (b != '\r' && b != '\n')
               return false;
         }

         return true;
      }

      /// <summary>
      /// Writes a message body line, reversing one level of mboxrd quoting
      /// (a line of the form ">>...>From " loses one leading '>').
      /// </summary>
      private static void WriteBodyLine(MemoryStream message, byte[] line)
      {
         int quoteCount = 0;
         while (quoteCount < line.Length && line[quoteCount] == '>')
            quoteCount++;

         if (quoteCount > 0 && StartsWithFromMarkerAt(line, quoteCount))
         {
            message.Write(line, 1, line.Length - 1);
            return;
         }

         message.Write(line, 0, line.Length);
      }

      private static bool StartsWithFromMarkerAt(byte[] line, int offset)
      {
         if (line.Length - offset < FromMarker.Length)
            return false;

         for (int i = 0; i < FromMarker.Length; i++)
         {
            if (line[offset + i] != FromMarker[i])
               return false;
         }

         return true;
      }

      /// <summary>
      /// The blank line before the next envelope line is a separator, not part
      /// of the message; remove one trailing blank line if present.
      /// </summary>
      private static byte[] TrimTrailingBlankLine(MemoryStream message)
      {
         var bytes = message.ToArray();

         int end = bytes.Length;
         if (end >= 1 && bytes[end - 1] == '\n')
         {
            end--;
            if (end >= 1 && bytes[end - 1] == '\r')
               end--;

            // Only trim when what remains still ends a line — i.e. the removed
            // bytes formed a blank line of their own.
            if (end == 0 || bytes[end - 1] == '\n')
            {
               var result = new byte[end];
               Array.Copy(bytes, result, end);
               return result;
            }
         }

         return bytes;
      }
   }
}
