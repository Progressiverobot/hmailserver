using System.Collections.Generic;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.SMTP
{
   [TestFixture]
   public class Routes : TestFixtureBase
   {
      [Test]
      [Description("Issue 284. Sender to Alias to Route not working.")]
      public void SendMessageToAliasForwardToRoute()
      {
         // Set up a server listening on port 250 which accepts email for test@otherdomain.com
         var deliveryResults = new Dictionary<string, int>();
         deliveryResults["user@example.test"] = 250;

         var smtpServerPort = TestSetup.GetNextFreePort();
         using (var server = new SmtpServerSimulator(1, smtpServerPort))
         {
            server.AddRecipientResult(deliveryResults);
            server.StartListen();

            // Add a route pointing at localhost
            var route = _settings.Routes.Add();
            route.DomainName = "example.test";
            route.TargetSMTPHost = "localhost";
            route.TargetSMTPPort = smtpServerPort;
            route.NumberOfTries = 1;
            route.MinutesBetweenTry = 5;
            route.TreatRecipientAsLocalDomain = true;
            route.TreatSenderAsLocalDomain = true;
            route.AllAddresses = false;
            route.Save();

            // Make sure only the specific user is valid.
            var routeAddress = route.Addresses.Add();
            routeAddress.Address = "user@" + _domain.Name;
            routeAddress.Save();

            SingletonProvider<TestSetup>.Instance.AddAlias(_domain, "users@example.test", "user@example.test");

            var smtpClient = new SmtpClientSimulator();
            smtpClient.Send("example@example.com", "users@example.test", "Test", "Test message");
            CustomAsserts.AssertRecipientsInDeliveryQueue(0);

            server.WaitForCompletion();

            Assert.IsTrue(server.MessageData.Contains("Test message"));
         }
      }


      [Test]
      [Description("If both route and SMTP relay is in use, route should have higher priortiy..")]
      public void RoutesShouldHaveHigherPrioThanSMTPRelay()
      {
         // Set up a server listening on port 250 which accepts email for test@example.test
         var deliveryResults = new Dictionary<string, int>();
         deliveryResults["user@example.test"] = 250;

         // We set the SMTP relayer here, but this should be ignored since the list's
         // address matches a route set up (test.com).
         _application.Settings.SMTPRelayer = "example.com";

         var smtpServerPort = TestSetup.GetNextFreePort();
         using (var server = new SmtpServerSimulator(1, smtpServerPort))
         {
            server.AddRecipientResult(deliveryResults);
            server.StartListen();

            // Add a route pointing at localhost
            var route = _settings.Routes.Add();
            route.DomainName = "example.test";
            route.TargetSMTPHost = "localhost";
            route.TargetSMTPPort = smtpServerPort;
            route.NumberOfTries = 1;
            route.MinutesBetweenTry = 5;
            route.TreatRecipientAsLocalDomain = true;
            route.TreatSenderAsLocalDomain = true;
            route.AllAddresses = false;
            route.Save();

            // Make sure only the specific user is valid.
            var routeAddress = route.Addresses.Add();
            routeAddress.Address = "user@" + _domain.Name;
            routeAddress.Save();

            SingletonProvider<TestSetup>.Instance.AddAlias(_domain, "users@example.test", "user@example.test");

            var smtpClient = new SmtpClientSimulator();
            smtpClient.Send("example@example.com", "users@example.test", "Test", "Test message");
            CustomAsserts.AssertRecipientsInDeliveryQueue(0);

            server.WaitForCompletion();

            Assert.IsTrue(server.MessageData.Contains("Test message"));
         }
      }

      [Test]
      [Description(
         "If a message with 4 recipients on the same domain is is delivered via a route, only one message should be delivered.")]
      public void RoutesShouldConsolidateRecipients()
      {
         // Set up a server listening on port 250 which accepts email for test@example.test
         var deliveryResults = new Dictionary<string, int>();
         deliveryResults["user1@example.test"] = 250;
         deliveryResults["user2@example.test"] = 250;
         deliveryResults["user3@example.test"] = 250;
         deliveryResults["user4@example.test"] = 250;

         var smtpServerPort = TestSetup.GetNextFreePort();
         using (var server = new SmtpServerSimulator(1, smtpServerPort))
         {
            server.AddRecipientResult(deliveryResults);
            server.StartListen();

            // Add a route pointing at localhost
            var route = _settings.Routes.Add();
            route.DomainName = "example.test";
            route.TargetSMTPHost = "localhost";
            route.TargetSMTPPort = smtpServerPort;
            route.NumberOfTries = 1;
            route.MinutesBetweenTry = 5;
            route.TreatRecipientAsLocalDomain = true;
            route.TreatSenderAsLocalDomain = true;
            route.AllAddresses = true;
            route.Save();

            var smtpClient = new SmtpClientSimulator();

            var recipients = new List<string>
            {
               "user1@example.test",
               "user2@example.test",
               "user3@example.test",
               "user4@example.test"
            };

            smtpClient.Send("example@example.com", recipients, "Test", "Test message");
            CustomAsserts.AssertRecipientsInDeliveryQueue(0);

            server.WaitForCompletion();

            Assert.IsTrue(server.MessageData.Contains("Test message"));
            Assert.AreEqual(deliveryResults.Count, server.RcptTosReceived);
         }
      }

      [Test]
      public void RoutesShouldSupportWildcardDomain()
      {
         // Set up a server listening on port 250 which accepts email for test@otherdomain.com
         var deliveryResults = new Dictionary<string, int>();
         deliveryResults["user@stuff.example.com"] = 250;

         var smtpServerPort = TestSetup.GetNextFreePort();
         using (var server = new SmtpServerSimulator(1, smtpServerPort))
         {
            server.AddRecipientResult(deliveryResults);
            server.StartListen();

            // Add a route pointing at localhost
            var route = _settings.Routes.Add();
            route.DomainName = "*.example.com";
            route.TargetSMTPHost = "localhost";
            route.TargetSMTPPort = smtpServerPort;
            route.NumberOfTries = 1;
            route.MinutesBetweenTry = 5;
            route.TreatRecipientAsLocalDomain = true;
            route.TreatSenderAsLocalDomain = true;
            route.AllAddresses = true;
            route.Save();

            // Make sure only the specific user is valid.
            var routeAddress = route.Addresses.Add();
            routeAddress.Address = "user@" + _domain.Name;
            routeAddress.Save();

            var smtpClient = new SmtpClientSimulator();
            smtpClient.Send("example@example.com", "user@stuff.example.com", "Test", "Test message");
            CustomAsserts.AssertRecipientsInDeliveryQueue(0);

            server.WaitForCompletion();

            Assert.IsTrue(server.MessageData.Contains("Test message"));
         }
      }

      [Test]
      [Description(
         "If a client attempts to deliver to a route, but the list is not in the route list an error should be returned.")]
      public void RecipientNotInListShouldReturnError()
      {
         // Add a route pointing at localhost
         var route = _settings.Routes.Add();
         route.DomainName = "example.test";
         route.TargetSMTPHost = "localhost";
         route.TargetSMTPPort = 255;
         route.NumberOfTries = 1;
         route.MinutesBetweenTry = 5;
         route.TreatRecipientAsLocalDomain = true;
         route.TreatSenderAsLocalDomain = true;
         route.AllAddresses = false; // only to recipients in list.
         route.Save();

         var smtpClient = new SmtpClientSimulator();

         var resultMessage = "";
         CustomAsserts.Throws<DeliveryFailedException>(() =>
            smtpClient.Send("example@example.com", "user1@example.test", "Test", "Test message", out resultMessage));
         Assert.AreEqual("550 Recipient not in route list.", resultMessage);
      }

      [Test]
      public void ShouldBePossibleToSendToRouteWithTargetIPAddress()
      {
         // Set up a server listening on port 250 which accepts email for test@otherdomain.com
         var deliveryResults = new Dictionary<string, int>();
         deliveryResults["test@dummy-example.com"] = 250;

         var smtpServerPort = TestSetup.GetNextFreePort();
         using (var server = new SmtpServerSimulator(1, smtpServerPort))
         {
            server.AddRecipientResult(deliveryResults);
            server.StartListen();

            var route = TestSetup.AddRoutePointingAtLocalhost(1, smtpServerPort, true, eConnectionSecurity.eCSNone);
            route.TargetSMTPHost = "127.0.0.1";
            route.Save();

            var smtpSimulator = new SmtpClientSimulator();
            smtpSimulator.Send("test@example.test",
               "test@dummy-example.com", "Mail 1", "Test message");


            // This should now be processed via the rule -> route -> external server we've set up.
            server.WaitForCompletion();
            var log = LogHandler.ReadCurrentDefaultLog();

            Assert.IsTrue(server.MessageData.Contains("Test message"));
         }
      }

      [Test]
      public void RecipientNotInListButDomainHasCatchAll_EmailShouldEndUpInCatchAll()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         // Add a route pointing at localhost
         var route = _settings.Routes.Add();
         route.DomainName = "example.test";
         route.TargetSMTPHost = "localhost";
         route.TargetSMTPPort = 255;
         route.NumberOfTries = 1;
         route.MinutesBetweenTry = 5;
         route.TreatRecipientAsLocalDomain = true;
         route.TreatSenderAsLocalDomain = true;
         route.AllAddresses = false;
         route.Save();

         var routeAddress = route.Addresses.Add();
         routeAddress.Address = "something@example.test";
         routeAddress.Save();
         route.Save();

         _domain.Postmaster = "test@example.test";
         _domain.Save();

         SmtpClientSimulator.StaticSend("test@example.test", "other@example.test", "A", "B");
         Pop3ClientSimulator.AssertMessageCount("test@example.test", "test", 1);
      }

      [Test]
      public void RecipientInRouteAndDomainHasCatchAll_EmailShouldNotGoToCatchAll()
      {
         // catchall is a local account; exchange-user is not a local account (lives on the remote server via route)
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "catchall@example.test", "test");

         var deliveryResults = new Dictionary<string, int>();
         deliveryResults["exchange-user@example.test"] = 250;

         var smtpServerPort = TestSetup.GetNextFreePort();
         using (var server = new SmtpServerSimulator(1, smtpServerPort))
         {
            server.AddRecipientResult(deliveryResults);
            server.StartListen();

            var route = _settings.Routes.Add();
            route.DomainName = "example.test";
            route.TargetSMTPHost = "localhost";
            route.TargetSMTPPort = smtpServerPort;
            route.NumberOfTries = 1;
            route.MinutesBetweenTry = 5;
            route.TreatRecipientAsLocalDomain = true;
            route.TreatSenderAsLocalDomain = true;
            route.AllAddresses = false;
            route.Save();

            var routeAddress = route.Addresses.Add();
            routeAddress.Address = "exchange-user@example.test";
            routeAddress.Save();
            route.Save();

            _domain.Postmaster = "catchall@example.test";
            _domain.Save();

            // exchange-user@example.test is in the route list — should follow route only, not catchall
            SmtpClientSimulator.StaticSend("sender@example.test", "exchange-user@example.test", "Subject", "Body");

            server.WaitForCompletion();

            Assert.IsTrue(server.MessageData.Contains("Body"));
            Pop3ClientSimulator.AssertMessageCount("catchall@example.test", "test", 0);
         }
      }
   }
}