// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
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
            catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
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

      /// <summary>
      ///    The event log as it stands right now, or an empty string when it does not
      ///    exist yet or is momentarily locked.
      ///
      ///    TestSetup.ReadExistingTextFile is the usual reader, but it asserts the file
      ///    into existence and then retries a hundred times for non-empty content. Both
      ///    are right for reading a log after the fact and wrong for polling one that is
      ///    expected to be absent on the first few passes: the caller here is already
      ///    inside a retry, and wants "not yet" to be cheap.
      /// </summary>
      private static string ReadEventLogOrEmpty()
      {
         try
         {
            string fileName = LogHandler.GetEventLogFileName();

            if (!File.Exists(fileName))
               return string.Empty;

            using (var stream = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
            {
               return reader.ReadToEnd();
            }
         }
         catch (IOException)
         {
            return string.Empty;
         }
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
         // server queue, the IOCP queue, the SMTP delivery queue, the external fetch
         // queue and - since they hold live connections - the asynchronous task and
         // name-lookup queues. Only the maintenance queue outlives the servers. That
         // churn is the condition under which a queue id must not be handed out twice,
         // under which a stopped queue must not be left with worker threads still
         // running inside it, and under which a queue removed on the way down has to be
         // created again on the way up - which is what this delivery proves.
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

      /// <summary>
      ///    Stopping the servers while a session's finalization task is still running
      ///    must not fault the process.
      ///
      ///    A task on the asynchronous queue holds its SMTPConnection alive through a
      ///    shared_ptr, and that connection owns a socket built on the io_context inside
      ///    Application::io_service_. The asynchronous and name-lookup queues used to be
      ///    created in InitInstance and removed in ExitInstance, while io_service_ is
      ///    created in StartServers and destroyed in StopServers - so the queues
      ///    straddled the io_context instead of nesting inside it. A task that was still
      ///    running when StopServers reached "Destructing IOCP" therefore released the
      ///    last reference to its connection AFTER the context that connection's socket
      ///    belonged to had been destroyed, and ~basic_stream_socket wrote through it.
      ///
      ///    It was found the hard way: a reverse-DNS prefetch sat in a two second
      ///    DnsQuery, finished 1.9 seconds after the io_context was destroyed, and took
      ///    an access violation. The shutdown had even logged that it was waiting for
      ///    that thread - it just waited two teardown steps too late.
      ///
      ///    The hold here is a script rather than a slow DNS lookup because it is the
      ///    same window reached deterministically: OnAcceptMessage runs on the
      ///    finalization task, on the thread that owns the connection, so a script that
      ///    takes a few seconds guarantees a task is mid-flight when Stop() is called.
      ///    Without the fix this is a reliable fault; with it, Stop() waits for the task
      ///    and the connection is released while its io_context is still alive.
      /// </summary>
      [Test]
      [Description("Stopping the servers while a finalization task still holds a live connection drains the task first, instead of destroying the io_context underneath its socket.")]
      public void TestStoppingTheServersWhileAFinalizationTaskRunsDoesNotFault()
      {
         // Both accounts up front, before anything is restarted, so that no COM object
         // this test holds is used across a stop.
         //
         // Two of them, because the mid-flight message's fate is deliberately not
         // asserted: its script had run, so the pipeline behind it may or may not have
         // saved it depending on where the shutdown interrupted the task. Counting
         // messages in the mailbox it was addressed to would make this test's result
         // depend on that, when what it is actually about is the process surviving.
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(
            _domain, "midflight@example.test", "test");
         var afterAccount = SingletonProvider<TestSetup>.Instance.AddAccount(
            _domain, "afterstop@example.test", "test");

         string afterAddress = afterAccount.Address;

         LogHandler.DeleteEventLog();

         // Timer is seconds since midnight and resets to zero there, so the elapsed
         // value is checked for having gone backwards as well as for having run long
         // enough. Without that, a run that straddles midnight would spin for the best
         // part of a day inside a script the server cannot interrupt.
         const string script =
            @"Sub OnAcceptMessage(oClient, message)
                 Dim startedAt, elapsed
                 EventLog.Write(""MIDFLIGHT-STARTED"")
                 startedAt = Timer
                 Do
                    elapsed = Timer - startedAt
                 Loop While elapsed >= 0 And elapsed < 4
                 EventLog.Write(""MIDFLIGHT-FINISHED"")
              End Sub";

         var scripting = _settings.Scripting;
         string scriptFile = scripting.CurrentScriptFile;
         string originalScript = File.Exists(scriptFile) ? File.ReadAllText(scriptFile) : string.Empty;
         bool originalEnabled = scripting.Enabled;

         try
         {
            File.WriteAllText(scriptFile, script);
            scripting.Enabled = true;
            scripting.Reload();

            // The send is expected NOT to complete. The servers are stopped underneath
            // it, so the socket carrying its "250" belongs to an io_context that has
            // been told to stop; what the client sees is a dropped session. That is the
            // scenario, not a failure of it, so the exception is swallowed here and the
            // health of the server is judged below by what it does afterwards.
            Task sender = Task.Run(() =>
            {
               try
               {
                  new SmtpClientSimulator().Send(
                     "test@example.test", account.Address, "Midflight", "Midflight");
               }
               catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
               {
                  // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
               }
            });

            try
            {
               // Wait for the task to actually be on the queue and inside the script.
               // Stopping before that would tear the servers down with nothing in
               // flight, which is a different (and already passing) test.
               RetryHelper.TryAction(TimeSpan.FromSeconds(30), () =>
                  RetryableAssert.StringContains("MIDFLIGHT-STARTED", ReadEventLogOrEmpty()));

               // The subject of the test. On the unfixed build the connection outlives
               // its io_context and the crash oracle records an access violation.
               _application.Stop();
               _application.Start();

               // Named here rather than left to TearDown so that a failure points at
               // the stop rather than at whatever ran next.
               CrashOracleAsserts.AssertNoMemorySafetyEvents();
            }
            finally
            {
               // Bounded, and only after the servers are back: the sending thread is
               // blocked on a socket with no receive timeout, and leaving it running
               // into the next fixture is how one test's mess becomes another test's
               // failure.
               sender.Wait(TimeSpan.FromSeconds(60));
            }
         }
         finally
         {
            // Through _application.Settings: the Scripting object taken above belongs
            // to the configuration that was loaded before the restart.
            var restored = _application.Settings.Scripting;
            File.WriteAllText(scriptFile, originalScript);
            restored.Enabled = originalEnabled;
            restored.Reload();
         }

         // The server is not merely un-crashed but working: the queues removed on the
         // way down were created again on the way up, and mail flows over them.
         RunBounded("Delivery after a stop taken mid-finalization", TimeSpan.FromSeconds(60), () =>
         {
            new SmtpClientSimulator().Send(
               "test@example.test", afterAddress, "AfterRestart", "AfterRestart");

            Pop3ClientSimulator.AssertMessageCount(afterAddress, "test", 1);
         });
      }
   }
}
