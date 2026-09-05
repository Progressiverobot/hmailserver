// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.SMTP
{
   /// <summary>
   ///    The outbound client's use of PIPELINING (RFC 2920) and CHUNKING (RFC 3030).
   ///
   ///    Until 5 September 2026 the delivery state machine was strictly one command,
   ///    one reply, and always DATA - a round trip per envelope command and one more
   ///    for the 354, on every delivery, to remotes that had advertised they did not
   ///    need them. Now: when the remote advertises PIPELINING, MAIL FROM, every RCPT
   ///    TO and the data command go out in one flight and the replies are counted
   ///    back in order; when it advertises CHUNKING, the message goes as one
   ///    BDAT ... LAST chunk with no 354 to wait for and no dot-stuffing.
   ///
   ///    Pipelining cannot be proved by looking at how the bytes arrived - TCP may
   ///    or may not put a client's three commands in one segment - so the simulator
   ///    holds its reply to MAIL FROM for a moment and records whether more commands
   ///    turned up meanwhile. A client that waits for each reply sends nothing
   ///    until it gets one.
   /// </summary>
   [TestFixture]
   public class OutboundPipelining : TestFixtureBase
   {
      private const string Sender = "sender@example.test";
      private const string Remote = "user@dummy-example.com";
      private const string SecondRemote = "second@dummy-example.com";

      [SetUp]
      public void CreateTheSender()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, Sender, "test");
      }

      private static Dictionary<string, int> Results(params KeyValuePair<string, int>[] results)
      {
         var dictionary = new Dictionary<string, int>();

         foreach (KeyValuePair<string, int> result in results)
            dictionary[result.Key] = result.Value;

         return dictionary;
      }

      private static KeyValuePair<string, int> Accepted(string address)
      {
         return new KeyValuePair<string, int>(address, 250);
      }

      private static KeyValuePair<string, int> Refused(string address)
      {
         return new KeyValuePair<string, int>(address, 550);
      }

      private static void Deliver(int port, string body, params string[] recipients)
      {
         TestSetup.AddRoutePointingAtLocalhost(5, port, false);

         var smtp = new SmtpClientSimulator();
         smtp.Send(Sender, new List<string>(recipients), "Outbound pipelining", body);
      }

      [Test]
      [Description("With PIPELINING advertised, RCPT TO and DATA are sent behind MAIL FROM without waiting for its reply, and the delivery completes")]
      public void TheEnvelopeIsPipelinedWhenTheRemoteAdvertisesIt()
      {
         int port = TestSetup.GetNextFreePort();

         using (var server = new SmtpServerSimulator(1, port))
         {
            server.AdvertisePipelining = true;
            server.HoldMailFromReplyFor = TimeSpan.FromSeconds(3);
            server.AddRecipientResult(Results(Accepted(Remote)));
            server.StartListen();

            Deliver(port, "Pipelined body.", Remote);

            server.WaitForCompletion();

            ClassicAssert.IsTrue(server.EnvelopeArrivedPipelined,
               "RCPT TO and DATA should have been in the simulator's buffer before it answered MAIL FROM. Commands: " +
               string.Join(" | ", server.CommandsReceived));
            StringAssert.Contains("Pipelined body.", server.MessageData);
            ClassicAssert.AreEqual(3, server.CommandsReceived.Count, string.Join(" | ", server.CommandsReceived));
            StringAssert.StartsWith("MAIL FROM:", server.CommandsReceived[0]);
            StringAssert.StartsWith("RCPT TO:", server.CommandsReceived[1]);
            ClassicAssert.AreEqual("DATA", server.CommandsReceived[2]);

            CustomAsserts.AssertRecipientsInDeliveryQueue(0);
         }
      }

      [Test]
      [Description("Without the advertisement the client still waits for each reply - the hold on MAIL FROM's reply sees nothing arrive")]
      public void WithoutTheAdvertisementTheClientWaitsForEachReply()
      {
         int port = TestSetup.GetNextFreePort();

         using (var server = new SmtpServerSimulator(1, port))
         {
            server.HoldMailFromReplyFor = TimeSpan.FromSeconds(2);
            server.AddRecipientResult(Results(Accepted(Remote)));
            server.StartListen();

            Deliver(port, "One at a time.", Remote);

            server.WaitForCompletion();

            ClassicAssert.IsFalse(server.EnvelopeArrivedPipelined,
               "A remote that did not advertise PIPELINING must get one command per reply. Commands: " +
               string.Join(" | ", server.CommandsReceived));
            StringAssert.Contains("One at a time.", server.MessageData);

            CustomAsserts.AssertRecipientsInDeliveryQueue(0);
         }
      }

      [Test]
      [Description("With CHUNKING advertised the message goes as one BDAT ... LAST chunk: no 354, no dot-stuffing, and the declared size is the chunk's size")]
      public void TheMessageGoesAsOneBdatChunkWhenTheRemoteAdvertisesChunking()
      {
         int port = TestSetup.GetNextFreePort();

         using (var server = new SmtpServerSimulator(1, port))
         {
            server.AdvertisePipelining = true;
            server.AdvertiseChunking = true;
            server.AddRecipientResult(Results(Accepted(Remote)));
            server.StartListen();

            // A line that starts with a dot: DATA would have to stuff it, BDAT
            // carries it as it is. Submitted stuffed (two dots), because the test
            // client sends the body verbatim over DATA and the server unstuffs it -
            // so this is what puts ONE dot at the start of the stored line.
            Deliver(port, "First line.\r\n..starts with a dot\r\nLast line.", Remote);

            server.WaitForCompletion();

            ClassicAssert.AreEqual(1, server.BdatCommands.Count,
               "Exactly one BDAT chunk, marked LAST. Commands: " + string.Join(" | ", server.CommandsReceived));

            Match bdat = Regex.Match(server.BdatCommands[0], @"^BDAT (\d+) LAST$");
            ClassicAssert.IsTrue(bdat.Success, "Not the BDAT <size> LAST shape: " + server.BdatCommands[0]);
            ClassicAssert.AreEqual(int.Parse(bdat.Groups[1].Value), server.MessageData.Length,
               "The declared size must be exactly the chunk's size.");

            ClassicAssert.IsFalse(server.CommandsReceived.Contains("DATA"), "DATA must not be sent when BDAT is used.");
            StringAssert.Contains("\r\n.starts with a dot\r\n", server.MessageData,
               "A BDAT chunk is not dot-stuffed.");
            StringAssert.DoesNotContain("\r\n..starts", server.MessageData);
            ClassicAssert.IsFalse(server.MessageData.EndsWith("\r\n.\r\n"),
               "A BDAT chunk carries no terminating dot.");

            CustomAsserts.AssertRecipientsInDeliveryQueue(0);
         }
      }

      [Test]
      [Description("A MAIL FROM refused inside a pipelined group bounces every recipient with the remote's reply; the RCPT and DATA replies that follow are consumed, not mistaken for anything")]
      public void AMailFromRefusalInAPipelinedGroupBouncesEveryRecipientWithTheRemoteReply()
      {
         int port = TestSetup.GetNextFreePort();

         using (var server = new SmtpServerSimulator(1, port))
         {
            server.AdvertisePipelining = true;
            server.MailFromResult = 550;
            server.AddRecipientResult(Results(Accepted(Remote)));
            server.StartListen();

            Deliver(port, "Refused at MAIL FROM.", Remote);

            server.WaitForCompletion();

            // The whole group still arrived - the client did not stop at the refusal
            // it had not yet seen - and nothing was queued for retry.
            ClassicAssert.AreEqual(3, server.CommandsReceived.Count, string.Join(" | ", server.CommandsReceived));
            ClassicAssert.AreEqual("", server.MessageData, "No body may be transmitted after a refused MAIL FROM.");

            CustomAsserts.AssertRecipientsInDeliveryQueue(0);

            string bounce = Pop3ClientSimulator.AssertGetFirstMessageText(Sender, "test");
            StringAssert.Contains("550", bounce);
            StringAssert.Contains("hMailServer sent: MAIL FROM:", bounce,
               "The bounce must quote the command the remote refused, not the last one sent.\r\n" + bounce);
         }
      }

      [Test]
      [Description("A recipient refused inside a pipelined group is bounced by name while the accepted one is delivered")]
      public void ARefusedRecipientInAPipelinedGroupIsBouncedAndTheRestDelivered()
      {
         int port = TestSetup.GetNextFreePort();

         using (var server = new SmtpServerSimulator(1, port))
         {
            server.AdvertisePipelining = true;
            server.AddRecipientResult(Results(Accepted(Remote), Refused(SecondRemote)));
            server.StartListen();

            Deliver(port, "One of two.", Remote, SecondRemote);

            server.WaitForCompletion();

            ClassicAssert.AreEqual(2, server.RcptTosReceived);
            StringAssert.Contains("One of two.", server.MessageData, "The accepted recipient's copy must be transmitted.");

            CustomAsserts.AssertRecipientsInDeliveryQueue(0);

            string bounce = Pop3ClientSimulator.AssertGetFirstMessageText(Sender, "test");
            StringAssert.Contains(SecondRemote, bounce);
            StringAssert.Contains("550", bounce);
            StringAssert.Contains("hMailServer sent: RCPT TO:<" + SecondRemote + ">", bounce,
               "The bounce must quote the RCPT TO the remote refused.\r\n" + bounce);
         }
      }

      [Test]
      [Description("OutboundPipelining=0 and OutboundChunking=0 keep the one-command-at-a-time, DATA-only client even when the remote advertises both")]
      public void BothCanBeSwitchedOffInTheIni()
      {
         ServerIniFile.SetSetting("OutboundPipelining", "0");
         ServerIniFile.SetSetting("OutboundChunking", "0");
         RestartServerAndReacquireCom();

         try
         {
            int port = TestSetup.GetNextFreePort();

            using (var server = new SmtpServerSimulator(1, port))
            {
               server.AdvertisePipelining = true;
               server.AdvertiseChunking = true;
               server.HoldMailFromReplyFor = TimeSpan.FromSeconds(2);
               server.AddRecipientResult(Results(Accepted(Remote)));
               server.StartListen();

               Deliver(port, "Switched off.", Remote);

               server.WaitForCompletion();

               ClassicAssert.IsFalse(server.EnvelopeArrivedPipelined, "OutboundPipelining=0 must mean one command per reply.");
               ClassicAssert.AreEqual(0, server.BdatCommands.Count, "OutboundChunking=0 must mean DATA, not BDAT.");
               ClassicAssert.IsTrue(server.CommandsReceived.Contains("DATA"), string.Join(" | ", server.CommandsReceived));
               StringAssert.Contains("Switched off.", server.MessageData);

               CustomAsserts.AssertRecipientsInDeliveryQueue(0);
            }
         }
         finally
         {
            ServerIniFile.SetSetting("OutboundPipelining", null);
            ServerIniFile.SetSetting("OutboundChunking", null);
            RestartServerAndReacquireCom();
         }
      }
   }
}
