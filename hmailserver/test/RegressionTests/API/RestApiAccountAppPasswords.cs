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
   ///    The administrative app-password routes: an account's app passwords
   ///    listed, issued and removed under /api/v1/accounts/{address}/app-passwords
   ///    by the administrator, where /api/v1/me/app-passwords does the same for
   ///    the account holder.
   ///
   ///    Every value a route claims to have stored is read back through COM, as
   ///    the Control Panel reads it - Account.AppPasswords - and an issued
   ///    password is shown to be a credential by logging on with it. A key
   ///    restricted to another domain is refused, because the app password is
   ///    reached through the address that scopes it.
   /// </summary>
   [TestFixture]
   public class RestApiAccountAppPasswords : TestFixtureBase
   {
      // The port the listener answers on: this one on the Windows bench, the
      // suite's own where RestListener finds one already on.
      private static int RestPort = 9125;
      private const string AdminPassword = "testar";

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
         Account account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, name + "@" + _domain.Name, "test");
         RequireRegisteredInterfaces(account);
         return account;
      }

      /// <summary>
      ///    The same stand-down Security/AppPasswords makes, for the same reason:
      ///    hMailServer is an out-of-process COM server running as LocalSystem, so
      ///    IInterfaceAppPasswords is resolved through HKLM, and a build that was
      ///    not registered from an elevated shell answers REGDB_E_IIDNOTREG to the
      ///    read-back - which says nothing about the route under test.
      /// </summary>
      private static void RequireRegisteredInterfaces(Account account)
      {
         try
         {
            var probe = account.AppPasswords;
            ClassicAssert.IsNotNull(probe);
         }
         catch (System.Runtime.InteropServices.COMException ex) when ((uint) ex.ErrorCode == 0x80040155)
         {
            Assert.Ignore(
               "The app-password COM interfaces are not registered on this machine, so the read-back cannot run. " +
               "Run 'hMailServer.exe /RegisterTypeLib' from an ELEVATED prompt in " +
               "hmailserver/source/Server/hMailServer/x64/Release, then run this fixture again. REGDB_E_IIDNOTREG: " + ex.Message);
         }
      }

      private static string Base(Account account)
      {
         return "/api/v1/accounts/" + account.Address + "/app-passwords";
      }

      [Test]
      [Description("An app password is issued over the route with its clear text answered once, seen by COM, logs on, is listed without the secret, and is removed with COM agreeing and the logon refused.")]
      public void AnAppPasswordIsIssuedListedUsedAndRemoved()
      {
         Account account = AddAccount("apppw1");

         (int status, string body) created = Http("POST", Base(account), "{\"name\":\"Phone\"}");
         Assert.AreEqual(201, created.status, created.body);
         StringAssert.Contains("\"name\":\"Phone\"", created.body);
         StringAssert.Contains("\"active\":true", created.body);
         StringAssert.Contains("\"last_used\":\"\"", created.body);
         string clearText = Extract(created.body, "password");
         Assert.IsTrue(clearText.Length >= 12, "The generated secret must clear the credential's own floor. Got: " + clearText);
         long id = long.Parse(Extract(created.body, "id"));

         // What the Control Panel would show.
         AppPasswords passwords = account.AppPasswords;
         Assert.AreEqual(1, passwords.Count);
         Assert.AreEqual("Phone", passwords[0].Name);
         Assert.AreEqual(id, passwords[0].ID);
         Assert.IsTrue(passwords[0].Active);
         Assert.IsFalse(string.IsNullOrEmpty(passwords[0].CreatedTime), "The store stamps the issue time.");

         // A credential, not a row: it opens the mailbox.
         var pop3 = new Pop3ClientSimulator();
         Assert.IsTrue(pop3.ConnectAndLogon(account.Address, clearText), "The issued password must open the mailbox.");
         pop3.Disconnect();

         // Listed as the account holder's own route lists them: never the secret.
         (int status, string body) listed = Http("GET", Base(account));
         Assert.AreEqual(200, listed.status, listed.body);
         StringAssert.Contains("\"id\":" + id + ",\"name\":\"Phone\"", listed.body);
         StringAssert.DoesNotContain(clearText, listed.body);
         StringAssert.DoesNotContain("\"password\"", listed.body);

         (int status, string body) removed = Http("DELETE", Base(account) + "/" + id);
         Assert.AreEqual(200, removed.status, removed.body);
         StringAssert.Contains("\"deleted\":true", removed.body);
         Assert.AreEqual(0, account.AppPasswords.Count);
         Assert.AreEqual(404, Http("DELETE", Base(account) + "/" + id).status, "Removed twice is not found.");

         Assert.IsFalse(new Pop3ClientSimulator().ConnectAndLogon(account.Address, clearText), "A removed password opens nothing.");
      }

      [Test]
      [Description("A chosen secret is held to the credential's floor of twelve characters, as SetPassword holds it over COM, and nothing is stored when it is refused; one that passes is stored as given.")]
      public void AChosenSecretIsHeldToTheFloor()
      {
         Account account = AddAccount("apppw2");

         (int status, string body) tooShort = Http("POST", Base(account), "{\"name\":\"Laptop\",\"password\":\"short\"}");
         Assert.AreEqual(400, tooShort.status, tooShort.body);
         StringAssert.Contains("at least 12 characters", tooShort.body);
         Assert.AreEqual(0, account.AppPasswords.Count, "Nothing is stored when the secret is refused.");

         const string chosen = "Chosen-Secret-2026!";
         (int status, string body) accepted = Http("POST", Base(account), "{\"name\":\"Laptop\",\"password\":\"" + chosen + "\"}");
         Assert.AreEqual(201, accepted.status, accepted.body);
         Assert.AreEqual(chosen, Extract(accepted.body, "password"));
         Assert.AreEqual(1, account.AppPasswords.Count);

         var pop3 = new Pop3ClientSimulator();
         Assert.IsTrue(pop3.ConnectAndLogon(account.Address, chosen), "The chosen secret must open the mailbox.");
         pop3.Disconnect();
      }

      [Test]
      [Description("A refusal names what is wrong: no name, an unknown field, a body that is not an object, an account that does not exist, another account's password, and the store's ceiling of twenty.")]
      public void RefusalsNameWhatIsWrong()
      {
         Account account = AddAccount("apppw3");
         Account other = AddAccount("apppw3b");

         (int status, string body) unnamed = Http("POST", Base(account), "{}");
         Assert.AreEqual(400, unnamed.status, unnamed.body);
         StringAssert.Contains("name is required", unnamed.body);

         (int status, string body) unknown = Http("POST", Base(account), "{\"name\":\"X\",\"colour\":\"blue\"}");
         Assert.AreEqual(400, unknown.status, unknown.body);
         StringAssert.Contains("unknown field: colour", unknown.body);

         Assert.AreEqual(400, Http("POST", Base(account), "[1]").status, "The body must be an object.");

         string nobody = "/api/v1/accounts/nobody@" + _domain.Name + "/app-passwords";
         Assert.AreEqual(404, Http("GET", nobody).status);
         Assert.AreEqual(404, Http("POST", nobody, "{\"name\":\"X\"}").status);
         Assert.AreEqual(404, Http("DELETE", nobody + "/1").status);

         (int status, string body) theirs = Http("POST", Base(other), "{\"name\":\"Theirs\"}");
         Assert.AreEqual(201, theirs.status, theirs.body);
         long theirId = long.Parse(Extract(theirs.body, "id"));
         Assert.AreEqual(404, Http("DELETE", Base(account) + "/" + theirId).status, "Another account's password is not found under this one.");
         Assert.AreEqual(1, other.AppPasswords.Count, "And it is still there.");

         for (int i = 1; i <= 20; i++)
         {
            (int status, string body) issued = Http("POST", Base(account), "{\"name\":\"Device " + i + "\"}");
            Assert.AreEqual(201, issued.status, "Password " + i + ": " + issued.body);
         }

         (int status, string body) refused = Http("POST", Base(account), "{\"name\":\"Device 21\"}");
         Assert.AreEqual(400, refused.status, refused.body);
         StringAssert.Contains("maximum of 20", refused.body);
         Assert.AreEqual(20, account.AppPasswords.Count);
      }

      [Test]
      [Description("A key restricted to another domain cannot see or issue an account's app passwords; a key for the account's own domain can; a read-only key can list and not issue.")]
      public void KeysAreScopedByTheAccountsDomain()
      {
         Account account = AddAccount("apppw4");
         (string elsewhereId, string elsewhereKey) = CreateKey("restapppw - elsewhere", "full", "elsewhere.test");
         (string hereId, string hereKey) = CreateKey("restapppw - here", "full", _domain.Name);
         (string readOnlyId, string readOnlyKey) = CreateKey("restapppw - readonly", "readonly", null);

         try
         {
            const string create = "{\"name\":\"Scoped\"}";

            Assert.AreEqual(403, Bearer("GET", Base(account), elsewhereKey).status, "Another domain's key must not list them.");
            Assert.AreEqual(403, Bearer("POST", Base(account), elsewhereKey, create).status, "Nor issue one.");
            Assert.AreEqual(0, account.AppPasswords.Count);

            (int status, string body) created = Bearer("POST", Base(account), hereKey, create);
            Assert.AreEqual(201, created.status, created.body);
            long id = long.Parse(Extract(created.body, "id"));
            Assert.AreEqual(1, account.AppPasswords.Count);

            Assert.AreEqual(403, Bearer("DELETE", Base(account) + "/" + id, elsewhereKey).status);

            Assert.AreEqual(200, Bearer("GET", Base(account), readOnlyKey).status, "A read-only key lists.");
            Assert.AreEqual(403, Bearer("POST", Base(account), readOnlyKey, create).status, "And issues nothing.");
            Assert.AreEqual(403, Bearer("DELETE", Base(account) + "/" + id, readOnlyKey).status, "Nor removes anything.");
            Assert.AreEqual(1, account.AppPasswords.Count);

            Assert.AreEqual(200, Bearer("DELETE", Base(account) + "/" + id, hereKey).status);
            Assert.AreEqual(0, account.AppPasswords.Count);
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
