// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System.Text;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.IMAP
{
   /// <summary>
   ///    RFC 9208 quota modernisation. GETQUOTA and GETQUOTAROOT existed, but
   ///    the capability was the bare RFC 2087 "QUOTA" atom with no resource
   ///    types, quota refusals carried no response code, SETQUOTA fell through
   ///    to "unknown command", and an account without a quota was answered with
   ///    "(STORAGE)" - a bare resource name RFC 9208's grammar does not permit.
   /// </summary>
   [TestFixture]
   public class QuotaResourceTypes : TestFixtureBase
   {
      private Account _account;

      [SetUp]
      public new void SetUp()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "quota9208@example.test", "test");
         _settings.IMAPQuotaEnabled = true;
      }

      [TearDown]
      public void TearDownQuota()
      {
         _settings.IMAPQuotaEnabled = true;
      }

      [Test]
      [Description("The capability names the storage resource; disabling quota removes both atoms.")]
      public void TheCapabilityNamesTheStorageResource()
      {
         var socket = new TcpConnection();
         ClassicAssert.IsTrue(socket.Connect(143), "Could not connect to the IMAP server on port 143.");
         socket.ReadUntil("* OK");

         socket.Send("A01 CAPABILITY\r\n");
         var capabilities = socket.ReadUntil("A01 OK");

         StringAssert.Contains("QUOTA=RES-STORAGE", capabilities,
            "RFC 9208 wants one QUOTA=RES-* atom per enforced resource. Got: " + capabilities);
         ClassicAssert.IsFalse(capabilities.Contains("QUOTA=RES-MESSAGE"),
            "No message-count quota is enforced, so RES-MESSAGE must not be promised. Got: " + capabilities);

         socket.Disconnect();

         // The control: with quota off, neither atom may appear.
         _settings.IMAPQuotaEnabled = false;

         var socket2 = new TcpConnection();
         ClassicAssert.IsTrue(socket2.Connect(143), "Could not reconnect to the IMAP server.");
         socket2.ReadUntil("* OK");
         socket2.Send("A01 CAPABILITY\r\n");
         var withoutQuota = socket2.ReadUntil("A01 OK");
         ClassicAssert.IsFalse(withoutQuota.Contains("QUOTA"),
            "With IMAP quota disabled no QUOTA atom may be advertised. Got: " + withoutQuota);
         socket2.Disconnect();
      }

      [Test]
      [Description("An APPEND the account has no room for is refused with the OVERQUOTA response code.")]
      public void AnOverQuotaAppendCarriesTheResponseCode()
      {
         _account.MaxSize = 1; // MB
         _account.Save();

         // Comfortably above the 1 MB account quota, comfortably below the
         // server's maximum message size - so the quota check is the one that
         // fires, not the size check.
         var message = new StringBuilder();
         message.Append("From: quota9208@example.test\r\n\r\n");
         message.Append(new string('x', 1500 * 1024));

         var imapSim = new ImapClientSimulator();
         imapSim.Connect();
         imapSim.Logon(_account.Address, "test");

         var result = imapSim.SendSingleCommandWithLiteral(
            "A01 APPEND INBOX {" + message.Length + "}", message.ToString());

         StringAssert.Contains("A01 NO [OVERQUOTA]", result,
            "The quota refusal must carry the OVERQUOTA response code (RFC 9208). Got: " + result);

         imapSim.Disconnect();
      }

      [Test]
      [Description("SETQUOTA is recognised and refused with an explanation, not left as an unknown command.")]
      public void SetQuotaIsRecognisedAndRefused()
      {
         var imapSim = new ImapClientSimulator();
         imapSim.Connect();
         imapSim.Logon(_account.Address, "test");

         imapSim.SendRaw("A01 SETQUOTA \"\" (STORAGE 512000)\r\n");
         var result = imapSim.ReceiveUntil("A01 ");

         StringAssert.Contains("A01 NO", result,
            "SETQUOTA must be refused NO, not answered BAD as an unknown command. Got: " + result);
         StringAssert.Contains("administered by the server administrator", result,
            "The refusal must say why - quotas are administrative configuration. Got: " + result);

         imapSim.Disconnect();
      }

      [Test]
      [Description("An account without a quota gets the empty resource list, not a bare resource name.")]
      public void AnUnlimitedAccountGetsTheEmptyList()
      {
         var imapSim = new ImapClientSimulator();
         imapSim.Connect();
         imapSim.Logon(_account.Address, "test");

         imapSim.SendRaw("A01 GETQUOTA \"\"\r\n");
         var result = imapSim.ReceiveUntil("A01 ");

         StringAssert.Contains("* QUOTA \"\" ()", result,
            "No quota means an empty resource list. Got: " + result);
         ClassicAssert.IsFalse(result.Contains("(STORAGE)"),
            "A bare resource name without usage and limit is not valid RFC 9208 grammar. Got: " + result);

         imapSim.Disconnect();
      }
   }
}
