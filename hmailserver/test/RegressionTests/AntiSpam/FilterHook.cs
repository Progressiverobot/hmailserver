// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.AntiSpam
{
   /// <summary>
   ///    An external filtering engine in the path of every accepted message.
   ///
   ///    There has never been a native way to do this. The custom virus scanner runs
   ///    a process per message and answers only "infected or not"; SpamAssassin has
   ///    its own daemon and protocol; an event script pays a script engine per
   ///    message and cannot return a score. Milter was ruled out in favour of HTTP,
   ///    which this server already speaks in both directions.
   ///
   ///    The verdict arrives as a score, which is what makes it compose with SPF,
   ///    DKIM, DMARC and the rest instead of being a second, parallel notion of spam.
   ///
   ///    Three things are worth pinning, and only one is "the score is applied":
   ///
   ///    * the engine is actually SENT the envelope and the message, because a hook
   ///      that posts an empty body would still pass a score assertion;
   ///    * an engine that does not answer does not stop the mail, by default, which
   ///      is the decision an administrator is most likely to be surprised by;
   ///    * and with fail-closed on, it does - because a switch that does nothing is
   ///      worse than no switch.
   /// </summary>
   [TestFixture]
   public class FilterHook : TestFixtureBase
   {
      private static void ConfigureHook(string url, string failClosed = "0", string timeout = "10")
      {
         ServerIniFile.SetSetting("FilterHookUrl", url);
         ServerIniFile.SetSetting("FilterHookFailClosed", failClosed);
         ServerIniFile.SetSetting("FilterHookTimeoutSeconds", timeout);
      }

      private static void ClearHook()
      {
         ServerIniFile.SetSetting("FilterHookUrl", null);
         ServerIniFile.SetSetting("FilterHookFailClosed", null);
         ServerIniFile.SetSetting("FilterHookTimeoutSeconds", null);
         ServerIniFile.SetSetting("FilterHookRejectScore", null);
      }

      [Test]
      [Description("The engine is sent the envelope, the connecting address and the message itself, and the score it returns is applied to the message")]
      public void TheEngineIsSentTheMessageAndItsScoreIsApplied()
      {
         string address = SingletonProvider<TestSetup>.Instance
            .AddAccount(_domain, "hooked@example.test", "test").Address;

         var antiSpam = _application.Settings.AntiSpam;
         antiSpam.SpamMarkThreshold = 5;
         antiSpam.SpamDeleteThreshold = 1000;
         antiSpam.AddHeaderReason = true;

         using (var engine = new FakeFilterEngine("{\"action\":\"add header\",\"score\":7.4,\"reason\":\"looks like spam to me\"}"))
         {
            try
            {
               ConfigureHook(engine.Url);
               RestartServerAndReacquireCom();

               SmtpClientSimulator.StaticSendRaw("outsider@unaligned-envelope.test", address,
                  "From: <outsider@unaligned-envelope.test>\r\n" +
                  "To: <" + address + ">\r\n" +
                  "Subject: hook probe\r\n" +
                  "\r\n" +
                  "A body the engine should be shown.\r\n");

               string delivered = Pop3ClientSimulator.AssertGetFirstMessageText(address, "test");

               // 7.4 rounds to 7, which is past the mark threshold of 5.
               StringAssert.Contains("looks like spam to me", delivered,
                  "The engine's own reason should reach the message. Got: " + delivered);
               StringAssert.Contains("(Score: 7)", delivered,
                  "7.4 should round to 7 rather than truncate to 6 - truncating would make every " +
                  "verdict slightly kinder than the engine intended. Got: " + delivered);

               // What the engine was actually sent. A hook that posted nothing would
               // still have passed the assertions above.
               ClassicAssert.AreEqual(1, engine.Requests.Count,
                  "The engine should have been asked exactly once about this message.");

               string request = engine.Requests[0];

               StringAssert.Contains("POST /checkv2", request, "Got: " + request);
               StringAssert.Contains("From: outsider@unaligned-envelope.test", request,
                  "The envelope sender must reach the engine. Got: " + request);
               StringAssert.Contains("Rcpt: " + address, request,
                  "The recipients must reach the engine. Got: " + request);
               StringAssert.Contains("Ip: 127.0.0.1", request,
                  "The connecting address must reach the engine - it is most of what a " +
                  "reputation engine works from. Got: " + request);
               StringAssert.Contains("A body the engine should be shown.", request,
                  "The message itself must be posted, not just its metadata. Got: " + request);
            }
            finally
            {
               ClearHook();
               RestartServerAndReacquireCom();
            }
         }
      }

      [Test]
      [Description("An engine that answers with an error does not stop the mail by default - a filter outage that lets spam through is recoverable, one that defers everything is not")]
      public void AnEngineThatDoesNotAnswerLetsTheMessageThrough()
      {
         string address = SingletonProvider<TestSetup>.Instance
            .AddAccount(_domain, "hookopen@example.test", "test").Address;

         var antiSpam = _application.Settings.AntiSpam;
         antiSpam.SpamMarkThreshold = 5;
         antiSpam.SpamDeleteThreshold = 10;

         // null means the engine answers 500: present, reachable, and no use.
         using (var engine = new FakeFilterEngine(null))
         {
            try
            {
               ConfigureHook(engine.Url, failClosed: "0");
               RestartServerAndReacquireCom();

               SmtpClientSimulator.StaticSend("outsider@unaligned-envelope.test", address,
                                              "open", "Body");

               Pop3ClientSimulator.AssertMessageCount(address, "test", 1);
            }
            finally
            {
               ClearHook();
               RestartServerAndReacquireCom();
            }
         }
      }

      [Test]
      [Description("With FilterHookFailClosed on, an engine that does not answer scores the message as spam - otherwise the switch would do nothing")]
      public void WithFailClosedAnUnansweredCheckIsTreatedAsSpam()
      {
         string address = SingletonProvider<TestSetup>.Instance
            .AddAccount(_domain, "hookclosed@example.test", "test").Address;

         var antiSpam = _application.Settings.AntiSpam;
         antiSpam.SpamMarkThreshold = 5;
         antiSpam.SpamDeleteThreshold = 10;

         using (var engine = new FakeFilterEngine(null))
         {
            try
            {
               ConfigureHook(engine.Url, failClosed: "1");
               RestartServerAndReacquireCom();

               // The default reject score is 100, well past the delete threshold of
               // 10, so the message is refused rather than delivered.
               CustomAsserts.Throws<DeliveryFailedException>(() =>
                  SmtpClientSimulator.StaticSend("outsider@unaligned-envelope.test", address, "closed", "Body"));

               Pop3ClientSimulator.AssertMessageCount(address, "test", 0);
            }
            finally
            {
               ClearHook();
               RestartServerAndReacquireCom();
               LogHandler.DeleteErrorLog();
            }
         }
      }

      [Test]
      [Description("A slow engine costs the configured timeout and no more, because this runs while an SMTP client is waiting for its answer to DATA")]
      public void ASlowEngineIsBoundedByTheConfiguredTimeout()
      {
         string address = SingletonProvider<TestSetup>.Instance
            .AddAccount(_domain, "hookslow@example.test", "test").Address;

         var antiSpam = _application.Settings.AntiSpam;
         antiSpam.SpamMarkThreshold = 5;
         antiSpam.SpamDeleteThreshold = 1000;

         // Answers far later than the timeout allows.
         using (var engine = new FakeFilterEngine("{\"action\":\"reject\"}", delayMilliseconds: 30000))
         {
            try
            {
               ConfigureHook(engine.Url, failClosed: "0", timeout: "2");
               RestartServerAndReacquireCom();

               var started = System.Diagnostics.Stopwatch.StartNew();

               SmtpClientSimulator.StaticSend("outsider@unaligned-envelope.test", address, "slow", "Body");

               Pop3ClientSimulator.AssertMessageCount(address, "test", 1);

               started.Stop();

               // Generous, because delivery does more than the hook - the point is
               // that it did not wait out the engine's 30 seconds.
               ClassicAssert.Less(started.Elapsed.TotalSeconds, 20,
                  "A hook configured with a 2 second timeout must not let a 30 second engine hold " +
                  "the message. Took " + started.Elapsed.TotalSeconds + "s.");
            }
            finally
            {
               ClearHook();
               RestartServerAndReacquireCom();
            }
         }
      }
   }
}
