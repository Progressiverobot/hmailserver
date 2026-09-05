// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Threading;
using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    Per-domain scope for the message archive.
   ///
   ///    ArchiveDir keeps a copy of every message that passes through the server.
   ///    An archive is usually kept for a legal or contractual reason, and that
   ///    reason usually applies to particular domains rather than to every domain
   ///    a server happens to host - so ArchiveDomains names the ones it applies to,
   ///    and a message for anyone else is not archived at all. The two cases worth
   ///    pinning: a message that touches no listed domain leaves nothing behind, and
   ///    a message from an unlisted local sender to a listed recipient still gives
   ///    the recipient their copy, made from the message itself because there is no
   ///    Sent copy to link it to.
   /// </summary>
   [TestFixture]
   public class ArchiveScopeTests : TestFixtureBase
   {
      private string archiveRoot_;

      // Named differently from the base class's SetUp so that nothing is hidden:
      // PerformBasicSetup there is what creates _domain.
      [SetUp]
      public void CreateTheArchiveRoot()
      {
         archiveRoot_ = Paths.Combine(Path.GetTempPath(), "hmail-archive-scope-" + Guid.NewGuid().ToString("N"));
         Directory.CreateDirectory(archiveRoot_);
      }

      [TearDown]
      public void RemoveTheArchive()
      {
         ServerIniFile.SetSetting("ArchiveDir", null);
         ServerIniFile.SetSetting("ArchiveDomains", null);
         RestartServerAndReacquireCom();

         try
         {
            if (archiveRoot_ != null && Directory.Exists(archiveRoot_))
               Directory.Delete(archiveRoot_, true);
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            // A leftover temp directory is not worth failing a test over.
         }
      }

      private static int MessageFilesUnder(string directory)
      {
         return Directory.Exists(directory) ? Directory.GetFiles(directory, "*.eml", SearchOption.AllDirectories).Length : 0;
      }

      // The archive copy is written at the end of DATA, before delivery - so by the
      // time POP3 can see the message the copy exists. Polled anyway: a copy is a
      // separate file operation and a fixed assumption about ordering is how a test
      // becomes intermittent.
      private static void WaitForMessageFiles(string directory, int expected)
      {
         for (int attempt = 0; attempt < 40 && MessageFilesUnder(directory) < expected; attempt++)
            Thread.Sleep(250);
      }

      [Test]
      [Description("With ArchiveDomains set, a message for a listed domain is archived and a message that touches no listed domain leaves nothing behind - matched without regard to case or spacing")]
      public void OnlyListedDomainsAreArchived()
      {
         // Everything over COM happens before the restart; after it only SMTP and
         // POP3 are used, so no proxy is touched once its process has gone.
         string listed = _domain.Name.ToLowerInvariant();
         Domain outside = SingletonProvider<TestSetup>.Instance.AddDomain("outside.test");
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "inside@" + listed, "test");
         SingletonProvider<TestSetup>.Instance.AddAccount(outside, "someone@outside.test", "test");

         ServerIniFile.SetSetting("ArchiveDir", archiveRoot_);
         ServerIniFile.SetSetting("ArchiveDomains", " " + listed.ToUpperInvariant() + " , nobody.example ");
         RestartServerAndReacquireCom();

         SmtpClientSimulator.StaticSend("remote@elsewhere.test", "inside@" + listed, "In scope", "For a listed domain.");
         Pop3ClientSimulator.AssertMessageCount("inside@" + listed, "test", 1);

         SmtpClientSimulator.StaticSend("remote@elsewhere.test", "someone@outside.test", "Out of scope", "For a domain that is not listed.");
         Pop3ClientSimulator.AssertMessageCount("someone@outside.test", "test", 1);

         string insideFolder = Paths.Combine(archiveRoot_, listed, "inside");
         string inbound = Paths.Combine(archiveRoot_, "Inbound");

         WaitForMessageFiles(insideFolder, 1);

         ClassicAssert.AreEqual(1, MessageFilesUnder(insideFolder),
            "The listed recipient should have their copy under <domain>\\<mailbox>: " + insideFolder);
         ClassicAssert.AreEqual(1, MessageFilesUnder(inbound),
            "Inbound should hold the external sender's copy of the in-scope message and nothing for the other: " + inbound);
         ClassicAssert.IsFalse(Directory.Exists(Paths.Combine(archiveRoot_, "outside.test")),
            "A message for a domain that is not listed must leave nothing in the archive.");
      }

      [Test]
      [Description("A local sender outside the list gets no Sent copy, but a listed recipient still gets theirs - made from the message itself")]
      public void AListedRecipientIsArchivedEvenWhenTheLocalSenderIsNot()
      {
         string listed = _domain.Name.ToLowerInvariant();
         Domain outside = SingletonProvider<TestSetup>.Instance.AddDomain("outside.test");
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "inside@" + listed, "test");
         SingletonProvider<TestSetup>.Instance.AddAccount(outside, "someone@outside.test", "test");

         ServerIniFile.SetSetting("ArchiveDir", archiveRoot_);
         ServerIniFile.SetSetting("ArchiveDomains", listed);
         RestartServerAndReacquireCom();

         SmtpClientSimulator.StaticSend("someone@outside.test", "inside@" + listed, "Across the line", "From an unlisted local sender to a listed recipient.");
         Pop3ClientSimulator.AssertMessageCount("inside@" + listed, "test", 1);

         string insideFolder = Paths.Combine(archiveRoot_, listed, "inside");
         WaitForMessageFiles(insideFolder, 1);

         string[] copies = Directory.Exists(insideFolder) ? Directory.GetFiles(insideFolder, "*.eml") : new string[0];
         ClassicAssert.AreEqual(1, copies.Length, "The listed recipient should have exactly one copy: " + insideFolder);
         StringAssert.DoesNotStartWith("Sent-", Path.GetFileName(copies[0]), "A recipient's copy is not a Sent copy.");

         ClassicAssert.IsFalse(Directory.Exists(Paths.Combine(archiveRoot_, "outside.test")),
            "An unlisted local sender gets no Sent copy.");
         ClassicAssert.AreEqual(0, MessageFilesUnder(Paths.Combine(archiveRoot_, "Inbound")),
            "A local sender's message never goes to Inbound, listed or not.");
      }
   }
}
