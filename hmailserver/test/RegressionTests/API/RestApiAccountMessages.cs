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
   ///    The administrative message routes: a folder's messages and an account's
   ///    whole listed under /api/v1/accounts/{address}/folders/{id}/messages and
   ///    /api/v1/accounts/{address}/messages, a message added from its text, read
   ///    with its headers and its file, its flags changed, and deleted one at a
   ///    time or all at once - what IMAPFolder.Messages, Account.Messages and
   ///    Account.DeleteMessages are over COM.
   ///
   ///    Every row a route claims to have written is read back through COM, as
   ///    the Control Panel reads it, and through IMAP, as a client sees it: a
   ///    message added here is counted and flagged by the next SELECT, and one
   ///    deleted here is gone from it. A key restricted to another domain is
   ///    refused, because the message is reached through the address that scopes
   ///    it.
   /// </summary>
   [TestFixture]
   public class RestApiAccountMessages : TestFixtureBase
   {
      // The port the listener answers on: this one on the Windows bench, the
      // suite's own where RestListener finds one already on.
      private static int RestPort = 9127;
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

      private static string Raw(string subject, string extraHeader = null)
      {
         return "From: sender@example.com\r\n" +
                "To: someone@example.test\r\n" +
                "Subject: " + subject + "\r\n" +
                (extraHeader == null ? "" : extraHeader + "\r\n") +
                "Date: Mon, 14 Sep 2026 10:00:00 +0000\r\n" +
                "\r\n" +
                "The body of " + subject + ".\r\n";
      }

      // Appends the messages over IMAP, as a client would, and answers the
      // account's INBOX.
      private static IMAPFolder Appended(Account account, params string[] subjects)
      {
         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(account.Address, UserPassword));
         foreach (string subject in subjects)
         {
            string raw = Raw(subject);
            StringAssert.Contains("OK", imap.SendSingleCommandWithLiteral("A01 APPEND INBOX {" + raw.Length + "}", raw));
         }
         imap.Disconnect();

         return account.IMAPFolders.get_ItemByName("INBOX");
      }

      private static string AccountMessages(Account account)
      {
         return "/api/v1/accounts/" + account.Address + "/messages";
      }

      private static string FolderMessages(Account account, IMAPFolder folder)
      {
         return "/api/v1/accounts/" + account.Address + "/folders/" + folder.ID + "/messages";
      }

      // The listing entry for one message: from its id to the end of its
      // flags object, which closes the entry.
      private static string Entry(string body, long id)
      {
         int at = body.IndexOf("{\"id\":" + id + ",", StringComparison.Ordinal);
         Assert.IsTrue(at >= 0, "No entry for message " + id + " in: " + body);
         int end = body.IndexOf("}}", at, StringComparison.Ordinal);
         Assert.IsTrue(end >= 0, "Unterminated entry in: " + body);
         return body.Substring(at, end + 2 - at);
      }

      [Test]
      [Description("A folder's messages are listed whole - a message flagged deleted stays listed, as it stays in the COM collection - in the order and with the ids and UIDs COM knows, and the account's whole listing agrees.")]
      public void AFoldersMessagesAreListedWholeAndAgreeWithCom()
      {
         Account account = AddAccount("msg-list");
         IMAPFolder inbox = Appended(account, "One", "Two", "Three");

         hMailServer.Messages stored = inbox.Messages;
         Assert.AreEqual(3, stored.Count);
         long first = stored[0].ID, second = stored[1].ID, third = stored[2].ID;

         (int status, string body) listed = Http("GET", FolderMessages(account, inbox));
         Assert.AreEqual(200, listed.status, listed.body);
         StringAssert.Contains("\"folder_id\":" + inbox.ID + ",\"total\":3,", listed.body);
         Assert.IsTrue(listed.body.IndexOf("{\"id\":" + first + ",", StringComparison.Ordinal) < listed.body.IndexOf("{\"id\":" + second + ",", StringComparison.Ordinal), "In UID order.");
         Assert.IsTrue(listed.body.IndexOf("{\"id\":" + second + ",", StringComparison.Ordinal) < listed.body.IndexOf("{\"id\":" + third + ",", StringComparison.Ordinal), "In UID order.");
         StringAssert.Contains("\"uid\":" + stored[0].UID + ",\"folder_id\":" + inbox.ID + ",\"account_id\":", Entry(listed.body, first));
         StringAssert.Contains("\"state\":2,", Entry(listed.body, first));
         StringAssert.Contains("\"seen\":false,\"deleted\":false", Entry(listed.body, first));

         // Flagged \Deleted over IMAP and not expunged: still listed, as COM lists it.
         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(account.Address, UserPassword));
         Assert.IsTrue(imap.SelectFolder("INBOX"));
         Assert.IsTrue(imap.SetFlagOnMessage(2, true, "\\Deleted"));
         imap.Disconnect();

         listed = Http("GET", FolderMessages(account, inbox));
         Assert.AreEqual(200, listed.status, listed.body);
         StringAssert.Contains("\"total\":3,", listed.body);
         StringAssert.Contains("\"deleted\":true", Entry(listed.body, second));
         StringAssert.Contains("\"deleted\":false", Entry(listed.body, third));
         Assert.AreEqual(3, inbox.Messages.Count, "COM's collection is the folder's whole.");

         (int status, string body) whole = Http("GET", AccountMessages(account));
         Assert.AreEqual(200, whole.status, whole.body);
         StringAssert.Contains("\"total\":3,", whole.body);
         StringAssert.Contains("\"deleted\":true", Entry(whole.body, second));
         Assert.AreEqual(3, account.Messages.Count);
      }

      [Test]
      [Description("A message added from its text is stored as APPEND stores one - counted and flagged by the next SELECT, seen by COM with its headers - read whole with its file and headers, changed in its flags, and deleted with IMAP and COM agreeing.")]
      public void AMessageIsAddedFromRawTextReadWholeFlaggedAndRemoved()
      {
         Account account = AddAccount("msg-add");
         IMAPFolder inbox = Appended(account, "Seed");
         string raw = Raw("Added over REST", "X-Origin: rest");

         (int status, string body) created = Http("POST", FolderMessages(account, inbox) + "?flags=seen&from=sender@example.com", raw);
         Assert.AreEqual(201, created.status, created.body);
         StringAssert.Contains("\"folder_id\":" + inbox.ID + ",\"account_id\":", created.body);
         StringAssert.Contains("\"state\":2,", created.body);
         StringAssert.Contains("\"from_address\":\"sender@example.com\"", created.body);
         StringAssert.Contains("\"seen\":true,\"deleted\":false", created.body);
         long id = long.Parse(Extract(created.body, "id"));
         Assert.IsTrue(long.Parse(Extract(created.body, "uid")) > 0, "The row got its UID on the way: " + created.body);

         // A client sees it: counted, and \Seen, by the next SELECT.
         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(account.Address, UserPassword));
         Assert.AreEqual(2, imap.GetMessageCount("INBOX"));
         Assert.IsTrue(imap.SelectFolder("INBOX"));
         StringAssert.Contains("\\Seen", imap.GetFlags(2));
         imap.Disconnect();

         // What the Control Panel would show: the row, and the file's headers.
         Assert.AreEqual(2, inbox.Messages.Count);
         hMailServer.Message stored = inbox.Messages.get_ItemByDBID(id);
         Assert.IsNotNull(stored, "COM must find the row by its id.");
         Assert.AreEqual("Added over REST", stored.Subject);
         Assert.AreEqual("rest", stored.get_HeaderValue("X-Origin"));
         Assert.AreEqual(2, stored.State);

         (int status, string body) read = Http("GET", AccountMessages(account) + "/" + id);
         Assert.AreEqual(200, read.status, read.body);
         StringAssert.StartsWith("{\"id\":" + id + ",", read.body);
         StringAssert.Contains("\"file\":\"", read.body);
         StringAssert.Contains("\"file_exists\":true", read.body);
         StringAssert.Contains("\"subject\":\"Added over REST\",\"from\":\"sender@example.com\",\"to\":\"someone@example.test\",\"cc\":\"\",\"date\":\"Mon, 14 Sep 2026 10:00:00 +0000\"", read.body);
         StringAssert.Contains("{\"name\":\"X-Origin\",\"value\":\"rest\"}", read.body);
         StringAssert.Contains("{\"name\":\"Subject\",\"value\":\"Added over REST\"}", read.body);

         (int status, string body) source = Http("GET", AccountMessages(account) + "/" + id + "/source");
         Assert.AreEqual(200, source.status, source.body);
         Assert.AreEqual(raw, source.body, "The file is the text as sent, nothing changed or added.");

         (int status, string body) flagged = Http("PUT", AccountMessages(account) + "/" + id, "{\"seen\":false,\"flagged\":true}");
         Assert.AreEqual(200, flagged.status, flagged.body);
         StringAssert.Contains("\"seen\":false,\"deleted\":false,\"flagged\":true", flagged.body);

         imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(account.Address, UserPassword));
         Assert.IsTrue(imap.SelectFolder("INBOX"));
         string flags = imap.GetFlags(2);
         StringAssert.Contains("\\Flagged", flags);
         StringAssert.DoesNotContain("\\Seen", flags);
         imap.Disconnect();

         (int status, string body) removed = Http("DELETE", AccountMessages(account) + "/" + id);
         Assert.AreEqual(200, removed.status, removed.body);
         StringAssert.Contains("\"deleted\":true", removed.body);
         Assert.AreEqual(404, Http("GET", AccountMessages(account) + "/" + id).status, "Gone.");
         Assert.AreEqual(404, Http("DELETE", AccountMessages(account) + "/" + id).status, "Deleted twice is not found.");

         Assert.AreEqual(1, inbox.Messages.Count);
         imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(account.Address, UserPassword));
         Assert.AreEqual(1, imap.GetMessageCount("INBOX"));
         imap.Disconnect();
      }

      [Test]
      [Description("The account's whole listing spans its folders, and deleting it whole empties the mailbox with COM and IMAP agreeing.")]
      public void AnAccountsMessagesSpanItsFoldersAndAreDeletedWhole()
      {
         Account account = AddAccount("msg-whole");
         IMAPFolder inbox = Appended(account, "In the inbox");

         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(account.Address, UserPassword));
         StringAssert.Contains("OK", imap.SendSingleCommand("A02 CREATE Projects"));
         string raw = Raw("In a folder");
         StringAssert.Contains("OK", imap.SendSingleCommandWithLiteral("A03 APPEND Projects {" + raw.Length + "}", raw));
         imap.Disconnect();

         IMAPFolder projects = account.IMAPFolders.get_ItemByName("Projects");

         (int status, string body) whole = Http("GET", AccountMessages(account));
         Assert.AreEqual(200, whole.status, whole.body);
         StringAssert.Contains("\"total\":2,", whole.body);
         StringAssert.Contains("\"folder_id\":" + inbox.ID + ",", whole.body);
         StringAssert.Contains("\"folder_id\":" + projects.ID + ",", whole.body);
         Assert.AreEqual(2, account.Messages.Count);
         Assert.AreEqual(1, projects.Messages.Count);

         (int status, string body) emptied = Http("DELETE", AccountMessages(account));
         Assert.AreEqual(200, emptied.status, emptied.body);
         StringAssert.Contains("\"deleted\":true", emptied.body);

         // Read afresh: a COM Account keeps the message collection it first
         // read, and the delete removed it from the server's cache, not from
         // the object this test holds.
         Account emptiedAccount = _domain.Accounts.get_ItemByAddress(account.Address);
         Assert.AreEqual(0, emptiedAccount.Messages.Count, "COM sees the mailbox emptied.");
         imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(account.Address, UserPassword));
         Assert.AreEqual(0, imap.GetMessageCount("INBOX"), "The inbox is kept, emptied.");
         imap.Disconnect();
      }

      [Test]
      [Description("A refusal names what is wrong and stores nothing, another account's message or folder is not found under this address, and a key restricted to another domain reaches nothing.")]
      public void RefusalsAreNamedAndKeysAreScopedByTheAccountsDomain()
      {
         Account owner = AddAccount("msg-scoped");
         Account other = AddAccount("msg-scoped-other");
         IMAPFolder inbox = Appended(owner, "Mine");
         IMAPFolder othersInbox = Appended(other, "Theirs");
         long mine = inbox.Messages[0].ID;
         long theirs = othersInbox.Messages[0].ID;
         string raw = Raw("Refused");

         (int status, string body) empty = Http("POST", FolderMessages(owner, inbox), "");
         Assert.AreEqual(400, empty.status, empty.body);
         StringAssert.Contains("the body is the message", empty.body);

         (int status, string body) headless = Http("POST", FolderMessages(owner, inbox), "no header line here\r\n\r\nbody\r\n");
         Assert.AreEqual(400, headless.status, headless.body);
         StringAssert.Contains("does not begin with a header line", headless.body);

         (int status, string body) shiny = Http("POST", FolderMessages(owner, inbox) + "?flags=seen,shiny", raw);
         Assert.AreEqual(400, shiny.status, shiny.body);
         StringAssert.Contains("unknown flag: shiny", shiny.body);
         Assert.AreEqual(1, inbox.Messages.Count, "A refused add stores nothing.");

         (int status, string body) unnamed = Http("PUT", AccountMessages(owner) + "/" + mine, "{}");
         Assert.AreEqual(400, unnamed.status, unnamed.body);
         StringAssert.Contains("no flag named", unnamed.body);
         (int status, string body) notBool = Http("PUT", AccountMessages(owner) + "/" + mine, "{\"seen\":\"yes\"}");
         Assert.AreEqual(400, notBool.status, notBool.body);
         StringAssert.Contains("must be true or false", notBool.body);
         Assert.AreEqual(400, Http("PUT", AccountMessages(owner) + "/" + mine, "{\"colour\":\"blue\"}").status);

         Assert.AreEqual(404, Http("GET", AccountMessages(owner) + "/" + theirs).status, "Another account's message is not found under this address.");
         Assert.AreEqual(404, Http("DELETE", AccountMessages(owner) + "/" + theirs).status);
         Assert.AreEqual(404, Http("PUT", AccountMessages(owner) + "/" + theirs, "{\"seen\":true}").status);
         Assert.AreEqual(404, Http("GET", AccountMessages(owner) + "/" + theirs + "/source").status);
         Assert.AreEqual(404, Http("GET", AccountMessages(owner) + "/999999999").status);
         Assert.AreEqual(404, Http("GET", FolderMessages(owner, othersInbox)).status, "Another account's folder is not in this account's tree.");
         Assert.AreEqual(404, Http("POST", FolderMessages(owner, othersInbox), raw).status);
         Assert.AreEqual(404, Http("GET", "/api/v1/accounts/nobody@" + _domain.Name + "/messages").status);
         Assert.AreEqual(1, othersInbox.Messages.Count, "And nothing of theirs changed.");

         (string elsewhereId, string elsewhereKey) = CreateKey("restmsg - elsewhere", "full", "elsewhere.test");
         (string hereId, string hereKey) = CreateKey("restmsg - here", "full", _domain.Name);
         (string readOnlyId, string readOnlyKey) = CreateKey("restmsg - readonly", "readonly", null);

         try
         {
            Assert.AreEqual(403, Bearer("GET", AccountMessages(owner), elsewhereKey).status, "Another domain's key must not list them.");
            Assert.AreEqual(403, Bearer("GET", FolderMessages(owner, inbox), elsewhereKey).status);
            Assert.AreEqual(403, Bearer("POST", FolderMessages(owner, inbox), elsewhereKey, raw).status, "Nor add one.");
            Assert.AreEqual(403, Bearer("DELETE", AccountMessages(owner) + "/" + mine, elsewhereKey).status);
            Assert.AreEqual(403, Bearer("DELETE", AccountMessages(owner), elsewhereKey).status);

            Assert.AreEqual(200, Bearer("GET", AccountMessages(owner), readOnlyKey).status, "A read-only key lists.");
            Assert.AreEqual(200, Bearer("GET", AccountMessages(owner) + "/" + mine, readOnlyKey).status);
            Assert.AreEqual(200, Bearer("GET", AccountMessages(owner) + "/" + mine + "/source", readOnlyKey).status);
            Assert.AreEqual(403, Bearer("POST", FolderMessages(owner, inbox), readOnlyKey, raw).status, "And changes nothing.");
            Assert.AreEqual(403, Bearer("PUT", AccountMessages(owner) + "/" + mine, readOnlyKey, "{\"seen\":true}").status);
            Assert.AreEqual(403, Bearer("DELETE", AccountMessages(owner) + "/" + mine, readOnlyKey).status);
            Assert.AreEqual(403, Bearer("DELETE", AccountMessages(owner), readOnlyKey).status);
            Assert.AreEqual(1, inbox.Messages.Count);

            (int status, string body) added = Bearer("POST", FolderMessages(owner, inbox), hereKey, raw);
            Assert.AreEqual(201, added.status, added.body);
            Assert.AreEqual(2, inbox.Messages.Count);
            Assert.AreEqual(200, Bearer("DELETE", AccountMessages(owner) + "/" + Extract(added.body, "id"), hereKey).status);
            Assert.AreEqual(1, inbox.Messages.Count);
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
