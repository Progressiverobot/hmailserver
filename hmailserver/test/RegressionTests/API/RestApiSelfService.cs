// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.API
{
   /// <summary>
   ///    The account's own endpoints on the REST listener - GET /api/v1/me,
   ///    POST /api/v1/me/password, PUT /api/v1/me/vacation - and the sign-in
   ///    page that presents them. An account's credentials reach exactly these
   ///    three and nothing else; the administrator password and an API key
   ///    reach everything else and not these. Every assertion is made against
   ///    what the server then did: a password that logs on (or no longer does),
   ///    a vacation message COM reads back.
   /// </summary>
   [TestFixture]
   public class RestApiSelfService : TestFixtureBase
   {
      private const int RestPort = 9530;
      private const string AdminPassword = "testar";
      private const string UserPassword = "Original-Passw0rd!";

      private Account _account;

      // The fixture domain's name is not fixed, so the address follows it.
      private string Address
      {
         get { return "self@" + _domain.Name; }
      }

      [SetUp]
      public void StartRestApi()
      {
         _settings.SetAdministratorPassword(AdminPassword);

         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, Address, UserPassword);

         IniFileSetting.Write("RestApiBindAddress", "127.0.0.1");
         IniFileSetting.Write("RestApiPort", RestPort.ToString());
         IniFileSetting.Write("RestApiCertificateFile", "");
         IniFileSetting.Write("RestApiPrivateKeyFile", "");

         _application.Reinitialize();

         (int status, string body) probe = Http("GET", "/api/v1/status", AdminHeader());
         Assert.AreEqual(200, probe.status, "REST API did not answer /api/v1/status. Body: " + probe.body);
      }

      [TearDown]
      public void StopRestApi()
      {
         IniFileSetting.Write("RestApiPort", "0");
         _application.Reinitialize();
      }

      [Test]
      [Description("GET /api/v1/me answers an account's own credentials with its own state")]
      public void MeAnswersTheAccountsOwnState()
      {
         _account.MaxSize = 25;
         _account.Save();

         (int status, string body) me = Http("GET", "/api/v1/me", UserHeader(UserPassword));

         Assert.AreEqual(200, me.status, "Body: " + me.body);
         StringAssert.Contains("\"address\":\"" + Address + "\"", me.body);
         StringAssert.Contains("\"domain\":\"" + _domain.Name + "\"", me.body);
         StringAssert.Contains("\"limit_mb\":25", me.body);
         StringAssert.Contains("\"used_bytes\":", me.body);
         StringAssert.Contains("\"vacation\":{\"enabled\":false", me.body);
         StringAssert.Contains("\"second_factor\":false", me.body);
         StringAssert.Contains("\"directory_linked\":false", me.body);
      }

      [Test]
      [Description("The account's credentials reach the account's endpoints only, and the administrator's reach everything but them")]
      public void EachCredentialReachesItsOwnSurfaceOnly()
      {
         (int status, string body) wrongPassword = Http("GET", "/api/v1/me", UserHeader("not-the-password"));
         Assert.AreEqual(401, wrongPassword.status, "Body: " + wrongPassword.body);

         (int status, string body) unknownAccount = Http("GET", "/api/v1/me", BasicHeader("nobody@" + _domain.Name, UserPassword));
         Assert.AreEqual(401, unknownAccount.status, "Body: " + unknownAccount.body);

         (int status, string body) adminOnMe = Http("GET", "/api/v1/me", AdminHeader());
         Assert.AreEqual(403, adminOnMe.status, "The administrator is not an account. Body: " + adminOnMe.body);
         StringAssert.Contains("account's own credentials", adminOnMe.body);

         (int status, string body) accountOnAdmin = Http("GET", "/api/v1/status", UserHeader(UserPassword));
         Assert.AreEqual(403, accountOnAdmin.status, "An account must not reach the administration API. Body: " + accountOnAdmin.body);

         (int status, string body) accountOnDomains = Http("GET", "/api/v1/domains", UserHeader(UserPassword));
         Assert.AreEqual(403, accountOnDomains.status, "Body: " + accountOnDomains.body);
      }

      [Test]
      [Description("PUT /api/v1/me/vacation sets the automatic reply COM reads back, and clears it again")]
      public void VacationRoundTrip()
      {
         (int status, string body) on = Http("PUT", "/api/v1/me/vacation", UserHeader(UserPassword),
            "{\"enabled\":true,\"subject\":\"Away until Monday\",\"message\":\"I am out of the office.\",\"expires\":true,\"expires_date\":\"2099-12-31\"}");

         Assert.AreEqual(200, on.status, "Body: " + on.body);
         StringAssert.Contains("\"enabled\":true", on.body);

         Account reread = _domain.Accounts.ItemByAddress[Address];
         Assert.IsTrue(reread.VacationMessageIsOn, "COM must see the automatic reply switched on.");
         Assert.AreEqual("Away until Monday", reread.VacationSubject);
         Assert.AreEqual("I am out of the office.", reread.VacationMessage);
         Assert.IsTrue(reread.VacationMessageExpires);

         // The store keeps a date-time; the day is what was asked for.
         StringAssert.StartsWith("2099-12-31", reread.VacationMessageExpiresDate);

         (int status, string body) me = Http("GET", "/api/v1/me", UserHeader(UserPassword));
         StringAssert.Contains("\"subject\":\"Away until Monday\"", me.body);

         // And the API hands the day back in the form it accepts, so a page can
         // put it straight into a date field.
         StringAssert.Contains("\"expires_date\":\"2099-12-31\"", me.body);

         (int status, string body) off = Http("PUT", "/api/v1/me/vacation", UserHeader(UserPassword),
            "{\"enabled\":false,\"subject\":\"\",\"message\":\"\"}");

         Assert.AreEqual(200, off.status, "Body: " + off.body);

         reread = _domain.Accounts.ItemByAddress[Address];
         Assert.IsFalse(reread.VacationMessageIsOn, "COM must see the automatic reply switched off again.");
      }

      [Test]
      [Description("A vacation body without 'enabled', or with a malformed expiry date, is refused")]
      public void MalformedVacationIsRefused()
      {
         (int status, string body) noEnabled = Http("PUT", "/api/v1/me/vacation", UserHeader(UserPassword),
            "{\"subject\":\"x\"}");
         Assert.AreEqual(400, noEnabled.status, "Body: " + noEnabled.body);

         (int status, string body) badDate = Http("PUT", "/api/v1/me/vacation", UserHeader(UserPassword),
            "{\"enabled\":true,\"expires\":true,\"expires_date\":\"31/12/2099\"}");
         Assert.AreEqual(400, badDate.status, "Body: " + badDate.body);
         StringAssert.Contains("YYYY-MM-DD", badDate.body);
      }

      [Test]
      [Description("POST /api/v1/me/password changes the password the account logs on with")]
      public void PasswordChangeRoundTrip()
      {
         const string newPassword = "Replacement-Passw0rd!";

         (int status, string body) changed = Http("POST", "/api/v1/me/password", UserHeader(UserPassword),
            "{\"current\":\"" + UserPassword + "\",\"new\":\"" + newPassword + "\"}");

         Assert.AreEqual(200, changed.status, "Body: " + changed.body);

         (int status, string body) oldRefused = Http("GET", "/api/v1/me", UserHeader(UserPassword));
         Assert.AreEqual(401, oldRefused.status, "The old password must no longer log on. Body: " + oldRefused.body);

         (int status, string body) newAccepted = Http("GET", "/api/v1/me", UserHeader(newPassword));
         Assert.AreEqual(200, newAccepted.status, "The new password must log on. Body: " + newAccepted.body);

         // The stamp the account carries, which the page shows.
         StringAssert.Contains("\"password_changed\":\"20", newAccepted.body);

         // And what a mail client sees: a message delivered, then collected
         // with the new password.
         SmtpClientSimulator.StaticSend("test@test.com", Address, "After the change", "Collected with the new password.");
         Pop3ClientSimulator.AssertMessageCount(Address, newPassword, 1);
      }

      [Test]
      [Description("The current password has to be the account password itself, and must match")]
      public void WrongCurrentPasswordIsRefused()
      {
         (int status, string body) refused = Http("POST", "/api/v1/me/password", UserHeader(UserPassword),
            "{\"current\":\"not-it\",\"new\":\"Another-Passw0rd!\"}");

         Assert.AreEqual(403, refused.status, "Body: " + refused.body);

         (int status, string body) still = Http("GET", "/api/v1/me", UserHeader(UserPassword));
         Assert.AreEqual(200, still.status, "The password must be unchanged. Body: " + still.body);

         (int status, string body) missing = Http("POST", "/api/v1/me/password", UserHeader(UserPassword),
            "{\"new\":\"Another-Passw0rd!\"}");
         Assert.AreEqual(400, missing.status, "Body: " + missing.body);
      }

      [Test]
      [Description("The password policy that binds an administrator binds the account too")]
      public void PasswordPolicyIsApplied()
      {
         IniFileSetting.Write("PasswordPolicyMinimumLength", "16");
         _application.Reinitialize();

         try
         {
            (int status, string body) tooShort = Http("POST", "/api/v1/me/password", UserHeader(UserPassword),
               "{\"current\":\"" + UserPassword + "\",\"new\":\"Short-1!\"}");

            Assert.AreEqual(400, tooShort.status, "Body: " + tooShort.body);
            StringAssert.Contains("\"error\"", tooShort.body);

            (int status, string body) still = Http("GET", "/api/v1/me", UserHeader(UserPassword));
            Assert.AreEqual(200, still.status, "The password must be unchanged. Body: " + still.body);
         }
         finally
         {
            IniFileSetting.Write("PasswordPolicyMinimumLength", "0");
            _application.Reinitialize();
         }
      }

      [Test]
      [Description("The sign-in page and its script are served without authentication, with a policy that allows no inline script")]
      public void ThePortalIsServedWithItsPolicy()
      {
         Response page = Raw("GET", "/portal", null, null);
         Assert.AreEqual(200, page.Status, page.Body);
         StringAssert.StartsWith("text/html", page.Header("Content-Type"));
         StringAssert.Contains("script-src 'self'", page.Header("Content-Security-Policy"));
         StringAssert.Contains("frame-ancestors 'none'", page.Header("Content-Security-Policy"));
         Assert.AreEqual("no-store", page.Header("Cache-Control"));
         Assert.AreEqual("nosniff", page.Header("X-Content-Type-Options"));
         StringAssert.Contains("<script src=\"/portal.js\"></script>", page.Body);
         Assert.IsFalse(page.Body.Contains("onclick="), "No inline event handler: the policy would block it.");

         Response script = Raw("GET", "/portal.js", null, null);
         Assert.AreEqual(200, script.Status, script.Body);
         StringAssert.StartsWith("text/javascript", script.Header("Content-Type"));
         StringAssert.Contains("/api/v1/me", script.Body);
      }

      [Test]
      [Description("POST /api/v1/session turns the password into a cookie that authenticates the account's endpoints on its own")]
      public void ASessionCookieAuthenticatesWithoutThePassword()
      {
         Response started = Raw("POST", "/api/v1/session", UserHeader(UserPassword), null);
         Assert.AreEqual(201, started.Status, started.Body);
         StringAssert.Contains("\"address\":\"" + Address + "\"", started.Body);

         string setCookie = started.Header("Set-Cookie");
         StringAssert.StartsWith("hmailsession=", setCookie);
         StringAssert.Contains("HttpOnly", setCookie);
         StringAssert.Contains("SameSite=Strict", setCookie);
         StringAssert.Contains("Path=/", setCookie);
         Assert.IsFalse(setCookie.Contains("Secure"), "Over the plain loopback listener the cookie must not be marked Secure, or the browser would never send it.");

         string cookie = CookieOf(setCookie);
         Assert.AreEqual(64, cookie.Length, "A 32-byte token in hex.");

         Response me = Raw("GET", "/api/v1/me", null, null, "Cookie: hmailsession=" + cookie + "\r\n");
         Assert.AreEqual(200, me.Status, me.Body);
         StringAssert.Contains("\"address\":\"" + Address + "\"", me.Body);

         // The cookie is a credential for the account's own endpoints only.
         Response status = Raw("GET", "/api/v1/status", null, null, "Cookie: hmailsession=" + cookie + "\r\n");
         Assert.AreEqual(403, status.Status, status.Body);

         // A session cannot start another session: that takes the password.
         Response again = Raw("POST", "/api/v1/session", null, null, "Cookie: hmailsession=" + cookie + "\r\n");
         Assert.AreEqual(403, again.Status, again.Body);
      }

      [Test]
      [Description("A request that changes something on a session must carry the X-Requested-With header")]
      public void AChangeOnASessionNeedsTheHeader()
      {
         string cookie = SignIn();

         Response bare = Raw("PUT", "/api/v1/me/vacation", null,
            "{\"enabled\":true,\"subject\":\"s\",\"message\":\"m\"}",
            "Cookie: hmailsession=" + cookie + "\r\n");
         Assert.AreEqual(403, bare.Status, bare.Body);
         StringAssert.Contains("X-Requested-With", bare.Body);

         Account untouched = _domain.Accounts.ItemByAddress[Address];
         Assert.IsFalse(untouched.VacationMessageIsOn, "The refused request must have changed nothing.");

         Response withHeader = Raw("PUT", "/api/v1/me/vacation", null,
            "{\"enabled\":true,\"subject\":\"s\",\"message\":\"m\"}",
            "Cookie: hmailsession=" + cookie + "\r\nX-Requested-With: hMailServer\r\n");
         Assert.AreEqual(200, withHeader.Status, withHeader.Body);

         Account changed = _domain.Accounts.ItemByAddress[Address];
         Assert.IsTrue(changed.VacationMessageIsOn);

         // A password never needed the header: it is not something a browser
         // sends on its own.
         (int status, string body) reset = Http("PUT", "/api/v1/me/vacation", UserHeader(UserPassword),
            "{\"enabled\":false,\"subject\":\"\",\"message\":\"\"}");
         Assert.AreEqual(200, reset.status, reset.body);
      }

      [Test]
      [Description("DELETE /api/v1/session ends the session, and a garbage or ended cookie is refused")]
      public void SigningOutEndsTheSession()
      {
         string cookie = SignIn();

         Response ended = Raw("DELETE", "/api/v1/session", null, null,
            "Cookie: hmailsession=" + cookie + "\r\nX-Requested-With: hMailServer\r\n");
         Assert.AreEqual(200, ended.Status, ended.Body);
         StringAssert.Contains("Max-Age=0", ended.Header("Set-Cookie"));

         Response afterwards = Raw("GET", "/api/v1/me", null, null, "Cookie: hmailsession=" + cookie + "\r\n");
         Assert.AreEqual(401, afterwards.Status, afterwards.Body);

         Response garbage = Raw("GET", "/api/v1/me", null, null, "Cookie: hmailsession=deadbeef; other=1\r\n");
         Assert.AreEqual(401, garbage.Status, garbage.Body);

         Response noSession = Raw("DELETE", "/api/v1/session", UserHeader(UserPassword), null);
         Assert.AreEqual(400, noSession.Status, noSession.Body);
      }

      [Test]
      [Description("A password change ends the account's other sessions and keeps the one that made it")]
      public void APasswordChangeEndsOtherSessions()
      {
         const string newPassword = "Rotated-Passw0rd!";

         string mine = SignIn();
         string theirs = SignIn();

         Response changed = Raw("POST", "/api/v1/me/password", null,
            "{\"current\":\"" + UserPassword + "\",\"new\":\"" + newPassword + "\"}",
            "Cookie: hmailsession=" + mine + "\r\nX-Requested-With: hMailServer\r\n");
         Assert.AreEqual(200, changed.Status, changed.Body);

         Response theirsAfter = Raw("GET", "/api/v1/me", null, null, "Cookie: hmailsession=" + theirs + "\r\n");
         Assert.AreEqual(401, theirsAfter.Status, "The other session must have been ended. " + theirsAfter.Body);

         Response mineAfter = Raw("GET", "/api/v1/me", null, null, "Cookie: hmailsession=" + mine + "\r\n");
         Assert.AreEqual(200, mineAfter.Status, "The session that changed the password stays. " + mineAfter.Body);

         (int status, string body) newAccepted = Http("GET", "/api/v1/me", UserHeader(newPassword));
         Assert.AreEqual(200, newAccepted.status, newAccepted.body);
      }

      // ------------------------------------------------------------ helpers ---

      private string SignIn()
      {
         Response started = Raw("POST", "/api/v1/session", UserHeader(UserPassword), null);
         Assert.AreEqual(201, started.Status, started.Body);
         return CookieOf(started.Header("Set-Cookie"));
      }

      private static string CookieOf(string setCookie)
      {
         int start = setCookie.IndexOf('=') + 1;
         int end = setCookie.IndexOf(';', start);
         return end > start ? setCookie.Substring(start, end - start) : setCookie.Substring(start);
      }

      private static string AdminHeader()
      {
         return BasicHeader("Administrator", AdminPassword);
      }

      private string UserHeader(string password)
      {
         return BasicHeader(Address, password);
      }

      private static string BasicHeader(string user, string password)
      {
         return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
      }

      private sealed class Response
      {
         public int Status;
         public string Body = "";
         public readonly System.Collections.Generic.Dictionary<string, string> Headers =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

         public string Header(string name)
         {
            string value;
            return Headers.TryGetValue(name, out value) ? value : "";
         }
      }

      private static (int status, string body) Http(string method, string path, string authorization, string requestBody = null)
      {
         Response response = Raw(method, path, authorization, requestBody);
         return (response.Status, response.Body);
      }

      private static Response Raw(string method, string path, string authorization, string requestBody, string extraHeaders = null)
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
               if (extraHeaders != null)
                  headers.Append(extraHeaders);

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

               byte[] buffer = new byte[8192];
               int read;
               while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                  memory.Write(buffer, 0, read);

               string raw = Encoding.UTF8.GetString(memory.ToArray());
               var response = new Response();

               int separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
               string head = separator >= 0 ? raw.Substring(0, separator) : raw;
               response.Body = separator >= 0 ? raw.Substring(separator + 4) : "";

               string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
               if (lines.Length > 0)
               {
                  string[] parts = lines[0].Split(' ');
                  if (parts.Length >= 2)
                     int.TryParse(parts[1], out response.Status);
               }

               for (int i = 1; i < lines.Length; i++)
               {
                  int colon = lines[i].IndexOf(':');
                  if (colon > 0)
                     response.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
               }

               return response;
            }
         }
      }
   }
}
