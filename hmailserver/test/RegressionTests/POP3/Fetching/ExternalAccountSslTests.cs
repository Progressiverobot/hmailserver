using System.Collections.Generic;
using System.Threading;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using RegressionTests.SSL;

namespace RegressionTests.POP3.Fetching
{
   [TestFixture]
   public class ExternalAccountSslTests : TestFixtureBase
   {
      [Test]
      [Description(
         "Tests to connect to a normal non-SSL POP3 server using SSL. An error should be reported and hMailServer should disconnect correctly.")]
      public void POP3ServerNotSupportingSSL()
      {
         var messages = new List<string>();

         var message = "From: Martin@example.com\r\n" +
                       "To: Martin@example.com\r\n" +
                       "Subject: Test\r\n" +
                       "\r\n" +
                       "Hello!";

         messages.Add(message);

         var port = TestSetup.GetNextFreePort();
         using (var pop3Server = new Pop3ServerSimulator(2, port, messages))
         {
            pop3Server.DisconnectImmediate = true;
            pop3Server.StartListen();

            LogHandler.DeleteCurrentDefaultLog();

            var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "user@example.test", "test");
            var fa = account.FetchAccounts.Add();

            fa.Enabled = true;
            fa.MinutesBetweenFetch = 10;
            fa.Name = "Test";
            fa.Username = "test@example.com";
            fa.Password = "test";
            fa.UseSSL = true;
            fa.ServerAddress = "localhost";
            fa.Port = port;
            fa.ProcessMIMERecipients = false;
            fa.Save();

            fa.DownloadNow();
            var contentSoFar = "";
            for (var i = 0; i <= 10; i++)
            {
               if (i == 5)
                  Assert.Fail("No connection: " + contentSoFar);

               contentSoFar = LogHandler.ReadCurrentDefaultLog();
               if (contentSoFar.Contains("TCPConnection - TLS/SSL handshake failed."))
                  break;

               Thread.Sleep(1000);
            }

            LogHandler.DeleteCurrentDefaultLog();

            // try again.
            fa.DownloadNow();

            contentSoFar = "";

            for (var i = 0; i <= 10; i++)
            {
               if (i == 5)
                  Assert.Fail("No connection: " + contentSoFar);

               contentSoFar = LogHandler.ReadCurrentDefaultLog();
               if (contentSoFar.Contains("TCPConnection - TLS/SSL handshake failed."))
                  break;

               Thread.Sleep(1000);
            }

            pop3Server.WaitForCompletion();

            fa.Delete();
         }
      }

      [Test]
      public void POP3ServerSupportingSSL()
      {
         var messages = new List<string>();

         var message = "From: Martin@example.com\r\n" +
                       "To: Martin@example.com\r\n" +
                       "Subject: Test\r\n" +
                       "\r\n" +
                       "Hello!";

         messages.Add(message);

         var port = TestSetup.GetNextFreePort();
         using (var pop3Server = new Pop3ServerSimulator(1, port, messages, eConnectionSecurity.eCSTLS))
         {
            pop3Server.SetCertificate(SslSetup.GetCertificate());
            pop3Server.StartListen();

            LogHandler.DeleteCurrentDefaultLog();

            var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "user@example.test", "test");
            var fa = account.FetchAccounts.Add();

            fa.Enabled = true;
            fa.MinutesBetweenFetch = 10;
            fa.Name = "Test";
            fa.Username = "test@example.com";
            fa.Password = "test";
            fa.ConnectionSecurity = eConnectionSecurity.eCSTLS;
            fa.ServerAddress = "localhost";
            fa.Port = port;
            fa.ProcessMIMERecipients = false;
            fa.Save();

            fa.DownloadNow();
            pop3Server.WaitForCompletion();

            Pop3ClientSimulator.AssertMessageCount("user@example.test", "test", 1);

            fa.Delete();
         }
      }

      [Test]
      public void POP3ServerNOTSupportingStartTLS_StartTLSRequired()
      {
         var messages = new List<string>();

         var message = "From: Martin@example.com\r\n" +
                       "To: Martin@example.com\r\n" +
                       "Subject: Test\r\n" +
                       "\r\n" +
                       "Hello!";

         messages.Add(message);

         var port = TestSetup.GetNextFreePort();
         using (var pop3Server = new Pop3ServerSimulator(1, port, messages, eConnectionSecurity.eCSNone))
         {
            pop3Server.SetCertificate(SslSetup.GetCertificate());
            pop3Server.StartListen();

            LogHandler.DeleteCurrentDefaultLog();

            var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "user@example.test", "test");
            var fa = account.FetchAccounts.Add();

            fa.Enabled = true;
            fa.MinutesBetweenFetch = 10;
            fa.Name = "Test";
            fa.Username = "test@example.com";
            fa.Password = "test";
            fa.ConnectionSecurity = eConnectionSecurity.eCSSTARTTLSRequired;
            fa.ServerAddress = "localhost";
            fa.Port = port;
            fa.ProcessMIMERecipients = false;
            fa.Save();

            fa.DownloadNow();

            var expectedMessage =
               string.Format(
                  "The download of messages from external account {0} failed. The external aAccount is configured to use STARTTLS connection security, but the POP3 server does not support it.",
                  fa.Name);

            var contentSoFar = "";

            for (var i = 0; i <= 10; i++)
            {
               if (i == 5)
                  Assert.Fail("No connection: " + contentSoFar);

               contentSoFar = LogHandler.ReadCurrentDefaultLog();
               if (contentSoFar.Contains(expectedMessage))
                  break;

               Thread.Sleep(1000);
            }

            pop3Server.WaitForCompletion();

            fa.Delete();
         }
      }

      [Test]
      public void POP3ServerNOTSupportingStartTLS_StartTLSOptional()
      {
         var messages = new List<string>();

         var message = "From: Martin@example.com\r\n" +
                       "To: Martin@example.com\r\n" +
                       "Subject: Test\r\n" +
                       "\r\n" +
                       "Hello!";

         messages.Add(message);

         var port = TestSetup.GetNextFreePort();
         using (var pop3Server = new Pop3ServerSimulator(1, port, messages, eConnectionSecurity.eCSNone))
         {
            pop3Server.SetCertificate(SslSetup.GetCertificate());
            pop3Server.StartListen();

            LogHandler.DeleteCurrentDefaultLog();

            var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "user@example.test", "test");
            var fa = account.FetchAccounts.Add();

            fa.Enabled = true;
            fa.MinutesBetweenFetch = 10;
            fa.Name = "Test";
            fa.Username = "test@example.com";
            fa.Password = "test";
            fa.ConnectionSecurity = eConnectionSecurity.eCSSTARTTLSOptional;
            fa.ServerAddress = "localhost";
            fa.Port = port;
            fa.ProcessMIMERecipients = false;
            fa.Save();

            fa.DownloadNow();
            pop3Server.WaitForCompletion();

            Pop3ClientSimulator.AssertMessageCount("user@example.test", "test", 1);

            fa.Delete();
         }
      }

      [Test]
      public void POP3ServerSupportingStartTLS_StartTLSRequired()
      {
         var messages = new List<string>();

         var message = "From: Martin@example.com\r\n" +
                       "To: Martin@example.com\r\n" +
                       "Subject: Test\r\n" +
                       "\r\n" +
                       "Hello!";

         messages.Add(message);

         var port = TestSetup.GetNextFreePort();
         using (var pop3Server = new Pop3ServerSimulator(1, port, messages, eConnectionSecurity.eCSSTARTTLSRequired))
         {
            pop3Server.SetCertificate(SslSetup.GetCertificate());
            pop3Server.StartListen();

            LogHandler.DeleteCurrentDefaultLog();

            var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "user@example.test", "test");
            var fa = account.FetchAccounts.Add();

            fa.Enabled = true;
            fa.MinutesBetweenFetch = 10;
            fa.Name = "Test";
            fa.Username = "test@example.com";
            fa.Password = "test";
            fa.ConnectionSecurity = eConnectionSecurity.eCSSTARTTLSRequired;
            fa.ServerAddress = "localhost";
            fa.Port = port;
            fa.ProcessMIMERecipients = false;
            fa.Save();

            fa.DownloadNow();
            pop3Server.WaitForCompletion();

            Pop3ClientSimulator.AssertMessageCount("user@example.test", "test", 1);

            fa.Delete();
         }
      }

      [Test]
      public void POP3ServerSupportingStartTLS_StartTLSOptional()
      {
         var messages = new List<string>();

         var message = "From: Martin@example.com\r\n" +
                       "To: Martin@example.com\r\n" +
                       "Subject: Test\r\n" +
                       "\r\n" +
                       "Hello!";

         messages.Add(message);

         var port = TestSetup.GetNextFreePort();
         using (var pop3Server = new Pop3ServerSimulator(1, port, messages, eConnectionSecurity.eCSSTARTTLSRequired))
         {
            pop3Server.SetCertificate(SslSetup.GetCertificate());
            pop3Server.StartListen();

            LogHandler.DeleteCurrentDefaultLog();

            var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "user@example.test", "test");
            var fa = account.FetchAccounts.Add();

            fa.Enabled = true;
            fa.MinutesBetweenFetch = 10;
            fa.Name = "Test";
            fa.Username = "test@example.com";
            fa.Password = "test";
            fa.ConnectionSecurity = eConnectionSecurity.eCSSTARTTLSOptional;
            fa.ServerAddress = "localhost";
            fa.Port = port;
            fa.ProcessMIMERecipients = false;
            fa.Save();

            fa.DownloadNow();
            pop3Server.WaitForCompletion();

            Pop3ClientSimulator.AssertMessageCount("user@example.test", "test", 1);

            fa.Delete();
         }
      }
   }
}