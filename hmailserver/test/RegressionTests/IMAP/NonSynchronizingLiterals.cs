// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.IMAP
{
   /// <summary>
   ///    Non-synchronizing literals (RFC 7888). The parsers always tolerated the
   ///    {n+} form - stripping the '+' and reading the octets - but the server
   ///    still answered every literal with a continuation the client never reads,
   ///    and never advertised the capability, so no client used the form. Now
   ///    LITERAL- is advertised (the variant that keeps non-synchronizing
   ///    literals at or under 4096 bytes, preserving the server's chance to
   ///    refuse an oversized APPEND with TOOBIG before the data moves) and the
   ///    continuation is suppressed exactly when the client said it is not
   ///    waiting for one.
   /// </summary>
   [TestFixture]
   public class NonSynchronizingLiterals : TestFixtureBase
   {
      [SetUp]
      public new void SetUp()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "literals@example.test", "test");
      }

      [Test]
      [Description("CAPABILITY advertises LITERAL-.")]
      public void CapabilityAdvertisesLiteralMinus()
      {
         var socket = new TcpConnection();
         ClassicAssert.IsTrue(socket.Connect(143), "Could not connect to the IMAP server on port 143.");
         socket.ReadUntil("* OK");

         socket.Send("A01 CAPABILITY\r\n");
         var capabilities = socket.ReadUntil("A01 OK");

         StringAssert.Contains(" LITERAL-", capabilities,
            "CAPABILITY must advertise LITERAL-. Got: " + capabilities);

         socket.Disconnect();
      }

      /// <summary>
      ///    The round trip the extension removes: LOGIN with both arguments as
      ///    {n+} literals, the whole command sent in one write with no reading
      ///    in between. The tagged OK must be the FIRST thing the server says -
      ///    a continuation sent for a literal the client never waited on would
      ///    sit in the stream ahead of it.
      /// </summary>
      [Test]
      [Description("A LOGIN sent entirely as {n+} literals completes with no continuation in the stream.")]
      public void LoginWithNonSynchronizingLiteralsGetsNoContinuation()
      {
         var socket = new TcpConnection();
         ClassicAssert.IsTrue(socket.Connect(143), "Could not connect to the IMAP server on port 143.");
         socket.ReadUntil("* OK");

         const string username = "literals@example.test";
         const string password = "test";

         socket.Send("A01 LOGIN {" + username.Length + "+}\r\n" +
                     username + " {" + password.Length + "+}\r\n" +
                     password + "\r\n");

         var response = socket.ReadUntil("A01 ");

         StringAssert.Contains("A01 OK", response,
            "The LOGIN built from {n+} literals must succeed. Got: " + response);
         ClassicAssert.IsFalse(response.Contains("+ Ready"),
            "No continuation may be sent for a non-synchronizing literal - the client is not " +
            "reading for one. Got: " + response);

         socket.Disconnect();
      }

      [Test]
      [Description("An APPEND whose message rides a {n+} literal delivers with no continuation.")]
      public void AppendWithNonSynchronizingLiteralGetsNoContinuation()
      {
         const string message = "From: literals@example.test\r\n" +
                                "Subject: nonsync\r\n" +
                                "\r\n" +
                                "Sent without waiting.\r\n";

         var socket = new TcpConnection();
         ClassicAssert.IsTrue(socket.Connect(143), "Could not connect to the IMAP server on port 143.");
         socket.ReadUntil("* OK");

         socket.Send("A01 LOGIN literals@example.test test\r\n");
         socket.ReadUntil("A01 OK");

         // Command, literal octets and closing CRLF in one write, nothing read
         // in between - the client-side behaviour LITERAL- licenses.
         socket.Send("A02 APPEND INBOX {" + message.Length + "+}\r\n" + message + "\r\n");

         var response = socket.ReadUntil("A02 ");

         StringAssert.Contains("A02 OK", response,
            "The APPEND with a non-synchronizing message literal must succeed. Got: " + response);
         ClassicAssert.IsFalse(response.Contains("+ Ready"),
            "No continuation may be sent for a non-synchronizing literal. Got: " + response);

         socket.Send("A03 STATUS INBOX (MESSAGES)\r\n");
         var status = socket.ReadUntil("A03 OK");
         StringAssert.Contains("MESSAGES 1", status,
            "The appended message must actually be in the mailbox. Got: " + status);

         socket.Disconnect();
      }

      /// <summary>
      ///    The control that keeps the suppression honest: the synchronizing form
      ///    must still be answered with a continuation, or every client that
      ///    never saw LITERAL- (and there is no other kind today) deadlocks
      ///    waiting for it. Basics.cs pins the same thing for APPEND.
      /// </summary>
      [Test]
      [Description("A synchronizing literal still receives its continuation - the control.")]
      public void ASynchronizingLiteralStillGetsTheContinuation()
      {
         var socket = new TcpConnection();
         ClassicAssert.IsTrue(socket.Connect(143), "Could not connect to the IMAP server on port 143.");
         socket.ReadUntil("* OK");

         const string username = "literals@example.test";

         socket.Send("A01 LOGIN {" + username.Length + "}\r\n");
         var continuation = socket.ReadUntil("+ ");

         StringAssert.Contains("+ Ready", continuation,
            "The synchronizing {n} form must still be answered with a continuation. Got: " + continuation);

         socket.Send(username + " \"test\"\r\n");
         var response = socket.ReadUntil("A01 ");
         StringAssert.Contains("A01 OK", response,
            "The synchronizing LOGIN must still complete. Got: " + response);

         socket.Disconnect();
      }
   }
}
