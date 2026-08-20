using System.Collections.Generic;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.SMTP
{
   /// <summary>
   ///    An outbound relay chosen by the SENDING domain (discussion #31).
   ///
   ///    The relay was a single server-wide setting, so every hosted domain left
   ///    through the same provider. That is fine for one organisation and wrong for
   ///    the case this server is often put to: several independent domains on one
   ///    box, each having bought its own delivery service, each needing its own
   ///    credentials on the way out.
   ///
   ///    It is deliberately a different question from a route. A route says "mail
   ///    addressed TO this domain goes to that host" and is about the destination;
   ///    this says "mail FROM this domain leaves through that host" and is about the
   ///    sender. The two can both apply to one message, so the order matters and is
   ///    asserted here rather than left to be discovered: route first, then the
   ///    sending domain's relay, then the server-wide relayer.
   ///
   ///    Every test uses two listening servers rather than one, because "the message
   ///    arrived at A" on its own does not show it would not also have arrived at A
   ///    with the feature switched off. The second server is the one that must stay
   ///    empty.
   /// </summary>
   [TestFixture]
   public class PerDomainRelay : TestFixtureBase
   {
      private const string ExternalRecipient = "recipient@relay-target.test";

      private static Dictionary<string, int> Accepts()
      {
         var results = new Dictionary<string, int>();
         results[ExternalRecipient] = 250;
         return results;
      }

      [Test]
      [Description("Mail from a domain with its own relay goes to that relay and not to the server-wide one")]
      public void ADomainRelayIsUsedInsteadOfTheServerWideRelayer()
      {
         int domainRelayPort = TestSetup.GetNextFreePort();
         int globalRelayPort = TestSetup.GetNextFreePort();

         using (var domainRelay = new SmtpServerSimulator(1, domainRelayPort))
         using (var globalRelay = new SmtpServerSimulator(1, globalRelayPort))
         {
            domainRelay.AddRecipientResult(Accepts());
            globalRelay.AddRecipientResult(Accepts());
            domainRelay.StartListen();
            globalRelay.StartListen();

            try
            {
               _application.Settings.SMTPRelayer = "localhost";
               _application.Settings.SMTPRelayerPort = globalRelayPort;

               _domain.RelayHost = "localhost";
               _domain.RelayPort = domainRelayPort;
               _domain.Save();

               SmtpClientSimulator.StaticSend("sender@" + _domain.Name, ExternalRecipient,
                                              "domain relay", "Sent through the domain's own relay.");

               CustomAsserts.AssertRecipientsInDeliveryQueue(0);

               domainRelay.WaitForCompletion();

               StringAssert.Contains("Sent through the domain's own relay.", domainRelay.MessageData,
                  "The message should have left through the relay configured on the sending domain.");

               // The half that makes the assertion above evidence rather than
               // coincidence: with the domain relay unset this message would have gone
               // here, and it must not have gone to both.
               ClassicAssert.IsTrue(string.IsNullOrEmpty(globalRelay.MessageData),
                  "The server-wide relayer must not have been used for a domain that names its own. " +
                  "Got: " + globalRelay.MessageData);
            }
            finally
            {
               _application.Settings.SMTPRelayer = "";
               _application.Settings.SMTPRelayerPort = 25;
            }
         }
      }

      [Test]
      [Description("A domain that names no relay still uses the server-wide relayer, so an upgraded installation behaves exactly as it did")]
      public void ADomainWithoutARelayStillUsesTheServerWideRelayer()
      {
         int globalRelayPort = TestSetup.GetNextFreePort();

         using (var globalRelay = new SmtpServerSimulator(1, globalRelayPort))
         {
            globalRelay.AddRecipientResult(Accepts());
            globalRelay.StartListen();

            try
            {
               _application.Settings.SMTPRelayer = "localhost";
               _application.Settings.SMTPRelayerPort = globalRelayPort;

               // Explicitly empty. The default, made deliberate: this is the assertion
               // that the feature costs nothing to anyone who does not want it.
               _domain.RelayHost = "";
               _domain.Save();

               SmtpClientSimulator.StaticSend("sender@" + _domain.Name, ExternalRecipient,
                                              "global relay", "Sent through the server-wide relayer.");

               CustomAsserts.AssertRecipientsInDeliveryQueue(0);

               globalRelay.WaitForCompletion();

               StringAssert.Contains("Sent through the server-wide relayer.", globalRelay.MessageData,
                  "With no relay named on the domain, the server-wide relayer must still be used.");
            }
            finally
            {
               _application.Settings.SMTPRelayer = "";
               _application.Settings.SMTPRelayerPort = 25;
            }
         }
      }

      [Test]
      [Description("A route still wins over the sending domain's relay, because a route is a statement about where the mail is going")]
      public void ARouteOutranksTheSendingDomainsRelay()
      {
         int routePort = TestSetup.GetNextFreePort();
         int domainRelayPort = TestSetup.GetNextFreePort();

         using (var routeTarget = new SmtpServerSimulator(1, routePort))
         using (var domainRelay = new SmtpServerSimulator(1, domainRelayPort))
         {
            routeTarget.AddRecipientResult(Accepts());
            domainRelay.AddRecipientResult(Accepts());
            routeTarget.StartListen();
            domainRelay.StartListen();

            var route = _settings.Routes.Add();
            route.DomainName = "relay-target.test";
            route.TargetSMTPHost = "localhost";
            route.TargetSMTPPort = routePort;
            route.NumberOfTries = 1;
            route.MinutesBetweenTry = 5;
            route.AllAddresses = true;
            route.Save();

            try
            {
               _domain.RelayHost = "localhost";
               _domain.RelayPort = domainRelayPort;
               _domain.Save();

               SmtpClientSimulator.StaticSend("sender@" + _domain.Name, ExternalRecipient,
                                              "route wins", "Sent through the route.");

               CustomAsserts.AssertRecipientsInDeliveryQueue(0);

               routeTarget.WaitForCompletion();

               StringAssert.Contains("Sent through the route.", routeTarget.MessageData,
                  "A route names the destination and must outrank a relay chosen by the sender's domain.");

               ClassicAssert.IsTrue(string.IsNullOrEmpty(domainRelay.MessageData),
                  "The sending domain's relay must not have been used when a route matched. " +
                  "Got: " + domainRelay.MessageData);
            }
            finally
            {
               _settings.Routes.DeleteByDBID(route.ID);
            }
         }
      }

      [TearDown]
      public void ClearTheDomainRelay()
      {
         // The domain object outlives this fixture - it is recreated per test by
         // PerformBasicSetup, but the relay columns are written to the database, and a
         // leftover host would send the next fixture's outbound mail to a port nothing
         // is listening on any more.
         _domain.RelayHost = "";
         _domain.RelayPort = 0;
         _domain.RelayRequiresAuthentication = false;
         _domain.RelayUsername = "";
         _domain.Save();
      }
   }
}
