// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.API
{
   /// <summary>
   ///    The REST gaps the Deck parity census named on 14 September 2026 and
   ///    the routes that closed them: the cache group's ceilings and lives, an
   ///    IP range's expiry, the blocked attachments and the greylisting white
   ///    list as resources, a rule action's abort-if-spam flag and the stored
   ///    user-interface language.
   ///
   ///    Every value a write claims to have changed is read back through COM,
   ///    as the Control Panel reads it, and never only from the response; every
   ///    refusal is checked to have changed nothing. What a test changes is
   ///    restored in TearDown, because the suite runs on these settings after
   ///    this fixture.
   /// </summary>
   [TestFixture]
   public class RestApiParityGaps : TestFixtureBase
   {
      // The port the listener answers on: this one on the Windows bench, the
      // suite's own where RestListener finds one already on.
      private static int RestPort = 9125;
      private const string AdminPassword = "testar";

      private sealed class Snapshot
      {
         public int DomainCacheMaxSizeKb, DomainCacheTTL, AccountCacheMaxSizeKb, AccountCacheTTL;
         public int AliasCacheMaxSizeKb, AliasCacheTTL, DistributionListCacheMaxSizeKb, DistributionListCacheTTL;
      }

      private Snapshot _before;

      private Snapshot Take()
      {
         hMailServer.Cache cache = _settings.Cache;
         return new Snapshot
         {
            DomainCacheMaxSizeKb = cache.DomainCacheMaxSizeKb,
            DomainCacheTTL = cache.DomainCacheTTL,
            AccountCacheMaxSizeKb = cache.AccountCacheMaxSizeKb,
            AccountCacheTTL = cache.AccountCacheTTL,
            AliasCacheMaxSizeKb = cache.AliasCacheMaxSizeKb,
            AliasCacheTTL = cache.AliasCacheTTL,
            DistributionListCacheMaxSizeKb = cache.DistributionListCacheMaxSizeKb,
            DistributionListCacheTTL = cache.DistributionListCacheTTL
         };
      }

      private void Restore(Snapshot s)
      {
         hMailServer.Cache cache = _settings.Cache;
         cache.DomainCacheMaxSizeKb = s.DomainCacheMaxSizeKb;
         cache.DomainCacheTTL = s.DomainCacheTTL;
         cache.AccountCacheMaxSizeKb = s.AccountCacheMaxSizeKb;
         cache.AccountCacheTTL = s.AccountCacheTTL;
         cache.AliasCacheMaxSizeKb = s.AliasCacheMaxSizeKb;
         cache.AliasCacheTTL = s.AliasCacheTTL;
         cache.DistributionListCacheMaxSizeKb = s.DistributionListCacheMaxSizeKb;
         cache.DistributionListCacheTTL = s.DistributionListCacheTTL;
      }

      [SetUp]
      public void StartRestApi()
      {
         _settings.SetAdministratorPassword(AdminPassword);
         _before = Take();

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
            Restore(_before);

            // The range a test makes, over COM, in case an assertion left it.
            hMailServer.SecurityRanges ranges = _settings.SecurityRanges;
            for (int i = ranges.Count - 1; i >= 0; i--)
               if (ranges[i].Name.StartsWith("rest-expiry", StringComparison.Ordinal)) ranges.DeleteByDBID(ranges[i].ID);
         }
         finally
         {
            RestListener.Stop();
            _application.Reinitialize();
         }
      }

      [Test]
      [Description("An IP range's expiry is created with its time, read back through COM, listed, moved, switched off and refused: a time without the flag, a flag without a time, a time that is not a date, and turning expiry on without a time each change nothing.")]
      public void IpRangeExpiryRoundTrip()
      {
         (int status, string body) created = Http("POST", "/api/v1/ipranges",
            "{\"name\":\"rest-expiry\",\"lower\":\"10.96.1.1\",\"upper\":\"10.96.1.1\",\"priority\":44,\"allow_smtp\":false," +
            "\"expires\":true,\"expires_time\":\"2030-01-02 03:04:05\"}");
         Assert.AreEqual(201, created.status, created.body);
         string id = IdOf(created.body);

         var range = _settings.SecurityRanges.get_ItemByName("rest-expiry");
         Assert.IsTrue(range.Expires);
         Assert.AreEqual(new DateTime(2030, 1, 2, 3, 4, 5), Convert.ToDateTime(range.ExpiresTime));

         string list = Http("GET", "/api/v1/ipranges").body;
         string entry = list.Substring(list.IndexOf("\"name\":\"rest-expiry\"", StringComparison.Ordinal));
         entry = entry.Substring(0, entry.IndexOf('}'));
         StringAssert.Contains("\"expires\":true", entry);
         StringAssert.Contains("\"expires_time\":\"2030-01-02 03:04:05\"", entry);

         // Moved, with a date alone meaning midnight; what the body leaves out stays.
         (int putStatus, string putBody) = Http("PUT", "/api/v1/ipranges/" + id, "{\"expires_time\":\"2031-06-07\"}");
         Assert.AreEqual(200, putStatus, putBody);
         StringAssert.Contains("\"expires\":true", putBody);
         StringAssert.Contains("\"expires_time\":\"2031-06-07 00:00:00\"", putBody);
         range = _settings.SecurityRanges.get_ItemByName("rest-expiry");
         Assert.AreEqual(new DateTime(2031, 6, 7, 0, 0, 0), Convert.ToDateTime(range.ExpiresTime));
         Assert.AreEqual(44, range.Priority, "Left out, so left alone.");
         Assert.IsFalse(range.AllowSMTPConnections);

         // Refused, each with nothing changed.
         (int badStatus, string badBody) = Http("PUT", "/api/v1/ipranges/" + id, "{\"expires_time\":\"2031-13-07 00:00:00\"}");
         Assert.AreEqual(400, badStatus, badBody);
         StringAssert.Contains("YYYY-MM-DD HH:MM:SS", badBody);
         Assert.AreEqual(400, Http("PUT", "/api/v1/ipranges/" + id, "{\"expires_time\":\"tomorrow\"}").status);
         Assert.AreEqual(400, Http("PUT", "/api/v1/ipranges/" + id, "{\"expires_time\":\"2031-06-07 25:00:00\"}").status);
         Assert.AreEqual(400, Http("PUT", "/api/v1/ipranges/" + id, "{\"expires\":\"yes\"}").status);
         Assert.AreEqual(400, Http("PUT", "/api/v1/ipranges/" + id, "{\"expires\":false,\"expires_time\":\"2032-01-01 00:00:00\"}").status, "a time is taken only with the flag");
         range = _settings.SecurityRanges.get_ItemByName("rest-expiry");
         Assert.IsTrue(range.Expires);
         Assert.AreEqual(new DateTime(2031, 6, 7, 0, 0, 0), Convert.ToDateTime(range.ExpiresTime));

         // Switched off: the listing no longer shows a time, and turning it on
         // again needs the time in the same body.
         (int offStatus, string offBody) = Http("PUT", "/api/v1/ipranges/" + id, "{\"expires\":false}");
         Assert.AreEqual(200, offStatus, offBody);
         StringAssert.Contains("\"expires\":false,\"expires_time\":\"\"", offBody);
         Assert.IsFalse(_settings.SecurityRanges.get_ItemByName("rest-expiry").Expires);
         (int onStatus, string onBody) = Http("PUT", "/api/v1/ipranges/" + id, "{\"expires\":true}");
         Assert.AreEqual(400, onStatus, onBody);
         StringAssert.Contains("expires_time is required", onBody);
         Assert.IsFalse(_settings.SecurityRanges.get_ItemByName("rest-expiry").Expires, "A refused change changes nothing.");
         Assert.AreEqual(200, Http("PUT", "/api/v1/ipranges/" + id, "{\"expires\":true,\"expires_time\":\"2033-02-03 04:05:06\"}").status);
         range = _settings.SecurityRanges.get_ItemByName("rest-expiry");
         Assert.IsTrue(range.Expires);
         Assert.AreEqual(new DateTime(2033, 2, 3, 4, 5, 6), Convert.ToDateTime(range.ExpiresTime));

         // A create with the flag and no time, or a time and no flag, is refused before anything is saved.
         Assert.AreEqual(400, Http("POST", "/api/v1/ipranges",
            "{\"name\":\"rest-expiry-2\",\"lower\":\"10.96.1.2\",\"upper\":\"10.96.1.2\",\"expires\":true}").status);
         Assert.AreEqual(400, Http("POST", "/api/v1/ipranges",
            "{\"name\":\"rest-expiry-2\",\"lower\":\"10.96.1.2\",\"upper\":\"10.96.1.2\",\"expires_time\":\"2030-01-01 00:00:00\"}").status);
         StringAssert.DoesNotContain("rest-expiry-2", Http("GET", "/api/v1/ipranges").body);

         // The owner of the value refuses the flag without a time over COM as well, in its own sentence.
         var viaCom = _settings.SecurityRanges.Add();
         viaCom.Name = "rest-expiry-3";
         viaCom.LowerIP = "10.96.1.3";
         viaCom.UpperIP = "10.96.1.3";
         viaCom.Expires = true;
         var refusal = Assert.Throws<System.Runtime.InteropServices.COMException>(() => viaCom.Save());
         StringAssert.Contains("An expiring range needs an expiry time", refusal.Message);

         Assert.AreEqual(200, Http("DELETE", "/api/v1/ipranges/" + id).status);
         StringAssert.DoesNotContain("rest-expiry", Http("GET", "/api/v1/ipranges").body);
      }

      [Test]
      [Description("The cache group's four ceilings and four lives are written by PUT and read back through COM as the Control Panel reads them; GET shows them; a negative value, a string for a number and a read-only counter are each refused with nothing changed.")]
      public void CacheCeilingsAndLivesRoundTrip()
      {
         (int status, string body) = Http("PUT", "/api/v1/settings/cache",
            "{\"domain_cache_max_size_kb\":2048,\"domain_cache_ttl\":120,\"account_cache_max_size_kb\":4096,\"account_cache_ttl\":90," +
            "\"alias_cache_max_size_kb\":512,\"alias_cache_ttl\":75,\"distribution_list_cache_max_size_kb\":256,\"distribution_list_cache_ttl\":45}");
         Assert.AreEqual(200, status, body);

         hMailServer.Cache cache = _settings.Cache;
         Assert.AreEqual(2048, cache.DomainCacheMaxSizeKb);
         Assert.AreEqual(120, cache.DomainCacheTTL);
         Assert.AreEqual(4096, cache.AccountCacheMaxSizeKb);
         Assert.AreEqual(90, cache.AccountCacheTTL);
         Assert.AreEqual(512, cache.AliasCacheMaxSizeKb);
         Assert.AreEqual(75, cache.AliasCacheTTL);
         Assert.AreEqual(256, cache.DistributionListCacheMaxSizeKb);
         Assert.AreEqual(45, cache.DistributionListCacheTTL);

         (int getStatus, string got) = Http("GET", "/api/v1/settings/cache");
         Assert.AreEqual(200, getStatus, got);
         StringAssert.Contains("\"domain_cache_max_size_kb\":2048", got);
         StringAssert.Contains("\"domain_cache_ttl\":120", got);
         StringAssert.Contains("\"account_cache_max_size_kb\":4096", got);
         StringAssert.Contains("\"alias_cache_ttl\":75", got);
         StringAssert.Contains("\"distribution_list_cache_max_size_kb\":256", got);
         StringAssert.Contains("\"distribution_list_cache_ttl\":45", got);

         // All or nothing: the acceptable key beside the refused one is not applied.
         (int refusedStatus, string refusedBody) = Http("PUT", "/api/v1/settings/cache", "{\"alias_cache_ttl\":10,\"domain_cache_ttl\":-1}");
         Assert.AreEqual(400, refusedStatus, refusedBody);
         StringAssert.Contains("domain_cache_ttl must be 0 or more", refusedBody);
         Assert.AreEqual(75, _settings.Cache.AliasCacheTTL, "A refused body changes nothing.");
         Assert.AreEqual(120, _settings.Cache.DomainCacheTTL);

         Assert.AreEqual(400, Http("PUT", "/api/v1/settings/cache", "{\"account_cache_max_size_kb\":-5}").status);
         Assert.AreEqual(400, Http("PUT", "/api/v1/settings/cache", "{\"account_cache_max_size_kb\":\"big\"}").status);
         Assert.AreEqual(400, Http("PUT", "/api/v1/settings/cache", "{\"domain_hit_rate\":50}").status, "a counter is read-only");
         Assert.AreEqual(400, Http("PUT", "/api/v1/settings/cache", "{\"domain_cache_size_kb\":1}").status, "what the cache holds is read-only");
         Assert.AreEqual(4096, _settings.Cache.AccountCacheMaxSizeKb);

         // A ceiling of 0 is accepted, as put_DomainCacheMaxSizeKb accepts it.
         Assert.AreEqual(200, Http("PUT", "/api/v1/settings/cache", "{\"domain_cache_max_size_kb\":0}").status);
         Assert.AreEqual(0, _settings.Cache.DomainCacheMaxSizeKb);

         // The OpenAPI document says the ceilings are held in memory only.
         (int docStatus, string doc) = Http("GET", "/api/v1/openapi.json");
         Assert.AreEqual(200, docStatus);
         StringAssert.Contains("Held in memory only", doc);
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
               catch (SocketException e)
               {
                  last = e;
                  System.Threading.Thread.Sleep(200);
               }
            }
            if (last != null)
               throw last;

            using (NetworkStream stream = client.GetStream())
            using (var memory = new MemoryStream())
            {
               byte[] bodyBytes = requestBody == null ? new byte[0] : Encoding.UTF8.GetBytes(requestBody);
               var headers = new StringBuilder();
               headers.Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\n");
               headers.Append("Host: 127.0.0.1\r\n");
               if (authorization != null)
                  headers.Append("Authorization: ").Append(authorization).Append("\r\n");
               if (bodyBytes.Length > 0)
                  headers.Append("Content-Type: application/json\r\n");
               headers.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
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

      // The id of a created entry, from the body every create answers with.
      private static string IdOf(string body)
      {
         int at = body.IndexOf("\"id\":", StringComparison.Ordinal);
         Assert.IsTrue(at >= 0, "No id in: " + body);
         int start = at + 5, end = start;
         while (end < body.Length && char.IsDigit(body[end]))
            end++;
         return body.Substring(start, end - start);
      }
   }
}
