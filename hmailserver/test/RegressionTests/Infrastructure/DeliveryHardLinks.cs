// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    DeliveryHardLinks=1: a message to several local recipients is one file with
   ///    a name in each recipient's folder rather than a copy per recipient. The
   ///    property that makes sharing safe is that every rewrite of a message file is
   ///    a temporary file renamed into place, so a change to one recipient's copy -
   ///    a rule that sets a header, a script that saves the message - replaces that
   ///    name alone and the others keep what they had. Both halves are asserted on
   ///    the file system itself: the file identity and the link count that NTFS
   ///    reports for each recipient's file.
   /// </summary>
   [TestFixture]
   public class DeliveryHardLinks : TestFixtureBase
   {
      private const string Password = "test";

      [StructLayout(LayoutKind.Sequential)]
      private struct FileInformation
      {
         public uint FileAttributes;
         public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
         public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
         public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
         public uint VolumeSerialNumber;
         public uint FileSizeHigh;
         public uint FileSizeLow;
         public uint NumberOfLinks;
         public uint FileIndexHigh;
         public uint FileIndexLow;
      }

      [DllImport("kernel32.dll", SetLastError = true)]
      private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

      // The NTFS identity of a file (volume + file index) and how many names it has.
      private static (string identity, uint links) Identify(string path)
      {
         using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
         {
            FileInformation information;
            Assert.IsTrue(GetFileInformationByHandle(stream.SafeFileHandle, out information),
               "GetFileInformationByHandle failed: " + Marshal.GetLastWin32Error());
            return (information.VolumeSerialNumber + ":" + information.FileIndexHigh + ":" + information.FileIndexLow,
                    information.NumberOfLinks);
         }
      }

      [TearDown]
      public void BackToCopies()
      {
         IniFileSetting.Delete("DeliveryHardLinks");
         _application.Reinitialize();
         _settings.AddDeliveredToHeader = true;
      }

      // Sharing needs every recipient's copy to be the same bytes, and Delivered-To
      // names the recipient - so the header is turned off with the links on, the
      // way an operator who wants the links has to turn it off.
      private void SetHardLinks(bool on)
      {
         if (on)
            IniFileSetting.Write("DeliveryHardLinks", "1");
         else
            IniFileSetting.Delete("DeliveryHardLinks");
         _application.Reinitialize();
         _settings.AddDeliveredToHeader = !on;
      }

      // One message to three local recipients in one transaction; the file each
      // recipient's copy lives in.
      private List<Message> DeliverToThree(out List<Account> accounts)
      {
         accounts = new List<Account>
         {
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "one@example.test", Password),
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "two@example.test", Password),
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "three@example.test", Password)
         };

         SmtpClientSimulator.StaticSend("sender@example.test",
            new List<string> { accounts[0].Address, accounts[1].Address, accounts[2].Address }, "Shared", "One body, three names.");

         var messages = new List<Message>();
         foreach (Account account in accounts)
         {
            Pop3ClientSimulator.AssertMessageCount(account.Address, Password, 1);
            messages.Add(account.IMAPFolders.get_ItemByName("INBOX").Messages[0]);
         }
         return messages;
      }

      [Test]
      [Description("With DeliveryHardLinks on, three recipients' copies are one NTFS file with three names, and the queue entry is gone.")]
      public void ThreeRecipientsShareOneFileWhenEnabled()
      {
         SetHardLinks(true);
         List<Account> accounts;
         List<Message> messages = DeliverToThree(out accounts);

         var first = Identify(messages[0].Filename);
         foreach (Message message in messages)
         {
            var identity = Identify(message.Filename);
            Assert.AreEqual(first.identity, identity.identity, "Every recipient's file is the same file: " + message.Filename);
            Assert.AreEqual(3u, identity.links, "Three names, one file, no queue copy left: " + message.Filename);
         }
      }

      [Test]
      [Description("A rewrite of one recipient's copy replaces that name alone: it becomes its own file, and the other two still share the old content.")]
      public void ARewriteOfOneCopyLeavesTheOthersShared()
      {
         SetHardLinks(true);
         List<Account> accounts;
         List<Message> messages = DeliverToThree(out accounts);

         Message rewritten = messages[1];
         rewritten.set_HeaderValue("X-Rewritten", "this copy only");
         rewritten.Save();

         var changed = Identify(rewritten.Filename);
         Assert.AreEqual(1u, changed.links, "The rewritten copy is a file of its own.");
         StringAssert.Contains("X-Rewritten: this copy only", File.ReadAllText(rewritten.Filename));

         var keptA = Identify(messages[0].Filename);
         var keptB = Identify(messages[2].Filename);
         Assert.AreEqual(keptA.identity, keptB.identity, "The other two still share one file.");
         Assert.AreEqual(2u, keptA.links, "Two names remain on the original.");
         Assert.AreNotEqual(changed.identity, keptA.identity);
         StringAssert.DoesNotContain("X-Rewritten", File.ReadAllText(messages[0].Filename), "The shared content was not touched.");
         StringAssert.DoesNotContain("X-Rewritten", File.ReadAllText(messages[2].Filename));
      }

      [Test]
      [Description("Off - the default - every recipient gets a file of its own, as always.")]
      public void OffByDefaultEveryRecipientGetsItsOwnFile()
      {
         SetHardLinks(false);
         List<Account> accounts;
         List<Message> messages = DeliverToThree(out accounts);

         var identities = new HashSet<string>();
         foreach (Message message in messages)
         {
            var identity = Identify(message.Filename);
            Assert.AreEqual(1u, identity.links, "A copy of its own: " + message.Filename);
            identities.Add(identity.identity);
         }
         Assert.AreEqual(3, identities.Count, "Three distinct files.");
      }
   }
}
