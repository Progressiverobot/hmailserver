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
   ///    The REST surfaces that were COM-only until 5 September 2026: IP ranges,
   ///    distribution lists, certificates, DKIM, the global rules, the logs, the
   ///    backup and a read-only settings snapshot. Every assertion here is made
   ///    against what the server actually did - a range that the next request
   ///    lists, a list whose members COM reads back, a log line that exists on
   ///    disk - and the authorisation tests pin the one rule that matters most
   ///    for a surface this wide: a domain-restricted key learns nothing
   ///    server-wide, and a read-only key changes nothing.
   /// </summary>
   [TestFixture]
   public class RestApiCoverage : TestFixtureBase
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

      private static (int status, string body) Http(string method, string path, string requestBody = null)
      {
         string credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes("Administrator:" + AdminPassword));
         return Http(method, path, "Basic " + credentials, requestBody);
      }

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

      // The token of a key created over the API, with the scope the caller asks for.
      private static string CreateKey(string label, string scope, string domains)
      {
         string body = "{\"label\":\"" + label + "\",\"scope\":\"" + scope + "\"" +
                       (domains == null ? "" : ",\"domains\":\"" + domains + "\"") + "}";
         (int status, string created) = Http("POST", "/api/v1/apikeys", body);
         Assert.AreEqual(201, status, "POST /api/v1/apikeys must create a key. Body: " + created);
         return Extract(created, "key");
      }

      // The string value of a top-level JSON property, enough for the bodies this API returns.
      private static string Extract(string json, string key)
      {
         string needle = "\"" + key + "\":\"";
         int at = json.IndexOf(needle, StringComparison.Ordinal);
         Assert.IsTrue(at >= 0, "No '" + key + "' in: " + json);
         int start = at + needle.Length;
         int end = json.IndexOf('"', start);
         return json.Substring(start, end - start);
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
      [Description("An IP range is created with its permissions, listed with them, deleted, and gone; a second delete is 404 and an address that does not parse is 400.")]
      public void IpRangesRoundTrip()
      {
         (int status, string body) created = Http("POST", "/api/v1/ipranges",
            "{\"name\":\"rest-range\",\"lower\":\"10.99.1.1\",\"upper\":\"10.99.1.254\",\"priority\":42," +
            "\"allow_smtp\":true,\"allow_imap\":false,\"allow_pop3\":false,\"deliver_remote_to_remote\":false,\"spam_protection\":false}");
         Assert.AreEqual(201, created.status, created.body);
         StringAssert.Contains("\"id\":", created.body);
         string id = created.body.Substring(created.body.IndexOf(':') + 1).TrimEnd('}');

         (int listStatus, string list) = Http("GET", "/api/v1/ipranges");
         Assert.AreEqual(200, listStatus, list);
         StringAssert.Contains("\"name\":\"rest-range\"", list);
         StringAssert.Contains("\"lower\":\"10.99.1.1\"", list);
         StringAssert.Contains("\"upper\":\"10.99.1.254\"", list);
         StringAssert.Contains("\"priority\":42", list);
         string entry = list.Substring(list.IndexOf("\"name\":\"rest-range\"", StringComparison.Ordinal));
         entry = entry.Substring(0, entry.IndexOf('}'));
         StringAssert.Contains("\"allow_smtp\":true", entry);
         StringAssert.Contains("\"allow_imap\":false", entry);
         StringAssert.Contains("\"allow_pop3\":false", entry);
         StringAssert.Contains("\"spam_protection\":false", entry);

         // The same range through COM, so the API and the Control Panel agree.
         var range = _settings.SecurityRanges.get_ItemByName("rest-range");
         Assert.AreEqual("10.99.1.1", range.LowerIP);
         Assert.AreEqual(42, range.Priority);
         Assert.IsFalse(range.AllowIMAPConnections);

         Assert.AreEqual(200, Http("DELETE", "/api/v1/ipranges/" + id).status);
         Assert.AreEqual(404, Http("DELETE", "/api/v1/ipranges/" + id).status);
         StringAssert.DoesNotContain("rest-range", Http("GET", "/api/v1/ipranges").body);

         Assert.AreEqual(400, Http("POST", "/api/v1/ipranges",
            "{\"name\":\"bad\",\"lower\":\"not-an-address\",\"upper\":\"10.0.0.1\"}").status);
         Assert.AreEqual(400, Http("POST", "/api/v1/ipranges", "{\"lower\":\"10.0.0.1\",\"upper\":\"10.0.0.2\"}").status,
            "a range without a name is refused");
      }

      [Test]
      [Description("A distribution list is created with members, listed with them, readable through COM, deleted, and gone.")]
      public void DistributionListsRoundTrip()
      {
         Account member = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "member@example.test", "secret");

         (int status, string body) created = Http("POST", "/api/v1/domains/example.test/lists",
            "{\"address\":\"everyone@example.test\",\"members\":[\"" + member.Address + "\",\"outside@elsewhere.test\"],\"require_auth\":true}");
         Assert.AreEqual(201, created.status, created.body);
         StringAssert.Contains("\"members\":2", created.body);

         (int listStatus, string list) = Http("GET", "/api/v1/domains/example.test/lists");
         Assert.AreEqual(200, listStatus, list);
         StringAssert.Contains("\"address\":\"everyone@example.test\"", list);
         StringAssert.Contains("\"require_auth\":true", list);
         StringAssert.Contains("\"member@example.test\"", list);
         StringAssert.Contains("\"outside@elsewhere.test\"", list);

         DistributionList viaCom = _domain.DistributionLists.get_ItemByAddress("everyone@example.test");
         Assert.AreEqual(2, viaCom.Recipients.Count);

         Assert.AreEqual(409, Http("POST", "/api/v1/domains/example.test/lists", "{\"address\":\"everyone@example.test\"}").status,
            "a second list with the same address is refused");
         Assert.AreEqual(400, Http("POST", "/api/v1/domains/example.test/lists", "{\"address\":\"list@other.test\"}").status,
            "an address outside the domain is refused");
         Assert.AreEqual(404, Http("GET", "/api/v1/domains/nosuch.test/lists").status);

         Assert.AreEqual(200, Http("DELETE", "/api/v1/lists/everyone@example.test").status);
         Assert.AreEqual(404, Http("DELETE", "/api/v1/lists/everyone@example.test").status);
         StringAssert.DoesNotContain("everyone@example.test", Http("GET", "/api/v1/domains/example.test/lists").body);
      }

      [Test]
      [Description("Certificates are listed by name and file, and the private key password never appears.")]
      public void CertificatesAreListedWithoutTheirPassword()
      {
         SSLCertificate certificate = _settings.SSLCertificates.Add();
         certificate.Name = "rest-cert";
         certificate.CertificateFile = "C:\\certs\\rest-cert.pem";
         certificate.PrivateKeyFile = "C:\\certs\\rest-cert.key";
         certificate.PrivateKeyPassword = "hunter2-secret";
         certificate.Save();

         try
         {
            (int status, string body) = Http("GET", "/api/v1/certificates");
            Assert.AreEqual(200, status, body);
            StringAssert.Contains("\"name\":\"rest-cert\"", body);
            StringAssert.Contains("rest-cert.pem", body);
            StringAssert.Contains("rest-cert.key", body);
            StringAssert.DoesNotContain("hunter2", body);
            StringAssert.DoesNotContain("password", body);
         }
         finally
         {
            certificate.Delete();
         }
      }

      [Test]
      [Description("The DKIM configuration of a domain is read back as set over COM; an unknown domain is 404.")]
      public void DkimIsReadBackPerDomain()
      {
         _domain.DKIMSelector = "rest2026";
         _domain.DKIMSignAliasesEnabled = true;
         _domain.Save();

         (int status, string body) = Http("GET", "/api/v1/domains/example.test/dkim");
         Assert.AreEqual(200, status, body);
         StringAssert.Contains("\"domain\":\"example.test\"", body);
         StringAssert.Contains("\"selector\":\"rest2026\"", body);
         StringAssert.Contains("\"sign_aliases\":true", body);
         StringAssert.Contains("\"enabled\":", body);

         Assert.AreEqual(404, Http("GET", "/api/v1/domains/nosuch.test/dkim").status);
      }

      [Test]
      [Description("The global rules are listed with their criteria and actions, named the way an operator would.")]
      public void GlobalRulesAreListedWithCriteriaAndActions()
      {
         Rule rule = _application.Rules.Add();
         rule.Name = "rest-rule";
         rule.Active = true;
         rule.UseAND = true;

         RuleCriteria criterion = rule.Criterias.Add();
         criterion.UsePredefined = true;
         criterion.PredefinedField = eRulePredefinedField.eFTSubject;
         criterion.MatchType = eRuleMatchType.eMTContains;
         criterion.MatchValue = "invoice";
         criterion.Save();

         RuleAction action = rule.Actions.Add();
         action.Type = eRuleActionType.eRAMoveToImapFolder;
         action.IMAPFolder = "Invoices";
         action.Save();

         rule.Save();

         try
         {
            (int status, string body) = Http("GET", "/api/v1/rules");
            Assert.AreEqual(200, status, body);
            StringAssert.Contains("\"name\":\"rest-rule\"", body);
            StringAssert.Contains("\"all_criteria\":true", body);
            StringAssert.Contains("\"field\":\"subject\"", body);
            StringAssert.Contains("\"match\":\"contains\"", body);
            StringAssert.Contains("\"value\":\"invoice\"", body);
            StringAssert.Contains("\"type\":\"move_to_folder\"", body);
            StringAssert.Contains("\"value\":\"Invoices\"", body);
         }
         finally
         {
            rule.Delete();
         }
      }

      [Test]
      [Description("The log files are listed, a file's tail is served line by line, and a name with a path in it is refused.")]
      public void LogsAreListedAndTailed()
      {
         // Make sure something recognisable was written today, through the
         // server's own logger: creating a range over the API writes an
         // application log line naming it, so the marker is the range's name.
         _settings.Logging.Enabled = true;
         _settings.Logging.LogApplication = true;
         string marker = "rest-log-marker-" + Guid.NewGuid().ToString("N");
         (int status, string created) = Http("POST", "/api/v1/ipranges",
            "{\"name\":\"" + marker + "\",\"lower\":\"10.97.0.1\",\"upper\":\"10.97.0.2\"}");
         Assert.AreEqual(201, status, created);
         string id = created.Substring(created.IndexOf(':') + 1).TrimEnd('}');
         Assert.AreEqual(200, Http("DELETE", "/api/v1/ipranges/" + id).status);

         (int listStatus, string list) = Http("GET", "/api/v1/logs");
         Assert.AreEqual(200, listStatus, list);
         StringAssert.Contains("hmailserver_", list);
         StringAssert.Contains("\"size\":", list);

         string name = "hmailserver_" + DateTime.Now.ToString("yyyy-MM-dd") + ".log";
         (int tailStatus, string tail) = Http("GET", "/api/v1/logs/" + name + "?lines=50");
         Assert.AreEqual(200, tailStatus, tail);
         StringAssert.Contains("\"name\":\"" + name + "\"", tail);
         StringAssert.Contains(marker, tail);

         Assert.AreEqual(400, Http("GET", "/api/v1/logs/..%5Chmailserver.ini").status, "a path is not a log name");
         Assert.AreEqual(400, Http("GET", "/api/v1/logs/hMailServer.ini").status, "only .log files are served");
         Assert.AreEqual(404, Http("GET", "/api/v1/logs/hmailserver_1999-01-01.log").status);
      }

      [Test]
      [Description("A backup is started over the API with the configured settings, its status is readable, and it completes.")]
      public void BackupStartsAndReportsItsStatus()
      {
         string directory = Paths.Combine(Path.GetTempPath(), "hm-rest-backup-" + Guid.NewGuid().ToString("N"));
         Directory.CreateDirectory(directory);

         BackupSettings backup = _settings.Backup;
         string previousDestination = backup.Destination;
         bool previousSettings = backup.BackupSettings;
         bool previousDomains = backup.BackupDomains;
         bool previousMessages = backup.BackupMessages;
         backup.Destination = directory;
         backup.BackupSettings = true;
         backup.BackupDomains = false;
         backup.BackupMessages = false;

         try
         {
            (int status, string body) started = Http("POST", "/api/v1/backup");
            Assert.AreEqual(202, started.status, started.body);

            string lastStatus = "";
            bool completed = false;
            for (int attempt = 0; attempt < 150 && !completed; attempt++)
            {
               Thread.Sleep(200);
               (int status, string body) = Http("GET", "/api/v1/backup");
               Assert.AreEqual(200, status, body);
               lastStatus = body;
               // The log lines are the backup log's tail, the same file the
               // backup fixtures read; "Backup completed successfully" is the
               // executer's last line.
               completed = body.Contains("Backup completed successfully");
            }

            Assert.IsTrue(completed, "The backup did not complete. Last status: " + lastStatus);
            Assert.IsTrue(Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories).Any(),
               "The backup completed but wrote nothing under " + directory);
         }
         finally
         {
            backup.Destination = previousDestination;
            backup.BackupSettings = previousSettings;
            backup.BackupDomains = previousDomains;
            backup.BackupMessages = previousMessages;
            try
            {
               Directory.Delete(directory, true);
            }
            catch (IOException)
            {
               // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
            }
         }
      }

      [Test]
      [Description("The settings snapshot reports what COM reports, and no secret.")]
      public void SettingsSnapshotMatchesCom()
      {
         (int status, string body) = Http("GET", "/api/v1/settings");
         Assert.AreEqual(200, status, body);
         StringAssert.Contains("\"host_name\":\"" + _settings.HostName + "\"", body);
         StringAssert.Contains("\"max_message_size_kb\":" + _settings.MaxMessageSize, body);
         StringAssert.Contains("\"max_smtp_connections\":" + _settings.MaxSMTPConnections, body);
         StringAssert.Contains("\"log_smtp_conversations\":", body);
         StringAssert.DoesNotContain("password", body);
      }

      [Test]
      [Description("A domain-restricted key is refused every server-wide surface, sees its own domain's lists and DKIM, and a read-only key cannot create a range.")]
      public void KeysAreHeldToTheirScope()
      {
         string restricted = CreateKey("rest coverage - domain", "full", "example.test");
         string readOnly = CreateKey("rest coverage - read-only", "readonly", null);

         foreach (string path in new[] { "/api/v1/ipranges", "/api/v1/certificates", "/api/v1/rules", "/api/v1/logs", "/api/v1/backup", "/api/v1/settings" })
         {
            (int status, string body) = Http("GET", path, "Bearer " + restricted, null);
            Assert.AreEqual(403, status, "a domain-restricted key must be refused " + path + ". Body: " + body);
         }

         Assert.AreEqual(200, Http("GET", "/api/v1/domains/example.test/lists", "Bearer " + restricted, null).status,
            "a domain-restricted key lists its own domain's lists");
         Assert.AreEqual(200, Http("GET", "/api/v1/domains/example.test/dkim", "Bearer " + restricted, null).status,
            "a domain-restricted key reads its own domain's DKIM configuration");

         (int roStatus, string roBody) = Http("POST", "/api/v1/ipranges", "Bearer " + readOnly,
            "{\"name\":\"ro-range\",\"lower\":\"10.98.0.1\",\"upper\":\"10.98.0.2\"}");
         Assert.AreEqual(403, roStatus, "a read-only key must not create a range. Body: " + roBody);
         StringAssert.DoesNotContain("ro-range", Http("GET", "/api/v1/ipranges").body);
      }

      [Test]
      [Description("The OpenAPI document describes each of the new routes - the test's copy of the contract, extended with the API.")]
      public void OpenApiDocumentDescribesTheNewRoutes()
      {
         (int status, string body) = Http("GET", "/api/v1/openapi.json");
         Assert.AreEqual(200, status, body);
         foreach (string path in new[]
         {
            "/api/v1/ipranges", "/api/v1/ipranges/{id}", "/api/v1/domains/{domain}/lists", "/api/v1/lists/{address}",
            "/api/v1/certificates", "/api/v1/domains/{domain}/dkim", "/api/v1/rules", "/api/v1/logs", "/api/v1/logs/{name}",
            "/api/v1/backup", "/api/v1/settings"
         })
         {
            StringAssert.Contains("\"" + path + "\"", body, "The OpenAPI document must describe " + path);
         }
      }
   }
}
