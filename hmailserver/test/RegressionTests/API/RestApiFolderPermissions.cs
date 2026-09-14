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
   ///    The administrative folder routes: an account's folder tree listed with
   ///    its ids under /api/v1/accounts/{address}/folders, and a folder's ACL
   ///    listed, granted, changed and revoked under
   ///    /api/v1/accounts/{address}/folders/{id}/permissions - what
   ///    IMAPFolder.Permissions is over COM.
   ///
   ///    Every row a route claims to have stored is read back through COM, as
   ///    the Control Panel reads it, and a grant is shown to be in force by the
   ///    server's own decision: the post right on an owner's inbox is what lets
   ///    the grantee write as the owner, and GET /api/v1/me/identities lists
   ///    that as a granted identity. A key restricted to another domain is
   ///    refused, because the folder is reached through the address that scopes
   ///    it.
   /// </summary>
   [TestFixture]
   public class RestApiFolderPermissions : TestFixtureBase
   {
      // The port the listener answers on: this one on the Windows bench, the
      // suite's own where RestListener finds one already on.
      private static int RestPort = 9126;
      private const string AdminPassword = "testar";
      private const string UserPassword = "test";

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

      private static string Folders(Account account)
      {
         return "/api/v1/accounts/" + account.Address + "/folders";
      }

      private static string Permissions(Account account, IMAPFolder folder)
      {
         return Folders(account) + "/" + folder.ID + "/permissions";
      }

      [Test]
      [Description("The account's own folder tree is listed with the ids COM knows, and nothing else's is.")]
      public void TheAccountsFoldersAreListedWithTheirIds()
      {
         Account account = AddAccount("acl-folders");
         IMAPFolder inbox = SeededInbox(account);
         IMAPFolder projects = account.IMAPFolders.Add("Projects");
         projects.Save();

         (int status, string body) listed = Http("GET", Folders(account));
         Assert.AreEqual(200, listed.status, listed.body);
         StringAssert.Contains("\"delimiter\":\"", listed.body);
         StringAssert.Contains("\"id\":" + inbox.ID + ",\"account_id\":", listed.body);
         StringAssert.Contains("\"name\":\"INBOX\"", listed.body);
         StringAssert.Contains("\"id\":" + projects.ID + ",\"account_id\":", listed.body);
         StringAssert.Contains("\"name\":\"Projects\"", listed.body);
         StringAssert.DoesNotContain("\"shared\"", listed.body, "The public namespace and other owners' shares are not this account's.");

         Assert.AreEqual(404, Http("GET", "/api/v1/accounts/nobody@" + _domain.Name + "/folders").status);
      }

      [Test]
      [Description("A permission is granted over the route, seen by COM, in force for the server's own decision, changed with the change seen, and revoked with COM agreeing.")]
      public void APermissionIsGrantedSeenInForceChangedAndRevoked()
      {
         Account owner = AddAccount("acl-owner");
         Account grantee = AddAccount("acl-grantee");
         IMAPFolder inbox = SeededInbox(owner);

         bool enforced = _settings.IMAPACLEnabled;
         _settings.IMAPACLEnabled = true;
         try
         {
            (int status, string body) created = Http("POST", Permissions(owner, inbox),
               "{\"type\":\"user\",\"account_id\":" + grantee.ID + ",\"rights\":{\"post\":true}}");
            Assert.AreEqual(201, created.status, created.body);
            StringAssert.Contains("\"folder_id\":" + inbox.ID + ",\"type\":\"user\",\"account_id\":" + grantee.ID + ",\"account\":\"" + grantee.Address + "\"", created.body);
            StringAssert.Contains("\"post\":true", created.body);
            StringAssert.Contains("\"lookup\":false", created.body);
            StringAssert.Contains("\"rights_text\":\"p\"", created.body);
            long id = long.Parse(Extract(created.body, "id"));

            // What the Control Panel would show. An account folder's COM
            // collection is handed out unloaded, so it is refreshed first.
            IMAPFolderPermissions stored = inbox.Permissions;
            stored.Refresh();
            Assert.AreEqual(1, stored.Count);
            IMAPFolderPermission row = stored[0];
            Assert.AreEqual(id, row.ID);
            Assert.AreEqual(eACLPermissionType.ePermissionTypeUser, row.PermissionType);
            Assert.AreEqual(grantee.ID, row.PermissionAccountID);
            Assert.IsTrue(row.get_Permission(eACLPermission.ePermissionPost));
            Assert.IsFalse(row.get_Permission(eACLPermission.ePermissionRead));

            // In force: the post right on the owner's inbox is what lets the
            // grantee write as the owner, and the server lists it so.
            (int status, string body) identities = AsAccount("GET", "/api/v1/me/identities", grantee);
            Assert.AreEqual(200, identities.status, identities.body);
            StringAssert.Contains("{\"address\":\"" + owner.Address + "\",\"name\":\"\",\"kind\":\"granted\"", identities.body);

            (int status, string body) changed = Http("PUT", Permissions(owner, inbox) + "/" + id,
               "{\"rights\":{\"post\":false,\"lookup\":true,\"read\":true}}");
            Assert.AreEqual(200, changed.status, changed.body);
            StringAssert.Contains("\"rights_text\":\"lr\"", changed.body);
            StringAssert.Contains("\"account_id\":" + grantee.ID, changed.body, "A field left out keeps its value.");

            identities = AsAccount("GET", "/api/v1/me/identities", grantee);
            StringAssert.DoesNotContain(owner.Address, identities.body, "Without the post right the grant is gone from the server's decision.");

            stored.Refresh();
            Assert.IsTrue(stored[0].get_Permission(eACLPermission.ePermissionLookup));
            Assert.IsTrue(stored[0].get_Permission(eACLPermission.ePermissionRead));
            Assert.IsFalse(stored[0].get_Permission(eACLPermission.ePermissionPost));

            (int status, string body) listed = Http("GET", Permissions(owner, inbox));
            Assert.AreEqual(200, listed.status, listed.body);
            StringAssert.StartsWith("[{\"id\":" + id + ",", listed.body);

            (int status, string body) revoked = Http("DELETE", Permissions(owner, inbox) + "/" + id);
            Assert.AreEqual(200, revoked.status, revoked.body);
            StringAssert.Contains("\"deleted\":true", revoked.body);
            Assert.AreEqual(404, Http("DELETE", Permissions(owner, inbox) + "/" + id).status, "Revoked twice is not found.");

            stored.Refresh();
            Assert.AreEqual(0, stored.Count);
            Assert.AreEqual("[]", Http("GET", Permissions(owner, inbox)).body);
         }
         finally
         {
            _settings.IMAPACLEnabled = enforced;
         }
      }

      [Test]
      [Description("A group permission names the group by id or by name, and COM reads the group id back.")]
      public void AGroupPermissionNamesTheGroup()
      {
         Account owner = AddAccount("acl-group-owner");
         IMAPFolder inbox = SeededInbox(owner);

         hMailServer.Group group = _settings.Groups.Add();
         group.Name = "RestAclGroup";
         group.Save();
         hMailServer.Group group2 = null;

         try
         {
            (int status, string body) created = Http("POST", Permissions(owner, inbox),
               "{\"type\":\"group\",\"group\":\"RestAclGroup\",\"rights\":{\"lookup\":true,\"read\":true}}");
            Assert.AreEqual(201, created.status, created.body);
            StringAssert.Contains("\"type\":\"group\",\"account_id\":0,\"account\":\"\",\"group_id\":" + group.ID + ",\"group\":\"RestAclGroup\"", created.body);
            StringAssert.Contains("\"rights_text\":\"lr\"", created.body);

            IMAPFolderPermissions stored = inbox.Permissions;
            stored.Refresh();
            Assert.AreEqual(1, stored.Count);
            Assert.AreEqual(eACLPermissionType.ePermissionTypeGroup, stored[0].PermissionType);
            Assert.AreEqual(group.ID, stored[0].PermissionGroupID);
            Assert.AreEqual(0, stored[0].PermissionAccountID);

            // The same group again is a conflict, not a second row: the table is
            // unique on (folder, type, group, account).
            (int status, string body) again = Http("POST", Permissions(owner, inbox),
               "{\"type\":\"group\",\"group_id\":" + group.ID + "}");
            Assert.AreEqual(409, again.status, again.body);
            StringAssert.Contains("already has a permission", again.body);

            group2 = _settings.Groups.Add();
            group2.Name = "RestAclGroup2";
            group2.Save();
            (int status, string body) byId = Http("POST", Permissions(owner, inbox),
               "{\"type\":\"group\",\"group_id\":" + group2.ID + "}");
            Assert.AreEqual(201, byId.status, byId.body);
            StringAssert.Contains("\"group\":\"RestAclGroup2\"", byId.body);
            StringAssert.Contains("\"rights_text\":\"\"", byId.body, "A right not named is not granted.");

            stored.Refresh();
            Assert.AreEqual(2, stored.Count);
         }
         finally
         {
            _settings.Groups.DeleteByDBID(group.ID);
            if (group2 != null)
               _settings.Groups.DeleteByDBID(group2.ID);
         }
      }

      [Test]
      [Description("A refusal names what is wrong, and nothing is stored: no type, a type other than the three, an account or group missing or unknown, the folder's owner, a mismatched name, an unknown right or field, the wrong value type, and a folder or permission that is not there.")]
      public void RefusalsNameWhatIsWrongAndStoreNothing()
      {
         Account owner = AddAccount("acl-refusals");
         Account other = AddAccount("acl-refusals-other");
         IMAPFolder inbox = SeededInbox(owner);
         IMAPFolder othersInbox = SeededInbox(other);
         string permissions = Permissions(owner, inbox);

         void Refused(string body, string reason)
         {
            (int status, string answer) = Http("POST", permissions, body);
            Assert.AreEqual(400, status, body + " -> " + answer);
            StringAssert.Contains(reason, answer, body);
         }

         Refused("{}", "type is required");
         Refused("{\"type\":\"owner\"}", "type must be user, group or anyone");
         Refused("{\"type\":\"user\"}", "names the account it is for");
         // One sentence for an account that is not there and for one the
         // credential may not name, so that a key restricted to a domain
         // learns nothing of the accounts outside it.
         Refused("{\"type\":\"user\",\"account_id\":999999999}", "no such account, or not one this credential may name");
         Refused("{\"type\":\"user\",\"account\":\"nobody@" + _domain.Name + "\"}", "no such account, or not one this credential may name");
         Refused("{\"type\":\"user\",\"account_id\":" + owner.ID + "}", "The folder owner's rights are implicit and cannot be changed.");
         Refused("{\"type\":\"user\",\"account_id\":" + other.ID + ",\"group_id\":1}", "names no group");
         Refused("{\"type\":\"group\"}", "names the group it is for");
         Refused("{\"type\":\"group\",\"group\":\"no-such-group\"}", "no group named no-such-group");
         Refused("{\"type\":\"anyone\",\"account_id\":" + other.ID + "}", "neither an account nor a group");
         Refused("{\"type\":\"anyone\",\"rights\":{\"fly\":true}}", "unknown right: fly");
         Refused("{\"type\":\"anyone\",\"rights\":{\"lookup\":\"yes\"}}", "must be true or false");
         Refused("{\"type\":\"anyone\",\"rights\":\"lr\"}", "rights must be an object");
         Refused("{\"type\":\"anyone\",\"colour\":\"blue\"}", "unknown field: colour");
         Assert.AreEqual(400, Http("POST", permissions, "[1]").status, "The body must be an object.");

         IMAPFolderPermissions stored = inbox.Permissions;
         stored.Refresh();
         Assert.AreEqual(0, stored.Count, "A refused grant stores nothing.");

         const string anyone = "{\"type\":\"anyone\",\"rights\":{\"lookup\":true}}";
         Assert.AreEqual(404, Http("GET", Folders(owner) + "/999999999/permissions").status);
         Assert.AreEqual(404, Http("POST", Folders(owner) + "/999999999/permissions", anyone).status);
         Assert.AreEqual(404, Http("POST", Folders(owner) + "/" + othersInbox.ID + "/permissions", anyone).status, "Another account's folder is not in this account's tree.");
         Assert.AreEqual(404, Http("PUT", permissions + "/999999999", anyone).status);
         Assert.AreEqual(404, Http("DELETE", permissions + "/999999999").status);
         Assert.AreEqual(404, Http("POST", "/api/v1/accounts/nobody@" + _domain.Name + "/folders/" + inbox.ID + "/permissions", anyone).status);

         IMAPFolderPermissions theirs = othersInbox.Permissions;
         theirs.Refresh();
         Assert.AreEqual(0, theirs.Count);
      }

      [Test]
      [Description("A key restricted to another domain cannot see or change an account's folders or their ACLs; a key for the account's own domain can; a read-only key can list and not change.")]
      public void KeysAreScopedByTheAccountsDomain()
      {
         Account owner = AddAccount("acl-scoped");
         Account grantee = AddAccount("acl-scoped-grantee");
         IMAPFolder inbox = SeededInbox(owner);
         string permissions = Permissions(owner, inbox);
         string grant = "{\"type\":\"user\",\"account_id\":" + grantee.ID + ",\"rights\":{\"lookup\":true}}";

         (string elsewhereId, string elsewhereKey) = CreateKey("restacl - elsewhere", "full", "elsewhere.test");
         (string hereId, string hereKey) = CreateKey("restacl - here", "full", _domain.Name);
         (string readOnlyId, string readOnlyKey) = CreateKey("restacl - readonly", "readonly", null);

         try
         {
            Assert.AreEqual(403, Bearer("GET", Folders(owner), elsewhereKey).status, "Another domain's key must not list the folders.");
            Assert.AreEqual(403, Bearer("GET", permissions, elsewhereKey).status, "Nor the ACL.");
            Assert.AreEqual(403, Bearer("POST", permissions, elsewhereKey, grant).status, "Nor grant.");

            (int status, string body) created = Bearer("POST", permissions, hereKey, grant);
            Assert.AreEqual(201, created.status, created.body);
            long id = long.Parse(Extract(created.body, "id"));

            Assert.AreEqual(403, Bearer("PUT", permissions + "/" + id, elsewhereKey, "{\"rights\":{\"read\":true}}").status);
            Assert.AreEqual(403, Bearer("DELETE", permissions + "/" + id, elsewhereKey).status);

            Assert.AreEqual(200, Bearer("GET", Folders(owner), readOnlyKey).status, "A read-only key lists the folders.");
            Assert.AreEqual(200, Bearer("GET", permissions, readOnlyKey).status, "And the ACL.");
            Assert.AreEqual(403, Bearer("POST", permissions, readOnlyKey, grant).status, "And grants nothing.");
            Assert.AreEqual(403, Bearer("PUT", permissions + "/" + id, readOnlyKey, "{\"rights\":{\"read\":true}}").status);
            Assert.AreEqual(403, Bearer("DELETE", permissions + "/" + id, readOnlyKey).status);

            IMAPFolderPermissions stored = inbox.Permissions;
            stored.Refresh();
            Assert.AreEqual(1, stored.Count);
            Assert.IsFalse(stored[0].get_Permission(eACLPermission.ePermissionRead), "The refused change changed nothing.");

            Assert.AreEqual(200, Bearer("DELETE", permissions + "/" + id, hereKey).status);
            stored.Refresh();
            Assert.AreEqual(0, stored.Count);
         }
         finally
         {
            Http("DELETE", "/api/v1/apikeys/" + elsewhereId);
            Http("DELETE", "/api/v1/apikeys/" + hereId);
            Http("DELETE", "/api/v1/apikeys/" + readOnlyId);
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
