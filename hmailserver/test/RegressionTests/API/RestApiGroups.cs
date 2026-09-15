// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
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
   ///    The account groups as a resource: listed, created, read, renamed and
   ///    deleted under /api/v1/groups and /api/v1/groups/{id}, and a group's
   ///    members listed, added and removed under /api/v1/groups/{id}/members -
   ///    what Settings.Groups, Group and Group.Members are over COM.
   ///
   ///    Every row a route claims to have stored is read back through COM, as
   ///    the Control Panel reads it, and a membership is shown to be in force by
   ///    the server's own decision: a group permission with the post right on an
   ///    owner's inbox is what lets a member write as the owner, and GET
   ///    /api/v1/me/identities lists that as a granted identity for the member
   ///    and for nobody else. A key restricted to a domain is refused the whole
   ///    resource, since a group holds accounts of any domain.
   /// </summary>
   [TestFixture]
   public class RestApiGroups : TestFixtureBase
   {
      // The port the listener answers on: this one on the Windows bench, the
      // suite's own where RestListener finds one already on.
      private static int RestPort = 9128;
      private const string AdminPassword = "testar";
      private const string UserPassword = "test";
      private const string GroupsPath = "/api/v1/groups";

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
         RestListener.Stop();
         _application.Reinitialize();
      }

      private Account AddAccount(string name)
      {
         return SingletonProvider<TestSetup>.Instance.AddAccount(_domain, name + "@" + _domain.Name, UserPassword);
      }

      // The inbox exists once something has been delivered to it.
      private static IMAPFolder SeededInbox(Account account)
      {
         SmtpClientSimulator.StaticSend("seed@example.com", account.Address, "Seed", "Creates the INBOX.");
         Pop3ClientSimulator.AssertMessageCount(account.Address, UserPassword, 1);
         return account.IMAPFolders.get_ItemByName("INBOX");
      }

      private static string Members(string groupId)
      {
         return GroupsPath + "/" + groupId + "/members";
      }

      private static string CreateGroup(string name)
      {
         (int status, string body) created = Http("POST", GroupsPath, "{\"name\":\"" + name + "\"}");
         Assert.AreEqual(201, created.status, created.body);
         return Extract(created.body, "id");
      }

      [Test]
      [Description("A group is created over the route, seen by COM under its id and its name, listed, read, renamed with COM seeing the new name, and deleted with COM agreeing.")]
      public void AGroupIsCreatedListedReadRenamedAndDeleted()
      {
         Assert.AreEqual(0, _settings.Groups.Count, "The suite starts every test without groups.");

         (int status, string body) created = Http("POST", GroupsPath, "{\"name\":\"RestGroup\"}");
         Assert.AreEqual(201, created.status, created.body);
         StringAssert.Contains("\"name\":\"RestGroup\"", created.body);
         string id = Extract(created.body, "id");

         // What the Control Panel would show.
         hMailServer.Group stored = _settings.Groups.get_ItemByName("RestGroup");
         Assert.AreEqual(long.Parse(id), (long) stored.ID);
         Assert.AreEqual(1, _settings.Groups.Count);

         (int status, string body) listed = Http("GET", GroupsPath);
         Assert.AreEqual(200, listed.status, listed.body);
         StringAssert.Contains("{\"id\":" + id + ",\"name\":\"RestGroup\"}", listed.body);

         (int status, string body) read = Http("GET", GroupsPath + "/" + id);
         Assert.AreEqual(200, read.status, read.body);
         Assert.AreEqual("{\"id\":" + id + ",\"name\":\"RestGroup\"}", read.body);

         (int status, string body) renamed = Http("PUT", GroupsPath + "/" + id, "{\"name\":\"RestGroupRenamed\"}");
         Assert.AreEqual(200, renamed.status, renamed.body);
         Assert.AreEqual("{\"id\":" + id + ",\"name\":\"RestGroupRenamed\"}", renamed.body);

         Assert.AreEqual(long.Parse(id), (long) _settings.Groups.get_ItemByName("RestGroupRenamed").ID, "COM sees the new name under the same id.");
         Assert.AreEqual(1, _settings.Groups.Count, "A rename is not a second group.");

         (int status, string body) unchanged = Http("PUT", GroupsPath + "/" + id, "{}");
         Assert.AreEqual(200, unchanged.status, unchanged.body);
         StringAssert.Contains("\"name\":\"RestGroupRenamed\"", unchanged.body, "A field left out keeps its value.");

         (int status, string body) deleted = Http("DELETE", GroupsPath + "/" + id);
         Assert.AreEqual(200, deleted.status, deleted.body);
         StringAssert.Contains("\"deleted\":true", deleted.body);
         Assert.AreEqual(404, Http("DELETE", GroupsPath + "/" + id).status, "Deleted twice is not found.");
         Assert.AreEqual(404, Http("GET", GroupsPath + "/" + id).status);

         Assert.AreEqual(0, _settings.Groups.Count);
         Assert.AreEqual("[]", Http("GET", GroupsPath).body);
      }

      [Test]
      [Description("Members are added by id and by address, listed, seen by COM, in force for the server's own decision through a group permission, removed with the decision changing, and gone with the group - with the permission that named it.")]
      public void MembersAreAddedSeenInForceRemovedAndGoneWithTheGroup()
      {
         Account owner = AddAccount("group-owner");
         Account member = AddAccount("group-member");
         Account other = AddAccount("group-other");
         Account outsider = AddAccount("group-outsider");
         IMAPFolder inbox = SeededInbox(owner);

         string id = CreateGroup("RestMembers");

         (int status, string body) byId = Http("POST", Members(id), "{\"account_id\":" + member.ID + "}");
         Assert.AreEqual(201, byId.status, byId.body);
         StringAssert.Contains("\"group_id\":" + id + ",\"account_id\":" + member.ID + ",\"account\":\"" + member.Address + "\"", byId.body);

         (int status, string body) byAddress = Http("POST", Members(id), "{\"account\":\"" + other.Address + "\"}");
         Assert.AreEqual(201, byAddress.status, byAddress.body);
         StringAssert.Contains("\"account_id\":" + other.ID + ",\"account\":\"" + other.Address + "\"", byAddress.body);

         (int status, string body) listed = Http("GET", Members(id));
         Assert.AreEqual(200, listed.status, listed.body);
         StringAssert.Contains("\"account\":\"" + member.Address + "\"", listed.body);
         StringAssert.Contains("\"account\":\"" + other.Address + "\"", listed.body);

         // What the Control Panel would show.
         hMailServer.Group stored = _settings.Groups.get_ItemByDBID(int.Parse(id));
         Assert.AreEqual(2, stored.Members.Count);
         Assert.AreEqual(member.ID, stored.Members[0].AccountID);
         Assert.AreEqual(other.ID, stored.Members[1].AccountID);

         bool enforced = _settings.IMAPACLEnabled;
         _settings.IMAPACLEnabled = true;
         try
         {
            // In force: the post right the group holds on the owner's inbox is
            // what lets a member write as the owner, and the server lists it so
            // - for the member, and not for an account outside the group.
            (int status, string body) grant = Http("POST", "/api/v1/accounts/" + owner.Address + "/folders/" + inbox.ID + "/permissions",
               "{\"type\":\"group\",\"group_id\":" + id + ",\"rights\":{\"post\":true}}");
            Assert.AreEqual(201, grant.status, grant.body);

            (int status, string body) identities = AsAccount("GET", "/api/v1/me/identities", member);
            Assert.AreEqual(200, identities.status, identities.body);
            StringAssert.Contains("{\"address\":\"" + owner.Address + "\",\"name\":\"\",\"kind\":\"granted\"", identities.body);
            StringAssert.DoesNotContain(owner.Address, AsAccount("GET", "/api/v1/me/identities", outsider).body, "An account outside the group holds nothing through it.");

            (int status, string body) removed = Http("DELETE", Members(id) + "/" + member.ID);
            Assert.AreEqual(200, removed.status, removed.body);
            StringAssert.Contains("\"deleted\":true", removed.body);
            Assert.AreEqual(404, Http("DELETE", Members(id) + "/" + member.ID).status, "Removed twice is not a member.");

            StringAssert.DoesNotContain(owner.Address, AsAccount("GET", "/api/v1/me/identities", member).body, "Out of the group, the grant is gone from the server's decision.");
            StringAssert.Contains(owner.Address, AsAccount("GET", "/api/v1/me/identities", other).body, "The other member still holds it.");

            Assert.AreEqual(1, stored.Members.Count);
            Assert.AreEqual(other.ID, stored.Members[0].AccountID);

            // The group goes with its members and the permission that named it,
            // as Groups.DeleteByDBID takes them over COM.
            Assert.AreEqual(200, Http("DELETE", GroupsPath + "/" + id).status);
            Assert.AreEqual(404, Http("GET", Members(id)).status);

            IMAPFolderPermissions permissions = inbox.Permissions;
            permissions.Refresh();
            Assert.AreEqual(0, permissions.Count, "The permission that named the group went with it.");
            StringAssert.DoesNotContain(owner.Address, AsAccount("GET", "/api/v1/me/identities", other).body);
         }
         finally
         {
            _settings.IMAPACLEnabled = enforced;
         }
      }

      [Test]
      [Description("A refusal names what is wrong, and nothing is stored: no name, an empty one, one over the column, the wrong type, an unknown field, a body that is not an object, a second group of the same name in any case, a member named by nothing, by an account that is not there, by two accounts that differ, or twice, and a group or a member that is not there.")]
      public void RefusalsNameWhatIsWrongAndStoreNothing()
      {
         Account account = AddAccount("group-refusals");
         Account another = AddAccount("group-refusals-other");

         void Refused(string method, string path, string body, int expected, string reason)
         {
            (int status, string answer) = Http(method, path, body);
            Assert.AreEqual(expected, status, method + " " + path + " " + body + " -> " + answer);
            StringAssert.Contains(reason, answer, body);
         }

         Refused("POST", GroupsPath, "{}", 400, "name is required");
         Refused("POST", GroupsPath, "{\"name\":\"\"}", 400, "name is required");
         Refused("POST", GroupsPath, "{\"name\":\"   \"}", 400, "name is required");
         Refused("POST", GroupsPath, "{\"name\":5}", 400, "name must be a string");
         Refused("POST", GroupsPath, "{\"name\":\"x\",\"colour\":\"blue\"}", 400, "unknown field: colour");
         Refused("POST", GroupsPath, "{\"name\":\"" + new string('n', 256) + "\"}", 400, "name must be at most 255 characters");
         Assert.AreEqual(400, Http("POST", GroupsPath, "[1]").status, "The body must be an object.");
         Assert.AreEqual(0, _settings.Groups.Count, "A refused create stores nothing.");

         string id = CreateGroup("Twice");
         Refused("POST", GroupsPath, "{\"name\":\"Twice\"}", 409, "Another group with this name already exists.");
         Refused("POST", GroupsPath, "{\"name\":\"TWICE\"}", 409, "Another group with this name already exists.");
         string otherId = CreateGroup("Other");
         Refused("PUT", GroupsPath + "/" + otherId, "{\"name\":\"twice\"}", 409, "Another group with this name already exists.");
         Refused("PUT", GroupsPath + "/" + otherId, "{\"name\":\"\"}", 400, "name must not be empty");
         Refused("PUT", GroupsPath + "/" + otherId, "{\"name\":true}", 400, "name must be a string");
         Assert.AreEqual("Other", _settings.Groups.get_ItemByDBID(int.Parse(otherId)).Name, "A refused rename changes nothing.");
         Assert.AreEqual(2, _settings.Groups.Count);

         Assert.AreEqual(404, Http("GET", GroupsPath + "/999999999").status);
         Assert.AreEqual(404, Http("PUT", GroupsPath + "/999999999", "{\"name\":\"x\"}").status);
         Assert.AreEqual(404, Http("DELETE", GroupsPath + "/999999999").status);
         Assert.AreEqual(404, Http("GET", GroupsPath + "/x").status, "Not an id.");

         Refused("POST", Members(id), "{}", 400, "account_id or account is required");
         Refused("POST", Members(id), "{\"account_id\":999999999}", 400, "no account with that id");
         Refused("POST", Members(id), "{\"account\":\"nobody@" + _domain.Name + "\"}", 400, "no account named nobody@" + _domain.Name);
         Refused("POST", Members(id), "{\"account_id\":\"x\"}", 400, "account_id must be a whole number above zero");
         Refused("POST", Members(id), "{\"account_id\":0}", 400, "account_id must be a whole number above zero");
         Refused("POST", Members(id), "{\"account_id\":" + account.ID + ",\"account\":\"" + another.Address + "\"}", 400, "account_id and account name different accounts");
         Refused("POST", Members(id), "{\"account_id\":" + account.ID + ",\"role\":\"admin\"}", 400, "unknown field: role");
         Assert.AreEqual(400, Http("POST", Members(id), "[1]").status);
         Assert.AreEqual(0, _settings.Groups.get_ItemByDBID(int.Parse(id)).Members.Count, "A refused member stores nothing.");

         Assert.AreEqual(201, Http("POST", Members(id), "{\"account_id\":" + account.ID + ",\"account\":\"" + account.Address + "\"}").status, "Both, naming the same account.");
         Refused("POST", Members(id), "{\"account_id\":" + account.ID + "}", 409, "the account is already a member of the group");
         Refused("POST", Members(id), "{\"account\":\"" + account.Address + "\"}", 409, "the account is already a member of the group");
         Assert.AreEqual(1, _settings.Groups.get_ItemByDBID(int.Parse(id)).Members.Count);

         Assert.AreEqual(404, Http("GET", Members("999999999")).status);
         Assert.AreEqual(404, Http("POST", Members("999999999"), "{\"account_id\":" + account.ID + "}").status);
         Refused("DELETE", Members(id) + "/" + another.ID, null, 404, "the account is not a member of the group");
         Assert.AreEqual(404, Http("DELETE", Members(id) + "/999999999").status);
         Assert.AreEqual(404, Http("DELETE", Members("999999999") + "/" + account.ID).status);
      }

      [Test]
      [Description("A key restricted to a domain - even the accounts' own - is refused the groups, since a group holds accounts of any domain; a read-only key lists and changes nothing; a full key does everything.")]
      public void KeysAreRefusedByDomainAndByScope()
      {
         Account account = AddAccount("group-scoped");

         (string elsewhereId, string elsewhereKey) = CreateKey("restgroups - elsewhere", "full", "elsewhere.test");
         (string hereId, string hereKey) = CreateKey("restgroups - here", "full", _domain.Name);
         (string readOnlyId, string readOnlyKey) = CreateKey("restgroups - readonly", "readonly", null);
         (string fullId, string fullKey) = CreateKey("restgroups - full", "full", null);

         try
         {
            const string create = "{\"name\":\"ScopedGroup\"}";
            Assert.AreEqual(403, Bearer("GET", GroupsPath, elsewhereKey).status, "Another domain's key must not list the groups.");
            Assert.AreEqual(403, Bearer("POST", GroupsPath, elsewhereKey, create).status);
            Assert.AreEqual(403, Bearer("GET", GroupsPath, hereKey).status, "Nor a key for the accounts' own domain: the resource is server-wide.");
            Assert.AreEqual(403, Bearer("POST", GroupsPath, hereKey, create).status);

            (int status, string body) created = Bearer("POST", GroupsPath, fullKey, create);
            Assert.AreEqual(201, created.status, created.body);
            string id = Extract(created.body, "id");

            Assert.AreEqual(200, Bearer("GET", GroupsPath, readOnlyKey).status, "A read-only key lists the groups.");
            Assert.AreEqual(200, Bearer("GET", GroupsPath + "/" + id, readOnlyKey).status);
            Assert.AreEqual(200, Bearer("GET", Members(id), readOnlyKey).status, "And the members.");
            Assert.AreEqual(403, Bearer("POST", GroupsPath, readOnlyKey, create).status, "And creates nothing.");
            Assert.AreEqual(403, Bearer("PUT", GroupsPath + "/" + id, readOnlyKey, "{\"name\":\"Renamed\"}").status);
            Assert.AreEqual(403, Bearer("POST", Members(id), readOnlyKey, "{\"account_id\":" + account.ID + "}").status);
            Assert.AreEqual(403, Bearer("DELETE", GroupsPath + "/" + id, readOnlyKey).status);

            Assert.AreEqual(403, Bearer("GET", Members(id), hereKey).status);
            Assert.AreEqual(403, Bearer("POST", Members(id), hereKey, "{\"account_id\":" + account.ID + "}").status, "A domain's key cannot put its own account in a group.");
            Assert.AreEqual(403, Bearer("DELETE", Members(id) + "/" + account.ID, hereKey).status);

            Assert.AreEqual(201, Bearer("POST", Members(id), fullKey, "{\"account_id\":" + account.ID + "}").status);
            Assert.AreEqual(403, Bearer("DELETE", Members(id) + "/" + account.ID, readOnlyKey).status);
            Assert.AreEqual(200, Bearer("DELETE", Members(id) + "/" + account.ID, fullKey).status);

            hMailServer.Group stored = _settings.Groups.get_ItemByDBID(int.Parse(id));
            Assert.AreEqual("ScopedGroup", stored.Name, "The refused rename changed nothing.");
            Assert.AreEqual(0, stored.Members.Count);

            Assert.AreEqual(200, Bearer("DELETE", GroupsPath + "/" + id, fullKey).status);
            Assert.AreEqual(0, _settings.Groups.Count);
         }
         finally
         {
            Http("DELETE", "/api/v1/apikeys/" + elsewhereId);
            Http("DELETE", "/api/v1/apikeys/" + hereId);
            Http("DELETE", "/api/v1/apikeys/" + readOnlyId);
            Http("DELETE", "/api/v1/apikeys/" + fullId);
         }
      }

      [Test]
      [Description("The OpenAPI document describes the four paths - the test's copy of the contract, extended with the API.")]
      public void OpenApiDescribesTheGroupRoutes()
      {
         (int status, string body) = Http("GET", "/api/v1/openapi.json");
         Assert.AreEqual(200, status, body);
         foreach (string path in new[]
         {
            "/api/v1/groups", "/api/v1/groups/{id}", "/api/v1/groups/{id}/members", "/api/v1/groups/{id}/members/{account_id}"
         })
         {
            StringAssert.Contains("\"" + path + "\":{", body, "The OpenAPI document must describe " + path);
         }
         int at = body.IndexOf("\"/api/v1/groups\":{", StringComparison.Ordinal);
         string entry = body.Substring(at, Math.Min(1200, body.Length - at));
         StringAssert.Contains("Server-wide", entry, "The entry says the resource is server-wide.");
      }

      private static (string id, string key) CreateKey(string label, string scope, string domains)
      {
         string body = "{\"label\":\"" + label + "\",\"scope\":\"" + scope + "\"" +
                       (domains == null ? "" : ",\"domains\":\"" + domains + "\"") + "}";
         (int status, string created) = Http("POST", "/api/v1/apikeys", body);
         Assert.AreEqual(201, status, "POST /api/v1/apikeys must create a key. Body: " + created);
         return (Extract(created, "id"), Extract(created, "key"));
      }

      // The value of a top-level JSON property, string or number, enough for
      // the bodies this API returns.
      private static string Extract(string json, string key)
      {
         Match match = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"?([^\",}]*)\"?");
         Assert.IsTrue(match.Success, "No '" + key + "' in: " + json);
         return match.Groups[1].Value;
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

      // The /api/v1/me routes: the account's own credentials.
      private static (int status, string body) AsAccount(string method, string path, Account account, string requestBody = null)
      {
         string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(account.Address + ":" + UserPassword));
         return Http(method, path, "Basic " + credentials, requestBody);
      }

      // Issues one HTTP/1.0 request against the REST listener and returns the
      // parsed status code and body. The connect is retried briefly to absorb the
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
