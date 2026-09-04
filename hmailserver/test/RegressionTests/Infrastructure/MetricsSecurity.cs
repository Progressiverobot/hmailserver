// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Linq;
using NUnit.Framework;
using RegressionTests.Shared;
using RegressionTests.SSL;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    Access control on the metrics listener (hMailServer.ini [Settings]
   ///    MetricsServerPort).
   ///
   ///    Until this work the listener performed no authentication of any kind and
   ///    served plain HTTP only, so the whole of its protection was the bind address
   ///    - MetricsServerBindAddress, defaulting to 127.0.0.1. That is total
   ///    protection right up to the moment somebody needs to scrape from another
   ///    host, sets the bind address to 0.0.0.0, and thereby publishes delivery-queue
   ///    depth, session counts, authentication-failure counts and the build and
   ///    database schema versions to anyone who can reach the port. Nothing warned
   ///    about it and nothing stopped it.
   ///
   ///    Six properties are pinned here. Every one of them fails against the unfixed
   ///    server, and the reason is stated on each test.
   ///
   ///    The one that matters most is the third: the health probes must go on
   ///    answering, unauthenticated, in EVERY configuration, including the
   ///    configuration this project's own docs/HighAvailabilityRunbook.md section 3
   ///    ships - MetricsServerPort=8080 on MetricsServerBindAddress=0.0.0.0, plain
   ///    HTTP, no credential, with the VIP health check pointed at /readyz. A load
   ///    balancer cannot present a bearer token and a Kubernetes httpGet probe has
   ///    nowhere to keep one, so an implementation that secured this endpoint by
   ///    refusing to start would take the failover health check away from every
   ///    reader who followed the runbook. NonLoopbackBindKeepsProbesOpen... is that
   ///    acceptance test, and it is written to fail both against the unfixed server
   ///    (which serves the exposition to the network) and against the obvious wrong
   ///    fix (which serves nothing at all).
   ///
   ///    Every test restores the shipped configuration in a finally block, and the
   ///    fixture does it again in a OneTimeTearDown. That matters more here than
   ///    usual: these settings live in hMailServer.ini rather than the database, the
   ///    whole suite runs against one live service, and a leftover
   ///    MetricsServerAuthToken would turn HealthProbes, PrometheusConventions,
   ///    DatabaseMetrics and DeliveryMetrics into 401s that have nothing to do with
   ///    what they test.
   /// </summary>
   [TestFixture]
   public class MetricsSecurity : TestFixtureBase
   {
      // 9300-9309 is this fixture's allocated range. Separate ports per concern so
      // that a listener left behind by a failing test cannot be mistaken for the
      // listener the next test is asserting about.
      private const int AuthPort = 9300;
      private const int BindGatePort = 9301;
      private const int TlsPort = 9302;
      private const int LenientPort = 9303;

      // Deliberately ASCII. The server narrows the ini value from UTF-16 to bytes
      // using the system code page, while a client base64-encodes UTF-8, so a
      // credential outside ASCII could compare unequal for reasons that have nothing
      // to do with the code under test.
      private const string Token = "metrics-token-3f9a1c7d5e2b48a6";
      private const string BasicUser = "prometheus";
      private const string BasicPassword = "scrape-pw-8b41";

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

      // Writes the complete access-control configuration in one call, so that every
      // test states all seven settings it depends on instead of inheriting whatever
      // the previous test left behind. These live in an ini file rather than the
      // database and the whole suite shares one live service, so "the last test
      // cleaned up after itself" is not something a test should have to trust.
      private void ConfigureListener(int port, string bindAddress, string token,
         string basicUser, string basicPassword, string certificateFile, string privateKeyFile)
      {
         WriteSetting("MetricsServerBindAddress", bindAddress);
         WriteSetting("MetricsServerPort", port.ToString());
         WriteSetting("MetricsServerAuthToken", token);
         WriteSetting("MetricsServerAuthUsername", basicUser);
         WriteSetting("MetricsServerAuthPassword", basicPassword);
         WriteSetting("MetricsServerCertificateFile", certificateFile);
         WriteSetting("MetricsServerPrivateKeyFile", privateKeyFile);
      }

      // Puts every setting this fixture touches back to its shipped value and
      // restarts, so the next fixture sees a listener that is off, loopback-bound,
      // unauthenticated and plaintext - which is what every other metrics fixture in
      // the suite assumes.
      private void RestoreDefaults()
      {
         ConfigureListener(port: 0, bindAddress: "127.0.0.1", token: "",
            basicUser: "", basicPassword: "", certificateFile: "", privateKeyFile: "");

         _application.Reinitialize();
      }

      // A belt-and-braces reset. If a test fails part way through its own finally
      // block - or is aborted - the ini file would otherwise stay configured, and a
      // leftover credential would fail HealthProbes, PrometheusConventions,
      // DatabaseMetrics and DeliveryMetrics with a 401 that has nothing to do with
      // what they test.
      [OneTimeTearDown]
      public void MetricsSecurityFixtureTearDown()
      {
         RestoreDefaults();
      }

      private static string BasicHeaderValue(string user, string password)
      {
         return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
      }

      private sealed class HttpResult
      {
         public int Status;
         public string Headers = "";
         public string Body = "";
         public long ElapsedMilliseconds;
      }

      // Issues one HTTP/1.0 GET and returns the status code, the raw header block and
      // the body. authorization, when non-null, becomes the Authorization header
      // verbatim - several tests below are about what the server does with a header
      // that is present but wrong, so the value is never sanitised here.
      private static HttpResult HttpGet(int port, string path, string authorization, bool useTls)
      {
         var request = new StringBuilder();
         request.Append("GET ").Append(path).Append(" HTTP/1.0\r\n");
         request.Append("Host: 127.0.0.1\r\n");

         if (authorization != null)
            request.Append("Authorization: ").Append(authorization).Append("\r\n");

         request.Append("Connection: close\r\n\r\n");

         return Exchange(port, Encoding.ASCII.GetBytes(request.ToString()), useTls, false);
      }

      // Sends bytes exactly as given, with no request built for us. Needed because
      // two of the properties below are about request framing itself - where the
      // header block ends, and what happens when it never formally ends - and a
      // helper that always emits a well-formed request cannot express either.
      private static HttpResult HttpRaw(int port, string rawRequest, bool halfCloseAfterWrite)
      {
         return Exchange(port, Encoding.ASCII.GetBytes(rawRequest), false, halfCloseAfterWrite);
      }

      private static HttpResult Exchange(int port, byte[] request, bool useTls, bool halfCloseAfterWrite)
      {
         using (TcpClient client = ConnectWithRetry(port))
         {
            // A bounded read, so a listener that answers nothing fails this test
            // rather than hanging the whole suite. Comfortably longer than the
            // server's own five-second request deadline.
            client.ReceiveTimeout = 20000;

            using (Stream transport = useTls ? OpenTls(client.GetStream(), port) : client.GetStream())
            {
               // Timed from here rather than from the connect, so the measurement is
               // request-to-response and does not include ConnectWithRetry's backoff
               // after a restart. Two tests assert on it, and they are about whether
               // the server recognised the end of the request or waited out its own
               // deadline - nothing else.
               var stopwatch = Stopwatch.StartNew();

               transport.Write(request, 0, request.Length);
               transport.Flush();

               if (halfCloseAfterWrite)
               {
                  // A write half-close: the server sees end-of-stream on the read
                  // side while the write side stays open for the response. This is
                  // what several health checkers do, and it is the only way to say
                  // "that is the whole request" without a blank line.
                  client.Client.Shutdown(SocketShutdown.Send);
               }

               using (var memory = new MemoryStream())
               {
                  byte[] buffer = new byte[4096];
                  int read;

                  try
                  {
                     while ((read = transport.Read(buffer, 0, buffer.Length)) > 0)
                        memory.Write(buffer, 0, read);
                  }
                  catch (IOException)
                  {
                     // A listener that abandons the connection - which is exactly
                     // what a TLS listener does when it is handed plain HTTP - shows
                     // up here. Whatever arrived before that is what the caller
                     // asserts on.
                  }

                  stopwatch.Stop();

                  string raw = Encoding.UTF8.GetString(memory.ToArray());

                  var result = new HttpResult();
                  result.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;

                  string[] lines = raw.Split(new[] { "\r\n" }, StringSplitOptions.None);
                  if (lines.Length > 0)
                  {
                     string[] parts = lines[0].Split(' ');
                     if (parts.Length >= 2)
                     {
                        int status;
                        if (int.TryParse(parts[1], out status))
                           result.Status = status;
                     }
                  }

                  int separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                  if (separator >= 0)
                  {
                     result.Headers = raw.Substring(0, separator);
                     result.Body = raw.Substring(separator + 4);
                  }
                  else
                  {
                     result.Headers = raw;
                  }

                  return result;
               }
            }
         }
      }

      // TLS over an open connection. Certificate errors are accepted on purpose: the
      // example certificate in "SSL examples" is self-signed and is not issued for
      // 127.0.0.1. What is being tested is that a TLS handshake completes at all and
      // that HTTP flows inside it, not PKI.
      //
      // TLS 1.2 is pinned rather than left to the platform default so the handshake
      // cannot fail for a reason outside this fixture - the listener's floor is TLS
      // 1.2 and the process-wide ServicePointManager default is not this fixture's to
      // rely on.
      private static Stream OpenTls(Stream transport, int port)
      {
         var sslStream = new SslStream(transport, false, (sender, certificate, chain, errors) => true);

         try
         {
            sslStream.AuthenticateAsClient("localhost", null, SslProtocols.Tls12, false);
         }
         catch (AuthenticationException ex)
         {
            // Reported as an explained failure rather than left as a raw SSPI
            // exception, because this is the precise point at which the unfixed
            // listener fails: it has no SSL_CTX at all, so it reads the ClientHello
            // as an HTTP request line, 404s it and closes.
            sslStream.Dispose();
            Assert.Fail("The TLS handshake against the metrics listener on port " + port +
               " did not complete: " + ex.Message +
               ". The listener must serve HTTPS when MetricsServerCertificateFile and " +
               "MetricsServerPrivateKeyFile are configured.");
         }
         catch (IOException ex)
         {
            sslStream.Dispose();
            Assert.Fail("The TLS handshake against the metrics listener on port " + port +
               " was abandoned by the server: " + ex.Message +
               ". The listener must serve HTTPS when MetricsServerCertificateFile and " +
               "MetricsServerPrivateKeyFile are configured.");
         }

         return sslStream;
      }

      // Connects, absorbing the bind race immediately after a service restart. A
      // fresh socket per attempt: a TcpClient whose Connect has thrown is not
      // reliably reusable.
      private static TcpClient ConnectWithRetry(int port)
      {
         SocketException last = null;

         for (int attempt = 0; attempt < 25; attempt++)
         {
            var client = new TcpClient();

            try
            {
               client.Connect("127.0.0.1", port);
               return client;
            }
            catch (SocketException ex)
            {
               last = ex;
               client.Close();
               Thread.Sleep(200);
            }
         }

         throw last;
      }

      // True when something accepts a connection on the port. Retries, because it is
      // used to assert the POSITIVE - that the listener is up - straight after a
      // restart.
      private static bool IsPortOpen(int port)
      {
         for (int attempt = 0; attempt < 25; attempt++)
         {
            try
            {
               using (var client = new TcpClient())
               {
                  client.Connect("127.0.0.1", port);
                  return true;
               }
            }
            catch (SocketException)
            {
               Thread.Sleep(200);
            }
         }

         return false;
      }

      // Value of one exact series identity in an exposition body, or NaN when absent.
      private static double ParseSeries(string body, string identity)
      {
         // A sample line is "<identity> <value>"; comments and blank lines are skipped.
         return body.Split('\n')
            .Select(raw => raw.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal))
            .Select(line => (Line: line, Space: line.LastIndexOf(' ')))
            .Where(sample => sample.Space > 0 && sample.Space < sample.Line.Length - 1 && sample.Line.Substring(0, sample.Space) == identity)
            .Select(sample => double.TryParse(sample.Line.Substring(sample.Space + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : (double?)null)
            .FirstOrDefault(value => value.HasValue) ?? double.NaN;
      }

      private static void AssertCarriesNoExposition(HttpResult result, string because)
      {
         Assert.IsFalse(result.Body.Contains("hmailserver_"),
            because + " Body: " + result.Body);
      }

      [Test]
      [Description("A configured bearer token is enforced on /metrics, refusals are counted, and the health probes stay open")]
      public void BearerTokenIsEnforcedOnMetricsButNotOnHealthProbes()
      {
         ConfigureListener(port: AuthPort, bindAddress: "127.0.0.1", token: Token,
            basicUser: "", basicPassword: "", certificateFile: "", privateKeyFile: "");

         _application.Reinitialize();

         try
         {
            // No credential at all. The unfixed listener answers 200 here with the
            // full exposition - it has no notion of an Authorization header - which
            // is the whole defect.
            HttpResult anonymous = HttpGet(AuthPort, "/metrics", null, false);
            Assert.AreEqual(401, anonymous.Status,
               "/metrics must refuse a request that presents no credential once MetricsServerAuthToken is set. Response: " +
               anonymous.Headers + anonymous.Body);
            AssertCarriesNoExposition(anonymous, "A refused request must not carry any part of the exposition.");
            Assert.IsTrue(anonymous.Headers.Contains("WWW-Authenticate: Bearer"),
               "A 401 must advertise the scheme that would work, and only that scheme. Headers: " + anonymous.Headers);
            Assert.IsFalse(anonymous.Headers.Contains("WWW-Authenticate: Basic"),
               "Basic must not be advertised when only a bearer token is configured - a browser would prompt for a " +
               "password that can never work. Headers: " + anonymous.Headers);

            // A wrong token of the same length as the right one, so this cannot pass
            // merely because the lengths differed.
            string wrongToken = "metrics-token-0000000000000000";
            Assert.AreEqual(Token.Length, wrongToken.Length, "The negative control must be the same length as the real token.");

            HttpResult wrong = HttpGet(AuthPort, "/metrics", "Bearer " + wrongToken, false);
            Assert.AreEqual(401, wrong.Status,
               "/metrics must refuse a wrong bearer token. Response: " + wrong.Headers + wrong.Body);

            // A syntactically valid credential in a scheme that is not configured.
            HttpResult wrongScheme = HttpGet(AuthPort, "/metrics", BasicHeaderValue(BasicUser, BasicPassword), false);
            Assert.AreEqual(401, wrongScheme.Status,
               "/metrics must refuse HTTP Basic when only a bearer token is configured. Response: " +
               wrongScheme.Headers + wrongScheme.Body);

            // The empty credential: a header that is present, well formed and carries
            // nothing. The one thing it must not be is treated as absent and waved
            // through. (The server trims the header value, so this arrives as the bare
            // word "Bearer" and is refused as an unusable credential rather than as an
            // empty token - either way it is a 401 and either way it is counted, which
            // is what matters.)
            HttpResult emptyBearer = HttpGet(AuthPort, "/metrics", "Bearer ", false);
            Assert.AreEqual(401, emptyBearer.Status,
               "/metrics must refuse an empty bearer token. Response: " + emptyBearer.Headers + emptyBearer.Body);

            // The health probes carry no credential and must all still answer. This
            // is the assertion that stops a later change putting authentication in
            // front of a probe that has nowhere to keep a secret.
            HttpResult live = HttpGet(AuthPort, "/livez", null, false);
            Assert.AreEqual(200, live.Status, "/livez must not be authenticated. Response: " + live.Headers + live.Body);
            Assert.IsTrue(live.Body.Contains("alive"), "/livez body: " + live.Body);

            HttpResult ready = HttpGet(AuthPort, "/readyz", null, false);
            Assert.AreEqual(200, ready.Status, "/readyz must not be authenticated. Response: " + ready.Headers + ready.Body);
            Assert.IsTrue(ready.Body.Contains("ready"), "/readyz body: " + ready.Body);

            HttpResult health = HttpGet(AuthPort, "/healthz", null, false);
            Assert.AreEqual(200, health.Status, "/healthz must not be authenticated. Response: " + health.Headers + health.Body);
            Assert.IsTrue(health.Body.Contains("\"status\":\"ok\""), "/healthz body: " + health.Body);

            // And the right token gets the exposition.
            HttpResult authorized = HttpGet(AuthPort, "/metrics", "Bearer " + Token, false);
            Assert.AreEqual(200, authorized.Status,
               "The configured bearer token must be accepted. Response: " + authorized.Headers + authorized.Body);
            Assert.IsTrue(authorized.Body.Contains("hmailserver_start_time_seconds"),
               "An authorized scrape must return the exposition. Body: " + authorized.Body);

            // The scheme name is case-insensitive per RFC 7235, and clients differ.
            HttpResult lowerCaseScheme = HttpGet(AuthPort, "/metrics", "bearer " + Token, false);
            Assert.AreEqual(200, lowerCaseScheme.Status,
               "The scheme name is case-insensitive in HTTP. Response: " + lowerCaseScheme.Headers + lowerCaseScheme.Body);

            // A query string must not be a way past the credential: "/metrics?x=1"
            // is the metrics endpoint, not an unknown path that 404s outside the
            // check.
            HttpResult withQuery = HttpGet(AuthPort, "/metrics?collect[]=x", null, false);
            Assert.AreEqual(401, withQuery.Status,
               "/metrics with a query string is still /metrics and is still behind the credential. Response: " +
               withQuery.Headers + withQuery.Body);

            // Four refusals had happened by the time the body being parsed below was
            // captured - the query-string one came after it, which is why the bound is
            // four and not five. The counter proves those requests really went through
            // the authorization path rather than, say, falling into the 404 branch, and
            // it is the only trace a refused scrape leaves, because failures are
            // deliberately not logged one line at a time.
            //
            // GreaterOrEqual rather than AreEqual: the counter lives in ServerStatus
            // and is not reset by Reinitialize(), so an earlier test in the same
            // service run legitimately leaves a non-zero value behind.
            double refused = ParseSeries(authorized.Body, "hmailserver_metrics_unauthorized_requests_total");
            Assert.IsFalse(double.IsNaN(refused),
               "hmailserver_metrics_unauthorized_requests_total is missing. Without it a password-guessing run against " +
               "an exposed /metrics leaves no trace at all. Body: " + authorized.Body);
            Assert.GreaterOrEqual(refused, 4.0,
               "Every refusal above must be counted. Body: " + authorized.Body);

            // The token must not appear anywhere in what the server hands out or
            // writes down.
            Assert.IsFalse(authorized.Body.Contains(Token), "The exposition must never carry the credential.");
            Assert.IsFalse(LogHandler.ReadCurrentDefaultLog().Contains(Token),
               "The configured credential must never be written to the log.");
         }
         finally
         {
            RestoreDefaults();
         }
      }

      [Test]
      [Description("HTTP Basic credentials are enforced on /metrics, and the user name is part of the secret")]
      public void HttpBasicCredentialsAreEnforcedOnMetrics()
      {
         ConfigureListener(port: AuthPort, bindAddress: "127.0.0.1", token: "",
            basicUser: BasicUser, basicPassword: BasicPassword, certificateFile: "", privateKeyFile: "");

         _application.Reinitialize();

         try
         {
            HttpResult anonymous = HttpGet(AuthPort, "/metrics", null, false);
            Assert.AreEqual(401, anonymous.Status,
               "/metrics must refuse an anonymous request once MetricsServerAuthUsername and MetricsServerAuthPassword " +
               "are set. The unfixed server answers 200 with the exposition. Response: " +
               anonymous.Headers + anonymous.Body);
            Assert.IsTrue(anonymous.Headers.Contains("WWW-Authenticate: Basic"),
               "A 401 must advertise Basic when Basic is what is configured. Headers: " + anonymous.Headers);
            Assert.IsFalse(anonymous.Headers.Contains("WWW-Authenticate: Bearer"),
               "Bearer must not be advertised when no token is configured. Headers: " + anonymous.Headers);

            HttpResult wrongPassword = HttpGet(AuthPort, "/metrics", BasicHeaderValue(BasicUser, "wrong-password"), false);
            Assert.AreEqual(401, wrongPassword.Status,
               "/metrics must refuse a wrong Basic password. Response: " + wrongPassword.Headers + wrongPassword.Body);

            // The user name is half of the credential, not a label on it. A listener
            // that only checked the password would pass everything else in this test.
            HttpResult wrongUser = HttpGet(AuthPort, "/metrics", BasicHeaderValue("someone-else", BasicPassword), false);
            Assert.AreEqual(401, wrongUser.Status,
               "/metrics must refuse a correct password presented under a different user name. Response: " +
               wrongUser.Headers + wrongUser.Body);

            HttpResult notBase64 = HttpGet(AuthPort, "/metrics", "Basic ****not base64****", false);
            Assert.AreEqual(401, notBase64.Status,
               "/metrics must refuse a Basic credential that is not valid base64. Response: " +
               notBase64.Headers + notBase64.Body);

            HttpResult authorized = HttpGet(AuthPort, "/metrics", BasicHeaderValue(BasicUser, BasicPassword), false);
            Assert.AreEqual(200, authorized.Status,
               "The configured Basic credential must be accepted. Response: " + authorized.Headers + authorized.Body);
            Assert.IsTrue(authorized.Body.Contains("hmailserver_start_time_seconds"),
               "An authorized scrape must return the exposition. Body: " + authorized.Body);

            // A bearer token must not be a way round a Basic-only configuration.
            HttpResult bearerAgainstBasic = HttpGet(AuthPort, "/metrics", "Bearer " + Token, false);
            Assert.AreEqual(401, bearerAgainstBasic.Status,
               "A bearer token must not be accepted when no token is configured. Response: " +
               bearerAgainstBasic.Headers + bearerAgainstBasic.Body);

            // The probes are open here too. Repeated from the bearer test on purpose:
            // the two credential schemes are separate branches of the same check, and
            // an authenticated probe would be just as fatal behind either one.
            HttpResult ready = HttpGet(AuthPort, "/readyz", null, false);
            Assert.AreEqual(200, ready.Status, "/readyz must not be authenticated. Response: " + ready.Headers + ready.Body);

            Assert.IsFalse(LogHandler.ReadCurrentDefaultLog().Contains(BasicPassword),
               "The configured password must never be written to the log.");
         }
         finally
         {
            RestoreDefaults();
         }
      }

      [Test]
      [Description("A non-loopback bind keeps the health probes open and unauthenticated while closing /metrics until a credential is configured")]
      public void NonLoopbackBindKeepsProbesOpenAndClosesMetricsUntilACredentialIsSet()
      {
         // The configuration from docs/HighAvailabilityRunbook.md section 3, verbatim
         // apart from the port: bound where the network can reach it, plain HTTP, no
         // credential. Both halves of the required behaviour are asserted here, and
         // they pull in opposite directions, which is the point:
         //
         //   - the exposition must NOT be readable (the unfixed server serves it);
         //   - the probes MUST be readable (an implementation that secured this by
         //     refusing to start serves nothing, and would break every deployment
         //     that followed the runbook).
         ConfigureListener(port: BindGatePort, bindAddress: "0.0.0.0", token: "",
            basicUser: "", basicPassword: "", certificateFile: "", privateKeyFile: "");

         _application.Reinitialize();

         try
         {
            // The listener has to be UP. Asserted first and separately from the
            // probes, so that "the probes did not answer" is distinguishable from
            // "nothing is listening at all" in the failure output.
            Assert.IsTrue(IsPortOpen(BindGatePort),
               "The metrics listener must still listen on a non-loopback bind: /livez, /readyz and /healthz have to " +
               "answer in every configuration, and a load balancer health check cannot present a credential. " +
               "Log: " + LogHandler.ReadCurrentDefaultLog());

            // The log states what it decided. Without this, a passing test below
            // could mean the service simply had not applied the new configuration
            // yet.
            Assert.IsTrue(LogHandler.DefaultLogContains("MetricsServer: /metrics will answer 503"),
               "A non-loopback bind with no credential must say so in the application log, naming the settings that " +
               "would fix it. Log: " + LogHandler.ReadCurrentDefaultLog());

            HttpResult live = HttpGet(BindGatePort, "/livez", null, false);
            Assert.AreEqual(200, live.Status,
               "/livez must answer without a credential on a non-loopback bind. Response: " + live.Headers + live.Body);
            Assert.IsTrue(live.Body.Contains("alive"), "/livez body: " + live.Body);

            HttpResult ready = HttpGet(BindGatePort, "/readyz", null, false);
            Assert.AreEqual(200, ready.Status,
               "/readyz must answer without a credential on a non-loopback bind - this is the probe the high " +
               "availability runbook points the VIP health check at. Response: " + ready.Headers + ready.Body);
            Assert.IsTrue(ready.Body.Contains("ready"), "/readyz body: " + ready.Body);

            HttpResult health = HttpGet(BindGatePort, "/healthz", null, false);
            Assert.AreEqual(200, health.Status,
               "/healthz must answer without a credential on a non-loopback bind. Response: " +
               health.Headers + health.Body);
            Assert.IsTrue(health.Body.Contains("\"status\":\"ok\""), "/healthz body: " + health.Body);

            // And the exposition is shut. 503 rather than 401: there is no credential
            // that would work in this configuration, so a challenge would only invite
            // guessing at one that does not exist.
            HttpResult metrics = HttpGet(BindGatePort, "/metrics", null, false);
            Assert.AreEqual(503, metrics.Status,
               "/metrics must not be served unauthenticated on a non-loopback bind. The unfixed server publishes " +
               "queue depth, session counts and auth-failure counts to anyone who can reach the port. Response: " +
               metrics.Headers + metrics.Body);
            AssertCarriesNoExposition(metrics,
               "A refused /metrics must not carry any part of the exposition.");
            Assert.IsTrue(metrics.Body.Contains("MetricsServerAuthToken"),
               "The 503 body must name the setting that opens the endpoint, or an operator has nothing to act on. " +
               "Body: " + metrics.Body);

            // Now configure the credential and nothing else. The same bind address
            // becomes legitimate, which proves the gate is about the credential and
            // not about the address.
            WriteSetting("MetricsServerAuthToken", Token);
            _application.Reinitialize();

            HttpResult stillAnonymous = HttpGet(BindGatePort, "/metrics", null, false);
            Assert.AreEqual(401, stillAnonymous.Status,
               "With a credential configured, an anonymous scrape must be challenged rather than refused as " +
               "unavailable. Response: " + stillAnonymous.Headers + stillAnonymous.Body);
            AssertCarriesNoExposition(stillAnonymous, "A 401 must not carry any part of the exposition.");

            HttpResult authorized = HttpGet(BindGatePort, "/metrics", "Bearer " + Token, false);
            Assert.AreEqual(200, authorized.Status,
               "A non-loopback bind with a credential configured must serve /metrics to a client that presents it. " +
               "Response: " + authorized.Headers + authorized.Body);
            Assert.IsTrue(authorized.Body.Contains("hmailserver_start_time_seconds"),
               "An authorized scrape must return the exposition. Body: " + authorized.Body);

            // The probes are still open with the credential in force.
            HttpResult readyAgain = HttpGet(BindGatePort, "/readyz", null, false);
            Assert.AreEqual(200, readyAgain.Status,
               "/readyz must stay unauthenticated once a credential is configured. Response: " +
               readyAgain.Headers + readyAgain.Body);

            // An operator who leaves this listener on a routed network without TLS
            // has to be told the credential travels in the clear. It is a warning and
            // not a refusal - see MetricsServer.h rule 3 - so the log line is the
            // only place it can appear.
            Assert.IsTrue(LogHandler.DefaultLogContains("will cross the network in clear text"),
               "A credential on a non-loopback bind without TLS must be reported in the application log. Log: " +
               LogHandler.ReadCurrentDefaultLog());
         }
         finally
         {
            RestoreDefaults();
         }
      }

      [Test]
      [Description("The metrics listener serves HTTPS from a context built by SslContextInitializer, refuses plain HTTP on that port, and does not move the mail TLS counters")]
      public void MetricsListenerServesTlsWithoutCountingItAsMailTls()
      {
         string certificate = Paths.Combine(SslSetup.GetSslCertPath(), "example.crt");
         string privateKey = Paths.Combine(SslSetup.GetSslCertPath(), "example.key");

         Assert.IsTrue(File.Exists(certificate), "Certificate " + certificate + " was not found.");
         Assert.IsTrue(File.Exists(privateKey), "Private key " + privateKey + " was not found.");

         // TLS 1.2 has to be enabled for the handshakes below - the listener's floor
         // is TLS 1.2 and the client pins TLS 1.2 - and it is: TestSetup's basic setup
         // forces all four TlsVersionNNEnabled settings on, restarting if it had to
         // change one. Nothing is set here, because a redundant write would only
         // invite the reader to wonder which of the two is authoritative.
         ConfigureListener(port: TlsPort, bindAddress: "127.0.0.1", token: Token,
            basicUser: "", basicPassword: "", certificateFile: certificate, privateKeyFile: privateKey);

         // Emptied before the restart, not after: the AssertNoReportedError at the end
         // of this test is about what building this listener's TLS context did, and
         // that happens during the restart below. Deleting afterwards would throw away
         // the very evidence being asserted on.
         LogHandler.DeleteErrorLog();

         _application.Reinitialize();

         try
         {
            // TLS, with the credential: the exposition. Against the unfixed listener
            // the handshake itself cannot complete - it has no SSL_CTX at all - so
            // Exchange fails this with an explained message before it gets here.
            HttpResult authorized = HttpGet(TlsPort, "/metrics", "Bearer " + Token, true);
            Assert.AreEqual(200, authorized.Status,
               "The metrics listener must serve /metrics over TLS. Response: " + authorized.Headers + authorized.Body);
            Assert.IsTrue(authorized.Body.Contains("hmailserver_start_time_seconds"),
               "The TLS scrape must return the exposition. Body: " + authorized.Body);

            // Baseline for the counter assertion at the end. Taken from this scrape's
            // own body, so the handshake that carried it is already included and
            // cannot be mistaken for one of the handshakes counted below.
            double handshakesBefore = ParseSeries(authorized.Body, "hmailserver_tls_handshakes_total");
            double failuresBefore = ParseSeries(authorized.Body, "hmailserver_tls_handshake_failures_total");
            Assert.IsFalse(double.IsNaN(handshakesBefore), "hmailserver_tls_handshakes_total is missing. Body: " + authorized.Body);
            Assert.IsFalse(double.IsNaN(failuresBefore), "hmailserver_tls_handshake_failures_total is missing. Body: " + authorized.Body);

            // TLS without a credential: still 401. Encryption is not authentication.
            HttpResult anonymous = HttpGet(TlsPort, "/metrics", null, true);
            Assert.AreEqual(401, anonymous.Status,
               "TLS must not be treated as authentication. Response: " + anonymous.Headers + anonymous.Body);

            // The probes stay open over TLS too - a probe configured with scheme
            // HTTPS still has nowhere to keep a secret.
            HttpResult live = HttpGet(TlsPort, "/livez", null, true);
            Assert.AreEqual(200, live.Status,
               "/livez must answer over TLS without a credential. Response: " + live.Headers + live.Body);

            HttpResult ready = HttpGet(TlsPort, "/readyz", null, true);
            Assert.AreEqual(200, ready.Status,
               "/readyz must answer over TLS without a credential. Response: " + ready.Headers + ready.Body);

            // Plain HTTP to the TLS port must get nothing. Falling back to plaintext
            // when the handshake fails would hand the whole exposition to anyone who
            // simply spoke HTTP, which would make the TLS configuration decorative.
            HttpResult plaintext = HttpGet(TlsPort, "/metrics", "Bearer " + Token, false);
            Assert.AreNotEqual(200, plaintext.Status,
               "A plain HTTP request to a TLS-configured metrics listener must not be answered. Response: " +
               plaintext.Headers + plaintext.Body);
            AssertCarriesNoExposition(plaintext,
               "A plain HTTP request to a TLS-configured metrics listener must not receive any part of the exposition.");

            // A second failed handshake, and the reason it is here rather than being a
            // duplicate: the tolerance below is one, so a single failed handshake could
            // be counted and still slip under it. Two cannot.
            HttpResult plaintextAgain = HttpGet(TlsPort, "/metrics", null, false);
            Assert.AreNotEqual(200, plaintextAgain.Status,
               "A plain HTTP request to a TLS-configured metrics listener must not be answered. Response: " +
               plaintextAgain.Headers + plaintextAgain.Body);

            // Six more handshakes happened since the baseline - four that completed
            // (including this last scrape's own) and two, the plain HTTP requests
            // above, that could not - and NONE of them may show up in the mail protocol
            // TLS counters. Those two series are what operators build
            // rate(hmailserver_tls_handshake_failures_total[5m]) alerts on, so a port
            // scanner knocking on the monitoring port must not be able to page somebody
            // about SMTP.
            //
            // A tolerance of one rather than exact equality: the service is live and
            // something else in it may legitimately complete one mail handshake while
            // this test runs. Counting the monitoring handshakes would show up as a
            // delta of four on the first series and two on the second, both clear of
            // that tolerance.
            HttpResult after = HttpGet(TlsPort, "/metrics", "Bearer " + Token, true);
            Assert.AreEqual(200, after.Status, "Response: " + after.Headers + after.Body);

            double handshakesAfter = ParseSeries(after.Body, "hmailserver_tls_handshakes_total");
            double failuresAfter = ParseSeries(after.Body, "hmailserver_tls_handshake_failures_total");

            Assert.LessOrEqual(handshakesAfter - handshakesBefore, 1.0,
               "Handshakes on the metrics listener must not be counted in hmailserver_tls_handshakes_total, which " +
               "describes TLS on the mail protocols. Before: " + handshakesBefore + ", after: " + handshakesAfter);
            Assert.LessOrEqual(failuresAfter - failuresBefore, 1.0,
               "A failed handshake on the metrics listener must not be counted in " +
               "hmailserver_tls_handshake_failures_total: monitoring traffic must not fire a mail TLS alert. " +
               "Before: " + failuresBefore + ", after: " + failuresAfter);

            // Building the context must not have produced a reported error: a
            // High-severity HM5113 out of SslContextInitializer would mean the
            // certificate reached it in a shape it could not use.
            CustomAsserts.AssertNoReportedError();
         }
         finally
         {
            RestoreDefaults();
            LogHandler.DeleteErrorLog();
         }
      }

      [Test]
      [Description("An Authorization header that arrives after the blank line is part of the body and must not authorize the request")]
      public void AuthorizationHeaderAfterTheHeaderBlockIsNotHonoured()
      {
         ConfigureListener(port: AuthPort, bindAddress: "127.0.0.1", token: Token,
            basicUser: "", basicPassword: "", certificateFile: "", privateKeyFile: "");

         _application.Reinitialize();

         try
         {
            // The header block ends at the first blank line. Everything after it is
            // the body, and a body is not metadata: it is attacker-controlled content
            // that must never be searched for credentials.
            //
            // This is a real reachable bypass and not a theoretical one. Any
            // implementation that stops reading at the terminator but then searches
            // the WHOLE buffer it has accumulated - which is the natural way to write
            // it, and how the first attempt at this work did write it - accepts the
            // request below, because the correct token really is present in the
            // bytes; it is just present in the wrong part of the request. A proxy or
            // a WAF in front of this endpoint inspects headers, not bodies, so a
            // credential smuggled below the blank line is exactly the shape that gets
            // past everything on the way in.
            //
            // Against the unfixed server this fails for a different reason - it has
            // no authorization at all and answers 200 - which is equally a failure of
            // the property being asserted.
            //
            // No Content-Length: this listener never reads a request body at all, so
            // one would be a decoration the server ignores, and a wrong one would only
            // make a reader wonder whether the framing mattered. The write half-close
            // below is what says the request is complete.
            string smuggled =
               "GET /metrics HTTP/1.0\r\n" +
               "Host: 127.0.0.1\r\n" +
               "Connection: close\r\n" +
               "\r\n" +
               "Authorization: Bearer " + Token + "\r\n" +
               "\r\n";

            HttpResult result = HttpRaw(AuthPort, smuggled, true);

            Assert.AreEqual(401, result.Status,
               "An Authorization header below the blank line is body content and must not authorize the request. " +
               "Response: " + result.Headers + result.Body);
            AssertCarriesNoExposition(result,
               "A request authorized only by a header hidden in its body must receive no part of the exposition.");

            // The control: the identical token in the header block is accepted, so the
            // test above cannot be passing because the token was wrong or because the
            // raw request was malformed in some other way.
            string proper =
               "GET /metrics HTTP/1.0\r\n" +
               "Host: 127.0.0.1\r\n" +
               "Authorization: Bearer " + Token + "\r\n" +
               "Connection: close\r\n" +
               "\r\n";

            HttpResult control = HttpRaw(AuthPort, proper, true);
            Assert.AreEqual(200, control.Status,
               "The same token in the header block must be accepted, or the assertion above proves nothing. " +
               "Response: " + control.Headers + control.Body);
            Assert.IsTrue(control.Body.Contains("hmailserver_start_time_seconds"),
               "The control scrape must return the exposition. Body: " + control.Body);

            // A header name that only looks like the real one must not match either.
            // "X-Authorization" contains "Authorization", so an implementation that
            // searches for the name rather than matching the whole field name would
            // accept this.
            string prefixed =
               "GET /metrics HTTP/1.0\r\n" +
               "Host: 127.0.0.1\r\n" +
               "X-Authorization: Bearer " + Token + "\r\n" +
               "Connection: close\r\n" +
               "\r\n";

            HttpResult wrongHeaderName = HttpRaw(AuthPort, prefixed, true);
            Assert.AreEqual(401, wrongHeaderName.Status,
               "Only the Authorization header itself may carry the credential - a header whose name merely contains " +
               "\"Authorization\" must not match. Response: " + wrongHeaderName.Headers + wrongHeaderName.Body);
         }
         finally
         {
            RestoreDefaults();
         }
      }

      [Test]
      [Description("A request that is not terminated by a blank CRLF line is still answered, because health probes rely on it")]
      public void RequestWithoutABlankLineTerminatorIsStillAnswered()
      {
         // No credential here: this test is about request framing, and the shipped
         // loopback configuration is the one every probe actually runs against.
         ConfigureListener(port: LenientPort, bindAddress: "127.0.0.1", token: "",
            basicUser: "", basicPassword: "", certificateFile: "", privateKeyFile: "");

         _application.Reinitialize();

         try
         {
            // Bare LF line endings, no CRLF anywhere. This is what a shell one-liner
            // sends - printf 'GET /livez\n\n' | nc host port - and what a handful of
            // minimal health checkers send. The listener used to answer it, because it
            // parsed whatever one recv() returned without looking for a terminator at
            // all, and an implementation that starts insisting on CRLFCRLF answers it
            // with a silent close: no status line, nothing. A load balancer reads that
            // as a failed health check and takes the node out of service, which makes
            // "we tightened the request parser" indistinguishable from "the mail
            // server went down".
            HttpResult bareLf = HttpRaw(LenientPort, "GET /livez\n\n", false);
            Assert.AreEqual(200, bareLf.Status,
               "A request terminated by bare LF must still be answered. Response: " + bareLf.Headers + bareLf.Body);
            Assert.IsTrue(bareLf.Body.Contains("alive"), "/livez body: " + bareLf.Body);
            Assert.LessOrEqual(bareLf.ElapsedMilliseconds, 4000L,
               "A bare-LF terminator must be recognised immediately, not waited out against the request deadline: a " +
               "probe with a three-second timeout fails either way. Took " + bareLf.ElapsedMilliseconds + " ms.");

            // No terminator at all - the client says "that is the whole request" by
            // half-closing its write side. Also answered, and also promptly, because
            // end-of-stream is as final a terminator as a blank line.
            HttpResult halfClosed = HttpRaw(LenientPort, "GET /readyz HTTP/1.0\r\n", true);
            Assert.AreEqual(200, halfClosed.Status,
               "A request with no blank line, ended by a write half-close, must still be answered. Response: " +
               halfClosed.Headers + halfClosed.Body);
            Assert.IsTrue(halfClosed.Body.Contains("ready"), "/readyz body: " + halfClosed.Body);
            Assert.LessOrEqual(halfClosed.ElapsedMilliseconds, 4000L,
               "End of stream ends the request; it must not be waited out against the request deadline. Took " +
               halfClosed.ElapsedMilliseconds + " ms.");

            // Mixed line endings, which is what happens when something concatenates a
            // request line written by one library with headers written by another.
            HttpResult mixed = HttpRaw(LenientPort, "GET /healthz HTTP/1.0\r\nHost: 127.0.0.1\n\r\n", false);
            Assert.AreEqual(200, mixed.Status,
               "Mixed CRLF and LF line endings must not stop the header block from being recognised. Response: " +
               mixed.Headers + mixed.Body);
            Assert.IsTrue(mixed.Body.Contains("\"status\":\"ok\""), "/healthz body: " + mixed.Body);

            // And leniency does not mean anything goes: an unknown path is still a
            // 404, so this test cannot be passing because the listener answers 200 to
            // everything.
            HttpResult unknown = HttpRaw(LenientPort, "GET /does-not-exist\n\n", false);
            Assert.AreEqual(404, unknown.Status,
               "An unknown path must still be 404. Response: " + unknown.Headers + unknown.Body);
         }
         finally
         {
            RestoreDefaults();
         }
      }
   }
}
