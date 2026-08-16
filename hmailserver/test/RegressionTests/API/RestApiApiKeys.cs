// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.API
{
   /// <summary>
   ///    The REST administration API (hMailServer.ini [Settings] RestApiPort)
   ///    used to accept exactly one credential: HTTP Basic carrying the
   ///    administrator password. That password is the same secret hMailAdmin and
   ///    the COM API use, it was replayed on every single request, and it had no
   ///    scope, no expiry and no restriction on where it could be presented
   ///    from. Anyone who captured one request - a proxy log, a CI job's console
   ///    output, a shell history file - held permanent, unrevocable, full
   ///    administrative access to the mail server, and the only way to take it
   ///    back was to change the administrator password everywhere at once.
   ///
   ///    API keys presented as "Authorization: Bearer &lt;token&gt;" fix that.
   ///    Each key is 32 bytes of RAND_bytes entropy, is stored only as its
   ///    SHA-256 digest, carries a label and an expiry, and can be pinned to a
   ///    source address or range. Keys can be created and revoked while the
   ///    server runs.
   ///
   ///    These tests pin the properties that matter, in both directions:
   ///
   ///      * a key issued by the API authenticates a subsequent request;
   ///      * an unknown, an expired, and an out-of-network key are all refused
   ///        with 401 and with byte-identical bodies, so a 401 never confirms
   ///        that a captured token is real and merely stale;
   ///      * a revoked key stops working at once, with no restart;
   ///      * a key cannot mint or revoke keys - only the administrator password
   ///        can, otherwise a narrowly scoped key escalates to an unscoped one
   ///        in a single request;
   ///      * Basic authentication still works, because removing it would break
   ///        every script that exists today. That last one is a negative control
   ///        and passes against the old code as well; it is here so that a
   ///        future change cannot quietly make bearer support a replacement
   ///        instead of an addition.
   /// </summary>
   [TestFixture]
   public class RestApiApiKeys : TestFixtureBase
   {
      // Fixed loopback port for the API under test. The listener refuses to run
      // without TLS unless it is bound to 127.0.0.1, which is why this test can
      // speak plain HTTP to it.
      private const int RestPort = 9104;

      // TestSetup.Authenticate() already expects this to be the administrator
      // password, and the REST API authenticates against the same credential.
      // It is set explicitly because an empty administrator password disables
      // the API outright.
      private const string AdminPassword = "testar";

      // The name the server gives its API key store, alongside hMailServer.ini.
      private const string KeyStoreFileName = "hMailServerApiKeys.ini";

      [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
      private static extern bool WritePrivateProfileString(string section, string key, string value, string filePath);

      // Both of these moved to Shared/IniFileSetting once the listener TLS tests
      // needed them too; the local names are kept so the call sites below read the
      // same as they always did. The DllImport above stays, because the API key store
      // is a different ini file with a section per key and is not a [Settings] value.
      private string[] CandidateDirectories()
      {
         return IniFileSetting.CandidateDirectories();
      }

      private void WriteSetting(string key, string value)
      {
         IniFileSetting.Write(key, value);
      }

      // The key store only exists once a key has been created, so this returns
      // null until then.
      private string FindKeyStore()
      {
         foreach (string directory in CandidateDirectories())
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

      // Rewrites one field of a stored key in place. Used to age a key past its
      // expiry without waiting, and it doubles as proof of the documented
      // out-of-band route: an administrator can edit or delete records in this
      // file and the change takes effect on the next request.
      private void EditStoredKey(string id, string field, string value)
      {
         string store = FindKeyStore();
         Assert.IsNotNull(store, "The API key store was not created. Looked for " + KeyStoreFileName + ".");

         Assert.IsTrue(
            WritePrivateProfileString("Key." + id, field, value, store),
            "Failed to write " + field + " for key " + id + " in " + store + ".");

         // Flush this process's profile cache to disk. WritePrivateProfileString
         // may hold the write in a per-process cache, and the server parses the
         // file's bytes rather than going through the profile API, so without
         // this the server can still read the old value and the test that ages a
         // key past its expiry would assert against stale data.
         WritePrivateProfileString(null, null, null, store);
      }

      // Issues one minimal HTTP/1.0 request against the REST listener and
      // returns the parsed status code and body. authorization is the complete
      // header value ("Basic ...", "Bearer ...") or null to send none. The
      // connect is retried briefly to absorb the listener bind race right after
      // a reinitialize.
      private static (int status, string body) Http(string method, string path, string authorization, string requestBody = null)
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

               byte[] bodyBytes = requestBody == null
                  ? new byte[0]
                  : Encoding.UTF8.GetBytes(requestBody);

               if (requestBody != null)
               {
                  headers.Append("Content-Type: application/json\r\n");
                  // Mandatory: the server reads the body according to
                  // Content-Length and would otherwise stop at the headers.
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

      // Creates a key through the API and returns its id and its clear-text
      // token, which the API hands back exactly once.
      private static (string id, string token) CreateKey(string label, string allowedFrom = null)
      {
         string requestJson = allowedFrom == null
            ? "{\"label\":\"" + label + "\"}"
            : "{\"label\":\"" + label + "\",\"allowed_from\":\"" + allowedFrom + "\"}";

         (int status, string body) created = Basic("POST", "/api/v1/apikeys", requestJson);

         Assert.AreEqual(201, created.status,
            "POST /api/v1/apikeys must create a key. Body: " + created.body);

         string id = JsonValue(created.body, "id");
         string token = JsonValue(created.body, "key");

         Assert.IsNotNull(id, "The create response carried no id. Body: " + created.body);
         Assert.IsNotNull(token, "The create response carried no key. Body: " + created.body);

         // The clear text is returned once and only here; nothing else in the
         // API can produce it again.
         Assert.IsTrue(token.StartsWith("hmapi_", StringComparison.Ordinal),
            "An API key must be recognisable as one. Got: " + token);

         return (id, token);
      }

      [SetUp]
      public void StartRestApi()
      {
         _settings.SetAdministratorPassword(AdminPassword);

         WriteSetting("RestApiBindAddress", "127.0.0.1");
         WriteSetting("RestApiPort", RestPort.ToString());

         // Start every test from an empty store so that one test's keys cannot
         // authenticate another's requests.
         DeleteKeyStore();

         // Reinitialize (not Stop/Start): RestApiPort is cached in
         // IniFileSettings, which is only re-read by InitInstance().
         _application.Reinitialize();

         // Sanity check that the listener is up and the credential works, so a
         // failure below is about authentication and not about setup.
         (int status, string body) probe = Basic("GET", "/api/v1/status");
         Assert.AreEqual(200, probe.status, "REST API did not answer /api/v1/status. Body: " + probe.body);
      }

      [TearDown]
      public void StopRestApi()
      {
         try
         {
            DeleteKeyStore();

            WriteSetting("RestApiPort", "0");
            _application.Reinitialize();
         }
         finally
         {
            // Safety net. Nothing in these tests is expected to report an error:
            // every refused credential is logged with LOG_APPLICATION,
            // deliberately, so that a brute-force attempt cannot flood the ERROR
            // log. But an entry left behind would fail whichever unrelated
            // fixture ran next (TestSetup.PerformBasicSetup asserts the log is
            // clean), so print anything that did turn up and then clear it.
            if (File.Exists(LogHandler.GetErrorLogFileName()))
               Console.WriteLine("Unexpected hMailServer ERROR log content: " + LogHandler.ReadErrorLog());

            LogHandler.DeleteErrorLog();
         }
      }

      [Test]
      [Description("A key issued by the API authenticates a later request as a bearer token")]
      public void IssuedKeyAuthenticatesAsABearerToken()
      {
         // Fails against the old code at the first assertion inside CreateKey:
         // /api/v1/apikeys did not exist, so POST answered 404 and there was no
         // way to obtain a key at all. Even given a token by other means, the
         // old Authenticate_ accepted "basic " and nothing else, so the bearer
         // request below answered 401.
         (string id, string token) key = CreateKey("regression - bearer accepted");

         (int status, string body) authenticated = Bearer("GET", "/api/v1/status", key.token);
         Assert.AreEqual(200, authenticated.status,
            "A valid API key must authenticate a request. Body: " + authenticated.body);

         // The key appears in the listing, by label, without its secret.
         (int status, string body) list = Basic("GET", "/api/v1/apikeys");
         Assert.AreEqual(200, list.status, "Body: " + list.body);
         Assert.IsTrue(list.body.Contains(key.id), "The new key was not listed. Body: " + list.body);
         Assert.IsFalse(list.body.Contains(key.token),
            "The listing must never contain the clear-text key. Body: " + list.body);
      }

      [Test]
      [Description("A token that was never issued is refused")]
      public void UnknownTokenIsRefused()
      {
         // Negative control: this passes against the old code too, because the
         // old code refused every bearer token including the good ones. It is
         // here so that adding bearer support cannot accidentally make the
         // empty store, or a garbage token, authenticate anything.
         //
         // Well-formed but never issued: right prefix, right length, all zeros.
         string unknown = "hmapi_" + new string('0', 64);

         (int status, string body) refused = Bearer("GET", "/api/v1/status", unknown);

         Assert.AreEqual(401, refused.status,
            "An API key that was never issued must be refused. Body: " + refused.body);

         // And a malformed one, which the server rejects before it even looks
         // at the store.
         (int status, string body) malformed = Bearer("GET", "/api/v1/status", "not-a-key");
         Assert.AreEqual(401, malformed.status, "Body: " + malformed.body);
      }

      [Test]
      [Description("An expired key is refused, and the 401 is indistinguishable from an unknown key")]
      public void ExpiredKeyIsRefusedWithoutSayingWhy()
      {
         (string id, string token) key = CreateKey("regression - expiry");

         // It works before it is aged.
         Assert.AreEqual(200, Bearer("GET", "/api/v1/status", key.token).status,
            "The key must work before it expires.");

         // Age it past its expiry by editing the store the way an administrator
         // would. No restart, no rebuild.
         EditStoredKey(key.id, "Expires", "2000-01-01 00:00:00");

         (int status, string body) expired = Bearer("GET", "/api/v1/status", key.token);
         Assert.AreEqual(401, expired.status,
            "An expired API key must be refused. Body: " + expired.body);

         // The interesting half: the refusal must not tell the holder of a
         // captured token that the token is genuine and merely stale, because
         // that is exactly the hint that makes it worth attacking the clock or
         // hunting for a fresher copy.
         (int status, string body) unknown = Bearer("GET", "/api/v1/status", "hmapi_" + new string('0', 64));

         Assert.AreEqual(unknown.status, expired.status, "The status codes must match.");
         Assert.AreEqual(unknown.body, expired.body,
            "An expired key and an unknown key must produce identical responses. Expired: " +
            expired.body + " Unknown: " + unknown.body);
      }

      [Test]
      [Description("A key pinned to another network is refused; one pinned to the caller's network is allowed")]
      public void SourceRestrictedKeyIsEnforcedInBothDirections()
      {
         // 10.99.0.0/24 cannot contain a loopback caller, so this key is
         // useless from where the test speaks - which is the point.
         (string id, string token) elsewhere = CreateKey("regression - other network", "10.99.0.0/24");

         (int status, string body) refused = Bearer("GET", "/api/v1/status", elsewhere.token);
         Assert.AreEqual(401, refused.status,
            "A key presented from outside its allowed source must be refused. Body: " + refused.body);

         // The refusal must again be indistinguishable from an unknown key.
         (int status, string body) unknown = Bearer("GET", "/api/v1/status", "hmapi_" + new string('0', 64));
         Assert.AreEqual(unknown.body, refused.body,
            "An out-of-network key and an unknown key must produce identical responses. Refused: " +
            refused.body + " Unknown: " + unknown.body);

         // The other direction matters just as much: a restriction that does
         // cover the caller must still let the request through. A fix that
         // refused everything with allowed_from set would be worse than no
         // restriction support at all.
         (string id, string token) exact = CreateKey("regression - exact address", "127.0.0.1");
         Assert.AreEqual(200, Bearer("GET", "/api/v1/status", exact.token).status,
            "A key pinned to the caller's exact address must be accepted.");

         (string id, string token) network = CreateKey("regression - loopback network", "127.0.0.0/8");
         Assert.AreEqual(200, Bearer("GET", "/api/v1/status", network.token).status,
            "A key pinned to a range containing the caller must be accepted.");

         (string id, string token) range = CreateKey("regression - address range", "127.0.0.0-127.0.0.255");
         Assert.AreEqual(200, Bearer("GET", "/api/v1/status", range.token).status,
            "A key pinned to an explicit lower-upper range containing the caller must be accepted.");
      }

      [Test]
      [Description("Revoking a key stops it working immediately, with no restart")]
      public void RevokedKeyStopsWorkingImmediately()
      {
         (string id, string token) key = CreateKey("regression - revocation");

         Assert.AreEqual(200, Bearer("GET", "/api/v1/status", key.token).status,
            "The key must work before it is revoked.");

         (int status, string body) revoked = Basic("DELETE", "/api/v1/apikeys/" + key.id);
         Assert.AreEqual(200, revoked.status, "Revoking the key failed. Body: " + revoked.body);

         // No Reinitialize between these two lines: the store is read on every
         // authentication attempt precisely so that revocation is immediate.
         (int status, string body) afterRevoke = Bearer("GET", "/api/v1/status", key.token);
         Assert.AreEqual(401, afterRevoke.status,
            "A revoked API key must stop working at once. Body: " + afterRevoke.body);

         // Revoking it twice is a 404, not a reported success - the same rule
         // the queue endpoints follow.
         Assert.AreEqual(404, Basic("DELETE", "/api/v1/apikeys/" + key.id).status,
            "Revoking an already-revoked key must be 404.");
      }

      [Test]
      [Description("An API key cannot list, create or revoke API keys - only the administrator password can")]
      public void ApiKeyCannotManageApiKeys()
      {
         (string id, string token) key = CreateKey("regression - no self service");

         // The key is unquestionably valid: it authenticates an ordinary
         // endpoint. So a 401 below is about what the credential is allowed to
         // do, not about whether it is real.
         Assert.AreEqual(200, Bearer("GET", "/api/v1/status", key.token).status,
            "The key must be valid for ordinary endpoints.");

         Assert.AreEqual(401, Bearer("GET", "/api/v1/apikeys", key.token).status,
            "An API key must not be able to list API keys.");

         Assert.AreEqual(401, Bearer("POST", "/api/v1/apikeys", key.token, "{\"label\":\"escalated\"}").status,
            "An API key must not be able to mint another key - that would turn a scoped key into an unscoped one.");

         Assert.AreEqual(401, Bearer("DELETE", "/api/v1/apikeys/" + key.id, key.token).status,
            "An API key must not be able to revoke keys - that would let one lock the administrator out.");

         // And the administrator password still can, so the refusals above are
         // about the credential and not about the endpoint being broken. The
         // listing also proves the refused POST created nothing.
         (int status, string body) list = Basic("GET", "/api/v1/apikeys");
         Assert.AreEqual(200, list.status,
            "The administrator password must still manage keys. Body: " + list.body);
         Assert.IsFalse(list.body.Contains("escalated"),
            "The refused create must not have stored a key. Body: " + list.body);
      }

      [Test]
      [Description("HTTP Basic authentication still works - bearer support is additive")]
      public void BasicAuthenticationStillWorks()
      {
         // Negative control. This passes against the old code too, deliberately:
         // it exists so that a later change cannot make bearer tokens a
         // replacement for Basic and silently break every existing script.
         Assert.AreEqual(200, Basic("GET", "/api/v1/status").status,
            "The administrator password must keep working.");

         Assert.AreEqual(200, Basic("GET", "/api/v1/domains").status,
            "The administrator password must keep working on data endpoints.");

         (int status, string body) wrongPassword =
            Http("GET", "/api/v1/status", BasicHeader(AdminPassword + "-wrong"));
         Assert.AreEqual(401, wrongPassword.status,
            "A wrong administrator password must still be refused. Body: " + wrongPassword.body);

         (int status, string body) noCredential = Http("GET", "/api/v1/status", null);
         Assert.AreEqual(401, noCredential.status,
            "No credential must still be refused. Body: " + noCredential.body);
      }

      [Test]
      [Description("Repeated wrong passwords from the loopback interface never ban the caller off anything")]
      public void RepeatedLocalFailuresNeverLockOutLocalAdministration()
      {
         // REST authentication failures feed the same auto-ban accounting the
         // mail ports use, and auto-ban works by creating a deny-everything IP
         // range at priority 100. A range covering 127.0.0.1 would deny the
         // server's own local clients - hMailAdmin, every local script, and
         // SMTP, IMAP and POP3 from the machine itself - which is a far worse
         // outcome than the guessing it was reacting to. Loopback is therefore
         // excluded from registration, and this test is what holds that in
         // place: it is the guarantee that a mistyped password, or a web admin
         // page still replaying a credential that has just been rotated, cannot
         // take local mail down.
         //
         // It also pins the second half of the same decision. Because loopback
         // never registers a failure, none of the requests below reaches
         // PersistentLogonFailure or PersistentSecurityRange at all, so a local
         // flood cannot grow hm_securityranges - and it is that table which is
         // read, uncached, on every single SMTP, IMAP and POP3 connection.
         //
         // Auto-ban has to be turned on for the test to mean anything:
         // PerformBasicSetup turns it off, and with it off RegisterFailedLogin
         // returns before it looks at anything.
         bool autoBanWasEnabled = _settings.AutoBanOnLogonFailure;
         int rangesBefore = _settings.SecurityRanges.Count;

         try
         {
            _settings.ClearLogonFailureList();
            _settings.AutoBanOnLogonFailure = true;
            _settings.MaxInvalidLogonAttempts = 3;
            _settings.MaxInvalidLogonAttemptsWithin = 5;
            _settings.AutoBanMinutes = 3;

            // Three times the ban threshold, and a wrong bearer token as well as
            // a wrong password, so both credential paths are covered.
            for (int i = 0; i < 9; i++)
            {
               (int status, string body) wrong =
                  Http("GET", "/api/v1/status", BasicHeader(AdminPassword + "-wrong"));

               // 0 is what the helper reports when the server closed the
               // connection without answering, which is how a refused caller
               // would look. Every one of these must still be an honest 401.
               Assert.AreEqual(401, wrong.status,
                  "Attempt " + i + " from 127.0.0.1 must be answered, not refused. Body: " + wrong.body);

               Assert.AreEqual(401, Bearer("GET", "/api/v1/status", "hmapi_" + new string('1', 64)).status,
                  "Attempt " + i + " with a bad bearer token must be answered, not refused.");
            }

            // The credential that is correct still works, from the same address,
            // straight after all of that.
            Assert.AreEqual(200, Basic("GET", "/api/v1/status").status,
               "The administrator must not be locked out of the management interface by their own typing.");

            // And nothing was banned: no new IP range, so local mail is
            // untouched. If the loopback exclusion is ever removed, this is the
            // assertion that fails - before a user finds out by losing mail.
            Assert.AreEqual(rangesBefore, _settings.SecurityRanges.Count,
               "No IP range may be created for a loopback caller.");

            Assert.IsFalse(File.Exists(LogHandler.GetErrorLogFileName()),
               "Refused credentials must not report errors: " +
               (File.Exists(LogHandler.GetErrorLogFileName()) ? LogHandler.ReadErrorLog() : ""));
         }
         finally
         {
            _settings.AutoBanOnLogonFailure = autoBanWasEnabled;
            _settings.ClearLogonFailureList();
         }
      }
   }
}
