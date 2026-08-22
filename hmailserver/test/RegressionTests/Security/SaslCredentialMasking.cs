// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Text;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.Security
{
   /// <summary>
   /// PasswordRemover is the scrubber that runs over every client line before it is
   /// written to the protocol log. Its POP3 arm used to look at nothing but the first
   /// four characters of the line, and hid the rest only when they spelled "PASS".
   /// Every other way of presenting a credential therefore went into the log
   /// verbatim:
   ///
   ///    AUTH PLAIN AHVzZXJAZXhhbXBsZS50ZXN0AHNlY3JldA==   (RFC 4959 initial response)
   ///    biwsbj11c2VyQGV4YW1wbGUudGVzdCxyPS4uLg==           (a SCRAM continuation)
   ///
   /// Both decode straight back to the credential, so a log file - which is world
   /// readable on a default install, and routinely pasted into forum posts and
   /// support tickets - was a password file. The POP3 connection class masks the
   /// continuation of a PLAIN or a bearer exchange because it tracks that state, but
   /// it does not track SCRAM, and it never saw the single-line initial-response form
   /// at all.
   ///
   /// These tests drive the raw protocol and then read the log back. The two masking
   /// tests fail against the unfixed code because the base64 credential is present in
   /// the log file; the guard tests pin the half that must not change - the command
   /// name, the mechanism name and the account name stay readable, because a log with
   /// no identity in it cannot tell an administrator which account is being attacked.
   /// </summary>
   [TestFixture]
   public class SaslCredentialMasking : TestFixtureBase
   {
      private const string Username = "saslmask@example.test";
      private const string Password = "Sup3rSecretPassword";

      private static string Base64(string value)
      {
         return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
      }

      /// <summary>
      /// Waits until the server has flushed a line containing <paramref name="marker" />
      /// and then returns the whole log, so that the assertions run against a log that
      /// is known to have reached the point of interest.
      /// </summary>
      private static string ReadLogAfter(string marker)
      {
         Assert.IsTrue(LogHandler.DefaultLogContains(marker),
            "The server never logged anything containing \"" + marker + "\".");

         return LogHandler.ReadCurrentDefaultLog();
      }

      private static TcpConnection ConnectPop3()
      {
         var connection = new TcpConnection();
         Assert.IsTrue(connection.Connect(110));
         Assert.IsTrue(connection.Receive().StartsWith("+OK"));
         return connection;
      }

      [Test]
      [Description("A POP3 AUTH PLAIN initial response must not reach the log; the mechanism name must.")]
      public void Pop3SaslPlainInitialResponseIsMasked()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, Username, Password);

         string initialResponse = Base64("\0" + Username + "\0" + Password);

         var connection = ConnectPop3();
         connection.Send("AUTH PLAIN " + initialResponse + "\r\n");
         connection.Receive();
         connection.Send("QUIT\r\n");
         connection.Receive();
         connection.Disconnect();

         string log = ReadLogAfter("QUIT");

         // The defect: the entire credential, base64 encoded, in the log.
         Assert.IsFalse(log.Contains(initialResponse),
            "The base64 SASL PLAIN initial response was written to the log: " + log);
         Assert.IsFalse(log.Contains(Password),
            "The password was written to the log: " + log);

         // ... and the part that must survive, because "which mechanism did this
         // client try" is the reason the line is logged at all.
         StringAssert.Contains("AUTH PLAIN ***", log);
      }

      [Test]
      [Description("A POP3 SCRAM continuation line carries the client proof and must not reach the log.")]
      public void Pop3ScramContinuationIsMasked()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, Username, Password);

         // A well formed RFC 5802 client-first-message, so that the server answers
         // with a challenge and the session survives long enough to send QUIT.
         string clientFirst = Base64("n,,n=" + Username + ",r=rOprNGfwEbeRWgbNEkqO");

         var connection = ConnectPop3();
         connection.Send("AUTH SCRAM-SHA-256\r\n");
         Assert.IsTrue(connection.Receive().TrimStart().StartsWith("+"),
            "Expected an empty SCRAM continuation.");

         connection.Send(clientFirst + "\r\n");
         connection.Receive();
         connection.Send("QUIT\r\n");
         connection.Receive();
         connection.Disconnect();

         // QUIT is sent after the credential line, so a log that has reached QUIT has
         // already written whatever it was going to write for the continuation.
         string log = ReadLogAfter("QUIT");

         // The defect: a bare base64 continuation line is not a "PASS" line, so the
         // old scrubber returned it untouched.
         Assert.IsFalse(log.Contains(clientFirst),
            "The base64 SCRAM continuation line was written to the log: " + log);

         StringAssert.Contains("AUTH SCRAM-SHA-256", log);
      }

      [Test]
      [Description("Guard: USER/PASS keeps the account name readable and still hides the password.")]
      public void Pop3UserNameStaysReadableAndPasswordDoesNot()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, Username, Password);

         var connection = ConnectPop3();
         connection.Send("USER " + Username + "\r\n");
         connection.Receive();
         connection.Send("PASS " + Password + "\r\n");
         connection.Receive();
         connection.Send("QUIT\r\n");
         connection.Receive();
         connection.Disconnect();

         string log = ReadLogAfter("PASS ***");

         StringAssert.Contains("USER " + Username, log);
         Assert.IsFalse(log.Contains(Password), "The password was written to the log: " + log);
      }

      [Test]
      [Description("Guard: ordinary POP3 commands are still logged verbatim.")]
      public void OrdinaryPop3CommandsAreNotMasked()
      {
         var connection = ConnectPop3();
         connection.Send("CAPA\r\n");
         connection.Receive();
         connection.Send("NOOP\r\n");
         connection.Receive();
         connection.Send("QUIT\r\n");
         connection.Receive();
         connection.Disconnect();

         string log = ReadLogAfter("QUIT");

         StringAssert.Contains("CAPA", log);
         StringAssert.Contains("NOOP", log);
         StringAssert.Contains("QUIT", log);
      }
   }
}
