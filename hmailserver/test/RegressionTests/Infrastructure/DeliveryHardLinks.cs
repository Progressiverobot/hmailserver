// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

      // The identity of a file and how many names it has, from "fsutil hardlink list":
      // one line per name the file has on its volume, so the sorted list of names is
      // the identity (two paths are the same file exactly when they list the same
      // names) and its length is the link count. Managed code and a system tool,
      // where this used to be a P/Invoke of GetFileInformationByHandle for the same
      // two facts.
      private static (string identity, uint links) Identify(string path)
      {
         string fullPath = Path.GetFullPath(path);
         var fsutil = new ProcessStartInfo("fsutil", "hardlink list \"" + fullPath + "\"")
         {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
         };

         using (var process = Process.Start(fsutil))
         {
            string output = process.StandardOutput.ReadToEnd();
            string errors = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode, "fsutil hardlink list failed for " + path + ": " + errors + output);

            var names = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
               .Select(name => name.Trim().ToLowerInvariant())
               .Where(name => name.Length > 0)
               .OrderBy(name => name, StringComparer.Ordinal)
               .ToList();
            Assert.IsTrue(names.Count > 0, "fsutil listed no names for " + path + ": " + output);

            return (Path.GetPathRoot(fullPath).ToLowerInvariant() + string.Join("|", names), (uint) names.Count);
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
