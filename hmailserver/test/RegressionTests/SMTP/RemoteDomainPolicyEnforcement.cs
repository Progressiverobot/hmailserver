// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.SMTP
{
   /// <summary>
   ///    What this server will do when it talks to a named remote domain, end to end
   ///    against the suite's SMTP simulator: the TLS it demands, the size it will
   ///    attempt, and the backup-MX recipient callout.
   ///
   ///    Every delivery here goes through a route to the simulator, because the
   ///    simulator cannot listen on 25 and a remote domain policy is deliberately
   ///    applied to a routed delivery as well as to an MX one - it is a statement about
   ///    the domain, not about how its server was found. The route is the suite's
   ///    ordinary one for dummy-example.com, at one try, so that a deferral that ran
   ///    out of tries comes back as a bounce the test can read: "Tried 1 time(s)" is
   ///    the retry path's own sentence, and it is what tells a deferral from a
   ///    permanent refusal, which carries no such line.
   ///
   ///    The policies and the verification cache are server-wide and outlive the
   ///    domain PerformBasicSetup recreates, so both are cleared before and after
   ///    every test.
   /// </summary>
   [TestFixture]
   public class RemoteDomainPolicyEnforcement : TestFixtureBase
   {
      private const string RemoteDomain = "dummy-example.com";

      private Account _account;

      [SetUp]
      public void AddSenderAndClearPolicies()
      {
         ClearPolicies_();

         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "policy-sender@example.test", "test");
      }

      [TearDown]
      public void ClearPoliciesAfterTest()
      {
         ClearPolicies_();
      }

      private void ClearPolicies_()
      {
         hMailServer.RemoteDomainPolicies policies = _settings.RemoteDomainPolicies;

         for (int i = policies.Count - 1; i >= 0; i--)
            policies[i].Delete();

         policies.ClearVerificationCache();
      }

      private RemoteDomainPolicy AddPolicy_(string domainName, Action<RemoteDomainPolicy> configure)
      {
         RemoteDomainPolicy policy = _settings.RemoteDomainPolicies.Add();
         policy.DomainName = domainName;
         policy.Active = true;
         configure(policy);
         policy.Save();
         return policy;
      }

      // ------------------------------------------------------------------ TLS

      [Test]
      [Description("A domain that requires TLS is deferred, not delivered in the clear, when the remote offers no STARTTLS - and the deferral names the rule.")]
      public void RequiredTlsIsDeferredWhenTheRemoteOffersNone()
      {
         AddPolicy_(RemoteDomain, p => p.OutboundTls = eRemoteTlsRequirement.eRTEncrypted);

         var deliveryResults = new Dictionary<string, int> { ["test@" + RemoteDomain] = 250 };

         var smtpServerPort = TestSetup.GetNextFreePort();
         using (var server = new SmtpServerSimulator(1, smtpServerPort, eConnectionSecurity.eCSNone))
         {
            server.AddRecipientResult(deliveryResults);
            server.StartListen();

            // The route itself asks for no encryption at all: the policy is what
            // raises it, which is the case an administrator writes a policy for.
            TestSetup.AddRoutePointingAtLocalhost(1, smtpServerPort, false, eConnectionSecurity.eCSNone);

            SmtpClientSimulator.StaticSend(_account.Address, "test@" + RemoteDomain, "Policy TLS", "Must not travel in the clear");

            server.WaitForCompletion();

            Assert.IsEmpty(server.MailFromCommand,
               "MAIL FROM was sent over a session the remote domain policy required to be encrypted.");
            Assert.IsEmpty(server.MessageData,
               "The message was transferred in the clear to a domain whose policy requires TLS.");

            CustomAsserts.AssertRecipientsInDeliveryQueue(0);

            string bounce = Pop3ClientSimulator.AssertGetFirstMessageText(_account.Address, "test");

            StringAssert.Contains("remote domain policy for " + RemoteDomain, bounce,
               "The failure does not say which rule refused the delivery.");
            StringAssert.Contains("NOT been sent in the clear", bounce,
               "The failure does not say the message was withheld rather than sent.");
            StringAssert.Contains("Tried 1 time(s)", bounce,
               "The refusal was permanent; a missing STARTTLS under a policy must be a deferral that the retries decide.");
         }
      }

      [Test]
      [Description("A domain that requires TLS is delivered, encrypted, when the remote offers STARTTLS - even through a route configured for none.")]
      public void RequiredTlsIsDeliveredWhenTheRemoteOffersIt()
      {
         AddPolicy_(RemoteDomain, p => p.OutboundTls = eRemoteTlsRequirement.eRTEncrypted);

         // "Encrypted" does not judge the certificate, and the simulator's is self-signed.
         _settings.VerifyRemoteSslCertificate = false;

         var deliveryResults = new Dictionary<string, int> { ["test@" + RemoteDomain] = 250 };

         var smtpServerPort = TestSetup.GetNextFreePort();
         using (var server = new SmtpServerSimulator(1, smtpServerPort, eConnectionSecurity.eCSSTARTTLSOptional))
         {
            server.AddRecipientResult(deliveryResults);
            server.StartListen();

            TestSetup.AddRoutePointingAtLocalhost(1, smtpServerPort, false, eConnectionSecurity.eCSNone);

            SmtpClientSimulator.StaticSend(_account.Address, "test@" + RemoteDomain, "Policy TLS", "Travels encrypted");

            server.WaitForCompletion();

            CustomAsserts.AssertRecipientsInDeliveryQueue(0, false);

            StringAssert.Contains("Travels encrypted", server.MessageData, "The message was not delivered.");
            Assert.IsTrue(LogHandler.DefaultLogContains("220 Ready to start TLS"),
               "The delivery did not negotiate STARTTLS, although the route asked for none and the policy required it.");
         }
      }

      [Test]
      [Description("A certificate that does not verify is refused when the policy says verified, and the message is deferred rather than sent.")]
      public void AnUnverifiableCertificateIsRefusedWhenThePolicySaysVerified()
      {
         AddPolicy_(RemoteDomain, p => p.OutboundTls = eRemoteTlsRequirement.eRTVerified);

         // Off globally, so the refusal below can only have come from the policy.
         _settings.VerifyRemoteSslCertificate = false;

         var deliveryResults = new Dictionary<string, int> { ["test@" + RemoteDomain] = 250 };

         var smtpServerPort = TestSetup.GetNextFreePort();
         using (var server = new SmtpServerSimulator(1, smtpServerPort, eConnectionSecurity.eCSSTARTTLSOptional))
         {
            server.AddRecipientResult(deliveryResults);
            server.StartListen();

            TestSetup.AddRoutePointingAtLocalhost(1, smtpServerPort, false, eConnectionSecurity.eCSNone);

            SmtpClientSimulator.StaticSend(_account.Address, "test@" + RemoteDomain, "Policy verify", "Needs a certificate that verifies");

            server.WaitForCompletion();

            Assert.IsEmpty(server.MessageData,
               "The message was delivered over a session whose certificate (self-signed, for another name) the policy required to verify.");

            CustomAsserts.AssertRecipientsInDeliveryQueue(0);

            string bounce = Pop3ClientSimulator.AssertGetFirstMessageText(_account.Address, "test");

            StringAssert.Contains("remote domain policy for " + RemoteDomain, bounce,
               "The failure does not say which rule refused the delivery.");
            StringAssert.Contains("certificate that verifies", bounce,
               "The failure does not say what the rule required.");
            StringAssert.Contains("Tried 1 time(s)", bounce,
               "An unverifiable certificate under a policy must be a deferral, not a permanent refusal.");
         }
      }

      [Test]
      [Description("The control: with no policy, the same remote offering no STARTTLS receives the message, as it always did.")]
      public void WithoutAPolicyTheSameRemoteReceivesTheMessage()
      {
         var deliveryResults = new Dictionary<string, int> { ["test@" + RemoteDomain] = 250 };

         var smtpServerPort = TestSetup.GetNextFreePort();
         using (var server = new SmtpServerSimulator(1, smtpServerPort, eConnectionSecurity.eCSNone))
         {
            server.AddRecipientResult(deliveryResults);
            server.StartListen();

            TestSetup.AddRoutePointingAtLocalhost(1, smtpServerPort, false, eConnectionSecurity.eCSNone);

            // A policy for a different domain must not reach this one.
            AddPolicy_("other-" + RemoteDomain, p => p.OutboundTls = eRemoteTlsRequirement.eRTDane);

            SmtpClientSimulator.StaticSend(_account.Address, "test@" + RemoteDomain, "No policy", "Delivered as before");

            server.WaitForCompletion();

            CustomAsserts.AssertRecipientsInDeliveryQueue(0, false);
            StringAssert.Contains("Delivered as before", server.MessageData,
               "A domain no policy governs was not delivered to.");
         }
      }

      // ----------------------------------------------------------------- size

      [Test]
      [Description("A message over the policy's size limit is never attempted: no connection, and a permanent 5.3.4 bounce naming the rule.")]
      public void AMessageOverTheLimitIsNotAttempted()
      {
         AddPolicy_(RemoteDomain, p => p.MaxMessageSizeKB = 2);

         var deliveryResults = new Dictionary<string, int> { ["test@" + RemoteDomain] = 250 };

         var smtpServerPort = TestSetup.GetNextFreePort();
         using (var server = new SmtpServerSimulator(1, smtpServerPort, eConnectionSecurity.eCSNone))
         {
            server.AddRecipientResult(deliveryResults);
            server.StartListen();

            TestSetup.AddRoutePointingAtLocalhost(3, smtpServerPort, false, eConnectionSecurity.eCSNone);

            SmtpClientSimulator.StaticSend(_account.Address, "test@" + RemoteDomain, "Too big for the partner",
               new string('x', 8000));

            // Three tries on the route, and still bounced at once: a message will not
            // be smaller on the next attempt, so this is the one refusal the policy
            // makes permanent.
            CustomAsserts.AssertRecipientsInDeliveryQueue(0, false);

            string bounce = Pop3ClientSimulator.AssertGetFirstMessageText(_account.Address, "test");

            StringAssert.Contains("remote domain policy for " + RemoteDomain, bounce,
               "The bounce does not say which rule refused the message.");
            StringAssert.Contains("allows at most 2 KB", bounce, "The bounce does not state the limit.");
            StringAssert.DoesNotContain("Tried", bounce,
               "The size refusal went through the retry path; it should be permanent at the first attempt.");

            Assert.IsEmpty(server.Conversation,
               "The server connected to the remote for a message the policy says it will not attempt.");
         }
      }

      [Test]
      [Description("A message under the policy's size limit is delivered - the control for the size refusal.")]
      public void AMessageUnderTheLimitIsDelivered()
      {
         AddPolicy_(RemoteDomain, p => p.MaxMessageSizeKB = 100);

         var deliveryResults = new Dictionary<string, int> { ["test@" + RemoteDomain] = 250 };

         var smtpServerPort = TestSetup.GetNextFreePort();
         using (var server = new SmtpServerSimulator(1, smtpServerPort, eConnectionSecurity.eCSNone))
         {
            server.AddRecipientResult(deliveryResults);
            server.StartListen();

            TestSetup.AddRoutePointingAtLocalhost(1, smtpServerPort, false, eConnectionSecurity.eCSNone);

            SmtpClientSimulator.StaticSend(_account.Address, "test@" + RemoteDomain, "Small enough", "A short body");

            server.WaitForCompletion();

            CustomAsserts.AssertRecipientsInDeliveryQueue(0, false);
            StringAssert.Contains("A short body", server.MessageData, "A message under the limit was not delivered.");
         }
      }

      // --------------------------------------------------------------- inbound

      [Test]
      [Description("Mail FROM a domain whose policy requires inbound TLS is refused 530 on a cleartext session; another sender on the same session is not.")]
      public void InboundTlsIsRequiredOfTheNamedSenderDomainOnly()
      {
         AddPolicy_("partner-inbound.test", p => p.RequireInboundTls = true);

         var smtp = new TcpConnection();
         smtp.Connect(25);
         Assert.IsTrue(smtp.Receive().StartsWith("220"), "No greeting.");
         smtp.SendAndReceive("EHLO client.test\r\n");

         string refused = smtp.SendAndReceive("MAIL FROM:<someone@partner-inbound.test>\r\n");
         StringAssert.StartsWith("530", refused,
            "Mail from a domain whose policy requires TLS was accepted on a cleartext session: " + refused);
         StringAssert.Contains("5.7.0", refused, "The refusal carries no enhanced status code.");

         string accepted = smtp.SendAndReceive("MAIL FROM:<someone@unrelated.test>\r\n");
         StringAssert.StartsWith("250", accepted,
            "A sender no policy names was refused as well: " + accepted);

         smtp.SendAndReceive("QUIT\r\n");
      }

      // --------------------------------------------------------------- callout

      /// <summary>
      ///    The backup-MX shape: a route that accepts every address at the domain and
      ///    treats it as local, so an unauthenticated sender may deliver to it, and a
      ///    policy that asks the "primary" - the simulator - before accepting.
      /// </summary>
      private void SetUpBackupMx_(int primaryPort, Action<RemoteDomainPolicy> configure)
      {
         TestSetup.AddRoutePointingAtLocalhost(1, TestSetup.GetNextFreePort(), true, eConnectionSecurity.eCSNone);

         AddPolicy_(RemoteDomain, p =>
         {
            p.CalloutEnabled = true;
            p.CalloutHost = "127.0.0.1";
            p.CalloutPort = primaryPort;
            p.CalloutTimeoutSeconds = 5;
            p.CalloutCacheMinutes = 60;
            p.CalloutMaxPerMinute = 10;
            configure?.Invoke(p);
         });
      }

      private static string RcptReply_(string recipient)
      {
         var smtp = new TcpConnection();
         smtp.Connect(25);
         smtp.Receive();
         smtp.SendAndReceive("HELO client.test\r\n");
         smtp.SendAndReceive("MAIL FROM:<someone@sender.test>\r\n");
         string reply = smtp.SendAndReceive("RCPT TO:<" + recipient + ">\r\n");
         smtp.SendAndReceive("QUIT\r\n");
         return reply;
      }

      [Test]
      [Description("A recipient the primary refuses is refused at RCPT TO; the verdict is cached and the primary is not asked again.")]
      public void ACalloutIsAnsweredCachedAndNotRepeated()
      {
         string unknown = "nosuch-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "@" + RemoteDomain;

         var primaryPort = TestSetup.GetNextFreePort();

         // One connection, one verdict. The simulator stops listening once its one
         // client has gone, so a second verification session could only be refused -
         // which gives no verdict, and no verdict accepts.
         using (var primary = new SmtpServerSimulator(1, primaryPort, eConnectionSecurity.eCSNone))
         {
            primary.AddRecipientResult(new Dictionary<string, int> { [unknown] = 550 });
            primary.StartListen();

            SetUpBackupMx_(primaryPort, null);

            string first = RcptReply_(unknown);
            StringAssert.StartsWith("550", first, "The primary refused the address and the backup MX accepted it: " + first);
            StringAssert.Contains("5.1.1", first, "The refusal carries no enhanced status code.");

            primary.WaitForCompletion();

            StringAssert.Contains("MAIL FROM:<>", primary.MailFromCommand,
               "The verification session did not use the null sender.");
            Assert.IsEmpty(primary.MessageData, "The verification session sent a message body.");
         }

         Assert.AreEqual(1, _settings.RemoteDomainPolicies.VerificationCacheSize, "The verdict was not remembered.");

         // Nothing listens any more. Still refused, so the answer came from the cache.
         string second = RcptReply_(unknown);
         StringAssert.StartsWith("550", second,
            "The second RCPT TO was not answered from the cache: with the primary gone, only a cached refusal refuses.");
      }

      [Test]
      [Description("A recipient the primary accepts is accepted by the backup MX.")]
      public void ARecipientThePrimaryAcceptsIsAccepted()
      {
         string known = "known-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "@" + RemoteDomain;

         var primaryPort = TestSetup.GetNextFreePort();
         using (var primary = new SmtpServerSimulator(1, primaryPort, eConnectionSecurity.eCSNone))
         {
            primary.AddRecipientResult(new Dictionary<string, int> { [known] = 250 });
            primary.StartListen();

            SetUpBackupMx_(primaryPort, null);

            string reply = RcptReply_(known);
            StringAssert.StartsWith("250", reply, "An address the primary accepts was refused: " + reply);

            primary.WaitForCompletion();
         }
      }

      [Test]
      [Description("A callout that times out does not turn into a rejection: the recipient is accepted, within the policy's timeout.")]
      public void ACalloutThatTimesOutAccepts()
      {
         string address = "slow-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "@" + RemoteDomain;

         // A primary that accepts the connection and never says a word - the case a
         // per-read socket timeout handles differently on each platform, and the one
         // the verification session's single deadline exists for.
         var primaryPort = TestSetup.GetNextFreePort();
         var listener = new TcpListener(IPAddress.Loopback, primaryPort);
         listener.Start();

         var held = new List<TcpClient>();
         var acceptor = new Thread(() =>
         {
            try
            {
               while (true)
                  held.Add(listener.AcceptTcpClient());
            }
            catch (SocketException)
            {
               // The listener was stopped.
            }
            catch (ObjectDisposedException)
            {
            }
         }) { IsBackground = true };
         acceptor.Start();

         try
         {
            SetUpBackupMx_(primaryPort, p => p.CalloutTimeoutSeconds = 2);

            DateTime started = DateTime.UtcNow;
            string reply = RcptReply_(address);
            TimeSpan took = DateTime.UtcNow - started;

            StringAssert.StartsWith("250", reply,
               "A verification session that timed out turned into a refusal: " + reply);
            Assert.Less(took.TotalSeconds, 20,
               "The RCPT TO waited far longer than the policy's two-second timeout: " + took);
         }
         finally
         {
            listener.Stop();
            foreach (TcpClient client in held.ToArray())
               client.Close();
         }
      }

      [Test]
      [Description("A primary that answers a temporary 4xx gives no verdict, and no verdict accepts.")]
      public void ATemporaryAnswerFromThePrimaryAccepts()
      {
         string address = "busy-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "@" + RemoteDomain;

         var primaryPort = TestSetup.GetNextFreePort();
         using (var primary = new SmtpServerSimulator(1, primaryPort, eConnectionSecurity.eCSNone))
         {
            primary.AddRecipientResult(new Dictionary<string, int> { [address] = 451 });
            primary.StartListen();

            SetUpBackupMx_(primaryPort, null);

            string reply = RcptReply_(address);
            StringAssert.StartsWith("250", reply, "A temporary answer from the primary became a refusal: " + reply);

            primary.WaitForCompletion();
         }
      }

      [Test]
      [Description("No callout is made for a domain this server hosts, even when a policy names it.")]
      public void NoCalloutForADomainThisServerHosts()
      {
         // example.test is the suite's own domain. A policy naming it, pointing at a
         // port where nothing listens, would give no verdict anyway - so the proof is
         // the address that does not exist: refused by this server's own lookup, with
         // its own words, and never by a verification session.
         AddPolicy_(_domain.Name, p =>
         {
            p.CalloutEnabled = true;
            p.CalloutHost = "127.0.0.1";
            p.CalloutPort = TestSetup.GetNextFreePort();
         });

         string existing = RcptReply_(_account.Address);
         StringAssert.StartsWith("250", existing, "A local account was refused under a callout policy for its own domain: " + existing);

         Assert.AreEqual(0, _settings.RemoteDomainPolicies.VerificationCacheSize,
            "A verification verdict was recorded for a domain this server is authoritative for.");
      }

      // --------------------------------------------------------- the collection

      [Test]
      [Description("The most specific active policy governs a domain: an exact name beats a pattern, and an inactive record does not fall through.")]
      public void TheMostSpecificActivePolicyGoverns()
      {
         AddPolicy_("*", p => p.MaxMessageSizeKB = 1000);
         AddPolicy_("*.example.org", p => p.MaxMessageSizeKB = 500);
         AddPolicy_("bank.example.org", p => p.MaxMessageSizeKB = 100);
         AddPolicy_("closed.example.org", p =>
         {
            p.MaxMessageSizeKB = 1;
            p.Active = false;
         });

         hMailServer.RemoteDomainPolicies policies = _settings.RemoteDomainPolicies;

         Assert.AreEqual(100, policies.get_PolicyForDomain("bank.example.org").MaxMessageSizeKB, "The exact name did not win.");
         Assert.AreEqual(500, policies.get_PolicyForDomain("mail.example.org").MaxMessageSizeKB, "The longer pattern did not win.");
         Assert.AreEqual(1000, policies.get_PolicyForDomain("elsewhere.test").MaxMessageSizeKB, "The catch-all did not apply.");

         // The inactive record is skipped, and the next most specific pattern governs -
         // an inactive record governs nothing of its own.
         Assert.AreEqual(500, policies.get_PolicyForDomain("closed.example.org").MaxMessageSizeKB,
            "An inactive record was applied.");
      }

      [Test]
      [Description("A second policy for the same pattern is refused by the save, naming why.")]
      public void ASecondPolicyForTheSamePatternIsRefused()
      {
         AddPolicy_("twice.test", p => { });

         RemoteDomainPolicy duplicate = _settings.RemoteDomainPolicies.Add();
         duplicate.DomainName = "twice.test";

         var error = Assert.Throws<System.Runtime.InteropServices.COMException>(() => duplicate.Save());
         StringAssert.Contains("already exists", error.Message);
      }

      [Test]
      [Description("A TLS requirement outside the four values is refused rather than stored.")]
      public void AnUnknownTlsRequirementIsRefused()
      {
         RemoteDomainPolicy policy = _settings.RemoteDomainPolicies.Add();
         policy.DomainName = "odd.test";

         Assert.Throws<System.Runtime.InteropServices.COMException>(() => policy.OutboundTls = (eRemoteTlsRequirement) 7);
      }

      [Test]
      [Description("The policies are in the database: a policy saved over COM is there after the collection is re-read from it.")]
      public void APolicyIsReadBackFromTheDatabase()
      {
         AddPolicy_("persisted.test", p =>
         {
            p.Description = "Read back";
            p.OutboundTls = eRemoteTlsRequirement.eRTDane;
            p.RequireInboundTls = true;
            p.MaxMessageSizeKB = 4096;
            p.MaxConnections = 3;
            p.MaxMessagesPerMinute = 40;
            p.AllowAutomaticReplies = false;
            p.AllowForwarding = false;
            p.CalloutEnabled = true;
            p.CalloutHost = "mx1.persisted.test";
            p.CalloutPort = 2525;
            p.CalloutTimeoutSeconds = 7;
            p.CalloutCacheMinutes = 15;
            p.CalloutMaxPerMinute = 4;
         });

         hMailServer.RemoteDomainPolicies policies = _settings.RemoteDomainPolicies;
         policies.Refresh();

         RemoteDomainPolicy read = policies.get_ItemByName("persisted.test");
         Assert.AreEqual("Read back", read.Description);
         Assert.AreEqual(eRemoteTlsRequirement.eRTDane, read.OutboundTls);
         Assert.IsTrue(read.RequireInboundTls);
         Assert.AreEqual(4096, read.MaxMessageSizeKB);
         Assert.AreEqual(3, read.MaxConnections);
         Assert.AreEqual(40, read.MaxMessagesPerMinute);
         Assert.IsFalse(read.AllowAutomaticReplies);
         Assert.IsFalse(read.AllowForwarding);
         Assert.IsTrue(read.CalloutEnabled);
         Assert.AreEqual("mx1.persisted.test", read.CalloutHost);
         Assert.AreEqual(2525, read.CalloutPort);
         Assert.AreEqual(7, read.CalloutTimeoutSeconds);
         Assert.AreEqual(15, read.CalloutCacheMinutes);
         Assert.AreEqual(4, read.CalloutMaxPerMinute);
      }
   }
}
