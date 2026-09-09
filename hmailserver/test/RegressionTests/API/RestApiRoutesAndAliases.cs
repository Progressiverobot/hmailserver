// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.API
{
   /// <summary>
   ///    The SMTP route routes (GET/POST /api/v1/routes, PUT/DELETE /api/v1/routes/&lt;id&gt;),
   ///    the alias writes (POST /api/v1/domains/&lt;domain&gt;/aliases, DELETE /api/v1/aliases/&lt;address&gt;)
   ///    and PUT /api/v1/accounts/&lt;address&gt;, plus the optional fields POST
   ///    /api/v1/domains/&lt;domain&gt;/accounts now honours.
   ///
   ///    Every assertion is made against what the server did, read back through
   ///    COM as the Control Panel reads it - app.Settings.Routes, domain.Aliases,
   ///    domain.Accounts - and never only against the response; and where the
   ///    running server behaves differently afterwards, that is what is asserted:
   ///    a route relays a message to the suite's SMTP simulator on the port the
   ///    route names and follows the route when it is moved, an alias delivers,
   ///    a changed password is the one IMAP accepts, an inactive account is refused.
   ///
   ///    Routes and aliases made here carry a prefix so that TearDown can remove
   ///    what a failed assertion left behind; the accounts live in example.test,
   ///    which PerformBasicSetup recreates before every test.
   /// </summary>
   [TestFixture]
   public class RestApiRoutesAndAliases : TestFixtureBase
   {
      private const int RestPort = 9123;
      private const string AdminPassword = "testar";
      private const string RoutePrefix = "restroute-";
      private const string AliasPrefix = "restalias-";

      // The domain the suite's outbound fixtures route to; TearDown removes a
      // route for it as it removes the prefixed ones.
      private const string RelayDomain = "dummy-example.com";

      private readonly List<string> _keyIds = new List<string>();

      private static string UniqueRouteDomain()
      {
         return RoutePrefix + Guid.NewGuid().ToString("N").Substring(0, 8) + ".test";
      }

      private static string UniqueAliasName()
      {
         return AliasPrefix + Guid.NewGuid().ToString("N").Substring(0, 8) + "@example.test";
      }

      private void WriteSetting(string key, string value)
      {
         string programDirectory = _application.Settings.Directories.ProgramDirectory;
         string[] candidates =
         {
            Paths.Combine(programDirectory, "hMailServer.ini"),
            Paths.Combine(programDirectory, "Bin", "hMailServer.ini"),
         };

         bool wroteAny = false;
         foreach (string iniPath in candidates.Where(File.Exists))
         {
            Assert.IsTrue(
               IniFile.WritePrivateProfileString("Settings", key, value, iniPath),
               "Failed to write " + key + " to " + iniPath + ".");
            wroteAny = true;
         }

         Assert.IsTrue(wroteAny, "Could not locate an existing hMailServer.ini to update.");
      }

      [SetUp]
      public void StartRestApi()
      {
         _settings.SetAdministratorPassword(AdminPassword);

         WriteSetting("RestApiBindAddress", "127.0.0.1");
         WriteSetting("RestApiPort", RestPort.ToString());

         _application.Reinitialize();

         (int status, string body) probe = Http("GET", "/api/v1/status");
         Assert.AreEqual(200, probe.status, "REST API did not answer /api/v1/status. Body: " + probe.body);
      }

      [TearDown]
      public void StopRestApi()
      {
         try
         {
            // Routes are server-wide and outlive the domain PerformBasicSetup
            // recreates, so they go first, over COM, whatever the test did.
            hMailServer.Routes routes = _settings.Routes;
            for (int i = routes.Count - 1; i >= 0; i--)
            {
               Route route = routes[i];
               if (route.DomainName.StartsWith(RoutePrefix, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(route.DomainName, RelayDomain, StringComparison.OrdinalIgnoreCase))
                  route.Delete();
            }

            Aliases aliases = _domain.Aliases;
            aliases.Refresh();
            for (int i = aliases.Count - 1; i >= 0; i--)
            {
               Alias alias = aliases[i];
               if (alias.Name.StartsWith(AliasPrefix, StringComparison.OrdinalIgnoreCase))
                  alias.Delete();
            }

            foreach (string id in _keyIds)
               Http("DELETE", "/api/v1/apikeys/" + id);
            _keyIds.Clear();
         }
         finally
         {
            WriteSetting("RestApiPort", "0");
            _application.Reinitialize();
         }
      }

      // The route through COM, or null - Settings.Routes is the collection the
      // running SMTP delivery reads, and the one the Control Panel lists.
      private Route RouteOverCom(string domainName)
      {
         try
         {
            return _settings.Routes.get_ItemByName(domainName);
         }
         catch (COMException)
         {
            return null;
         }
      }

      private Alias AliasOverCom(string name)
      {
         Aliases aliases = _domain.Aliases;
         aliases.Refresh();
         try
         {
            return aliases.get_ItemByName(name);
         }
         catch (COMException)
         {
            return null;
         }
      }

      private Account AccountOverCom(string address)
      {
         try
         {
            return _domain.Accounts.ItemByAddress[address];
         }
         catch (COMException)
         {
            return null;
         }
      }

      private static string RouteBody(string domainName, string host, int port, string extra = "")
      {
         return "{\"domain_name\":\"" + domainName + "\",\"target_smtp_host\":\"" + host + "\",\"target_smtp_port\":" + port + extra + "}";
      }

      [Test]
      [Description("A route is created with every field, read back through COM as the Control Panel lists it, replaced whole by PUT, deleted, and gone; a second delete and a PUT on the id are 404.")]
      public void RouteRoundTrip()
      {
         string name = UniqueRouteDomain();

         (int status, string body) created = Http("POST", "/api/v1/routes",
            RouteBody(name, "127.0.0.1", 2525,
               ",\"number_of_tries\":4,\"minutes_between_try\":7,\"description\":\"made over REST\"," +
               "\"relayer_requires_authentication\":true,\"relayer_auth_username\":\"relay-user\",\"relayer_auth_password\":\"relay-secret\"," +
               "\"treat_recipient_as_local_domain\":true,\"treat_sender_as_local_domain\":true," +
               "\"all_addresses\":false,\"addresses\":[\"a@" + name + "\",\"B@" + name + "\",\"A@" + name + "\"]," +
               "\"connection_security\":\"starttls_required\""));
         Assert.AreEqual(201, created.status, created.body);
         StringAssert.Contains("\"domain_name\":\"" + name + "\"", created.body);
         StringAssert.Contains("\"target_smtp_port\":2525", created.body);
         StringAssert.Contains("\"connection_security\":\"starttls_required\"", created.body);
         StringAssert.Contains("\"addresses\":[\"a@" + name + "\",\"B@" + name + "\"]", created.body,
            "The address list is de-duplicated without regard to case, as RouteAddresses matches it.");
         StringAssert.DoesNotContain("relay-secret", created.body, "The relay password is write-only.");
         StringAssert.DoesNotContain("relayer_auth_password", created.body);
         int id = ExtractNumber(created.body, "id");

         // COM sees the route the Control Panel would show, field for field.
         Route route = RouteOverCom(name);
         Assert.IsNotNull(route, "The route must exist through COM after POST /api/v1/routes.");
         Assert.AreEqual(id, route.ID);
         Assert.AreEqual("127.0.0.1", route.TargetSMTPHost);
         Assert.AreEqual(2525, route.TargetSMTPPort);
         Assert.AreEqual(4, route.NumberOfTries);
         Assert.AreEqual(7, route.MinutesBetweenTry);
         Assert.AreEqual("made over REST", route.Description);
         Assert.IsTrue(route.RelayerRequiresAuth);
         Assert.AreEqual("relay-user", route.RelayerAuthUsername);
         Assert.IsTrue(route.TreatRecipientAsLocalDomain);
         Assert.IsTrue(route.TreatSenderAsLocalDomain);
         Assert.IsFalse(route.AllAddresses);
         Assert.AreEqual(eConnectionSecurity.eCSSTARTTLSRequired, route.ConnectionSecurity);
         Assert.AreEqual(2, route.Addresses.Count);
         var addresses = new List<string> { route.Addresses[0].Address, route.Addresses[1].Address };
         CollectionAssert.Contains(addresses, "a@" + name);
         CollectionAssert.Contains(addresses, "B@" + name);

         // The listing shows it as the same entry the create answered with.
         (int listStatus, string list) = Http("GET", "/api/v1/routes");
         Assert.AreEqual(200, listStatus, list);
         StringAssert.Contains("\"id\":" + id + ",\"domain_name\":\"" + name + "\"", list);

         // PUT replaces the whole record: what the body does not name goes back
         // to the default a new route gets, and the address list becomes the
         // body's. The relay password is the one thing kept.
         (int putStatus, string putBody) = Http("PUT", "/api/v1/routes/" + id,
            RouteBody(name, "relay.example.test", 2526,
               ",\"all_addresses\":false,\"addresses\":[\"c@" + name + "\"]," +
               "\"relayer_requires_authentication\":true,\"relayer_auth_username\":\"relay-user\",\"connection_security\":\"tls\""));
         Assert.AreEqual(200, putStatus, putBody);
         StringAssert.Contains("\"target_smtp_host\":\"relay.example.test\"", putBody);
         StringAssert.Contains("\"addresses\":[\"c@" + name + "\"]", putBody);

         route = RouteOverCom(name);
         Assert.IsNotNull(route);
         Assert.AreEqual(id, route.ID, "An update keeps the route's id.");
         Assert.AreEqual("relay.example.test", route.TargetSMTPHost);
         Assert.AreEqual(2526, route.TargetSMTPPort);
         Assert.AreEqual(3, route.NumberOfTries, "Not named by the PUT, so the Control Panel's default for a new route.");
         Assert.AreEqual(10, route.MinutesBetweenTry);
         Assert.AreEqual("", route.Description);
         Assert.IsFalse(route.TreatRecipientAsLocalDomain);
         Assert.IsFalse(route.TreatSenderAsLocalDomain);
         Assert.AreEqual(eConnectionSecurity.eCSTLS, route.ConnectionSecurity);
         Assert.AreEqual(1, route.Addresses.Count);
         Assert.AreEqual("c@" + name, route.Addresses[0].Address);

         // A refused PUT leaves the running route as it was.
         (int refusedStatus, string refusedBody) = Http("PUT", "/api/v1/routes/" + id, RouteBody(name, "other.example.test", 70000));
         Assert.AreEqual(400, refusedStatus, refusedBody);
         Assert.AreEqual("relay.example.test", RouteOverCom(name).TargetSMTPHost);
         Assert.AreEqual(2526, RouteOverCom(name).TargetSMTPPort);

         // Deleted, with its addresses; gone through COM and from the listing.
         (int deleteStatus, string deleteBody) = Http("DELETE", "/api/v1/routes/" + id);
         Assert.AreEqual(200, deleteStatus, deleteBody);
         StringAssert.Contains("\"deleted\":true", deleteBody);
         Assert.IsNull(RouteOverCom(name), "The route must be gone through COM after DELETE.");
         StringAssert.DoesNotContain(name, Http("GET", "/api/v1/routes").body);

         Assert.AreEqual(404, Http("DELETE", "/api/v1/routes/" + id).status);
         Assert.AreEqual(404, Http("PUT", "/api/v1/routes/" + id, RouteBody(name, "127.0.0.1", 25)).status);
      }

      [Test]
      [Description("A route made over REST relays the next message to the host and port it names; a PUT that moves it is followed by the message after; the delete ends it.")]
      public void RouteRelaysMailAndFollowsAnUpdate()
      {
         int firstPort = TestSetup.GetNextFreePort();
         int secondPort = TestSetup.GetNextFreePort();

         Account sender = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sender@example.test", "test");

         var deliveryResults = new Dictionary<string, int> { { "dummy@" + RelayDomain, 250 } };

         (int status, string body) created = Http("POST", "/api/v1/routes",
            RouteBody(RelayDomain, "127.0.0.1", firstPort, ",\"number_of_tries\":1,\"minutes_between_try\":1"));
         Assert.AreEqual(201, created.status, created.body);
         int id = ExtractNumber(created.body, "id");
         Assert.AreEqual(firstPort, RouteOverCom(RelayDomain).TargetSMTPPort);

         // The message goes where the route points, with no restart and
         // nothing else touched: the route was added to the collection the
         // delivery code reads, as a route saved in the Control Panel is.
         using (var server = new SmtpServerSimulator(1, firstPort))
         {
            server.AddRecipientResult(deliveryResults);
            server.StartListen();

            string result;
            new SmtpClientSimulator().Send(false, sender.Address, "test", sender.Address, "dummy@" + RelayDomain,
               "Relayed by the route", "Relayed by the route", out result);

            server.WaitForCompletion();
            Assert.IsTrue(server.MessageData.Contains("Relayed by the route"), server.MessageData);
         }

         // Moved to another port by PUT; the next message follows it.
         (int putStatus, string putBody) = Http("PUT", "/api/v1/routes/" + id,
            RouteBody(RelayDomain, "127.0.0.1", secondPort, ",\"number_of_tries\":1,\"minutes_between_try\":1"));
         Assert.AreEqual(200, putStatus, putBody);
         Assert.AreEqual(secondPort, RouteOverCom(RelayDomain).TargetSMTPPort);

         using (var server = new SmtpServerSimulator(1, secondPort))
         {
            server.AddRecipientResult(deliveryResults);
            server.StartListen();

            string result;
            new SmtpClientSimulator().Send(false, sender.Address, "test", sender.Address, "dummy@" + RelayDomain,
               "Relayed after the move", "Relayed after the move", out result);

            server.WaitForCompletion();
            Assert.IsTrue(server.MessageData.Contains("Relayed after the move"), server.MessageData);
         }

         CustomAsserts.AssertRecipientsInDeliveryQueue(0);

         Assert.AreEqual(200, Http("DELETE", "/api/v1/routes/" + id).status);
         Assert.IsNull(RouteOverCom(RelayDomain));
      }

      [Test]
      [Description("A missing field, a field of the wrong type or range, an unknown field, a body that is not JSON and a duplicate domain are refused and create nothing; unknown ids are 404.")]
      public void RouteRefusals()
      {
         string name = UniqueRouteDomain();
         int before = _settings.Routes.Count;

         (int status, string body) noDomain = Http("POST", "/api/v1/routes", "{\"target_smtp_host\":\"127.0.0.1\"}");
         Assert.AreEqual(400, noDomain.status, noDomain.body);
         StringAssert.Contains("domain_name is required", noDomain.body);

         (int status, string body) noHost = Http("POST", "/api/v1/routes", "{\"domain_name\":\"" + name + "\"}");
         Assert.AreEqual(400, noHost.status, noHost.body);
         StringAssert.Contains("target_smtp_host is required", noHost.body);

         Assert.AreEqual(400, Http("POST", "/api/v1/routes", RouteBody(name, "127.0.0.1", 70000)).status, "A port out of range.");
         Assert.AreEqual(400, Http("POST", "/api/v1/routes", RouteBody(name, "127.0.0.1", 25, ",\"connection_security\":\"ssl\"")).status, "Not one of the four words.");
         Assert.AreEqual(400, Http("POST", "/api/v1/routes", RouteBody(name, "127.0.0.1", 25, ",\"all_addresses\":\"yes\"")).status, "A boolean given as a string.");
         Assert.AreEqual(400, Http("POST", "/api/v1/routes", RouteBody(name, "127.0.0.1", 25, ",\"addresses\":[\"not-an-address\"]")).status);
         Assert.AreEqual(400, Http("POST", "/api/v1/routes", RouteBody(name, "127.0.0.1", 25, ",\"relayer_requires_authentication\":true")).status,
            "Authentication without a user name.");

         (int status, string body) unknown = Http("POST", "/api/v1/routes", RouteBody(name, "127.0.0.1", 25, ",\"target_smpt_port\":25"));
         Assert.AreEqual(400, unknown.status, unknown.body);
         StringAssert.Contains("unknown field: target_smpt_port", unknown.body, "A misspelt key is named, not ignored.");

         Assert.AreEqual(400, Http("POST", "/api/v1/routes", "not json").status);
         Assert.AreEqual(404, Http("PUT", "/api/v1/routes/999999", RouteBody(name, "127.0.0.1", 25)).status);
         Assert.AreEqual(404, Http("DELETE", "/api/v1/routes/999999").status);

         Assert.IsNull(RouteOverCom(name), "No refused POST may have created the route.");
         Assert.AreEqual(before, _settings.Routes.Count);

         // A duplicate is a conflict, not a validation failure, whatever the case.
         (int status, string body) created = Http("POST", "/api/v1/routes", RouteBody(name, "127.0.0.1", 25));
         Assert.AreEqual(201, created.status, created.body);
         int id = ExtractNumber(created.body, "id");

         (int status, string body) duplicate = Http("POST", "/api/v1/routes", RouteBody(name.ToUpperInvariant(), "127.0.0.1", 26));
         Assert.AreEqual(409, duplicate.status, duplicate.body);
         Assert.AreEqual(before + 1, _settings.Routes.Count, "The duplicate POST must not have created a second route.");
         Assert.AreEqual(25, RouteOverCom(name).TargetSMTPPort, "The duplicate POST must not have changed the first route.");

         // Renaming a route onto another route's domain is the same conflict.
         string other = UniqueRouteDomain();
         (int status, string body) second = Http("POST", "/api/v1/routes", RouteBody(other, "127.0.0.1", 25));
         Assert.AreEqual(201, second.status, second.body);
         int secondId = ExtractNumber(second.body, "id");
         Assert.AreEqual(409, Http("PUT", "/api/v1/routes/" + id, RouteBody(other, "127.0.0.1", 25)).status);
         Assert.AreEqual(name, RouteOverCom(name).DomainName);

         Assert.AreEqual(200, Http("DELETE", "/api/v1/routes/" + secondId).status);
         Assert.AreEqual(200, Http("DELETE", "/api/v1/routes/" + id).status);
      }

      [Test]
      [Description("Routes are server-wide: a domain-restricted key is refused every route verb whatever its scope, a read-only key may only list, an unrestricted full key may do everything, and no credential is 401.")]
      public void RouteAuthorisation()
      {
         string name = UniqueRouteDomain();

         (string scopedId, string scopedKey) = CreateKey("restroute - scoped", "full", "example.test");
         (string readOnlyId, string readOnlyKey) = CreateKey("restroute - readonly", "readonly", null);
         (string fullId, string fullKey) = CreateKey("restroute - full", "full", null);

         Assert.AreEqual(403, Bearer("GET", "/api/v1/routes", scopedKey).status, "The listing names every domain's relay.");
         Assert.AreEqual(403, Bearer("POST", "/api/v1/routes", scopedKey, RouteBody(name, "127.0.0.1", 25)).status);
         Assert.AreEqual(403, Bearer("PUT", "/api/v1/routes/1", scopedKey, RouteBody(name, "127.0.0.1", 25)).status);
         Assert.AreEqual(403, Bearer("DELETE", "/api/v1/routes/1", scopedKey).status);
         Assert.IsNull(RouteOverCom(name), "The refused POST must not have created the route.");

         Assert.AreEqual(200, Bearer("GET", "/api/v1/routes", readOnlyKey).status);
         Assert.AreEqual(403, Bearer("POST", "/api/v1/routes", readOnlyKey, RouteBody(name, "127.0.0.1", 25)).status);
         Assert.AreEqual(403, Bearer("PUT", "/api/v1/routes/1", readOnlyKey, RouteBody(name, "127.0.0.1", 25)).status);
         Assert.AreEqual(403, Bearer("DELETE", "/api/v1/routes/1", readOnlyKey).status);
         Assert.IsNull(RouteOverCom(name));

         (int status, string body) created = Bearer("POST", "/api/v1/routes", fullKey, RouteBody(name, "127.0.0.1", 25));
         Assert.AreEqual(201, created.status, created.body);
         int id = ExtractNumber(created.body, "id");
         Assert.IsNotNull(RouteOverCom(name));
         Assert.AreEqual(200, Bearer("PUT", "/api/v1/routes/" + id, fullKey, RouteBody(name, "127.0.0.1", 26)).status);
         Assert.AreEqual(26, RouteOverCom(name).TargetSMTPPort);
         Assert.AreEqual(200, Bearer("DELETE", "/api/v1/routes/" + id, fullKey).status);
         Assert.IsNull(RouteOverCom(name));

         Assert.AreEqual(401, Http("POST", "/api/v1/routes", null, RouteBody(name, "127.0.0.1", 25)).status);
         Assert.IsNull(RouteOverCom(name));
      }

      [Test]
      [Description("An alias is created, read back through COM, listed, delivers the next message to its target, is switched off by active false, deleted, and gone; a message to it is then refused.")]
      public void AliasRoundTripDelivers()
      {
         Account target = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "tux@example.test", "test");
         string name = UniqueAliasName();

         (int status, string body) created = Http("POST", "/api/v1/domains/example.test/aliases",
            "{\"name\":\"" + name + "\",\"value\":\"" + target.Address + "\"}");
         Assert.AreEqual(201, created.status, created.body);
         Assert.AreEqual("{\"name\":\"" + name + "\",\"value\":\"" + target.Address + "\",\"active\":true}", created.body,
            "The entry as GET /api/v1/domains/{domain}/aliases emits it.");

         Alias alias = AliasOverCom(name);
         Assert.IsNotNull(alias, "The alias must exist through COM after the POST.");
         Assert.AreEqual(name, alias.Name);
         Assert.AreEqual(target.Address, alias.Value);
         Assert.IsTrue(alias.Active);

         StringAssert.Contains(created.body, Http("GET", "/api/v1/domains/example.test/aliases").body);

         // It delivers: the alias is in effect for the next RCPT TO, because
         // PersistentAlias::SaveObject dropped the name from the alias cache.
         new SmtpClientSimulator().Send("someone@dummy-example.com", name, "Through the alias", "Through the alias");
         string text = Pop3ClientSimulator.AssertGetFirstMessageText(target.Address, "test");
         StringAssert.Contains("Through the alias", text);

         // An inactive alias is stored as such and refuses mail, as the Control Panel's checkbox does.
         string inactive = UniqueAliasName();
         (int offStatus, string offBody) = Http("POST", "/api/v1/domains/example.test/aliases",
            "{\"name\":\"" + inactive + "\",\"value\":\"" + target.Address + "\",\"active\":false}");
         Assert.AreEqual(201, offStatus, offBody);
         StringAssert.Contains("\"active\":false", offBody);
         Assert.IsFalse(AliasOverCom(inactive).Active);
         CustomAsserts.Throws<DeliveryFailedException>(() =>
            new SmtpClientSimulator().Send("someone@dummy-example.com", inactive, "To an inactive alias", "To an inactive alias"));

         // Deleted, and gone: through COM, from the listing, and to the SMTP client.
         (int deleteStatus, string deleteBody) = Http("DELETE", "/api/v1/aliases/" + name);
         Assert.AreEqual(200, deleteStatus, deleteBody);
         StringAssert.Contains("\"deleted\":true", deleteBody);
         Assert.IsNull(AliasOverCom(name), "The alias must be gone through COM after DELETE.");
         StringAssert.DoesNotContain(name, Http("GET", "/api/v1/domains/example.test/aliases").body);
         Assert.AreEqual(404, Http("DELETE", "/api/v1/aliases/" + name).status);
         CustomAsserts.Throws<DeliveryFailedException>(() =>
            new SmtpClientSimulator().Send("someone@dummy-example.com", name, "After the delete", "After the delete"));

         Assert.AreEqual(200, Http("DELETE", "/api/v1/aliases/" + inactive).status);
      }

      [Test]
      [Description("A name outside the domain, a missing name or value, a value that is not an address, an unknown field, a name an account already has, an unknown domain and a duplicate are refused, and none of them creates an alias.")]
      public void AliasRefusals()
      {
         Account account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "tux@example.test", "test");
         string name = UniqueAliasName();
         int before = _domain.Aliases.Count;

         (int status, string body) outside = Http("POST", "/api/v1/domains/example.test/aliases",
            "{\"name\":\"" + AliasPrefix + "x@other.test\",\"value\":\"" + account.Address + "\"}");
         Assert.AreEqual(400, outside.status, outside.body);
         StringAssert.Contains("in the domain", outside.body);

         Assert.AreEqual(400, Http("POST", "/api/v1/domains/example.test/aliases", "{\"name\":\"" + name + "\"}").status, "value missing.");
         Assert.AreEqual(400, Http("POST", "/api/v1/domains/example.test/aliases", "{\"value\":\"" + account.Address + "\"}").status, "name missing.");
         Assert.AreEqual(400, Http("POST", "/api/v1/domains/example.test/aliases", "{\"name\":\"" + name + "\",\"value\":\"tux\"}").status, "value not an address.");
         Assert.AreEqual(400, Http("POST", "/api/v1/domains/example.test/aliases", "{\"name\":\"" + name + "\",\"value\":\"" + account.Address + "\",\"activ\":true}").status, "unknown field.");
         Assert.AreEqual(400, Http("POST", "/api/v1/domains/example.test/aliases", "[1,2]").status);
         Assert.AreEqual(404, Http("POST", "/api/v1/domains/restnope.test/aliases", "{\"name\":\"a@restnope.test\",\"value\":\"" + account.Address + "\"}").status);

         // A name an account already has is refused by the limitation check
         // COM runs, with its own sentence - the one the Control Panel shows.
         (int status, string body) taken = Http("POST", "/api/v1/domains/example.test/aliases",
            "{\"name\":\"" + account.Address + "\",\"value\":\"" + name + "\"}");
         Assert.AreEqual(400, taken.status, taken.body);
         StringAssert.Contains("already exists", taken.body);

         Assert.AreEqual(before, _domain.Aliases.Count, "No refused POST may have created an alias.");

         (int status, string body) created = Http("POST", "/api/v1/domains/example.test/aliases",
            "{\"name\":\"" + name + "\",\"value\":\"" + account.Address + "\"}");
         Assert.AreEqual(201, created.status, created.body);
         Assert.AreEqual(409, Http("POST", "/api/v1/domains/example.test/aliases",
            "{\"name\":\"" + name.ToUpperInvariant() + "\",\"value\":\"" + account.Address + "\"}").status, "A duplicate, whatever the case.");
         Assert.AreEqual(before + 1, _domain.Aliases.Count);

         Assert.AreEqual(404, Http("DELETE", "/api/v1/aliases/nobody@restnope.test").status);
         Assert.AreEqual(200, Http("DELETE", "/api/v1/aliases/" + name).status);
      }

      [Test]
      [Description("Alias writes are scoped to the domain: a key issued for another domain is refused, a key issued for this one is allowed, a read-only key changes nothing, and no credential is 401.")]
      public void AliasAuthorisation()
      {
         Account account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "tux@example.test", "test");
         string name = UniqueAliasName();
         string body = "{\"name\":\"" + name + "\",\"value\":\"" + account.Address + "\"}";

         (string otherId, string otherKey) = CreateKey("restalias - other domain", "full", "restother.test");
         (string ownId, string ownKey) = CreateKey("restalias - own domain", "full", "example.test");
         (string readOnlyId, string readOnlyKey) = CreateKey("restalias - readonly", "readonly", null);

         (int status, string refused) = Bearer("POST", "/api/v1/domains/example.test/aliases", otherKey, body);
         Assert.AreEqual(403, status, "A key for another domain must not create an alias here. Body: " + refused);
         Assert.IsNull(AliasOverCom(name));

         Assert.AreEqual(403, Bearer("POST", "/api/v1/domains/example.test/aliases", readOnlyKey, body).status);
         Assert.IsNull(AliasOverCom(name));

         Assert.AreEqual(401, Http("POST", "/api/v1/domains/example.test/aliases", null, body).status);
         Assert.IsNull(AliasOverCom(name));

         (int ownStatus, string ownBody) = Bearer("POST", "/api/v1/domains/example.test/aliases", ownKey, body);
         Assert.AreEqual(201, ownStatus, ownBody);
         Assert.IsNotNull(AliasOverCom(name));

         // The delete is scoped by the address's domain, which is what stops a
         // key for one customer deleting another's alias by editing the path.
         Assert.AreEqual(403, Bearer("DELETE", "/api/v1/aliases/" + name, otherKey).status);
         Assert.AreEqual(403, Bearer("DELETE", "/api/v1/aliases/" + name, readOnlyKey).status);
         Assert.IsNotNull(AliasOverCom(name), "The refused DELETEs must not have removed the alias.");

         Assert.AreEqual(200, Bearer("DELETE", "/api/v1/aliases/" + name, ownKey).status);
         Assert.IsNull(AliasOverCom(name));
      }

      [Test]
      [Description("PUT /api/v1/accounts/<address> changes only what the body names; every field is read back through COM; a new password is the one IMAP accepts and the old one is refused; active false refuses the logon.")]
      public void AccountUpdate()
      {
         Account account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "tux@example.test", "test");

         (int status, string body) updated = Http("PUT", "/api/v1/accounts/" + account.Address,
            "{\"max_size_mb\":77,\"first_name\":\"Tux\",\"last_name\":\"Linux\"," +
            "\"signature_enabled\":true,\"signature_plain_text\":\"-- Tux\",\"signature_html\":\"<b>Tux</b>\"," +
            "\"forward_enabled\":true,\"forward_address\":\"boss@example.test\",\"forward_keep_original\":true}");
         Assert.AreEqual(200, updated.status, updated.body);
         StringAssert.StartsWith("{\"address\":\"" + account.Address + "\",\"active\":true,", updated.body,
            "The listing's fields first, in its order.");
         StringAssert.Contains("\"max_size_mb\":77", updated.body);
         StringAssert.Contains("\"admin_level\":\"user\"", updated.body);
         StringAssert.DoesNotContain("password", updated.body, "No password, hashed or otherwise.");

         Account reread = AccountOverCom(account.Address);
         Assert.AreEqual(77, reread.MaxSize);
         Assert.AreEqual("Tux", reread.PersonFirstName);
         Assert.AreEqual("Linux", reread.PersonLastName);
         Assert.IsTrue(reread.SignatureEnabled);
         Assert.AreEqual("-- Tux", reread.SignaturePlainText);
         Assert.AreEqual("<b>Tux</b>", reread.SignatureHTML);
         Assert.IsTrue(reread.ForwardEnabled);
         Assert.AreEqual("boss@example.test", reread.ForwardAddress);
         Assert.IsTrue(reread.ForwardKeepOriginal);
         Assert.IsTrue(reread.Active);
         Assert.AreEqual(eAdminLevel.hAdminLevelNormal, reread.AdminLevel);

         // A body naming one field leaves every other as it was.
         Assert.AreEqual(200, Http("PUT", "/api/v1/accounts/" + account.Address, "{\"last_name\":\"Penguin\"}").status);
         reread = AccountOverCom(account.Address);
         Assert.AreEqual("Penguin", reread.PersonLastName);
         Assert.AreEqual("Tux", reread.PersonFirstName);
         Assert.AreEqual(77, reread.MaxSize);
         Assert.IsTrue(reread.ForwardEnabled);

         // The password: hashed as InterfaceAccount::put_Password hashes it,
         // and the next logon reads it because the account cache was dropped.
         Assert.IsTrue(ImapClientSimulator.ValidatePassword(account.Address, "test"));
         (int passwordStatus, string passwordBody) = Http("PUT", "/api/v1/accounts/" + account.Address, "{\"password\":\"New-Tux-Pass-5678\"}");
         Assert.AreEqual(200, passwordStatus, passwordBody);
         Assert.IsFalse(ImapClientSimulator.ValidatePassword(account.Address, "test"), "The old password must be refused.");
         Assert.IsTrue(ImapClientSimulator.ValidatePassword(account.Address, "New-Tux-Pass-5678"), "The new password must log on over IMAP.");
         Assert.IsTrue(AccountOverCom(account.Address).ValidatePassword("New-Tux-Pass-5678"), "COM validates the same hash.");

         // active false refuses the logon; active true restores it.
         (int offStatus, string offBody) = Http("PUT", "/api/v1/accounts/" + account.Address, "{\"active\":false}");
         Assert.AreEqual(200, offStatus, offBody);
         StringAssert.Contains("\"active\":false", offBody);
         Assert.IsFalse(AccountOverCom(account.Address).Active);
         Assert.IsFalse(ImapClientSimulator.ValidatePassword(account.Address, "New-Tux-Pass-5678"), "An inactive account must not log on.");
         StringAssert.Contains("{\"address\":\"" + account.Address + "\",\"active\":false}", Http("GET", "/api/v1/domains/example.test/accounts").body);

         Assert.AreEqual(200, Http("PUT", "/api/v1/accounts/" + account.Address, "{\"active\":true}").status);
         Assert.IsTrue(AccountOverCom(account.Address).Active);
         Assert.IsTrue(ImapClientSimulator.ValidatePassword(account.Address, "New-Tux-Pass-5678"));
      }

      [Test]
      [Description("An unknown field, an empty body, a body that is not JSON, an admin_level that is not a level, a negative size, a forwarding without an address or to the account itself, an empty password and an unknown account are refused and change nothing.")]
      public void AccountUpdateRefusals()
      {
         Account account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "tux@example.test", "test");
         Assert.AreEqual(200, Http("PUT", "/api/v1/accounts/" + account.Address, "{\"first_name\":\"Tux\",\"max_size_mb\":5}").status);

         (int status, string body) unknown = Http("PUT", "/api/v1/accounts/" + account.Address, "{\"colour\":\"blue\"}");
         Assert.AreEqual(400, unknown.status, unknown.body);
         StringAssert.Contains("unknown field: colour", unknown.body);

         Assert.AreEqual(400, Http("PUT", "/api/v1/accounts/" + account.Address, "{}").status, "Nothing to update.");
         Assert.AreEqual(400, Http("PUT", "/api/v1/accounts/" + account.Address, "garbage").status);
         Assert.AreEqual(400, Http("PUT", "/api/v1/accounts/" + account.Address, "{\"max_size_mb\":-1}").status);
         Assert.AreEqual(400, Http("PUT", "/api/v1/accounts/" + account.Address, "{\"max_size_mb\":\"big\"}").status);
         Assert.AreEqual(400, Http("PUT", "/api/v1/accounts/" + account.Address, "{\"active\":\"yes\"}").status);
         Assert.AreEqual(400, Http("PUT", "/api/v1/accounts/" + account.Address, "{\"forward_enabled\":true}").status, "Forwarding needs an address.");
         Assert.AreEqual(400, Http("PUT", "/api/v1/accounts/" + account.Address,
            "{\"forward_enabled\":true,\"forward_address\":\"" + account.Address.ToUpperInvariant() + "\"}").status, "Forwarding to itself would loop.");
         Assert.AreEqual(400, Http("PUT", "/api/v1/accounts/" + account.Address, "{\"password\":\"\"}").status);
         Assert.AreEqual(404, Http("PUT", "/api/v1/accounts/nobody@example.test", "{\"active\":true}").status);

         // Only the three words are levels, as InterfaceAccount::put_AdminLevel
         // refuses a number that is none of them.
         (int levelStatus, string levelBody) = Http("PUT", "/api/v1/accounts/" + account.Address, "{\"admin_level\":\"root\"}");
         Assert.AreEqual(400, levelStatus, levelBody);
         StringAssert.Contains("admin_level must be user, domain or server", levelBody);
         Assert.AreEqual(400, Http("PUT", "/api/v1/accounts/" + account.Address, "{\"admin_level\":2}").status);

         Account reread = AccountOverCom(account.Address);
         Assert.AreEqual("Tux", reread.PersonFirstName, "No refused PUT may have changed the account.");
         Assert.AreEqual(5, reread.MaxSize);
         Assert.IsFalse(reread.ForwardEnabled);
         Assert.AreEqual(eAdminLevel.hAdminLevelNormal, reread.AdminLevel);
         Assert.IsTrue(ImapClientSimulator.ValidatePassword(account.Address, "test"));
      }

      [Test]
      [Description("The administrator password sets admin_level to domain, server and back to user, read back through COM, and may update an account that is a server administrator; the level is persisted as InterfaceAccount::put_AdminLevel + Save persist it.")]
      public void AccountAdminLevelByTheAdministrator()
      {
         Account account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "tux@example.test", "test");

         (int domainStatus, string domainBody) = Http("PUT", "/api/v1/accounts/" + account.Address, "{\"admin_level\":\"domain\"}");
         Assert.AreEqual(200, domainStatus, domainBody);
         StringAssert.Contains("\"admin_level\":\"domain\"", domainBody);
         Assert.AreEqual(eAdminLevel.hAdminLevelDomainAdmin, AccountOverCom(account.Address).AdminLevel);

         (int serverStatus, string serverBody) = Http("PUT", "/api/v1/accounts/" + account.Address, "{\"admin_level\":\"server\",\"first_name\":\"Root\"}");
         Assert.AreEqual(200, serverStatus, serverBody);
         StringAssert.Contains("\"admin_level\":\"server\"", serverBody);
         Assert.AreEqual(eAdminLevel.hAdminLevelServerAdmin, AccountOverCom(account.Address).AdminLevel);
         Assert.AreEqual("Root", AccountOverCom(account.Address).PersonFirstName);

         // Now a server administrator: the administrator password may still
         // update it, level included - it is the credential COM's Save allows.
         (int stillStatus, string stillBody) = Http("PUT", "/api/v1/accounts/" + account.Address, "{\"last_name\":\"Admin\"}");
         Assert.AreEqual(200, stillStatus, stillBody);
         Assert.AreEqual("Admin", AccountOverCom(account.Address).PersonLastName);

         (int userStatus, string userBody) = Http("PUT", "/api/v1/accounts/" + account.Address, "{\"admin_level\":\"user\"}");
         Assert.AreEqual(200, userStatus, userBody);
         StringAssert.Contains("\"admin_level\":\"user\"", userBody);
         Assert.AreEqual(eAdminLevel.hAdminLevelNormal, AccountOverCom(account.Address).AdminLevel);

         // One made a server administrator over COM is updated the same way.
         Account admin = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "restadmin@example.test", "test");
         admin.AdminLevel = eAdminLevel.hAdminLevelServerAdmin;
         admin.Save();

         (int adminStatus, string adminBody) = Http("PUT", "/api/v1/accounts/" + admin.Address, "{\"active\":false}");
         Assert.AreEqual(200, adminStatus, adminBody);
         Assert.IsFalse(AccountOverCom(admin.Address).Active);
         Assert.AreEqual(eAdminLevel.hAdminLevelServerAdmin, AccountOverCom(admin.Address).AdminLevel, "A body that does not name admin_level leaves it.");
      }

      [Test]
      [Description("The account update is scoped to the address's domain: a key for another domain is refused, a key for this one is allowed, a read-only key changes nothing, and no credential is 401. A domain-restricted key carries the domain administrator's authority: it may set admin_level user or domain, is refused server, and may not touch an account that is a server administrator - while an unrestricted key may do all of it.")]
      public void AccountUpdateAuthorisation()
      {
         Account account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "tux@example.test", "test");

         (string otherId, string otherKey) = CreateKey("restaccount - other domain", "full", "restother.test");
         (string ownId, string ownKey) = CreateKey("restaccount - own domain", "full", "example.test");
         (string readOnlyId, string readOnlyKey) = CreateKey("restaccount - readonly", "readonly", null);
         (string fullId, string fullKey) = CreateKey("restaccount - every domain", "full", null);

         Assert.AreEqual(403, Bearer("PUT", "/api/v1/accounts/" + account.Address, otherKey, "{\"first_name\":\"Other\"}").status);
         Assert.AreEqual(403, Bearer("PUT", "/api/v1/accounts/" + account.Address, readOnlyKey, "{\"first_name\":\"ReadOnly\"}").status);
         Assert.AreEqual(401, Http("PUT", "/api/v1/accounts/" + account.Address, null, "{\"first_name\":\"Nobody\"}").status);
         Assert.AreEqual("", AccountOverCom(account.Address).PersonFirstName, "No refused PUT may have changed the account.");

         (int status, string body) own = Bearer("PUT", "/api/v1/accounts/" + account.Address, ownKey, "{\"first_name\":\"Own\"}");
         Assert.AreEqual(200, own.status, own.body);
         Assert.AreEqual("Own", AccountOverCom(account.Address).PersonFirstName);

         // The domain administrator's authority, as InterfaceAccount::put_AdminLevel
         // grants it: user and domain, never server.
         Assert.AreEqual(200, Bearer("PUT", "/api/v1/accounts/" + account.Address, ownKey, "{\"admin_level\":\"domain\"}").status);
         Assert.AreEqual(eAdminLevel.hAdminLevelDomainAdmin, AccountOverCom(account.Address).AdminLevel);

         (int status, string body) promoted = Bearer("PUT", "/api/v1/accounts/" + account.Address, ownKey, "{\"admin_level\":\"server\"}");
         Assert.AreEqual(403, promoted.status, "A domain-restricted key must not make a server administrator. Body: " + promoted.body);
         StringAssert.Contains("restricted to named domains", promoted.body);
         Assert.AreEqual(eAdminLevel.hAdminLevelDomainAdmin, AccountOverCom(account.Address).AdminLevel, "The refused PUT must not have changed the level.");

         Assert.AreEqual(200, Bearer("PUT", "/api/v1/accounts/" + account.Address, ownKey, "{\"admin_level\":\"user\"}").status);
         Assert.AreEqual(eAdminLevel.hAdminLevelNormal, AccountOverCom(account.Address).AdminLevel);

         // An unrestricted key carries the server administrator's authority.
         (int status, string body) byFull = Bearer("PUT", "/api/v1/accounts/" + account.Address, fullKey, "{\"admin_level\":\"server\"}");
         Assert.AreEqual(200, byFull.status, byFull.body);
         Assert.AreEqual(eAdminLevel.hAdminLevelServerAdmin, AccountOverCom(account.Address).AdminLevel);

         // ...and a server administrator's account is beyond a domain-restricted
         // key altogether: InterfaceAccount::Save's guard, with the caller known.
         (int status, string body) touched = Bearer("PUT", "/api/v1/accounts/" + account.Address, ownKey, "{\"first_name\":\"Taken\"}");
         Assert.AreEqual(403, touched.status, "A domain-restricted key must not update a server administrator's account. Body: " + touched.body);
         StringAssert.Contains("server administrator", touched.body);
         Assert.AreEqual("Own", AccountOverCom(account.Address).PersonFirstName, "The refused PUT must not have changed the account.");
         Assert.AreEqual(403, Bearer("PUT", "/api/v1/accounts/" + account.Address, ownKey, "{\"password\":\"Taken-Over-1234\"}").status);
         Assert.IsTrue(ImapClientSimulator.ValidatePassword(account.Address, "test"), "The refused PUT must not have changed the password.");
         Assert.AreEqual(403, Bearer("PUT", "/api/v1/accounts/" + account.Address, ownKey, "{\"admin_level\":\"user\"}").status, "Nor demote it.");
         Assert.AreEqual(eAdminLevel.hAdminLevelServerAdmin, AccountOverCom(account.Address).AdminLevel);

         Assert.AreEqual(200, Bearer("PUT", "/api/v1/accounts/" + account.Address, fullKey, "{\"admin_level\":\"user\"}").status);
         Assert.AreEqual(eAdminLevel.hAdminLevelNormal, AccountOverCom(account.Address).AdminLevel);
         Assert.AreEqual(200, Bearer("PUT", "/api/v1/accounts/" + account.Address, ownKey, "{\"first_name\":\"Again\"}").status, "A user again, so the domain key reaches it again.");
         Assert.AreEqual("Again", AccountOverCom(account.Address).PersonFirstName);

         // One made a server administrator over COM: the same two answers.
         Account admin = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "restadmin@example.test", "test");
         admin.AdminLevel = eAdminLevel.hAdminLevelServerAdmin;
         admin.Save();
         Assert.AreEqual(403, Bearer("PUT", "/api/v1/accounts/" + admin.Address, ownKey, "{\"active\":false}").status);
         Assert.IsTrue(AccountOverCom(admin.Address).Active);
         Assert.AreEqual(200, Bearer("PUT", "/api/v1/accounts/" + admin.Address, fullKey, "{\"active\":false}").status);
         Assert.IsFalse(AccountOverCom(admin.Address).Active);
      }

      [Test]
      [Description("POST /api/v1/domains/<domain>/accounts honours max_size_mb, active, first_name and last_name, as the OpenAPI document has advertised the size since the route existed; a negative size is refused and creates nothing.")]
      public void CreateAccountHonoursOptionalFields()
      {
         (int status, string body) created = Http("POST", "/api/v1/domains/example.test/accounts",
            "{\"address\":\"created@example.test\",\"password\":\"Created-Pass-1234\",\"max_size_mb\":33,\"active\":false,\"first_name\":\"Cre\",\"last_name\":\"Ated\"}");
         Assert.AreEqual(201, created.status, created.body);

         Account account = AccountOverCom("created@example.test");
         Assert.IsNotNull(account);
         Assert.AreEqual(33, account.MaxSize);
         Assert.IsFalse(account.Active);
         Assert.AreEqual("Cre", account.PersonFirstName);
         Assert.AreEqual("Ated", account.PersonLastName);

         // The name the OpenAPI document spelt it with is accepted as well.
         (int camelStatus, string camelBody) = Http("POST", "/api/v1/domains/example.test/accounts",
            "{\"address\":\"camel@example.test\",\"password\":\"Camel-Pass-1234\",\"maxSizeMB\":9}");
         Assert.AreEqual(201, camelStatus, camelBody);
         Assert.AreEqual(9, AccountOverCom("camel@example.test").MaxSize);
         Assert.IsTrue(AccountOverCom("camel@example.test").Active, "active defaults to true, as InterfaceAccounts::Add leaves it.");

         (int negativeStatus, string negativeBody) = Http("POST", "/api/v1/domains/example.test/accounts",
            "{\"address\":\"negative@example.test\",\"password\":\"Negative-Pass-1234\",\"max_size_mb\":-5}");
         Assert.AreEqual(400, negativeStatus, negativeBody);
         Assert.IsNull(AccountOverCom("negative@example.test"), "The refused POST must not have created the account.");
      }

      private (string id, string key) CreateKey(string label, string scope, string domains)
      {
         string body = "{\"label\":\"" + label + "\",\"scope\":\"" + scope + "\"" +
                       (domains == null ? "" : ",\"domains\":\"" + domains + "\"") + "}";
         (int status, string created) = Http("POST", "/api/v1/apikeys", body);
         Assert.AreEqual(201, status, "POST /api/v1/apikeys must create a key. Body: " + created);
         string id = Extract(created, "id");
         _keyIds.Add(id);
         return (id, Extract(created, "key"));
      }

      // The string value of a top-level JSON property, enough for the bodies this API returns.
      private static string Extract(string json, string key)
      {
         Match match = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
         Assert.IsTrue(match.Success, "No '" + key + "' in: " + json);
         return match.Groups[1].Value;
      }

      private static int ExtractNumber(string json, string key)
      {
         Match match = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(\\d+)");
         Assert.IsTrue(match.Success, "No numeric '" + key + "' in: " + json);
         return int.Parse(match.Groups[1].Value);
      }

      private static (int status, string body) Http(string method, string path, string requestBody = null)
      {
         string credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes("Administrator:" + AdminPassword));
         return Http(method, path, "Basic " + credentials, requestBody);
      }

      private static (int status, string body) Bearer(string method, string path, string token, string requestBody = null)
      {
         return Http(method, path, "Bearer " + token, requestBody);
      }

      // Issues one HTTP/1.0 request against the REST listener and returns the
      // parsed status code and body. authorization is the complete header value
      // or null to send none. The connect is retried briefly to absorb the
      // listener bind race right after a reinitialize.
      private static (int status, string body) Http(string method, string path, string authorization, string requestBody)
      {
         using (var client = new TcpClient())
         {
            Exception last = null;
            for (int attempt = 0; attempt < 25; attempt++)
            {
               try
               {
                  client.Connect("127.0.0.1", RestPort);
                  last = null;
                  break;
               }
               catch (SocketException ex)
               {
                  last = ex;
                  Thread.Sleep(200);
               }
            }

            if (last != null)
               throw last;

            using (NetworkStream stream = client.GetStream())
            using (var memory = new MemoryStream())
            {
               var headers = new StringBuilder();
               headers.Append(method + " " + path + " HTTP/1.0\r\n");
               headers.Append("Host: 127.0.0.1\r\n");
               if (authorization != null)
                  headers.Append("Authorization: " + authorization + "\r\n");

               byte[] bodyBytes = requestBody == null ? new byte[0] : Encoding.UTF8.GetBytes(requestBody);
               if (requestBody != null)
               {
                  headers.Append("Content-Type: application/json\r\n");
                  headers.Append("Content-Length: " + bodyBytes.Length + "\r\n");
               }

               headers.Append("Connection: close\r\n\r\n");

               byte[] headerBytes = Encoding.ASCII.GetBytes(headers.ToString());
               stream.Write(headerBytes, 0, headerBytes.Length);
               if (bodyBytes.Length > 0)
                  stream.Write(bodyBytes, 0, bodyBytes.Length);

               byte[] buffer = new byte[4096];
               int read;
               while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                  memory.Write(buffer, 0, read);

               string raw = Encoding.UTF8.GetString(memory.ToArray());

               int statusCode = 0;
               string[] lines = raw.Split(new[] { "\r\n" }, StringSplitOptions.None);
               if (lines.Length > 0)
               {
                  string[] parts = lines[0].Split(' ');
                  if (parts.Length >= 2)
                     int.TryParse(parts[1], out statusCode);
               }

               int separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
               string body = separator >= 0 ? raw.Substring(separator + 4) : "";

               return (statusCode, body);
            }
         }
      }
   }
}
