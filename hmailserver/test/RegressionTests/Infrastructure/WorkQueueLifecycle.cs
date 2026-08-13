// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

using System;
using System.Threading.Tasks;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    The work queues that carry every asynchronous unit of work in the server:
   ///    the size of their thread pools, and their behaviour across a stop and a start.
   ///
   ///    Both tests here are about a queue still being able to run work at all. That
   ///    failure produces no error and no bounce - the message is accepted, the
   ///    finalization task is queued, and nothing ever picks it up - so it is exactly
   ///    the kind that a suite of protocol tests can miss entirely.
   /// </summary>
   [TestFixture]
   public class WorkQueueLifecycle : TestFixtureBase
   {
      /// <summary>
      ///    Runs an action that talks to the server on a thread of its own, and fails
      ///    the test if it has not finished in time.
      ///
      ///    The client simulators read from a blocking socket with no receive timeout,
      ///    so a server that accepts a message and then never answers stops the calling
      ///    thread for good. A test whose subject is "the pool can still run work" must
      ///    not be able to hang the whole run when it finds that it cannot.
      /// </summary>
      private static void RunBounded(string description, TimeSpan timeout, Action action)
      {
         Exception failure = null;

         Task worker = Task.Run(() =>
         {
            try
            {
               action();
            }
            catch (Exception ex)
            {
               failure = ex;
            }
         });

         bool finished = worker.Wait(timeout);

         Assert.IsTrue(finished,
            description + " did not finish within " + timeout.TotalSeconds
            + " seconds. The work queue carrying it is not running the work it was handed.");

         if (failure != null)
            throw new Exception(description + " failed: " + failure.Message, failure);
      }

      [Test]
      [Description("A configured asynchronous thread count of zero is clamped to a working pool, instead of producing a queue that accepts every task and runs none of them.")]
      public void TestZeroAsynchronousThreadsIsClampedToAWorkingPool()
      {
         // Created before anything is restarted, so that the COM objects this test
         // holds are never used across a reinitialize.
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(
            _domain, "clamped@example.test", "test");

         string address = account.Address;

         int originalThreads = _settings.MaxAsynchronousThreads;

         try
         {
            // MaxAsynchronousThreads is administrator-settable over COM and from the
            // Control Panel, and nothing on the way in rejects zero. Zero used to reach
            // WorkQueue::Start() unaltered, and its loop then created no threads at all.
            _settings.MaxAsynchronousThreads = 0;

            // The live queue is told at once, through Application::OnPropertyChanged, so
            // the clamp is visible without a restart. Asserted before anything is sent:
            // on a build without the clamp this is where the test stops, which is also
            // what keeps it away from a send to a server that cannot answer it.
            RetryHelper.TryAction(TimeSpan.FromSeconds(10), () =>
            {
               RetryableAssert.StringContains("outside the supported range of 1 to 100",
                                              LogHandler.ReadCurrentDefaultLog());
            });

            // Reinitialize so the asynchronous queue is rebuilt from the stored value.
            // That queue finalizes and acknowledges every received message, so if it has
            // no threads the send below never gets its reply.
            _application.Reinitialize();

            RunBounded("Delivery over a queue configured for zero threads", TimeSpan.FromSeconds(60), () =>
            {
               var smtpClientSimulator = new SmtpClientSimulator();
               smtpClientSimulator.Send("test@example.test", address, "Clamp", "Clamp");

               Pop3ClientSimulator.AssertMessageCount(address, "test", 1);
            });
         }
         finally
         {
            // Through _application.Settings rather than the cached _settings: this
            // runs after a Reinitialize, and the other fixtures that reinitialize
            // re-fetch the Settings object afterwards rather than reusing one taken
            // before it.
            _application.Settings.MaxAsynchronousThreads = originalThreads;
            _application.Reinitialize();
         }
      }

      [Test]
      [Description("Stopping and starting the servers repeatedly leaves every work queue able to run work, and reports no error.")]
      public void TestRepeatedRestartsLeaveTheWorkQueuesUsable()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(
            _domain, "restarted@example.test", "test");

         string address = account.Address;

         // Every server start creates work queues and every stop removes them: the main
         // server queue, the IOCP queue, the SMTP delivery queue and the external fetch
         // queue all come and go with the servers, while the maintenance, asynchronous
         // and name-lookup queues outlive them. That churn is the condition under which
         // a queue id must not be handed out twice, and under which a stopped queue must
         // not be left with worker threads still running inside it.
         for (int i = 0; i < 3; i++)
         {
            _application.Stop();
            _application.Start();
         }

         RunBounded("Delivery after three restarts", TimeSpan.FromSeconds(60), () =>
         {
            var smtpClientSimulator = new SmtpClientSimulator();
            smtpClientSimulator.Send("test@example.test", address, "Restart", "Restart");

            Pop3ClientSimulator.AssertMessageCount(address, "test", 1);
         });

         CustomAsserts.AssertNoReportedError();
      }
   }
}
