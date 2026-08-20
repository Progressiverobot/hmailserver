// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    The queryable message trace.
   ///
   ///    The question it exists for is "what happened to the message Jane sent at
   ///    14:20", and the answer used to be to grep several log files and hope the
   ///    relevant one had not rotated. The events were never missing - the AWStats
   ///    journal has been called from every interesting site for years - they simply
   ///    went to a text file shaped for a web-statistics tool, with no way to ask it
   ///    anything.
   ///
   ///    So the tests here are not about instrumentation coverage. They are about the
   ///    three things that make the events useful: that they are recorded
   ///    independently of the AWStats journal (two separate features that happen to
   ///    observe the same moments), that the queue id correlates the events of one
   ///    message, and that the whole thing is genuinely off until asked for - because
   ///    this table records who corresponds with whom, retained.
   /// </summary>
   [TestFixture]
   public class MessageTraceTests : TestFixtureBase
   {
      private Account _recipient;

      [SetUp]
      public new void SetUp()
      {
         _recipient = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "traced@example.test", "test");

         EmptyTheTrace();
      }

      [TearDown]
      public new void TearDown()
      {
         ServerIniFile.SetSetting("MessageTraceEnabled", null);
         _application.Reinitialize();

         EmptyTheTrace();
      }

      private void EmptyTheTrace()
      {
         _application.Database.ExecuteSQL("delete from hm_messagetrace");
      }

      private void EnableTrace()
      {
         ServerIniFile.SetSetting("MessageTraceEnabled", "1");
         _application.Reinitialize();
      }

      private int TraceCount(string address)
      {
         var trace = _application.GlobalObjects.MessageTrace;
         trace.Search(address);
         return trace.Count;
      }

      [Test]
      [Description("Off by default: nothing is recorded until an administrator asks for it, because this table is a record of who corresponds with whom")]
      public void OffByDefault()
      {
         SmtpClientSimulator.StaticSend("outsider@example.com", _recipient.Address, "quiet", "body");
         Pop3ClientSimulator.AssertMessageCount(_recipient.Address, "test", 1);

         ClassicAssert.AreEqual(0, TraceCount(""),
            "A delivered message must leave no trace row while the feature is off. This is a privacy " +
            "default, not a performance one.");
      }

      [Test]
      [Description("A delivery is recorded with the who, when and status a support question actually needs")]
      public void ADeliveryIsRecorded()
      {
         EnableTrace();

         SmtpClientSimulator.StaticSend("sender@example.com", _recipient.Address, "traced", "body");
         Pop3ClientSimulator.AssertMessageCount(_recipient.Address, "test", 1);

         var trace = _application.GlobalObjects.MessageTrace;
         trace.Search("sender@example.com");

         ClassicAssert.GreaterOrEqual(trace.Count, 1, "The delivery must be recorded.");

         var traceEvent = trace[0];

         ClassicAssert.AreEqual("delivered", traceEvent.EventName);
         ClassicAssert.AreEqual("sender@example.com", traceEvent.Sender);
         ClassicAssert.AreEqual(_recipient.Address, traceEvent.Recipient);
         ClassicAssert.AreEqual(250, traceEvent.StatusCode);
         ClassicAssert.IsNotEmpty(traceEvent.OccurredTime);
         ClassicAssert.Greater(traceEvent.QueueID, 0,
            "A delivered message had a queue entry, so its events must be correlatable.");
      }

      [Test]
      [Description("A refusal is recorded too - the case where the message never arrived is exactly when somebody asks")]
      public void ARefusalIsRecorded()
      {
         EnableTrace();

         // An unknown recipient is refused at RCPT, before a message exists - which is
         // why its queue id is 0 rather than a number. Asserted rather than tidied
         // away, because a reader of the trace needs to know that 0 means "there was
         // never a queue entry" and not "the id was lost".
         CustomAsserts.Throws<DeliveryFailedException>(() =>
            SmtpClientSimulator.StaticSend("sender@example.com", "nobody@example.test", "refused", "body"));

         var trace = _application.GlobalObjects.MessageTrace;
         trace.Search("nobody@example.test");

         ClassicAssert.AreEqual(1, trace.Count, "The refusal must be recorded.");

         var traceEvent = trace[0];
         ClassicAssert.AreEqual("failed", traceEvent.EventName);
         ClassicAssert.AreEqual(550, traceEvent.StatusCode);
         ClassicAssert.AreEqual(0, traceEvent.QueueID,
            "Refused at RCPT, so there was never a queue entry. 0 is the honest answer.");
      }

      [Test]
      [Description("Searching matches sender OR recipient - somebody reporting a problem knows one of the two, not which")]
      public void SearchingMatchesEitherEnd()
      {
         EnableTrace();

         SmtpClientSimulator.StaticSend("findme@example.com", _recipient.Address, "traced", "body");
         Pop3ClientSimulator.AssertMessageCount(_recipient.Address, "test", 1);

         ClassicAssert.GreaterOrEqual(TraceCount("findme@example.com"), 1, "Found by sender.");
         ClassicAssert.GreaterOrEqual(TraceCount(_recipient.Address), 1, "...and by recipient.");
         ClassicAssert.AreEqual(0, TraceCount("someone@unrelated.invalid"),
            "An address with no events returns nothing rather than everything.");
      }

      [Test]
      [Description("The events of one message can be pulled together by queue id, oldest first - the story rather than a list")]
      public void OneMessageCanBeFollowedByQueueId()
      {
         EnableTrace();

         SmtpClientSimulator.StaticSend("story@example.com", _recipient.Address, "traced", "body");
         Pop3ClientSimulator.AssertMessageCount(_recipient.Address, "test", 1);

         var trace = _application.GlobalObjects.MessageTrace;
         trace.Search("story@example.com");
         ClassicAssert.GreaterOrEqual(trace.Count, 1);

         int queueId = trace[0].QueueID;

         var byQueue = _application.GlobalObjects.MessageTrace;
         byQueue.SearchByQueueID(queueId);

         ClassicAssert.GreaterOrEqual(byQueue.Count, 1, "The queue id must find its own events.");

         for (int i = 0; i < byQueue.Count; i++)
         {
            ClassicAssert.AreEqual(queueId, byQueue[i].QueueID,
               "Every event returned must belong to the requested message.");
         }
      }

      [Test]
      [Description("The trace does not depend on the AWStats journal - two features that observe the same moments")]
      public void TheTraceIsIndependentOfAwstats()
      {
         bool awstatsWasEnabled = _application.Settings.Logging.AWStatsEnabled;

         try
         {
            _application.Settings.Logging.AWStatsEnabled = false;
            EnableTrace();

            SmtpClientSimulator.StaticSend("independent@example.com", _recipient.Address, "traced", "body");
            Pop3ClientSimulator.AssertMessageCount(_recipient.Address, "test", 1);

            ClassicAssert.GreaterOrEqual(TraceCount("independent@example.com"), 1,
               "The trace hangs off the same call sites as the AWStats journal but must not be gated " +
               "on it: an administrator who wants a queryable trace should not have to switch on a " +
               "web-statistics journal to get one.");
         }
         finally
         {
            _application.Settings.Logging.AWStatsEnabled = awstatsWasEnabled;
         }
      }
   }
}
