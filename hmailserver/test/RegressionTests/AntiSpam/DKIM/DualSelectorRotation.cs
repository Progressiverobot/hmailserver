// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.AntiSpam.DKIM
{
   /// <summary>
   ///    DKIM dual-selector rotation: a domain can stage a second selector/key pair
   ///    (DKIMSecondarySelector / DKIMSecondaryPrivateKeyFile) beside the primary,
   ///    publish the new DNS record while the primary continues to sign, and cut
   ///    over with DKIMPromoteSecondary once the record resolves.
   ///
   ///    Two invariants carry all the value here, and both fail silently if broken:
   ///
   ///    1. The secondary must NEVER sign. It exists precisely because its DNS
   ///       record may not resolve yet; a signature from it fails verification at
   ///       every receiver until propagation completes.
   ///
   ///    2. A promote must never leave the domain unable to sign. Promoting an
   ///       empty or half-configured pair would blank the primary, and the only
   ///       symptom would be an error-log line while every outgoing message left
   ///       unsigned - the exact outage the staged rotation exists to prevent.
   ///       So the negative controls assert not just that a bad promote errors,
   ///       but that the primary is byte-for-byte intact afterwards.
   ///
   ///    Promote is deliberately in-memory until Save, like every other mutator on
   ///    the Domain interface, and that contract is pinned here too: a promote that
   ///    is not saved must be discarded on re-read, and one that is saved must
   ///    survive one.
   /// </summary>
   [TestFixture]
   public class DualSelectorRotation : TestFixtureBase
   {
      private static string GetPrivateKeyFile()
      {
         var sslPath = Paths.Combine(TestContext.CurrentContext.TestDirectory, "..\\..\\..\\..\\SSL examples");

         var exampleKeyFile = Paths.Combine(sslPath, "example.key");
         if (!File.Exists(exampleKeyFile))
            throw new Exception("Example key file could not be found.");

         return exampleKeyFile;
      }

      /// <summary>
      ///    Fetches the domain again through a collection freshly loaded from
      ///    hm_domains. Application.Domains constructs a new collection and reads it
      ///    straight from the database on every access, so this observes exactly what
      ///    was persisted - unsaved in-memory changes on another COM object are not
      ///    visible through it.
      /// </summary>
      private Domain ReReadDomainFromDatabase(string name)
      {
         return _application.Domains.get_ItemByName(name);
      }

      [Test]
      [Description("If the staged selector/key pair does not survive a Save and a re-read from the database, " +
                   "the operator publishes DNS for a key the server has silently forgotten, and the eventual " +
                   "promote either fails or - worse - promotes stale values.")]
      public void TheStagedPairSurvivesASaveAndAReReadFromTheDatabase()
      {
         var keyFile = GetPrivateKeyFile();

         _domain.DKIMSelector = "PrimarySelector";
         _domain.DKIMPrivateKeyFile = keyFile;
         _domain.DKIMSecondarySelector = "StagedSelector";
         _domain.DKIMSecondaryPrivateKeyFile = keyFile;
         _domain.Save();

         var reRead = ReReadDomainFromDatabase("example.test");

         Assert.AreEqual("StagedSelector", reRead.DKIMSecondarySelector,
            "The staged selector did not survive the round trip to the database.");
         Assert.AreEqual(keyFile, reRead.DKIMSecondaryPrivateKeyFile,
            "The staged private key file did not survive the round trip to the database.");

         // The primary pair must be untouched by merely staging a rotation.
         Assert.AreEqual("PrimarySelector", reRead.DKIMSelector);
         Assert.AreEqual(keyFile, reRead.DKIMPrivateKeyFile);
      }

      [Test]
      [Description("A domain that never staged a rotation must read back empty secondary values. If a default " +
                   "leaks in here, every pre-existing domain suddenly has a phantom rotation staged, and a " +
                   "promote on it would install configuration nobody wrote.")]
      public void ADomainThatNeverStagedARotationReadsBackEmptySecondaryValues()
      {
         var domain = SingletonProvider<TestSetup>.Instance.AddDomain("never-staged.test");

         try
         {
            var reRead = ReReadDomainFromDatabase("never-staged.test");

            Assert.AreEqual("", reRead.DKIMSecondarySelector,
               "A domain that never staged a rotation reported a secondary selector.");
            Assert.AreEqual("", reRead.DKIMSecondaryPrivateKeyFile,
               "A domain that never staged a rotation reported a secondary private key file.");
         }
         finally
         {
            _application.Domains.DeleteByDBID(domain.ID);
         }
      }

      [Test]
      [Description("Promoting with nothing staged must fail. If this regresses, the promote overwrites the " +
                   "primary selector and key with empty strings and the domain silently stops signing - " +
                   "the outage the staged rotation exists to prevent.")]
      public void PromoteWithNothingStagedFailsAndLeavesThePrimaryUntouched()
      {
         var keyFile = GetPrivateKeyFile();

         _domain.DKIMSelector = "PrimarySelector";
         _domain.DKIMPrivateKeyFile = keyFile;
         _domain.DKIMSignEnabled = true;
         _domain.Save();

         var ex = Assert.Throws<COMException>(() => _domain.DKIMPromoteSecondary());
         StringAssert.Contains("must both be configured", ex.Message);

         // The refusal must have left the in-memory object alone...
         Assert.AreEqual("PrimarySelector", _domain.DKIMSelector,
            "The failed promote blanked or changed the primary selector in memory.");
         Assert.AreEqual(keyFile, _domain.DKIMPrivateKeyFile,
            "The failed promote blanked or changed the primary key file in memory.");

         // ...and the database alone.
         var reRead = ReReadDomainFromDatabase("example.test");
         Assert.AreEqual("PrimarySelector", reRead.DKIMSelector);
         Assert.AreEqual(keyFile, reRead.DKIMPrivateKeyFile);
      }

      [Test]
      [Description("Promoting with only the selector staged (no key file) must fail and leave the primary " +
                   "intact. A promote that installed a selector with no key would stop the domain signing " +
                   "with only an error-log line to show for it.")]
      public void PromoteWithOnlyTheSelectorStagedFails()
      {
         var keyFile = GetPrivateKeyFile();

         _domain.DKIMSelector = "PrimarySelector";
         _domain.DKIMPrivateKeyFile = keyFile;
         _domain.DKIMSecondarySelector = "StagedSelector";
         _domain.Save();

         var ex = Assert.Throws<COMException>(() => _domain.DKIMPromoteSecondary());
         StringAssert.Contains("must both be configured", ex.Message);

         Assert.AreEqual("PrimarySelector", _domain.DKIMSelector,
            "The failed promote changed the primary selector.");
         Assert.AreEqual(keyFile, _domain.DKIMPrivateKeyFile,
            "The failed promote changed the primary key file.");

         var reRead = ReReadDomainFromDatabase("example.test");
         Assert.AreEqual("PrimarySelector", reRead.DKIMSelector);
         Assert.AreEqual(keyFile, reRead.DKIMPrivateKeyFile);
      }

      [Test]
      [Description("Promoting with only the key file staged (no selector) must fail and leave the primary " +
                   "intact. A key with no selector cannot be referenced from DNS, so promoting it would " +
                   "leave every signature unverifiable.")]
      public void PromoteWithOnlyTheKeyFileStagedFails()
      {
         var keyFile = GetPrivateKeyFile();

         _domain.DKIMSelector = "PrimarySelector";
         _domain.DKIMPrivateKeyFile = keyFile;
         _domain.DKIMSecondaryPrivateKeyFile = keyFile;
         _domain.Save();

         var ex = Assert.Throws<COMException>(() => _domain.DKIMPromoteSecondary());
         StringAssert.Contains("must both be configured", ex.Message);

         Assert.AreEqual("PrimarySelector", _domain.DKIMSelector,
            "The failed promote changed the primary selector.");
         Assert.AreEqual(keyFile, _domain.DKIMPrivateKeyFile,
            "The failed promote changed the primary key file.");

         var reRead = ReReadDomainFromDatabase("example.test");
         Assert.AreEqual("PrimarySelector", reRead.DKIMSelector);
         Assert.AreEqual(keyFile, reRead.DKIMPrivateKeyFile);
      }

      [Test]
      [Description("Promoting a staged pair whose key file does not exist on disk must fail and leave BOTH " +
                   "slots untouched. The promote erases the old key from the configuration, so a promote " +
                   "onto a missing file would leave the domain with no usable key at all; and clearing the " +
                   "staged slot on failure would throw away the values the operator needs to correct.")]
      public void PromoteOntoAMissingKeyFileFailsAndLeavesBothSlotsUntouched()
      {
         var keyFile = GetPrivateKeyFile();
         var missingKeyFile = Paths.Combine(Path.GetTempPath(),
            "hmailserver-no-such-dkim-key-" + TestSetup.UniqueString() + ".key");

         _domain.DKIMSelector = "PrimarySelector";
         _domain.DKIMPrivateKeyFile = keyFile;
         _domain.DKIMSecondarySelector = "StagedSelector";
         _domain.DKIMSecondaryPrivateKeyFile = missingKeyFile;
         _domain.Save();

         var ex = Assert.Throws<COMException>(() => _domain.DKIMPromoteSecondary());
         StringAssert.Contains("does not exist", ex.Message);

         // The primary must still be intact - a promote that blanked it would
         // silently stop the domain signing.
         Assert.AreEqual("PrimarySelector", _domain.DKIMSelector,
            "The failed promote changed the primary selector.");
         Assert.AreEqual(keyFile, _domain.DKIMPrivateKeyFile,
            "The failed promote changed the primary key file.");

         // And the staged slot must still hold what was staged, so the operator can
         // see and correct the bad path rather than reconstruct it.
         Assert.AreEqual("StagedSelector", _domain.DKIMSecondarySelector,
            "The failed promote cleared the staged selector.");
         Assert.AreEqual(missingKeyFile, _domain.DKIMSecondaryPrivateKeyFile,
            "The failed promote cleared the staged key file.");

         var reRead = ReReadDomainFromDatabase("example.test");
         Assert.AreEqual("PrimarySelector", reRead.DKIMSelector);
         Assert.AreEqual(keyFile, reRead.DKIMPrivateKeyFile);
         Assert.AreEqual("StagedSelector", reRead.DKIMSecondarySelector);
         Assert.AreEqual(missingKeyFile, reRead.DKIMSecondaryPrivateKeyFile);
      }

      [Test]
      [Description("The happy path: promote makes the staged pair primary and clears the staged slot, in " +
                   "memory only. If the in-memory contract regresses to persisting immediately, a promote " +
                   "the operator thinks better of is half-committed; if the swap or the clear regresses, a " +
                   "second promote resurrects a retired key whose DNS record is about to disappear.")]
      public void PromoteSwapsThePairInMemoryAndPersistsOnlyOnSave()
      {
         var oldKeyFile = GetPrivateKeyFile();
         var stagedKeyFile = Paths.Combine(Path.GetTempPath(),
            "hmailserver-staged-dkim-" + TestSetup.UniqueString() + ".key");

         File.Copy(oldKeyFile, stagedKeyFile, true);

         try
         {
            _domain.DKIMSelector = "OldSelector";
            _domain.DKIMPrivateKeyFile = oldKeyFile;
            _domain.DKIMSecondarySelector = "NewSelector";
            _domain.DKIMSecondaryPrivateKeyFile = stagedKeyFile;
            _domain.Save();

            _domain.DKIMPromoteSecondary();

            // In memory: the staged pair is now primary, and the staged slot is
            // empty - not swapped, because rotation retires the old key.
            Assert.AreEqual("NewSelector", _domain.DKIMSelector,
               "The promote did not make the staged selector primary.");
            Assert.AreEqual(stagedKeyFile, _domain.DKIMPrivateKeyFile,
               "The promote did not make the staged key file primary.");
            Assert.AreEqual("", _domain.DKIMSecondarySelector,
               "The promote left a value in the staged selector slot.");
            Assert.AreEqual("", _domain.DKIMSecondaryPrivateKeyFile,
               "The promote left a value in the staged key file slot.");

            // Not yet saved: the database must still hold the pre-promote state, so
            // a promote the caller abandons is discarded like any other unsaved edit.
            var reReadBeforeSave = ReReadDomainFromDatabase("example.test");
            Assert.AreEqual("OldSelector", reReadBeforeSave.DKIMSelector,
               "An unsaved promote reached the database.");
            Assert.AreEqual(oldKeyFile, reReadBeforeSave.DKIMPrivateKeyFile,
               "An unsaved promote reached the database.");
            Assert.AreEqual("NewSelector", reReadBeforeSave.DKIMSecondarySelector);
            Assert.AreEqual(stagedKeyFile, reReadBeforeSave.DKIMSecondaryPrivateKeyFile);

            // And after Save, the cutover survives a re-read.
            _domain.Save();

            var reReadAfterSave = ReReadDomainFromDatabase("example.test");
            Assert.AreEqual("NewSelector", reReadAfterSave.DKIMSelector,
               "The saved promote did not survive a re-read.");
            Assert.AreEqual(stagedKeyFile, reReadAfterSave.DKIMPrivateKeyFile,
               "The saved promote did not survive a re-read.");
            Assert.AreEqual("", reReadAfterSave.DKIMSecondarySelector,
               "The cleared staged selector did not survive a re-read.");
            Assert.AreEqual("", reReadAfterSave.DKIMSecondaryPrivateKeyFile,
               "The cleared staged key file did not survive a re-read.");
         }
         finally
         {
            // The domain is recreated by the next test's setup, so pointing its key
            // file at a deleted temp file is harmless - but the file must not leak.
            File.Delete(stagedKeyFile);
         }
      }

      [Test]
      [Description("With a secondary staged, a delivered message carries exactly ONE DKIM-Signature and its " +
                   "s= names the PRIMARY selector. The secondary is by definition waiting for DNS " +
                   "propagation, so a signature from it fails verification at every receiver - and " +
                   "real-world filters score broken signatures negatively, so a second signature can only " +
                   "hurt deliverability, never help it.")]
      public void TheSecondaryNeverSigns_ADeliveredMessageCarriesExactlyOnePrimarySignature()
      {
         var keyFile = GetPrivateKeyFile();

         _domain.DKIMSelector = "PrimarySelector";
         _domain.DKIMPrivateKeyFile = keyFile;
         _domain.DKIMSecondarySelector = "StagedSelector";
         _domain.DKIMSecondaryPrivateKeyFile = keyFile;
         _domain.DKIMSignEnabled = true;
         _domain.Save();

         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         var deliveryResults = new Dictionary<string, int>();
         deliveryResults["test@example.com"] = 250;

         var port = TestSetup.GetNextFreePort();
         using (var server = new SmtpServerSimulator(1, port))
         {
            server.SecondsToWaitBeforeTerminate = 60;
            server.AddRecipientResult(deliveryResults);
            server.StartListen();

            // Route example.com at the local simulator, so no assertion here can be
            // broken by a real-world DNS change.
            Signing.AddRoutePointingAtLocalhost(5, port);

            var smtp = new SmtpClientSimulator();
            smtp.Send("test@example.test", new List<string> {"test@example.com"}, "Test", "Test message");

            server.WaitForCompletion();
            var messageData = server.MessageData;

            CustomAsserts.AssertRecipientsInDeliveryQueue(0);

            int signatureCount = Regex.Matches(messageData, "DKIM-Signature", RegexOptions.IgnoreCase).Count;
            Assert.AreEqual(1, signatureCount,
               $"Expected exactly 1 DKIM-Signature with a rotation staged, but found {signatureCount}.\r\n" +
               messageData);

            Assert.IsTrue(messageData.Contains("s=PrimarySelector"),
               "The signature does not name the primary selector in s=.\r\n" + messageData);

            Assert.IsFalse(messageData.Contains("StagedSelector"),
               "The staged selector appears in the delivered message - the secondary must never sign.\r\n" +
               messageData);
         }
      }
   }
}
