// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.IMAP
{
   /// <summary>
   ///    RFC 6855 section 4: once UTF8=ACCEPT is enabled, a client may send an
   ///    APPEND's message as "UTF8 (~{n}" with the closing ")" following the
   ///    octets. Thunderbird 128+ does this for every Sent copy. Until issue #53
   ///    the server refused the line before reading the message - the simple
   ///    command parser counts parentheses across the whole line - and answered
   ///    "BAD APPEND Command requires at least 2 parameter", so no Sent copy was
   ///    ever stored and the only workaround was per-machine client
   ///    configuration.
   /// </summary>
   [TestFixture]
   public class Utf8Append : TestFixtureBase
   {
      private Account _account;

      [SetUp]
      public new void SetUp()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "utf8append@example.test", "test");
      }

      private static ImapClientSimulator LogonAndEnableUtf8(Account account)
      {
         var imapSim = new ImapClientSimulator();
         imapSim.Connect();
         imapSim.Logon(account.Address, "test");

         string enabled = imapSim.SendSingleCommand("E01 ENABLE UTF8=ACCEPT");
         StringAssert.Contains("* ENABLED UTF8=ACCEPT", enabled,
            "UTF8=ACCEPT must be enabled before the wrapped form is used. Got: " + enabled);

         return imapSim;
      }

      [Test]
      [Description("The exact Thunderbird shape from issue #53: append \"Sent\" (\\Seen) UTF8 (~{n} is accepted and the copy is stored with its flag.")]
      public void TheThunderbirdSentCopyIsStored()
      {
         const string message = "Subject: sent copy\r\n\r\nSaved through UTF8 (~{n}).\r\n";

         var imapSim = LogonAndEnableUtf8(_account);
         imapSim.SendSingleCommand("A00 CREATE Sent");

         // Lower-case command word and the quoted mailbox, exactly as logged in the report.
         imapSim.SendRaw("A01 append \"Sent\" (\\Seen) UTF8 (~{" + message.Length + "}\r\n");
         string continuation = imapSim.ReceiveUntilAny("+ ", "A01 ");
         StringAssert.Contains("+ Ready", continuation,
            "The wrapped literal8 must be answered with a continuation like any other literal. Got: " + continuation);

         // The octets, then the ")" that closes the UTF8 wrapper, then the command's CRLF.
         imapSim.SendRaw(message + ")\r\n");
         string response = imapSim.ReceiveUntil("A01 ");
         StringAssert.Contains("A01 OK", response,
            "The APPEND must complete. Got: " + response);

         imapSim.SendRaw("A02 SELECT Sent\r\n");
         string select = imapSim.ReceiveUntil("A02 ");
         StringAssert.Contains("1 EXISTS", select, "The Sent copy must be in the mailbox. Got: " + select);

         imapSim.SendRaw("A03 FETCH 1 (FLAGS BODY.PEEK[TEXT])\r\n");
         string fetch = imapSim.ReceiveUntil("A03 ");
         StringAssert.Contains("Saved through UTF8 (~{n}).", fetch,
            "The stored message must be intact - the wrapper's \")\" must not have leaked into it. Got: " + fetch);
         StringAssert.Contains("\\Seen", fetch,
            "The flag list before the UTF8 wrapper must still be applied. Got: " + fetch);

         imapSim.Disconnect();
      }

      [Test]
      [Description("The non-synchronizing form UTF8 (~{n+}: octets and the closing \")\" arrive without a continuation.")]
      public void TheNonSynchronizingFormIsAccepted()
      {
         const string message = "Subject: literal-plus\r\n\r\nOne round trip.\r\n";

         var imapSim = LogonAndEnableUtf8(_account);

         imapSim.SendRaw("A01 APPEND INBOX UTF8 (~{" + message.Length + "+}\r\n" + message + ")\r\n");
         string response = imapSim.ReceiveUntil("A01 ");
         StringAssert.Contains("A01 OK", response,
            "The one-round-trip wrapped APPEND must complete. Got: " + response);

         imapSim.SendRaw("A02 STATUS INBOX (MESSAGES)\r\n");
         string status = imapSim.ReceiveUntil("A02 ");
         StringAssert.Contains("MESSAGES 1", status, "The message must be stored. Got: " + status);

         imapSim.Disconnect();
      }

      [Test]
      [Description("MULTIAPPEND: a later message of the same command may be wrapped too.")]
      public void ALaterMultiAppendMessageMayBeWrapped()
      {
         const string first = "Subject: first\r\n\r\nPlain literal.\r\n";
         const string second = "Subject: second\r\n\r\nWrapped literal8.\r\n";

         var imapSim = LogonAndEnableUtf8(_account);

         imapSim.SendRaw("A01 APPEND INBOX {" + first.Length + "}\r\n");
         string continuation = imapSim.ReceiveUntilAny("+ ", "A01 ");
         StringAssert.Contains("+ Ready", continuation, "No continuation for the first literal. Got: " + continuation);

         imapSim.SendRaw(first + " (\\Flagged) UTF8 (~{" + second.Length + "}\r\n");
         continuation = imapSim.ReceiveUntilAny("+ ", "A01 ");
         StringAssert.Contains("+ Ready", continuation, "No continuation for the wrapped second literal. Got: " + continuation);

         imapSim.SendRaw(second + ")\r\n");
         string response = imapSim.ReceiveUntil("A01 ");
         StringAssert.Contains("A01 OK", response,
            "The two-message APPEND must succeed with a single tagged OK. Got: " + response);

         imapSim.SendRaw("A02 STATUS INBOX (MESSAGES)\r\n");
         string status = imapSim.ReceiveUntil("A02 ");
         StringAssert.Contains("MESSAGES 2", status, "Both messages must be stored. Got: " + status);

         imapSim.SendRaw("A03 SELECT INBOX\r\n");
         imapSim.ReceiveUntil("A03 ");
         imapSim.SendRaw("A04 FETCH 2 (FLAGS BODY.PEEK[TEXT])\r\n");
         string fetch = imapSim.ReceiveUntil("A04 ");
         StringAssert.Contains("Wrapped literal8.", fetch, "The second message must be intact. Got: " + fetch);
         StringAssert.Contains("\\Flagged", fetch, "The second message's flags must be applied. Got: " + fetch);

         imapSim.Disconnect();
      }

      /// <summary>
      ///    The negative control for the framing: the ")" is part of the
      ///    protocol, not of the message. A client that opens the wrapper and
      ///    never closes it disagrees with the server about where its message
      ///    ended, and the answer has to be a refusal, not a stored message with
      ///    guessed boundaries.
      /// </summary>
      [Test]
      [Description("A UTF8 wrapper that is never closed is refused with BAD and nothing is stored.")]
      public void AnUnclosedWrapperIsRefused()
      {
         const string message = "Subject: unclosed\r\n\r\nNo closing parenthesis follows.\r\n";

         var imapSim = LogonAndEnableUtf8(_account);

         imapSim.SendRaw("A01 APPEND INBOX UTF8 (~{" + message.Length + "}\r\n");
         string continuation = imapSim.ReceiveUntilAny("+ ", "A01 ");
         StringAssert.Contains("+ Ready", continuation, "No continuation. Got: " + continuation);

         imapSim.SendRaw(message + "\r\n");
         string response = imapSim.ReceiveUntil("A01 ");
         StringAssert.Contains("A01 BAD", response,
            "An unclosed UTF8 wrapper must be refused. Got: " + response);

         imapSim.SendRaw("A02 STATUS INBOX (MESSAGES)\r\n");
         string status = imapSim.ReceiveUntil("A02 ");
         StringAssert.Contains("MESSAGES 0", status, "Nothing may be stored from a refused APPEND. Got: " + status);

         imapSim.Disconnect();
      }

      /// <summary>
      ///    The wrapper is recognised by its shape - the keyword, the "(", the
      ///    literal8 - not by the word UTF8 alone. A mailbox that happens to be
      ///    called UTF8 takes a plain literal exactly as any other mailbox does,
      ///    and "APPEND UTF8 (~{n}" with no mailbox at all is a malformed command
      ///    rather than a message filed somewhere by guesswork.
      /// </summary>
      [Test]
      [Description("A mailbox named UTF8 is not mistaken for the wrapper keyword, and a wrapper with no mailbox is refused.")]
      public void AMailboxNamedUtf8IsNotMistakenForTheWrapper()
      {
         const string message = "Subject: folder name\r\n\r\nThe folder is called UTF8.\r\n";

         var imapSim = LogonAndEnableUtf8(_account);
         imapSim.SendSingleCommand("A00 CREATE UTF8");

         // The plain literal8 form against the mailbox named UTF8.
         imapSim.SendRaw("A01 APPEND UTF8 ~{" + message.Length + "}\r\n");
         string continuation = imapSim.ReceiveUntilAny("+ ", "A01 ");
         StringAssert.Contains("+ Ready", continuation, "No continuation. Got: " + continuation);

         imapSim.SendRaw(message + "\r\n");
         string response = imapSim.ReceiveUntil("A01 ");
         StringAssert.Contains("A01 OK", response,
            "APPEND to a mailbox named UTF8 must work as the plain literal8 form. Got: " + response);

         imapSim.SendRaw("A02 STATUS UTF8 (MESSAGES)\r\n");
         string status = imapSim.ReceiveUntil("A02 ");
         StringAssert.Contains("MESSAGES 1", status, "The message must be in the UTF8 mailbox. Got: " + status);

         // The wrapper with the mailbox missing: nothing may be stored.
         imapSim.SendRaw("A03 APPEND UTF8 (~{" + message.Length + "}\r\n");
         response = imapSim.ReceiveUntilAny("+ ", "A03 ");
         StringAssert.Contains("A03 BAD", response,
            "A UTF8 wrapper with no mailbox before it is a malformed APPEND. Got: " + response);

         imapSim.Disconnect();
      }
   }
}
