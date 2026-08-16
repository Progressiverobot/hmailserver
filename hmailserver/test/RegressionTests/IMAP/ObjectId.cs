// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System.Text.RegularExpressions;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.IMAP
{
   /// <summary>
   ///    OBJECTID (RFC 8474), on schema 6015. The email id is stamped at a
   ///    message's first save and - the exact opposite of the save date -
   ///    deliberately carried by copies: the same content keeps the same
   ///    EMAILID wherever it goes, which is what lets a client recognise a
   ///    message it has already downloaded when it reappears in another folder.
   ///    The mailbox id is the folder's database id, which survives RENAME.
   ///    THREADID is answered NIL: this server has no thread ids, and the RFC
   ///    provides for saying so rather than inventing one.
   /// </summary>
   [TestFixture]
   public class ObjectId : TestFixtureBase
   {
      private Account _account;

      [SetUp]
      public new void SetUp()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "objectid@example.test", "test");
      }

      [Test]
      [Description("A COPY keeps the EMAILID while the UID changes - the id the RFC promises.")]
      public void ACopyKeepsTheEmailId()
      {
         SmtpClientSimulator.StaticSend("sender@example.test", _account.Address, "stable id", "Body.");
         Pop3ClientSimulator.AssertMessageCount(_account.Address, "test", 1);

         var imapSim = new ImapClientSimulator();
         imapSim.Connect();
         imapSim.Logon(_account.Address, "test");
         imapSim.SelectFolder("INBOX");
         imapSim.CreateFolder("Archive");

         imapSim.SendRaw("A02 FETCH 1 (UID EMAILID)\r\n");
         var original = imapSim.ReceiveUntil("A02 OK");

         var originalId = Regex.Match(original, @"EMAILID \((M[A-Za-z0-9]+)\)");
         ClassicAssert.IsTrue(originalId.Success, "No EMAILID on the original. Got: " + original);

         imapSim.SendRaw("A03 COPY 1 Archive\r\n");
         imapSim.ReceiveUntil("A03 ");

         imapSim.SelectFolder("Archive");
         imapSim.SendRaw("A04 FETCH 1 (UID EMAILID)\r\n");
         var copy = imapSim.ReceiveUntil("A04 OK");

         StringAssert.Contains("EMAILID (" + originalId.Groups[1].Value + ")", copy,
            "RFC 8474: the copy is the same content and must keep the EMAILID. Got: " + copy);

         imapSim.Disconnect();
      }

      [Test]
      [Description("MAILBOXID appears on SELECT and STATUS, and survives a RENAME - the control the RFC cares about.")]
      public void TheMailboxIdSurvivesRename()
      {
         var imapSim = new ImapClientSimulator();
         imapSim.Connect();
         imapSim.Logon(_account.Address, "test");

         imapSim.SendRaw("A01 CREATE Projects\r\n");
         var created = imapSim.ReceiveUntil("A01 ");
         var createdId = Regex.Match(created, @"\[MAILBOXID \((F\d+)\)\]");
         ClassicAssert.IsTrue(createdId.Success,
            "CREATE must answer with the new mailbox's id. Got: " + created);

         imapSim.SendRaw("A02 SELECT Projects\r\n");
         var selected = imapSim.ReceiveUntil("A02 ");
         StringAssert.Contains("[MAILBOXID (" + createdId.Groups[1].Value + ")]", selected,
            "SELECT reports the same id CREATE announced. Got: " + selected);

         imapSim.SendRaw("A03 RENAME Projects Clients\r\n");
         imapSim.ReceiveUntil("A03 ");

         imapSim.SendRaw("A04 STATUS Clients (MAILBOXID)\r\n");
         var status = imapSim.ReceiveUntil("A04 ");
         StringAssert.Contains("MAILBOXID (" + createdId.Groups[1].Value + ")", status,
            "RFC 8474: the id is the mailbox's identity, so a RENAME must not change it. Got: " + status);

         imapSim.Disconnect();
      }

      [Test]
      [Description("SEARCH EMAILID finds the message; THREADID is answered NIL and matches nothing.")]
      public void SearchByEmailIdAndNilThreadId()
      {
         SmtpClientSimulator.StaticSend("sender@example.test", _account.Address, "searchable", "Body.");
         Pop3ClientSimulator.AssertMessageCount(_account.Address, "test", 1);

         var imapSim = new ImapClientSimulator();
         imapSim.Connect();
         imapSim.Logon(_account.Address, "test");
         imapSim.SelectFolder("INBOX");

         imapSim.SendRaw("A02 FETCH 1 (EMAILID THREADID)\r\n");
         var fetch = imapSim.ReceiveUntil("A02 OK");

         StringAssert.Contains("THREADID NIL", fetch,
            "No thread ids exist, and the RFC provides NIL for exactly that. Got: " + fetch);

         var emailId = Regex.Match(fetch, @"EMAILID \((M[A-Za-z0-9]+)\)");
         ClassicAssert.IsTrue(emailId.Success, "No EMAILID in the FETCH. Got: " + fetch);

         imapSim.SendRaw("A03 SEARCH EMAILID " + emailId.Groups[1].Value + "\r\n");
         var found = imapSim.ReceiveUntil("A03 ");
         StringAssert.Contains("* SEARCH 1", found,
            "SEARCH EMAILID must find the message by its id. Got: " + found);

         imapSim.SendRaw("A04 SEARCH EMAILID Mdoesnotexist\r\n");
         var notFound = imapSim.ReceiveUntil("A04 ");
         ClassicAssert.IsFalse(notFound.Contains("* SEARCH 1"),
            "A nonexistent id matches nothing - the control. Got: " + notFound);

         imapSim.Disconnect();
      }

      [Test]
      [Description("CAPABILITY advertises OBJECTID.")]
      public void CapabilityAdvertisesObjectId()
      {
         var socket = new TcpConnection();
         ClassicAssert.IsTrue(socket.Connect(143), "Could not connect to the IMAP server on port 143.");
         socket.ReadUntil("* OK");

         socket.Send("A01 CAPABILITY\r\n");
         var capabilities = socket.ReadUntil("A01 OK");

         StringAssert.Contains(" OBJECTID", capabilities,
            "CAPABILITY must advertise OBJECTID. Got: " + capabilities);

         socket.Disconnect();
      }
   }
}
