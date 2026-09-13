// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
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
   ///    The external (fetch) account routes: an account's fetch accounts listed,
   ///    created, read, changed, deleted and told to collect now, under
   ///    /api/v1/accounts/{address}/fetch-accounts.
   ///
   ///    Every value a route claims to have saved is read back through COM, as
   ///    the Control Panel reads it - Account.FetchAccounts - and a collection
   ///    asked for over the route is shown to have happened by the message it
   ///    delivered. A key restricted to another domain is refused, because the
   ///    fetch account is reached through the address that scopes it.
   /// </summary>
   [TestFixture]
   public class RestApiFetchAccounts : TestFixtureBase
   {
      // The port the listener answers on: this one on the Windows bench, the
      // suite's own where RestListener finds one already on.
      private static int RestPort = 9124;
      private const string AdminPassword = "testar";

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
         RestListener.Stop();
         _application.Reinitialize();
      }

      private static string Base(Account account)
      {
         return "/api/v1/accounts/" + account.Address + "/fetch-accounts";
      }

      [Test]
      [Description("A fetch account is created over the route and seen by COM with every field, listed and read back, changed with the change seen by COM and a refused change seen by nobody, not found under another account, and deleted with COM agreeing.")]
      public void FetchAccountRoundTripsThroughCom()
      {
         Account account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "collector@example.test", "test");
         Account other = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "other@example.test", "test");

         (int status, string body) created = Http("POST", Base(account),
            "{\"name\":\"Remote POP3\",\"server_address\":\"localhost\",\"port\":9491,\"username\":\"remote@dummy-example.com\"," +
            "\"password\":\"far-side-secret\",\"minutes_between_fetch\":60,\"days_to_keep_messages\":2,\"use_antispam\":false," +
            "\"use_antivirus\":false,\"process_mime_recipients\":true}");
         Assert.AreEqual(201, created.status, created.body);
         StringAssert.Contains("\"name\":\"Remote POP3\"", created.body);
         StringAssert.Contains("\"server_type\":\"pop3\"", created.body);
         StringAssert.Contains("\"enabled\":true", created.body);
         StringAssert.Contains("\"connection_security\":\"none\"", created.body);
         StringAssert.Contains("\"locked\":false", created.body);
         StringAssert.DoesNotContain("far-side-secret", created.body, "The remote password is write-only.");
         StringAssert.DoesNotContain("\"password\"", created.body);
         long id = long.Parse(Extract(created.body, "id"));

         // COM sees what the Control Panel would show.
         FetchAccounts overCom = account.FetchAccounts;
         Assert.AreEqual(1, overCom.Count);
         FetchAccount fetchAccount = overCom[0];
         Assert.AreEqual(id, fetchAccount.ID);
         Assert.AreEqual("Remote POP3", fetchAccount.Name);
         Assert.AreEqual("localhost", fetchAccount.ServerAddress);
         Assert.AreEqual(9491, fetchAccount.Port);
         Assert.AreEqual("remote@dummy-example.com", fetchAccount.Username);
         Assert.AreEqual(60, fetchAccount.MinutesBetweenFetch);
         Assert.AreEqual(2, fetchAccount.DaysToKeepMessages);
         Assert.IsTrue(fetchAccount.Enabled);
         Assert.AreEqual(0, fetchAccount.ServerType, "POP3 is 0 over COM.");
         Assert.IsFalse(fetchAccount.UseSSL);
         Assert.IsTrue(fetchAccount.ProcessMIMERecipients);
         Assert.IsFalse(fetchAccount.UseAntiSpam);
         Assert.IsFalse(fetchAccount.IsLocked);

         // Listed and read back as the same entry.
         (int listStatus, string list) = Http("GET", Base(account));
         Assert.AreEqual(200, listStatus, list);
         StringAssert.Contains("\"id\":" + id + ",\"name\":\"Remote POP3\"", list);

         (int getStatus, string got) = Http("GET", Base(account) + "/" + id);
         Assert.AreEqual(200, getStatus, got);
         StringAssert.Contains("\"username\":\"remote@dummy-example.com\"", got);

         // Not reachable through an account that does not own it.
         (int otherStatus, string otherBody) = Http("GET", Base(other) + "/" + id);
         Assert.AreEqual(404, otherStatus, otherBody);
         StringAssert.Contains("fetch account not found", otherBody);
         Assert.AreEqual(404, Http("DELETE", Base(other) + "/" + id).status, "Nor deletable through it.");
         Assert.AreEqual(1, account.FetchAccounts.Count);

         // Changed: what the body names changes, what it leaves out stays.
         (int putStatus, string putBody) = Http("PUT", Base(account) + "/" + id,
            "{\"minutes_between_fetch\":15,\"server_type\":\"imap\",\"connection_security\":\"tls\",\"enabled\":false}");
         Assert.AreEqual(200, putStatus, putBody);
         StringAssert.Contains("\"server_type\":\"imap\"", putBody);
         StringAssert.Contains("\"connection_security\":\"tls\"", putBody);
         StringAssert.Contains("\"enabled\":false", putBody);
         StringAssert.Contains("\"username\":\"remote@dummy-example.com\"", putBody);

         fetchAccount = account.FetchAccounts[0];
         Assert.AreEqual(15, fetchAccount.MinutesBetweenFetch);
         Assert.AreEqual(1, fetchAccount.ServerType, "IMAP is 1 over COM.");
         Assert.IsTrue(fetchAccount.UseSSL, "tls is what UseSSL reads back as.");
         Assert.IsFalse(fetchAccount.Enabled);
         Assert.AreEqual("remote@dummy-example.com", fetchAccount.Username);
         Assert.AreEqual(9491, fetchAccount.Port);

         // Refused, and nothing changed: a port out of range, an unknown field, a
         // server type that is not one of the two, a name emptied.
         foreach (string refused in new[]
         {
            "{\"port\":70000}", "{\"bogus\":1}", "{\"server_type\":\"nntp\"}", "{\"name\":\"  \"}", "{\"minutes_between_fetch\":\"often\"}", "not json"
         })
         {
            (int refusedStatus, string refusedBody) = Http("PUT", Base(account) + "/" + id, refused);
            Assert.AreEqual(400, refusedStatus, refused + " -> " + refusedBody);
            StringAssert.Contains("\"error\"", refusedBody);
         }
         fetchAccount = account.FetchAccounts[0];
         Assert.AreEqual(15, fetchAccount.MinutesBetweenFetch);
         Assert.AreEqual(9491, fetchAccount.Port);
         Assert.AreEqual("Remote POP3", fetchAccount.Name);

         // A create needs a name, a server and a port.
         (int noNameStatus, string noNameBody) = Http("POST", Base(account), "{\"server_address\":\"localhost\",\"port\":110}");
         Assert.AreEqual(400, noNameStatus, noNameBody);
         StringAssert.Contains("name is required", noNameBody);
         (int noPortStatus, string noPortBody) = Http("POST", Base(account), "{\"name\":\"x\",\"server_address\":\"localhost\"}");
         Assert.AreEqual(400, noPortStatus, noPortBody);
         StringAssert.Contains("port is required", noPortBody);
         Assert.AreEqual(1, account.FetchAccounts.Count, "A refused create makes nothing.");

         // An account that does not exist.
         Assert.AreEqual(404, Http("GET", "/api/v1/accounts/nobody@example.test/fetch-accounts").status);
         Assert.AreEqual(404, Http("POST", "/api/v1/accounts/nobody@example.test/fetch-accounts", "{\"name\":\"x\",\"server_address\":\"h\",\"port\":1}").status);

         // Deleted, with COM agreeing; a second delete finds nothing.
         (int deleteStatus, string deleteBody) = Http("DELETE", Base(account) + "/" + id);
         Assert.AreEqual(200, deleteStatus, deleteBody);
         StringAssert.Contains("\"deleted\":true", deleteBody);
         Assert.AreEqual(0, account.FetchAccounts.Count);
         Assert.AreEqual(404, Http("GET", Base(account) + "/" + id).status);
         Assert.AreEqual(404, Http("DELETE", Base(account) + "/" + id).status);
         Assert.AreEqual("[]", Http("GET", Base(account)).body);
      }

      [Test]
      [Description("POST .../download collects from the remote mailbox now, as DownloadNow does: the message the simulated POP3 server held arrives in the account, and the fetch account is unlocked again afterwards.")]
      public void DownloadCollectsNow()
      {
         var messages = new List<string>
         {
            "From: sender@dummy-example.com\r\n" +
            "To: fetched@example.test\r\n" +
            "Subject: Collected over the route\r\n" +
            "\r\n" +
            "Body.\r\n"
         };

         int port = TestSetup.GetNextFreePort();
         using (var pop3Server = new Pop3ServerSimulator(1, port, messages))
         {
            pop3Server.StartListen();

            Account account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "fetched@example.test", "test");

            (int status, string body) created = Http("POST", Base(account),
               "{\"name\":\"Simulated POP3\",\"server_address\":\"localhost\",\"port\":" + port + ",\"username\":\"user\",\"password\":\"pw\"}");
            Assert.AreEqual(201, created.status, created.body);
            long id = long.Parse(Extract(created.body, "id"));

            (int queuedStatus, string queuedBody) = Http("POST", Base(account) + "/" + id + "/download");
            Assert.AreEqual(202, queuedStatus, queuedBody);
            StringAssert.Contains("\"queued\":true", queuedBody);

            pop3Server.WaitForCompletion();

            // The fetcher unlocks the account when it is done; the message it
            // collected is then in the mailbox.
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && Http("GET", Base(account) + "/" + id).body.Contains("\"locked\":true"))
               Thread.Sleep(100);
            StringAssert.Contains("\"locked\":false", Http("GET", Base(account) + "/" + id).body);

            Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

            Assert.AreEqual(200, Http("DELETE", Base(account) + "/" + id).status);
            Assert.AreEqual(404, Http("POST", Base(account) + "/" + id + "/download").status, "Nothing to collect from once deleted.");
         }
      }

      [Test]
      [Description("A key restricted to another domain cannot see or change an account's fetch accounts; a key for the account's own domain can; a read-only key can list and not change.")]
      public void KeysAreScopedByTheAccountsDomain()
      {
         Account account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "scoped@example.test", "test");
         (string elsewhereId, string elsewhereKey) = CreateKey("restfetch - elsewhere", "full", "elsewhere.test");
         (string hereId, string hereKey) = CreateKey("restfetch - here", "full", "example.test");
         (string readOnlyId, string readOnlyKey) = CreateKey("restfetch - readonly", "readonly", null);

         try
         {
            string create = "{\"name\":\"Scoped\",\"server_address\":\"localhost\",\"port\":9492}";

            Assert.AreEqual(403, Bearer("GET", Base(account), elsewhereKey).status, "Another domain's key must not list them.");
            Assert.AreEqual(403, Bearer("POST", Base(account), elsewhereKey, create).status, "Nor create one.");
            Assert.AreEqual(0, account.FetchAccounts.Count);

            (int status, string body) created = Bearer("POST", Base(account), hereKey, create);
            Assert.AreEqual(201, created.status, created.body);
            long id = long.Parse(Extract(created.body, "id"));
            Assert.AreEqual(1, account.FetchAccounts.Count);

            Assert.AreEqual(403, Bearer("DELETE", Base(account) + "/" + id, elsewhereKey).status);
            Assert.AreEqual(403, Bearer("POST", Base(account) + "/" + id + "/download", elsewhereKey).status);

            Assert.AreEqual(200, Bearer("GET", Base(account), readOnlyKey).status, "A read-only key lists.");
            Assert.AreEqual(403, Bearer("PUT", Base(account) + "/" + id, readOnlyKey, "{\"port\":9493}").status, "And changes nothing.");
            Assert.AreEqual(403, Bearer("POST", Base(account) + "/" + id + "/download", readOnlyKey).status, "A collection is a change.");
            Assert.AreEqual(9492, account.FetchAccounts[0].Port);

            Assert.AreEqual(200, Bearer("DELETE", Base(account) + "/" + id, hereKey).status);
            Assert.AreEqual(0, account.FetchAccounts.Count);
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
