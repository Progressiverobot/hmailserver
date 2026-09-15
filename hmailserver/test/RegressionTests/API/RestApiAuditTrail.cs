// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.API
{
   /// <summary>
   ///    The audit trail: who changed what, when, from where, and over which
   ///    interface, in an append-only hash-chained table that GET /api/v1/audit
   ///    reads and GET /api/v1/audit/verify walks.
   ///
   ///    What these tests are really holding is the claim the feature makes. A
   ///    record that covers COM but not REST is a record with a way round it, so
   ///    the same change is made over both and each row has to name the interface
   ///    it came over. A record that copies a password into a table the Deck
   ///    renders is worse than none, so a secret has to arrive as "(changed)".
   ///    And a chain nobody can break is not a chain that has been tested: two
   ///    tests here edit and delete a row through Database.ExecuteSQL, which is
   ///    exactly what an intruder with database access would do, and require
   ///    Verify to name the row.
   /// </summary>
   [TestFixture]
   public class RestApiAuditTrail : TestFixtureBase
   {
      private static int RestPort = 9129;
      private const string AdminPassword = "testar";
      private const string AuditPath = "/api/v1/audit";

      [SetUp]
      public void StartRestApi()
      {
         _settings.SetAdministratorPassword(AdminPassword);

         RestPort = RestListener.Start(RestPort);

         _application.Reinitialize();

         (int status, string body) probe = Http("GET", "/api/v1/status");
         Assert.AreEqual(200, probe.status, "REST API did not answer /api/v1/status. Body: " + probe.body);

         // Every fixture before this one has made administrative changes of its
         // own, and this one asserts on counts. Emptying the table is safe: the
         // chain head is read from the table on every write, never cached, so a
         // trail that starts again starts anchored at the empty hash.
         _application.Database.ExecuteSQL("delete from hm_audit");
      }

      [TearDown]
      public void StopRestApi()
      {
         RestListener.Stop();
         _application.Reinitialize();
      }

      [Test]
      [Description("A domain created over COM and a domain created over REST both appear, each naming the interface it was made over and the address it came from.")]
      public void AChangeOverComAndOverRestBothLandWithTheirInterface()
      {
         Domain overCom = _application.Domains.Add();
         overCom.Name = "auditcom.example.com";
         overCom.Active = true;
         overCom.Save();

         (int status, string body) created = Http("POST", "/api/v1/domains", "{\"name\":\"auditrest.example.com\"}");
         Assert.AreEqual(201, created.status, created.body);

         string trail = Read(AuditPath + "?object_type=domain&limit=50");

         string comRow = RowFor(trail, "auditcom.example.com");
         string restRow = RowFor(trail, "auditrest.example.com");

         StringAssert.Contains("\"interface\":\"COM\"", comRow, "The Control Panel and every script reach the server over COM; a row that does not say so cannot be told from one made through the API.");
         StringAssert.Contains("\"actor\":\"Administrator\"", comRow);
         StringAssert.Contains("\"action\":\"created\"", comRow);

         StringAssert.Contains("\"interface\":\"REST\"", restRow);
         StringAssert.Contains("\"actor\":\"administrator\"", restRow, "The REST layer names the credential the way its own log line does.");
         StringAssert.Contains("\"address\":\"127.0.0.1\"", restRow, "A change made over the network records where it came from.");
         StringAssert.Contains("\"action\":\"created\"", restRow);

         _application.Domains.DeleteByDBID(overCom.ID);
      }

      [Test]
      [Description("A setting changed over COM is recorded once, with the value it had and the value it was given.")]
      public void ASettingRecordsTheValueItHadAndTheValueItWasGiven()
      {
         int before = _settings.AutoBanMinutes;
         int after = before == 71 ? 72 : 71;

         _settings.AutoBanMinutes = after;

         try
         {
            string trail = Read(AuditPath + "?object_type=setting&limit=50");
            string row = RowFor(trail, "AutoBanMinutes");

            StringAssert.Contains("\"action\":\"updated\"", row);
            StringAssert.Contains("\"interface\":\"COM\"", row);
            StringAssert.Contains(before + " -> " + after, row,
               "A change-control process asks for the value before as well as the value after.");

            // Written once, not once per field of the settings page.
            Assert.AreEqual(1, Occurrences(trail, "AutoBanMinutes: " + before + " -> " + after),
               "One change is one row.");
         }
         finally
         {
            _settings.AutoBanMinutes = before;
         }
      }

      [Test]
      [Description("A password set over COM is recorded as changed and never as a value.")]
      public void ASecretIsRecordedAsChangedAndNeverAsAValue()
      {
         const string Secret = "NotInTheTrail-9f3c1a";

         Account account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "audited@" + _domain.Name, "test");
         account.Password = Secret;
         account.Save();

         string trail = Read(AuditPath + "?limit=100");

         StringAssert.DoesNotContain(Secret, trail,
            "An audit trail that copies every password an administrator sets into a table the Deck renders is worse than no audit trail.");
         StringAssert.Contains("(changed)", trail, "The fact of the change is recorded; the value never is.");
      }

      [Test]
      [Description("Every row carries the hash of the row before it, the first is anchored at the empty hash, and verify walks the whole chain.")]
      public void TheChainIsWrittenAndVerifies()
      {
         MakeThreeChanges();

         string trail = Read(AuditPath + "?limit=100");
         List<string> hashes = Values(trail, "hash");
         List<string> previous = Values(trail, "previous_hash");

         Assert.GreaterOrEqual(hashes.Count, 3, "The three changes above are recorded. Body: " + trail);

         // Newest first, so row n points back at row n+1.
         for (int i = 0; i + 1 < hashes.Count; i++)
         {
            Assert.AreEqual(64, hashes[i].Length, "A SHA-256 in hex is 64 characters: " + hashes[i]);
            Assert.AreEqual(hashes[i + 1], previous[i], "Row " + i + " does not carry the hash of the row before it.");
         }

         Assert.AreEqual(string.Empty, previous[previous.Count - 1], "The oldest row is anchored at the empty hash.");

         (int status, string body) verified = Http("GET", AuditPath + "/verify");
         Assert.AreEqual(200, verified.status, verified.body);
         StringAssert.Contains("\"intact\":true", verified.body, verified.body);
         StringAssert.Contains("\"first_broken_id\":0", verified.body);
      }

      [Test]
      [Description("A row edited in the database is found by verify, which names it - which is the whole reason the rows are chained.")]
      public void AnEditedRowIsFoundAndNamed()
      {
         MakeThreeChanges();

         string trail = Read(AuditPath + "?limit=100");
         List<string> ids = Values(trail, "id");
         Assert.GreaterOrEqual(ids.Count, 3, trail);

         // The middle one: an intruder covering their tracks edits the row that
         // says what they did, not the newest row in the table.
         string target = ids[1];
         _application.Database.ExecuteSQL(
            "update hm_audit set auditobjectname = 'covered.up' where auditid = " + target);

         (int status, string body) verified = Http("GET", AuditPath + "/verify");
         Assert.AreEqual(200, verified.status, verified.body);
         StringAssert.Contains("\"intact\":false", verified.body, verified.body);
         StringAssert.Contains("\"first_broken_id\":" + target, verified.body, verified.body);
         StringAssert.Contains("edited", verified.body);
      }

      [Test]
      [Description("A row deleted from the middle of the trail is found by verify, at the row that pointed at it.")]
      public void ADeletedRowIsFoundAtTheRowThatPointedAtIt()
      {
         MakeThreeChanges();

         string trail = Read(AuditPath + "?limit=100");
         List<string> ids = Values(trail, "id");
         Assert.GreaterOrEqual(ids.Count, 3, trail);

         string removed = ids[1];
         string pointingAtIt = ids[0];

         _application.Database.ExecuteSQL("delete from hm_audit where auditid = " + removed);

         (int status, string body) verified = Http("GET", AuditPath + "/verify");
         Assert.AreEqual(200, verified.status, verified.body);
         StringAssert.Contains("\"intact\":false", verified.body, verified.body);
         StringAssert.Contains("\"first_broken_id\":" + pointingAtIt, verified.body, verified.body);
         StringAssert.Contains("removed", verified.body);
      }

      [Test]
      [Description("The trail pages and filters: a limit takes a page, an offset takes the next one, and a filter on who or what narrows it.")]
      public void TheTrailPagesAndFilters()
      {
         MakeThreeChanges();

         (int status, string body) firstPage = Http("GET", AuditPath + "?limit=1&offset=0");
         Assert.AreEqual(200, firstPage.status, firstPage.body);
         Assert.AreEqual(1, Values(firstPage.body, "id").Count, firstPage.body);
         StringAssert.Contains("\"offset\":0", firstPage.body);

         (int status, string body) secondPage = Http("GET", AuditPath + "?limit=1&offset=1");
         Assert.AreEqual(200, secondPage.status, secondPage.body);
         Assert.AreNotEqual(Values(firstPage.body, "id")[0], Values(secondPage.body, "id")[0],
            "The second page is a different row.");

         (int status, string body) filtered = Http("GET", AuditPath + "?object_type=setting");
         Assert.AreEqual(200, filtered.status, filtered.body);
         StringAssert.DoesNotContain("\"object_type\":\"domain\"", filtered.body,
            "A filter on the object type answers only that type.");

         (int status, string body) byActor = Http("GET", AuditPath + "?actor=Administrator");
         Assert.AreEqual(200, byActor.status, byActor.body);
         Assert.Greater(Values(byActor.body, "id").Count, 0, byActor.body);

         (int status, string body) nobody = Http("GET", AuditPath + "?actor=nobody-by-that-name");
         Assert.AreEqual(200, nobody.status, nobody.body);
         StringAssert.Contains("\"total\":0", nobody.body, nobody.body);
      }

      [Test]
      [Description("An API key of any scope is refused the audit trail, and refused with a 401 so that it learns nothing from asking.")]
      public void TheTrailIsTheAdministratorsAndNotAKeys()
      {
         (string id, string key) unrestricted = CreateKey("audit-full", "full", null);
         (string id, string key) restricted = CreateKey("audit-domain", "full", _domain.Name);

         Assert.AreEqual(401, Bearer("GET", AuditPath, unrestricted.key).status,
            "A key that could read who changed what could watch for its own footprints.");
         Assert.AreEqual(401, Bearer("GET", AuditPath + "/verify", restricted.key).status);
         Assert.AreEqual(401, Bearer("GET", "/api/v1/alerts/rules", unrestricted.key).status,
            "The alert rules hold the webhook secrets.");

         Http("DELETE", "/api/v1/apikeys/" + unrestricted.id);
         Http("DELETE", "/api/v1/apikeys/" + restricted.id);
      }

      [Test]
      [Description("Recording can be switched off, and then nothing is recorded - which is a setting whose own change is the last thing in the trail.")]
      public void RecordingCanBeSwitchedOff()
      {
         (int status, string body) off = Http("PUT", "/api/v1/settings", "{\"audit_trail_enabled\":false}");
         Assert.AreEqual(200, off.status, off.body);

         try
         {
            StringAssert.Contains("AuditTrailEnabled: true -> false", Read(AuditPath + "?limit=200"),
               "Switching the recorder off is the one change it records while off - otherwise the way to change something unobserved is to turn the observer off first.");

            int before = Values(Read(AuditPath + "?limit=200"), "id").Count;

            Domain quiet = _application.Domains.Add();
            quiet.Name = "unrecorded.example.com";
            quiet.Active = true;
            quiet.Save();
            _application.Domains.DeleteByDBID(quiet.ID);

            Assert.AreEqual(before, Values(Read(AuditPath + "?limit=200"), "id").Count,
               "With recording off, nothing is written.");
         }
         finally
         {
            (int status, string body) on = Http("PUT", "/api/v1/settings", "{\"audit_trail_enabled\":true}");
            Assert.AreEqual(200, on.status, on.body);
         }
      }

      // ---- helpers -----------------------------------------------------------

      // Three changes of three different shapes, so that a chain test is not
      // testing one code path three times.
      private void MakeThreeChanges()
      {
         Domain domain = _application.Domains.Add();
         domain.Name = "chain.example.com";
         domain.Active = true;
         domain.Save();

         int before = _settings.AutoBanMinutes;
         _settings.AutoBanMinutes = before == 61 ? 62 : 61;
         _settings.AutoBanMinutes = before;

         _application.Domains.DeleteByDBID(domain.ID);
      }

      private static string Read(string path)
      {
         (int status, string body) answer = Http("GET", path);
         Assert.AreEqual(200, answer.status, answer.body);
         return answer.body;
      }

      // The JSON object within the trail that names the given text.
      private static string RowFor(string trail, string needle)
      {
         foreach (string entry in trail.Split(new[] { "},{" }, StringSplitOptions.None))
         {
            if (entry.Contains(needle))
               return entry;
         }

         Assert.Fail("No audit row naming '" + needle + "' in: " + trail);
         return null;
      }

      private static List<string> Values(string json, string key)
      {
         var values = new List<string>();
         foreach (Match match in Regex.Matches(json, "\"" + key + "\"\\s*:\\s*\"?([^\",}]*)\"?"))
            values.Add(match.Groups[1].Value);

         return values;
      }

      private static int Occurrences(string text, string needle)
      {
         int count = 0;
         int at = 0;
         while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
         {
            count++;
            at += needle.Length;
         }

         return count;
      }

      private static (string id, string key) CreateKey(string label, string scope, string domains)
      {
         string body = "{\"label\":\"" + label + "\",\"scope\":\"" + scope + "\"" +
                       (domains == null ? "" : ",\"domains\":\"" + domains + "\"") + "}";
         (int status, string created) = Http("POST", "/api/v1/apikeys", body);
         Assert.AreEqual(201, status, "POST /api/v1/apikeys must create a key. Body: " + created);
         return (Extract(created, "id"), Extract(created, "key"));
      }

      private static string Extract(string json, string key)
      {
         Match match = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"?([^\",}]*)\"?");
         Assert.IsTrue(match.Success, "No '" + key + "' in: " + json);
         return match.Groups[1].Value;
      }

      private static (int status, string body) Http(string method, string path, string requestBody = null)
      {
         string credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes("Administrator:" + AdminPassword));
         return RestRequest.Send("127.0.0.1", RestPort, method, path, "Basic " + credentials, requestBody);
      }

      private static (int status, string body) Bearer(string method, string path, string token, string requestBody = null)
      {
         return RestRequest.Send("127.0.0.1", RestPort, method, path, "Bearer " + token, requestBody);
      }
   }
}
