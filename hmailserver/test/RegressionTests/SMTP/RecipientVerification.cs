// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.SMTP
{
   /// <summary>
   /// RCPT TO answers a permanent 550 for an address that does not exist, and a
   /// temporary 451 when it could not find out. The two used to be the same reply,
   /// because a lookup that failed and a lookup that found nothing are reported
   /// identically inside the server - so a database briefly locked by a backup told
   /// the sending server that a valid mailbox did not exist, and the mail was
   /// bounced rather than retried.
   ///
   /// The distinction is made by a thread-local marker set when the database could
   /// not answer. These tests pin the far more common half: that an ordinary
   /// unknown address is still refused permanently, and a valid one is still
   /// accepted. Getting that wrong would be worse than the bug being fixed -
   /// answering 451 for genuinely unknown addresses would make every sender retry
   /// non-existent mailboxes for days.
   /// </summary>
   [TestFixture]
   public class RecipientVerification : TestFixtureBase
   {
      private static TcpConnection ConnectAndHelo()
      {
         var socket = new TcpConnection();
         Assert.IsTrue(socket.Connect(25));
         Assert.IsTrue(socket.Receive().StartsWith("220"));
         socket.SendAndReceive("HELO example.test\r\n");
         return socket;
      }

      [Test]
      [Description("An address that does not exist is still refused permanently")]
      public void UnknownRecipientIsRefusedPermanently()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         var socket = ConnectAndHelo();
         socket.SendAndReceive("MAIL FROM:<sender@example.test>\r\n");
         string result = socket.SendAndReceive("RCPT TO:<nosuchuser@example.test>\r\n");
         socket.Disconnect();

         Assert.IsTrue(result.StartsWith("550"), result);

         // Specifically not a 4xx: a permanent answer is what stops a sender
         // retrying an address that will never exist.
         Assert.IsFalse(result.StartsWith("4"), result);
      }

      [Test]
      [Description("A valid address is still accepted")]
      public void KnownRecipientIsAccepted()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         var socket = ConnectAndHelo();
         socket.SendAndReceive("MAIL FROM:<sender@example.test>\r\n");
         string result = socket.SendAndReceive("RCPT TO:<" + account.Address + ">\r\n");
         socket.Disconnect();

         Assert.IsTrue(result.StartsWith("250"), result);
      }

      [Test]
      [Description("A non-local domain gets a definitive answer, never a temporary deferral")]
      public void NonLocalDomainIsNotDeferred()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         var socket = ConnectAndHelo();
         socket.SendAndReceive("MAIL FROM:<sender@example.test>\r\n");
         string result = socket.SendAndReceive("RCPT TO:<someone@not-a-local-domain.test>\r\n");
         socket.Disconnect();

         // Whether this is accepted for relay or refused depends on the IP range
         // policy, and both are correct answers. What must never happen is a 4xx:
         // the delivery-possibility check consults the database, and a temporary
         // deferral here would mean the new "database did not answer" branch had
         // misfired on a perfectly good lookup.
         Assert.IsFalse(result.StartsWith("4"), result);
      }

      [Test]
      [Description("A whole message still passes through RCPT TO and is delivered")]
      public void AcceptedRecipientStillReceivesTheMessage()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Recipient verification", "Body");

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         string message = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");
         Assert.IsTrue(message.Contains("Recipient verification"), message);
      }
   }
}
