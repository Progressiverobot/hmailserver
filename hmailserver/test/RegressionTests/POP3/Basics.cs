// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Mail;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Shared;
using Attachment = System.Net.Mail.Attachment;

namespace RegressionTests.POP3
{
   [TestFixture]
   public class Basics : TestFixtureBase
   {
      private string[] GetTestFiles()
      {
         var files = new List<string>();
         var a = Assembly.GetExecutingAssembly();
         files.Add(a.Location);

         // create a file with a lot of dots.
         var sb = new StringBuilder();
         for (var i = 0; i < 10000; i++) sb.Append("....................");

         var tempFile = Path.GetTempFileName() + ".txt";
         File.WriteAllText(tempFile, sb.ToString());
         files.Add(tempFile);

         return files.ToArray();
      }

      [Test]
      [Description("Security: with the per-IP auto-ban disabled, a single POP3 connection must still be disconnected after the per-connection authentication-failure cap (defense-in-depth brute-force protection).")]
      public void TestPerConnectionLoginFailureCapDisconnects()
      {
         var settings = SingletonProvider<TestSetup>.Instance.GetApp().Settings;
         bool originalAutoBan = settings.AutoBanOnLogonFailure;
         settings.AutoBanOnLogonFailure = false;
         settings.ClearLogonFailureList();

         try
         {
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "cappop3@example.test", "secret");

            var tc = new TcpConnection();
            Assert.IsTrue(tc.Connect(110));
            tc.ReadUntil("+OK"); // banner

            string last = "";
            bool disconnected = false;
            for (int i = 1; i <= 12; i++)
            {
               try
               {
                  tc.Send("USER cappop3@example.test\r\n");
                  tc.ReadUntil("+OK");
                  tc.Send("PASS wrongpassword\r\n");
                  last = tc.Receive();
               }
               catch (Exception)
               {
                  disconnected = true;
                  break;
               }

               if (last.Contains("Too many invalid logon attempts"))
               {
                  disconnected = true;
                  break;
               }
            }

            Assert.IsTrue(disconnected,
               "The POP3 connection should have been disconnected after the per-connection authentication-failure cap. Last response: " + last);

            tc.Disconnect();
         }
         finally
         {
            settings.AutoBanOnLogonFailure = originalAutoBan;
            settings.ClearLogonFailureList();
         }
      }

      [Test]
      [Description("RFC 5034: CAPA advertises SASL PLAIN SCRAM-SHA-256.")]
      public void TestSaslAdvertised()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "saslcapa@example.test", "test");

         var tc = new TcpConnection();
         Assert.IsTrue(tc.Connect(110));
         tc.ReadUntil("+OK"); // banner
         tc.Send("CAPA\r\n");
         string caps = tc.Receive();
         Assert.IsTrue(caps.Contains("SASL PLAIN SCRAM-SHA-256"),
            "CAPA should advertise SASL PLAIN SCRAM-SHA-256. Got: " + caps);
         tc.Send("QUIT\r\n");
         tc.Disconnect();
      }

      [Test]
      [Description("RFC 5034 SASL PLAIN over POP3 authenticates a local account.")]
      public void TestAuthPlainAuthenticates()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "saslplain@example.test", "SeC-r3t Pass!");

         var tc = new TcpConnection();
         Assert.IsTrue(tc.Connect(110));
         tc.ReadUntil("+OK"); // banner

         string final = Pop3SaslTestClient.AuthenticatePlain(tc, "saslplain@example.test", "SeC-r3t Pass!");
         Assert.IsTrue(final.StartsWith("+OK"),
            "SASL PLAIN authentication should succeed. Got: " + final);

         // The session must be in TRANSACTION state and usable.
         tc.Send("STAT\r\n");
         Assert.IsTrue(tc.Receive().StartsWith("+OK"), "Session should be usable after AUTH PLAIN.");
         tc.Send("QUIT\r\n");
         tc.Disconnect();
      }

      [Test]
      [Description("RFC 5802/7677 (SCRAM-SHA-256) over POP3 (RFC 5034): a full SCRAM exchange " +
                   "authenticates a PBKDF2-hashed account and the server proves it knows the key.")]
      public void TestScramSha256Authenticates()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "scrampop@example.test", "SeC-r3t Pass!");

         var tc = new TcpConnection();
         Assert.IsTrue(tc.Connect(110));
         tc.ReadUntil("+OK"); // banner

         string final = Pop3SaslTestClient.AuthenticateScram(tc, "scrampop@example.test", "SeC-r3t Pass!");
         Assert.IsTrue(final.StartsWith("+OK"),
            "SCRAM-SHA-256 authentication should succeed. Got: " + final);

         tc.Send("STAT\r\n");
         Assert.IsTrue(tc.Receive().StartsWith("+OK"), "Session should be usable after SCRAM logon.");
         tc.Send("QUIT\r\n");
         tc.Disconnect();
      }

      [Test]
      [Description("RFC 5802/7677 (SCRAM-SHA-256) over POP3: a wrong password produces an invalid " +
                   "client proof and the server rejects the exchange without authenticating.")]
      public void TestScramSha256WrongPasswordFails()
      {
         var settings = SingletonProvider<TestSetup>.Instance.GetApp().Settings;
         bool originalAutoBan = settings.AutoBanOnLogonFailure;
         settings.AutoBanOnLogonFailure = false;
         settings.ClearLogonFailureList();
         try
         {
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "scrambadpop@example.test", "correct horse");

            var tc = new TcpConnection();
            Assert.IsTrue(tc.Connect(110));
            tc.ReadUntil("+OK"); // banner

            string final = Pop3SaslTestClient.AuthenticateScram(tc, "scrambadpop@example.test", "wrong password");
            Assert.IsTrue(final.StartsWith("-ERR"),
               "SCRAM-SHA-256 with a wrong password must be rejected. Got: " + final);
            tc.Disconnect();
         }
         finally
         {
            settings.AutoBanOnLogonFailure = originalAutoBan;
            settings.ClearLogonFailureList();
         }
      }

      [Test]
      [Description("Test to send a number of attachments...")]
      public void TestAttachmentEncoding()
      {
         var testFiles = GetTestFiles();

         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         foreach (var testFile in testFiles)
         {
            Trace.WriteLine(testFile);

            var fileHash = GetFileHash(testFile);

            var mail = new MailMessage();
            mail.From = new MailAddress("test@example.test");
            mail.To.Add("test@example.test");
            mail.Subject = "Test";
            mail.Attachments.Add(new Attachment(testFile));

            SendMessage(mail);

            Pop3ClientSimulator.AssertMessageCount("test@example.test", "test", 1);

            var sim = new Pop3ClientSimulator();
            sim.ConnectAndLogon("test@example.test", "test");
            var fileContent = sim.RETR(1);
            sim.DELE(1);
            sim.QUIT();


            var message = new Message();

            try
            {
               File.WriteAllText(message.Filename, fileContent);
               message.RefreshContent();

               message.Attachments[0].SaveAs(message.Filename);
               var fileHashAfter = GetFileHash(message.Filename);

               Assert.AreEqual(fileHash, fileHashAfter);
            }
            finally
            {
               File.Delete(message.Filename);
            }
         }
      }

      [Test]
      public void TestConnection()
      {
         // Create a test account
         // Fetch the default domain
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "pop3user@example.test", "test");

         // Send 5 messages to this account.
         var smtpClientSimulator = new SmtpClientSimulator();
         for (var i = 0; i < 5; i++)
            smtpClientSimulator.Send("test@example.test", "pop3user@example.test", "INBOX", "POP3 test message");


         Pop3ClientSimulator.AssertMessageCount("pop3user@example.test", "test", 5);
      }

      [Test]
      public void TestDELEInvalid()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         for (var i = 1; i <= 10; i++)
            SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "TestBody" + i);

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 10);

         var sim = new Pop3ClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         Assert.IsFalse(sim.DELE(0));
         Assert.IsFalse(sim.DELE(-1));
         Assert.IsFalse(sim.DELE(1000));
         Assert.IsTrue(sim.DELE(5));
      }


      [Test]
      public void TestLIST()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "TestBody1");
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "TestBody2");
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "TestBody3");

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 3);

         var sim = new Pop3ClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         var result = sim.LIST();

         Assert.IsTrue(result.Contains("3 messages"));
         Assert.IsTrue(result.Contains("\r\n1"));
         Assert.IsTrue(result.Contains("\r\n2"));
         Assert.IsTrue(result.Contains("\r\n3"));
         Assert.IsTrue(result.Contains("\r\n."));
      }

      [Test]
      public void TestLISTInvalid()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         for (var i = 1; i <= 10; i++)
            SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "TestBody" + i);

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 10);

         var sim = new Pop3ClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         var result = sim.LIST(0);
         Assert.IsTrue(result.Contains("No such message"));
         result = sim.LIST(-1);
         Assert.IsTrue(result.Contains("No such message"));
         result = sim.LIST(100);
         Assert.IsTrue(result.Contains("No such message"));
      }

      [Test]
      public void TestLISTSpecific()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "TestBody1");
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "TestBody2");
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "TestBody3");

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 3);

         var sim = new Pop3ClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         var result = sim.LIST(2);

         Assert.IsTrue(result.Contains("OK 2"));

         result = sim.LIST(3);
         Assert.IsTrue(result.Contains("OK 3"));
      }

      [Test]
      public void TestLISTWithDeleted()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         for (var i = 1; i <= 10; i++)
            SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "TestBody" + i);

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 10);

         var sim = new Pop3ClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.DELE(2);
         sim.DELE(4);
         var result = sim.LIST();

         Assert.IsTrue(result.Contains("8 messages"));
         Assert.IsTrue(result.Contains("\r\n1"));
         Assert.IsTrue(result.Contains("\r\n3"));
         Assert.IsTrue(result.Contains("\r\n5"));
         Assert.IsTrue(result.Contains("\r\n."));
      }

      [Test]
      [Description("Test to log on a mailbox containing a message which has been marked as deleted using IMAP")]
      public void TestLogonMailboxWithDeletedMessage()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         for (var i = 1; i <= 3; i++)
            SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test",
               "Line1\r\nLine2\r\nLine3\r\nLine4\r\nLine\r\n");

         // Mark the second message as deleted using IMAP.
         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 3);

         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.SelectFolder("INBOX");
         sim.SetDeletedFlag(2);
         sim.Disconnect();

         // Now list messages and confirm that all are listed.

         var pop3Client = new Pop3ClientSimulator();
         pop3Client.ConnectAndLogon(account.Address, "test");
         var listResponse = pop3Client.LIST();
         var uidlResponse = pop3Client.UIDL();

         Assert.IsTrue(listResponse.Contains("\r\n1"));
         Assert.IsTrue(listResponse.Contains("\r\n2"));
         Assert.IsTrue(listResponse.Contains("\r\n3"));
         Assert.IsTrue(listResponse.Contains("\r\n.\r\n"));
         Assert.IsTrue(listResponse.Contains("3 messages"));

         Assert.IsTrue(uidlResponse.Contains("\r\n1"));
         Assert.IsTrue(uidlResponse.Contains("\r\n2"));
         Assert.IsTrue(uidlResponse.Contains("\r\n3"));
         Assert.IsTrue(uidlResponse.Contains("\r\n.\r\n"));
         Assert.IsTrue(uidlResponse.Contains("3 messages"));
      }

      [Test]
      public void TestPOP3TransactionSafety()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "TestBody");
         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         var sim = new Pop3ClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");

         // Now delete the message using an IMAP client.
         var imapSimulator = new ImapClientSimulator();
         Assert.IsTrue(imapSimulator.ConnectAndLogon(account.Address, "test"));
         Assert.IsTrue(imapSimulator.SelectFolder("INBOX"));
         Assert.IsTrue(imapSimulator.SetDeletedFlag(1));
         Assert.IsTrue(imapSimulator.Expunge());
         Assert.AreEqual(0, imapSimulator.GetMessageCount("Inbox"));

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "TestBody");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         // This deletion should not have any effect, since the POP3 connection is referencing an old message.
         sim.DELE(1);
         sim.QUIT();

         Assert.AreEqual(1, imapSimulator.GetMessageCount("Inbox"));
      }

      [Test]
      public void TestRETR()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "TestBody1");
         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "TestBody2");
         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 2);

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "TestBody3");
         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 3);

         var sim = new Pop3ClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         var result = sim.RETR(1);
         Assert.IsTrue(result.Contains("TestBody1"), result);
         result = sim.RETR(2);
         Assert.IsTrue(result.Contains("TestBody2"), result);
         result = sim.RETR(3);
         Assert.IsTrue(result.Contains("TestBody3"), result);

         Assert.IsFalse(result.Contains(".\r\n."));
      }


      [Test]
      public void TestTOPInvalid()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         for (var i = 1; i <= 10; i++)
            SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "TestBody" + i);

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 10);

         var sim = new Pop3ClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         Assert.IsTrue(sim.TOP(-1, 0).Contains("No such message"));
         Assert.IsTrue(sim.TOP(0, 0).Contains("No such message"));
         Assert.IsTrue(sim.TOP(100, 0).Contains("No such message"));
      }

      [Test]
      public void TestTOPSpecificEntire()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         for (var i = 1; i <= 10; i++)
            SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "TestBody" + i);

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 10);

         var sim = new Pop3ClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         var result = sim.TOP(1, 0);

         Assert.IsTrue(result.Contains("Received"));
         Assert.IsTrue(result.Contains("Subject"));
      }

      [Test]
      public void TestTOPSpecificPartial()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         for (var i = 1; i <= 10; i++)
            SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test",
               "Line1\r\nLine2\r\nLine3\r\nLine4\r\nLine\r\n");

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 10);

         var sim = new Pop3ClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         var result = sim.TOP(4, 2);

         Assert.IsTrue(result.Contains("Received"));
         Assert.IsTrue(result.Contains("Line1"));
         Assert.IsTrue(result.Contains("Line2"));
         Assert.IsFalse(result.Contains("Line3"));
         Assert.IsFalse(result.Contains("Line4"));
      }

      [Test]
      public void TestTopDotOnOtherwiseEmptyLineShouldBeEscaped()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test",
            "Line1\r\nLine2\r\n..\r\nLine4\r\n..A\r\n.B\r\nLine6\r\n");

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         var sim = new Pop3ClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         var result = sim.TOP(1, 100);

         Assert.IsTrue(result.Contains("Line1\r\nLine2\r\n..\r\nLine4\r\n..A\r\nB\r\nLine6\r\n"), result);
      }

      [Test]
      public void TestUIDLInvalid()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         for (var i = 1; i <= 10; i++)
            SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "TestBody" + i);

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 10);

         var sim = new Pop3ClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         var result = sim.UIDL(0);
         Assert.IsTrue(result.Contains("No such message"));
         result = sim.UIDL(-1);
         Assert.IsTrue(result.Contains("No such message"));
         result = sim.UIDL(100);
         Assert.IsTrue(result.Contains("No such message"));
      }

      [Test]
      public void TestUIDLSpecific()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "TestBody1");
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "TestBody2");
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "TestBody3");

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 3);

         var sim = new Pop3ClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         var result = sim.UIDL(2);

         Assert.IsTrue(result.Contains("OK 2"));

         result = sim.UIDL(3);
         Assert.IsTrue(result.Contains("OK 3"));
      }

      [Test]
      public void TestUIDLWithDeleted()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         for (var i = 1; i <= 10; i++)
            SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "TestBody" + i);

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 10);

         var sim = new Pop3ClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.DELE(2);
         sim.DELE(4);
         var result = sim.UIDL();

         Assert.IsTrue(result.Contains("8 messages"));
         Assert.IsTrue(result.Contains("\r\n1"));
         Assert.IsTrue(result.Contains("\r\n3"));
         Assert.IsTrue(result.Contains("\r\n5"));
         Assert.IsTrue(result.Contains("\r\n."));
      }

      [Test]
      public void WelcomeMessage()
      {
         SingletonProvider<TestSetup>.Instance.GetApp().Settings.WelcomePOP3 = "HOWDYHO POP3";

         var simulator = new Pop3ClientSimulator();

         var sWelcomeMessage = simulator.GetWelcomeMessage();

         if (sWelcomeMessage != "+OK HOWDYHO POP3\r\n")
            throw new Exception("ERROR - Wrong welcome message.");
      }


      public static void SendMessage(MailMessage mailMessage)
      {
         for (var i = 0; i < 5; i++)
            try
            {
               var client = new SmtpClient("localhost", 25);
               client.Send(mailMessage);

               return;
            }
            catch
            {
               if (i == 4)
                  throw;
            }
      }


      public static string GetFileHash(string fileName)
      {
         var bytes = File.ReadAllBytes(fileName);
         SHA1 sha = new SHA1CryptoServiceProvider();
         var hash = new StringBuilder();

         var hashedData = sha.ComputeHash(bytes);

         foreach (var b in hashedData) hash.Append(string.Format("{0,2:X2}", b));

         //return the hashed value
         return hash.ToString();
      }
   }

   /// <summary>
   ///    A minimal SASL client for POP3 (RFC 5034) used to exercise the server's AUTH
   ///    PLAIN and AUTH SCRAM-SHA-256 (RFC 5802 / RFC 7677) implementation over a raw
   ///    connection that has already read the +OK banner.
   /// </summary>
   internal static class Pop3SaslTestClient
   {
      /// <summary>Runs AUTH PLAIN (no initial response). Returns the final +OK/-ERR line.</summary>
      public static string AuthenticatePlain(TcpConnection con, string username, string password)
      {
         con.Send("AUTH PLAIN\r\n");
         string challenge = con.Receive();
         Assert.IsTrue(challenge.TrimStart().StartsWith("+"),
            "Expected a continuation for AUTH PLAIN. Got: " + challenge);

         string plain = "\0" + username + "\0" + password;
         con.Send(Convert.ToBase64String(Encoding.UTF8.GetBytes(plain)) + "\r\n");
         return con.Receive();
      }

      /// <summary>
      ///    Runs a full SCRAM-SHA-256 exchange (no SASL-IR). Returns the final
      ///    +OK on success or the -ERR rejection on a bad proof.
      /// </summary>
      public static string AuthenticateScram(TcpConnection con, string username, string password)
      {
         var nonceBytes = new byte[18];
         using (var rng = RandomNumberGenerator.Create())
            rng.GetBytes(nonceBytes);
         string clientNonce = Convert.ToBase64String(nonceBytes);

         string clientFirstBare = "n=" + SaslName(username) + ",r=" + clientNonce;
         string clientFirst = "n,," + clientFirstBare;

         con.Send("AUTH SCRAM-SHA-256\r\n");
         string challenge = con.Receive();
         Assert.IsTrue(challenge.TrimStart().StartsWith("+"),
            "Expected an empty SCRAM continuation. Got: " + challenge);

         con.Send(Base64(clientFirst) + "\r\n");
         string serverFirstLine = con.Receive();
         if (!serverFirstLine.TrimStart().StartsWith("+"))
            return serverFirstLine; // protocol error
         string serverFirst = DecodeContinuation(serverFirstLine);

         string combinedNonce = Attribute(serverFirst, "r");
         byte[] salt = Convert.FromBase64String(Attribute(serverFirst, "s"));
         int iterations = int.Parse(Attribute(serverFirst, "i"));
         Assert.IsTrue(combinedNonce.StartsWith(clientNonce),
            "Server nonce must start with the client nonce. Got: " + combinedNonce);

         byte[] saltedPassword;
         using (var pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256))
            saltedPassword = pbkdf2.GetBytes(32);

         byte[] clientKey = Hmac(saltedPassword, "Client Key");
         byte[] storedKey = Sha256(clientKey);

         string clientFinalWithoutProof = "c=biws,r=" + combinedNonce;
         string authMessage = clientFirstBare + "," + serverFirst + "," + clientFinalWithoutProof;
         byte[] clientSignature = Hmac(storedKey, authMessage);
         byte[] clientProof = Xor(clientKey, clientSignature);

         string clientFinal = clientFinalWithoutProof + ",p=" + Convert.ToBase64String(clientProof);
         con.Send(Base64(clientFinal) + "\r\n");

         string afterFinal = con.Receive();
         if (!afterFinal.TrimStart().StartsWith("+"))
            return afterFinal; // rejected proof (-ERR)

         // Verify the server proved knowledge of the key (ServerSignature).
         string serverFinal = DecodeContinuation(afterFinal);
         byte[] serverKey = Hmac(saltedPassword, "Server Key");
         byte[] serverSignature = Hmac(serverKey, authMessage);
         Assert.AreEqual("v=" + Convert.ToBase64String(serverSignature), serverFinal,
            "Server signature (v=) did not verify.");

         // Empty client response acknowledges the server-final; server completes auth.
         con.Send("\r\n");
         return con.Receive();
      }

      private static string SaslName(string name)
      {
         return name.Replace("=", "=3D").Replace(",", "=2C");
      }

      private static string Base64(string s)
      {
         return Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
      }

      private static string DecodeContinuation(string line)
      {
         string t = line.Trim();
         if (t.StartsWith("+"))
            t = t.Substring(1).Trim();
         if (t.Length == 0)
            return "";
         return Encoding.UTF8.GetString(Convert.FromBase64String(t));
      }

      private static string Attribute(string message, string key)
      {
         foreach (var part in message.Split(','))
            if (part.StartsWith(key + "="))
               return part.Substring(key.Length + 1);
         return "";
      }

      private static byte[] Hmac(byte[] key, string data)
      {
         using (var h = new HMACSHA256(key))
            return h.ComputeHash(Encoding.ASCII.GetBytes(data));
      }

      private static byte[] Sha256(byte[] data)
      {
         using (var sha = SHA256.Create())
            return sha.ComputeHash(data);
      }

      private static byte[] Xor(byte[] a, byte[] b)
      {
         var r = new byte[a.Length];
         for (int i = 0; i < a.Length; i++)
            r[i] = (byte) (a[i] ^ b[i]);
         return r;
      }
   }
}