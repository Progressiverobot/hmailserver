// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.API
{
   /// <summary>
   ///    GET /api/v1/reports: what happened on this server, per domain and per day.
   ///
   ///    The thing that can go wrong with a report is not that it fails - it is
   ///    that it answers, confidently, with the wrong number. So the arithmetic
   ///    here is checked against rows this fixture wrote itself: six trace rows
   ///    whose expected totals are worked out in the test and asserted exactly,
   ///    rather than "some number greater than zero", which would pass for a
   ///    report that double-counted every message.
   ///
   ///    The other half is honesty about what is NOT answerable. The spam and
   ///    virus counts and the size of the message store come from the
   ///    server-wide metric history, which carries no domain at all; the report
   ///    says so, refuses those sections outright to a domain-restricted key,
   ///    and names them in the summary's "omitted" list rather than quietly
   ///    handing that credential a smaller document.
   ///
   ///    And the domain filter, which is the authorisation question this route
   ///    raises: a key issued for one domain must not learn another domain's
   ///    senders, recipients, failures or mailbox sizes by putting a name in a
   ///    query parameter. That is the same identifier-in-the-request shape that
   ///    RestApiAuthorization pins for DELETE /api/v1/accounts, against a route
   ///    that reads rather than destroys - which makes it quieter, not smaller.
   /// </summary>
   [TestFixture]
   public class RestApiReports : TestFixtureBase
   {
      // From the 11440-11449 range reserved for this work. The port the
      // listener answers on: this one on the Windows bench, the suite's own
      // where RestListener finds one already on.
      private static int RestPort = 11440;

      // TestSetup.Authenticate() already expects this to be the administrator
      // password, and the REST API authenticates against the same credential.
      private const string AdminPassword = "testar";

      private const string OtherDomain = "other.example.test";

      // Two whole days, both safely inside the default window and inside the
      // trace's default thirty-day retention, and both far enough from midnight
      // that a run starting at 23:59:59 cannot straddle a day boundary between
      // writing a row and asking for it.
      private static string Day(int daysBack)
      {
         return DateTime.Now.Date.AddDays(-daysBack).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
      }

      private static string Stamp(int daysBack)
      {
         return Day(daysBack) + " 12:00:00";
      }

      private static (int status, string body, string headers) Http(string method, string path, string authorization)
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
               var head = new StringBuilder();
               head.Append(method + " " + path + " HTTP/1.0\r\n");
               head.Append("Host: 127.0.0.1\r\n");
               if (authorization != null)
                  head.Append("Authorization: " + authorization + "\r\n");
               head.Append("Connection: close\r\n\r\n");

               byte[] request = Encoding.ASCII.GetBytes(head.ToString());
               stream.Write(request, 0, request.Length);

               byte[] buffer = new byte[8192];
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
               string headers = separator >= 0 ? raw.Substring(0, separator) : raw;
               string body = separator >= 0 ? raw.Substring(separator + 4) : "";

               return (statusCode, body, headers);
            }
         }
      }

      private static string BasicCredential()
      {
         return "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("Administrator:" + AdminPassword));
      }

      private static (int status, string body, string headers) Basic(string method, string path)
      {
         return Http(method, path, BasicCredential());
      }

      private static (int status, string body, string headers) Bearer(string method, string path, string token)
      {
         return Http(method, path, "Bearer " + token);
      }

      private static string JsonValue(string json, string name)
      {
         Match match = Regex.Match(json, "\"" + name + "\"\\s*:\\s*\"([^\"]*)\"");
         return match.Success ? match.Groups[1].Value : null;
      }

      // Creates a key through the API, which needs the administrator password,
      // and returns its clear-text token. domains empty means every domain.
      private static string CreateKey(string label, string scope, string domains)
      {
         var fields = new List<string> { "\"label\":\"" + label + "\"" };

         if (scope != null)
            fields.Add("\"scope\":\"" + scope + "\"");

         if (domains != null)
            fields.Add("\"domains\":\"" + domains + "\"");

         string body = "{" + string.Join(",", fields) + "}";

         // The key routes take a body, which the small GET-only helper above
         // does not send, so this one request is written out in full.
         using (var client = new TcpClient())
         {
            client.Connect("127.0.0.1", RestPort);
            using (NetworkStream stream = client.GetStream())
            using (var memory = new MemoryStream())
            {
               byte[] payload = Encoding.UTF8.GetBytes(body);
               var head = new StringBuilder();
               head.Append("POST /api/v1/apikeys HTTP/1.0\r\n");
               head.Append("Host: 127.0.0.1\r\n");
               head.Append("Authorization: " + BasicCredential() + "\r\n");
               head.Append("Content-Type: application/json\r\n");
               head.Append("Content-Length: " + payload.Length + "\r\n");
               head.Append("Connection: close\r\n\r\n");

               byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
               stream.Write(headBytes, 0, headBytes.Length);
               stream.Write(payload, 0, payload.Length);

               byte[] buffer = new byte[4096];
               int read;
               while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                  memory.Write(buffer, 0, read);

               string raw = Encoding.UTF8.GetString(memory.ToArray());
               int separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
               string answer = separator >= 0 ? raw.Substring(separator + 4) : raw;

               string token = JsonValue(answer, "key");
               Assert.IsNotNull(token, "The API key create answered no key. Body: " + answer);
               return token;
            }
         }
      }

      private void Trace(string occurred, string eventName, string sender, string recipient, int status)
      {
         _application.Database.ExecuteSQL(
            "insert into hm_messagetrace (mtqueueid, mtoccurred, mtevent, mtsender, mtrecipient, mtsourceip, mtstatuscode, mtdetail) " +
            "values (1, '" + occurred + "', '" + eventName + "', '" + sender + "', '" + recipient + "', '192.0.2.1', " +
            status.ToString(CultureInfo.InvariantCulture) + ", '')");
      }

      // The six rows every arithmetic assertion below is worked out from. Two
      // of them are identical on purpose: the route groups in the database, so
      // a pair that collapses into one grouped row with a count of two is
      // exactly the case where a report that read rows instead of counts would
      // silently lose a message.
      private void WriteTheKnownRows()
      {
         _application.Database.ExecuteSQL("delete from hm_messagetrace");

         Trace(Stamp(2), "delivered", "outside@remote.example", "alice@example.test", 250);
         Trace(Stamp(2), "delivered", "outside@remote.example", "alice@example.test", 250);
         Trace(Stamp(2), "delivered", "alice@example.test", "far@remote.example", 250);
         Trace(Stamp(1), "failed", "outside@remote.example", "nobody@example.test", 550);
         Trace(Stamp(1), "failed", "alice@example.test", "gone@remote.example", 450);
         Trace(Stamp(1), "delivered", "bob@" + OtherDomain, "alice@example.test", 250);
      }

      private static string Window()
      {
         return "from=" + Day(3) + "&to=" + Day(0);
      }

      [SetUp]
      public void StartRestApi()
      {
         _settings.SetAdministratorPassword(AdminPassword);

         RestPort = RestListener.Start(RestPort);

         // Explicitly plaintext: these are ini settings, so a TLS binding left
         // behind by the SSL fixtures would fail every plain request below for
         // a reason that has nothing to do with reports.
         IniFileSetting.Write("RestApiCertificateFile", "");
         IniFileSetting.Write("RestApiPrivateKeyFile", "");

         // The trace is off by default, deliberately: it records who
         // corresponds with whom. A report of it has to be tested with it on.
         ServerIniFile.SetSetting("MessageTraceEnabled", "1");

         _application.Reinitialize();
      }

      [TearDown]
      public void StopRestApi()
      {
         ServerIniFile.SetSetting("MessageTraceEnabled", null);
         _application.Database.ExecuteSQL("delete from hm_messagetrace");
         _application.Reinitialize();
      }

      [Test]
      [Description("The index names every section, what each is counted from, and - as plainly - what this server cannot answer at all")]
      public void TheIndexNamesTheSourcesAndWhatCannotBeAnswered()
      {
         (int status, string body, string headers) index = Basic("GET", "/api/v1/reports");

         Assert.AreEqual(200, index.status, "Body: " + index.body);

         foreach (string section in new[] { "traffic", "failures", "senders", "recipients", "mailboxes", "volume", "storage" })
            StringAssert.Contains("\"name\":\"" + section + "\"", index.body);

         StringAssert.Contains("\"scope\":\"server\"", index.body,
            "The sections counted from the server-wide metrics must be marked as such.");
         StringAssert.Contains("hm_messagetrace", index.body);
         StringAssert.Contains("hm_metricsamples", index.body);

         // The half that keeps the feature honest.
         StringAssert.Contains("not_answerable", index.body);
         StringAssert.Contains("Spam and virus counts per domain", index.body,
            "A report that cannot attribute spam to a domain must say so rather than leave the reader to assume it could.");
         StringAssert.Contains("Storage growth per domain", index.body);
         StringAssert.Contains("\"enabled\":true", index.body,
            "The trace was switched on in SetUp, so the index must say it is recording.");
      }

      [Test]
      [Description("The arithmetic of the traffic, failure, sender and recipient aggregates against six rows whose totals are known")]
      public void TheAggregatesAreCountedCorrectlyFromKnownRows()
      {
         SingletonProvider<TestSetup>.Instance.AddDomain(OtherDomain);
         WriteTheKnownRows();

         (int status, string body, string headers) traffic =
            Basic("GET", "/api/v1/reports/traffic?" + Window() + "&domain=example.test");

         Assert.AreEqual(200, traffic.status, "Body: " + traffic.body);

         // Two days, worked out by hand from the rows above:
         //   the earlier day: two deliveries in, one out, nothing failed;
         //   the later day:   one delivery in (from the other local domain),
         //                    one failure in and one failure out.
         StringAssert.Contains(
            "{\"day\":\"" + Day(2) + "\",\"domain\":\"example.test\",\"incoming\":2,\"outgoing\":1,\"failed_incoming\":0,\"failed_outgoing\":0}",
            traffic.body, "The earlier day is wrong. Body: " + traffic.body);
         StringAssert.Contains(
            "{\"day\":\"" + Day(1) + "\",\"domain\":\"example.test\",\"incoming\":1,\"outgoing\":0,\"failed_incoming\":1,\"failed_outgoing\":1}",
            traffic.body, "The later day is wrong. Body: " + traffic.body);
         StringAssert.Contains("\"totals\":{\"incoming\":3,\"outgoing\":1,\"failed_incoming\":1,\"failed_outgoing\":1}",
            traffic.body, "The totals are wrong. Body: " + traffic.body);

         Assert.IsFalse(traffic.body.Contains("remote.example"),
            "Only domains this server hosts may appear in a per-domain report: the other end of a conversation is not this server's domain. Body: " + traffic.body);

         // The same rows read as failures: one permanent refusal inbound, one
         // temporary failure outbound, each with its reason in words.
         (int status, string body, string headers) failures =
            Basic("GET", "/api/v1/reports/failures?" + Window() + "&domain=example.test");

         Assert.AreEqual(200, failures.status, "Body: " + failures.body);
         StringAssert.Contains("\"status\":550", failures.body);
         StringAssert.Contains("the mailbox was unavailable", failures.body);
         StringAssert.Contains("\"status\":450", failures.body);
         StringAssert.Contains("\"incoming\":1,\"outgoing\":0,\"total\":1", failures.body);
         StringAssert.Contains("\"incoming\":0,\"outgoing\":1,\"total\":1", failures.body);

         // Senders: only the local one, with its one delivery and its one failure.
         (int status, string body, string headers) senders =
            Basic("GET", "/api/v1/reports/senders?" + Window() + "&domain=example.test");

         Assert.AreEqual(200, senders.status, "Body: " + senders.body);
         StringAssert.Contains("{\"sender\":\"alice@example.test\",\"domain\":\"example.test\",\"messages\":1,\"failed\":1}",
            senders.body, "Body: " + senders.body);
         Assert.IsFalse(senders.body.Contains("outside@remote.example"),
            "A remote sender is not a sender in one of this server's domains. Body: " + senders.body);

         // Recipients: three deliveries to one mailbox, and the refused one
         // separately - a recipient that never received anything is still a
         // recipient somebody tried to reach.
         (int status, string body, string headers) recipients =
            Basic("GET", "/api/v1/reports/recipients?" + Window() + "&domain=example.test");

         Assert.AreEqual(200, recipients.status, "Body: " + recipients.body);
         StringAssert.Contains("{\"recipient\":\"alice@example.test\",\"domain\":\"example.test\",\"messages\":3,\"failed\":0}",
            recipients.body, "Body: " + recipients.body);
         StringAssert.Contains("{\"recipient\":\"nobody@example.test\",\"domain\":\"example.test\",\"messages\":0,\"failed\":1}",
            recipients.body, "Body: " + recipients.body);

         // With no domain named, both local domains are counted, and the one
         // delivery between them is this domain's incoming and the other's
         // outgoing - which is what the server did with it.
         (int status, string body, string headers) all = Basic("GET", "/api/v1/reports/traffic?" + Window());

         Assert.AreEqual(200, all.status, "Body: " + all.body);
         StringAssert.Contains("{\"day\":\"" + Day(1) + "\",\"domain\":\"" + OtherDomain + "\",\"incoming\":0,\"outgoing\":1,\"failed_incoming\":0,\"failed_outgoing\":0}",
            all.body, "Body: " + all.body);
      }

      [Test]
      [Description("A window outside the rows is empty rather than wrong, and a day is a whole day at both ends")]
      public void TheWindowIsHalfOpenAndInclusiveOfItsLastDay()
      {
         WriteTheKnownRows();

         (int status, string body, string headers) outside =
            Basic("GET", "/api/v1/reports/traffic?from=" + Day(9) + "&to=" + Day(5));

         Assert.AreEqual(200, outside.status, "Body: " + outside.body);
         StringAssert.Contains("\"rows\":[]", outside.body,
            "A window that holds no rows is an empty report, not an error. Body: " + outside.body);
         StringAssert.Contains("\"totals\":{\"incoming\":0,\"outgoing\":0,\"failed_incoming\":0,\"failed_outgoing\":0}", outside.body);

         // The last day of the window is whole: rows stamped at noon on it are
         // inside. A half-open range that ended at 00:00:00 of the last day
         // would silently drop the most recent day, which is the day an
         // administrator is most often asking about.
         (int status, string body, string headers) lastDay =
            Basic("GET", "/api/v1/reports/traffic?from=" + Day(1) + "&to=" + Day(1) + "&domain=example.test");

         Assert.AreEqual(200, lastDay.status, "Body: " + lastDay.body);
         StringAssert.Contains("\"totals\":{\"incoming\":1,\"outgoing\":0,\"failed_incoming\":1,\"failed_outgoing\":1}",
            lastDay.body, "A one-day window must hold that whole day. Body: " + lastDay.body);
      }

      [Test]
      [Description("The mailbox section counts what is in the store now, for the mailbox it names")]
      public void TheMailboxSectionCountsWhatIsInTheStore()
      {
         Account account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "sized@example.test", "test");

         for (int i = 0; i < 3; i++)
            SmtpClientSimulator.StaticSend("outsider@remote.example", account.Address, "report " + i, "body");

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 3);

         (int status, string body, string headers) mailboxes =
            Basic("GET", "/api/v1/reports/mailboxes?" + Window() + "&domain=example.test&top=50");

         Assert.AreEqual(200, mailboxes.status, "Body: " + mailboxes.body);

         Match row = Regex.Match(mailboxes.body,
            "\\{\"address\":\"sized@example\\.test\",\"domain\":\"example\\.test\",\"messages\":(\\d+),\"bytes\":(\\d+),");

         Assert.IsTrue(row.Success, "The mailbox is not in the report. Body: " + mailboxes.body);
         Assert.AreEqual("3", row.Groups[1].Value, "Three messages were delivered. Body: " + mailboxes.body);
         ClassicAssert.Greater(int.Parse(row.Groups[2].Value, CultureInfo.InvariantCulture), 0,
            "Three delivered messages occupy more than nothing. Body: " + mailboxes.body);

         StringAssert.Contains("\"domains\":[", mailboxes.body,
            "The section carries the per-domain roll-up as well as the mailboxes.");
         StringAssert.Contains("AS IT IS NOW", mailboxes.body,
            "The note must say that this is a size now and not a history, because the section ignores the window.");
      }

      [Test]
      [Description("A key restricted to one domain sees its own domain, is refused another by name, and is refused the server-wide sections outright")]
      public void ADomainRestrictedKeyIsConfinedToItsDomain()
      {
         SingletonProvider<TestSetup>.Instance.AddDomain(OtherDomain);
         WriteTheKnownRows();

         string token = CreateKey("regression - reports, one domain", "full", "example.test");

         // Its own domain, by name: allowed, and the numbers are the ones above.
         (int status, string body, string headers) mine =
            Bearer("GET", "/api/v1/reports/traffic?" + Window() + "&domain=example.test", token);

         Assert.AreEqual(200, mine.status, "Body: " + mine.body);
         StringAssert.Contains("\"totals\":{\"incoming\":3,\"outgoing\":1", mine.body);

         // Another domain, by name: refused, and told why.
         (int status, string body, string headers) theirs =
            Bearer("GET", "/api/v1/reports/traffic?" + Window() + "&domain=" + OtherDomain, token);

         Assert.AreEqual(403, theirs.status,
            "A key restricted to one domain must not report on another by naming it in a query parameter. Body: " + theirs.body);
         StringAssert.Contains("not permitted for that domain", theirs.body);

         // No domain named: confined, not refused - a listing that answered 403
         // would be useless to exactly the credential the restriction exists for.
         (int status, string body, string headers) unnamed =
            Bearer("GET", "/api/v1/reports/traffic?" + Window(), token);

         Assert.AreEqual(200, unnamed.status, "Body: " + unnamed.body);
         StringAssert.Contains("example.test", unnamed.body);
         Assert.IsFalse(unnamed.body.Contains(OtherDomain),
            "A domain-restricted key must not be handed another domain's traffic in an unfiltered listing. Body: " + unnamed.body);

         // The server-wide sections: refused outright, as the delivery queue is,
         // because these counters carry no domain and narrowing them would mean
         // inventing an attribution.
         foreach (string section in new[] { "volume", "storage" })
         {
            (int status, string body, string headers) wide =
               Bearer("GET", "/api/v1/reports/" + section + "?" + Window(), token);

            Assert.AreEqual(403, wide.status,
               "The " + section + " section is counted from server-wide metrics and must be refused to a domain-restricted key. Body: " + wide.body);
            StringAssert.Contains("server-wide", wide.body);
         }

         // And the summary tells it which sections it did not get, rather than
         // handing it a quietly smaller document.
         (int status, string body, string headers) summary =
            Bearer("GET", "/api/v1/reports/summary?" + Window(), token);

         Assert.AreEqual(200, summary.status, "Body: " + summary.body);
         StringAssert.Contains("\"omitted\":[", summary.body);
         StringAssert.Contains("{\"section\":\"volume\",\"reason\":", summary.body);
         StringAssert.Contains("{\"section\":\"storage\",\"reason\":", summary.body);
         StringAssert.Contains("\"traffic\":{", summary.body,
            "The sections it may see must still be there.");

         // A read-only key is enough for every report: nothing here changes
         // anything, and a monitoring credential should not need full authority.
         string readOnly = CreateKey("regression - reports, read only", null, null);

         Assert.AreEqual(200, Bearer("GET", "/api/v1/reports/summary?" + Window(), readOnly).status,
            "A read-only key must be able to read a report.");
      }

      [Test]
      [Description("format=csv answers text/csv with the same columns and rows as the JSON, and a section is offered as a file")]
      public void TheSameTableIsAnsweredAsCsv()
      {
         WriteTheKnownRows();

         (int status, string body, string headers) csv =
            Basic("GET", "/api/v1/reports/traffic?" + Window() + "&domain=example.test&format=csv");

         Assert.AreEqual(200, csv.status, "Body: " + csv.body);
         StringAssert.Contains("text/csv", csv.headers, "Headers: " + csv.headers);
         StringAssert.Contains("Content-Disposition: attachment", csv.headers, "Headers: " + csv.headers);
         StringAssert.Contains("nosniff", csv.headers, "Headers: " + csv.headers);

         string[] lines = csv.body.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);

         Assert.AreEqual("day,domain,incoming,outgoing,failed_incoming,failed_outgoing", lines[0],
            "The CSV header is the JSON's columns, in the same order. Body: " + csv.body);
         CollectionAssert.Contains(lines, Day(2) + ",example.test,2,1,0,0");
         CollectionAssert.Contains(lines, Day(1) + ",example.test,1,0,1,1");

         // The summary is several tables, so it is not a CSV, and says so
         // rather than writing one table and pretending it was all of them.
         (int status, string body, string headers) refused =
            Basic("GET", "/api/v1/reports/summary?" + Window() + "&format=csv");

         Assert.AreEqual(400, refused.status, "Body: " + refused.body);
         StringAssert.Contains("not one table", refused.body);
      }

      [Test]
      [Description("A date, a window, a row count, a format, a section or a domain the route does not take is refused by name rather than guessed at")]
      public void WhatTheRouteWillNotTakeIsRefusedByName()
      {
         Assert.AreEqual(400, Basic("GET", "/api/v1/reports/traffic?from=yesterday").status,
            "A date that is not a date must be refused.");
         Assert.AreEqual(400, Basic("GET", "/api/v1/reports/traffic?from=2026-02-31&to=2026-03-01").status,
            "A day that does not exist must be refused, not rounded into the next month.");
         Assert.AreEqual(400, Basic("GET", "/api/v1/reports/traffic?from=2026-09-15&to=2026-09-01").status,
            "A window that ends before it starts must be refused.");
         Assert.AreEqual(400, Basic("GET", "/api/v1/reports/traffic?from=2020-01-01&to=2026-01-01").status,
            "A window of years must be refused rather than answered slowly.");
         Assert.AreEqual(400, Basic("GET", "/api/v1/reports/senders?top=0").status,
            "A row count outside the range must be refused.");
         Assert.AreEqual(400, Basic("GET", "/api/v1/reports/senders?top=5000").status,
            "A row count outside the range must be refused.");
         Assert.AreEqual(400, Basic("GET", "/api/v1/reports/traffic?format=xml").status,
            "A format the route does not write must be refused.");

         (int status, string body, string headers) section = Basic("GET", "/api/v1/reports/nonsense");
         Assert.AreEqual(404, section.status, "Body: " + section.body);
         StringAssert.Contains("\"sections\":[", section.body,
            "A refusal should say what the sections are.");

         (int status, string body, string headers) domain =
            Basic("GET", "/api/v1/reports/traffic?domain=not-hosted.example");
         Assert.AreEqual(404, domain.status, "Body: " + domain.body);
         StringAssert.Contains("no such domain", domain.body);
      }

      [Test]
      [Description("The OpenAPI document describes the route, its parameters and what it will not answer")]
      public void TheOpenApiDocumentDescribesTheRoute()
      {
         (int status, string body, string headers) spec = Basic("GET", "/api/v1/openapi.json");

         Assert.AreEqual(200, spec.status);
         StringAssert.Contains("\"/api/v1/reports\":", spec.body);
         StringAssert.Contains("\"/api/v1/reports/{section}\":", spec.body);
         StringAssert.Contains("MessageTraceEnabled", spec.body,
            "The document must say that the trace-based sections are off by default.");
         StringAssert.Contains("refused for a domain-restricted key", spec.body);
      }

      [Test]
      [Description("The store-size gauges the storage section reads are sampled: the sampler writes them and the metric history serves them")]
      public void TheStoreSizeIsSampledIntoTheMetricHistory()
      {
         int written = _application.Utilities.SampleMetricsNow();

         ClassicAssert.GreaterOrEqual(written, 16,
            "The sampler records the fourteen ServerStatus metrics and the two store gauges.");

         foreach (string metric in new[] { "store_bytes", "store_messages" })
         {
            string history = _application.Utilities.GetMetricHistory(metric, 60, 1);

            StringAssert.Contains("\"metric\":\"" + metric + "\"", history);
            StringAssert.Contains("\"known\":true", history,
               metric + " must be a metric this server records, or the storage report has nothing to read.");
            StringAssert.Contains("\"samples\":[{", history);
         }

         (int status, string body, string headers) storage = Basic("GET", "/api/v1/reports/storage?" + Window());

         Assert.AreEqual(200, storage.status, "Body: " + storage.body);
         StringAssert.Contains("\"growth_bytes\":", storage.body);
         StringAssert.Contains("store_bytes", storage.body,
            "The note must name the samples the section is built from.");
         StringAssert.Contains("\"day\":\"" + Day(0) + "\"", storage.body,
            "Today has just been sampled, so today must be in the series. Body: " + storage.body);
      }
   }
}
