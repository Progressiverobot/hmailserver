// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    The on-disk format of every text file the server writes with a byte order
   ///    mark - Sieve scripts, the backup index, the backup and event logs, the
   ///    rate-limiter state - is UTF-16LE, and it is the same file whichever
   ///    platform wrote it. On Windows a wchar_t is two bytes and a String's own
   ///    bytes were always that format; the server now goes through one codec on
   ///    every platform, and this fixture pins the Windows side of the contract
   ///    from outside the process: a file written by .NET's Encoding.Unicode - the
   ///    reference UTF-16LE-with-mark writer on this platform - is read back by
   ///    the server character for character, and a file the server writes is read
   ///    back by .NET the same way. The C++ self-test (FileUtilitiesTester) holds
   ///    the same bytes as an array and proves the Linux build decodes and
   ///    re-encodes them identically, so the two together are the cross-platform
   ///    claim.
   ///
   ///    The file used is the account's active Sieve script, because SieveStorage
   ///    reads it through ReadCompleteTextFile and writes it through
   ///    WriteToFile(..., true) - the two functions every marked file goes through
   ///    - and because the COM property Account.SieveScript exposes both sides
   ///    without a protocol session in between. The property is invoked late-bound
   ///    for the reason SieveAccountScript gives.
   /// </summary>
   [TestFixture]
   public class Utf16FileFormat : TestFixtureBase
   {
      // "# <nihongo> <grinning face> caf<e-acute>" then a rule. The emoji is above
      // the Basic Multilingual Plane, so it is a surrogate pair in UTF-16 and the
      // one character whose on-disk form differs from a Linux wchar_t; the CJK and
      // the accented character are one code unit each and cover the ordinary case.
      private const string Script =
         "require \"fileinto\";\r\n" +
         "# \u65E5\u672C\u8A9E \U0001F600 caf\u00E9\r\n" +
         "if header :contains \"Subject\" \"lottery\" {\r\n" +
         "  fileinto \"Filed\";\r\n" +
         "}\r\n";

      private static string GetScript(object account)
      {
         return (string) account.GetType().InvokeMember(
            "SieveScript", BindingFlags.GetProperty, null, account, null);
      }

      private static void SetScript(object account, string script)
      {
         account.GetType().InvokeMember(
            "SieveScript", BindingFlags.SetProperty, null, account, new object[] { script });
      }

      // The path layout is SieveStorage's, the same one SieveIncludeDelivery writes
      // to, and TestServerWritesUtf16 below proves it is where the server writes.
      private string ActiveScriptPath(Account account)
      {
         string dataDirectory = _settings.Directories.DataDirectory;
         string domain = account.Address.Substring(account.Address.IndexOf('@') + 1);
         string localPart = account.Address.Substring(0, account.Address.IndexOf('@'));

         return Paths.Combine(dataDirectory, "Sieve", domain, localPart, "active.sieve");
      }

      [Test]
      [Description("A UTF-16LE file written by .NET, emoji and CJK included, is read by the server character for character and the script in it runs.")]
      public void TestServerReadsUtf16WrittenByDotNet()
      {
         Account account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "utf16-read@example.test", "test");
         account.IMAPFolders.Add("Filed");

         string path = ActiveScriptPath(account);
         Directory.CreateDirectory(Path.GetDirectoryName(path));

         // Encoding.Unicode is UTF-16LE, and WriteAllText puts its preamble - the
         // FF FE mark - at the front. That is exactly the file a Windows server
         // writes, which the byte check below confirms rather than assumes.
         File.WriteAllText(path, Script, Encoding.Unicode);

         byte[] onDisk = File.ReadAllBytes(path);
         Assert.AreEqual(0xFF, onDisk[0]);
         Assert.AreEqual(0xFE, onDisk[1]);
         Assert.IsTrue(onDisk.Skip(2).SequenceEqual(Encoding.Unicode.GetBytes(Script)));

         // The emoji went to disk as the surrogate pair D83D DE00, low byte first.
         // These four bytes are the ones the C++ self-test embeds; if this ever
         // fails, the two tests have stopped describing the same file.
         byte[] surrogatePair = { 0x3D, 0xD8, 0x00, 0xDE };
         Assert.IsTrue(Enumerable.Range(0, onDisk.Length - 3).Any(i => onDisk.Skip(i).Take(4).SequenceEqual(surrogatePair)));

         // The server decodes the file through ReadCompleteTextFile and hands the
         // String back as a BSTR, so an exact match here is the whole decode path.
         Assert.AreEqual(Script, GetScript(account));

         // And the decoded script is a script: a message the rule matches is filed
         // by it, so the bytes reached the Sieve interpreter as text it could run.
         SmtpClientSimulator.StaticSend("sender@example.test", account.Address, "You won the lottery!", "body");

         IMAPFolder filed = account.IMAPFolders.get_ItemByName("Filed");
         CustomAsserts.AssertFolderMessageCount(filed, 1);

         IMAPFolder inbox = account.IMAPFolders.get_ItemByName("INBOX");
         CustomAsserts.AssertFolderMessageCount(inbox, 0);

         SetScript(account, "");
      }

      [Test]
      [Description("A file the server writes with a byte order mark is UTF-16LE with the mark, byte for byte what .NET's Encoding.Unicode produces, and .NET reads it back.")]
      public void TestServerWritesUtf16()
      {
         Account account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "utf16-write@example.test", "test");

         SetScript(account, Script);

         string path = ActiveScriptPath(account);
         Assert.IsTrue(File.Exists(path), "The server did not write the active script where SieveStorage's layout says it does: " + path);

         byte[] onDisk = File.ReadAllBytes(path);
         Assert.AreEqual(0xFF, onDisk[0]);
         Assert.AreEqual(0xFE, onDisk[1]);
         Assert.IsTrue(onDisk.Skip(2).SequenceEqual(Encoding.Unicode.GetBytes(Script)),
            "The bytes after the mark are not the UTF-16LE encoding of the script.");

         // ReadAllText honours the mark it finds, so this is the file as any
         // Windows program would read it.
         Assert.AreEqual(Script, File.ReadAllText(path, Encoding.Unicode));

         SetScript(account, "");
      }
   }
}
