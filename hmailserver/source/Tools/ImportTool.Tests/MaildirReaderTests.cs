// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using ImportTool.MboxImport;
using Xunit;

namespace ImportTool.Tests
{
   /// <summary>
   ///    The Maildir reader, on trees built here: which directories are folders, which
   ///    files are messages, the flags in a file name, and the line endings handed to
   ///    the server. The wizard around it is not tested - it has no silent mode - so
   ///    this is where the Maildir route's correctness lives.
   ///    <para />
   ///    The files written here carry ';' before the info part rather than the format's
   ///    ':', because a colon in a Windows file name is not a character but the start of
   ///    an alternate data stream - which is exactly why the reader accepts ';' and '!'
   ///    too: a Maildir that has been copied to Windows arrives with one of them.
   /// </summary>
   public sealed class MaildirReaderTests : IDisposable
   {
      private readonly string _root = Path.Join(Path.GetTempPath(), "hm-maildir-" + Guid.NewGuid().ToString("N"));

      public MaildirReaderTests()
      {
         Directory.CreateDirectory(_root);
      }

      public void Dispose()
      {
         try
         {
            Directory.Delete(_root, true);
         }
         catch (IOException)
         {
            // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
         }
      }

      private string Maildir(string relative = "")
      {
         var directory = relative.Length == 0 ? _root : Path.Join(_root, relative);
         foreach (var sub in new[] { "cur", "new", "tmp" })
            Directory.CreateDirectory(Path.Join(directory, sub));
         return directory;
      }

      private static void Message(string directory, string subdirectory, string name, string text = "From: a@b\nSubject: x\n\nbody\n")
      {
         File.WriteAllText(Path.Join(directory, subdirectory, name), text);
      }

      [Theory]
      [InlineData("1700000000.M1P1.host:2,FS", "\\Flagged \\Seen")]
      [InlineData("1700000000.M1P1.host;2,FS", "\\Flagged \\Seen")]
      [InlineData("1700000000.M1P1.host!2,FS", "\\Flagged \\Seen")]
      [InlineData("1700000000.M1P1.host:2,RS", "\\Answered \\Seen")]
      [InlineData("1700000000.M1P1.host:2,DT", "\\Draft \\Deleted")]
      [InlineData("1700000000.M1P1.host,S=1234:2,S", "\\Seen")]
      [InlineData("1700000000.M1P1.host:2,P", "")]
      [InlineData("1700000000.M1P1.host:2,", "")]
      [InlineData("1700000000.M1P1.host", "")]
      public void TheFlagsAreTheLettersAfterTheInfoMarker(string fileName, string expected)
      {
         Assert.Equal(expected, MaildirReader.FlagsOf(fileName));
      }

      [Fact]
      public void AMaildirIsADirectoryWithCurAndNew()
      {
         var maildir = Maildir();
         Directory.CreateDirectory(Path.Join(_root, "plain"));

         Assert.True(MaildirReader.IsMaildir(maildir));
         Assert.False(MaildirReader.IsMaildir(Path.Join(_root, "plain")));
         Assert.False(MaildirReader.IsMaildir(Path.Join(_root, "missing")));
      }

      [Fact]
      public void TheRootIsTheInboxAndDottedMaildirsBesideItAreTheFolders()
      {
         Maildir();
         Maildir(".Sent");
         Maildir(".Archive");
         Maildir(".Archive.2025");
         Directory.CreateDirectory(Path.Join(_root, ".notamaildir"));
         Directory.CreateDirectory(Path.Join(_root, "undotted"));
         Directory.CreateDirectory(Path.Join(_root, "undotted", "cur"));
         Directory.CreateDirectory(Path.Join(_root, "undotted", "new"));

         var folders = MaildirReader.Folders(_root).ToList();

         Assert.Equal(new[] { "INBOX", "Archive", "Archive.2025", "Sent" }, folders.Select(f => f.Key).ToArray());
         Assert.Equal(_root, folders[0].Value);
         Assert.Equal(Path.Join(_root, ".Archive.2025"), folders[2].Value);
      }

      [Fact]
      public void NewThenCurAreTheMessagesAndTmpAndDotfilesAreNot()
      {
         var maildir = Maildir();
         Message(maildir, "new", "1700000001.M2P1.host");
         Message(maildir, "cur", "1700000000.M1P1.host;2,FS");
         Message(maildir, "cur", "1700000002.M3P1.host;2,");
         Message(maildir, "tmp", "1700000003.M4P1.host");
         Message(maildir, "cur", ".uidlist", "not a message");

         var messages = MaildirReader.Messages("INBOX", maildir).ToList();

         Assert.Equal(new[] { "1700000001.M2P1.host", "1700000000.M1P1.host;2,FS", "1700000002.M3P1.host;2," },
            messages.Select(m => Path.GetFileName(m.Path)).ToArray());
         Assert.Equal(new[] { "", "\\Flagged \\Seen", "" }, messages.Select(m => m.Flags).ToArray());
         Assert.All(messages, m => Assert.Equal("INBOX", m.FolderName));
         Assert.Equal(3, MaildirReader.Count(_root));
      }

      [Fact]
      public void AFolderWithoutNewStillYieldsCur()
      {
         var directory = Path.Join(_root, ".Odd");
         Directory.CreateDirectory(Path.Join(directory, "cur"));
         Message(directory, "cur", "1700000000.M1P1.host;2,S");

         Assert.Single(MaildirReader.Messages("Odd", directory));
      }

      [Fact]
      public void LoneLineFeedsBecomeCrLfAndExistingCrLfIsLeftAlone()
      {
         var unix = Encoding.ASCII.GetBytes("From: a@b\nSubject: x\n\nbody\n");
         var converted = MaildirReader.WithCrLf(unix);
         Assert.Equal("From: a@b\r\nSubject: x\r\n\r\nbody\r\n", Encoding.ASCII.GetString(converted));

         var dos = Encoding.ASCII.GetBytes("From: a@b\r\nSubject: x\r\n\r\nbody\r\n");
         Assert.Same(dos, MaildirReader.WithCrLf(dos));

         var mixed = Encoding.ASCII.GetBytes("a\r\nb\nc\r\n");
         Assert.Equal("a\r\nb\r\nc\r\n", Encoding.ASCII.GetString(MaildirReader.WithCrLf(mixed)));

         Assert.Equal("\r\n", Encoding.ASCII.GetString(MaildirReader.WithCrLf(new[] { (byte)'\n' })));
         Assert.Empty(MaildirReader.WithCrLf(Array.Empty<byte>()));
      }

      [Fact]
      public void ReadMessageReadsTheFileWithCrLf()
      {
         var maildir = Maildir();
         Message(maildir, "cur", "1700000000.M1P1.host;2,S", "Subject: x\n\nbody\n");

         var bytes = MaildirReader.ReadMessage(Path.Join(maildir, "cur", "1700000000.M1P1.host;2,S"));

         Assert.Equal("Subject: x\r\n\r\nbody\r\n", Encoding.ASCII.GetString(bytes));
      }
   }
}
