// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.SMTP
{
   /// <summary>
   ///    Outbound SIZE (RFC 1870) - the client half. When the receiving server's
   ///    EHLO advertises SIZE, MAIL FROM declares this message's size; and when
   ///    the advertised limit is smaller than the message, the transfer is
   ///    refused locally BEFORE any bytes move - one round trip instead of a
   ///    whole upload that some servers only reject after DATA. A server that
   ///    does not advertise SIZE gets the bare MAIL FROM it always got.
   /// </summary>
   [TestFixture]
   public class OutboundSize : TestFixtureBase
   {
      private Account _account;

      [SetUp]
      public new void SetUp()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "size-sender@example.test", "test");
      }

      [Test]
      [Description("A remote advertising SIZE gets the message's size declared on MAIL FROM.")]
      public void AnAdvertisedSizeIsDeclaredOnMailFrom()
      {
         var deliveryResults = new Dictionary<string, int> { ["test@dummy-example.com"] = 250 };

         var smtpServerPort = TestSetup.GetNextFreePort();
         using (var server = new SmtpServerSimulator(1, smtpServerPort))
         {
            server.EhloSizeAdvertisement = 10485760;
            server.AddRecipientResult(deliveryResults);
            server.StartListen();

            TestSetup.AddRoutePointingAtLocalhost(1, smtpServerPort, false);

            SmtpClientSimulator.StaticSend(_account.Address, "test@dummy-example.com", "Size declared", "Body");

            server.WaitForCompletion();

            Match declared = Regex.Match(server.MailFromCommand, @" SIZE=(\d+)\s*$");
            Assert.IsTrue(declared.Success,
               "MAIL FROM carried no SIZE parameter although the remote advertised SIZE: " + server.MailFromCommand);

            long declaredSize = long.Parse(declared.Groups[1].Value);
            Assert.Greater(declaredSize, 0, "The declared size is not a positive byte count.");
            Assert.Less(declaredSize, 65536, "The declared size is implausibly large for this tiny test message.");

            Assert.IsTrue(server.MessageData.Contains("Size declared"),
               "The message did not deliver after SIZE was declared.");
         }
      }

      /// <summary>
      ///    The point of the feature: an oversized message never transfers. The
      ///    remote's limit is 500 bytes, the message is several thousand; the
      ///    client must stop at EHLO - no MAIL FROM, no DATA - and bounce with an
      ///    explanation naming both numbers' origin.
      /// </summary>
      [Test]
      [Description("A message above the remote's advertised limit is refused locally, before transfer.")]
      public void AMessageAboveTheAdvertisedLimitIsNotTransferred()
      {
         var deliveryResults = new Dictionary<string, int> { ["test@dummy-example.com"] = 250 };

         var smtpServerPort = TestSetup.GetNextFreePort();
         using (var server = new SmtpServerSimulator(1, smtpServerPort))
         {
            server.EhloSizeAdvertisement = 500;
            server.AddRecipientResult(deliveryResults);
            server.StartListen();

            TestSetup.AddRoutePointingAtLocalhost(1, smtpServerPort, false);

            SmtpClientSimulator.StaticSend(_account.Address, "test@dummy-example.com", "Too big",
               new string('x', 5000));

            server.WaitForCompletion();

            Assert.IsEmpty(server.MailFromCommand,
               "MAIL FROM was sent although the message exceeds the remote's advertised SIZE limit.");
            Assert.IsEmpty(server.MessageData,
               "Message bytes were transferred to a server whose advertised limit the message exceeds.");

            CustomAsserts.AssertRecipientsInDeliveryQueue(0);

            string bounceMessage = Pop3ClientSimulator.AssertGetFirstMessageText(_account.Address, "test");
            Assert.IsTrue(bounceMessage.Contains("EHLO SIZE limit"),
               "The bounce does not explain that the remote's advertised SIZE limit was the reason: " + bounceMessage);
         }
      }

      /// <summary>
      ///    The control: no SIZE in EHLO, no SIZE on MAIL FROM. An implementation
      ///    that declared the size unconditionally would pass both tests above
      ///    and fail here - against servers that reject unknown MAIL parameters
      ///    (RFC 5321 makes them optional to support).
      /// </summary>
      [Test]
      [Description("A remote not advertising SIZE gets the bare MAIL FROM - the control.")]
      public void AnUnadvertisedSizeKeepsMailFromBare()
      {
         var deliveryResults = new Dictionary<string, int> { ["test@dummy-example.com"] = 250 };

         var smtpServerPort = TestSetup.GetNextFreePort();
         using (var server = new SmtpServerSimulator(1, smtpServerPort))
         {
            server.AddRecipientResult(deliveryResults);
            server.StartListen();

            TestSetup.AddRoutePointingAtLocalhost(1, smtpServerPort, false);

            SmtpClientSimulator.StaticSend(_account.Address, "test@dummy-example.com", "No size", "Body");

            server.WaitForCompletion();

            Assert.IsNotEmpty(server.MailFromCommand, "The message was never attempted.");
            Assert.IsFalse(server.MailFromCommand.Contains("SIZE"),
               "MAIL FROM carries a SIZE parameter although the remote never advertised SIZE: " + server.MailFromCommand);

            Assert.IsTrue(server.MessageData.Contains("No size"),
               "The ordinary delivery stopped working.");
         }
      }
   }
}
