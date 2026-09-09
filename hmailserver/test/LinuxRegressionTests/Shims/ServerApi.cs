// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace RegressionTests.Shared
{
   /// <summary>
   ///    One answer from the REST API: the status and the body, with the body's
   ///    JSON on demand. A body that is not JSON is kept as text and Json is null.
   /// </summary>
   public sealed class ApiAnswer
   {
      public int Status { get; }
      public string Body { get; }
      public JsonElement? Json { get; }

      public ApiAnswer(int status, string body)
      {
         Status = status;
         Body = body ?? string.Empty;

         try
         {
            using (var document = JsonDocument.Parse(Body))
               Json = document.RootElement.Clone();
         }
         catch (JsonException)
         {
            Json = null;
         }
      }

      public bool Ok => Status >= 200 && Status < 300;

      /// <summary>The "error" the API puts in a refusal, or the raw body when there is none.</summary>
      public string Error
      {
         get
         {
            JsonElement error;
            if (Json.HasValue && Json.Value.ValueKind == JsonValueKind.Object && Json.Value.TryGetProperty("error", out error))
               return error.GetString();
            return Body;
         }
      }

      public ApiAnswer Expect(int status, string doing)
      {
         if (Status != status)
            Assert.Fail("The REST API answered " + Status + " to " + doing + " (expected " + status + "): " + Body);
         return this;
      }
   }

   /// <summary>
   ///    The REST API of the server under test, as the fixture layer sees it. This is
   ///    what stands where the Windows suite has a COM Application object: every
   ///    domain, account and list the shims make, every queue drain and every log
   ///    read goes through here, with the administrator password, or with an
   ///    account's own credentials for the /api/v1/me routes.
   ///
   ///    Synchronous on purpose. The fixtures are synchronous, the API has a single
   ///    worker thread, and a test that wants two requests in flight is a test of the
   ///    HTTP server, which this project does not carry.
   /// </summary>
   public static class ServerApi
   {
      private static readonly HttpClient Client = new HttpClient
      {
         BaseAddress = new Uri(TestTarget.RestBaseUrl),
         Timeout = TimeSpan.FromSeconds(60)
      };

      private static readonly string AdminAuthorization = Basic("Administrator", TestTarget.AdminPassword);

      private static string Basic(string user, string password)
      {
         return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
      }

      public static ApiAnswer Get(string path)
      {
         return Send(HttpMethod.Get, path, null, AdminAuthorization);
      }

      public static ApiAnswer Post(string path, string json)
      {
         return Send(HttpMethod.Post, path, json, AdminAuthorization);
      }

      public static ApiAnswer Put(string path, string json)
      {
         return Send(HttpMethod.Put, path, json, AdminAuthorization);
      }

      public static ApiAnswer Delete(string path)
      {
         return Send(HttpMethod.Delete, path, null, AdminAuthorization);
      }

      /// <summary>
      ///    A GET that answers null instead of failing the test when the server
      ///    does not answer at all - for watching a listener go away and return.
      /// </summary>
      public static ApiAnswer TryGet(string path)
      {
         try
         {
            return Send(HttpMethod.Get, path, null, AdminAuthorization);
         }
         catch (InvalidOperationException)
         {
            return null;
         }
      }

      /// <summary>The /api/v1/me routes: an account's own credentials, never the administrator's.</summary>
      public static ApiAnswer AsAccount(string address, string password, HttpMethod method, string path, string json = null)
      {
         return Send(method, path, json, Basic(address, password));
      }

      // ---- The server's rate limit, kept on this side of the wire ----
      //
      // RestApiServer refuses a credential's 201st request in any ten-second window
      // with 429, and counts the refused ones too, so a caller that keeps pushing
      // stays refused until the window closes. A fixture layer that makes a dozen
      // calls per test and runs several tests a second reaches that on its own, and
      // the first run of this project did: every fixture's OneTimeSetUp failed on
      // "too many requests". So each credential's requests are counted here, in the
      // same window, and a call that would be the one over the line waits for the
      // window to move instead of being sent. The budget is below the server's so
      // that clock skew between the two counts cannot put a request over.
      private const int RequestsPerWindow = 180;
      private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
      private static readonly Dictionary<string, Queue<DateTime>> Recent = new Dictionary<string, Queue<DateTime>>();

      private static void WaitForBudget(string authorization)
      {
         while (true)
         {
            TimeSpan wait;

            lock (Recent)
            {
               Queue<DateTime> sent;
               if (!Recent.TryGetValue(authorization, out sent))
                  Recent[authorization] = sent = new Queue<DateTime>();

               var now = DateTime.UtcNow;
               while (sent.Count > 0 && now - sent.Peek() >= Window)
                  sent.Dequeue();

               if (sent.Count < RequestsPerWindow)
               {
                  sent.Enqueue(now);
                  return;
               }

               wait = sent.Peek() + Window - now + TimeSpan.FromMilliseconds(100);
            }

            System.Threading.Thread.Sleep(wait);
         }
      }

      private static ApiAnswer Send(HttpMethod method, string path, string json, string authorization)
      {
         for (var attempt = 1; ; attempt++)
         {
            WaitForBudget(authorization);

            using (var request = new HttpRequestMessage(method, path))
            {
               request.Headers.TryAddWithoutValidation("Authorization", authorization);

               if (json != null)
                  request.Content = new StringContent(json, Encoding.UTF8, "application/json");

               try
               {
                  using (var response = Client.Send(request, HttpCompletionOption.ResponseContentRead))
                  {
                     var body = response.Content == null ? string.Empty : response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                     // Refused by the server's count after all (another client on
                     // the same credential, or a window that started earlier than
                     // this side saw). Wait the whole window out, once; a second
                     // refusal is reported as what it is.
                     if ((int) response.StatusCode == 429 && attempt == 1)
                     {
                        var retryAfter = response.Headers.RetryAfter != null && response.Headers.RetryAfter.Delta.HasValue
                           ? response.Headers.RetryAfter.Delta.Value
                           : Window;

                        lock (Recent)
                           Recent.Remove(authorization);

                        System.Threading.Thread.Sleep(retryAfter + TimeSpan.FromMilliseconds(250));
                        continue;
                     }

                     return new ApiAnswer((int) response.StatusCode, body);
                  }
               }
               catch (HttpRequestException ex)
               {
                  throw new InvalidOperationException(
                     "The REST API at " + TestTarget.RestBaseUrl + " did not answer " + method + " " + path +
                     ". Is the server running, and is HMTEST_REST_PORT right? " + ex.Message, ex);
               }
            }
         }
      }

      /// <summary>A JSON string literal, escaped the way RFC 8259 asks.</summary>
      public static string Quote(string value)
      {
         return JsonSerializer.Serialize(value ?? string.Empty);
      }

      /// <summary>The elements of a JSON array answer, or of the named array property of an object answer.</summary>
      public static List<JsonElement> Array(ApiAnswer answer, string property = null)
      {
         var items = new List<JsonElement>();

         if (!answer.Json.HasValue)
            return items;

         var root = answer.Json.Value;

         if (property != null)
         {
            JsonElement inner;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(property, out inner))
               return items;
            root = inner;
         }

         if (root.ValueKind != JsonValueKind.Array)
            return items;

         foreach (var element in root.EnumerateArray())
            items.Add(element);

         return items;
      }

      public static string StringOf(JsonElement element, string property)
      {
         JsonElement value;
         if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out value))
            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
         return null;
      }

      public static long LongOf(JsonElement element, string property, long fallback = 0)
      {
         JsonElement value;
         long parsed;
         if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out value))
            return fallback;
         if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out parsed))
            return parsed;
         if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out parsed))
            return parsed;
         return fallback;
      }

      // ---- What this server's API can do, learned once from its own description ----

      private static readonly Lazy<string> OpenApi = new Lazy<string>(() =>
      {
         var answer = Get("/api/v1/openapi.json");
         return answer.Ok ? answer.Body : string.Empty;
      });

      /// <summary>
      ///    Whether POST /api/v1/domains and DELETE /api/v1/domains/{domain} exist on this
      ///    server. They were added after the account routes and a server built from an
      ///    older tree has only the listing; the fixture layer then works with a domain
      ///    the environment provisioned (docs/RegressionEnvironment.md says how) instead
      ///    of making and deleting its own.
      /// </summary>
      public static bool HasDomainWriteRoutes
      {
         get { return HasRoute("/api/v1/domains", "post"); }
      }

      // The write surface of wave 162, each probed the same way: the path's
      // entry in the OpenAPI document names the verb. A server without the
      // route is an older one, and the shim that needs it skips with the
      // reason that names it.
      public static bool HasSettingsWriteRoutes
      {
         get { return HasRoute("/api/v1/settings", "put"); }
      }

      public static bool HasAliasWriteRoutes
      {
         get { return HasRoute("/api/v1/domains/{domain}/aliases", "post"); }
      }

      public static bool HasRouteWriteRoutes
      {
         get { return HasRoute("/api/v1/routes", "post"); }
      }

      public static bool HasAccountUpdateRoute
      {
         get { return HasRoute("/api/v1/accounts/{address}", "put"); }
      }

      public static bool HasRoute(string path, string verb)
      {
         var document = OpenApi.Value;
         var start = document.IndexOf("\"" + path + "\":{", StringComparison.Ordinal);
         if (start < 0)
            return false;
         var end = document.IndexOf("\"/api/v1/", start + path.Length + 4, StringComparison.Ordinal);
         var entry = end < 0 ? document.Substring(start) : document.Substring(start, end - start);
         return entry.Contains("\"" + verb + "\":");
      }
   }
}
