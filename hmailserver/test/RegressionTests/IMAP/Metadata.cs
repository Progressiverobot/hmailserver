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
   ///    METADATA (RFC 5464), over the hm_imap_metadata table schema 6014 added.
   ///    Annotations live on mailboxes or on the server itself (the "" mailbox);
   ///    /private entries are scoped to the owning account, /shared entries to
   ///    account 0 - one row visible to every session with access to the
   ///    folder. Values are capped at the column's 2048 characters, refused
   ///    with the RFC's MAXSIZE response code beyond it, and server-level
   ///    /shared entries are read-only: there is no administrator session over
   ///    IMAP to own them.
   /// </summary>
   [TestFixture]
   public class Metadata : TestFixtureBase
   {
      private Account _account;

      [SetUp]
      public new void SetUp()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "metadata@example.test", "test");
      }

      private ImapClientSimulator Logon()
      {
         var imapSim = new ImapClientSimulator();
         imapSim.Connect();
         imapSim.Logon(_account.Address, "test");
         return imapSim;
      }

      [Test]
      [Description("The round trip: SETMETADATA stores, GETMETADATA returns the value as a literal.")]
      public void AMailboxAnnotationRoundTrips()
      {
         var imapSim = Logon();

         imapSim.SendRaw("A01 SETMETADATA INBOX (/private/comment \"my private note\")\r\n");
         var response = imapSim.ReceiveUntil("A01 ");
         StringAssert.Contains("A01 OK", response, "SETMETADATA must succeed. Got: " + response);

         imapSim.SendRaw("A02 GETMETADATA INBOX /private/comment\r\n");
         response = imapSim.ReceiveUntil("A02 ");

         StringAssert.Contains("* METADATA \"INBOX\" (/private/comment {15}", response,
            "The value must come back as a literal on an untagged METADATA response. Got: " + response);
         StringAssert.Contains("my private note", response,
            "And it must be the stored value. Got: " + response);

         imapSim.Disconnect();
      }

      [Test]
      [Description("NIL removes the entry; a missing entry is answered NIL.")]
      public void NilRemovesAndMissingIsNil()
      {
         var imapSim = Logon();

         imapSim.SendRaw("A01 SETMETADATA INBOX (/private/comment \"temporary\")\r\n");
         imapSim.ReceiveUntil("A01 ");

         imapSim.SendRaw("A02 SETMETADATA INBOX (/private/comment NIL)\r\n");
         var response = imapSim.ReceiveUntil("A02 ");
         StringAssert.Contains("A02 OK", response, "Removing with NIL must succeed. Got: " + response);

         imapSim.SendRaw("A03 GETMETADATA INBOX /private/comment\r\n");
         response = imapSim.ReceiveUntil("A03 ");
         StringAssert.Contains("/private/comment NIL", response,
            "RFC 5464: an entry with no value is answered NIL. Got: " + response);

         imapSim.Disconnect();
      }

      [Test]
      [Description("Server annotations use the empty mailbox name.")]
      public void ServerAnnotationsUseTheEmptyMailboxName()
      {
         var imapSim = Logon();

         imapSim.SendRaw("A01 SETMETADATA \"\" (/private/vendor/hmailserver/note \"server-scoped\")\r\n");
         var response = imapSim.ReceiveUntil("A01 ");
         StringAssert.Contains("A01 OK", response, "A private server annotation must be settable. Got: " + response);

         imapSim.SendRaw("A02 GETMETADATA \"\" /private/vendor/hmailserver/note\r\n");
         response = imapSim.ReceiveUntil("A02 ");
         StringAssert.Contains("server-scoped", response,
            "The server annotation must round-trip. Got: " + response);

         // Server-level /shared entries are read-only: no administrator session
         // exists over IMAP to own them.
         imapSim.SendRaw("A03 SETMETADATA \"\" (/shared/comment \"nope\")\r\n");
         response = imapSim.ReceiveUntil("A03 ");
         StringAssert.Contains("A03 NO [NOPERM]", response,
            "Writing server-level shared metadata must be refused. Got: " + response);

         imapSim.Disconnect();
      }

      [Test]
      [Description("DEPTH infinity returns the entry and everything under it.")]
      public void DepthInfinityReturnsDescendants()
      {
         var imapSim = Logon();

         imapSim.SendRaw("A01 SETMETADATA INBOX (/private/filters/values/a \"first\" /private/filters/values/b \"second\")\r\n");
         var response = imapSim.ReceiveUntil("A01 ");
         StringAssert.Contains("A01 OK", response, "Setting two entries in one command must work. Got: " + response);

         imapSim.SendRaw("A02 GETMETADATA (DEPTH infinity) INBOX /private/filters\r\n");
         response = imapSim.ReceiveUntil("A02 ");

         StringAssert.Contains("/private/filters/values/a", response,
            "DEPTH infinity must return descendants. Got: " + response);
         StringAssert.Contains("/private/filters/values/b", response,
            "All of them. Got: " + response);

         imapSim.Disconnect();
      }

      [Test]
      [Description("An oversized value is refused with the MAXSIZE response code - the control.")]
      public void AnOversizedValueIsRefused()
      {
         var imapSim = Logon();

         imapSim.SendRaw("A01 SETMETADATA INBOX (/private/comment \"" + new string('x', 3000) + "\")\r\n");
         var response = imapSim.ReceiveUntil("A01 ");

         StringAssert.Contains("A01 NO [METADATA MAXSIZE 2048]", response,
            "A value over the cap is refused with the code that names the limit. Got: " + response);

         imapSim.Disconnect();
      }

      [Test]
      [Description("An entry outside /private and /shared is refused BAD.")]
      public void AnUnknownNamespaceIsRefused()
      {
         var imapSim = Logon();

         imapSim.SendRaw("A01 SETMETADATA INBOX (/bogus/comment \"x\")\r\n");
         var response = imapSim.ReceiveUntil("A01 ");
         StringAssert.Contains("A01 BAD", response,
            "Entry names must start /private/ or /shared/. Got: " + response);

         imapSim.Disconnect();
      }

      [Test]
      [Description("CAPABILITY advertises METADATA.")]
      public void CapabilityAdvertisesMetadata()
      {
         var socket = new TcpConnection();
         ClassicAssert.IsTrue(socket.Connect(143), "Could not connect to the IMAP server on port 143.");
         socket.ReadUntil("* OK");

         socket.Send("A01 CAPABILITY\r\n");
         var capabilities = socket.ReadUntil("A01 OK");

         StringAssert.Contains(" METADATA", capabilities,
            "CAPABILITY must advertise METADATA. Got: " + capabilities);

         socket.Disconnect();
      }
   }
}
