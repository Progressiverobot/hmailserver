// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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
   ///    The remote domain policies over REST: GET and POST /api/v1/remote-domains,
   ///    PUT and DELETE /api/v1/remote-domains/&lt;id&gt;, the effective-policy query and
   ///    the verification-cache clear.
   ///
   ///    Every write is read back through COM - Settings.RemoteDomainPolicies, the
   ///    collection ExternalDelivery and SMTPConnection read for every message - and
   ///    never only from the response, because a REST handler that answered 201 and
   ///    wrote to a copy the delivery path never sees would pass every assertion made
   ///    against its own body.
   /// </summary>
   [TestFixture]
   public class RestApiRemoteDomains : TestFixtureBase
   {
      // The port the listener answers on: this one on the Windows bench, the
      // suite's own where RestListener finds one already on.
      private static int RestPort = 9131;
      private const string AdminPassword = "testar";

      private readonly List<string> _keyIds = new List<string>();

      [SetUp]
      public void StartRestApi()
      {
         ClearPolicies_();

         _settings.SetAdministratorPassword(AdminPassword);

         RestPort = RestListener.Start(RestPort);

         _application.Reinitialize();

         (int status, string body) probe = Http("GET", "/api/v1/status");
         Assert.AreEqual(200, probe.status, "REST API did not answer /api/v1/status. Body: " + probe.body);
      }

      [TearDown]
      public void StopRestApi()
      {
         try
         {
            ClearPolicies_();

            foreach (string id in _keyIds)
               Http("DELETE", "/api/v1/apikeys/" + id);
            _keyIds.Clear();
         }
         finally
         {
            RestListener.Stop();
            _application.Reinitialize();
         }
      }

      private void ClearPolicies_()
      {
         hMailServer.RemoteDomainPolicies policies = _settings.RemoteDomainPolicies;
         for (int i = policies.Count - 1; i >= 0; i--)
            policies[i].Delete();
         policies.ClearVerificationCache();
      }

      private RemoteDomainPolicy PolicyOverCom_(string domainName)
      {
         try
         {
            return _settings.RemoteDomainPolicies.get_ItemByName(domainName);
         }
         catch (COMException)
         {
            return null;
         }
      }

      [Test]
      [Description("A policy is created, listed, read back through COM with every field, replaced by PUT - absent fields taking the create's defaults - and deleted.")]
      public void APolicyRoundTrips()
      {
         string body = "{\"domain_name\":\"partner-rest.test\",\"description\":\"The partner\",\"outbound_tls\":\"verified\"," +
                       "\"require_inbound_tls\":true,\"max_message_size_kb\":2048,\"max_connections\":2,\"max_messages_per_minute\":30," +
                       "\"allow_automatic_replies\":false,\"allow_forwarding\":false,\"callout_enabled\":true," +
                       "\"callout_host\":\"mx.partner-rest.test\",\"callout_port\":2525,\"callout_timeout_seconds\":8," +
                       "\"callout_cache_minutes\":30,\"callout_max_per_minute\":5}";

         (int status, string created) = Http("POST", "/api/v1/remote-domains", body);
         Assert.AreEqual(201, status, created);
         int id = ExtractNumber(created, "id");
         StringAssert.Contains("\"outbound_tls\":\"verified\"", created);

         RemoteDomainPolicy policy = PolicyOverCom_("partner-rest.test");
         Assert.IsNotNull(policy, "The POST answered 201 and the running collection does not have the policy.");
         Assert.AreEqual(id, policy.ID);
         Assert.AreEqual("The partner", policy.Description);
         Assert.AreEqual(eRemoteTlsRequirement.eRTVerified, policy.OutboundTls);
         Assert.IsTrue(policy.RequireInboundTls);
         Assert.AreEqual(2048, policy.MaxMessageSizeKB);
         Assert.AreEqual(2, policy.MaxConnections);
         Assert.AreEqual(30, policy.MaxMessagesPerMinute);
         Assert.IsFalse(policy.AllowAutomaticReplies);
         Assert.IsFalse(policy.AllowForwarding);
         Assert.IsTrue(policy.CalloutEnabled);
         Assert.AreEqual("mx.partner-rest.test", policy.CalloutHost);
         Assert.AreEqual(2525, policy.CalloutPort);
         Assert.AreEqual(8, policy.CalloutTimeoutSeconds);
         Assert.AreEqual(30, policy.CalloutCacheMinutes);
         Assert.AreEqual(5, policy.CalloutMaxPerMinute);

         (int listStatus, string list) = Http("GET", "/api/v1/remote-domains");
         Assert.AreEqual(200, listStatus);
         StringAssert.Contains("\"domain_name\":\"partner-rest.test\"", list);

         (int putStatus, string replaced) = Http("PUT", "/api/v1/remote-domains/" + id,
            "{\"domain_name\":\"partner-rest.test\",\"outbound_tls\":\"dane\"}");
         Assert.AreEqual(200, putStatus, replaced);

         policy = PolicyOverCom_("partner-rest.test");
         Assert.AreEqual(eRemoteTlsRequirement.eRTDane, policy.OutboundTls, "The PUT did not reach the running policy.");
         Assert.AreEqual(0, policy.MaxMessageSizeKB, "A PUT replaces the record: an absent field takes the create's default.");
         Assert.IsTrue(policy.AllowForwarding, "A PUT replaces the record: an absent field takes the create's default.");
         Assert.IsFalse(policy.CalloutEnabled);

         Assert.AreEqual(200, Http("DELETE", "/api/v1/remote-domains/" + id).status);
         Assert.IsNull(PolicyOverCom_("partner-rest.test"), "The DELETE answered 200 and the policy is still in effect.");
         Assert.AreEqual(404, Http("DELETE", "/api/v1/remote-domains/" + id).status);
      }

      [Test]
      [Description("A body the resource cannot honour is refused with a sentence naming the field, and nothing is created.")]
      public void ABadBodyIsRefusedByName()
      {
         (int status, string body) refused = Http("POST", "/api/v1/remote-domains", "{\"domain_name\":\"bad-rest.test\",\"outbound_tls\":\"sometimes\"}");
         Assert.AreEqual(400, refused.status, refused.body);
         StringAssert.Contains("outbound_tls", refused.body);

         refused = Http("POST", "/api/v1/remote-domains", "{\"domain_name\":\"bad-rest.test\",\"callout_timeout_seconds\":61}");
         Assert.AreEqual(400, refused.status, "A timeout above sixty seconds is spent inside somebody's SMTP session. Body: " + refused.body);
         StringAssert.Contains("callout_timeout_seconds", refused.body);

         refused = Http("POST", "/api/v1/remote-domains", "{\"domain_name\":\"bad-rest.test\",\"requires_tls\":true}");
         Assert.AreEqual(400, refused.status, "A misspelt key must be refused, not ignored. Body: " + refused.body);
         StringAssert.Contains("unknown field", refused.body);

         refused = Http("POST", "/api/v1/remote-domains", "{\"description\":\"no domain\"}");
         Assert.AreEqual(400, refused.status, refused.body);
         StringAssert.Contains("domain_name", refused.body);

         refused = Http("POST", "/api/v1/remote-domains", "{\"domain_name\":\"someone@bad-rest.test\"}");
         Assert.AreEqual(400, refused.status, "An address is not a domain pattern. Body: " + refused.body);

         Assert.IsNull(PolicyOverCom_("bad-rest.test"), "A refused body created a policy.");

         Assert.AreEqual(201, Http("POST", "/api/v1/remote-domains", "{\"domain_name\":\"twice-rest.test\"}").status);
         Assert.AreEqual(409, Http("POST", "/api/v1/remote-domains", "{\"domain_name\":\"twice-rest.test\"}").status,
            "A second policy for one pattern must be refused as a conflict.");
      }

      [Test]
      [Description("The effective query answers the record that governs a domain, not the record named by it, and 404 when none does.")]
      public void TheEffectivePolicyIsTheMostSpecific()
      {
         Assert.AreEqual(201, Http("POST", "/api/v1/remote-domains", "{\"domain_name\":\"*.effective-rest.test\",\"max_message_size_kb\":500}").status);
         Assert.AreEqual(201, Http("POST", "/api/v1/remote-domains", "{\"domain_name\":\"bank.effective-rest.test\",\"max_message_size_kb\":100}").status);

         (int status, string body) bank = Http("GET", "/api/v1/remote-domains/effective?domain=bank.effective-rest.test");
         Assert.AreEqual(200, bank.status, bank.body);
         StringAssert.Contains("\"domain_name\":\"bank.effective-rest.test\"", bank.body);

         (int status, string body) other = Http("GET", "/api/v1/remote-domains/effective?domain=mail.effective-rest.test");
         Assert.AreEqual(200, other.status, other.body);
         StringAssert.Contains("\"domain_name\":\"*.effective-rest.test\"", other.body);

         Assert.AreEqual(404, Http("GET", "/api/v1/remote-domains/effective?domain=nothing-governs.test").status);
         Assert.AreEqual(400, Http("GET", "/api/v1/remote-domains/effective").status);
      }

      [Test]
      [Description("Clearing the verification cache answers how many verdicts went, and a read-only key may not do it.")]
      public void TheVerificationCacheIsClearedByAnAdministrator()
      {
         (int status, string body) cleared = Http("POST", "/api/v1/remote-domains/verification-cache/clear", "{}");
         Assert.AreEqual(200, cleared.status, cleared.body);
         StringAssert.Contains("\"cleared\":", cleared.body);

         (string readOnlyId, string readOnlyKey) = CreateKey_("remote-domains - readonly", "readonly", null);
         Assert.AreEqual(403, Bearer_("POST", "/api/v1/remote-domains/verification-cache/clear", readOnlyKey, "{}").status,
            "Clearing the cache changes what the next RCPT TO decides; a read-only key must not.");
      }

      [Test]
      [Description("Remote domain policies are server-wide: a domain-restricted key is refused every verb, a read-only key may only read, and no credential is 401.")]
      public void PoliciesAreServerWide()
      {
         (string scopedId, string scopedKey) = CreateKey_("remote-domains - scoped", "full", "example.test");
         (string readOnlyId, string readOnlyKey) = CreateKey_("remote-domains - readonly", "readonly", null);

         string body = "{\"domain_name\":\"scope-rest.test\"}";

         Assert.AreEqual(403, Bearer_("GET", "/api/v1/remote-domains", scopedKey).status);
         Assert.AreEqual(403, Bearer_("POST", "/api/v1/remote-domains", scopedKey, body).status);
         Assert.AreEqual(403, Bearer_("GET", "/api/v1/remote-domains/effective?domain=scope-rest.test", scopedKey).status);
         Assert.IsNull(PolicyOverCom_("scope-rest.test"));

         Assert.AreEqual(200, Bearer_("GET", "/api/v1/remote-domains", readOnlyKey).status);
         Assert.AreEqual(403, Bearer_("POST", "/api/v1/remote-domains", readOnlyKey, body).status);
         Assert.AreEqual(403, Bearer_("PUT", "/api/v1/remote-domains/1", readOnlyKey, body).status);
         Assert.AreEqual(403, Bearer_("DELETE", "/api/v1/remote-domains/1", readOnlyKey).status);
         Assert.IsNull(PolicyOverCom_("scope-rest.test"));

         Assert.AreEqual(401, Http("POST", "/api/v1/remote-domains", null, body).status);
         Assert.IsNull(PolicyOverCom_("scope-rest.test"));
      }

      [Test]
      [Description("The OpenAPI document describes the resource, which is what the Control Deck builds its form from.")]
      public void TheOpenApiDocumentDescribesTheResource()
      {
         (int status, string body) document = Http("GET", "/api/v1/openapi.json");
         Assert.AreEqual(200, document.status);
         StringAssert.Contains("\"/api/v1/remote-domains\"", document.body);
         StringAssert.Contains("\"/api/v1/remote-domains/{id}\"", document.body);
         StringAssert.Contains("\"/api/v1/remote-domains/effective\"", document.body);
         StringAssert.Contains("\"enum\":[\"none\",\"encrypted\",\"verified\",\"dane\"]", document.body);
      }

      private (string id, string key) CreateKey_(string label, string scope, string domains)
      {
         string body = "{\"label\":\"" + label + "\",\"scope\":\"" + scope + "\"" +
                       (domains == null ? "" : ",\"domains\":\"" + domains + "\"") + "}";
         (int status, string created) = Http("POST", "/api/v1/apikeys", body);
         Assert.AreEqual(201, status, "POST /api/v1/apikeys must create a key. Body: " + created);
         string id = Extract_(created, "id");
         _keyIds.Add(id);
         return (id, Extract_(created, "key"));
      }

      private static string Extract_(string json, string key)
      {
         Match match = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
         Assert.IsTrue(match.Success, "No '" + key + "' in: " + json);
         return match.Groups[1].Value;
      }

      private static int ExtractNumber(string json, string key)
      {
         Match match = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(\\d+)");
         Assert.IsTrue(match.Success, "No numeric '" + key + "' in: " + json);
         return int.Parse(match.Groups[1].Value);
      }

      private static (int status, string body) Http(string method, string path, string requestBody = null)
      {
         string credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes("Administrator:" + AdminPassword));
         return Http(method, path, "Basic " + credentials, requestBody);
      }

      private static (int status, string body) Bearer_(string method, string path, string token, string requestBody = null)
      {
         return Http(method, path, "Bearer " + token, requestBody);
      }

      // One HTTP/1.0 request against the REST listener; authorization is the whole
      // header value, or null to send none. The connect is retried briefly to
      // absorb the listener's bind race right after a reinitialize.
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
