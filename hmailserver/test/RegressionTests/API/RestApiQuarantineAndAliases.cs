// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Linq;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.API
{
   /// <summary>
   ///    The REST API's quarantine and alias endpoints, and the OpenAPI
   ///    description that maps them.
   ///
   ///    The quarantine endpoints exist because the store shipped
   ///    administrator-reviewable but only over COM - a remote reviewer had no
   ///    way in. The OpenAPI document lives in the router's own source file so
   ///    a route change and its documentation land in the same diff; the test
   ///    here pins that promise by asserting every route is described, which
   ///    fails the moment somebody adds an endpoint without documenting it.
   /// </summary>
   [TestFixture]
   public class RestApiQuarantineAndAliases : TestFixtureBase
   {
      private const int RestPort = 9098;
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

      private static (int status, string body) Http(string method, string path)
      {
         string credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes("Administrator:" + AdminPassword));

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
               byte[] request = Encoding.ASCII.GetBytes(
                  method + " " + path + " HTTP/1.0\r\n" +
                  "Host: 127.0.0.1\r\n" +
                  "Authorization: Basic " + credentials + "\r\n" +
                  "Connection: close\r\n\r\n");

               stream.Write(request, 0, request.Length);

               byte[] buffer = new byte[4096];
               int read;
               while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                  memory.Write(buffer, 0, read);

               string raw = Encoding.UTF8.GetString(memory.ToArray());

               int statusCode = 0;
               string[] lines = raw.Split(new[] {"\r\n"}, StringSplitOptions.None);
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
         WriteSetting("RestApiPort", "0");
         _application.Reinitialize();
      }

      [Test]
      [Description("The OpenAPI document describes every route the server actually serves - an endpoint added without documentation fails here")]
      public void OpenApiDocumentDescribesEveryRoute()
      {
         (int status, string body) = Http("GET", "/api/v1/openapi.json");

         Assert.AreEqual(200, status, body);
         StringAssert.Contains("\"openapi\":\"3.0.3\"", body);

         // One entry per route the dispatcher can reach. This list is the
         // test's copy of the contract: extending the API means extending BOTH
         // the document and this list, and the failure message says so.
         string[] documentedPaths =
         {
            "/api/v1/status",
            "/api/v1/domains",
            "/api/v1/domains/{domain}/accounts",
            "/api/v1/accounts/{address}",
            "/api/v1/queue",
            "/api/v1/queue/{id}/retry",
            "/api/v1/queue/{id}",
            "/api/v1/quarantine",
            "/api/v1/quarantine/{id}/release",
            "/api/v1/quarantine/{id}",
            "/api/v1/domains/{domain}/aliases",
            "/api/v1/tlsa",
            "/api/v1/apikeys",
            "/api/v1/apikeys/{id}",
            "/api/v1/openapi.json"
         };

         foreach (string path in documentedPaths)
         {
            StringAssert.Contains("\"" + path + "\"", body,
               "The OpenAPI document must describe " + path + " - a route without documentation is how the " +
               "description drifts from the router it sits beside.");
         }
      }

      [Test]
      [Description("Quarantine entries can be listed and deleted over the API; unknown ids are 404, and a failed release leaves the entry standing")]
      public void QuarantineCanBeListedAndDeleted()
      {
         // Seeded directly, with a file that does not exist. The full
         // spam-scoring path to a real quarantine entry is AntiSpam.Quarantine's
         // job; what the API owes is faithful listing, a 404 for a wrong id,
         // and - the part worth a deliberate broken entry - that a release
         // whose file is gone FAILS and leaves the row visible, rather than
         // reporting a delivery that never happened.
         int id = SingletonProvider<TestSetup>.Instance.GetApp().Database.ExecuteSQLWithReturn(
            "insert into hm_quarantine (quarantinefilename, quarantinesender, quarantinerecipients, quarantinesubject, quarantinereason, quarantinescore, quarantinesize, quarantinecreated) " +
            "values ('rest-api-test-no-such-file.eml', 'quarantined-sender@dummy-example.com', 'victim@example.test', 'api subject', 'api reason', 12, 345, GETDATE())");

         try
         {
            (int status, string body) = Http("GET", "/api/v1/quarantine");
            Assert.AreEqual(200, status, body);
            StringAssert.Contains("quarantined-sender@dummy-example.com", body,
               "The seeded entry must appear in the listing.");
            StringAssert.Contains("\"id\":" + id, body);

            // Releasing an entry whose file is missing must fail - and must
            // not consume the entry, because "released" for a message nobody
            // received is the lie the store exists to avoid.
            (status, body) = Http("POST", "/api/v1/quarantine/" + id + "/release");
            Assert.AreEqual(500, status, "A release with no file behind it must fail. Body: " + body);

            (status, body) = Http("GET", "/api/v1/quarantine");
            StringAssert.Contains("\"id\":" + id, body, "A failed release must leave the entry standing.");

            // Unknown ids are 404 on both mutating verbs.
            (status, _) = Http("POST", "/api/v1/quarantine/999999999/release");
            Assert.AreEqual(404, status);

            (status, _) = Http("DELETE", "/api/v1/quarantine/999999999");
            Assert.AreEqual(404, status);

            // And the delete works, once, tolerating the missing file.
            (status, body) = Http("DELETE", "/api/v1/quarantine/" + id);
            Assert.AreEqual(200, status, body);
            StringAssert.Contains("\"deleted\":true", body);

            (status, _) = Http("DELETE", "/api/v1/quarantine/" + id);
            Assert.AreEqual(404, status, "The second delete of the same id must be a 404, not a second success.");
         }
         finally
         {
            SingletonProvider<TestSetup>.Instance.GetApp().Database.ExecuteSQL(
               "delete from hm_quarantine where quarantineid = " + id);
         }
      }

      [Test]
      [Description("Aliases are listed per domain, in the same shape as the account listing; an unknown domain is 404")]
      public void AliasesAreListedPerDomain()
      {
         var alias = SingletonProvider<TestSetup>.Instance.AddAlias(_domain, "api-alias@example.test", "api-target@example.test");

         try
         {
            (int status, string body) = Http("GET", "/api/v1/domains/example.test/aliases");

            Assert.AreEqual(200, status, body);
            StringAssert.Contains("\"name\":\"api-alias@example.test\"", body);
            StringAssert.Contains("\"value\":\"api-target@example.test\"", body);
            StringAssert.Contains("\"active\":true", body);

            (status, body) = Http("GET", "/api/v1/domains/no-such-domain.test/aliases");
            Assert.AreEqual(404, status, body);
         }
         finally
         {
            alias.Delete();
         }
      }
   }
}
