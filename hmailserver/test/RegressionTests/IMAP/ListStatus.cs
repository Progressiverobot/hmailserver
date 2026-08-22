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
   ///    LIST-STATUS (RFC 5819). Without it a client syncing N mailboxes at
   ///    startup issues one STATUS per mailbox - N round trips; with it, LIST
   ///    RETURN (STATUS (items)) answers each listed selectable mailbox's STATUS
   ///    inline, immediately after its LIST line. The STATUS lines come from the
   ///    same builder the STATUS command uses, so the two can never disagree.
   /// </summary>
   [TestFixture]
   public class ListStatus : TestFixtureBase
   {
      private Account _account;

      [SetUp]
      public new void SetUp()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "liststatus@example.test", "test");
      }

      [Test]
      [Description("LIST RETURN (STATUS ...) answers each mailbox's STATUS inline, after its LIST line.")]
      public void ListReturnStatusAnswersInline()
      {
         SmtpClientSimulator.StaticSend("sender@example.test", _account.Address, "unread one", "Body");
         Pop3ClientSimulator.AssertMessageCount(_account.Address, "test", 1);

         var imapSim = new ImapClientSimulator();
         imapSim.Connect();
         imapSim.Logon(_account.Address, "test");
         imapSim.CreateFolder("Reports");

         imapSim.SendRaw("A02 LIST \"\" % RETURN (STATUS (MESSAGES UNSEEN))\r\n");
         var response = imapSim.ReceiveUntil("A02 OK");

         StringAssert.Contains("* STATUS \"INBOX\" (MESSAGES 1 UNSEEN 1)", response,
            "INBOX's STATUS must ride along with the LIST response. Got: " + response);
         StringAssert.Contains("* STATUS \"Reports\" (MESSAGES 0 UNSEEN 0)", response,
            "The empty folder's STATUS must ride along too. Got: " + response);

         // RFC 5819: the STATUS response follows the LIST line it belongs to.
         int listInbox = response.IndexOf("* LIST (\\HasNoChildren) \".\" \"INBOX\"");
         int statusInbox = response.IndexOf("* STATUS \"INBOX\"");
         ClassicAssert.IsTrue(listInbox >= 0,
            "The LIST line for INBOX must still be present. Got: " + response);
         ClassicAssert.IsTrue(statusInbox > listInbox,
            "INBOX's STATUS must come after INBOX's LIST line. Got: " + response);

         imapSim.Disconnect();
      }

      /// <summary>
      ///    The control: a LIST without the return option must not grow STATUS
      ///    lines, or every existing client pays for a feature it never asked
      ///    for - and a parser that unconditionally attached them would pass the
      ///    positive test above.
      /// </summary>
      [Test]
      [Description("A plain LIST carries no STATUS lines - the control.")]
      public void APlainListCarriesNoStatusLines()
      {
         var imapSim = new ImapClientSimulator();
         imapSim.Connect();
         imapSim.Logon(_account.Address, "test");

         imapSim.SendRaw("A02 LIST \"\" %\r\n");
         var response = imapSim.ReceiveUntil("A02 OK");

         ClassicAssert.IsFalse(response.Contains("* STATUS"),
            "A LIST without RETURN (STATUS ...) must not produce STATUS responses. Got: " + response);

         imapSim.Disconnect();
      }

      [Test]
      [Description("A STATUS return option without an item list is refused BAD.")]
      public void AStatusOptionWithoutItemsIsRefused()
      {
         var imapSim = new ImapClientSimulator();
         imapSim.Connect();
         imapSim.Logon(_account.Address, "test");

         imapSim.SendRaw("A02 LIST \"\" % RETURN (STATUS)\r\n");
         var response = imapSim.ReceiveUntil("A02 ");

         StringAssert.Contains("A02 BAD", response,
            "STATUS with no item list is a protocol error and must be refused. Got: " + response);

         imapSim.Disconnect();
      }

      [Test]
      [Description("CAPABILITY advertises LIST-STATUS.")]
      public void CapabilityAdvertisesListStatus()
      {
         var socket = new TcpConnection();
         ClassicAssert.IsTrue(socket.Connect(143), "Could not connect to the IMAP server on port 143.");
         socket.ReadUntil("* OK");

         socket.Send("A01 CAPABILITY\r\n");
         var capabilities = socket.ReadUntil("A01 OK");

         StringAssert.Contains("LIST-STATUS", capabilities,
            "CAPABILITY must advertise LIST-STATUS. Got: " + capabilities);

         socket.Disconnect();
      }
   }
}
