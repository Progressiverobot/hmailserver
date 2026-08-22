// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Threading;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.POP3
{
   /// <summary>
   /// Three POP3 defects found by source audit, all in the same connection handler.
   ///
   /// 1. RFC 1939 requires a message marked for deletion to be treated as if it were
   ///    not in the maildrop at all. The scan-listing forms (LIST / UIDL with no
   ///    argument) always honoured that, but the single-message forms (LIST n /
   ///    UIDL n) went straight to the message and formatted a "+OK" reply with no
   ///    deleted check. Within one session the two forms therefore disagreed: LIST
   ///    said eight messages and then LIST 2 happily reported a size for the one the
   ///    client had just deleted. A client that trusts LIST n then issues RETR n and
   ///    gets "-ERR", because RETR did check the flag.
   ///
   /// 2. RFC 4959 defines a bare "=" as the wire form of an *empty* initial response.
   ///    The AUTH parser read "=" as meaning "no initial response at all", so it
   ///    armed the pending-continuation state and sent a "+" the client was not
   ///    waiting for. The session then ran one line out of step - the client's next
   ///    real command was swallowed as SASL continuation data.
   ///
   /// 3. The password and OAuth2 bearer paths fire OnClientLogon on success *and* on
   ///    failure, with Client.Authenticated carrying the outcome. The SCRAM failure
   ///    path fired nothing, so script-based lockout and auditing could not see a
   ///    SCRAM brute-force attempt at all.
   ///
   /// Each test below also pins the behaviour that must NOT change: a live message
   /// still lists, a real SASL exchange still gets its continuation, and a successful
   /// SCRAM logon still reports Authenticated = True exactly once.
   /// </summary>
   [TestFixture]
   public class ProtocolConformance : TestFixtureBase
   {
      [Test]
      [Description("RFC 1939: LIST n for a message marked deleted answers -ERR, and a message " +
                   "that is not deleted is still listed.")]
      public void ListOfSingleDeletedMessageIsRefused()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "listdeleted@example.test", "test");

         for (var i = 1; i <= 3; i++)
            SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "TestBody" + i);

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 3);

         var sim = new Pop3ClientSimulator();
         Assert.IsTrue(sim.ConnectAndLogon(account.Address, "test"));

         Assert.IsTrue(sim.DELE(2), "DELE 2 should have marked the second message deleted.");

         // The defect: this answered "+OK 2 <size>" for a message the client had
         // already deleted in this same session.
         var deleted = sim.LIST(2);
         Assert.IsTrue(deleted.TrimStart().StartsWith("-ERR"),
            "LIST of a message marked deleted must answer -ERR (RFC 1939 treats it as absent). Got: " + deleted);

         // Negative control: the rest of the maildrop is untouched. If this ever
         // regressed to -ERR the "fix" would have broken every normal client.
         var live = sim.LIST(3);
         Assert.IsTrue(live.TrimStart().StartsWith("+OK 3 "),
            "LIST of a message that is not deleted must still report its size. Got: " + live);

         sim.QUIT();
      }

      [Test]
      [Description("RFC 1939: UIDL n for a message marked deleted answers -ERR, and a message " +
                   "that is not deleted still returns its UID.")]
      public void UidlOfSingleDeletedMessageIsRefused()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "uidldeleted@example.test", "test");

         for (var i = 1; i <= 3; i++)
            SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "TestBody" + i);

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 3);

         var sim = new Pop3ClientSimulator();
         Assert.IsTrue(sim.ConnectAndLogon(account.Address, "test"));

         Assert.IsTrue(sim.DELE(1), "DELE 1 should have marked the first message deleted.");

         // The defect: this answered "+OK 1 <uid>", so a client resynchronising by
         // UID saw a message the scan listing had already dropped.
         var deleted = sim.UIDL(1);
         Assert.IsTrue(deleted.TrimStart().StartsWith("-ERR"),
            "UIDL of a message marked deleted must answer -ERR (RFC 1939 treats it as absent). Got: " + deleted);

         var live = sim.UIDL(2);
         Assert.IsTrue(live.TrimStart().StartsWith("+OK 2 "),
            "UIDL of a message that is not deleted must still return its UID. Got: " + live);

         sim.QUIT();
      }

      [Test]
      [Description("RFC 4959: 'AUTH PLAIN =' carries an empty initial response, so it is processed " +
                   "as a (failing) credential rather than turned into a continuation, and the " +
                   "session stays in step.")]
      public void AuthPlainWithEmptyInitialResponseIsNotAContinuation()
      {
         var settings = SingletonProvider<TestSetup>.Instance.GetApp().Settings;
         var originalAutoBan = settings.AutoBanOnLogonFailure;
         settings.AutoBanOnLogonFailure = false;
         settings.ClearLogonFailureList();

         try
         {
            var tc = new TcpConnection();
            Assert.IsTrue(tc.Connect(110));
            tc.ReadUntil("+OK"); // banner

            tc.Send("AUTH PLAIN =\r\n");
            var response = tc.Receive();

            // The defect: the server replied with a "+" continuation here, because
            // "=" was read as "no initial response". An empty initial response is
            // still a response: it decodes to nothing, so it must simply fail.
            Assert.IsTrue(response.TrimStart().StartsWith("-ERR"),
               "'AUTH PLAIN =' is an empty initial response and must be rejected, not answered with a continuation. Got: " + response);

            // And the session must still be in step. With the pending-continuation
            // state wrongly armed, this CAPA line was consumed as SASL data and
            // answered "-ERR Invalid SASL PLAIN response."
            tc.Send("CAPA\r\n");
            var caps = tc.Receive();
            Assert.IsTrue(caps.TrimStart().StartsWith("+OK"),
               "The command after 'AUTH PLAIN =' must be parsed as a command, not as SASL continuation data. Got: " + caps);

            // Negative control: a real AUTH PLAIN with no initial response must still
            // get its continuation and still authenticate.
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "saslir@example.test", "SeC-r3t Pass!");

            var tc2 = new TcpConnection();
            Assert.IsTrue(tc2.Connect(110));
            tc2.ReadUntil("+OK"); // banner
            var final = Pop3SaslTestClient.AuthenticatePlain(tc2, "saslir@example.test", "SeC-r3t Pass!");
            Assert.IsTrue(final.TrimStart().StartsWith("+OK"),
               "AUTH PLAIN without an initial response must still work. Got: " + final);
            tc2.Send("QUIT\r\n");
            tc2.Disconnect();

            tc.Send("QUIT\r\n");
            tc.Disconnect();
         }
         finally
         {
            settings.AutoBanOnLogonFailure = originalAutoBan;
            settings.ClearLogonFailureList();
         }
      }

      [Test]
      [Description("A failed SCRAM-SHA-256 authentication fires OnClientLogon with " +
                   "Client.Authenticated = False, the same contract the password and bearer " +
                   "paths use, so script-based lockout and auditing can see SCRAM brute force.")]
      public void FailedScramLogonFiresOnClientLogon()
      {
         var settings = SingletonProvider<TestSetup>.Instance.GetApp().Settings;
         var scripting = settings.Scripting;
         var originalAutoBan = settings.AutoBanOnLogonFailure;
         settings.AutoBanOnLogonFailure = false;
         settings.ClearLogonFailureList();

         try
         {
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "scramevent@example.test", "correct horse");

            var script = "Sub OnClientLogon(oClient) " + Environment.NewLine +
                         " EventLog.Write(\"POP3-Logon-Event\")" + Environment.NewLine +
                         " EventLog.Write(\"IsAuthenticated: \" & oClient.Authenticated) " + Environment.NewLine +
                         " EventLog.Write(\"Username: \" & oClient.Username) " + Environment.NewLine +
                         "End Sub" + Environment.NewLine + Environment.NewLine;

            File.WriteAllText(scripting.CurrentScriptFile, script);
            scripting.Enabled = true;
            scripting.Reload();

            LogHandler.DeleteEventLog();

            var tc = new TcpConnection();
            Assert.IsTrue(tc.Connect(110));
            tc.ReadUntil("+OK"); // banner

            var final = Pop3SaslTestClient.AuthenticateScram(tc, "scramevent@example.test", "wrong password");
            Assert.IsTrue(final.TrimStart().StartsWith("-ERR"),
               "SCRAM with a wrong password must be rejected. Got: " + final);
            tc.Disconnect();

            // The defect: nothing at all was written here, because the SCRAM failure
            // path never fired the event.
            var log = WaitForEventLogContaining("POP3-Logon-Event");
            Assert.IsTrue(log.Contains("POP3-Logon-Event"),
               "A failed SCRAM logon must fire OnClientLogon. Event log: " + log);
            Assert.IsTrue(log.Contains("IsAuthenticated: False"),
               "The event must carry Client.Authenticated = False for a failed SCRAM logon. Event log: " + log);
            Assert.IsTrue(log.Contains("Username: scramevent@example.test"),
               "The event must carry the attempted user name. Event log: " + log);

            // Negative control: a successful SCRAM logon still reports True, so the
            // added event did not blur the two outcomes together.
            LogHandler.DeleteEventLog();

            var tc2 = new TcpConnection();
            Assert.IsTrue(tc2.Connect(110));
            tc2.ReadUntil("+OK"); // banner
            var ok = Pop3SaslTestClient.AuthenticateScram(tc2, "scramevent@example.test", "correct horse");
            Assert.IsTrue(ok.TrimStart().StartsWith("+OK"), "SCRAM with the correct password must succeed. Got: " + ok);
            tc2.Send("QUIT\r\n");
            tc2.Disconnect();

            var successLog = WaitForEventLogContaining("POP3-Logon-Event");
            Assert.IsTrue(successLog.Contains("IsAuthenticated: True"),
               "A successful SCRAM logon must still report Authenticated = True. Event log: " + successLog);
            Assert.IsFalse(successLog.Contains("IsAuthenticated: False"),
               "A successful SCRAM logon must not also report a failure. Event log: " + successLog);
         }
         finally
         {
            scripting.Enabled = false;
            settings.AutoBanOnLogonFailure = originalAutoBan;
            settings.ClearLogonFailureList();
         }
      }

      /// <summary>
      /// The event log is written asynchronously by the script server, so poll for the
      /// expected text rather than reading once. Returns whatever the log holds when
      /// the text appears or the wait runs out, so the assertion can report it.
      /// </summary>
      private static string WaitForEventLogContaining(string text)
      {
         var fileName = LogHandler.GetEventLogFileName();

         var contents = "";
         for (var i = 0; i < 60; i++)
         {
            if (File.Exists(fileName))
            {
               try
               {
                  using (var stream = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                  using (var reader = new StreamReader(stream))
                  {
                     contents = reader.ReadToEnd();
                  }
               }
               catch (IOException)
               {
                  // The server may be mid-write; try again.
               }

               if (contents.Contains(text))
                  return contents;
            }

            Thread.Sleep(250);
         }

         return contents;
      }
   }
}
