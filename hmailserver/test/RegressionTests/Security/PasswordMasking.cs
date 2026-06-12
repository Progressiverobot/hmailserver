// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.Security
{
   [TestFixture]
   public class PasswordMasking : TestFixtureBase
   {
      [SetUp]
      public new void SetUp()
      {
         _status = SingletonProvider<TestSetup>.Instance.GetApp().Status;
         var logFile = _settings.Logging.CurrentDefaultLog;
         if (File.Exists(logFile))
            File.Delete(logFile);
      }

      private Status _status;

      private const string _username = "NonSecretUser@example.test";
      private const string _password = "SecretPassword";

      private string EncodeBase64(string s)
      {
         var bytes = Encoding.UTF8.GetBytes(s);
         return Convert.ToBase64String(bytes);
      }

      private string GetUsername()
      {
         return _username;
      }

      private string GetPassword()
      {
         return _password;
      }


      private void EnsureNoPassword(bool usernameExpected = true)
      {
         var logFileName = _settings.Logging.CurrentDefaultLog;

         for (var i = 1; i <= 10; i++)
         {
            var text = string.Empty;

            try
            {
               using (var fileStream = File.Open(logFileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
               using (var streamReader = new StreamReader(fileStream))
               {
                  text = streamReader.ReadToEnd();
               }
            }
            catch
            {
               if (i == 10)
                  throw;

               Thread.Sleep(1000);
               continue;
            }

            if (usernameExpected)
               Assert.IsTrue(text.Contains(_username) || text.Contains(EncodeBase64(_username)), text);
            else
               Assert.IsFalse(text.Contains(_username) || text.Contains(EncodeBase64(_username)), text);

            Assert.IsFalse(text.Contains(_password) || text.Contains(EncodeBase64(_password)), text);
            Assert.IsTrue(text.Contains("***"), text);
         }
      }

      internal Route AddRoutePointingAtLocalhostWithAuth(int numberOfTries, int port)
      {
         // Add a route pointing at localhost
         var settings = SingletonProvider<TestSetup>.Instance.GetApp().Settings;

         var route = settings.Routes.Add();
         route.DomainName = "dummy-example.com";
         route.TargetSMTPHost = "localhost";
         route.TargetSMTPPort = port;
         route.NumberOfTries = numberOfTries;
         route.RelayerRequiresAuth = true;
         route.RelayerAuthUsername = GetUsername();
         route.SetRelayerAuthPassword(GetPassword());
         route.MinutesBetweenTry = 5;
         route.Save();

         return route;
      }

      [Test]
      public void TestIMAPServerLiteral()
      {
         var sim = new ImapClientSimulator();
         sim.Connect();
         Assert.IsTrue(sim.Send("a01 login " + GetUsername() + " {4}").StartsWith("+"));
         sim.Send(GetPassword());
         EnsureNoPassword();
      }

      [Test]
      public void TestIMAPServerLiteral2()
      {
         var sim = new ImapClientSimulator();
         sim.Connect();
         Assert.IsTrue(sim.Send("a01 login {" + GetUsername().Length + "} {4}").StartsWith("+"));
         Assert.IsTrue(sim.Send(GetUsername() + " {" + GetPassword().Length + "}").StartsWith("+"));
         sim.Send(GetPassword());
         EnsureNoPassword();
      }

      [Test]
      public void TestIMAPServerNormal()
      {
         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(GetUsername(), GetPassword());
         EnsureNoPassword();
      }

      [Test]
      public void TestPOP3Client()
      {
         var messages = new List<string>();

         var message = "From: Martin@example.com\r\n" +
                       "To: Martin@example.com\r\n" +
                       "Subject: Test\r\n" +
                       "\r\n" +
                       "Hello!";

         messages.Add(message);

         var port = TestSetup.GetNextFreePort();
         using (var pop3Server = new Pop3ServerSimulator(1, port, messages))
         {
            pop3Server.StartListen();

            var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "user@example.test", "test");
            var fa = account.FetchAccounts.Add();

            fa.Enabled = true;
            fa.MinutesBetweenFetch = 10;
            fa.Name = "Test";
            fa.Username = GetUsername();
            fa.Password = GetPassword();
            fa.UseSSL = false;
            fa.ServerAddress = "localhost";
            fa.Port = port;
            fa.ProcessMIMERecipients = false;
            fa.Save();

            fa.DownloadNow();

            pop3Server.WaitForCompletion();

            fa.Delete();

            var downloadedMessage = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");
            StringAssert.Contains("Hello!", downloadedMessage);

            EnsureNoPassword();
         }
      }

      [Test]
      public void TestPOP3Server()
      {
         var sim = new Pop3ClientSimulator();
         sim.ConnectAndLogon(GetUsername(), GetPassword());
         EnsureNoPassword();
      }

      [Test]
      public void TestSMTPClient()
      {
         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         Assert.AreEqual(0, _status.UndeliveredMessages.Length);

         // No valid recipients...
         var deliveryResults = new Dictionary<string, int>();
         deliveryResults["test@dummy-example.com"] = 550;

         var smtpServerPort = TestSetup.GetNextFreePort();

         using (var server = new SmtpServerSimulator(1, smtpServerPort))
         {
            server.AddRecipientResult(deliveryResults);
            server.StartListen();

            // Add a route so we can connect to localhost.
            AddRoutePointingAtLocalhostWithAuth(0, smtpServerPort);

            // Send message to this route.
            var smtp = new SmtpClientSimulator();
            smtp.Send("test@example.test", "test@dummy-example.com", "Test", "Test message");

            CustomAsserts.AssertRecipientsInDeliveryQueue(0);

            var undeliveredMessages = _status.UndeliveredMessages;

            // Wait for the client to disconnect.
            server.WaitForCompletion();

            CustomAsserts.AssertRecipientsInDeliveryQueue(0);

            EnsureNoPassword();
         }
      }

      [Test]
      public void TestSMTPServerAuthLogin()
      {
         var sim = new SmtpClientSimulator();
         string errorMsg;

         CustomAsserts.Throws<AuthenticationException>(() =>
            sim.ConnectAndLogon(GetUsername(), GetPassword(), out errorMsg));

         EnsureNoPassword();
      }

      [Test]
      public void TestSMTPServerAuthLoginUsernameAsThirdParameter()
      {
         _settings.AllowSMTPAuthPlain = true;

         var sock = new TcpConnection();
         sock.Connect(25);
         Assert.IsTrue(sock.Receive().StartsWith("220"));
         sock.Send("EHLO test.com\r\n");
         Assert.IsTrue(sock.Receive().StartsWith("250"));

         var base64EncodedUsername = EncodeBase64(GetUsername());
         sock.Send("AUTH LOGIN " + base64EncodedUsername + "\r\n");
         Assert.IsTrue(sock.Receive().StartsWith("334"));

         sock.Send(EncodeBase64(GetPassword()) + "\r\n");
         Assert.IsTrue(sock.Receive().StartsWith("535"));
         EnsureNoPassword();
      }

      [Test]
      public void TestSMTPServerAuthPlain()
      {
         _settings.AllowSMTPAuthPlain = true;

         var sock = new TcpConnection();
         sock.Connect(25);
         Assert.IsTrue(sock.Receive().StartsWith("220"));
         sock.Send("EHLO test.com\r\n");
         Assert.IsTrue(sock.Receive().StartsWith("250"));
         sock.Send("AUTH PLAIN\r\n");
         Assert.IsTrue(sock.Receive().StartsWith("334"));

         var str = "\t" + GetUsername() + "\t" + GetPassword();

         sock.Send(EncodeBase64(str) + "\r\n");
         Assert.IsTrue(sock.Receive().StartsWith("535"));
         EnsureNoPassword();
      }

      [Test]
      public void TestSMTPServerAuthPlainSingleLine()
      {
         _settings.AllowSMTPAuthPlain = true;

         var sock = new TcpConnection();
         sock.Connect(25);
         Assert.IsTrue(sock.Receive().StartsWith("220"));
         sock.Send("EHLO test.com\r\n");
         Assert.IsTrue(sock.Receive().StartsWith("250"));
         sock.Send("AUTH PLAIN 77+9dXNlcm5hbWVAZG9tYWluLmNvbe+/vVBAc3N3b3Jk\r\n");
         Assert.IsTrue(sock.Receive().StartsWith("535"));

         EnsureNoPassword(false);
      }
   }
}