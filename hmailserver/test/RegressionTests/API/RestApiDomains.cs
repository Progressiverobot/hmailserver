// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
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
   ///    The domain's own write routes on the REST API: POST /api/v1/domains,
   ///    PUT /api/v1/domains/&lt;name&gt; and DELETE /api/v1/domains/&lt;name&gt;.
   ///    Until these existed the first domain on a Linux installation was an
   ///    INSERT INTO hm_domains, because nothing but COM could create one.
   ///
   ///    Every assertion is made against what the server did, read back through
   ///    COM as the Control Panel would read it, and never only against the
   ///    response: a 201 for a domain that is not there is the failure shape
   ///    the account route once had. The authorisation tests pin the rule that
   ///    matters for a route that decides which domains exist at all: a key
   ///    issued for named domains is refused, whatever its scope, and a
   ///    read-only key changes nothing.
   ///
   ///    Domain names are unique per test and never "example.test", which every
   ///    other fixture in the suite runs on.
   /// </summary>
   [TestFixture]
   public class RestApiDomains : TestFixtureBase
   {
      // The port the listener answers on: this one on the Windows bench, the
      // suite's own where RestListener finds one already on.
      private static int RestPort = 9098;
      private const string AdminPassword = "testar";

      // Every domain this fixture creates starts with this, so TearDown can
      // remove whatever a failed assertion left behind without touching the
      // suite's own domain.
      private const string DomainPrefix = "restdom-";

      private static string UniqueDomainName()
      {
         return DomainPrefix + Guid.NewGuid().ToString("N").Substring(0, 8) + ".test";
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

         RestPort = RestListener.Start(RestPort);

         _application.Reinitialize();

         (int status, string body) probe = Http("GET", "/api/v1/status");
         Assert.AreEqual(200, probe.status, "REST API did not answer /api/v1/status. Body: " + probe.body);
      }

      [TearDown]
      public void StopRestApi()
      {
         // The domains first, over COM, so that a test that failed between its
         // create and its delete does not leave a domain for the next fixture
         // to find. PerformBasicSetup clears every domain before each test as
         // well; this is for the run that stops here.
         try
         {
            hMailServer.Domains domains = _application.Domains;
            for (int i = domains.Count - 1; i >= 0; i--)
            {
               Domain domain = domains[i];
               if (domain.Name.StartsWith(DomainPrefix, StringComparison.OrdinalIgnoreCase))
                  domain.Delete();
            }
         }
         finally
         {
            RestListener.Stop();
            _application.Reinitialize();
         }
      }

      // The domain through COM, or null. Asked of COM rather than of the API
      // so that the API's answer is checked against something that did not
      // produce it.
      private Domain DomainOverCom(string name)
      {
         try
         {
            return _application.Domains.get_ItemByName(name);
         }
         catch (System.Runtime.InteropServices.COMException)
         {
            return null;
         }
      }

      [Test]
      [Description("A domain is created, listed, read back through COM, refused as a duplicate, switched off and on, deleted, and gone; a second delete is 404.")]
      public void DomainRoundTrip()
      {
         string name = UniqueDomainName();

         (int status, string body) created = Http("POST", "/api/v1/domains",
            "{\"name\":\"" + name + "\",\"active\":true,\"postmaster\":\"postmaster@" + name + "\"}");
         Assert.AreEqual(201, created.status, created.body);
         StringAssert.Contains("\"name\":\"" + name + "\"", created.body);
         StringAssert.Contains("\"active\":true", created.body);
         StringAssert.Contains("\"postmaster\":\"postmaster@" + name + "\"", created.body);

         // The listing shows it, as the same entry the create answered with.
         (int listStatus, string list) = Http("GET", "/api/v1/domains");
         Assert.AreEqual(200, listStatus, list);
         StringAssert.Contains("\"name\":\"" + name + "\",\"active\":true", list);

         // COM sees the same domain the Control Panel would show, with every
         // default a domain made there gets.
         Domain domain = DomainOverCom(name);
         Assert.IsNotNull(domain, "The domain must exist through COM after POST /api/v1/domains.");
         Assert.AreEqual(name, domain.Name);
         Assert.IsTrue(domain.Active);
         Assert.AreEqual("postmaster@" + name, domain.Postmaster);
         Assert.AreEqual("+", domain.PlusAddressingCharacter, "The default a new domain gets in the Control Panel.");

         // It is a real domain: an account can be created in it over the API,
         // which is what the packaging README's first-domain recipe needs.
         (int accountStatus, string accountBody) = Http("POST", "/api/v1/domains/" + name + "/accounts",
            "{\"address\":\"first@" + name + "\",\"password\":\"S0me-Long-Passphrase\"}");
         Assert.AreEqual(201, accountStatus, accountBody);

         // Creating it again is a conflict, not a validation failure, and the
         // domain is unchanged by the attempt.
         (int duplicateStatus, string duplicateBody) = Http("POST", "/api/v1/domains", "{\"name\":\"" + name.ToUpperInvariant() + "\"}");
         Assert.AreEqual(409, duplicateStatus, duplicateBody);
         Assert.AreEqual(1, _application.Domains.Count - CountOtherDomains(name),
            "The duplicate POST must not have created a second domain under the same name.");

         // Switched off: the response, the listing and COM all agree; the
         // postmaster, not named in the body, is left alone.
         (int offStatus, string offBody) = Http("PUT", "/api/v1/domains/" + name, "{\"active\":false}");
         Assert.AreEqual(200, offStatus, offBody);
         StringAssert.Contains("\"active\":false", offBody);
         StringAssert.Contains("\"name\":\"" + name + "\",\"active\":false", Http("GET", "/api/v1/domains").body);
         Assert.IsFalse(DomainOverCom(name).Active);
         Assert.AreEqual("postmaster@" + name, DomainOverCom(name).Postmaster);

         // Switched on again with a new postmaster.
         (int onStatus, string onBody) = Http("PUT", "/api/v1/domains/" + name,
            "{\"active\":true,\"postmaster\":\"admin@" + name + "\"}");
         Assert.AreEqual(200, onStatus, onBody);
         StringAssert.Contains("\"active\":true", onBody);
         Assert.IsTrue(DomainOverCom(name).Active);
         Assert.AreEqual("admin@" + name, DomainOverCom(name).Postmaster);

         // A body that does not name active is refused, and changes nothing.
         (int noActiveStatus, string noActiveBody) = Http("PUT", "/api/v1/domains/" + name, "{\"postmaster\":\"x@" + name + "\"}");
         Assert.AreEqual(400, noActiveStatus, noActiveBody);
         Assert.AreEqual("admin@" + name, DomainOverCom(name).Postmaster);

         // Deleted, with the account that was in it.
         (int deleteStatus, string deleteBody) = Http("DELETE", "/api/v1/domains/" + name);
         Assert.AreEqual(200, deleteStatus, deleteBody);
         StringAssert.Contains("\"deleted\":true", deleteBody);
         Assert.IsNull(DomainOverCom(name), "The domain must be gone through COM after DELETE.");
         StringAssert.DoesNotContain(name, Http("GET", "/api/v1/domains").body);
         Assert.AreEqual(404, Http("GET", "/api/v1/domains/" + name + "/accounts").status,
            "The domain's accounts must be gone with it.");

         string dataDirectory = _settings.Directories.DataDirectory;
         Assert.IsFalse(Directory.Exists(Paths.Combine(dataDirectory, name)),
            "The domain's data directory must be removed with it, as COM's delete removes it.");

         // Deleting it again, and switching it, are 404 - it is not there.
         Assert.AreEqual(404, Http("DELETE", "/api/v1/domains/" + name).status);
         Assert.AreEqual(404, Http("PUT", "/api/v1/domains/" + name, "{\"active\":true}").status);
      }

      // Every domain that is not the one under test, so the count assertion
      // above survives whatever else the suite's setup left in place.
      private int CountOtherDomains(string name)
      {
         int others = 0;
         hMailServer.Domains domains = _application.Domains;
         for (int i = 0; i < domains.Count; i++)
         {
            if (!string.Equals(domains[i].Name, name, StringComparison.OrdinalIgnoreCase))
               others++;
         }
         return others;
      }

      [Test]
      [Description("A name that is not a domain name, a missing name, and a name a domain alias already has are all 400, and none of them creates anything.")]
      public void InvalidNameIsRefused()
      {
         int before = _application.Domains.Count;

         (int status, string body) spaces = Http("POST", "/api/v1/domains", "{\"name\":\"not a domain\"}");
         Assert.AreEqual(400, spaces.status, spaces.body);
         StringAssert.Contains("not a valid domain name", spaces.body,
            "The refusal is the limitation check's own sentence, the one the Control Panel shows.");

         Assert.AreEqual(400, Http("POST", "/api/v1/domains", "{\"name\":\"-leading.test\"}").status);
         Assert.AreEqual(400, Http("POST", "/api/v1/domains", "{\"name\":\"\"}").status);
         Assert.AreEqual(400, Http("POST", "/api/v1/domains", "{\"active\":true}").status,
            "A body without a name is refused.");

         // A domain alias of the suite's domain answers to a name; a domain by
         // that name would make every address in it ambiguous, and COM refuses
         // it for that reason. So does the route, through the same check.
         string aliasName = UniqueDomainName();
         DomainAlias alias = _domain.DomainAliases.Add();
         alias.DomainID = _domain.ID;
         alias.AliasName = aliasName;
         alias.Save();
         try
         {
            (int aliasStatus, string aliasBody) = Http("POST", "/api/v1/domains", "{\"name\":\"" + aliasName + "\"}");
            Assert.AreEqual(400, aliasStatus, aliasBody);
            StringAssert.Contains("domain alias", aliasBody);
         }
         finally
         {
            alias.Delete();
         }

         Assert.AreEqual(before, _application.Domains.Count, "No refused POST may have created a domain.");
      }

      [Test]
      [Description("A key restricted to named domains cannot create or delete a domain, whatever its scope; a read-only key cannot change one; an unrestricted full key can.")]
      public void ScopedAndReadOnlyKeysAreRefused()
      {
         string name = UniqueDomainName();
         string other = UniqueDomainName();

         // Full authority, deliberately, so that nothing below is explained by
         // the key being read-only. The only thing narrowing it is the domain
         // list - and it is issued for the suite's own domain, so the DELETE
         // below is the one an operator would least like to see succeed.
         (string scopedId, string scopedKey) = CreateKey("restdom - scoped", "full", "example.test");
         (string readOnlyId, string readOnlyKey) = CreateKey("restdom - readonly", "readonly", null);
         (string fullId, string fullKey) = CreateKey("restdom - full", "full", null);

         try
         {
            (int status, string body) refusedCreate = Bearer("POST", "/api/v1/domains", scopedKey, "{\"name\":\"" + name + "\"}");
            Assert.AreEqual(403, refusedCreate.status,
               "A domain-scoped key must not create a domain. Body: " + refusedCreate.body);
            Assert.IsNull(DomainOverCom(name), "The refused POST must not have created the domain.");

            (int status, string body) refusedDelete = Bearer("DELETE", "/api/v1/domains/example.test", scopedKey);
            Assert.AreEqual(403, refusedDelete.status,
               "A domain-scoped key must not delete a domain, not even one it was issued for. Body: " + refusedDelete.body);
            Assert.IsNotNull(DomainOverCom("example.test"), "The refused DELETE must not have deleted the domain.");

            // The update is scoped like the account routes: another domain is
            // refused, the key's own is allowed.
            Domain otherDomain = SingletonProvider<TestSetup>.Instance.AddDomain(other);
            Assert.AreEqual(403, Bearer("PUT", "/api/v1/domains/" + other, scopedKey, "{\"active\":false}").status,
               "A domain-scoped key must not switch another domain off.");
            Assert.IsTrue(DomainOverCom(other).Active, "The refused PUT must not have changed the other domain.");

            (int status, string body) ownUpdate = Bearer("PUT", "/api/v1/domains/example.test", scopedKey, "{\"active\":true}");
            Assert.AreEqual(200, ownUpdate.status,
               "A domain-scoped key with full scope may update the domain it was issued for. Body: " + ownUpdate.body);

            // A read-only key reaches none of the three.
            Assert.AreEqual(403, Bearer("POST", "/api/v1/domains", readOnlyKey, "{\"name\":\"" + name + "\"}").status);
            Assert.AreEqual(403, Bearer("PUT", "/api/v1/domains/" + other, readOnlyKey, "{\"active\":false}").status);
            Assert.AreEqual(403, Bearer("DELETE", "/api/v1/domains/" + other, readOnlyKey).status);
            Assert.IsNull(DomainOverCom(name));
            Assert.IsNotNull(DomainOverCom(other));
            Assert.IsTrue(DomainOverCom(other).Active);

            // An unrestricted full key is the credential automation is meant to
            // use, and it carries the administrator's authority for these routes.
            (int status, string body) created = Bearer("POST", "/api/v1/domains", fullKey, "{\"name\":\"" + name + "\"}");
            Assert.AreEqual(201, created.status, created.body);
            Assert.IsNotNull(DomainOverCom(name));
            Assert.AreEqual(200, Bearer("DELETE", "/api/v1/domains/" + name, fullKey).status);
            Assert.IsNull(DomainOverCom(name));

            Assert.AreEqual(200, Bearer("DELETE", "/api/v1/domains/" + other, fullKey).status);
            Assert.IsNull(DomainOverCom(other));

            // No credential at all is 401, and the domain listing is untouched.
            Assert.AreEqual(401, Http("POST", "/api/v1/domains", null, "{\"name\":\"" + name + "\"}").status);
            Assert.IsNull(DomainOverCom(name));
         }
         finally
         {
            Http("DELETE", "/api/v1/apikeys/" + scopedId);
            Http("DELETE", "/api/v1/apikeys/" + readOnlyId);
            Http("DELETE", "/api/v1/apikeys/" + fullId);
         }
      }

      private static (string id, string key) CreateKey(string label, string scope, string domains)
      {
         string body = "{\"label\":\"" + label + "\",\"scope\":\"" + scope + "\"" +
                       (domains == null ? "" : ",\"domains\":\"" + domains + "\"") + "}";
         (int status, string created) = Http("POST", "/api/v1/apikeys", body);
         Assert.AreEqual(201, status, "POST /api/v1/apikeys must create a key. Body: " + created);
         return (Extract(created, "id"), Extract(created, "key"));
      }

      // The string value of a top-level JSON property, enough for the bodies this API returns.
      private static string Extract(string json, string key)
      {
         Match match = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
         Assert.IsTrue(match.Success, "No '" + key + "' in: " + json);
         return match.Groups[1].Value;
      }

      [Test]
      [Description("PUT /api/v1/domains/{name} takes the whole domain - limits, plus addressing, greylisting, signature, DKIM algorithm, retention, relay and automatic reply - with COM reading every value the API set, the listing showing them, the relay password never emitted, and a refused field changing nothing.")]
      public void DomainFieldsRoundTripThroughCom()
      {
         string name = UniqueDomainName();
         Assert.AreEqual(201, Http("POST", "/api/v1/domains", "{\"name\":\"" + name + "\"}").status);

         (int status, string body) = Http("PUT", "/api/v1/domains/" + name,
            "{\"active\":true,\"max_size_mb\":123,\"max_message_size_kb\":456,\"max_account_size_mb\":78,\"max_accounts\":7,\"max_accounts_enabled\":true," +
            "\"max_aliases\":8,\"max_lists\":9,\"plus_addressing_enabled\":true,\"plus_addressing_character\":\"-\",\"use_greylisting\":false," +
            "\"signature_enabled\":true,\"signature_method\":\"overwrite\",\"signature_plain_text\":\"Regards\",\"signature_html\":\"<b>Regards</b>\"," +
            "\"signature_add_to_replies\":true,\"signature_add_to_local_mail\":true,\"dkim_signing_algorithm\":\"sha1\",\"message_retention_days\":30," +
            "\"relay_host\":\"relay.example\",\"relay_port\":2525,\"relay_requires_auth\":true,\"relay_username\":\"relayuser\",\"relay_password\":\"relaysecret\"," +
            "\"relay_connection_security\":\"starttls_required\",\"vacation_enabled\":true,\"vacation_subject\":\"Away\",\"vacation_message\":\"Back soon\"}");
         Assert.AreEqual(200, status, body);
         StringAssert.Contains("\"max_size_mb\":123", body);
         StringAssert.Contains("\"signature_method\":\"overwrite\"", body);
         StringAssert.Contains("\"relay_connection_security\":\"starttls_required\"", body);
         StringAssert.Contains("\"dkim_signing_algorithm\":\"sha1\"", body);
         StringAssert.DoesNotContain("relaysecret", body, "The relay password is write-only.");
         StringAssert.DoesNotContain("\"relay_password\"", body);

         Domain domain = DomainOverCom(name);
         Assert.AreEqual(123, domain.MaxSize);
         Assert.AreEqual(456, domain.MaxMessageSize);
         Assert.AreEqual(78, domain.MaxAccountSize);
         Assert.AreEqual(7, domain.MaxNumberOfAccounts);
         Assert.IsTrue(domain.MaxNumberOfAccountsEnabled);
         Assert.AreEqual(8, domain.MaxNumberOfAliases);
         Assert.AreEqual(9, domain.MaxNumberOfDistributionLists);
         Assert.IsFalse(domain.MaxNumberOfAliasesEnabled, "Left out, so left alone.");
         Assert.IsTrue(domain.PlusAddressingEnabled);
         Assert.AreEqual("-", domain.PlusAddressingCharacter);
         Assert.IsFalse(domain.AntiSpamEnableGreylisting);
         Assert.IsTrue(domain.SignatureEnabled);
         Assert.AreEqual(eDomainSignatureMethod.eSMOverwriteAccountSignature, domain.SignatureMethod);
         Assert.AreEqual("Regards", domain.SignaturePlainText);
         Assert.AreEqual("<b>Regards</b>", domain.SignatureHTML);
         Assert.IsTrue(domain.AddSignaturesToReplies);
         Assert.IsTrue(domain.AddSignaturesToLocalMail);
         Assert.AreEqual(eDKIMAlgorithm.eSHA1, domain.DKIMSigningAlgorithm);
         Assert.AreEqual(30, domain.MessageRetentionDays);
         Assert.AreEqual("relay.example", domain.RelayHost);
         Assert.AreEqual(2525, domain.RelayPort);
         Assert.IsTrue(domain.RelayRequiresAuthentication);
         Assert.AreEqual("relayuser", domain.RelayUsername);
         Assert.AreEqual(eConnectionSecurity.eCSSTARTTLSRequired, domain.RelayConnectionSecurity);
         Assert.IsTrue(domain.VacationMessageIsOn);
         Assert.AreEqual("Away", domain.VacationSubject);
         Assert.AreEqual("Back soon", domain.VacationMessage);

         // The listing carries the same fields.
         string list = Http("GET", "/api/v1/domains").body;
         StringAssert.Contains("\"name\":\"" + name + "\",\"active\":true,\"postmaster\":\"\",\"max_message_size_kb\":456,\"max_size_mb\":123", list);

         // Refused, and nothing changed.
         foreach (string refused in new[]
         {
            "{\"active\":true,\"signature_method\":\"maybe\"}", "{\"active\":true,\"bogus\":1}", "{\"active\":true,\"relay_port\":70000}",
            "{\"active\":true,\"plus_addressing_character\":\"--\"}", "{\"active\":true,\"max_size_mb\":\"lots\"}", "{\"max_size_mb\":1}", "{\"active\":true,\"name\":\"\"}"
         })
         {
            (int refusedStatus, string refusedBody) = Http("PUT", "/api/v1/domains/" + name, refused);
            Assert.AreEqual(400, refusedStatus, refused + " -> " + refusedBody);
         }
         domain = DomainOverCom(name);
         Assert.AreEqual(123, domain.MaxSize);
         Assert.AreEqual(2525, domain.RelayPort);
         Assert.AreEqual("-", domain.PlusAddressingCharacter);
      }

      [Test]
      [Description("A new name in PUT /api/v1/domains/{name} renames the domain as the Control Panel does: the old name is gone, the new one is there through COM and over the API, and the account in it answers to the new name.")]
      public void DomainRenameFollowsEveryAddress()
      {
         string oldName = UniqueDomainName();
         string newName = UniqueDomainName();
         Assert.AreEqual(201, Http("POST", "/api/v1/domains", "{\"name\":\"" + oldName + "\"}").status);
         Assert.AreEqual(201, Http("POST", "/api/v1/domains/" + oldName + "/accounts",
            "{\"address\":\"first@" + oldName + "\",\"password\":\"S0me-Long-Passphrase\"}").status);

         (int status, string body) = Http("PUT", "/api/v1/domains/" + oldName, "{\"active\":true,\"name\":\"" + newName + "\"}");
         Assert.AreEqual(200, status, body);
         StringAssert.Contains("\"name\":\"" + newName + "\"", body);

         Assert.IsNull(DomainOverCom(oldName), "The old name is gone.");
         Domain renamed = DomainOverCom(newName);
         Assert.IsNotNull(renamed, "The domain exists under the new name through COM.");
         Assert.IsNotNull(renamed.Accounts.get_ItemByAddress("first@" + newName), "The account followed the domain.");

         Assert.AreEqual(404, Http("GET", "/api/v1/domains/" + oldName + "/accounts").status);
         StringAssert.Contains("first@" + newName, Http("GET", "/api/v1/domains/" + newName + "/accounts").body);
      }

      [Test]
      [Description("A domain alias is added over the route, listed, seen by COM, refused as a duplicate, deleted with COM agreeing, and not found afterwards.")]
      public void DomainAliasesRoundTripThroughCom()
      {
         string name = UniqueDomainName();
         string alias = UniqueDomainName();
         Assert.AreEqual(201, Http("POST", "/api/v1/domains", "{\"name\":\"" + name + "\"}").status);

         (int status, string body) created = Http("POST", "/api/v1/domains/" + name + "/domain-aliases", "{\"name\":\"" + alias + "\"}");
         Assert.AreEqual(201, created.status, created.body);
         StringAssert.Contains("\"name\":\"" + alias + "\"", created.body);
         StringAssert.Contains("\"id\":", created.body);

         (int listStatus, string list) = Http("GET", "/api/v1/domains/" + name + "/domain-aliases");
         Assert.AreEqual(200, listStatus, list);
         StringAssert.Contains("\"name\":\"" + alias + "\"", list);

         DomainAliases overCom = DomainOverCom(name).DomainAliases;
         Assert.AreEqual(1, overCom.Count);
         Assert.AreEqual(alias, overCom[0].AliasName);

         (int duplicateStatus, string duplicateBody) = Http("POST", "/api/v1/domains/" + name + "/domain-aliases", "{\"name\":\"" + alias + "\"}");
         Assert.AreEqual(400, duplicateStatus, duplicateBody);
         Assert.AreEqual(400, Http("POST", "/api/v1/domains/" + name + "/domain-aliases", "{\"name\":\"\"}").status);
         Assert.AreEqual(404, Http("POST", "/api/v1/domains/nobody-" + name + "/domain-aliases", "{\"name\":\"x.test\"}").status);

         (int deleteStatus, string deleteBody) = Http("DELETE", "/api/v1/domains/" + name + "/domain-aliases/" + alias);
         Assert.AreEqual(200, deleteStatus, deleteBody);
         StringAssert.Contains("\"deleted\":true", deleteBody);
         Assert.AreEqual(0, DomainOverCom(name).DomainAliases.Count);
         Assert.AreEqual(404, Http("DELETE", "/api/v1/domains/" + name + "/domain-aliases/" + alias).status);
         Assert.AreEqual("[]", Http("GET", "/api/v1/domains/" + name + "/domain-aliases").body);
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
