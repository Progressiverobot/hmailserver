// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
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
   ///    Alerts: a condition becomes true, and somebody is told - by mail, by a
   ///    signed webhook, or in one daily digest rather than one message per event.
   ///
   ///    The tests that matter here are the ones about NOT sending. An alerting
   ///    system that mails every minute is switched off by its administrator on
   ///    the first day, and then nothing tells them anything: so a condition that
   ///    stays true fires once, a condition inside its cool-down waits for the
   ///    digest, and the digest itself says nothing when nothing happened. The
   ///    webhook half is held to the recipe the documentation states - HMAC-SHA256
   ///    over "timestamp.body" - by recomputing it here from the two headers, and
   ///    to its promise about failure by pointing a rule at a port nothing is
   ///    listening on and requiring the event to be dead-lettered rather than
   ///    retried for ever.
   /// </summary>
   [TestFixture]
   public class RestApiAlerts : TestFixtureBase
   {
      private static int RestPort = 9130;
      private const string AdminPassword = "testar";
      private const string RulesPath = "/api/v1/alerts/rules";
      private const string EventsPath = "/api/v1/alerts/events";
      private const string Secret = "a-shared-secret-nobody-else-has";

      [SetUp]
      public void StartRestApi()
      {
         _settings.SetAdministratorPassword(AdminPassword);

         RestPort = RestListener.Start(RestPort);

         _application.Reinitialize();

         (int status, string body) probe = Http("GET", "/api/v1/status");
         Assert.AreEqual(200, probe.status, "REST API did not answer /api/v1/status. Body: " + probe.body);

         _application.Database.ExecuteSQL("delete from hm_alertevents");
      }

      [TearDown]
      public void StopRestApi()
      {
         // Back to the shipped rules, so the fixture after this one does not
         // inherit a condition that mails somebody.
         Http("PUT", "/api/v1/settings",
            "{\"alerts_enabled\":true,\"alert_recipient\":\"\",\"alert_sender_address\":\"\"," +
            "\"alert_digest_enabled\":true,\"alert_digest_hour\":7,\"alert_max_per_hour\":20,\"alert_webhook_max_attempts\":5}");

         foreach (string condition in new[] { "test.state", "test.webhook", "test.deadletter", "test.digest", "test.ceiling" })
            Http("PUT", RulesPath + "/" + condition, "{\"enabled\":false,\"webhook\":\"\",\"webhook_secret\":\"\",\"actions\":1}");

         RestListener.Stop();
         _application.Reinitialize();
      }

      [Test]
      [Description("The six conditions the roadmap names ship as rules, and only the two always worth knowing about are on.")]
      public void TheSixConditionsShipAndOnlyTwoAreOn()
      {
         string rules = Read(RulesPath);

         foreach (string condition in new[] { "backup.failed", "certificate.expiring", "disk.low",
                                              "queue.stalled", "autoban.storm", "minidump.written" })
            StringAssert.Contains("\"condition\":\"" + condition + "\"", rules, rules);

         Assert.IsTrue(Enabled(rules, "backup.failed"), "A backup that failed is not noticed until the day it is needed, which is the worst day to find out.");
         Assert.IsTrue(Enabled(rules, "certificate.expiring"), "A certificate a week from expiry is always worth knowing about.");

         foreach (string quiet in new[] { "disk.low", "queue.stalled", "autoban.storm", "minidump.written" })
            Assert.IsFalse(Enabled(rules, quiet), quiet + " ships off, so a stock install mails nobody about it.");
      }

      [Test]
      [Description("A condition nothing has shipped is created by writing its rule, which is how the set grows without a schema change.")]
      public void AConditionIsAddedByWritingItsRule()
      {
         (int status, string body) created = Http("PUT", RulesPath + "/test.state",
            "{\"enabled\":true,\"digest\":false,\"cooldown_minutes\":0,\"actions\":1}");
         Assert.AreEqual(201, created.status, created.body);
         StringAssert.Contains("\"condition\":\"test.state\"", created.body);

         (int status, string body) again = Http("PUT", RulesPath + "/test.state", "{\"threshold\":5}");
         Assert.AreEqual(200, again.status, again.body);
         StringAssert.Contains("\"threshold\":5", again.body);
         StringAssert.Contains("\"enabled\":true", again.body, "A field the body leaves out keeps the value it has.");
      }

      [Test]
      [Description("A condition that stays true fires once: raised twice, one event, and it clears once.")]
      public void AConditionThatStaysTrueFiresOnce()
      {
         Http("PUT", RulesPath + "/test.state", "{\"enabled\":true,\"digest\":false,\"cooldown_minutes\":0,\"actions\":1}");

         Assert.AreEqual("true", Extract(Raise("test.state", false), "raised"));
         Assert.AreEqual("false", Extract(Raise("test.state", false), "raised"),
            "The disk is not low once a minute for an hour; it became low once.");
         Assert.AreEqual("false", Extract(Raise("test.state", false), "raised"));

         string events = Read(EventsPath + "?limit=50");
         Assert.AreEqual(1, Count(events, "\"condition\":\"test.state\""), events);
      }

      [Test]
      [Description("An occurrence - a backup that failed, a minidump - is recorded every time, because there is no such thing as one that is still happening.")]
      public void AnOccurrenceIsRecordedEveryTime()
      {
         Http("PUT", RulesPath + "/test.state", "{\"enabled\":true,\"digest\":true,\"cooldown_minutes\":0,\"actions\":1}");

         Assert.AreEqual("true", Extract(Raise("test.state", true), "raised"));
         Assert.AreEqual("true", Extract(Raise("test.state", true), "raised"));

         string events = Read(EventsPath + "?limit=50");
         Assert.AreEqual(2, Count(events, "\"condition\":\"test.state\""), events);
      }

      [Test]
      [Description("A rule that is off raises nothing, and so does a condition with no rule at all.")]
      public void ARuleThatIsOffRaisesNothing()
      {
         Http("PUT", RulesPath + "/test.state", "{\"enabled\":false,\"actions\":1}");

         Assert.AreEqual("false", Extract(Raise("test.state", false), "raised"));
         Assert.AreEqual("false", Extract(Raise("no.rule.for.this", false), "raised"));

         Assert.AreEqual(0, Count(Read(EventsPath), "\"condition\":\"test.state\""));
      }

      [Test]
      [Description("The digest is the quiet default: a condition marked for it is held rather than sent, and one message at the digest hour carries everything that fired.")]
      public void TheDigestCarriesWhatFired()
      {
         Account recipient = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "alerts@" + _domain.Name, "test");
         Account sender = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "alerter@" + _domain.Name, "test");

         (int status, string body) configured = Http("PUT", "/api/v1/settings",
            "{\"alert_recipient\":\"" + recipient.Address + "\",\"alert_sender_address\":\"" + sender.Address + "\"," +
            "\"alert_digest_enabled\":true,\"alert_digest_hour\":" + DateTime.UtcNow.Hour + "}");
         Assert.AreEqual(200, configured.status, configured.body);

         Http("PUT", RulesPath + "/test.digest", "{\"enabled\":true,\"digest\":true,\"cooldown_minutes\":0,\"actions\":1}");

         Assert.AreEqual("true", Extract(Raise("test.digest", true, "The disk that was nearly full."), "raised"));

         string held = Read(EventsPath + "?limit=50");
         StringAssert.Contains("\"held_for_digest\":true", held, held);
         StringAssert.Contains("\"notified\":false", held, held);

         (int status, string body) ran = Http("POST", "/api/v1/alerts/run", "{}");
         Assert.AreEqual(200, ran.status, ran.body);
         StringAssert.Contains("\"digest_events\":1", ran.body, ran.body);

         string message = Pop3ClientSimulator.AssertGetFirstMessageText(recipient.Address, "test");
         StringAssert.Contains("test.digest", message, "The digest names what fired.");
         StringAssert.Contains("The disk that was nearly full.", message);
         StringAssert.Contains("digest", message.ToLowerInvariant());

         StringAssert.Contains("\"notified\":true", Read(EventsPath + "?limit=50"),
            "Once it has gone out it does not go out again.");

         (int status, string body) second = Http("POST", "/api/v1/alerts/run", "{}");
         Assert.AreEqual(200, second.status, second.body);
         StringAssert.Contains("\"digest_events\":0", second.body,
            "A digest that says 'nothing happened' every morning is the first thing an administrator filters away.");
      }

      [Test]
      [Description("A condition set to send at once sends one message per event, naming the condition and its summary.")]
      public void APerEventRuleSendsOneMessage()
      {
         Account recipient = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "atonce@" + _domain.Name, "test");
         Account sender = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "atonce-from@" + _domain.Name, "test");

         Http("PUT", "/api/v1/settings",
            "{\"alert_recipient\":\"" + recipient.Address + "\",\"alert_sender_address\":\"" + sender.Address + "\"}");

         Http("PUT", RulesPath + "/test.state", "{\"enabled\":true,\"digest\":false,\"cooldown_minutes\":0,\"actions\":1}");

         Raise("test.state", true, "The queue that stopped turning over.");

         (int status, string body) ran = Http("POST", "/api/v1/alerts/run", "{}");
         Assert.AreEqual(200, ran.status, ran.body);
         StringAssert.Contains("\"notifications_sent\":1", ran.body, ran.body);

         string message = Pop3ClientSimulator.AssertGetFirstMessageText(recipient.Address, "test");
         StringAssert.Contains("test.state", message);
         StringAssert.Contains("The queue that stopped turning over.", message);
         StringAssert.Contains("Auto-Submitted: auto-generated", message,
            "An alert must not provoke an auto-reply from whatever receives it.");
      }

      [Test]
      [Description("A webhook body carries a timestamp and an HMAC-SHA256 signature over 'timestamp.body' that a receiver can recompute - which is the recipe the documentation states.")]
      public void AWebhookBodyIsSignedSoAReceiverCanVerifyIt()
      {
         using (var endpoint = new FakeHttpEndpoint(200, "{}"))
         {
            Http("PUT", RulesPath + "/test.webhook",
               "{\"enabled\":true,\"digest\":false,\"cooldown_minutes\":0,\"actions\":2," +
               "\"webhook\":\"" + endpoint.UrlFor("/hook") + "\",\"webhook_secret\":\"" + Secret + "\"}");

            Raise("test.webhook", true, "Something a receiver should hear about.");

            (int status, string body) ran = Http("POST", "/api/v1/alerts/run", "{}");
            Assert.AreEqual(200, ran.status, ran.body);
            StringAssert.Contains("\"webhooks_delivered\":1", ran.body, ran.body);

            string request = OneRequest(endpoint);
            Assert.IsNotNull(request, "The webhook was never called.");

            string timestamp = Header(request, "X-hMailServer-Timestamp");
            string signature = Header(request, "X-hMailServer-Signature");
            string sent = BodyOf(request);

            Assert.IsNotEmpty(timestamp, "A signature with no timestamp can be replayed for ever.");
            StringAssert.StartsWith("sha256=", signature, signature);

            string expected = Hmac(Secret, timestamp + "." + sent);
            Assert.AreEqual("sha256=" + expected, signature,
               "The recipe in docs/AlertsAndAuditTrail.md is HMAC-SHA256 over the timestamp, a full stop, and the raw body.");

            StringAssert.Contains("\"condition\":\"test.webhook\"", sent, sent);
            StringAssert.Contains("\"state\":\"raised\"", sent);
            StringAssert.Contains("Something a receiver should hear about.", sent);
            StringAssert.Contains("Content-Type: application/json", request, request);

            StringAssert.Contains("\"webhook\":\"delivered\"", Read(EventsPath + "?limit=50"));
         }
      }

      [Test]
      [Description("An endpoint that answers with an error is a failure, not a delivery: the event stays pending for the next attempt.")]
      public void AnEndpointThatRefusesIsNotADelivery()
      {
         using (var endpoint = new FakeHttpEndpoint(500, "no thank you"))
         {
            Http("PUT", RulesPath + "/test.webhook",
               "{\"enabled\":true,\"digest\":false,\"cooldown_minutes\":0,\"actions\":2," +
               "\"webhook\":\"" + endpoint.UrlFor("/hook") + "\",\"webhook_secret\":\"" + Secret + "\"}");

            Raise("test.webhook", true, "The endpoint is there and unhappy.");

            (int status, string body) ran = Http("POST", "/api/v1/alerts/run", "{}");
            Assert.AreEqual(200, ran.status, ran.body);
            StringAssert.Contains("\"webhooks_delivered\":0", ran.body, ran.body);

            StringAssert.Contains("\"webhook\":\"pending\"", Read(EventsPath + "?limit=50"));
         }
      }

      [Test]
      [Description("An endpoint that does not answer is retried and then dead-lettered, with the event kept rather than lost.")]
      public void AFailingEndpointIsRetriedAndThenDeadLettered()
      {
         Http("PUT", "/api/v1/settings", "{\"alert_webhook_max_attempts\":2}");

         // An endpoint that was there and is not any more: a port taken and
         // then given up, which is the shape of a receiver that has moved and
         // the commonest reason a webhook stops working.
         string gone;
         using (var endpoint = new FakeHttpEndpoint(200, "{}"))
            gone = endpoint.UrlFor("/gone");

         Http("PUT", RulesPath + "/test.deadletter",
            "{\"enabled\":true,\"digest\":false,\"cooldown_minutes\":0,\"actions\":2," +
            "\"webhook\":\"" + gone + "\",\"webhook_secret\":\"" + Secret + "\"}");

         Raise("test.deadletter", true, "Nobody is listening.");

         (int status, string body) first = Http("POST", "/api/v1/alerts/run", "{}");
         Assert.AreEqual(200, first.status, first.body);
         StringAssert.Contains("\"webhooks_delivered\":0", first.body, first.body);
         StringAssert.Contains("\"webhook\":\"pending\"", Read(EventsPath + "?limit=50"),
            "One failure is a retry, not a verdict.");

         (int status, string body) second = Http("POST", "/api/v1/alerts/run", "{}");
         Assert.AreEqual(200, second.status, second.body);

         string events = Read(EventsPath + "?limit=50");
         StringAssert.Contains("\"webhook\":\"dead-lettered\"", events, events);
         StringAssert.Contains("\"webhook_attempts\":2", events, events);
         StringAssert.Contains("Nobody is listening.", events,
            "The event is kept, with what it said. It simply stops being retried.");
      }

      [Test]
      [Description("A rule that calls a webhook is refused without an address and without a secret, because an unsigned body arriving at a URL anyone can read from a configuration file is an alert anyone can forge.")]
      public void AWebhookNeedsAnAddressAndASecret()
      {
         (int status, string body) noAddress = Http("PUT", RulesPath + "/test.webhook",
            "{\"enabled\":true,\"actions\":2,\"webhook\":\"\"}");
         Assert.AreEqual(400, noAddress.status, noAddress.body);
         StringAssert.Contains("address", noAddress.body);

         (int status, string body) noSecret = Http("PUT", RulesPath + "/test.webhook",
            "{\"enabled\":true,\"actions\":2,\"webhook\":\"http://127.0.0.1:1/x\",\"webhook_secret\":\"\"}");
         Assert.AreEqual(400, noSecret.status, noSecret.body);
         StringAssert.Contains("secret", noSecret.body);

         (int status, string body) noAction = Http("PUT", RulesPath + "/test.webhook", "{\"actions\":0}");
         Assert.AreEqual(400, noAction.status, noAction.body);
      }

      [Test]
      [Description("A webhook secret is never handed back; the answer says only whether one is set.")]
      public void AWebhookSecretIsNeverHandedBack()
      {
         Http("PUT", RulesPath + "/test.webhook",
            "{\"enabled\":true,\"actions\":2,\"webhook\":\"http://127.0.0.1:1/hook\"," +
            "\"webhook_secret\":\"" + Secret + "\"}");

         string rules = Read(RulesPath);
         StringAssert.DoesNotContain(Secret, rules, "A reader of the page must not be able to take away what they could sign with.");
         StringAssert.Contains("\"webhook_secret_set\":true", rules, rules);
      }

      [Test]
      [Description("Past the ceiling per hour, everything goes into the digest instead - which is the difference between an alerting system an administrator keeps and one they switch off in the first hour.")]
      public void PastTheCeilingEverythingGoesIntoTheDigest()
      {
         Http("PUT", "/api/v1/settings", "{\"alert_max_per_hour\":1,\"alert_digest_enabled\":true}");
         Http("PUT", RulesPath + "/test.ceiling", "{\"enabled\":true,\"digest\":false,\"cooldown_minutes\":0,\"actions\":1}");

         Raise("test.ceiling", true, "The first one.");
         Http("POST", "/api/v1/alerts/run", "{}");

         Raise("test.ceiling", true, "The second one, over the ceiling.");

         string events = Read(EventsPath + "?limit=50");
         string second = events.Substring(0, events.IndexOf("The first one.", StringComparison.Ordinal));
         StringAssert.Contains("\"held_for_digest\":true", second, events);
      }

      [Test]
      [Description("With alerting switched off altogether, nothing is raised and nothing is sent.")]
      public void AlertingCanBeSwitchedOff()
      {
         Http("PUT", RulesPath + "/test.state", "{\"enabled\":true,\"digest\":false,\"cooldown_minutes\":0,\"actions\":1}");
         Http("PUT", "/api/v1/settings", "{\"alerts_enabled\":false}");

         Assert.AreEqual("false", Extract(Raise("test.state", true), "raised"));
         Assert.AreEqual(0, Count(Read(EventsPath), "\"condition\":\"test.state\""));

         (int status, string body) ran = Http("POST", "/api/v1/alerts/run", "{}");
         Assert.AreEqual(200, ran.status, ran.body);
         StringAssert.Contains("\"notifications_sent\":0", ran.body);
      }

      // ---- helpers -----------------------------------------------------------

      private string Raise(string condition, bool occurrence, string summary = "Raised by the suite.")
      {
         (int status, string body) answer = Http("POST", "/api/v1/alerts/test",
            "{\"condition\":\"" + condition + "\",\"summary\":\"" + summary + "\",\"occurrence\":" +
            (occurrence ? "true" : "false") + "}");
         Assert.AreEqual(200, answer.status, answer.body);
         return answer.body;
      }

      private static string Read(string path)
      {
         (int status, string body) answer = Http("GET", path);
         Assert.AreEqual(200, answer.status, answer.body);
         return answer.body;
      }

      private static bool Enabled(string rules, string condition)
      {
         Match match = Regex.Match(rules, "\"condition\":\"" + Regex.Escape(condition) + "\",\"enabled\":(true|false)");
         Assert.IsTrue(match.Success, "No rule for " + condition + " in: " + rules);
         return match.Groups[1].Value == "true";
      }

      private static int Count(string text, string needle)
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

      private static string Extract(string json, string key)
      {
         Match match = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"?([^\",}]*)\"?");
         Assert.IsTrue(match.Success, "No '" + key + "' in: " + json);
         return match.Groups[1].Value;
      }

      private static string Hmac(string key, string data)
      {
         using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key)))
         {
            byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
            var text = new StringBuilder();
            foreach (byte value in hash)
               text.Append(value.ToString("x2"));

            return text.ToString();
         }
      }

      private static (int status, string body) Http(string method, string path, string requestBody = null)
      {
         string credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes("Administrator:" + AdminPassword));
         return RestRequest.Send("127.0.0.1", RestPort, method, path, "Basic " + credentials, requestBody);
      }

      // FakeHttpEndpoint keeps each request as its raw text - request line,
      // headers and body - which is exactly what a real receiver has to work
      // from when it verifies a signature.
      private static string OneRequest(FakeHttpEndpoint endpoint)
      {
         for (int attempt = 0; attempt < 100; attempt++)
         {
            List<string> requests = endpoint.Requests;
            if (requests.Count > 0)
               return requests[0];

            Thread.Sleep(100);
         }

         return null;
      }

      private static string Header(string request, string name)
      {
         foreach (string line in request.Split(new[] { "\r\n" }, StringSplitOptions.None))
         {
            if (line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
               return line.Substring(name.Length + 1).Trim();
         }

         return "";
      }

      private static string BodyOf(string request)
      {
         int at = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
         return at >= 0 ? request.Substring(at + 4) : "";
      }
   }
}
