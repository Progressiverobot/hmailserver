// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.IMAP
{
   /// <summary>
   ///    SAVEDATE (RFC 8514), backed by the messagesavedate column schema 6013
   ///    added. The save date is when a message was saved into its current
   ///    mailbox - deliberately its own column, because INTERNALDATE rides
   ///    messagecreatetime, which IMAP COPY must preserve; the save date is the
   ///    one that goes fresh on every save. Existing rows were backfilled with
   ///    their create time, the best approximation available.
   /// </summary>
   [TestFixture]
   public class SaveDate : TestFixtureBase
   {
      private Account _account;

      [SetUp]
      public new void SetUp()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "savedate@example.test", "test");
      }

      /// <summary>
      ///    The decoupling that is the whole point: an APPEND with a 2008 date
      ///    gets INTERNALDATE 2008 - the client asked for that - and a SAVEDATE
      ///    of today, because today is when it was saved here. A SAVEDATE that
      ///    rode the create time would show 2008 for both and fail this.
      /// </summary>
      [Test]
      [Description("INTERNALDATE keeps the APPEND date; SAVEDATE is when it was actually saved.")]
      public void SaveDateIsDecoupledFromInternalDate()
      {
         const string message = "Subject: dated\r\n\r\nBody.\r\n";

         var imapSim = new ImapClientSimulator();
         imapSim.Connect();
         imapSim.Logon(_account.Address, "test");
         imapSim.SelectFolder("INBOX");

         imapSim.SendRaw("A01 APPEND INBOX \"22-Feb-2008 22:00:00 +0200\" {" + message.Length + "+}\r\n" + message + "\r\n");
         var response = imapSim.ReceiveUntil("A01 ");
         StringAssert.Contains("A01 OK", response, "The APPEND must succeed. Got: " + response);

         imapSim.SendRaw("A02 FETCH 1 (INTERNALDATE SAVEDATE)\r\n");
         var fetch = imapSim.ReceiveUntil("A02 OK");

         StringAssert.Contains("INTERNALDATE \"22-Feb-2008", fetch,
            "INTERNALDATE must keep the date the APPEND supplied. Got: " + fetch);
         StringAssert.Contains("SAVEDATE \"", fetch,
            "The SAVEDATE item must be answered. Got: " + fetch);
         StringAssert.Contains("-" + DateTime.Now.Year + " ", fetch,
            "SAVEDATE must be today - when the message was saved - not the APPEND date. Got: " + fetch);

         imapSim.Disconnect();
      }

      [Test]
      [Description("The three SAVED* search keys and SAVEDATESUPPORTED select by save date.")]
      public void TheSearchKeysSelectBySaveDate()
      {
         SmtpClientSimulator.StaticSend("sender@example.test", _account.Address, "searchable", "Body.");
         Pop3ClientSimulator.AssertMessageCount(_account.Address, "test", 1);

         var imapSim = new ImapClientSimulator();
         imapSim.Connect();
         imapSim.Logon(_account.Address, "test");
         imapSim.SelectFolder("INBOX");

         imapSim.SendRaw("A02 SEARCH SAVEDSINCE 1-Jan-2020\r\n");
         var since = imapSim.ReceiveUntil("A02 ");
         StringAssert.Contains("* SEARCH 1", since,
            "A message saved today is SAVEDSINCE 2020. Got: " + since);

         imapSim.SendRaw("A03 SEARCH SAVEDBEFORE 1-Jan-2020\r\n");
         var before = imapSim.ReceiveUntil("A03 ");
         ClassicAssert.IsFalse(before.Contains("* SEARCH 1"),
            "A message saved today is not SAVEDBEFORE 2020 - the control. Got: " + before);

         imapSim.SendRaw("A04 SEARCH SAVEDATESUPPORTED\r\n");
         var supported = imapSim.ReceiveUntil("A04 ");
         StringAssert.Contains("* SEARCH 1", supported,
            "Every mailbox records save dates after schema 6013, so SAVEDATESUPPORTED matches. Got: " + supported);

         imapSim.Disconnect();
      }

      [Test]
      [Description("A COPY gets a fresh save date while its INTERNALDATE is preserved.")]
      public void ACopyGetsAFreshSaveDate()
      {
         const string message = "Subject: to copy\r\n\r\nBody.\r\n";

         var imapSim = new ImapClientSimulator();
         imapSim.Connect();
         imapSim.Logon(_account.Address, "test");
         imapSim.SelectFolder("INBOX");
         imapSim.CreateFolder("Archive");

         imapSim.SendRaw("A01 APPEND INBOX \"22-Feb-2008 22:00:00 +0200\" {" + message.Length + "+}\r\n" + message + "\r\n");
         var response = imapSim.ReceiveUntil("A01 ");
         StringAssert.Contains("A01 OK", response);

         imapSim.SendRaw("A02 COPY 1 Archive\r\n");
         response = imapSim.ReceiveUntil("A02 ");
         StringAssert.Contains("A02 OK", response, "The COPY must succeed. Got: " + response);

         imapSim.SelectFolder("Archive");
         imapSim.SendRaw("A03 FETCH 1 (INTERNALDATE SAVEDATE)\r\n");
         var fetch = imapSim.ReceiveUntil("A03 OK");

         StringAssert.Contains("INTERNALDATE \"22-Feb-2008", fetch,
            "RFC 3501: COPY preserves INTERNALDATE. Got: " + fetch);
         StringAssert.Contains("-" + DateTime.Now.Year + " ", fetch,
            "RFC 8514: the copy was saved into Archive today, so its SAVEDATE is today. Got: " + fetch);

         imapSim.Disconnect();
      }

      [Test]
      [Description("CAPABILITY advertises SAVEDATE.")]
      public void CapabilityAdvertisesSaveDate()
      {
         var socket = new TcpConnection();
         ClassicAssert.IsTrue(socket.Connect(143), "Could not connect to the IMAP server on port 143.");
         socket.ReadUntil("* OK");

         socket.Send("A01 CAPABILITY\r\n");
         var capabilities = socket.ReadUntil("A01 OK");

         StringAssert.Contains(" SAVEDATE", capabilities,
            "CAPABILITY must advertise SAVEDATE. Got: " + capabilities);

         socket.Disconnect();
      }
   }
}
