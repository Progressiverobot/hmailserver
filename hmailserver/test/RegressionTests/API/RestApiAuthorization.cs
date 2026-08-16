// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.API
{
   /// <summary>
   ///    Authorisation, and mail loss, on the REST administration API
   ///    (hMailServer.ini [Settings] RestApiPort).
   ///
   ///    Four separate defects are pinned here. Each one is described at the
   ///    test that covers it; the short version:
   ///
   ///      * <b>An account created over the REST API could not receive mail,
   ///        and the mail was lost rather than bounced.</b>
   ///        <c>HandleCreateAccount_</c> called the one-argument
   ///        <c>PersistentAccount::SaveObject</c>, which forwards
   ///        createInbox = false, so the hm_accounts row existed and the INBOX
   ///        row in hm_imapfolders did not. Every COM caller passes true.
   ///        Delivery then failed inside
   ///        <c>LocalDelivery::CreateAccountLevelMessage_</c> with HM5209 and
   ///        added nothing to the bounce list, so the sender was told the
   ///        message was accepted and nobody ever saw it.
   ///
   ///      * <b>Every API key was the administrator password with a different
   ///        spelling.</b> A key carried a label, an expiry and an optional
   ///        source restriction; nothing else. So a key minted for a monitoring
   ///        probe could DELETE any account in any domain. Keys now carry a
   ///        Scope (readonly by default) and an optional domain list, enforced
   ///        in one place.
   ///
   ///      * <b>A key could act on another domain's data by changing one path
   ///        segment.</b> DELETE /api/v1/accounts/&lt;address&gt; names its
   ///        target entirely in the path, which is the classic
   ///        identifier-in-the-path authorisation bypass - against a route that
   ///        destroys a mailbox.
   ///
   ///      * <b>An oversized request was silently truncated and then
   ///        processed.</b> Only the declared Content-Length was measured
   ///        against the 64 KB cap, never headers plus body, so a request that
   ///        was never fully received could still create an API key.
   ///
   ///    There is also a per-credential request budget, which did not exist in
   ///    any form: failed authentication was counted (per source address, via
   ///    auto-ban), successful requests were not counted at all.
   /// </summary>
   [TestFixture]
   public class RestApiAuthorization : TestFixtureBase
   {
      // Fixed loopback port for the API under test. The listener refuses to run
      // without TLS unless it is bound to 127.0.0.1, which is why this test can
      // speak plain HTTP to it.
      private const int RestPort = 9510;

      // TestSetup.Authenticate() already expects this to be the administrator
      // password, and the REST API authenticates against the same credential.
      // It is set explicitly because an empty administrator password disables
      // the API outright.
      private const string AdminPassword = "testar";

      // The name the server gives its API key store, alongside hMailServer.ini.
      private const string KeyStoreFileName = "hMailServerApiKeys.ini";

      // A second domain, so that "another account's data" is a real thing to
      // reach for and not a hypothetical. PerformBasicSetup clears every domain
      // before each test, so this needs no explicit cleanup.
      private const string OtherDomain = "other.example.test";

      private string FindKeyStore()
      {
         foreach (string directory in IniFileSetting.CandidateDirectories())
         {
            string candidate = Path.Combine(directory, KeyStoreFileName);
            if (File.Exists(candidate))
               return candidate;
         }

         return null;
      }

      private void DeleteKeyStore()
      {
         string store = FindKeyStore();
         if (store != null)
            File.Delete(store);
      }

      // Issues one minimal HTTP/1.0 request against the REST listener and
      // returns the parsed status code and body. authorization is the complete
      // header value ("Basic ...", "Bearer ...") or null to send none. The
      // connect is retried briefly to absorb the listener bind race right after
      // a reinitialize.
      //
      // extraHeader and forcedContentLength exist only for the oversized-request
      // tests: one needs the header block padded, the other needs a
      // Content-Length that disagrees with the body it sends.
      private static (int status, string body) Http(string method, string path, string authorization,
         string requestBody = null, string extraHeader = null, int forcedContentLength = -1)
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

               if (extraHeader != null)
                  headers.Append(extraHeader + "\r\n");

               byte[] bodyBytes = requestBody == null
                  ? new byte[0]
                  : Encoding.UTF8.GetBytes(requestBody);

               if (requestBody != null)
               {
                  headers.Append("Content-Type: application/json\r\n");
                  // Mandatory: the server reads the body according to
                  // Content-Length and would otherwise stop at the headers.
                  headers.Append("Content-Length: " +
                     (forcedContentLength >= 0 ? forcedContentLength : bodyBytes.Length) + "\r\n");
               }

               headers.Append("Connection: close\r\n\r\n");

               byte[] headerBytes = Encoding.ASCII.GetBytes(headers.ToString());

               // Both writes are guarded. A request the server refuses on size
               // grounds is answered and closed while the rest of the body is
               // still in flight, so the write can legitimately fail with a
               // reset - which is not a test failure, it is the server doing
               // what it was asked to. Status 0 is what the caller then sees.
               try
               {
                  stream.Write(headerBytes, 0, headerBytes.Length);

                  if (bodyBytes.Length > 0)
                     stream.Write(bodyBytes, 0, bodyBytes.Length);
               }
               catch (IOException)
               {
                  return (0, "");
               }

               string raw;

               try
               {
                  byte[] buffer = new byte[4096];
                  int read;
                  while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                     memory.Write(buffer, 0, read);

                  raw = Encoding.UTF8.GetString(memory.ToArray());
               }
               catch (IOException)
               {
                  return (0, "");
               }

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

      private static string BasicHeader(string password)
      {
         return "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("Administrator:" + password));
      }

      private static (int status, string body) Basic(string method, string path, string requestBody = null)
      {
         return Http(method, path, BasicHeader(AdminPassword), requestBody);
      }

      private static (int status, string body) Bearer(string method, string path, string token, string requestBody = null)
      {
         return Http(method, path, "Bearer " + token, requestBody);
      }

      private static string JsonValue(string json, string name)
      {
         Match match = Regex.Match(json, "\"" + name + "\"\\s*:\\s*\"([^\"]*)\"");
         return match.Success ? match.Groups[1].Value : null;
      }

      // Creates a key through the API and returns its id, its clear-text token
      // (which the API hands back exactly once) and the scope the server says it
      // gave the key.
      private static (string id, string token, string scope) CreateKey(string label,
         string scope = null, string domains = null)
      {
         var fields = new List<string>();
         fields.Add("\"label\":\"" + label + "\"");

         if (scope != null)
            fields.Add("\"scope\":\"" + scope + "\"");

         if (domains != null)
            fields.Add("\"domains\":\"" + domains + "\"");

         (int status, string body) created = Basic("POST", "/api/v1/apikeys", "{" + string.Join(",", fields) + "}");

         Assert.AreEqual(201, created.status,
            "POST /api/v1/apikeys must create a key. Body: " + created.body);

         string id = JsonValue(created.body, "id");
         string token = JsonValue(created.body, "key");
         string grantedScope = JsonValue(created.body, "scope");

         Assert.IsNotNull(id, "The create response carried no id. Body: " + created.body);
         Assert.IsNotNull(token, "The create response carried no key. Body: " + created.body);

         return (id, token, grantedScope);
      }

      // Asks the API, with the administrator credential, whether an account is
      // still there. Deliberately over the API rather than over COM: the
      // question is whether the REST layer destroyed something, so the answer
      // should come from the same view of the world the REST layer writes to.
      private static bool AccountExists(string domain, string address)
      {
         (int status, string body) list = Basic("GET", "/api/v1/domains/" + domain + "/accounts");

         Assert.AreEqual(200, list.status,
            "GET /api/v1/domains/" + domain + "/accounts failed. Body: " + list.body);

         return list.body.Contains("\"" + address + "\"");
      }

      [SetUp]
      public void StartRestApi()
      {
         _settings.SetAdministratorPassword(AdminPassword);

         IniFileSetting.Write("RestApiBindAddress", "127.0.0.1");
         IniFileSetting.Write("RestApiPort", RestPort.ToString());

         // Explicitly plaintext. These are ini settings, so they survive a run
         // and another fixture (SSL/ListenerTlsConfiguration) sets them; if one
         // were left behind the listener would come up on HTTPS and every plain
         // request below would fail for a reason that has nothing to do with
         // what is being tested.
         IniFileSetting.Write("RestApiCertificateFile", "");
         IniFileSetting.Write("RestApiPrivateKeyFile", "");

         // Start every test from an empty store so that one test's keys cannot
         // authenticate another's requests.
         DeleteKeyStore();

         // Reinitialize (not Stop/Start): RestApiPort is cached in
         // IniFileSettings, which is only re-read by InitInstance(). It also
         // resets the per-credential request counters, which are held in the
         // listener and dropped when it stops - so the budget one test spends
         // is not charged to the next.
         _application.Reinitialize();

         // Sanity check that the listener is up and the credential works, so a
         // failure below is about authorisation and not about setup.
         (int status, string body) probe = Basic("GET", "/api/v1/status");
         Assert.AreEqual(200, probe.status, "REST API did not answer /api/v1/status. Body: " + probe.body);
      }

      [TearDown]
      public void StopRestApi()
      {
         try
         {
            DeleteKeyStore();

            IniFileSetting.Write("RestApiPort", "0");
            _application.Reinitialize();
         }
         finally
         {
            // Safety net. Nothing in these tests is expected to report an
            // error: a refused credential and a refused permission are both
            // LOG_APPLICATION, deliberately, so that a flood cannot fill the
            // ERROR log. But an entry left behind would fail whichever
            // unrelated fixture ran next (TestSetup.PerformBasicSetup asserts
            // the log is clean), so print anything that did turn up and then
            // clear it.
            //
            // Against the unfixed server this is where HM5209 shows up - the
            // mail-loss defect the first test covers reports one error per lost
            // message.
            if (File.Exists(LogHandler.GetErrorLogFileName()))
               Console.WriteLine("Unexpected hMailServer ERROR log content: " + LogHandler.ReadErrorLog());

            LogHandler.DeleteErrorLog();
         }
      }

      [Test]
      [Description("A mailbox created over the REST API can receive mail (it had no INBOX, and the mail was lost)")]
      public void AccountCreatedOverTheApiCanReceiveMail()
      {
         const string address = "restcreated@example.test";
         const string password = "restcreated-password";

         (int status, string body) created = Basic("POST", "/api/v1/domains/example.test/accounts",
            "{\"address\":\"" + address + "\",\"password\":\"" + password + "\"}");

         Assert.AreEqual(201, created.status,
            "POST /api/v1/domains/example.test/accounts must create the account. Body: " + created.body);

         // Everything above passes against the unfixed server too - the account
         // row was written correctly. What follows is the part that did not.
         //
         // Unfixed, there is no INBOX row for this account, so
         // PersistentIMAPFolder::GetUserInboxFolder returns 0,
         // LocalDelivery::CreateAccountLevelMessage_ gives up, HM5209 is
         // reported and the message is neither delivered nor bounced. This
         // assertion then fails on its 45-second timeout with the mailbox empty,
         // and the TearDown above prints the HM5209 that explains why.
         var smtp = new SmtpClientSimulator();
         smtp.Send("test@example.test", address, "Delivered to a REST-created mailbox",
            "This message proves the mailbox exists as far as delivery is concerned.");

         Pop3ClientSimulator.AssertMessageCount(address, password, 1, TimeSpan.FromSeconds(45));

         string text = Pop3ClientSimulator.AssertGetFirstMessageText(address, password);
         StringAssert.Contains("Delivered to a REST-created mailbox", text);

         // And nothing was reported while doing it. HM5209 - "Unable to create
         // account-level message" - is exactly what the old code produced here,
         // so this is not a decorative assertion.
         CustomAsserts.AssertNoReportedError();
      }

      [Test]
      [Description("A key created without a scope is read-only and cannot delete an account")]
      public void KeyWithNoScopeIsReadOnlyAndCannotDestroyAnything()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "victim@example.test", "secret");

         (string id, string token, string scope) key = CreateKey("regression - default scope");

         // Least privilege by default, and the response says so rather than
         // leaving the caller to guess.
         Assert.AreEqual("readonly", key.scope,
            "A create request that names no scope must produce a read-only key.");

         // The key is unquestionably valid: it reads. So the refusals below are
         // about what it is allowed to do, not about whether it is real.
         Assert.AreEqual(200, Bearer("GET", "/api/v1/status", key.token).status,
            "A read-only key must still be able to read.");
         Assert.AreEqual(200, Bearer("GET", "/api/v1/domains", key.token).status,
            "A read-only key must still be able to read.");

         // The assertion the old code fails: it answered 200 {"deleted":true}
         // and the mailbox, its folders and its files were gone.
         (int status, string body) refused = Bearer("DELETE", "/api/v1/accounts/victim@example.test", key.token);

         Assert.AreEqual(403, refused.status,
            "A read-only key must not be able to delete an account. Body: " + refused.body);
         StringAssert.Contains("read-only", refused.body);

         Assert.IsTrue(AccountExists("example.test", "victim@example.test"),
            "The refused DELETE must not have deleted the account.");

         // Creating is a change too.
         (int status, string body) refusedCreate = Bearer("POST", "/api/v1/domains/example.test/accounts",
            key.token, "{\"address\":\"minted@example.test\",\"password\":\"secret\"}");

         Assert.AreEqual(403, refusedCreate.status,
            "A read-only key must not be able to create an account. Body: " + refusedCreate.body);
         Assert.IsFalse(AccountExists("example.test", "minted@example.test"),
            "The refused POST must not have created an account.");
      }

      [Test]
      [Description("A key created with scope 'full' can still write - the restriction is not a blanket refusal")]
      public void FullScopeKeyCanStillWrite()
      {
         // The other direction, and it matters as much: a fix that refused every
         // write from every key would pass the test above and be useless.
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "removable@example.test", "secret");

         (string id, string token, string scope) key = CreateKey("regression - full scope", "full");

         Assert.AreEqual("full", key.scope, "The server must grant the scope that was asked for.");

         (int status, string body) deleted = Bearer("DELETE", "/api/v1/accounts/removable@example.test", key.token);

         Assert.AreEqual(200, deleted.status,
            "A full-scope key must still be able to delete an account. Body: " + deleted.body);
         Assert.IsFalse(AccountExists("example.test", "removable@example.test"),
            "The account should have been deleted.");

         // And an unrecognised scope is refused rather than quietly interpreted.
         (int status, string body) badScope = Basic("POST", "/api/v1/apikeys",
            "{\"label\":\"regression - bad scope\",\"scope\":\"administrator\"}");

         Assert.AreEqual(400, badScope.status,
            "An unrecognised scope must be refused, not guessed at. Body: " + badScope.body);
      }

      [Test]
      [Description("A key restricted to one domain cannot reach another domain by changing the address in the path")]
      public void DomainScopedKeyCannotActOnAnotherDomain()
      {
         Domain other = SingletonProvider<TestSetup>.Instance.AddDomain(OtherDomain);

         SingletonProvider<TestSetup>.Instance.AddAccount(other, "victim@" + OtherDomain, "secret");
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "mine@example.test", "secret");

         // Full authority, deliberately, so that nothing below can be explained
         // by the key being read-only. The only thing narrowing it is the domain
         // list.
         (string id, string token, string scope) key =
            CreateKey("regression - domain scoped", "full", "example.test");

         // The bypass, in one line: the same route, the same verb, the same
         // credential, one path segment changed. Against the unfixed server this
         // answered 200 {"deleted":true} and destroyed a mailbox in a domain the
         // key had nothing to do with.
         (int status, string body) refused = Bearer("DELETE", "/api/v1/accounts/victim@" + OtherDomain, key.token);

         Assert.AreEqual(403, refused.status,
            "A domain-scoped key must not delete an account in another domain. Body: " + refused.body);
         Assert.IsTrue(AccountExists(OtherDomain, "victim@" + OtherDomain),
            "The refused DELETE must not have deleted the other domain's account.");

         // Listing and creating in the other domain are refused for the same
         // reason and by the same rule.
         Assert.AreEqual(403, Bearer("GET", "/api/v1/domains/" + OtherDomain + "/accounts", key.token).status,
            "A domain-scoped key must not list another domain's accounts.");

         (int status, string body) refusedCreate = Bearer("POST", "/api/v1/domains/" + OtherDomain + "/accounts",
            key.token, "{\"address\":\"planted@" + OtherDomain + "\",\"password\":\"secret\"}");

         Assert.AreEqual(403, refusedCreate.status,
            "A domain-scoped key must not create an account in another domain. Body: " + refusedCreate.body);
         Assert.IsFalse(AccountExists(OtherDomain, "planted@" + OtherDomain),
            "The refused POST must not have created an account in the other domain.");

         // Its own domain still works, which is what makes the restriction a
         // restriction rather than a wall.
         Assert.AreEqual(200, Bearer("GET", "/api/v1/domains/example.test/accounts", key.token).status,
            "A domain-scoped key must still be able to work in its own domain.");

         (int status, string body) allowed = Bearer("DELETE", "/api/v1/accounts/mine@example.test", key.token);
         Assert.AreEqual(200, allowed.status,
            "A domain-scoped key must be able to delete an account in its own domain. Body: " + allowed.body);

         // The domain listing is filtered rather than refused, so the key is not
         // handed the names of domains it cannot touch.
         // Matched on the whole JSON field rather than on the bare name: the
         // other domain is a subdomain of this one, so a substring test for
         // "example.test" would be satisfied by "other.example.test" and would
         // pass whatever the filter did.
         (int status, string body) domains = Bearer("GET", "/api/v1/domains", key.token);
         Assert.AreEqual(200, domains.status, "Body: " + domains.body);
         StringAssert.Contains("\"name\":\"example.test\"", domains.body);
         Assert.IsFalse(domains.body.Contains(OtherDomain),
            "A domain-scoped key must not be told the names of other domains. Body: " + domains.body);

         // The delivery queue is server-wide: one queued message carries
         // recipients in any number of domains, so it is refused outright rather
         // than narrowed wrongly.
         Assert.AreEqual(403, Bearer("GET", "/api/v1/queue", key.token).status,
            "A domain-scoped key must not read the server-wide delivery queue.");
         Assert.AreEqual(403, Bearer("DELETE", "/api/v1/queue/1", key.token).status,
            "A domain-scoped key must not delete from the server-wide delivery queue.");
      }

      [Test]
      [Description("The request budget is spent per credential: one key flooding does not refuse another key or the administrator")]
      public void RequestBudgetIsPerCredentialAndNotGlobal()
      {
         (string id, string token, string scope) noisy = CreateKey("regression - noisy");
         (string id, string token, string scope) quiet = CreateKey("regression - quiet");

         // Well inside the budget: normal use is never refused. If this ever
         // fails the limit has been set somewhere absurd.
         for (int i = 0; i < 20; i++)
         {
            Assert.AreEqual(200, Bearer("GET", "/api/v1/status", noisy.token).status,
               "Ordinary use must not be rate limited (request " + i + ").");
         }

         // Now spend the budget. Bounded rather than open-ended: against the
         // unfixed server no request is ever refused, so the loop has to end by
         // itself and say so.
         int refusedAt = -1;
         string refusedBody = "";
         int attempts = 0;

         DateTime started = DateTime.UtcNow;

         for (int i = 0; i < 2000 && refusedAt < 0; i++)
         {
            attempts = i + 1;

            (int status, string body) response = Bearer("GET", "/api/v1/status", noisy.token);

            if (response.status == 429)
            {
               refusedAt = i;
               refusedBody = response.body;
               break;
            }

            Assert.AreEqual(200, response.status,
               "A request inside the budget must be answered normally. Request " + i + ", body: " + response.body);
         }

         double achievedPerSecond = attempts / Math.Max(0.001, (DateTime.UtcNow - started).TotalSeconds);

         // The budget is a count within a fixed window, so reaching it needs the
         // requests to arrive faster than the window closes. That is a very low
         // bar - the limit is twenty a second and a loopback request is a
         // millisecond or two - but if this bench managed less than that, say so
         // rather than blaming the server.
         Assert.Greater(refusedAt, -1,
            "One credential made " + attempts + " requests without ever being refused, so there is no " +
            "per-credential request budget at all. (Achieved " + achievedPerSecond.ToString("F0") +
            " requests a second; the budget can only be reached above about twenty a second.)");

         StringAssert.Contains("too many requests", refusedBody);

         // Still refused on the next request: the budget is spent for the rest
         // of the window rather than reopening one request at a time.
         Assert.AreEqual(429, Bearer("GET", "/api/v1/status", noisy.token).status,
            "A credential over its budget must stay refused for the window.");

         // And the point of the whole exercise. The other key and the
         // administrator are unaffected, which is what "per credential" means -
         // a shared budget would have locked the administrator out of their own
         // management interface the moment one script misbehaved.
         Assert.AreEqual(200, Bearer("GET", "/api/v1/status", quiet.token).status,
            "Another key must not be refused because a different key flooded.");
         Assert.AreEqual(200, Basic("GET", "/api/v1/status").status,
            "The administrator must not be refused because a key flooded.");
      }

      [Test]
      [Description("An oversized request is refused rather than truncated and acted on")]
      public void OversizedRequestIsRefusedRatherThanTruncated()
      {
         // The cap is 64 KB for headers and body together. This body is just
         // under it on its own, so the old "is Content-Length larger than the
         // cap" check does not fire - but with the headers it is over, and the
         // old reader answered that by leaving the loop with the body cut short
         // and processing what it had. A truncated JSON body is not a syntax
         // error to GetJsonStringValue_: the label is at the front, so the
         // request created a key.
         const string label = "regression - oversized probe";

         string oversizedBody = "{\"label\":\"" + label + "\",\"filler\":\"" + new string('a', 65000) + "\"}";

         // Pad the header block so that headers + terminator + body is over the
         // cap by a comfortable margin whatever the length of the request line.
         string padding = "X-Padding: " + new string('p', 1000);

         (int status, string body) oversized = Http("POST", "/api/v1/apikeys",
            BasicHeader(AdminPassword), oversizedBody, padding);

         // The substantive assertion, and the one that does the work: whatever
         // the server said, it must not have acted on a request it never
         // finished receiving. Against the unfixed server the key is here.
         (int status, string body) list = Basic("GET", "/api/v1/apikeys");
         Assert.AreEqual(200, list.status, "Body: " + list.body);
         Assert.IsFalse(list.body.Contains("oversized probe"),
            "A request that exceeded the size cap must not have created a key. Body: " + list.body);

         // The status, when one arrived at all. A refusal on size grounds is
         // answered while the rest of the body is still in flight, so the
         // response can legitimately be lost to a reset - status 0. What it must
         // never be is 201.
         Assert.AreNotEqual(201, oversized.status,
            "An oversized request must not report success. Body: " + oversized.body);

         if (oversized.status != 0)
         {
            Assert.AreEqual(413, oversized.status,
               "An oversized request must be answered 413. Body: " + oversized.body);
         }

         // A declared Content-Length over the cap is the easy half of the same
         // rule, and it has no write race, so the status can be asserted
         // outright. The old code answered 400 "malformed request" for this.
         (int status, string body) declared = Http("POST", "/api/v1/apikeys",
            BasicHeader(AdminPassword), "{\"label\":\"tiny\"}", null, 200000);

         Assert.AreEqual(413, declared.status,
            "A Content-Length over the cap must be answered 413. Body: " + declared.body);
      }

      [Test]
      [Description("Negative controls: the routes and refusals that must not have changed")]
      public void RoutingAndKeyManagementRefusalsAreUnchanged()
      {
         // These pass against the old code as well, deliberately. The
         // authorisation decision moved out of the dispatch and into one
         // function, and the dispatch was rewritten around a parsed route; this
         // test is what says that rewrite did not quietly move an endpoint or
         // change who is refused.
         Assert.AreEqual(200, Basic("GET", "/api/v1/status").status);
         Assert.AreEqual(200, Basic("GET", "/api/v1/domains").status);
         Assert.AreEqual(200, Basic("GET", "/api/v1/queue").status);
         Assert.AreEqual(200, Basic("GET", "/api/v1/tlsa").status);
         Assert.AreEqual(200, Basic("GET", "/api/v1/domains/example.test/accounts").status);
         Assert.AreEqual(404, Basic("GET", "/api/v1/no-such-endpoint").status);
         Assert.AreEqual(404, Basic("DELETE", "/api/v1/accounts/no-such-account@example.test").status);
         Assert.AreEqual(404, Basic("POST", "/api/v1/queue/999999999/retry").status);
         Assert.AreEqual(401, Http("GET", "/api/v1/status", null).status,
            "No credential must still be refused.");

         (string id, string token, string scope) key = CreateKey("regression - key management", "full");

         // Key management is administrator-only for every key, whatever its
         // scope, and the refusal is 401 rather than 403 - including for a verb
         // that does not exist, so that a key learns nothing from probing the
         // prefix.
         Assert.AreEqual(401, Bearer("GET", "/api/v1/apikeys", key.token).status,
            "A full-scope key must still not be able to list keys.");
         Assert.AreEqual(401, Bearer("POST", "/api/v1/apikeys", key.token, "{\"label\":\"escalated\"}").status,
            "A full-scope key must still not be able to mint a key.");
         Assert.AreEqual(401, Bearer("DELETE", "/api/v1/apikeys/" + key.id, key.token).status,
            "A full-scope key must still not be able to revoke a key.");
         Assert.AreEqual(401, Bearer("PUT", "/api/v1/apikeys", key.token).status,
            "An unsupported verb under the key-management prefix must be 401 for a key, not 404.");

         // And the administrator still can, so the refusals above are about the
         // credential rather than about the endpoint being broken.
         (int status, string body) list = Basic("GET", "/api/v1/apikeys");
         Assert.AreEqual(200, list.status, "Body: " + list.body);
         Assert.IsFalse(list.body.Contains("escalated"),
            "The refused create must not have stored a key. Body: " + list.body);

         // The unauthenticated login shell is still served, and is still the only
         // thing that is.
         Assert.AreEqual(200, Http("GET", "/", null).status,
            "The web admin login page must still be served without a credential.");
         Assert.AreEqual(401, Http("POST", "/", null).status,
            "Only GET / is unauthenticated.");
      }
   }
}
