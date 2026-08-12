// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.AntiSpam.DKIM
{
   /// <summary>
   /// ARC sealing (RFC 8617) of relayed mail.
   ///
   /// The whole point of ARC is mail that is *not* ours: when we forward or relay
   /// somebody else's message, SPF and DKIM break at the next hop, and an ARC set
   /// carries the authentication results we observed across that hop. Sealing was
   /// only ever reached at the end of the DKIM signing path, which returns early
   /// unless the From: domain is hosted here with DKIM enabled - so exactly the
   /// mail ARC exists for was never sealed. These tests drive the three relay
   /// shapes hMailServer supports with a third-party author domain, and therefore
   /// find no ARC headers at all until the sealing decision is made independently
   /// of author-domain DKIM signing.
   ///
   /// The negative control (sealing disabled) and the author-domain test guard the
   /// two things the fix must not change: the ArcSealingEnabled gate, and DKIM
   /// signing itself.
   /// </summary>
   [TestFixture]
   public class ArcSealing : TestFixtureBase
   {
      [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
      private static extern bool WritePrivateProfileString(string section, string key, string value, string filePath);

      private const string ExternalSender = "alice@external-sender.example";

      private void WriteSetting(string key, string value)
      {
         // The server reads hMailServer.ini from its bin directory; write to every
         // existing candidate so the file the service actually reads is updated
         // regardless of the install/dev layout.
         string programDirectory = _application.Settings.Directories.ProgramDirectory;
         string[] candidates =
         {
            Path.Combine(programDirectory, "hMailServer.ini"),
            Path.Combine(programDirectory, "Bin", "hMailServer.ini"),
         };

         bool wroteAny = false;
         foreach (string iniPath in candidates)
         {
            if (!File.Exists(iniPath))
               continue;

            Assert.IsTrue(
               WritePrivateProfileString("Settings", key, value, iniPath),
               "Failed to write " + key + " to " + iniPath + ".");
            wroteAny = true;
         }

         Assert.IsTrue(wroteAny, "Could not locate an existing hMailServer.ini to update.");
      }

      private static string GetPrivateKeyFile()
      {
         var sslPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "..\\..\\..\\..\\SSL examples");

         var exampleKeyFile = Path.Combine(sslPath, "example.key");
         if (!File.Exists(exampleKeyFile))
            throw new Exception("Example key file could not be found.");

         return exampleKeyFile;
      }

      private void EnableDkimOnTestDomain()
      {
         _domain.DKIMPrivateKeyFile = GetPrivateKeyFile();
         _domain.DKIMSelector = "TestSelector";
         _domain.DKIMSignEnabled = true;
         _domain.Save();
      }

      private static void AssertArcSealed(string messageData, string expectedSealingDomain)
      {
         Assert.IsTrue(Regex.IsMatch(messageData, @"^ARC-Seal:", RegexOptions.IgnoreCase | RegexOptions.Multiline),
            "Expected an ARC-Seal header on the relayed message but none was found.\r\n" + messageData);
         Assert.IsTrue(Regex.IsMatch(messageData, @"^ARC-Message-Signature:", RegexOptions.IgnoreCase | RegexOptions.Multiline),
            "Expected an ARC-Message-Signature header on the relayed message but none was found.\r\n" + messageData);
         Assert.IsTrue(Regex.IsMatch(messageData, @"^ARC-Authentication-Results:", RegexOptions.IgnoreCase | RegexOptions.Multiline),
            "Expected an ARC-Authentication-Results header on the relayed message but none was found.\r\n" + messageData);

         string seal = ExtractHeader(messageData, "ARC-Seal");
         Assert.IsNotNull(seal, "Could not read the ARC-Seal value.\r\n" + messageData);

         Assert.IsTrue(seal.Contains("i=1"), "First ARC set should be instance 1. ARC-Seal: " + seal);
         Assert.IsTrue(seal.Contains("cv=none"), "First ARC set should record cv=none. ARC-Seal: " + seal);
         Assert.IsTrue(seal.ToLower().Contains("d=" + expectedSealingDomain.ToLower()),
            "ARC-Seal should be signed by the local domain handling the relay. ARC-Seal: " + seal);
         Assert.IsTrue(seal.Contains("s=TestSelector"), "ARC-Seal should name the domain's DKIM selector. ARC-Seal: " + seal);
         Assert.IsTrue(Regex.IsMatch(seal, @"b=\S"), "ARC-Seal should carry a signature. ARC-Seal: " + seal);

         string messageSignature = ExtractHeader(messageData, "ARC-Message-Signature");
         Assert.IsNotNull(messageSignature, "Could not read the ARC-Message-Signature value.\r\n" + messageData);
         Assert.IsTrue(messageSignature.ToLower().Contains("d=" + expectedSealingDomain.ToLower()),
            "ARC-Message-Signature should be signed by the same domain. ARC-Message-Signature: " + messageSignature);
         Assert.IsTrue(Regex.IsMatch(messageSignature, @"bh=\S"),
            "ARC-Message-Signature should carry a body hash. ARC-Message-Signature: " + messageSignature);
      }

      private static void AssertNotArcSealed(string messageData)
      {
         Assert.IsFalse(messageData.ToLower().Contains("arc-seal"),
            "The message should not have been ARC-sealed.\r\n" + messageData);
         Assert.IsFalse(messageData.ToLower().Contains("arc-message-signature"),
            "The message should not have been ARC-sealed.\r\n" + messageData);
      }

      /// <summary>
      /// Returns the unfolded value of a header field, or null when it is absent.
      /// </summary>
      private static string ExtractHeader(string messageData, string fieldName)
      {
         string value = null;

         foreach (string rawLine in messageData.Replace("\r\n", "\n").Split('\n'))
         {
            if (value != null)
            {
               if (rawLine.StartsWith(" ") || rawLine.StartsWith("\t"))
               {
                  value += " " + rawLine.Trim();
                  continue;
               }

               return value;
            }

            if (rawLine.Length == 0)
               break;

            if (rawLine.StartsWith(fieldName + ":", StringComparison.OrdinalIgnoreCase))
               value = rawLine.Substring(fieldName.Length + 1).Trim();
         }

         return value;
      }

      [Test]
      [Description("An alias at a local domain that expands to an external address is a relay of " +
                   "third-party mail: the author domain is not ours, so no DKIM signature is added, " +
                   "but with ArcSealingEnabled the relayed copy must carry an ARC set sealed by the " +
                   "local domain that owns the alias. Fails before the fix because ARC sealing was " +
                   "only reachable at the end of the author-domain DKIM signing path.")]
      public void WhenRelayingThirdPartyMailViaLocalAlias_RelayedMessageShouldBeArcSealed()
      {
         try
         {
            WriteSetting("ArcSealingEnabled", "1");
            _application.Reinitialize();

            EnableDkimOnTestDomain();

            SingletonProvider<TestSetup>.Instance.AddAlias(_domain, "arclist@example.test", "external@example.com");

            var port = TestSetup.GetNextFreePort();
            using (var smtpServer = new SmtpServerSimulator(1, port))
            {
               smtpServer.SecondsToWaitBeforeTerminate = 60;
               smtpServer.AddRecipientResult(new Dictionary<string, int> { { "external@example.com", 250 } });
               smtpServer.StartListen();

               Signing.AddRoutePointingAtLocalhost(5, port);

               SmtpClientSimulator.StaticSend(ExternalSender, "arclist@example.test", "ARC test", "Test body");

               CustomAsserts.AssertRecipientsInDeliveryQueue(0);
               smtpServer.WaitForCompletion();

               var messageData = smtpServer.MessageData;

               // We are not the author domain, so there must be no DKIM signature of
               // ours. That is precisely the case that used to go unsealed.
               Assert.IsFalse(messageData.ToLower().Contains("dkim-signature"),
                  "Third-party mail must not be DKIM-signed as if it were ours.\r\n" + messageData);

               AssertArcSealed(messageData, "example.test");
            }
         }
         finally
         {
            WriteSetting("ArcSealingEnabled", "0");
            _application.Reinitialize();
         }
      }

      [Test]
      [Description("Forwarding third-party mail from a local mailbox to an external address, with " +
                   "envelope-From rewriting enabled, must produce an ARC set sealed by the forwarding " +
                   "domain. Fails before the fix: the From: domain is external, so DKIM signing " +
                   "returns early and sealing was never reached.")]
      public void WhenForwardingThirdPartyMail_ForwardedMessageShouldBeArcSealed()
      {
         try
         {
            WriteSetting("ArcSealingEnabled", "1");
            _application.Reinitialize();

            EnableDkimOnTestDomain();

            var forwarder = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "arcfwd@example.test", "test");

            var port = TestSetup.GetNextFreePort();
            using (var smtpServer = new SmtpServerSimulator(1, port))
            {
               smtpServer.SecondsToWaitBeforeTerminate = 60;
               smtpServer.AddRecipientResult(new Dictionary<string, int> { { "external@example.com", 250 } });
               smtpServer.StartListen();

               Signing.AddRoutePointingAtLocalhost(5, port);

               forwarder.ForwardEnabled = true;
               forwarder.ForwardAddress = "external@example.com";
               forwarder.ForwardKeepOriginal = false;
               forwarder.Save();

               _settings.RewriteEnvelopeFromWhenForwarding = true;
               try
               {
                  SmtpClientSimulator.StaticSend(ExternalSender, forwarder.Address, "ARC test", "Test body");

                  CustomAsserts.AssertRecipientsInDeliveryQueue(0);
                  smtpServer.WaitForCompletion();

                  var messageData = smtpServer.MessageData;

                  Assert.IsFalse(messageData.ToLower().Contains("dkim-signature"),
                     "Third-party mail must not be DKIM-signed as if it were ours.\r\n" + messageData);

                  AssertArcSealed(messageData, "example.test");
               }
               finally
               {
                  _settings.RewriteEnvelopeFromWhenForwarding = false;
               }
            }
         }
         finally
         {
            WriteSetting("ArcSealingEnabled", "0");
            _application.Reinitialize();
         }
      }

      [Test]
      [Description("A plain account forward leaves nothing local in the envelope or the recipient " +
                   "list of the queued copy; the forwarding mailbox is only recoverable from the " +
                   "X-Original-Rcpt-To header written at reception. With that header enabled the " +
                   "forwarded copy must still be sealed by the forwarding domain. Fails before the " +
                   "fix for the same reason as the other relay shapes.")]
      public void WhenForwardingThirdPartyMailWithoutEnvelopeRewrite_ForwardedMessageShouldBeArcSealed()
      {
         try
         {
            WriteSetting("ArcSealingEnabled", "1");
            WriteSetting("AddXOriginalRcptTo", "1");
            _application.Reinitialize();

            EnableDkimOnTestDomain();

            var forwarder = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "arcfwd2@example.test", "test");

            var port = TestSetup.GetNextFreePort();
            using (var smtpServer = new SmtpServerSimulator(1, port))
            {
               smtpServer.SecondsToWaitBeforeTerminate = 60;
               smtpServer.AddRecipientResult(new Dictionary<string, int> { { "external@example.com", 250 } });
               smtpServer.StartListen();

               Signing.AddRoutePointingAtLocalhost(5, port);

               forwarder.ForwardEnabled = true;
               forwarder.ForwardAddress = "external@example.com";
               forwarder.ForwardKeepOriginal = false;
               forwarder.Save();

               SmtpClientSimulator.StaticSend(ExternalSender, forwarder.Address, "ARC test", "Test body");

               CustomAsserts.AssertRecipientsInDeliveryQueue(0);
               smtpServer.WaitForCompletion();

               var messageData = smtpServer.MessageData;

               AssertArcSealed(messageData, "example.test");
            }
         }
         finally
         {
            WriteSetting("AddXOriginalRcptTo", "0");
            WriteSetting("ArcSealingEnabled", "0");
            _application.Reinitialize();
         }
      }

      [Test]
      [Description("Negative control: with ArcSealingEnabled off, relayed third-party mail must not " +
                   "be sealed. Passes both before and after the fix - it exists to prove that the " +
                   "new sealing path is still gated on the setting.")]
      public void WhenArcSealingDisabled_RelayedMessageShouldNotBeSealed()
      {
         WriteSetting("ArcSealingEnabled", "0");
         _application.Reinitialize();

         EnableDkimOnTestDomain();

         SingletonProvider<TestSetup>.Instance.AddAlias(_domain, "arclist@example.test", "external@example.com");

         var port = TestSetup.GetNextFreePort();
         using (var smtpServer = new SmtpServerSimulator(1, port))
         {
            smtpServer.SecondsToWaitBeforeTerminate = 60;
            smtpServer.AddRecipientResult(new Dictionary<string, int> { { "external@example.com", 250 } });
            smtpServer.StartListen();

            Signing.AddRoutePointingAtLocalhost(5, port);

            SmtpClientSimulator.StaticSend(ExternalSender, "arclist@example.test", "ARC test", "Test body");

            CustomAsserts.AssertRecipientsInDeliveryQueue(0);
            smtpServer.WaitForCompletion();

            AssertNotArcSealed(smtpServer.MessageData);
         }
      }

      [Test]
      [Description("Author-domain mail must keep both its DKIM signature and, with sealing enabled, " +
                   "the ARC set it already got before this change. Passes both before and after the " +
                   "fix - it exists to prove the DKIM path was not disturbed.")]
      public void WhenSendingAuthorDomainMail_MessageShouldStillBeDkimSignedAndSealed()
      {
         try
         {
            WriteSetting("ArcSealingEnabled", "1");
            _application.Reinitialize();

            EnableDkimOnTestDomain();

            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sender@example.test", "test");

            var port = TestSetup.GetNextFreePort();
            using (var smtpServer = new SmtpServerSimulator(1, port))
            {
               smtpServer.SecondsToWaitBeforeTerminate = 60;
               smtpServer.AddRecipientResult(new Dictionary<string, int> { { "external@example.com", 250 } });
               smtpServer.StartListen();

               Signing.AddRoutePointingAtLocalhost(5, port);

               SmtpClientSimulator.StaticSend("sender@example.test", "external@example.com", "ARC test", "Test body");

               CustomAsserts.AssertRecipientsInDeliveryQueue(0);
               smtpServer.WaitForCompletion();

               var messageData = smtpServer.MessageData;

               Assert.IsTrue(messageData.ToLower().Contains("dkim-signature"),
                  "Author-domain mail must still be DKIM-signed.\r\n" + messageData);
               Assert.IsTrue(messageData.ToLower().Contains("d=example.test"),
                  "Expected the DKIM signature of the author domain.\r\n" + messageData);

               AssertArcSealed(messageData, "example.test");
            }
         }
         finally
         {
            WriteSetting("ArcSealingEnabled", "0");
            _application.Reinitialize();
         }
      }
   }
}
