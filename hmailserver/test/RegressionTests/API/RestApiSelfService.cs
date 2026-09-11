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
using RegressionTests.Infrastructure;
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
         StringAssert.Contains("frame-src 'self'", page.Header("Content-Security-Policy"));
         StringAssert.Contains("<iframe id=\"message-html\" sandbox=\"allow-popups allow-popups-to-escape-sandbox\"", page.Body);
         StringAssert.Contains("img-src data:", page.Header("Content-Security-Policy"), "The srcdoc frame inherits this policy, and the message's own data-URI images need it.");
         StringAssert.Contains("<input id=\"compose-bcc\"", page.Body);
         StringAssert.Contains("<button id=\"message-reply-all\"", page.Body);
         StringAssert.Contains("<div id=\"bulk-bar\" hidden>", page.Body);
         Assert.AreEqual("no-store", page.Header("Cache-Control"));
         Assert.AreEqual("nosniff", page.Header("X-Content-Type-Options"));
         StringAssert.Contains("<script src=\"/portal.js\"></script>", page.Body);
         Assert.IsFalse(page.Body.Contains("onclick="), "No inline event handler: the policy would block it.");

         Response script = Raw("GET", "/portal.js", null, null);
         Assert.AreEqual(200, script.Status, script.Body);
         StringAssert.StartsWith("text/javascript", script.Header("Content-Type"));
         StringAssert.Contains("/api/v1/me", script.Body);
         StringAssert.Contains("/api/v1/me/folders", script.Body);
         StringAssert.Contains("srcdoc", script.Body);
         StringAssert.Contains("img-src data:", script.Body);
         StringAssert.Contains("carryAttachments", script.Body);
         StringAssert.Contains("before_uid", script.Body);
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

      // ---------------------------------------------------------- quarantine ---

      private const string SurblTestPoint = "surbl-org-permanent-test-point.com.multi.surbl.org";
      private const string SpamBody =
         "This is a test message with a SURBL url: -> http://surbl-org-permanent-test-point.com/ <-";

      [OneTimeSetUp]
      public void ServeTheSurblTestPoint()
      {
         SuiteDns.Zone.WithA(SurblTestPoint, "127.0.0.2");
      }

      [OneTimeTearDown]
      public void RestoreTheSuiteDns()
      {
         SuiteDns.Reset();
      }

      // The recipe AntiSpam.Quarantine uses: the SURBL test point scores 10,
      // the delete threshold is 5, and with QuarantineEnabled the message is
      // held instead of refused.
      private void EnableQuarantine()
      {
         var antiSpam = _application.Settings.AntiSpam;
         antiSpam.SpamMarkThreshold = 1;
         antiSpam.SpamDeleteThreshold = 5;

         var surbl = antiSpam.SURBLServers[0];
         surbl.Active = true;
         surbl.Score = 10;
         surbl.Save();

         EmptyTheQuarantine();

         ServerIniFile.SetSetting("QuarantineEnabled", "1");
         _application.Reinitialize();
      }

      private void DisableQuarantine()
      {
         EmptyTheQuarantine();
         ServerIniFile.SetSetting("QuarantineEnabled", null);
         _application.Reinitialize();

         var surbl = _application.Settings.AntiSpam.SURBLServers[0];
         surbl.Active = false;
         surbl.Save();
      }

      private void EmptyTheQuarantine()
      {
         var quarantine = _application.Settings.AntiSpam.Quarantine;
         quarantine.Refresh();
         while (quarantine.Count > 0)
         {
            quarantine.DeleteByDBID(quarantine[0].ID);
            quarantine.Refresh();
         }
      }

      private static void SendSpam(string subject, params string[] recipients)
      {
         var socket = new TcpConnection();
         Assert.IsTrue(socket.Connect(25), "Could not connect to SMTP.");
         socket.ReadUntil("220");
         socket.Send("HELO test\r\n");
         socket.ReadUntil("250");
         socket.Send("MAIL FROM:<outsider@example.com>\r\n");
         socket.ReadUntil("\r\n");
         foreach (string recipient in recipients)
         {
            socket.Send("RCPT TO:<" + recipient + ">\r\n");
            socket.ReadUntil("\r\n");
         }
         socket.Send("DATA\r\n");
         socket.ReadUntil("\r\n");
         socket.Send("From: outsider@example.com\r\n" +
                     "To: " + string.Join(", ", recipients) + "\r\n" +
                     "Subject: " + subject + "\r\n" +
                     "\r\n" +
                     SpamBody + "\r\n" +
                     ".\r\n");
         string reply = socket.ReadUntil("\r\n");
         socket.Send("QUIT\r\n");
         socket.Disconnect();
         StringAssert.StartsWith("250", reply, "A quarantined message is accepted, not refused.");
      }

      private static long FirstHeldId(string listBody)
      {
         int at = listBody.IndexOf("\"id\":", StringComparison.Ordinal);
         Assert.IsTrue(at >= 0, "No held message in: " + listBody);
         int start = at + 5;
         int end = start;
         while (end < listBody.Length && char.IsDigit(listBody[end]))
            end++;
         return long.Parse(listBody.Substring(start, end - start));
      }

      [Test]
      [Description("A held message is listed for its recipient only, and releasing it delivers it to that recipient")]
      public void HeldMessagesAreListedAndReleasedForTheirRecipientOnly()
      {
         const string otherPassword = "Other-Passw0rd!";
         string other = "other@" + _domain.Name;
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, other, otherPassword);

         EnableQuarantine();
         try
         {
            SendSpam("Held for self", Address);

            (int status, string body) mine = Http("GET", "/api/v1/me/quarantine", UserHeader(UserPassword));
            Assert.AreEqual(200, mine.status, mine.body);
            StringAssert.Contains("\"enabled\":true", mine.body);
            StringAssert.Contains("\"subject\":\"Held for self\"", mine.body);
            StringAssert.DoesNotContain("recipients", mine.body);
            long id = FirstHeldId(mine.body);

            (int status, string body) theirs = Http("GET", "/api/v1/me/quarantine", BasicHeader(other, otherPassword));
            Assert.AreEqual(200, theirs.status, theirs.body);
            StringAssert.Contains("\"messages\":[]", theirs.body);

            (int status, string body) theirRelease = Http("POST", "/api/v1/me/quarantine/" + id + "/release", BasicHeader(other, otherPassword));
            Assert.AreEqual(404, theirRelease.status, "Somebody else's held message must look like no message at all. " + theirRelease.body);

            (int status, string body) release = Http("POST", "/api/v1/me/quarantine/" + id + "/release", UserHeader(UserPassword));
            Assert.AreEqual(200, release.status, release.body);

            Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);

            (int status, string body) after = Http("GET", "/api/v1/me/quarantine", UserHeader(UserPassword));
            StringAssert.Contains("\"messages\":[]", after.body);

            (int status, string body) gone = Http("POST", "/api/v1/me/quarantine/" + id + "/release", UserHeader(UserPassword));
            Assert.AreEqual(404, gone.status, gone.body);
         }
         finally
         {
            DisableQuarantine();
         }
      }

      [Test]
      [Description("A message held for two recipients is released or discarded for one of them without touching the other's copy")]
      public void OneRecipientsActionLeavesTheOthersCopy()
      {
         const string otherPassword = "Other-Passw0rd!";
         string other = "other@" + _domain.Name;
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, other, otherPassword);

         EnableQuarantine();
         try
         {
            SendSpam("Held for both", Address, other);

            (int status, string body) mine = Http("GET", "/api/v1/me/quarantine", UserHeader(UserPassword));
            long id = FirstHeldId(mine.body);

            (int status, string body) theirs = Http("GET", "/api/v1/me/quarantine", BasicHeader(other, otherPassword));
            StringAssert.Contains("\"subject\":\"Held for both\"", theirs.body);

            // I give mine up: nothing is delivered to me, and theirs is still held.
            (int status, string body) discard = Http("DELETE", "/api/v1/me/quarantine/" + id, UserHeader(UserPassword));
            Assert.AreEqual(200, discard.status, discard.body);

            (int status, string body) mineAfter = Http("GET", "/api/v1/me/quarantine", UserHeader(UserPassword));
            StringAssert.Contains("\"messages\":[]", mineAfter.body);

            (int status, string body) theirsAfter = Http("GET", "/api/v1/me/quarantine", BasicHeader(other, otherPassword));
            StringAssert.Contains("\"subject\":\"Held for both\"", theirsAfter.body);

            // They release theirs: delivered to them only, and the entry is gone.
            (int status, string body) release = Http("POST", "/api/v1/me/quarantine/" + id + "/release", BasicHeader(other, otherPassword));
            Assert.AreEqual(200, release.status, release.body);

            Pop3ClientSimulator.AssertMessageCount(other, otherPassword, 1);
            Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 0);

            var quarantine = _application.Settings.AntiSpam.Quarantine;
            quarantine.Refresh();
            Assert.AreEqual(0, quarantine.Count, "The entry goes with its last recipient.");
         }
         finally
         {
            DisableQuarantine();
         }
      }

      // ------------------------------------------------------- the mailbox ---

      private static void Deliver(string to, string subject, string body)
      {
         SmtpClientSimulator.StaticSend("sender@example.com", to, subject, body);
      }

      private static long NumberAt(string body, int start)
      {
         int end = start;
         while (end < body.Length && char.IsDigit(body[end]))
            end++;
         return long.Parse(body.Substring(start, end - start));
      }

      // The id of the object a marker sits in: the last "id" before it.
      private static long IdBefore(string body, string marker)
      {
         int at = body.IndexOf(marker, StringComparison.Ordinal);
         Assert.IsTrue(at >= 0, "Missing " + marker + " in: " + body);
         int idAt = body.LastIndexOf("\"id\":", at, StringComparison.Ordinal);
         Assert.IsTrue(idAt >= 0, "No id before " + marker + " in: " + body);
         return NumberAt(body, idAt + 5);
      }

      [Test]
      public void ContactsRoundTrip()
      {
         (int status, string body) empty = Http("GET", "/api/v1/me/contacts", UserHeader(UserPassword));
         Assert.AreEqual(200, empty.status, "Body: " + empty.body);
         StringAssert.Contains("\"contacts\":[]", empty.body);

         (int status, string body) created = Http("POST", "/api/v1/me/contacts", UserHeader(UserPassword),
            "{\"name\":\"Alice Example\",\"address\":\"Alice@Example.com\"}");
         Assert.AreEqual(201, created.status, "Body: " + created.body);
         StringAssert.Contains("\"address\":\"alice@example.com\"", created.body);
         StringAssert.Contains("\"source\":\"manual\"", created.body);
         long id = long.Parse(Between(created.body, "\"id\":", ","));
         Assert.Greater(id, 0);

         (int status, string body) duplicate = Http("POST", "/api/v1/me/contacts", UserHeader(UserPassword),
            "{\"address\":\"alice@example.com\"}");
         Assert.AreEqual(409, duplicate.status, "Body: " + duplicate.body);

         (int status, string body) malformed = Http("POST", "/api/v1/me/contacts", UserHeader(UserPassword),
            "{\"address\":\"not an address\"}");
         Assert.AreEqual(400, malformed.status, "Body: " + malformed.body);

         (int status, string body) renamed = Http("PUT", "/api/v1/me/contacts/" + id, UserHeader(UserPassword),
            "{\"name\":\"Alice Renamed\"}");
         Assert.AreEqual(200, renamed.status, "Body: " + renamed.body);
         StringAssert.Contains("\"name\":\"Alice Renamed\"", renamed.body);

         (int status, string body) listed = Http("GET", "/api/v1/me/contacts", UserHeader(UserPassword));
         Assert.AreEqual(200, listed.status, "Body: " + listed.body);
         StringAssert.Contains("\"name\":\"Alice Renamed\"", listed.body);
         StringAssert.Contains("\"count\":1", listed.body);

         (int status, string body) deleted = Http("DELETE", "/api/v1/me/contacts/" + id, UserHeader(UserPassword));
         Assert.AreEqual(200, deleted.status, "Body: " + deleted.body);

         (int status, string body) again = Http("DELETE", "/api/v1/me/contacts/" + id, UserHeader(UserPassword));
         Assert.AreEqual(404, again.status, "Body: " + again.body);
      }

      [Test]
      public void ContactsCompletionFiltersByText()
      {
         foreach (string entry in new[] { "{\"name\":\"Alice Example\",\"address\":\"alice@example.com\"}",
                                          "{\"name\":\"Bob Builder\",\"address\":\"bob@example.com\"}",
                                          "{\"name\":\"Carol\",\"address\":\"carol@somewhere.test\"}" })
         {
            (int status, string body) created = Http("POST", "/api/v1/me/contacts", UserHeader(UserPassword), entry);
            Assert.AreEqual(201, created.status, "Body: " + created.body);
         }

         (int status, string body) byName = Http("GET", "/api/v1/me/contacts?q=ALI", UserHeader(UserPassword));
         Assert.AreEqual(200, byName.status, "Body: " + byName.body);
         StringAssert.Contains("alice@example.com", byName.body);
         StringAssert.DoesNotContain("bob@example.com", byName.body);
         StringAssert.Contains("\"count\":1", byName.body);

         (int status, string body) byDomain = Http("GET", "/api/v1/me/contacts?q=example.com&limit=1", UserHeader(UserPassword));
         Assert.AreEqual(200, byDomain.status, "Body: " + byDomain.body);
         StringAssert.Contains("\"count\":1", byDomain.body);
         StringAssert.Contains("\"total\":2", byDomain.body);
      }

      [Test]
      public void ContactsAreCollectedFromSentRecipients()
      {
         string other = "other@" + _domain.Name;
         string third = "third@" + _domain.Name;
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, other, UserPassword);
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, third, UserPassword);

         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.IsTrue(imap.CreateFolder("Sent"));
         imap.Disconnect();

         (int status, string body) sent = Http("POST", "/api/v1/me/messages", UserHeader(UserPassword),
            "{\"to\":\"Other Person <" + other + ">\",\"cc\":\"" + third + "\",\"subject\":\"Collected\",\"text\":\"Body.\"}");
         Assert.AreEqual(201, sent.status, "Body: " + sent.body);

         (int status, string body) listed = Http("GET", "/api/v1/me/contacts", UserHeader(UserPassword));
         Assert.AreEqual(200, listed.status, "Body: " + listed.body);
         StringAssert.Contains("\"name\":\"Other Person\",\"address\":\"" + other + "\",\"source\":\"collected\"", listed.body);
         StringAssert.Contains("\"address\":\"" + third + "\",\"source\":\"collected\"", listed.body);
         StringAssert.DoesNotContain(Address, listed.body);
         StringAssert.Contains("\"count\":2", listed.body);

         // Sending again adds nothing: one row per address.
         (int status, string body) sentAgain = Http("POST", "/api/v1/me/messages", UserHeader(UserPassword),
            "{\"to\":\"" + other + "\",\"subject\":\"Again\",\"text\":\"Body.\"}");
         Assert.AreEqual(201, sentAgain.status, "Body: " + sentAgain.body);
         (int status, string body) still = Http("GET", "/api/v1/me/contacts", UserHeader(UserPassword));
         StringAssert.Contains("\"count\":2", still.body);
      }

      [Test]
      public void ContactsAreTheAccountsOwn()
      {
         string other = "other@" + _domain.Name;
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, other, UserPassword);

         (int status, string body) created = Http("POST", "/api/v1/me/contacts", UserHeader(UserPassword),
            "{\"name\":\"Mine\",\"address\":\"mine@example.com\"}");
         Assert.AreEqual(201, created.status, "Body: " + created.body);
         long id = long.Parse(Between(created.body, "\"id\":", ","));

         (int status, string body) theirs = Http("GET", "/api/v1/me/contacts", BasicHeader(other, UserPassword));
         Assert.AreEqual(200, theirs.status, "Body: " + theirs.body);
         StringAssert.DoesNotContain("mine@example.com", theirs.body);

         (int status, string body) theirDelete = Http("DELETE", "/api/v1/me/contacts/" + id, BasicHeader(other, UserPassword));
         Assert.AreEqual(404, theirDelete.status, "Body: " + theirDelete.body);

         (int status, string body) theirRename = Http("PUT", "/api/v1/me/contacts/" + id, BasicHeader(other, UserPassword), "{\"name\":\"Stolen\"}");
         Assert.AreEqual(404, theirRename.status, "Body: " + theirRename.body);

         (int status, string body) mine = Http("GET", "/api/v1/me/contacts", UserHeader(UserPassword));
         StringAssert.Contains("\"name\":\"Mine\"", mine.body);
      }

      [Test]
      public void PreferencesRoundTrip()
      {
         (int status, string body) empty = Http("GET", "/api/v1/me/preferences", UserHeader(UserPassword));
         Assert.AreEqual(200, empty.status, "Body: " + empty.body);
         Assert.AreEqual("{\"preferences\":{}}", empty.body);

         (int status, string body) saved = Http("PUT", "/api/v1/me/preferences", UserHeader(UserPassword),
            "{\"theme\":\"light\",\"density\":\"compact\",\"undo_seconds\":\"10\"}");
         Assert.AreEqual(200, saved.status, "Body: " + saved.body);
         StringAssert.Contains("\"density\":\"compact\"", saved.body);
         StringAssert.Contains("\"theme\":\"light\"", saved.body);

         // A later PUT merges: one key changed, one removed, the third untouched.
         (int status, string body) changed = Http("PUT", "/api/v1/me/preferences", UserHeader(UserPassword),
            "{\"theme\":\"dark\",\"density\":null}");
         Assert.AreEqual(200, changed.status, "Body: " + changed.body);
         Assert.AreEqual("{\"preferences\":{\"theme\":\"dark\",\"undo_seconds\":\"10\"}}", changed.body);

         (int status, string body) read = Http("GET", "/api/v1/me/preferences", UserHeader(UserPassword));
         Assert.AreEqual(changed.body, read.body);

         (int status, string body) badKey = Http("PUT", "/api/v1/me/preferences", UserHeader(UserPassword), "{\"bad key!\":\"x\"}");
         Assert.AreEqual(400, badKey.status, "Body: " + badKey.body);
         (int status, string body) badValue = Http("PUT", "/api/v1/me/preferences", UserHeader(UserPassword), "{\"theme\":1}");
         Assert.AreEqual(400, badValue.status, "Body: " + badValue.body);
         (int status, string body) notAnObject = Http("PUT", "/api/v1/me/preferences", UserHeader(UserPassword), "[1,2]");
         Assert.AreEqual(400, notAnObject.status, "Body: " + notAnObject.body);
         (int status, string body) tooLong = Http("PUT", "/api/v1/me/preferences", UserHeader(UserPassword),
            "{\"theme\":\"" + new string('x', 4001) + "\"}");
         Assert.AreEqual(400, tooLong.status, "Body: " + tooLong.body);

         // A refused body changes nothing.
         (int status, string body) still = Http("GET", "/api/v1/me/preferences", UserHeader(UserPassword));
         Assert.AreEqual(read.body, still.body);
      }

      [Test]
      [Description("A preferences request with more than a hundred members, or one that would leave more than a hundred keys, is refused before anything is written")]
      public void PreferencesAreCappedBeforeAnythingIsWritten()
      {
         var many = new StringBuilder("{");
         for (int i = 0; i < 101; i++)
            many.Append((i > 0 ? "," : "") + "\"k" + i + "\":\"v\"");
         many.Append("}");
         (int status, string body) tooMany = Http("PUT", "/api/v1/me/preferences", UserHeader(UserPassword), many.ToString());
         Assert.AreEqual(400, tooMany.status, "Body: " + tooMany.body);
         StringAssert.Contains("members", tooMany.body);

         // Ninety-nine keys fit; a request for three more is refused whole,
         // and the two that would have fitted are not written either.
         var fill = new StringBuilder("{");
         for (int i = 0; i < 99; i++)
            fill.Append((i > 0 ? "," : "") + "\"fill" + i + "\":\"v\"");
         fill.Append("}");
         (int status, string body) filled = Http("PUT", "/api/v1/me/preferences", UserHeader(UserPassword), fill.ToString());
         Assert.AreEqual(200, filled.status, "Body: " + filled.body);
         (int status, string body) over = Http("PUT", "/api/v1/me/preferences", UserHeader(UserPassword), "{\"extra1\":\"v\",\"extra2\":\"v\",\"extra3\":\"v\"}");
         Assert.AreEqual(400, over.status, "Body: " + over.body);
         (int status, string body) after = Http("GET", "/api/v1/me/preferences", UserHeader(UserPassword));
         Assert.IsFalse(after.body.Contains("\"extra1\""), "Nothing of a refused request is written. Body: " + after.body);
         Assert.AreEqual(99, after.body.Split(new[] { "\"fill" }, StringSplitOptions.None).Length - 1, after.body);

         // Removing one and adding one in the same request stays within the cap.
         (int status, string body) swap = Http("PUT", "/api/v1/me/preferences", UserHeader(UserPassword), "{\"fill0\":null,\"swapped\":\"v\",\"last\":\"v\"}");
         Assert.AreEqual(200, swap.status, "Body: " + swap.body);
      }

      [Test]
      public void PreferencesAreTheAccountsOwn()
      {
         string other = "other@" + _domain.Name;
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, other, UserPassword);

         (int status, string body) saved = Http("PUT", "/api/v1/me/preferences", UserHeader(UserPassword), "{\"theme\":\"light\"}");
         Assert.AreEqual(200, saved.status, "Body: " + saved.body);

         (int status, string body) theirs = Http("GET", "/api/v1/me/preferences", BasicHeader(other, UserPassword));
         Assert.AreEqual(200, theirs.status, "Body: " + theirs.body);
         Assert.AreEqual("{\"preferences\":{}}", theirs.body);

         (int status, string body) theirSave = Http("PUT", "/api/v1/me/preferences", BasicHeader(other, UserPassword), "{\"theme\":\"dark\"}");
         Assert.AreEqual(200, theirSave.status, "Body: " + theirSave.body);

         (int status, string body) mine = Http("GET", "/api/v1/me/preferences", UserHeader(UserPassword));
         Assert.AreEqual("{\"preferences\":{\"theme\":\"light\"}}", mine.body);
      }

      [Test]
      [Description("The identities are the account, every alias that resolves to it, and every account whose INBOX grants it the post right - the SMTP rule, listed")]
      public void IdentitiesListTheAccountItsAliasesAndTheGrants()
      {
         string sales = "sales@" + _domain.Name;
         Alias alias = _domain.Aliases.Add();
         alias.Name = sales;
         alias.Value = Address;
         alias.Active = true;
         alias.Save();

         string shop = "shop@" + _domain.Name;
         Alias chained = _domain.Aliases.Add();
         chained.Name = shop;
         chained.Value = sales;
         chained.Active = true;
         chained.Save();

         string boss = "boss@" + _domain.Name;
         Account owner = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, boss, UserPassword);
         SmtpClientSimulator.StaticSend("seed@example.com", boss, "Seed", "Creates the INBOX.");
         Pop3ClientSimulator.AssertMessageCount(boss, UserPassword, 1);

         IMAPFolder inbox = owner.IMAPFolders.get_ItemByName("INBOX");
         IMAPFolderPermission grant = inbox.Permissions.Add();
         grant.PermissionType = eACLPermissionType.ePermissionTypeUser;
         grant.PermissionAccountID = _account.ID;
         grant.set_Permission(eACLPermission.ePermissionPost, true);
         grant.Save();

         bool enforced = _settings.IMAPACLEnabled;
         _settings.IMAPACLEnabled = true;
         try
         {
            (int status, string body) listed = Http("GET", "/api/v1/me/identities", UserHeader(UserPassword));
            Assert.AreEqual(200, listed.status, "Body: " + listed.body);
            StringAssert.StartsWith("{\"identities\":[{\"address\":\"" + Address + "\",\"name\":\"\",\"kind\":\"account\"", listed.body);
            StringAssert.Contains("{\"address\":\"" + sales + "\",\"name\":\"\",\"kind\":\"alias\",\"header\":\"" + sales + "\"}", listed.body);
            StringAssert.Contains("{\"address\":\"" + shop + "\",\"name\":\"\",\"kind\":\"alias\"", listed.body);
            StringAssert.Contains("{\"address\":\"" + boss + "\",\"name\":\"\",\"kind\":\"granted\"", listed.body);

            // The grant is the owner's, not the grantee's: the owner lists only itself.
            (int status, string body) theirs = Http("GET", "/api/v1/me/identities", BasicHeader(boss, UserPassword));
            Assert.AreEqual(200, theirs.status, "Body: " + theirs.body);
            StringAssert.DoesNotContain(Address, theirs.body);
            StringAssert.DoesNotContain(sales, theirs.body);
         }
         finally
         {
            _settings.IMAPACLEnabled = enforced;
         }

         // With enforcement off nothing is granted, as at MAIL FROM; the aliases stay.
         _settings.IMAPACLEnabled = false;
         try
         {
            (int status, string body) without = Http("GET", "/api/v1/me/identities", UserHeader(UserPassword));
            Assert.AreEqual(200, without.status, "Body: " + without.body);
            StringAssert.DoesNotContain(boss, without.body);
            StringAssert.Contains(sales, without.body);
         }
         finally
         {
            _settings.IMAPACLEnabled = enforced;
         }
      }

      [Test]
      [Description("A send or a draft may name one of the identities as its From, with the account's name or one written in the field; anything else is 403, and something that is not an address is 400")]
      public void SendingAsAnIdentityIsHonouredAndAsAStrangerRefused()
      {
         string sales = "sales@" + _domain.Name;
         Alias alias = _domain.Aliases.Add();
         alias.Name = sales;
         alias.Value = Address;
         alias.Active = true;
         alias.Save();

         string other = "other@" + _domain.Name;
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, other, UserPassword);

         (int status, string body) named = Http("PUT", "/api/v1/me/settings", UserHeader(UserPassword),
            "{\"name\":{\"first\":\"Sales\",\"last\":\"Desk\"}}");
         Assert.AreEqual(200, named.status, "Body: " + named.body);

         (int status, string body) sent = Http("POST", "/api/v1/me/messages", UserHeader(UserPassword),
            "{\"from\":\"" + sales + "\",\"to\":\"" + other + "\",\"subject\":\"As sales\",\"text\":\"Body.\"}");
         Assert.AreEqual(201, sent.status, "Body: " + sent.body);
         string received = Pop3ClientSimulator.AssertGetFirstMessageText(other, UserPassword);
         StringAssert.Contains("From: \"Sales Desk\" <" + sales + ">", received);

         (int status, string body) refused = Http("POST", "/api/v1/me/messages", UserHeader(UserPassword),
            "{\"from\":\"stranger@example.com\",\"to\":\"" + other + "\",\"subject\":\"Not mine\",\"text\":\"Body.\"}");
         Assert.AreEqual(403, refused.status, "Body: " + refused.body);
         StringAssert.Contains("stranger@example.com", refused.body);

         (int status, string body) malformed = Http("POST", "/api/v1/me/messages", UserHeader(UserPassword),
            "{\"from\":\"not an address\",\"to\":\"" + other + "\",\"subject\":\"Not one\",\"text\":\"Body.\"}");
         Assert.AreEqual(400, malformed.status, "Body: " + malformed.body);

         // A name with a line break in it would end the From header; refused.
         (int status, string body) folded = Http("POST", "/api/v1/me/messages", UserHeader(UserPassword),
            "{\"from\":\"Sales\\r\\nBcc: x@example.com <" + sales + ">\",\"to\":\"" + other + "\",\"subject\":\"Folded\",\"text\":\"Body.\"}");
         Assert.AreEqual(400, folded.status, "Body: " + folded.body);
         StringAssert.Contains("control characters", folded.body);

         // A name with a quote and a backslash is escaped in the quoted-string.
         (int status, string body) quoted = Http("POST", "/api/v1/me/messages", UserHeader(UserPassword),
            "{\"from\":\"Say \\\"hi\\\" \\\\ Desk <" + sales + ">\",\"to\":\"" + other + "\",\"subject\":\"Quoted\",\"text\":\"Body.\"}");
         Assert.AreEqual(201, quoted.status, "Body: " + quoted.body);
         string quotedReceived = Pop3ClientSimulator.AssertGetFirstMessageText(other, UserPassword);
         StringAssert.Contains("From: \"Say \\\"hi\\\" \\\\ Desk\" <" + sales + ">", quotedReceived);

         // A subject that spells a key's name is a subject.
         (int status, string body) spelled = Http("POST", "/api/v1/me/messages", UserHeader(UserPassword),
            "{\"to\":\"" + other + "\",\"subject\":\"from\",\"text\":\"Body.\"}");
         Assert.AreEqual(201, spelled.status, "Body: " + spelled.body);
         string spelledReceived = Pop3ClientSimulator.AssertGetFirstMessageText(other, UserPassword);
         StringAssert.Contains("Subject: from\r\n", spelledReceived);
         StringAssert.Contains("From: \"Sales Desk\" <" + Address + ">", spelledReceived);

         // A draft keeps the chosen From, with the name written in the field.
         (int status, string body) draft = Http("POST", "/api/v1/me/drafts", UserHeader(UserPassword),
            "{\"from\":\"The Desk <" + sales + ">\",\"to\":\"" + other + "\",\"subject\":\"Draft as sales\",\"text\":\"Body.\"}");
         Assert.AreEqual(201, draft.status, "Body: " + draft.body);
         long draftId = long.Parse(Between(draft.body, "\"id\":", ","));
         (int status, string body) read = Http("GET", "/api/v1/me/messages/" + draftId, UserHeader(UserPassword));
         Assert.AreEqual(200, read.status, "Body: " + read.body);
         StringAssert.Contains("\\\"The Desk\\\" <" + sales + ">", read.body);

         // A granted identity: the owner's post right on their INBOX lets this
         // account send as them, and the From carries the owner's name.
         Account owner = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "owner@" + _domain.Name, UserPassword);
         owner.PersonFirstName = "Olive";
         owner.PersonLastName = "Owner";
         owner.Save();
         Deliver(owner.Address, "Seed", "Creates the INBOX.");
         Pop3ClientSimulator.AssertMessageCount(owner.Address, UserPassword, 1);
         var inbox = owner.IMAPFolders.get_ItemByName("INBOX");
         var grant = inbox.Permissions.Add();
         grant.PermissionType = eACLPermissionType.ePermissionTypeUser;
         grant.PermissionAccountID = _account.ID;
         grant.set_Permission(eACLPermission.ePermissionPost, true);
         grant.Save();

         (int status, string body) listed = Http("GET", "/api/v1/me/identities", UserHeader(UserPassword));
         Assert.AreEqual(200, listed.status, "Body: " + listed.body);
         StringAssert.Contains("\"address\":\"" + owner.Address + "\",\"name\":\"Olive Owner\",\"kind\":\"granted\"", listed.body);

         (int status, string body) asOwner = Http("POST", "/api/v1/me/messages", UserHeader(UserPassword),
            "{\"from\":\"" + owner.Address + "\",\"to\":\"" + other + "\",\"subject\":\"As the owner\",\"text\":\"Body.\"}");
         Assert.AreEqual(201, asOwner.status, "Body: " + asOwner.body);
         string asOwnerReceived = Pop3ClientSimulator.AssertGetFirstMessageText(other, UserPassword);
         StringAssert.Contains("From: \"Olive Owner\" <" + owner.Address + ">", asOwnerReceived);

         (int status, string body) draftRefused = Http("POST", "/api/v1/me/drafts", UserHeader(UserPassword),
            "{\"from\":\"stranger@example.com\",\"to\":\"" + other + "\",\"subject\":\"Not mine\",\"text\":\"Body.\"}");
         Assert.AreEqual(403, draftRefused.status, "Body: " + draftRefused.body);
      }

      private static string Between(string body, string after, string until)
      {
         int start = body.IndexOf(after, StringComparison.Ordinal);
         Assert.IsTrue(start >= 0, "Missing " + after + " in: " + body);
         start += after.Length;
         int end = body.IndexOf(until, start, StringComparison.Ordinal);
         Assert.IsTrue(end >= start, "Unterminated " + after + " in: " + body);
         return body.Substring(start, end - start);
      }

      // One folder's own fields, cut out of the tree by its path: from the
      // "{" that opens it to its subfolders, so an assertion about its counts
      // cannot read a child's or a neighbour's.
      private static string FolderEntry(string tree, string path)
      {
         int at = tree.IndexOf("\"path\":\"" + path + "\"", StringComparison.Ordinal);
         Assert.IsTrue(at >= 0, "No folder " + path + " in: " + tree);
         int start = tree.LastIndexOf('{', at);
         int end = tree.IndexOf("\"subfolders\":[", at, StringComparison.Ordinal);
         return tree.Substring(start, end - start);
      }

      // One message's entry in a listing, by its subject: from the "{" that
      // opens it to the "}}" that closes its flags.
      private static string EntryFor(string listing, string subject)
      {
         int at = listing.IndexOf("\"subject\":\"" + subject + "\"", StringComparison.Ordinal);
         Assert.IsTrue(at >= 0, "No entry for " + subject + " in: " + listing);
         int start = listing.LastIndexOf('{', at);
         int depth = 0;
         for (int i = start; i < listing.Length; i++)
         {
            if (listing[i] == '{')
               depth++;
            else if (listing[i] == '}' && --depth == 0)
               return listing.Substring(start, i + 1 - start);
         }
         Assert.Fail("Unterminated entry for " + subject + " in: " + listing);
         return null;
      }

      private string OtherAccount()
      {
         string address = "other@" + _domain.Name;
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, UserPassword);
         return address;
      }

      [Test]
      [Description("GET /api/v1/me/folders is the account's own folder tree with its counts, and nobody else's")]
      public void TheFolderTreeIsTheAccountsOwn()
      {
         Deliver(Address, "First things", "One.");
         Deliver(Address, "Second things", "Two.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 2);

         (int status, string body) before = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         Assert.AreEqual(200, before.status, "Body: " + before.body);
         string delimiter = Between(before.body, "\"delimiter\":\"", "\"");

         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.IsTrue(imap.CreateFolder("Projects" + delimiter + "Alpha"));
         imap.Disconnect();

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         Assert.AreEqual(200, tree.status, "Body: " + tree.body);

         string inbox = FolderEntry(tree.body, "INBOX");
         StringAssert.Contains("\"name\":\"INBOX\"", inbox);
         StringAssert.Contains("\"writable\":true", inbox);
         StringAssert.Contains("\"messages\":2,\"unseen\":2", inbox);

         string alpha = FolderEntry(tree.body, "Projects" + delimiter + "Alpha");
         StringAssert.Contains("\"name\":\"Alpha\"", alpha);
         long projectsId = IdBefore(tree.body, "\"path\":\"Projects\"");
         Assert.AreEqual(projectsId.ToString(), Between(alpha, "\"parent_id\":", ","), "Alpha's parent is Projects: " + tree.body);
         StringAssert.Contains("\"messages\":0,\"unseen\":0", alpha);

         (int status, string body) others = Http("GET", "/api/v1/me/folders", BasicHeader(OtherAccount(), UserPassword));
         Assert.AreEqual(200, others.status, "Body: " + others.body);
         Assert.IsFalse(others.body.Contains("Alpha"), "Another account's tree is its own: " + others.body);

         (int status, string body) admin = Http("GET", "/api/v1/me/folders", AdminHeader());
         Assert.AreEqual(403, admin.status, "Body: " + admin.body);
      }

      [Test]
      [Description("A folder's messages are listed newest first with their headers and live flags, paged, and to their owner only")]
      public void MessagesAreListedNewestFirstWithTheirFlags()
      {
         Deliver(Address, "First things", "One.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);
         Deliver(Address, "Second things", "Two.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 2);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");

         (int status, string body) page = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         Assert.AreEqual(200, page.status, "Body: " + page.body);
         StringAssert.Contains("\"folder_id\":" + inboxId + ",\"total\":2", page.body);
         Assert.Less(page.body.IndexOf("Second things", StringComparison.Ordinal), page.body.IndexOf("First things", StringComparison.Ordinal),
            "Newest first: " + page.body);

         string second = EntryFor(page.body, "Second things");
         StringAssert.Contains("\"from\":\"sender@example.com\"", second);
         StringAssert.Contains("\"seen\":false", second);
         long secondUid = long.Parse(Between(second, "\"uid\":", ","));

         (int status, string body) one = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages?limit=1", UserHeader(UserPassword));
         Assert.AreEqual(200, one.status, "Body: " + one.body);
         StringAssert.Contains("\"total\":2", one.body);
         StringAssert.Contains("Second things", one.body);
         Assert.IsFalse(one.body.Contains("First things"), "limit=1 is the newest one only: " + one.body);

         (int status, string body) older = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages?before_uid=" + secondUid, UserHeader(UserPassword));
         Assert.AreEqual(200, older.status, "Body: " + older.body);
         StringAssert.Contains("First things", older.body);
         Assert.IsFalse(older.body.Contains("Second things"), "before_uid pages past the newest: " + older.body);

         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.IsTrue(imap.SelectFolder("INBOX"));
         Assert.IsTrue(imap.SetFlagOnMessage(1, true, "\\Seen"));
         imap.Disconnect();

         (int status, string body) after = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         Assert.AreEqual(200, after.status, "Body: " + after.body);
         StringAssert.Contains("\"seen\":true", EntryFor(after.body, "First things"));
         StringAssert.Contains("\"seen\":false", EntryFor(after.body, "Second things"));

         (int status, string body) counts = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         StringAssert.Contains("\"messages\":2,\"unseen\":1", FolderEntry(counts.body, "INBOX"));

         (int status, string body) unknown = Http("GET", "/api/v1/me/folders/987654321/messages", UserHeader(UserPassword));
         Assert.AreEqual(404, unknown.status, "Body: " + unknown.body);

         (int status, string body) others = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", BasicHeader(OtherAccount(), UserPassword));
         Assert.AreEqual(404, others.status, "Another account's folder is not found, not forbidden. Body: " + others.body);
      }

      [Test]
      [Description("GET /api/v1/me/messages/{id} reads one message with its text, for the account it belongs to and no other")]
      public void AMessageIsReadByItsOwnerOnly()
      {
         Deliver(Address, "Numbers", "The quarterly numbers are attached in spirit.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         (int status, string body) page = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         long messageId = IdBefore(page.body, "\"subject\":\"Numbers\"");

         (int status, string body) message = Http("GET", "/api/v1/me/messages/" + messageId, UserHeader(UserPassword));
         Assert.AreEqual(200, message.status, "Body: " + message.body);
         StringAssert.Contains("\"subject\":\"Numbers\"", message.body);
         StringAssert.Contains("\"folder_id\":" + inboxId, message.body);
         StringAssert.Contains("\"from\":\"sender@example.com\"", message.body);
         StringAssert.Contains("\"to\":\"" + Address + "\"", message.body);
         StringAssert.Contains("\"truncated\":false", message.body);
         StringAssert.Contains("The quarterly numbers are attached in spirit.", message.body);
         StringAssert.Contains("\"attachments\":[]", message.body);
         StringAssert.Contains("\"seen\":false", message.body);

         (int status, string body) others = Http("GET", "/api/v1/me/messages/" + messageId, BasicHeader(OtherAccount(), UserPassword));
         Assert.AreEqual(404, others.status, "Another account's message is not found, not forbidden. Body: " + others.body);

         (int status, string body) unknown = Http("GET", "/api/v1/me/messages/987654321", UserHeader(UserPassword));
         Assert.AreEqual(404, unknown.status, "Body: " + unknown.body);

         (int status, string body) admin = Http("GET", "/api/v1/me/messages/" + messageId, AdminHeader());
         Assert.AreEqual(403, admin.status, "Body: " + admin.body);
      }

      [Test]
      [Description("PUT /api/v1/me/messages/{id}/flags changes the named flags and no other, and IMAP sees them")]
      public void FlagsAreChangedAndSeenOverImap()
      {
         Deliver(Address, "Numbers", "One.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         (int status, string body) page = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         long messageId = IdBefore(page.body, "\"subject\":\"Numbers\"");
         string flagsPath = "/api/v1/me/messages/" + messageId + "/flags";

         (int status, string body) nothing = Http("PUT", flagsPath, UserHeader(UserPassword), "{\"colour\":\"red\"}");
         Assert.AreEqual(400, nothing.status, "Body: " + nothing.body);

         (int status, string body) set = Http("PUT", flagsPath, UserHeader(UserPassword), "{\"seen\":true,\"flagged\":true}");
         Assert.AreEqual(200, set.status, "Body: " + set.body);
         StringAssert.Contains("\"seen\":true,\"flagged\":true", set.body);

         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.IsTrue(imap.SelectFolder("INBOX"));
         string flags = imap.GetFlags(1);
         StringAssert.Contains("\\Seen", flags);
         StringAssert.Contains("\\Flagged", flags);
         imap.Disconnect();

         (int status, string body) unseen = Http("PUT", flagsPath, UserHeader(UserPassword), "{\"seen\":false}");
         Assert.AreEqual(200, unseen.status, "Body: " + unseen.body);
         StringAssert.Contains("\"seen\":false,\"flagged\":true", unseen.body);

         imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.IsTrue(imap.SelectFolder("INBOX"));
         flags = imap.GetFlags(1);
         Assert.IsFalse(flags.Contains("\\Seen"), "Only the named flag changed: " + flags);
         StringAssert.Contains("\\Flagged", flags);
         imap.Disconnect();

         (int status, string body) counts = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         StringAssert.Contains("\"messages\":1,\"unseen\":1", FolderEntry(counts.body, "INBOX"));

         (int status, string body) others = Http("PUT", flagsPath, BasicHeader(OtherAccount(), UserPassword), "{\"seen\":true}");
         Assert.AreEqual(404, others.status, "Another account's message is not found, not forbidden. Body: " + others.body);
      }

      [Test]
      [Description("POST /api/v1/me/messages/{id}/move puts the message in another of the account's folders, as a new message, and only there")]
      public void AMessageIsMovedToAnotherFolderOfTheAccount()
      {
         Deliver(Address, "Numbers", "One.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);

         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.IsTrue(imap.CreateFolder("Projects"));
         imap.Disconnect();

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         long projectsId = IdBefore(tree.body, "\"path\":\"Projects\"");
         (int status, string body) page = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         long messageId = IdBefore(page.body, "\"subject\":\"Numbers\"");
         string movePath = "/api/v1/me/messages/" + messageId + "/move";

         (int status, string body) missing = Http("POST", movePath, UserHeader(UserPassword), "{}");
         Assert.AreEqual(400, missing.status, "Body: " + missing.body);

         (int status, string body) unknown = Http("POST", movePath, UserHeader(UserPassword), "{\"folder_id\":987654321}");
         Assert.AreEqual(404, unknown.status, "Body: " + unknown.body);

         (int status, string body) same = Http("POST", movePath, UserHeader(UserPassword), "{\"folder_id\":" + inboxId + "}");
         Assert.AreEqual(400, same.status, "Body: " + same.body);

         string other = OtherAccount();
         Deliver(other, "Theirs", "Two.");
         Pop3ClientSimulator.AssertMessageCount(other, UserPassword, 1);
         (int status, string body) othersTree = Http("GET", "/api/v1/me/folders", BasicHeader(other, UserPassword));
         long othersInbox = IdBefore(othersTree.body, "\"path\":\"INBOX\"");

         (int status, string body) intoTheirs = Http("POST", movePath, UserHeader(UserPassword), "{\"folder_id\":" + othersInbox + "}");
         Assert.AreEqual(404, intoTheirs.status, "Another account's folder is not found, not forbidden. Body: " + intoTheirs.body);

         (int status, string body) moved = Http("POST", movePath, UserHeader(UserPassword), "{\"folder_id\":" + projectsId + "}");
         Assert.AreEqual(200, moved.status, "Body: " + moved.body);
         StringAssert.Contains("\"folder_id\":" + projectsId, moved.body);
         long newId = long.Parse(Between(moved.body, "\"id\":", ","));
         Assert.AreNotEqual(messageId, newId, "A moved message is a new row.");

         (int status, string body) inbox = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         StringAssert.Contains("\"total\":0", inbox.body);
         (int status, string body) projects = Http("GET", "/api/v1/me/folders/" + projectsId + "/messages", UserHeader(UserPassword));
         StringAssert.Contains("\"total\":1", projects.body);
         StringAssert.Contains("\"id\":" + newId + ",", projects.body);

         (int status, string body) old = Http("GET", "/api/v1/me/messages/" + messageId, UserHeader(UserPassword));
         Assert.AreEqual(404, old.status, "The old id is gone. Body: " + old.body);
         (int status, string body) fresh = Http("GET", "/api/v1/me/messages/" + newId, UserHeader(UserPassword));
         Assert.AreEqual(200, fresh.status, "Body: " + fresh.body);
         StringAssert.Contains("One.", fresh.body);

         imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.AreEqual(0, imap.GetMessageCount("INBOX"));
         Assert.AreEqual(1, imap.GetMessageCount("Projects"));
         imap.Disconnect();

         Pop3ClientSimulator.AssertMessageCount(other, UserPassword, 1);
      }

      [Test]
      [Description("DELETE /api/v1/me/messages/{id} moves to the Trash folder when the account has one, and is final otherwise or on request")]
      public void DeleteGoesToTheTrashWhenThereIsOne()
      {
         Deliver(Address, "First", "One.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         string listPath = "/api/v1/me/folders/" + inboxId + "/messages";
         long first = IdBefore(Http("GET", listPath, UserHeader(UserPassword)).body, "\"subject\":\"First\"");

         (int status, string body) gone = Http("DELETE", "/api/v1/me/messages/" + first, UserHeader(UserPassword));
         Assert.AreEqual(200, gone.status, "Body: " + gone.body);
         StringAssert.Contains("\"deleted\":true", gone.body);
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 0);

         (int status, string body) again = Http("DELETE", "/api/v1/me/messages/" + first, UserHeader(UserPassword));
         Assert.AreEqual(404, again.status, "Body: " + again.body);

         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.IsTrue(imap.CreateFolder("Trash"));
         imap.Disconnect();

         Deliver(Address, "Second", "Two.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);

         tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long trashId = IdBefore(tree.body, "\"path\":\"Trash\"");
         StringAssert.Contains("\"special_use\":\"\\\\Trash\"", FolderEntry(tree.body, "Trash"));
         long second = IdBefore(Http("GET", listPath, UserHeader(UserPassword)).body, "\"subject\":\"Second\"");

         (int status, string body) trashed = Http("DELETE", "/api/v1/me/messages/" + second, UserHeader(UserPassword));
         Assert.AreEqual(200, trashed.status, "Body: " + trashed.body);
         StringAssert.Contains("\"deleted\":false,\"moved_to\":" + trashId, trashed.body);
         long inTrash = long.Parse(Between(trashed.body, "\"id\":", "}"));
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 0);

         imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.AreEqual(1, imap.GetMessageCount("Trash"));
         imap.Disconnect();

         (int status, string body) emptied = Http("DELETE", "/api/v1/me/messages/" + inTrash, UserHeader(UserPassword));
         Assert.AreEqual(200, emptied.status, "Body: " + emptied.body);
         StringAssert.Contains("\"deleted\":true", emptied.body);

         Deliver(Address, "Third", "Three.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);
         long third = IdBefore(Http("GET", listPath, UserHeader(UserPassword)).body, "\"subject\":\"Third\"");

         (int status, string body) others = Http("DELETE", "/api/v1/me/messages/" + third, BasicHeader(OtherAccount(), UserPassword));
         Assert.AreEqual(404, others.status, "Another account's message is not found, not forbidden. Body: " + others.body);

         (int status, string body) admin = Http("DELETE", "/api/v1/me/messages/" + third, AdminHeader());
         Assert.AreEqual(403, admin.status, "Body: " + admin.body);

         (int status, string body) final = Http("DELETE", "/api/v1/me/messages/" + third + "?permanent=1", UserHeader(UserPassword));
         Assert.AreEqual(200, final.status, "Body: " + final.body);
         StringAssert.Contains("\"deleted\":true", final.body);

         imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.AreEqual(0, imap.GetMessageCount("INBOX"));
         Assert.AreEqual(0, imap.GetMessageCount("Trash"));
         imap.Disconnect();
      }

      [Test]
      [Description("POST /api/v1/me/messages sends as the account to local recipients, and keeps a read copy in the Sent folder")]
      public void AMessageIsSentToLocalRecipientsAndKeptInSent()
      {
         string other = OtherAccount();
         string third = "third@" + _domain.Name;
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, third, UserPassword);

         _account.PersonFirstName = "Self";
         _account.PersonLastName = "Service";
         _account.Save();

         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.IsTrue(imap.CreateFolder("Sent"));
         imap.Disconnect();

         (int status, string body) sent = Http("POST", "/api/v1/me/messages", UserHeader(UserPassword),
            "{\"to\":\"" + other + "\",\"cc\":\"" + third + "\",\"subject\":\"Hello there\",\"text\":\"Body text here.\"}");
         Assert.AreEqual(201, sent.status, "Body: " + sent.body);
         StringAssert.Contains("\"recipients\":2", sent.body);
         long sentId = long.Parse(Between(sent.body, "\"sent_id\":", "}"));
         Assert.Greater(sentId, 0, "A copy is kept in the Sent folder: " + sent.body);

         Pop3ClientSimulator.AssertMessageCount(other, UserPassword, 1);
         Pop3ClientSimulator.AssertMessageCount(third, UserPassword, 1);

         string received = Pop3ClientSimulator.AssertGetFirstMessageText(other, UserPassword);
         StringAssert.Contains("Body text here.", received);
         StringAssert.Contains("Subject: Hello there", received);
         StringAssert.Contains("From: \"Self Service\" <" + Address + ">", received);
         StringAssert.Contains("To: " + other, received);
         StringAssert.Contains("CC: " + third, received);
         StringAssert.Contains("Message-ID:", received);
         StringAssert.Contains("Date:", received);

         imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.AreEqual(1, imap.GetMessageCount("Sent"));
         imap.Disconnect();

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         StringAssert.Contains("\"messages\":1,\"unseen\":0", FolderEntry(tree.body, "Sent"));

         (int status, string body) copy = Http("GET", "/api/v1/me/messages/" + sentId, UserHeader(UserPassword));
         Assert.AreEqual(200, copy.status, "Body: " + copy.body);
         StringAssert.Contains("Body text here.", copy.body);
         StringAssert.Contains("\"seen\":true", copy.body);
      }

      [Test]
      [Description("A submission names the address it refuses, needs a recipient, and is the account's alone")]
      public void ASubmissionNamesWhatItRefuses()
      {
         (int status, string body) nobody = Http("POST", "/api/v1/me/messages", UserHeader(UserPassword),
            "{\"subject\":\"Hello\",\"text\":\"Nobody is here.\"}");
         Assert.AreEqual(400, nobody.status, "Body: " + nobody.body);

         string unknown = "nobody@" + _domain.Name;
         (int status, string body) refused = Http("POST", "/api/v1/me/messages", UserHeader(UserPassword),
            "{\"to\":\"" + unknown + "\",\"subject\":\"Hello\",\"text\":\"Nobody is here.\"}");
         Assert.AreEqual(400, refused.status, "Body: " + refused.body);
         StringAssert.Contains(unknown, refused.body);

         (int status, string body) malformed = Http("POST", "/api/v1/me/messages", UserHeader(UserPassword),
            "{\"to\":\"not an address\",\"text\":\"x\"}");
         Assert.AreEqual(400, malformed.status, "Body: " + malformed.body);

         (int status, string body) admin = Http("POST", "/api/v1/me/messages", AdminHeader(),
            "{\"to\":\"" + Address + "\",\"text\":\"x\"}");
         Assert.AreEqual(403, admin.status, "Body: " + admin.body);

         string cookie = SignIn();
         Response bare = Raw("POST", "/api/v1/me/messages", null,
            "{\"to\":\"" + Address + "\",\"text\":\"x\"}",
            "Cookie: hmailsession=" + cookie + "\r\n");
         Assert.AreEqual(403, bare.Status, "A send on a session needs the header. " + bare.Body);

         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 0);
      }

      [Test]
      [Description("A submission to an external address goes out through the delivery queue like any other")]
      public void AMessageIsRelayedToAnExternalRecipient()
      {
         var deliveryResults = new System.Collections.Generic.Dictionary<string, int>();
         deliveryResults["test@dummy-example.com"] = 250;

         int smtpServerPort = TestSetup.GetNextFreePort();
         using (var server = new SmtpServerSimulator(1, smtpServerPort))
         {
            server.AddRecipientResult(deliveryResults);
            server.StartListen();

            TestSetup.AddRoutePointingAtLocalhost(1, smtpServerPort, false);

            (int status, string body) sent = Http("POST", "/api/v1/me/messages", UserHeader(UserPassword),
               "{\"to\":\"Dummy <test@dummy-example.com>\",\"subject\":\"Outward\",\"text\":\"Going out.\"}");
            Assert.AreEqual(201, sent.status, "Body: " + sent.body);
            StringAssert.Contains("\"recipients\":1", sent.body);

            server.WaitForCompletion();

            StringAssert.Contains("Going out.", server.MessageData);
            StringAssert.Contains("To: Dummy <test@dummy-example.com>", server.MessageData);
            StringAssert.Contains("Subject: Outward", server.MessageData);
            StringAssert.Contains("<" + Address + ">", server.MailFromCommand);
         }

         CustomAsserts.AssertRecipientsInDeliveryQueue(0);
      }

      private static byte[] Pattern(int length)
      {
         var bytes = new byte[length];
         for (int i = 0; i < length; i++)
            bytes[i] = (byte) (i % 251);
         return bytes;
      }

      private static string Base64Part(string contentType, string name, byte[] content)
      {
         return "--b1\r\n" +
                "Content-Type: " + contentType + "; name=\"" + name + "\"\r\n" +
                "Content-Transfer-Encoding: base64\r\n" +
                "Content-Disposition: attachment; filename=\"" + name + "\"\r\n" +
                "\r\n" +
                Convert.ToBase64String(content, Base64FormattingOptions.InsertLineBreaks) + "\r\n";
      }

      [Test]
      [Description("GET /api/v1/me/messages/{id}/attachments/{index} serves one attachment decoded, as a download, and only to its owner")]
      public void AnAttachmentIsDownloadedAsAFile()
      {
         byte[] numbers = Pattern(1000);
         byte[] page = Encoding.ASCII.GetBytes("<html><script>alert(1)</script></html>");
         string accented = "Résumé \"final\".pdf";
         string encodedName = "=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(accented)) + "?=";

         string raw =
            "From: sender@example.com\r\n" +
            "To: " + Address + "\r\n" +
            "Subject: With files\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=\"b1\"\r\n" +
            "\r\n" +
            "--b1\r\n" +
            "Content-Type: text/plain\r\n" +
            "\r\n" +
            "See attached.\r\n" +
            Base64Part("application/octet-stream", "numbers.bin", numbers) +
            Base64Part("text/html", "page.html", page) +
            Base64Part("application/pdf", encodedName, Pattern(10)) +
            "--b1--\r\n";

         SmtpClientSimulator.StaticSendRaw("sender@example.com", Address, raw);
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         long messageId = IdBefore(Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword)).body, "\"subject\":\"With files\"");

         (int status, string body) message = Http("GET", "/api/v1/me/messages/" + messageId, UserHeader(UserPassword));
         Assert.AreEqual(200, message.status, "Body: " + message.body);
         StringAssert.Contains("{\"index\":0,\"name\":\"numbers.bin\",\"size\":1000,\"content_type\":\"application/octet-stream\",\"content_id\":\"\"}", message.body);
         StringAssert.Contains("\"name\":\"page.html\"", message.body);
         StringAssert.Contains("\"content_type\":\"text/html\"", message.body);
         StringAssert.Contains("See attached.", message.body);

         string path = "/api/v1/me/messages/" + messageId + "/attachments/";

         Response numbersFile = Raw("GET", path + "0", UserHeader(UserPassword), null);
         Assert.AreEqual(200, numbersFile.Status, numbersFile.Body);
         Assert.AreEqual("application/octet-stream", numbersFile.Header("Content-Type"));
         StringAssert.Contains("attachment; filename=\"numbers.bin\"", numbersFile.Header("Content-Disposition"));
         Assert.AreEqual("nosniff", numbersFile.Header("X-Content-Type-Options"));
         Assert.AreEqual("no-store", numbersFile.Header("Cache-Control"));
         Assert.AreEqual(numbers, numbersFile.BodyBytes, "The bytes come back exactly as attached.");

         Response html = Raw("GET", path + "1", UserHeader(UserPassword), null);
         Assert.AreEqual(200, html.Status, html.Body);
         Assert.AreEqual("application/octet-stream", html.Header("Content-Type"), "A type a browser would render is not served under it.");
         StringAssert.Contains("sandbox", html.Header("Content-Security-Policy"));
         Assert.AreEqual(page, html.BodyBytes);

         Response pdf = Raw("GET", path + "2", UserHeader(UserPassword), null);
         Assert.AreEqual(200, pdf.Status, pdf.Body);
         Assert.AreEqual("application/pdf", pdf.Header("Content-Type"));
         StringAssert.Contains("filename=\"R_sum_ _final_.pdf\"", pdf.Header("Content-Disposition"));
         StringAssert.Contains("filename*=UTF-8''R%C3%A9sum%C3%A9%20%22final%22.pdf", pdf.Header("Content-Disposition"));

         (int status, string body) missing = Http("GET", path + "3", UserHeader(UserPassword));
         Assert.AreEqual(404, missing.status, "Body: " + missing.body);

         (int status, string body) others = Http("GET", path + "0", BasicHeader(OtherAccount(), UserPassword));
         Assert.AreEqual(404, others.status, "Body: " + others.body);

         (int status, string body) admin = Http("GET", path + "0", AdminHeader());
         Assert.AreEqual(403, admin.status, "Body: " + admin.body);
      }

      [Test]
      [Description("q on a folder's listing finds the text in the subject, the sender or the body, whatever its case")]
      public void AFolderIsSearchedBySubjectSenderAndBody()
      {
         SmtpClientSimulator.StaticSend("alice@example.com", Address, "Quarterly numbers", "The figures.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);
         SmtpClientSimulator.StaticSend("bob@example.com", Address, "Lunch", "The invoice is attached in spirit.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 2);
         SmtpClientSimulator.StaticSend("carol@example.com", Address, "Other", "Nothing to see.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 3);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         string listPath = "/api/v1/me/folders/" + inboxId + "/messages";

         (int status, string body) bySubject = Http("GET", listPath + "?q=numbers", UserHeader(UserPassword));
         Assert.AreEqual(200, bySubject.status, "Body: " + bySubject.body);
         StringAssert.Contains("\"query\":\"numbers\"", bySubject.body);
         StringAssert.Contains("Quarterly numbers", bySubject.body);
         Assert.IsFalse(bySubject.body.Contains("Lunch") || bySubject.body.Contains("Other"), "Only the match: " + bySubject.body);
         StringAssert.Contains("\"scanned\":3,\"complete\":true", bySubject.body);

         (int status, string body) byBody = Http("GET", listPath + "?q=INVOICE", UserHeader(UserPassword));
         Assert.AreEqual(200, byBody.status, "Body: " + byBody.body);
         StringAssert.Contains("Lunch", byBody.body);
         Assert.IsFalse(byBody.body.Contains("Quarterly"), "The body match only: " + byBody.body);

         (int status, string body) bySender = Http("GET", listPath + "?q=carol%40example", UserHeader(UserPassword));
         Assert.AreEqual(200, bySender.status, "Body: " + bySender.body);
         StringAssert.Contains("Other", bySender.body);
         Assert.IsFalse(bySender.body.Contains("Lunch"), "The sender match only: " + bySender.body);

         (int status, string body) nothing = Http("GET", listPath + "?q=zebra", UserHeader(UserPassword));
         Assert.AreEqual(200, nothing.status, "Body: " + nothing.body);
         StringAssert.Contains("\"messages\":[]", nothing.body);

         (int status, string body) plain = Http("GET", listPath, UserHeader(UserPassword));
         StringAssert.Contains("\"total\":3", plain.body);
         StringAssert.Contains("\"query\":\"\"", plain.body);
      }

      [Test]
      [Description("GET /api/v1/me/search looks through every folder of the account and names the folder of each hit")]
      public void ASearchAcrossFoldersNamesTheFolder()
      {
         SmtpClientSimulator.StaticSend("alice@example.com", Address, "Budget draft", "Numbers inside.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);
         SmtpClientSimulator.StaticSend("alice@example.com", Address, "Budget final", "Numbers agreed.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 2);

         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.IsTrue(imap.CreateFolder("Projects"));
         imap.Disconnect();

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         long projectsId = IdBefore(tree.body, "\"path\":\"Projects\"");
         long draft = IdBefore(Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword)).body, "\"subject\":\"Budget draft\"");

         (int status, string body) moved = Http("POST", "/api/v1/me/messages/" + draft + "/move", UserHeader(UserPassword), "{\"folder_id\":" + projectsId + "}");
         Assert.AreEqual(200, moved.status, "Body: " + moved.body);

         (int status, string body) found = Http("GET", "/api/v1/me/search?q=budget", UserHeader(UserPassword));
         Assert.AreEqual(200, found.status, "Body: " + found.body);
         StringAssert.Contains("\"folder\":\"Projects\"", found.body);
         StringAssert.Contains("\"folder\":\"INBOX\"", found.body);
         StringAssert.Contains("Budget draft", found.body);
         StringAssert.Contains("Budget final", found.body);
         StringAssert.Contains("\"complete\":true", found.body);

         (int status, string body) one = Http("GET", "/api/v1/me/search?q=agreed", UserHeader(UserPassword));
         StringAssert.Contains("Budget final", one.body);
         Assert.IsFalse(one.body.Contains("Budget draft"), "Only the match: " + one.body);

         (int status, string body) missing = Http("GET", "/api/v1/me/search", UserHeader(UserPassword));
         Assert.AreEqual(400, missing.status, "Body: " + missing.body);

         (int status, string body) admin = Http("GET", "/api/v1/me/search?q=budget", AdminHeader());
         Assert.AreEqual(403, admin.status, "Body: " + admin.body);
      }

      [Test]
      [Description("Text that is not ASCII comes out of the mailbox routes and goes into the automatic reply as UTF-8")]
      public void NonAsciiTextSurvivesBothWays()
      {
         string subject = "Résumé für Zoë";
         string encodedSubject = "=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(subject)) + "?=";
         string raw =
            "From: =?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Ærø Sørensen")) + "?= <sender@example.com>\r\n" +
            "To: " + Address + "\r\n" +
            "Subject: " + encodedSubject + "\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "Content-Transfer-Encoding: 8bit\r\n" +
            "\r\n" +
            "Grüße aus Zürich.\r\n";

         SmtpClientSimulator.StaticSendRaw("sender@example.com", Address, raw);
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         (int status, string body) page = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         StringAssert.Contains("\"subject\":\"" + subject + "\"", page.body);
         StringAssert.Contains("Ærø Sørensen", page.body);

         long messageId = IdBefore(page.body, "\"subject\":\"" + subject + "\"");
         (int status, string body) message = Http("GET", "/api/v1/me/messages/" + messageId, UserHeader(UserPassword));
         StringAssert.Contains("Grüße aus Zürich.", message.body);

         (int status, string body) found = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages?q=" + Uri.EscapeDataString("zürich"), UserHeader(UserPassword));
         StringAssert.Contains("\"subject\":\"" + subject + "\"", found.body);

         (int status, string body) vacation = Http("PUT", "/api/v1/me/vacation", UserHeader(UserPassword),
            "{\"enabled\":true,\"subject\":\"Väck mig\",\"message\":\"Jag är på semester.\"}");
         Assert.AreEqual(200, vacation.status, "Body: " + vacation.body);
         Account reread = _domain.Accounts.ItemByAddress[Address];
         Assert.AreEqual("Väck mig", reread.VacationSubject);
         Assert.AreEqual("Jag är på semester.", reread.VacationMessage);

         (int status, string body) me = Http("GET", "/api/v1/me", UserHeader(UserPassword));
         StringAssert.Contains("\"subject\":\"Väck mig\"", me.body);
      }

      // RFC 4314 rights as stored in hm_acl.aclvalue.
      private const int RightLookup = 1;
      private const int RightRead = 2;

      private static void GrantOnFolder(IMAPFolder folder, Account grantee, int rights)
      {
         SingletonProvider<TestSetup>.Instance.GetApp().Database.ExecuteSQL(
            string.Format(
               "insert into hm_acl (aclsharefolderid, aclpermissiontype, aclpermissiongroupid, aclpermissionaccountid, aclvalue) " +
               "values ({0}, 0, 0, {1}, {2})",
               folder.ID, grantee.ID, rights));
      }

      [Test]
      [Description("A folder another account shared appears under its owner, with the rights the owner granted and no more")]
      public void ASharedFolderAppearsUnderItsOwnerWithTheRightsGranted()
      {
         Account owner = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "owner@" + _domain.Name, UserPassword);
         SmtpClientSimulator.StaticSend("alice@example.com", owner.Address, "Team plan", "The plan is simple.");
         Pop3ClientSimulator.AssertMessageCount(owner.Address, UserPassword, 1);

         IMAPFolder ownersInbox = CustomAsserts.AssertFolderExists(owner.IMAPFolders, "INBOX");
         GrantOnFolder(ownersInbox, _account, RightLookup | RightRead);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         Assert.AreEqual(200, tree.status, "Body: " + tree.body);
         StringAssert.Contains("\"owner\":\"" + owner.Address + "\"", tree.body);
         string shared = FolderEntry(tree.body, "#Users." + owner.Address + ".INBOX");
         StringAssert.Contains("\"account_id\":" + owner.ID + ",", shared);
         StringAssert.Contains("\"writable\":false", shared);
         StringAssert.Contains("\"messages\":1,\"unseen\":1", shared);
         long sharedId = IdBefore(tree.body, "\"path\":\"#Users." + owner.Address + ".INBOX\"");
         Assert.AreEqual(ownersInbox.ID, sharedId, "The shared folder is the owner's folder, under its own id.");

         (int status, string body) page = Http("GET", "/api/v1/me/folders/" + sharedId + "/messages", UserHeader(UserPassword));
         Assert.AreEqual(200, page.status, "Body: " + page.body);
         StringAssert.Contains("Team plan", page.body);
         long messageId = IdBefore(page.body, "\"subject\":\"Team plan\"");

         (int status, string body) message = Http("GET", "/api/v1/me/messages/" + messageId, UserHeader(UserPassword));
         Assert.AreEqual(200, message.status, "Body: " + message.body);
         StringAssert.Contains("The plan is simple.", message.body);

         (int status, string body) flag = Http("PUT", "/api/v1/me/messages/" + messageId + "/flags", UserHeader(UserPassword), "{\"seen\":true}");
         Assert.AreEqual(403, flag.status, "Read is not write. Body: " + flag.body);

         (int status, string body) delete = Http("DELETE", "/api/v1/me/messages/" + messageId, UserHeader(UserPassword));
         Assert.AreEqual(403, delete.status, "Body: " + delete.body);

         (int status, string body) mine = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(mine.body, "\"path\":\"INBOX\"");
         (int status, string body) move = Http("POST", "/api/v1/me/messages/" + messageId + "/move", UserHeader(UserPassword), "{\"folder_id\":" + inboxId + "}");
         Assert.AreEqual(400, move.status, "A message never moves between mailboxes. Body: " + move.body);

         Pop3ClientSimulator.AssertMessageCount(owner.Address, UserPassword, 1);

         (int status, string body) third = Http("GET", "/api/v1/me/folders", BasicHeader(OtherAccount(), UserPassword));
         Assert.IsFalse(third.body.Contains(owner.Address), "Nothing was shared with the third account: " + third.body);
         (int status, string body) thirdPage = Http("GET", "/api/v1/me/folders/" + sharedId + "/messages", BasicHeader("other@" + _domain.Name, UserPassword));
         Assert.AreEqual(404, thirdPage.status, "Body: " + thirdPage.body);
      }

      [Test]
      [Description("A public folder appears under its namespace, and its messages - which belong to no account - are read from the public store")]
      public void APublicFolderAppearsUnderItsNamespace()
      {
         Account owner = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "poster@" + _domain.Name, UserPassword);
         SmtpClientSimulator.StaticSend("alice@example.com", owner.Address, "Notice one", "Read all about it.");
         Pop3ClientSimulator.AssertMessageCount(owner.Address, UserPassword, 1);

         IMAPFolder notices = _settings.PublicFolders.Add("Notices");
         notices.Save();

         IMAPFolderPermission posting = notices.Permissions.Add();
         posting.PermissionAccountID = owner.ID;
         posting.PermissionType = eACLPermissionType.ePermissionTypeUser;
         posting.set_Permission(eACLPermission.ePermissionLookup, true);
         posting.set_Permission(eACLPermission.ePermissionRead, true);
         posting.set_Permission(eACLPermission.ePermissionInsert, true);
         posting.Save();

         IMAPFolderPermission reading = notices.Permissions.Add();
         reading.PermissionAccountID = _account.ID;
         reading.PermissionType = eACLPermissionType.ePermissionTypeUser;
         reading.set_Permission(eACLPermission.ePermissionLookup, true);
         reading.set_Permission(eACLPermission.ePermissionRead, true);
         reading.Save();

         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(owner.Address, UserPassword));
         Assert.IsTrue(imap.SelectFolder("INBOX"));
         Assert.IsTrue(imap.Copy(1, "#Public.Notices"));
         imap.Disconnect();

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         Assert.AreEqual(200, tree.status, "Body: " + tree.body);
         StringAssert.Contains("\"owner\":\"#Public\"", tree.body);
         string entry = FolderEntry(tree.body, "#Public.Notices");
         StringAssert.Contains("\"account_id\":0,", entry);
         StringAssert.Contains("\"messages\":1,", entry);
         long noticesId = IdBefore(tree.body, "\"path\":\"#Public.Notices\"");

         (int status, string body) page = Http("GET", "/api/v1/me/folders/" + noticesId + "/messages", UserHeader(UserPassword));
         Assert.AreEqual(200, page.status, "Body: " + page.body);
         long messageId = IdBefore(page.body, "\"subject\":\"Notice one\"");

         (int status, string body) message = Http("GET", "/api/v1/me/messages/" + messageId, UserHeader(UserPassword));
         Assert.AreEqual(200, message.status, "Body: " + message.body);
         StringAssert.Contains("Read all about it.", message.body);

         (int status, string body) third = Http("GET", "/api/v1/me/folders/" + noticesId + "/messages", BasicHeader(OtherAccount(), UserPassword));
         Assert.AreEqual(404, third.status, "No right on the public folder: " + third.body);
      }

      [Test]
      [Description("PUT /api/v1/me/settings sets the name, the forwarding and the signature COM reads back, and refuses a forwarding that would loop")]
      public void SettingsRoundTrip()
      {
         string other = OtherAccount();

         (int status, string body) saved = Http("PUT", "/api/v1/me/settings", UserHeader(UserPassword),
            "{\"name\":{\"first\":\"Ada\",\"last\":\"Lovelace\"}," +
            "\"forwarding\":{\"enabled\":true,\"address\":\"" + other + "\",\"keep_original\":true}," +
            "\"signature\":{\"enabled\":true,\"text\":\"-- \\nAda\",\"html\":\"\"}}");
         Assert.AreEqual(200, saved.status, "Body: " + saved.body);
         StringAssert.Contains("\"first\":\"Ada\"", saved.body);
         StringAssert.Contains("\"address\":\"" + other + "\"", saved.body);

         Account reread = _domain.Accounts.ItemByAddress[Address];
         Assert.AreEqual("Ada", reread.PersonFirstName);
         Assert.AreEqual("Lovelace", reread.PersonLastName);
         Assert.IsTrue(reread.ForwardEnabled);
         Assert.AreEqual(other, reread.ForwardAddress);
         Assert.IsTrue(reread.ForwardKeepOriginal);
         Assert.IsTrue(reread.SignatureEnabled);
         Assert.AreEqual("-- \nAda", reread.SignaturePlainText);

         (int status, string body) read = Http("GET", "/api/v1/me/settings", UserHeader(UserPassword));
         Assert.AreEqual(200, read.status, "Body: " + read.body);
         StringAssert.Contains("\"name\":{\"first\":\"Ada\",\"last\":\"Lovelace\"}", read.body);
         StringAssert.Contains("\"forwarding\":{\"enabled\":true,\"address\":\"" + other + "\",\"keep_original\":true}", read.body);
         StringAssert.Contains("\"signature\":{\"enabled\":true,\"text\":\"-- \\nAda\"", read.body);

         (int status, string body) partial = Http("PUT", "/api/v1/me/settings", UserHeader(UserPassword),
            "{\"forwarding\":{\"enabled\":false,\"address\":\"\",\"keep_original\":true}}");
         Assert.AreEqual(200, partial.status, "Body: " + partial.body);
         reread = _domain.Accounts.ItemByAddress[Address];
         Assert.IsFalse(reread.ForwardEnabled);
         Assert.AreEqual("Ada", reread.PersonFirstName, "An object the body does not name is left as it is.");

         (int status, string body) loop = Http("PUT", "/api/v1/me/settings", UserHeader(UserPassword),
            "{\"forwarding\":{\"enabled\":true,\"address\":\"" + Address.ToUpperInvariant() + "\"}}");
         Assert.AreEqual(400, loop.status, "Body: " + loop.body);

         (int status, string body) malformed = Http("PUT", "/api/v1/me/settings", UserHeader(UserPassword),
            "{\"forwarding\":{\"enabled\":true,\"address\":\"nowhere\"}}");
         Assert.AreEqual(400, malformed.status, "Body: " + malformed.body);

         (int status, string body) nothing = Http("PUT", "/api/v1/me/settings", UserHeader(UserPassword), "{}");
         Assert.AreEqual(400, nothing.status, "Body: " + nothing.body);

         (int status, string body) admin = Http("GET", "/api/v1/me/settings", AdminHeader());
         Assert.AreEqual(403, admin.status, "Body: " + admin.body);
      }

      [Test]
      [Description("PUT /api/v1/me/filters stores a Sieve script that runs at delivery, refuses one that does not parse, and removes it when empty")]
      public void FiltersRunAtDelivery()
      {
         _account.IMAPFolders.Add("Spam");

         string script =
            "require \"fileinto\";\r\n" +
            "if header :contains \"Subject\" \"lottery\" {\r\n" +
            "  fileinto \"Spam\";\r\n" +
            "}\r\n";
         string scriptJson = script.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");

         (int status, string body) bad = Http("PUT", "/api/v1/me/filters", UserHeader(UserPassword), "{\"script\":\"if header :contains {\\r\\n\"}");
         Assert.AreEqual(400, bad.status, "Body: " + bad.body);
         StringAssert.Contains("\"error\":", bad.body);

         (int status, string body) missing = Http("PUT", "/api/v1/me/filters", UserHeader(UserPassword), "{}");
         Assert.AreEqual(400, missing.status, "Body: " + missing.body);

         (int status, string body) saved = Http("PUT", "/api/v1/me/filters", UserHeader(UserPassword), "{\"script\":\"" + scriptJson + "\"}");
         Assert.AreEqual(200, saved.status, "Body: " + saved.body);

         (int status, string body) read = Http("GET", "/api/v1/me/filters", UserHeader(UserPassword));
         Assert.AreEqual(200, read.status, "Body: " + read.body);
         StringAssert.Contains("\"active\":\"" + scriptJson + "\"", read.body);

         SmtpClientSimulator.StaticSend("sender@example.com", Address, "You won the lottery!", "Congratulations.");
         CustomAsserts.AssertFolderMessageCount(_account.IMAPFolders.get_ItemByName("Spam"), 1);
         CustomAsserts.AssertFolderMessageCount(_account.IMAPFolders.get_ItemByName("INBOX"), 0);

         (int status, string body) cleared = Http("PUT", "/api/v1/me/filters", UserHeader(UserPassword), "{\"script\":\"\"}");
         Assert.AreEqual(200, cleared.status, "Body: " + cleared.body);
         (int status, string body) empty = Http("GET", "/api/v1/me/filters", UserHeader(UserPassword));
         StringAssert.Contains("\"active\":\"\"", empty.body);

         SmtpClientSimulator.StaticSend("sender@example.com", Address, "Another lottery", "Again.");
         CustomAsserts.AssertFolderMessageCount(_account.IMAPFolders.get_ItemByName("INBOX"), 1);
         CustomAsserts.AssertFolderMessageCount(_account.IMAPFolders.get_ItemByName("Spam"), 1);

         (int status, string body) admin = Http("PUT", "/api/v1/me/filters", AdminHeader(), "{\"script\":\"\"}");
         Assert.AreEqual(403, admin.status, "Body: " + admin.body);
      }

      [Test]
      [Description("The escapes a JSON string may carry - newlines, tabs, \\u code points, a surrogate pair - are read as the characters they stand for")]
      public void JsonEscapesAreReadAsCharacters()
      {
         (int status, string body) vacation = Http("PUT", "/api/v1/me/vacation", UserHeader(UserPassword),
            "{\"enabled\":true,\"subject\":\"Caf\\u00e9 \\ud83d\\ude80\",\"message\":\"Line one\\nLine two\\ttabbed\"}");
         Assert.AreEqual(200, vacation.status, "Body: " + vacation.body);

         Account reread = _domain.Accounts.ItemByAddress[Address];
         Assert.AreEqual("Café \U0001F680", reread.VacationSubject);
         Assert.AreEqual("Line one\nLine two\ttabbed", reread.VacationMessage);

         (int status, string body) me = Http("GET", "/api/v1/me", UserHeader(UserPassword));
         StringAssert.Contains("\"subject\":\"Café \U0001F680\"", me.body);
         StringAssert.Contains("\"message\":\"Line one\\nLine two\\ttabbed\"", me.body);
      }

      [Test]
      [Description("POST /api/v1/me/drafts keeps a draft in the Drafts folder, made when there is none, and replaces it as a new message")]
      public void ADraftIsKeptInTheDraftsFolderAndReplaced()
      {
         (int status, string body) saved = Http("POST", "/api/v1/me/drafts", UserHeader(UserPassword),
            "{\"to\":\"someone@example.com\",\"subject\":\"Half written\",\"text\":\"So far so good.\"}");
         Assert.AreEqual(201, saved.status, "Body: " + saved.body);
         long draftId = long.Parse(Between(saved.body, "\"id\":", ","));
         long draftsFolderId = long.Parse(Between(saved.body, "\"folder_id\":", "}"));

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         string drafts = FolderEntry(tree.body, "Drafts");
         StringAssert.Contains("\"special_use\":\"\\\\Drafts\"", drafts);
         StringAssert.Contains("\"messages\":1,\"unseen\":0", drafts);
         Assert.AreEqual(draftsFolderId, IdBefore(tree.body, "\"path\":\"Drafts\""));

         (int status, string body) read = Http("GET", "/api/v1/me/messages/" + draftId, UserHeader(UserPassword));
         Assert.AreEqual(200, read.status, "Body: " + read.body);
         StringAssert.Contains("\"subject\":\"Half written\"", read.body);
         StringAssert.Contains("\"to\":\"someone@example.com\"", read.body);
         StringAssert.Contains("So far so good.", read.body);
         StringAssert.Contains("\"draft\":true", read.body);

         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.AreEqual(1, imap.GetMessageCount("Drafts"));
         StringAssert.Contains("\\Draft", imap.GetFlags(1));
         imap.Disconnect();

         (int status, string body) replaced = Http("POST", "/api/v1/me/drafts", UserHeader(UserPassword),
            "{\"to\":\"someone@example.com\",\"subject\":\"Nearly done\",\"text\":\"Almost there.\",\"replace_id\":" + draftId + "}");
         Assert.AreEqual(201, replaced.status, "Body: " + replaced.body);
         long newId = long.Parse(Between(replaced.body, "\"id\":", ","));
         Assert.AreNotEqual(draftId, newId, "New content is a new message.");

         (int status, string body) old = Http("GET", "/api/v1/me/messages/" + draftId, UserHeader(UserPassword));
         Assert.AreEqual(404, old.status, "The replaced draft is gone. Body: " + old.body);
         (int status, string body) page = Http("GET", "/api/v1/me/folders/" + draftsFolderId + "/messages", UserHeader(UserPassword));
         StringAssert.Contains("\"total\":1", page.body);
         StringAssert.Contains("Nearly done", page.body);

         (int status, string body) admin = Http("POST", "/api/v1/me/drafts", AdminHeader(), "{\"subject\":\"x\"}");
         Assert.AreEqual(403, admin.status, "Body: " + admin.body);
      }

      [Test]
      [Description("A listing carries the thread headers, and a reply sent with them threads correctly and marks the original answered")]
      public void AReplyCarriesTheThreadHeadersAndMarksTheOriginalAnswered()
      {
         string raw =
            "From: alice@example.com\r\n" +
            "To: " + Address + "\r\n" +
            "Subject: Plans\r\n" +
            "Message-ID: <original-1@example.com>\r\n" +
            "\r\n" +
            "What do you think?\r\n";
         SmtpClientSimulator.StaticSendRaw("alice@example.com", Address, raw);
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         (int status, string body) page = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         StringAssert.Contains("\"message_id\":\"<original-1@example.com>\",\"in_reply_to\":\"\",\"references\":\"\"", page.body);
         long originalId = IdBefore(page.body, "\"subject\":\"Plans\"");

         string other = OtherAccount();
         (int status, string body) sent = Http("POST", "/api/v1/me/messages", UserHeader(UserPassword),
            "{\"to\":\"" + other + "\",\"subject\":\"Re: Plans\",\"text\":\"I think yes.\"," +
            "\"in_reply_to\":\"<original-1@example.com>\",\"references\":\"<original-1@example.com>\",\"answered_id\":" + originalId + "}");
         Assert.AreEqual(201, sent.status, "Body: " + sent.body);

         Pop3ClientSimulator.AssertMessageCount(other, UserPassword, 1);

         (int status, string body) theirs = Http("GET", "/api/v1/me/folders", BasicHeader(other, UserPassword));
         long theirInbox = IdBefore(theirs.body, "\"path\":\"INBOX\"");
         (int status, string body) theirPage = Http("GET", "/api/v1/me/folders/" + theirInbox + "/messages", BasicHeader(other, UserPassword));
         StringAssert.Contains("\"in_reply_to\":\"<original-1@example.com>\",\"references\":\"<original-1@example.com>\"", theirPage.body);

         // Fetching over POP3 removes the message, so this comes after the listing above.
         string received = Pop3ClientSimulator.AssertGetFirstMessageText(other, UserPassword);
         StringAssert.Contains("In-Reply-To: <original-1@example.com>", received);
         StringAssert.Contains("References: <original-1@example.com>", received);

         (int status, string body) original = Http("GET", "/api/v1/me/messages/" + originalId, UserHeader(UserPassword));
         StringAssert.Contains("\"answered\":true", original.body);

         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.IsTrue(imap.SelectFolder("INBOX"));
         StringAssert.Contains("\\Answered", imap.GetFlags(1));
         imap.Disconnect();
      }

      [Test]
      [Description("Files named in a submission go out as attachments under their names and types, and the Sent copy serves them back")]
      public void AttachmentsGoOutWithTheMessage()
      {
         string other = OtherAccount();
         byte[] numbers = Pattern(1000);

         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.IsTrue(imap.CreateFolder("Sent"));
         imap.Disconnect();

         string body =
            "{\"to\":\"" + other + "\",\"subject\":\"With files\",\"text\":\"See attached.\",\"attachments\":[" +
            "{\"name\":\"numbers.bin\",\"type\":\"application/octet-stream\",\"data\":\"" + Convert.ToBase64String(numbers) + "\"}," +
            "{\"name\":\"note.txt\",\"type\":\"text/plain\",\"data\":\"" + Convert.ToBase64String(Encoding.ASCII.GetBytes("hello")) + "\"}" +
            "]}";

         (int status, string body) sent = Http("POST", "/api/v1/me/messages", UserHeader(UserPassword), body);
         Assert.AreEqual(201, sent.status, "Body: " + sent.body);
         long sentId = long.Parse(Between(sent.body, "\"sent_id\":", "}"));

         Pop3ClientSimulator.AssertMessageCount(other, UserPassword, 1);
         string received = Pop3ClientSimulator.AssertGetFirstMessageText(other, UserPassword);
         StringAssert.Contains("name=\"numbers.bin\"", received);
         StringAssert.Contains("filename=\"numbers.bin\"", received);
         StringAssert.Contains("Content-Type: text/plain", received);
         StringAssert.Contains("name=\"note.txt\"", received);
         StringAssert.Contains(Convert.ToBase64String(Encoding.ASCII.GetBytes("hello")), received);
         StringAssert.Contains("See attached.", received);

         (int status, string body) copy = Http("GET", "/api/v1/me/messages/" + sentId, UserHeader(UserPassword));
         Assert.AreEqual(200, copy.status, "Body: " + copy.body);
         // The entry carries its type and its content id as well as its name and
         // size: the page reads those to decide whether an attachment is an inline
         // cid: image, and what type to hand the frame it renders the body in.
         StringAssert.Contains("{\"index\":0,\"name\":\"numbers.bin\",\"size\":1000,"
            + "\"content_type\":\"application/octet-stream\",\"content_id\":\"\"}", copy.body,
            "An unknown type is reported as application/octet-stream, which is what stops a browser running it. Body: " + copy.body);
         StringAssert.Contains("{\"index\":1,\"name\":\"note.txt\",\"size\":5,"
            + "\"content_type\":\"text/plain\",\"content_id\":\"\"}", copy.body,
            "Body: " + copy.body);

         Response file = Raw("GET", "/api/v1/me/messages/" + sentId + "/attachments/0", UserHeader(UserPassword), null);
         Assert.AreEqual(200, file.Status, file.Body);
         Assert.AreEqual(numbers, file.BodyBytes);

         StringBuilder tooMany = new StringBuilder("{\"to\":\"" + other + "\",\"text\":\"x\",\"attachments\":[");
         for (int i = 0; i < 21; i++)
            tooMany.Append(i > 0 ? "," : "").Append("{\"name\":\"f" + i + ".txt\",\"type\":\"text/plain\",\"data\":\"aGk=\"}");
         tooMany.Append("]}");
         (int status, string body) refused = Http("POST", "/api/v1/me/messages", UserHeader(UserPassword), tooMany.ToString());
         Assert.AreEqual(400, refused.status, "Body: " + refused.body);
         Pop3ClientSimulator.AssertMessageCount(other, UserPassword, 0);
      }

      [Test]
      [Description("A large body is accepted where a message is sent or a draft kept, and refused everywhere else")]
      public void LargeBodiesAreAllowedOnlyWhereTheyBelong()
      {
         string big = "{\"to\":\"x@example.com\",\"text\":\"" + new string('a', 200 * 1024) + "\"}";

         (int status, string body) send = Http("POST", "/api/v1/me/messages", UserHeader("not-the-password"), big);
         Assert.AreEqual(401, send.status, "The body was read and the credentials refused, not the size. Body: " + send.body);

         (int status, string body) draft = Http("POST", "/api/v1/me/drafts", UserHeader("not-the-password"), big);
         Assert.AreEqual(401, draft.status, "Body: " + draft.body);

         // The two above are sent for real, body and all, because they are meant
         // to be read. These two are refused on the declared length before the
         // body is read, so they are asked for the way a client asks for
         // something that might be refused - see RefusedBySize.
         (int status, string body) elsewhere = RefusedBySize("POST", "/api/v1/me/vacation", UserHeader(UserPassword), big.Length);
         Assert.AreEqual(413, elsewhere.status, "Body: " + elsewhere.body);

         (int status, string body) admin = RefusedBySize("POST", "/api/v1/status", AdminHeader(), big.Length);
         Assert.AreEqual(413, admin.status, "Body: " + admin.body);
      }

      // ------------------------------------------------- the folder writes ---

      // The account's root folders as COM sees them, which is how the Control
      // Panel would: the collection is the one the running server holds, so a
      // folder a REST call made is in it without a reload.
      private bool ComHasRootFolder(string name)
      {
         IMAPFolders folders = _account.IMAPFolders;
         for (int i = 0; i < folders.Count; i++)
         {
            if (string.Equals(folders[i].Name, name, StringComparison.OrdinalIgnoreCase))
               return true;
         }

         return false;
      }

      private string Delimiter()
      {
         return Between(Http("GET", "/api/v1/me/folders", UserHeader(UserPassword)).body, "\"delimiter\":\"", "\"");
      }

      private long FolderIdOf(string path)
      {
         return IdBefore(Http("GET", "/api/v1/me/folders", UserHeader(UserPassword)).body, "\"path\":\"" + path + "\"");
      }

      [Test]
      [Description("POST /api/v1/me/folders creates a folder COM then reads back, a name carrying the delimiter creates the whole path, and parent_id nests")]
      public void FoldersAreCreatedThroughTheirRoute()
      {
         string delimiter = Delimiter();

         (int status, string body) plain = Http("POST", "/api/v1/me/folders", UserHeader(UserPassword), "{\"name\":\"Projects\"}");
         Assert.AreEqual(201, plain.status, "Body: " + plain.body);
         StringAssert.Contains("\"name\":\"Projects\"", plain.body);
         StringAssert.Contains("\"path\":\"Projects\"", plain.body);
         StringAssert.Contains("\"parent_id\":-1", plain.body);
         StringAssert.Contains("\"special_use\":\"\"", plain.body);
         StringAssert.Contains("\"messages\":0,\"unseen\":0", plain.body);
         StringAssert.Contains("\"subfolders\":[]", plain.body);

         long projectsId = IdBefore(plain.body, "\"path\":\"Projects\"");
         IMAPFolder projects = CustomAsserts.AssertFolderExists(_account.IMAPFolders, "Projects");
         Assert.AreEqual(projectsId, (long) projects.ID, "The answer names the folder COM now holds.");

         // A name carrying the hierarchy delimiter is a path, and CREATE makes
         // every level of it that is missing.
         (int status, string body) tree = Http("POST", "/api/v1/me/folders", UserHeader(UserPassword),
            "{\"name\":\"Work" + delimiter + "2026" + delimiter + "Q1\"}");
         Assert.AreEqual(201, tree.status, "Body: " + tree.body);
         StringAssert.Contains("\"name\":\"Q1\"", tree.body);
         StringAssert.Contains("\"path\":\"Work" + delimiter + "2026" + delimiter + "Q1\"", tree.body);

         IMAPFolder work = CustomAsserts.AssertFolderExists(_account.IMAPFolders, "Work");
         IMAPFolder year = CustomAsserts.AssertFolderExists(work.SubFolders, "2026");
         IMAPFolder quarter = CustomAsserts.AssertFolderExists(year.SubFolders, "Q1");
         Assert.AreEqual((long) year.ID, (long) quarter.ParentID);

         // parent_id nests under a folder of this account.
         (int status, string body) nested = Http("POST", "/api/v1/me/folders", UserHeader(UserPassword),
            "{\"name\":\"Alpha\",\"parent_id\":" + projectsId + "}");
         Assert.AreEqual(201, nested.status, "Body: " + nested.body);
         StringAssert.Contains("\"path\":\"Projects" + delimiter + "Alpha\"", nested.body);
         StringAssert.Contains("\"parent_id\":" + projectsId, nested.body);
         CustomAsserts.AssertFolderExists(projects.SubFolders, "Alpha");

         // And the listing shows what the create answered.
         (int status, string body) listing = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         StringAssert.Contains("\"name\":\"Alpha\"", FolderEntry(listing.body, "Projects" + delimiter + "Alpha"));
      }

      [Test]
      [Description("The folder create refuses what IMAP CREATE refuses, with CREATE's own sentences")]
      public void TheFolderCreateRefusesWhatImapRefuses()
      {
         string delimiter = Delimiter();

         Assert.AreEqual(201, Http("POST", "/api/v1/me/folders", UserHeader(UserPassword), "{\"name\":\"Projects\"}").status);

         (int status, string body) again = Http("POST", "/api/v1/me/folders", UserHeader(UserPassword), "{\"name\":\"Projects\"}");
         Assert.AreEqual(409, again.status, "Body: " + again.body);
         StringAssert.Contains("Folder already exists.", again.body);

         (int status, string body) nameless = Http("POST", "/api/v1/me/folders", UserHeader(UserPassword), "{}");
         Assert.AreEqual(400, nameless.status, "Body: " + nameless.body);
         StringAssert.Contains("Folder name not specified.", nameless.body);

         (int status, string body) empty = Http("POST", "/api/v1/me/folders", UserHeader(UserPassword), "{\"name\":\"\"}");
         Assert.AreEqual(400, empty.status, "Body: " + empty.body);
         StringAssert.Contains("Folder name not specified.", empty.body);

         // A namespace prefix is not this account's to write, and IsValidFolderName
         // is what says so - the same answer CREATE gives.
         (int status, string body) namespaced = Http("POST", "/api/v1/me/folders", UserHeader(UserPassword),
            "{\"name\":\"#Public" + delimiter + "Shared\"}");
         Assert.AreEqual(400, namespaced.status, "Body: " + namespaced.body);
         StringAssert.Contains("CREATE The folder name is invalid.", namespaced.body);

         (int status, string body) hole = Http("POST", "/api/v1/me/folders", UserHeader(UserPassword),
            "{\"name\":\"A" + delimiter + delimiter + "B\"}");
         Assert.AreEqual(400, hole.status, "An empty element is not a folder name. Body: " + hole.body);
         StringAssert.Contains("CREATE The folder name is invalid.", hole.body);

         (int status, string body) nowhere = Http("POST", "/api/v1/me/folders", UserHeader(UserPassword),
            "{\"name\":\"X\",\"parent_id\":987654321}");
         Assert.AreEqual(404, nowhere.status, "Body: " + nowhere.body);
         StringAssert.Contains("Folder could not be found.", nowhere.body);

         (int status, string body) garbage = Http("POST", "/api/v1/me/folders", UserHeader(UserPassword), "not json");
         Assert.AreEqual(400, garbage.status, "Body: " + garbage.body);

         // Another account's folder cannot be a parent, and the administrator is
         // not an account at all.
         (int status, string body) theirs = Http("POST", "/api/v1/me/folders", BasicHeader(OtherAccount(), UserPassword),
            "{\"name\":\"Mine\",\"parent_id\":" + FolderIdOf("Projects") + "}");
         Assert.AreEqual(404, theirs.status, "Body: " + theirs.body);

         (int status, string body) admin = Http("POST", "/api/v1/me/folders", AdminHeader(), "{\"name\":\"Nope\"}");
         Assert.AreEqual(403, admin.status, "Body: " + admin.body);

         // The create is a write, so a browser session needs the header.
         string cookie = SignIn();
         Response bare = Raw("POST", "/api/v1/me/folders", null, "{\"name\":\"ViaSession\"}",
            "Cookie: hmailsession=" + cookie + "\r\n");
         Assert.AreEqual(403, bare.Status, "A create on a session needs X-Requested-With. " + bare.Body);
         Assert.IsFalse(ComHasRootFolder("ViaSession"), "Nothing was created.");

         Response withHeader = Raw("POST", "/api/v1/me/folders", null, "{\"name\":\"ViaSession\"}",
            "Cookie: hmailsession=" + cookie + "\r\nX-Requested-With: hMailServer\r\n");
         Assert.AreEqual(201, withHeader.Status, withHeader.Body);
         Assert.IsTrue(ComHasRootFolder("ViaSession"));
      }

      [Test]
      [Description("PUT /api/v1/me/folders/{id} renames as IMAP RENAME does: the subfolders follow, and a path moves the folder under a parent it makes")]
      public void FoldersAreRenamedThroughTheirRoute()
      {
         string delimiter = Delimiter();

         long projectsId = IdBefore(Http("POST", "/api/v1/me/folders", UserHeader(UserPassword), "{\"name\":\"Projects\"}").body, "\"path\":\"Projects\"");
         long alphaId = IdBefore(Http("POST", "/api/v1/me/folders", UserHeader(UserPassword),
            "{\"name\":\"Alpha\",\"parent_id\":" + projectsId + "}").body, "\"name\":\"Alpha\"");

         (int status, string body) renamed = Http("PUT", "/api/v1/me/folders/" + projectsId, UserHeader(UserPassword), "{\"name\":\"Ideas\"}");
         Assert.AreEqual(200, renamed.status, "Body: " + renamed.body);
         StringAssert.Contains("\"id\":" + projectsId, renamed.body);
         StringAssert.Contains("\"name\":\"Ideas\"", renamed.body);
         StringAssert.Contains("\"path\":\"Ideas\"", renamed.body);
         // The child kept its id and followed its parent, which is what RENAME
         // does and why nothing has to be done to it.
         StringAssert.Contains("\"id\":" + alphaId, renamed.body);
         StringAssert.Contains("\"path\":\"Ideas" + delimiter + "Alpha\"", renamed.body);

         Assert.IsFalse(ComHasRootFolder("Projects"), "The old name is gone.");
         IMAPFolder ideas = CustomAsserts.AssertFolderExists(_account.IMAPFolders, "Ideas");
         Assert.AreEqual(projectsId, (long) ideas.ID, "It is the same folder, renamed.");
         Assert.AreEqual(alphaId, (long) CustomAsserts.AssertFolderExists(ideas.SubFolders, "Alpha").ID);

         // A path moves it, making the parent it names when that is missing.
         (int status, string body) moved = Http("PUT", "/api/v1/me/folders/" + projectsId, UserHeader(UserPassword),
            "{\"name\":\"Archive" + delimiter + "Old\"}");
         Assert.AreEqual(200, moved.status, "Body: " + moved.body);
         StringAssert.Contains("\"path\":\"Archive" + delimiter + "Old\"", moved.body);
         StringAssert.Contains("\"path\":\"Archive" + delimiter + "Old" + delimiter + "Alpha\"", moved.body);

         IMAPFolder archive = CustomAsserts.AssertFolderExists(_account.IMAPFolders, "Archive");
         IMAPFolder old = CustomAsserts.AssertFolderExists(archive.SubFolders, "Old");
         Assert.AreEqual(projectsId, (long) old.ID);
         Assert.AreEqual((long) archive.ID, (long) old.ParentID);

         // And IMAP sees the same tree.
         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.IsTrue(imap.SelectFolder("Archive" + delimiter + "Old" + delimiter + "Alpha"));
         imap.Disconnect();
      }

      [Test]
      [Description("The folder rename refuses what IMAP RENAME refuses, with RENAME's own sentences")]
      public void TheFolderRenameRefusesWhatImapRefuses()
      {
         string delimiter = Delimiter();

         long inboxId = FolderIdOf("INBOX");
         long workId = IdBefore(Http("POST", "/api/v1/me/folders", UserHeader(UserPassword), "{\"name\":\"Work\"}").body, "\"path\":\"Work\"");
         Http("POST", "/api/v1/me/folders", UserHeader(UserPassword), "{\"name\":\"Taken\"}");

         (int status, string body) inbox = Http("PUT", "/api/v1/me/folders/" + inboxId, UserHeader(UserPassword), "{\"name\":\"NotInbox\"}");
         Assert.AreEqual(403, inbox.status, "Body: " + inbox.body);
         StringAssert.Contains("Cannot rename INBOX.", inbox.body);

         (int status, string body) toInbox = Http("PUT", "/api/v1/me/folders/" + workId, UserHeader(UserPassword), "{\"name\":\"inbox\"}");
         Assert.AreEqual(403, toInbox.status, "The name is compared without case, as IMAP compares it. Body: " + toInbox.body);
         StringAssert.Contains("Cannot rename INBOX.", toInbox.body);

         (int status, string body) taken = Http("PUT", "/api/v1/me/folders/" + workId, UserHeader(UserPassword), "{\"name\":\"Taken\"}");
         Assert.AreEqual(409, taken.status, "Body: " + taken.body);
         StringAssert.Contains("Target folder already exist.", taken.body);

         (int status, string body) intoItself = Http("PUT", "/api/v1/me/folders/" + workId, UserHeader(UserPassword),
            "{\"name\":\"Work" + delimiter + "Deeper\"}");
         Assert.AreEqual(400, intoItself.status, "Body: " + intoItself.body);
         StringAssert.Contains("A folder cannot be moved into one of its subfolders.", intoItself.body);

         (int status, string body) invalid = Http("PUT", "/api/v1/me/folders/" + workId, UserHeader(UserPassword),
            "{\"name\":\"#Public" + delimiter + "X\"}");
         Assert.AreEqual(400, invalid.status, "Body: " + invalid.body);
         StringAssert.Contains("The new folder name is invalid.", invalid.body);

         (int status, string body) unknown = Http("PUT", "/api/v1/me/folders/987654321", UserHeader(UserPassword), "{\"name\":\"X\"}");
         Assert.AreEqual(404, unknown.status, "Body: " + unknown.body);
         StringAssert.Contains("Folder could not be found.", unknown.body);

         (int status, string body) theirs = Http("PUT", "/api/v1/me/folders/" + workId, BasicHeader(OtherAccount(), UserPassword), "{\"name\":\"Mine\"}");
         Assert.AreEqual(404, theirs.status, "Another account's folder is not found, not forbidden. Body: " + theirs.body);
         Assert.IsTrue(ComHasRootFolder("Work"), "And nothing happened to it.");

         (int status, string body) admin = Http("PUT", "/api/v1/me/folders/" + workId, AdminHeader(), "{\"name\":\"Mine\"}");
         Assert.AreEqual(403, admin.status, "Body: " + admin.body);
      }

      [Test]
      [Description("DELETE /api/v1/me/folders/{id} deletes the folder with its subfolders and their messages, and refuses the inbox and a designated folder")]
      public void FoldersAreDeletedThroughTheirRoute()
      {
         string delimiter = Delimiter();

         long tempId = IdBefore(Http("POST", "/api/v1/me/folders", UserHeader(UserPassword), "{\"name\":\"Temp\"}").body, "\"path\":\"Temp\"");
         Http("POST", "/api/v1/me/folders", UserHeader(UserPassword), "{\"name\":\"Inner\",\"parent_id\":" + tempId + "}");

         Deliver(Address, "Filed away", "One.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);

         long inboxId = FolderIdOf("INBOX");
         long messageId = IdBefore(Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword)).body, "\"subject\":\"Filed away\"");
         long innerId = FolderIdOf("Temp" + delimiter + "Inner");
         Assert.AreEqual(200, Http("POST", "/api/v1/me/messages/" + messageId + "/move", UserHeader(UserPassword),
            "{\"folder_id\":" + innerId + "}").status);

         (int status, string body) gone = Http("DELETE", "/api/v1/me/folders/" + tempId, UserHeader(UserPassword));
         Assert.AreEqual(200, gone.status, "Body: " + gone.body);
         StringAssert.Contains("\"deleted\":true", gone.body);

         Assert.IsFalse(ComHasRootFolder("Temp"), "The folder and its subtree are gone.");

         (int status, string body) listing = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         Assert.IsFalse(listing.body.Contains("\"Temp\""), "Body: " + listing.body);
         Assert.IsFalse(listing.body.Contains("\"Inner\""), "The subfolder went with it: " + listing.body);

         (int status, string body) message = Http("GET", "/api/v1/me/messages/" + messageId, UserHeader(UserPassword));
         Assert.AreEqual(404, message.status, "And so did the message in it. Body: " + message.body);

         // The two refusals.
         (int status, string body) inbox = Http("DELETE", "/api/v1/me/folders/" + inboxId, UserHeader(UserPassword));
         Assert.AreEqual(403, inbox.status, "Body: " + inbox.body);
         StringAssert.Contains("You cannot delete the inbox.", inbox.body);

         // A folder the server designates - here by its name, which is how an
         // account that never sent CREATE ... USE gets a \Sent - is refused.
         Http("POST", "/api/v1/me/folders", UserHeader(UserPassword), "{\"name\":\"Sent\"}");
         long sentId = FolderIdOf("Sent");
         StringAssert.Contains("\"special_use\":\"\\\\Sent\"", FolderEntry(Http("GET", "/api/v1/me/folders", UserHeader(UserPassword)).body, "Sent"));

         (int status, string body) designated = Http("DELETE", "/api/v1/me/folders/" + sentId, UserHeader(UserPassword));
         Assert.AreEqual(403, designated.status, "Body: " + designated.body);
         StringAssert.Contains("designated for a special use", designated.body);
         Assert.IsTrue(ComHasRootFolder("Sent"));

         (int status, string body) unknown = Http("DELETE", "/api/v1/me/folders/987654321", UserHeader(UserPassword));
         Assert.AreEqual(404, unknown.status, "Body: " + unknown.body);

         (int status, string body) theirs = Http("DELETE", "/api/v1/me/folders/" + sentId, BasicHeader(OtherAccount(), UserPassword));
         Assert.AreEqual(404, theirs.status, "Body: " + theirs.body);

         (int status, string body) admin = Http("DELETE", "/api/v1/me/folders/" + sentId, AdminHeader());
         Assert.AreEqual(403, admin.status, "Body: " + admin.body);

         string cookie = SignIn();
         Response bare = Raw("DELETE", "/api/v1/me/folders/" + sentId, null, null, "Cookie: hmailsession=" + cookie + "\r\n");
         Assert.AreEqual(403, bare.Status, "A delete on a session needs X-Requested-With. " + bare.Body);
         Assert.IsTrue(ComHasRootFolder("Sent"));
      }

      // --------------------------------------------- the inline image, and ---
      // ------------------------------------------------- the change probe ----

      [Test]
      [Description("An inline image is listed with its declared type and its Content-ID, and the download answers under that type")]
      public void AnInlineImageCarriesItsTypeAndContentId()
      {
         // The smallest real PNG: one transparent pixel.
         byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

         string raw =
            "From: sender@example.com\r\n" +
            "To: " + Address + "\r\n" +
            "Subject: Inline image\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/related; boundary=\"b1\"; type=\"text/html\"\r\n" +
            "\r\n" +
            "--b1\r\n" +
            "Content-Type: text/html; charset=us-ascii\r\n" +
            "Content-Transfer-Encoding: 7bit\r\n" +
            "\r\n" +
            "<html><body>Look: <img src=\"cid:logo@example.com\"></body></html>\r\n" +
            "--b1\r\n" +
            "Content-Type: image/png; name=\"logo.png\"\r\n" +
            "Content-Transfer-Encoding: base64\r\n" +
            "Content-ID: <logo@example.com>\r\n" +
            "Content-Disposition: inline; filename=\"logo.png\"\r\n" +
            "\r\n" +
            Convert.ToBase64String(png, Base64FormattingOptions.InsertLineBreaks) + "\r\n" +
            "--b1--\r\n";

         SmtpClientSimulator.StaticSendRaw("sender@example.com", Address, raw);
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);

         long inboxId = FolderIdOf("INBOX");
         long messageId = IdBefore(Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword)).body,
            "\"subject\":\"Inline image\"");

         (int status, string body) message = Http("GET", "/api/v1/me/messages/" + messageId, UserHeader(UserPassword));
         Assert.AreEqual(200, message.status, "Body: " + message.body);

         // What the page needs to render it: the cid: reference in the HTML, the
         // same value in content_id with the angle brackets off, and a type it
         // can decide from before it fetches anything.
         StringAssert.Contains("cid:logo@example.com", message.body);
         StringAssert.Contains("\"content_type\":\"image/png\"", message.body);
         StringAssert.Contains("\"content_id\":\"logo@example.com\"", message.body);
         StringAssert.Contains("\"name\":\"logo.png\"", message.body);

         Response image = Raw("GET", "/api/v1/me/messages/" + messageId + "/attachments/0", UserHeader(UserPassword), null);
         Assert.AreEqual(200, image.Status, image.Body);
         Assert.AreEqual("image/png", image.Header("Content-Type"), "An img element pointed here has to get the declared type.");
         Assert.AreEqual("nosniff", image.Header("X-Content-Type-Options"));
         Assert.AreEqual(png, image.BodyBytes);
      }

      [Test]
      [Description("GET /api/v1/me/changes hands back a stable, opaque token and says changed only when this account's mailbox moved")]
      public void TheChangeProbeSaysWhenTheMailboxMoved()
      {
         (int status, string body) first = Http("GET", "/api/v1/me/changes", UserHeader(UserPassword));
         Assert.AreEqual(200, first.status, "Body: " + first.body);

         string token = Between(first.body, "\"token\":\"", "\"");
         Assert.AreEqual(64, token.Length, "The token is a SHA-256 in hex: " + first.body);
         Assert.IsFalse(first.body.Contains("\"changed\""), "changed is answered only when since was asked: " + first.body);

         long inboxId = FolderIdOf("INBOX");
         StringAssert.Contains("{\"id\":" + inboxId + ",\"count\":0,\"unseen\":0}", first.body);

         (int status, string body) again = Http("GET", "/api/v1/me/changes", UserHeader(UserPassword));
         Assert.AreEqual(token, Between(again.body, "\"token\":\"", "\""), "An unchanged mailbox stands still.");

         (int status, string body) unchanged = Http("GET", "/api/v1/me/changes?since=" + token, UserHeader(UserPassword));
         Assert.AreEqual(200, unchanged.status, "Body: " + unchanged.body);
         StringAssert.Contains("\"changed\":false", unchanged.body);

         Deliver(Address, "Something arrived", "One.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);

         (int status, string body) changed = Http("GET", "/api/v1/me/changes?since=" + token, UserHeader(UserPassword));
         StringAssert.Contains("\"changed\":true", changed.body);
         StringAssert.Contains("{\"id\":" + inboxId + ",\"count\":1,\"unseen\":1}", changed.body);

         string moved = Between(changed.body, "\"token\":\"", "\"");
         Assert.AreNotEqual(token, moved, "A delivery moves the token.");

         // A folder created moves it too, and reading a message moves it back to
         // neither of the two before.
         Http("POST", "/api/v1/me/folders", UserHeader(UserPassword), "{\"name\":\"Later\"}");
         (int status, string body) afterFolder = Http("GET", "/api/v1/me/changes?since=" + moved, UserHeader(UserPassword));
         StringAssert.Contains("\"changed\":true", afterFolder.body);
         string withFolder = Between(afterFolder.body, "\"token\":\"", "\"");

         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.IsTrue(imap.SelectFolder("INBOX"));
         Assert.IsTrue(imap.SetFlagOnMessage(1, true, "\\Seen"));
         imap.Disconnect();

         (int status, string body) afterSeen = Http("GET", "/api/v1/me/changes?since=" + withFolder, UserHeader(UserPassword));
         StringAssert.Contains("\"changed\":true", afterSeen.body);
         StringAssert.Contains("{\"id\":" + inboxId + ",\"count\":1,\"unseen\":0}", afterSeen.body);
         string settled = Between(afterSeen.body, "\"token\":\"", "\"");

         // Another account's mailbox never moves this one's.
         string other = OtherAccount();
         Deliver(other, "Not mine", "Two.");
         Pop3ClientSimulator.AssertMessageCount(other, UserPassword, 1);

         (int status, string body) stillSettled = Http("GET", "/api/v1/me/changes?since=" + settled, UserHeader(UserPassword));
         StringAssert.Contains("\"changed\":false", stillSettled.body);
         Assert.IsFalse(stillSettled.body.Contains("Not mine"), "The probe carries counts and nothing else: " + stillSettled.body);

         // Two accounts standing at the same shape do not share a token.
         (int status, string body) theirs = Http("GET", "/api/v1/me/changes", BasicHeader(other, UserPassword));
         Assert.AreEqual(200, theirs.status, "Body: " + theirs.body);
         Assert.AreNotEqual(settled, Between(theirs.body, "\"token\":\"", "\""));

         (int status, string body) theirsOnMine = Http("GET", "/api/v1/me/changes?since=" + settled, BasicHeader(other, UserPassword));
         StringAssert.Contains("\"changed\":true", theirsOnMine.body);

         // A since that is not a token this mailbox ever had reads as a change,
         // which is the answer that cannot lose a message.
         (int status, string body) nonsense = Http("GET", "/api/v1/me/changes?since=not-a-token", UserHeader(UserPassword));
         StringAssert.Contains("\"changed\":true", nonsense.body);

         (int status, string body) admin = Http("GET", "/api/v1/me/changes", AdminHeader());
         Assert.AreEqual(403, admin.status, "Body: " + admin.body);
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
         public byte[] BodyBytes = new byte[0];
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

      /// <summary>
      /// Asks for a body the server will refuse on its declared length, the way
      /// RFC 7231 section 5.1.1 says to ask for one: Expect: 100-continue, with
      /// the body withheld until the server says to send it. It never does - it
      /// answers 413 off the Content-Length - and because nothing was in flight
      /// the answer can be read.
      ///
      /// Sending the body first and then reading is not a reliable way to see a
      /// refusal, and that is the server behaving correctly rather than a race
      /// worth fixing: it refuses before reading the body, and closing a socket
      /// whose receive buffer still holds an unread request resets the
      /// connection, which discards the response with it. That surfaces here as
      /// a connection abort instead of a status.
      /// </summary>
      private static (int status, string body) RefusedBySize(string method, string path, string authorization, int declaredLength)
      {
         using (var client = new TcpClient())
         {
            client.Connect("127.0.0.1", RestPort);

            using (NetworkStream stream = client.GetStream())
            using (var memory = new MemoryStream())
            {
               var head = new StringBuilder();
               head.Append(method + " " + path + " HTTP/1.1\r\n");
               head.Append("Host: 127.0.0.1\r\n");
               if (authorization != null)
                  head.Append("Authorization: " + authorization + "\r\n");
               head.Append("Content-Type: application/json\r\n");
               head.Append("Content-Length: " + declaredLength + "\r\n");
               head.Append("Expect: 100-continue\r\n");
               head.Append("Connection: close\r\n\r\n");

               byte[] bytes = Encoding.ASCII.GetBytes(head.ToString());
               stream.Write(bytes, 0, bytes.Length);

               byte[] buffer = new byte[8192];
               int read;
               while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                  memory.Write(buffer, 0, read);

               string raw = Encoding.UTF8.GetString(memory.ToArray());
               int separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
               string first = raw.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
               string[] parts = first.Split(' ');
               int status = 0;
               if (parts.Length >= 2)
                  int.TryParse(parts[1], out status);

               return (status, separator >= 0 ? raw.Substring(separator + 4) : "");
            }
         }
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

               byte[] all = memory.ToArray();
               string raw = Encoding.UTF8.GetString(all);
               var response = new Response();

               int separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
               string head = separator >= 0 ? raw.Substring(0, separator) : raw;
               response.Body = separator >= 0 ? raw.Substring(separator + 4) : "";
               // The head is ASCII, so its length in characters is its length in
               // bytes; the body is kept as bytes too, for a download.
               if (separator >= 0)
               {
                  response.BodyBytes = new byte[all.Length - separator - 4];
                  Array.Copy(all, separator + 4, response.BodyBytes, 0, response.BodyBytes.Length);
               }

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
