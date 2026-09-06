// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.API
{
   /// <summary>
   ///    The archive index over REST: search, one entry, hold and release, with
   ///    the domain scope decided from the query and from the row rather than from
   ///    the route - a domain-restricted key searches one of its own domains and
   ///    touches only rows in them.
   /// </summary>
   [TestFixture]
   public class RestApiArchive : TestFixtureBase
   {
      private const int RestPort = 9098;
      private const string AdminPassword = "testar";
      private string archiveRoot_;

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
            Assert.IsTrue(IniFile.WritePrivateProfileString("Settings", key, value, iniPath), "Failed to write " + key + " to " + iniPath + ".");
            wroteAny = true;
         }

         Assert.IsTrue(wroteAny, "Could not locate an existing hMailServer.ini to update.");
      }

      private static (int status, string body) Http(string method, string path, string authorization = null, string requestBody = null)
      {
         if (authorization == null)
            authorization = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("Administrator:" + AdminPassword));

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
               return (statusCode, separator >= 0 ? raw.Substring(separator + 4) : "");
            }
         }
      }

      private static string Extract(string json, string key)
      {
         string needle = "\"" + key + "\":\"";
         int at = json.IndexOf(needle, StringComparison.Ordinal);
         Assert.IsTrue(at >= 0, "No '" + key + "' in: " + json);
         int start = at + needle.Length;
         return json.Substring(start, json.IndexOf('"', start) - start);
      }

      // Rows outlive the fixtures that wrote them - the archive outlives its accounts
      // by design - so every count here is scoped to a subject no other test or run
      // has used.
      private static string Unique(string prefix)
      {
         return prefix + " " + Guid.NewGuid().ToString("N").Substring(0, 8);
      }

      private static int Count(string json, string needle)
      {
         int count = 0;
         for (int at = json.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = json.IndexOf(needle, at + 1, StringComparison.Ordinal))
            count++;
         return count;
      }

      private static long FirstId(string json)
      {
         const string needle = "\"id\":";
         int at = json.IndexOf(needle, StringComparison.Ordinal);
         Assert.IsTrue(at >= 0, "No entry in: " + json);
         int start = at + needle.Length;
         return long.Parse(json.Substring(start, json.IndexOf(',', start) - start));
      }

      [SetUp]
      public void StartRestApiAndTheArchive()
      {
         archiveRoot_ = Paths.Combine(Path.GetTempPath(), "hmail-rest-archive-" + Guid.NewGuid().ToString("N"));
         Directory.CreateDirectory(archiveRoot_);

         _settings.SetAdministratorPassword(AdminPassword);
         WriteSetting("RestApiBindAddress", "127.0.0.1");
         WriteSetting("RestApiPort", RestPort.ToString());
         WriteSetting("ArchiveDir", archiveRoot_);
         _application.Reinitialize();

         (int status, string body) probe = Http("GET", "/api/v1/status");
         Assert.AreEqual(200, probe.status, "REST API did not answer /api/v1/status. Body: " + probe.body);
      }

      [TearDown]
      public void StopRestApiAndRemoveTheArchive()
      {
         WriteSetting("RestApiPort", "0");
         WriteSetting("ArchiveDir", "");
         _application.Reinitialize();
         try
         {
            Directory.Delete(archiveRoot_, true);
         }
         catch (IOException)
         {
            // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
         }
      }

      private void ArchiveOneMessage(string subject)
      {
         Account alice = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "alice@example.test", "test");
         Account bob = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "bob@example.test", "test");
         SmtpClientSimulator.StaticSend(alice.Address, bob.Address, subject, "Body text.");
         CustomAsserts.AssertRecipientsInDeliveryQueue(0);
      }

      [Test]
      [Description("The archive is searched over REST by domain, subject and mailbox; one entry is read by id; an unknown id is 404; the OpenAPI document names the routes.")]
      public void TheArchiveIsSearchedOverRest()
      {
         string needle = Unique("REST archive needle");
         ArchiveOneMessage(needle);

         (int status, string body) = Http("GET", "/api/v1/archive?domain=example.test&subject=" + Uri.EscapeDataString(needle));
         Assert.AreEqual(200, status, body);
         Assert.AreEqual(2, Count(body, "\"id\":"), body);
         StringAssert.Contains("\"subject\":\"" + needle + "\"", body);

         (int subjectStatus, string bySubject) = Http("GET", "/api/v1/archive?subject=" + Uri.EscapeDataString(needle.Substring(5)) + "&mailbox=bob");
         Assert.AreEqual(200, subjectStatus, bySubject);
         Assert.AreEqual(1, Count(bySubject, "\"id\":"), "the %20 in the query is decoded and mailbox narrows to bob: " + bySubject);
         StringAssert.Contains("\"direction\":\"received\"", bySubject);

         long id = FirstId(bySubject);
         (int oneStatus, string one) = Http("GET", "/api/v1/archive/" + id);
         Assert.AreEqual(200, oneStatus, one);
         StringAssert.Contains("\"id\":" + id + ",", one);
         StringAssert.Contains("\"mailbox\":\"bob\"", one);

         Assert.AreEqual(404, Http("GET", "/api/v1/archive/987654321").status);

         (int docStatus, string doc) = Http("GET", "/api/v1/openapi.json");
         Assert.AreEqual(200, docStatus);
         foreach (string path in new[] { "/api/v1/archive", "/api/v1/archive/{id}", "/api/v1/archive/{id}/hold" })
            StringAssert.Contains("\"" + path + "\"", doc, "The OpenAPI document must describe " + path);
      }

      [Test]
      [Description("A hold is placed and lifted over REST, read back in the entry, and refused for an unknown id and for a read-only key.")]
      public void HoldsArePlacedAndLiftedOverRest()
      {
         string needle = Unique("Hold me");
         ArchiveOneMessage(needle);
         (int status, string body) = Http("GET", "/api/v1/archive?domain=example.test&mailbox=bob&subject=" + Uri.EscapeDataString(needle));
         Assert.AreEqual(200, status, body);
         long id = FirstId(body);

         Assert.AreEqual(200, Http("POST", "/api/v1/archive/" + id + "/hold").status);
         StringAssert.Contains("\"hold\":true", Http("GET", "/api/v1/archive/" + id).body);
         StringAssert.Contains("\"id\":" + id + ",", Http("GET", "/api/v1/archive?domain=example.test&hold=1").body, "hold=1 lists the held copies");

         Assert.AreEqual(200, Http("DELETE", "/api/v1/archive/" + id + "/hold").status);
         StringAssert.Contains("\"hold\":false", Http("GET", "/api/v1/archive/" + id).body);
         Assert.AreEqual(404, Http("POST", "/api/v1/archive/987654321/hold").status);

         (int created, string key) = Http("POST", "/api/v1/apikeys", null, "{\"label\":\"rest archive - readonly\",\"scope\":\"readonly\"}");
         Assert.AreEqual(201, created, key);
         string token = Extract(key, "key");
         Assert.AreEqual(403, Http("POST", "/api/v1/archive/" + id + "/hold", "Bearer " + token).status, "a read-only key cannot place a hold");
         Assert.AreEqual(200, Http("GET", "/api/v1/archive/" + id, "Bearer " + token).status, "but it can read");
      }

      [Test]
      [Description("A domain-restricted key must name one of its domains to search, sees only rows in them, and cannot touch a row of another domain or an Inbound copy.")]
      public void ADomainRestrictedKeyStaysInItsDomains()
      {
         string needle = Unique("Scoped");
         ArchiveOneMessage(needle);

         (int created, string key) = Http("POST", "/api/v1/apikeys", null, "{\"label\":\"rest archive - domain\",\"scope\":\"full\",\"domains\":\"example.test\"}");
         Assert.AreEqual(201, created, key);
         string bearer = "Bearer " + Extract(key, "key");

         Assert.AreEqual(403, Http("GET", "/api/v1/archive", bearer).status, "no domain named");
         Assert.AreEqual(403, Http("GET", "/api/v1/archive?domain=other.test", bearer).status, "another domain");

         (int status, string body) = Http("GET", "/api/v1/archive?domain=example.test&subject=" + Uri.EscapeDataString(needle), bearer);
         Assert.AreEqual(200, status, body);
         Assert.AreEqual(2, Count(body, "\"id\":"), body);

         long id = FirstId(body);
         Assert.AreEqual(200, Http("POST", "/api/v1/archive/" + id + "/hold", bearer).status, "its own domain's row can be held");
         Assert.AreEqual(200, Http("DELETE", "/api/v1/archive/" + id + "/hold", bearer).status);

         // An Inbound copy (from an outside sender) belongs to no domain: not the key's.
         string outside = Unique("From outside");
         SmtpClientSimulator.StaticSend("outsider@elsewhere.test", "bob@example.test", outside, "Body text.");
         CustomAsserts.AssertRecipientsInDeliveryQueue(0);
         (int allStatus, string all) = Http("GET", "/api/v1/archive?subject=" + Uri.EscapeDataString(outside));
         Assert.AreEqual(200, allStatus, all);
         StringAssert.Contains("\"direction\":\"inbound\"", all);
         long inboundId = 0;
         foreach (string entry in all.Split(new[] { "{\"id\":" }, StringSplitOptions.RemoveEmptyEntries)
                                     .Where(entry => entry.Contains("\"direction\":\"inbound\"")))
            inboundId = long.Parse(entry.Substring(0, entry.IndexOf(',')));
         Assert.IsTrue(inboundId > 0, "the Inbound copy has a row: " + all);
         Assert.AreEqual(403, Http("GET", "/api/v1/archive/" + inboundId, bearer).status, "the Inbound copy is nobody's domain");
         Assert.AreEqual(200, Http("GET", "/api/v1/archive/" + inboundId).status, "the administrator sees it");
      }
   }
}
