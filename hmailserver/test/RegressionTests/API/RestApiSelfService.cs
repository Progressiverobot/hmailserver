// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
         StringAssert.Contains("'/html' + (allowed ? '?remote=1' : '')", script.Body, "The HTML part is the server's own document, pointed at by src; nothing is built into a srcdoc here.");
         StringAssert.DoesNotContain("frame.srcdoc", script.Body);
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

      [Test]
      [Description("A forged Authentication-Results field draws no verdict: only a field carrying this server's own authserv-id is read, and only while the server writes one")]
      public void AForgedAuthenticationResultsFieldShowsNoVerdict()
      {
         // With AuthenticationResultsEnabled off (the default), nothing is
         // stripped on receipt and nothing is written; a sender's own field
         // arrives intact - and must not be believed.
         string ownId = Environment.MachineName.ToLowerInvariant();
         var client = new SmtpClientSimulator();
         client.SendRaw("forger@example.com", Address,
            "From: forger@example.com\r\nTo: " + Address + "\r\nSubject: Forged verdicts\r\n" +
            "Authentication-Results: " + ownId + "; spf=pass smtp.mailfrom=example.com; dkim=pass header.d=example.com; dmarc=pass\r\n" +
            "Authentication-Results: other.example; spf=pass\r\n\r\nTrust me.\r\n");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         (int status, string body) page = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         long id = IdBefore(page.body, "\"subject\":\"Forged verdicts\"");
         (int status, string body) message = Http("GET", "/api/v1/me/messages/" + id, UserHeader(UserPassword));
         Assert.AreEqual(200, message.status, "Body: " + message.body);
         StringAssert.Contains("\"authentication\":{\"spf\":\"\",\"dkim\":\"\",\"dkim_domain\":\"\",\"dmarc\":\"\",\"results\":\"\"}", message.body);
         // The sender's display name does not decide the External badge.
         StringAssert.Contains("\"external\":true", message.body);
      }

      [Test]
      [Description("The HTML part is served as a document under its own policy: remote images blocked unless ?remote=1, an embedded image inlined, the message JSON saying whether anything remote is named; a search with a word repeated thirty times is the search with it once")]
      public void TheHtmlDocumentIsServedUnderItsOwnPolicy()
      {
         string pixel = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";
         var client = new SmtpClientSimulator();
         client.SendRaw("sender@example.com", Address,
            "From: sender@example.com\r\nTo: " + Address + "\r\nSubject: Pictures\r\nMIME-Version: 1.0\r\n" +
            "Content-Type: multipart/related; boundary=\"rel\"\r\n\r\n--rel\r\nContent-Type: text/html; charset=utf-8\r\n\r\n" +
            "<p>Embedded: <img src=\"cid:one@example.com\"> Remote: <img src=\"https://tracker.example.org/pixel.gif\"></p>\r\n" +
            "--rel\r\nContent-Type: image/png\r\nContent-ID: <one@example.com>\r\nContent-Transfer-Encoding: base64\r\n\r\n" + pixel + "\r\n--rel--\r\n");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         (int status, string body) page = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         long id = IdBefore(page.body, "\"subject\":\"Pictures\"");
         (int status, string body) message = Http("GET", "/api/v1/me/messages/" + id, UserHeader(UserPassword));
         Assert.AreEqual(200, message.status, "Body: " + message.body);
         StringAssert.Contains("\"html_remote\":true", message.body);

         Response blocked = Raw("GET", "/api/v1/me/messages/" + id + "/html", UserHeader(UserPassword), null);
         Assert.AreEqual(200, blocked.Status, blocked.Body);
         StringAssert.StartsWith("text/html", blocked.Header("Content-Type"));
         string policy = blocked.Header("Content-Security-Policy");
         StringAssert.Contains("img-src data:;", policy);
         StringAssert.Contains("sandbox", policy);
         StringAssert.Contains("frame-ancestors 'self'", policy);
         StringAssert.Contains("data:image/png;base64," + pixel, blocked.Body);
         StringAssert.Contains("https://tracker.example.org/pixel.gif", blocked.Body);
         Assert.AreEqual("nosniff", blocked.Header("X-Content-Type-Options"));

         Response allowed = Raw("GET", "/api/v1/me/messages/" + id + "/html?remote=1", UserHeader(UserPassword), null);
         Assert.AreEqual(200, allowed.Status, allowed.Body);
         StringAssert.Contains("img-src data: https: http:", allowed.Header("Content-Security-Policy"));

         (int status, string body) once = Http("GET", "/api/v1/me/search?q=Embedded", UserHeader(UserPassword));
         Assert.AreEqual(200, once.status, "Body: " + once.body);
         StringAssert.Contains("\"subject\":\"Pictures\"", once.body);
         string repeated = string.Join("+", Enumerable.Repeat("Embedded", 30));
         (int status, string body) many = Http("GET", "/api/v1/me/search?q=" + repeated, UserHeader(UserPassword));
         Assert.AreEqual(200, many.status, "Body: " + many.body);
         StringAssert.Contains("\"subject\":\"Pictures\"", many.body);
      }

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

      [Test]
      [Description("POST /move with to = archive | junk | trash | inbox files the message in the folder designated so, making it by that name when the account has none")]
      public void FilingByDesignationMakesTheFolderAndMovesTheMessage()
      {
         Deliver(Address, "Filed", "One.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         StringAssert.DoesNotContain("\"path\":\"Junk\"", tree.body);
         (int status, string body) page = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         long messageId = IdBefore(page.body, "\"subject\":\"Filed\"");
         string movePath = "/api/v1/me/messages/" + messageId + "/move";

         (int status, string body) bogus = Http("POST", movePath, UserHeader(UserPassword), "{\"to\":\"elsewhere\"}");
         Assert.AreEqual(400, bogus.status, "Body: " + bogus.body);

         (int status, string body) junked = Http("POST", movePath, UserHeader(UserPassword), "{\"to\":\"junk\"}");
         Assert.AreEqual(200, junked.status, "Body: " + junked.body);
         long inJunk = long.Parse(Between(junked.body, "\"id\":", ","));

         (int status, string body) after = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         string junk = FolderEntry(after.body, "Junk");
         StringAssert.Contains("\"special_use\":\"\\\\Junk\"", junk);
         long junkId = IdBefore(after.body, "\"path\":\"Junk\"");
         (int status, string body) junkPage = Http("GET", "/api/v1/me/folders/" + junkId + "/messages", UserHeader(UserPassword));
         StringAssert.Contains("\"subject\":\"Filed\"", junkPage.body);

         // Not junk: back to the inbox by name.
         (int status, string body) back = Http("POST", "/api/v1/me/messages/" + inJunk + "/move", UserHeader(UserPassword), "{\"to\":\"inbox\"}");
         Assert.AreEqual(200, back.status, "Body: " + back.body);
         long inInbox = long.Parse(Between(back.body, "\"id\":", ","));
         StringAssert.Contains("\"folder_id\":" + inboxId, back.body);

         (int status, string body) archived = Http("POST", "/api/v1/me/messages/" + inInbox + "/move", UserHeader(UserPassword), "{\"to\":\"archive\"}");
         Assert.AreEqual(200, archived.status, "Body: " + archived.body);
         long inArchive = long.Parse(Between(archived.body, "\"id\":", ","));
         (int status, string body) trashed = Http("POST", "/api/v1/me/messages/" + inArchive + "/move", UserHeader(UserPassword), "{\"to\":\"trash\"}");
         Assert.AreEqual(200, trashed.status, "Body: " + trashed.body);

         (int status, string body) all = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         StringAssert.Contains("\"special_use\":\"\\\\Archive\"", FolderEntry(all.body, "Archive"));
         StringAssert.Contains("\"special_use\":\"\\\\Trash\"", FolderEntry(all.body, "Trash"));
      }

      [Test]
      [Description("DELETE on a message with no Trash folder makes one and moves the message there")]
      public void DeletingWithNoTrashFolderMakesOne()
      {
         Deliver(Address, "Binned", "One.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         StringAssert.DoesNotContain("\"path\":\"Trash\"", tree.body);
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         (int status, string body) page = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         long messageId = IdBefore(page.body, "\"subject\":\"Binned\"");

         (int status, string body) deleted = Http("DELETE", "/api/v1/me/messages/" + messageId, UserHeader(UserPassword));
         Assert.AreEqual(200, deleted.status, "Body: " + deleted.body);
         StringAssert.Contains("\"deleted\":false,\"moved_to\":", deleted.body);

         (int status, string body) after = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         string trash = FolderEntry(after.body, "Trash");
         StringAssert.Contains("\"messages\":1,", trash);
      }

      [Test]
      [Description("POST /folders/{id}/empty expunges everything in a Junk or Trash folder and refuses any other folder")]
      public void EmptyingJunkOrTrashRemovesEverythingThere()
      {
         Deliver(Address, "Spam one", "One.");
         Deliver(Address, "Spam two", "Two.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 2);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         (int status, string body) page = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         foreach (long id in new[] { "Spam one", "Spam two" }.Select(subject => IdBefore(page.body, "\"subject\":\"" + subject + "\"")))
         {
            (int status, string body) junked = Http("POST", "/api/v1/me/messages/" + id + "/move", UserHeader(UserPassword), "{\"to\":\"junk\"}");
            Assert.AreEqual(200, junked.status, "Body: " + junked.body);
         }

         (int status, string body) refused = Http("POST", "/api/v1/me/folders/" + inboxId + "/empty", UserHeader(UserPassword));
         Assert.AreEqual(400, refused.status, "Body: " + refused.body);

         (int status, string body) after = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long junkId = IdBefore(after.body, "\"path\":\"Junk\"");
         StringAssert.Contains("\"messages\":2,", FolderEntry(after.body, "Junk"));

         string other = "other@" + _domain.Name;
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, other, UserPassword);
         (int status, string body) theirs = Http("POST", "/api/v1/me/folders/" + junkId + "/empty", BasicHeader(other, UserPassword));
         Assert.AreEqual(404, theirs.status, "Body: " + theirs.body);

         (int status, string body) emptied = Http("POST", "/api/v1/me/folders/" + junkId + "/empty", UserHeader(UserPassword));
         Assert.AreEqual(200, emptied.status, "Body: " + emptied.body);
         StringAssert.Contains("\"deleted\":2,", emptied.body);

         (int status, string body) empty = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         StringAssert.Contains("\"messages\":0,", FolderEntry(empty.body, "Junk"));
         (int status, string body) again = Http("POST", "/api/v1/me/folders/" + junkId + "/empty", UserHeader(UserPassword));
         Assert.AreEqual(200, again.status, "Body: " + again.body);
         StringAssert.Contains("\"deleted\":0,", again.body);
      }

      [Test]
      [Description("A message carries its raw headers, the SPF, DKIM and DMARC verdicts of its Authentication-Results, whether its sender is external, and its source as message/rfc822")]
      public void AMessageCarriesItsHeadersVerdictsAndSource()
      {
         SmtpClientSimulator.StaticSend("alice@example.com", Address, "From outside", "Hello from outside.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);
         string local = "colleague@" + _domain.Name;
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, local, UserPassword);
         SmtpClientSimulator.StaticSend(local, Address, "From inside", "Hello from inside.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 2);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         (int status, string body) page = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         long outside = IdBefore(page.body, "\"subject\":\"From outside\"");
         long inside = IdBefore(page.body, "\"subject\":\"From inside\"");

         (int status, string body) message = Http("GET", "/api/v1/me/messages/" + outside, UserHeader(UserPassword));
         Assert.AreEqual(200, message.status, "Body: " + message.body);
         StringAssert.Contains("\"external\":true", message.body);
         StringAssert.Contains("\"authentication\":{\"spf\":\"", message.body);
         StringAssert.Contains("Subject: From outside", message.body.Replace("\\r\\n", "\n"));
         StringAssert.Contains("\"headers\":\"", message.body);

         (int status, string body) colleague = Http("GET", "/api/v1/me/messages/" + inside, UserHeader(UserPassword));
         Assert.AreEqual(200, colleague.status, "Body: " + colleague.body);
         StringAssert.Contains("\"external\":false", colleague.body);

         (int status, string body) source = Http("GET", "/api/v1/me/messages/" + outside + "/source", UserHeader(UserPassword));
         Assert.AreEqual(200, source.status, "Body: " + source.body);
         StringAssert.Contains("Subject: From outside", source.body);
         StringAssert.Contains("Hello from outside.", source.body);

         string other = "other@" + _domain.Name;
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, other, UserPassword);
         (int status, string body) theirs = Http("GET", "/api/v1/me/messages/" + outside + "/source", BasicHeader(other, UserPassword));
         Assert.AreEqual(404, theirs.status, "Body: " + theirs.body);
      }

      [Test]
      [Description("q takes words, quoted phrases and from:, to:, subject:, has:attachment, before:, after:, in:, is:unread and is:flagged, in the search and in a folder listing")]
      public void SearchOperatorsNarrowTheHits()
      {
         SmtpClientSimulator.StaticSend("alice@example.com", Address, "Invoice March", "The March invoice is attached in spirit.");
         SmtpClientSimulator.StaticSend("bob@example.org", Address, "Invoice April", "The April invoice.");
         SmtpClientSimulator.StaticSend("carol@example.net", Address, "Holiday plans", "Sun and sand.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 3);

         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.IsTrue(imap.CreateFolder("Sent"));
         imap.Disconnect();
         (int status, string body) sent = Http("POST", "/api/v1/me/messages", UserHeader(UserPassword),
            "{\"to\":\"" + Address + "\",\"subject\":\"With a file\",\"text\":\"See the file.\",\"attachments\":[{\"name\":\"note.txt\",\"type\":\"text/plain\",\"data\":\"SGVsbG8=\"}]}");
         Assert.AreEqual(201, sent.status, "Body: " + sent.body);
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 4);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         (int status, string body) page = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         long april = IdBefore(page.body, "\"subject\":\"Invoice April\"");
         long holiday = IdBefore(page.body, "\"subject\":\"Holiday plans\"");
         Http("PUT", "/api/v1/me/messages/" + april + "/flags", UserHeader(UserPassword), "{\"seen\":true}");
         Http("PUT", "/api/v1/me/messages/" + holiday + "/flags", UserHeader(UserPassword), "{\"flagged\":true}");

         Func<string, string> search = (q) =>
         {
            (int status, string body) hit = Http("GET", "/api/v1/me/search?q=" + Uri.EscapeDataString(q), UserHeader(UserPassword));
            Assert.AreEqual(200, hit.status, "q=" + q + " Body: " + hit.body);
            return hit.body;
         };

         string byFrom = search("from:alice");
         StringAssert.Contains("Invoice March", byFrom);
         StringAssert.DoesNotContain("Invoice April", byFrom);

         string wordAndFrom = search("invoice from:bob");
         StringAssert.Contains("Invoice April", wordAndFrom);
         StringAssert.DoesNotContain("Invoice March", wordAndFrom);

         string bySubject = search("subject:holiday");
         StringAssert.Contains("Holiday plans", bySubject);
         StringAssert.DoesNotContain("Invoice", bySubject);

         string phrase = search("\"invoice april\"");
         StringAssert.Contains("Invoice April", phrase);
         StringAssert.DoesNotContain("Invoice March", phrase);

         string unread = search("is:unread invoice");
         StringAssert.Contains("Invoice March", unread);
         StringAssert.DoesNotContain("Invoice April", unread);

         string flagged = search("is:flagged");
         StringAssert.Contains("Holiday plans", flagged);
         StringAssert.DoesNotContain("Invoice", flagged);

         string dated = search("before:2000-01-01 invoice");
         StringAssert.DoesNotContain("Invoice", dated);
         string since = search("after:2000-01-01 invoice");
         StringAssert.Contains("Invoice March", since);
         StringAssert.Contains("Invoice April", since);

         string inFolder = search("in:inbox holiday");
         StringAssert.Contains("Holiday plans", inFolder);
         string elsewhere = search("in:nowhere holiday");
         StringAssert.DoesNotContain("Holiday plans", elsewhere);

         string withFile = search("has:attachment");
         StringAssert.Contains("With a file", withFile);
         StringAssert.DoesNotContain("Holiday plans", withFile);

         string toMe = search("to:" + Address + " april");
         StringAssert.Contains("Invoice April", toMe);

         (int status, string body) listed = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages?q=" + Uri.EscapeDataString("from:carol"), UserHeader(UserPassword));
         Assert.AreEqual(200, listed.status, "Body: " + listed.body);
         StringAssert.Contains("Holiday plans", listed.body);
         StringAssert.DoesNotContain("Invoice", listed.body);
      }

      [Test]
      [Description("An app password is made once with its clear text - the account's own password proving who asks - listed without it, signs in, may not mint or revoke another or end the account's sessions, and is removed")]
      public void AppPasswordsAreMadeListedAndRemoved()
      {
         (int status, string body) none = Http("GET", "/api/v1/me/app-passwords", UserHeader(UserPassword));
         Assert.AreEqual(200, none.status, "Body: " + none.body);
         Assert.AreEqual("{\"app_passwords\":[]}", none.body);

         (int status, string body) unnamed = Http("POST", "/api/v1/me/app-passwords", UserHeader(UserPassword), "{\"name\":\"  \",\"password\":\"" + UserPassword + "\"}");
         Assert.AreEqual(400, unnamed.status, "Body: " + unnamed.body);

         // Minting a credential asks for the account's own password again,
         // whatever authenticated the request: none is 400, a wrong one 403.
         (int status, string body) unproven = Http("POST", "/api/v1/me/app-passwords", UserHeader(UserPassword), "{\"name\":\"Phone\"}");
         Assert.AreEqual(400, unproven.status, "Body: " + unproven.body);
         StringAssert.Contains("password is required", unproven.body);
         (int status, string body) wrong = Http("POST", "/api/v1/me/app-passwords", UserHeader(UserPassword), "{\"name\":\"Phone\",\"password\":\"not-the-one\"}");
         Assert.AreEqual(403, wrong.status, "Body: " + wrong.body);
         (int status, string body) stillNone = Http("GET", "/api/v1/me/app-passwords", UserHeader(UserPassword));
         Assert.AreEqual("{\"app_passwords\":[]}", stillNone.body);

         (int status, string body) made = Http("POST", "/api/v1/me/app-passwords", UserHeader(UserPassword), "{\"name\":\"Phone\",\"password\":\"" + UserPassword + "\"}");
         Assert.AreEqual(201, made.status, "Body: " + made.body);
         StringAssert.Contains("\"name\":\"Phone\"", made.body);
         string secret = Between(made.body, "\"password\":\"", "\"");
         Assert.AreEqual(20, secret.Replace("-", "").Length, "A 20-character secret, grouped by dashes. Body: " + made.body);
         long id = long.Parse(Between(made.body, "\"id\":", ","));

         (int status, string body) listed = Http("GET", "/api/v1/me/app-passwords", UserHeader(UserPassword));
         Assert.AreEqual(200, listed.status, "Body: " + listed.body);
         StringAssert.Contains("\"name\":\"Phone\"", listed.body);
         StringAssert.DoesNotContain(secret, listed.body);
         StringAssert.DoesNotContain("\"password\"", listed.body);

         // It is a credential for the mailbox: IMAP takes it.
         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, secret), "The app password signs in over IMAP.");
         imap.Disconnect();

         // And for the mailbox only. A request it authenticates reads as the
         // account, but may not mint another - not even with the account's
         // own password in the body - nor revoke one, nor end the account
         // holder's browser sessions: a leaked phone password must not be
         // able to replace itself and lock the owner out.
         (int status, string body) asProgram = Http("GET", "/api/v1/me/app-passwords", BasicHeader(Address, secret));
         Assert.AreEqual(200, asProgram.status, "Body: " + asProgram.body);
         (int status, string body) mint = Http("POST", "/api/v1/me/app-passwords", BasicHeader(Address, secret), "{\"name\":\"Another\",\"password\":\"" + UserPassword + "\"}");
         Assert.AreEqual(403, mint.status, "Body: " + mint.body);
         (int status, string body) revoke = Http("DELETE", "/api/v1/me/app-passwords/" + id, BasicHeader(Address, secret));
         Assert.AreEqual(403, revoke.status, "Body: " + revoke.body);
         (int status, string body) endAll = Http("DELETE", "/api/v1/me/sessions", BasicHeader(Address, secret));
         Assert.AreEqual(403, endAll.status, "Body: " + endAll.body);
         (int status, string body) unchanged = Http("GET", "/api/v1/me/app-passwords", UserHeader(UserPassword));
         Assert.AreEqual(1, CountOf(unchanged.body, "\"name\":\""), "One app password, the first: " + unchanged.body);
         StringAssert.Contains("\"name\":\"Phone\"", unchanged.body);

         string other = "other@" + _domain.Name;
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, other, UserPassword);
         (int status, string body) theirs = Http("DELETE", "/api/v1/me/app-passwords/" + id, BasicHeader(other, UserPassword));
         Assert.AreEqual(404, theirs.status, "Body: " + theirs.body);

         (int status, string body) removed = Http("DELETE", "/api/v1/me/app-passwords/" + id, UserHeader(UserPassword));
         Assert.AreEqual(200, removed.status, "Body: " + removed.body);
         (int status, string body) again = Http("DELETE", "/api/v1/me/app-passwords/" + id, UserHeader(UserPassword));
         Assert.AreEqual(404, again.status, "Body: " + again.body);

         var gone = new ImapClientSimulator();
         Assert.IsFalse(gone.ConnectAndLogon(Address, secret), "A removed app password signs nothing in.");
      }

      [Test]
      [Description("POST /api/v1/me/messages with mime: the entity the page built is delivered byte for byte under this server's own headers, and refused when it is not 7-bit or not an entity")]
      public void AMessageBuiltOnThePageIsDeliveredAsGiven()
      {
         string entity = "Content-Type: multipart/signed; protocol=\"application/pkcs7-signature\"; micalg=sha-256; boundary=\"bb\"\r\n\r\n" +
            "--bb\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\nSigned on the page, caf=C3=A9.\r\n" +
            "--bb\r\nContent-Type: application/pkcs7-signature; name=\"smime.p7s\"\r\nContent-Transfer-Encoding: base64\r\nContent-Disposition: attachment; filename=\"smime.p7s\"\r\n\r\nAAECAwQFBgc=\r\n--bb--\r\n";
         (int status, string body) sent = Http("POST", "/api/v1/me/messages", UserHeader(UserPassword),
            "{\"to\":\"" + Address + "\",\"subject\":\"Built on the page\",\"mime\":" + JsonText(entity) + "}");
         Assert.AreEqual(201, sent.status, "Body: " + sent.body);
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         (int status, string body) list = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         Assert.AreEqual(200, list.status, "Body: " + list.body);
         long id = IdBefore(list.body, "\"subject\":\"Built on the page\"");

         (int status, string body) source = Http("GET", "/api/v1/me/messages/" + id + "/source", UserHeader(UserPassword));
         Assert.AreEqual(200, source.status, "Body: " + source.body);
         StringAssert.Contains(entity, source.body, "The entity is in the file exactly as given.");
         StringAssert.Contains("Subject: Built on the page\r\n", source.body);
         StringAssert.Contains("From: ", source.body);
         StringAssert.Contains("Date: ", source.body);
         StringAssert.Contains("Message-ID: <", source.body);
         StringAssert.Contains("MIME-Version: 1.0\r\n", source.body);
         Assert.IsTrue(source.body.IndexOf("\r\n\r\n", StringComparison.Ordinal) == source.body.IndexOf("\r\n\r\n--bb", StringComparison.Ordinal),
            "The first blank line is the entity's own: the server's headers and the entity's Content-Type make one block. Source: " + source.body);

         (int status, string body) message = Http("GET", "/api/v1/me/messages/" + id, UserHeader(UserPassword));
         Assert.AreEqual(200, message.status, "Body: " + message.body);
         StringAssert.Contains("Signed on the page, caf", message.body, "The server reads the text out of the multipart/signed entity.");

         (int status, string body) eightBit = Http("POST", "/api/v1/me/messages", UserHeader(UserPassword),
            "{\"to\":\"" + Address + "\",\"subject\":\"Not 7-bit\",\"mime\":\"Content-Type: text/plain\\r\\n\\r\\ncaf\u00e9\\r\\n\"}");
         Assert.AreEqual(400, eightBit.status, "Body: " + eightBit.body);
         StringAssert.Contains("7-bit", eightBit.body);

         (int status, string body) notAnEntity = Http("POST", "/api/v1/me/messages", UserHeader(UserPassword),
            "{\"to\":\"" + Address + "\",\"subject\":\"Not an entity\",\"mime\":\"Hello\\r\\n\"}");
         Assert.AreEqual(400, notAnEntity.status, "Body: " + notAnEntity.body);
         StringAssert.Contains("Content-Type", notAnEntity.body);
      }

      [Test]
      [Description("The account's S/MIME key store: an own certificate with its wrapped key and chain, a recipient's certificate, each added, replaced by fingerprint, listed and removed, with the shape of each field checked")]
      public void SmimeKeysAndCertificatesAreKeptForTheAccount()
      {
         (int status, string body) empty = Http("GET", "/api/v1/me/smime", UserHeader(UserPassword));
         Assert.AreEqual(200, empty.status, "Body: " + empty.body);
         StringAssert.Contains("\"own\":[]", empty.body);
         StringAssert.Contains("\"recipients\":[]", empty.body);

         string certificate = Convert.ToBase64String(new byte[] { 0x30, 0x03, 0x02, 0x01, 0x01 });
         string own = new string('a', 64);
         string key = "{\"kdf\":\"PBKDF2-SHA256\",\"iterations\":600000,\"salt\":\"AAAAAAAAAAAAAAAAAAAAAA==\",\"iv\":\"AAAAAAAAAAAAAAAA\",\"data\":\"AQIDBA==\"}";
         (int status, string body) added = Http("PUT", "/api/v1/me/smime/own", UserHeader(UserPassword),
            "{\"address\":\"" + Address + "\",\"name\":\"Me, Myself\",\"fingerprint\":\"" + own + "\",\"certificate\":\"" + certificate + "\",\"chain\":[\"" + certificate + "\"],\"key\":" + key + ",\"not_after\":2000000000}");
         Assert.AreEqual(201, added.status, "Body: " + added.body);
         StringAssert.Contains("\"fingerprint\":\"" + own + "\"", added.body);
         StringAssert.Contains("\"chain\":[\"" + certificate + "\"]", added.body);
         StringAssert.Contains("\"key\":" + key, added.body);
         StringAssert.Contains("\"not_after\":2000000000", added.body);

         (int status, string body) replaced = Http("PUT", "/api/v1/me/smime/own", UserHeader(UserPassword),
            "{\"address\":\"" + Address + "\",\"name\":\"Me again\",\"fingerprint\":\"" + own + "\",\"certificate\":\"" + certificate + "\",\"chain\":[],\"key\":" + key + "}");
         Assert.AreEqual(200, replaced.status, "Body: " + replaced.body);
         StringAssert.Contains("\"name\":\"Me again\"", replaced.body);
         StringAssert.Contains("\"chain\":[]", replaced.body);

         (int status, string body) badFingerprint = Http("PUT", "/api/v1/me/smime/own", UserHeader(UserPassword),
            "{\"address\":\"" + Address + "\",\"fingerprint\":\"ABC\",\"certificate\":\"" + certificate + "\",\"key\":" + key + "}");
         Assert.AreEqual(400, badFingerprint.status, "Body: " + badFingerprint.body);
         (int status, string body) noKey = Http("PUT", "/api/v1/me/smime/own", UserHeader(UserPassword),
            "{\"address\":\"" + Address + "\",\"fingerprint\":\"" + own + "\",\"certificate\":\"" + certificate + "\"}");
         Assert.AreEqual(400, noKey.status, "Body: " + noKey.body);
         (int status, string body) badCertificate = Http("PUT", "/api/v1/me/smime/own", UserHeader(UserPassword),
            "{\"address\":\"" + Address + "\",\"fingerprint\":\"" + own + "\",\"certificate\":\"not base64!\",\"key\":" + key + "}");
         Assert.AreEqual(400, badCertificate.status, "Body: " + badCertificate.body);

         string bob = new string('b', 64);
         (int status, string body) recipient = Http("PUT", "/api/v1/me/smime/recipients", UserHeader(UserPassword),
            "{\"address\":\"Bob@Example.test\",\"name\":\"Bob\",\"fingerprint\":\"" + bob + "\",\"certificate\":\"" + certificate + "\",\"not_after\":1900000000}");
         Assert.AreEqual(201, recipient.status, "Body: " + recipient.body);
         StringAssert.Contains("\"address\":\"bob@example.test\"", recipient.body);
         Assert.IsFalse(recipient.body.Contains("\"key\""), "A recipient's entry carries no key. Body: " + recipient.body);

         (int status, string body) listed = Http("GET", "/api/v1/me/smime", UserHeader(UserPassword));
         Assert.AreEqual(1, CountOf(listed.body, "\"fingerprint\":\"" + own + "\""), listed.body);
         Assert.AreEqual(1, CountOf(listed.body, "\"fingerprint\":\"" + bob + "\""), listed.body);
         StringAssert.Contains("\"limits\":{\"own\":20,\"recipients\":500}", listed.body);

         (int status, string body) removed = Http("DELETE", "/api/v1/me/smime/own/" + own, UserHeader(UserPassword));
         Assert.AreEqual(200, removed.status, "Body: " + removed.body);
         (int status, string body) again = Http("DELETE", "/api/v1/me/smime/own/" + own, UserHeader(UserPassword));
         Assert.AreEqual(404, again.status, "Body: " + again.body);
         (int status, string body) recipientGone = Http("DELETE", "/api/v1/me/smime/recipients/" + bob, UserHeader(UserPassword));
         Assert.AreEqual(200, recipientGone.status, "Body: " + recipientGone.body);
         (int status, string body) emptyAgain = Http("GET", "/api/v1/me/smime", UserHeader(UserPassword));
         StringAssert.Contains("\"own\":[]", emptyAgain.body);
         StringAssert.Contains("\"recipients\":[]", emptyAgain.body);
      }

      [Test]
      [Description("POST /api/v1/me/smime/chain: a certificate signed by no trusted root is answered untrusted with OpenSSL's reason, the subject read from it and the roots consulted counted; what is not a certificate is refused")]
      public void TheChainCheckAnswersForACertificateNoRootSigned()
      {
         using (RSA rsa = RSA.Create(2048))
         {
            var request = new CertificateRequest("CN=Nobody Example, E=nobody@example.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using (X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30)))
            {
               string der = Convert.ToBase64String(certificate.RawData);
               (int status, string body) answer = Http("POST", "/api/v1/me/smime/chain", UserHeader(UserPassword), "{\"certificates\":[\"" + der + "\"],\"purpose\":\"sign\"}");
               Assert.AreEqual(200, answer.status, "Body: " + answer.body);
               StringAssert.Contains("\"trusted\":false", answer.body);
               StringAssert.Contains("self", answer.body.ToLowerInvariant());
               StringAssert.Contains("Nobody Example", answer.body);
               StringAssert.Contains("\"roots\":", answer.body);
               Assert.IsFalse(answer.body.Contains("\"roots\":0,"), "The system's roots were consulted. Body: " + answer.body);
               StringAssert.Contains("\"not_after\":", answer.body);
            }
         }

         (int status, string body) notDer = Http("POST", "/api/v1/me/smime/chain", UserHeader(UserPassword), "{\"certificates\":[\"AAECAw==\"]}");
         Assert.AreEqual(200, notDer.status, "Body: " + notDer.body);
         StringAssert.Contains("\"trusted\":false", notDer.body);
         StringAssert.Contains("not X.509 DER", notDer.body);
         (int status, string body) notBase64 = Http("POST", "/api/v1/me/smime/chain", UserHeader(UserPassword), "{\"certificates\":[\"not base64!\"]}");
         Assert.AreEqual(400, notBase64.status, "Body: " + notBase64.body);
         (int status, string body) none = Http("POST", "/api/v1/me/smime/chain", UserHeader(UserPassword), "{\"certificates\":[]}");
         Assert.AreEqual(400, none.status, "Body: " + none.body);
      }

      private static string JsonText(string text)
      {
         return "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
      }

      [Test]
      [Description("The account's browser sessions are listed with the current one marked, ended one at a time, and ended all but the current one")]
      public void SessionsAreListedAndEnded()
      {
         string first = SignIn();
         string second = SignIn();

         Response listed = Raw("GET", "/api/v1/me/sessions", null, null, "Cookie: hmailsession=" + first + "\r\n");
         Assert.AreEqual(200, listed.Status, listed.Body);
         Assert.AreEqual(2, CountOf(listed.Body, "\"created_seconds_ago\""), listed.Body);
         Assert.AreEqual(1, CountOf(listed.Body, "\"current\":true"), listed.Body);

         // A caller on a password is current for none and sees them all.
         (int status, string body) byPassword = Http("GET", "/api/v1/me/sessions", UserHeader(UserPassword));
         Assert.AreEqual(200, byPassword.status, "Body: " + byPassword.body);
         Assert.AreEqual(0, CountOf(byPassword.body, "\"current\":true"), byPassword.body);

         Response ended = Raw("DELETE", "/api/v1/me/sessions", null, null, "Cookie: hmailsession=" + first + "\r\nX-Requested-With: hMailServer\r\n");
         Assert.AreEqual(200, ended.Status, ended.Body);
         StringAssert.Contains("\"ended\":1", ended.Body);

         Response secondGone = Raw("GET", "/api/v1/me", null, null, "Cookie: hmailsession=" + second + "\r\n");
         Assert.AreEqual(401, secondGone.Status, secondGone.Body);
         Response firstStays = Raw("GET", "/api/v1/me", null, null, "Cookie: hmailsession=" + first + "\r\n");
         Assert.AreEqual(200, firstStays.Status, firstStays.Body);

         string third = SignIn();
         Response two = Raw("GET", "/api/v1/me/sessions", null, null, "Cookie: hmailsession=" + first + "\r\n");
         Assert.AreEqual(2, CountOf(two.Body, "\"created_seconds_ago\""), two.Body);
         string otherId = "";
         foreach (string part in two.Body.Split(new[] { "{\"id\":\"" }, StringSplitOptions.RemoveEmptyEntries).Where(part => part.Contains("\"current\":false")))
         {
            otherId = part.Substring(0, 12);
         }
         Assert.AreEqual(12, otherId.Length, two.Body);

         Response bad = Raw("DELETE", "/api/v1/me/sessions/000000000000", null, null, "Cookie: hmailsession=" + first + "\r\nX-Requested-With: hMailServer\r\n");
         Assert.AreEqual(404, bad.Status, bad.Body);
         Response one = Raw("DELETE", "/api/v1/me/sessions/" + otherId, null, null, "Cookie: hmailsession=" + first + "\r\nX-Requested-With: hMailServer\r\n");
         Assert.AreEqual(200, one.Status, one.Body);
         Response thirdGone = Raw("GET", "/api/v1/me", null, null, "Cookie: hmailsession=" + third + "\r\n");
         Assert.AreEqual(401, thirdGone.Status, thirdGone.Body);
         Response onceMore = Raw("DELETE", "/api/v1/me/sessions/" + otherId, null, null, "Cookie: hmailsession=" + first + "\r\nX-Requested-With: hMailServer\r\n");
         Assert.AreEqual(404, onceMore.Status, onceMore.Body);
      }

      [Test]
      [Description("GET /api/v1/me/storage lists the quota, each folder's count and bytes, and the largest messages; emptying with older_than_days keeps the recent, deletes the old, and refuses a value it cannot read")]
      public void StorageListsFoldersAndTheLargestMessages()
      {
         Deliver(Address, "Small", "Tiny.");
         Deliver(Address, "Large", new string('x', 20000));
         Deliver(Address, "Medium", new string('y', 5000));
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 3);

         (int status, string body) storage = Http("GET", "/api/v1/me/storage", UserHeader(UserPassword));
         Assert.AreEqual(200, storage.status, "Body: " + storage.body);
         StringAssert.Contains("\"used_bytes\":", storage.body);
         StringAssert.Contains("\"path\":\"INBOX\",\"messages\":3,\"bytes\":", storage.body);
         int largestAt = storage.body.IndexOf("\"largest\":[", StringComparison.Ordinal);
         Assert.IsTrue(largestAt > 0, storage.body);
         string largest = storage.body.Substring(largestAt);
         Assert.IsTrue(largest.IndexOf("\"subject\":\"Large\"", StringComparison.Ordinal) < largest.IndexOf("\"subject\":\"Medium\"", StringComparison.Ordinal), "Largest first: " + largest);
         Assert.IsTrue(largest.IndexOf("\"subject\":\"Medium\"", StringComparison.Ordinal) < largest.IndexOf("\"subject\":\"Small\"", StringComparison.Ordinal), "Then the next: " + largest);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         (int status, string body) page = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         long small = IdBefore(page.body, "\"subject\":\"Small\"");
         long medium = IdBefore(page.body, "\"subject\":\"Medium\"");
         foreach (long id in new[] { small, medium })
         {
            (int status, string body) junked = Http("POST", "/api/v1/me/messages/" + id + "/move", UserHeader(UserPassword), "{\"to\":\"trash\"}");
            Assert.AreEqual(200, junked.status, "Body: " + junked.body);
         }
         (int status, string body) after = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long trashId = IdBefore(after.body, "\"path\":\"Trash\"");
         // A move gives a message a new id: the one to age is the Trash's.
         (int status, string body) inTrash = Http("GET", "/api/v1/me/folders/" + trashId + "/messages", UserHeader(UserPassword));
         small = IdBefore(inTrash.body, "\"subject\":\"Small\"");

         // Nothing is a week old yet.
         (int status, string body) kept = Http("POST", "/api/v1/me/folders/" + trashId + "/empty?older_than_days=7", UserHeader(UserPassword));
         Assert.AreEqual(200, kept.status, "Body: " + kept.body);
         StringAssert.Contains("\"deleted\":0,\"remaining\":0,", kept.body);
         StringAssert.Contains("\"older_than_days\":7", kept.body);

         // A value the route cannot read is refused - never taken as "everything",
         // which is what the filter being off would mean.
         foreach (string bad in new[] { "abc", "-1", "7x", "99999", "1e3" })
         {
            (int status, string body) refused = Http("POST", "/api/v1/me/folders/" + trashId + "/empty?older_than_days=" + bad, UserHeader(UserPassword));
            Assert.AreEqual(400, refused.status, "older_than_days=" + bad + " Body: " + refused.body);
         }
         (int status, string body) untouched = Http("GET", "/api/v1/me/folders/" + trashId + "/messages", UserHeader(UserPassword));
         Assert.AreEqual(2, CountOf(untouched.body, "\"subject\":\""), "Both still in Trash: " + untouched.body);

         // Small arrived years ago, says the store; the cache is emptied so
         // the route reads that afresh. Then a week's clean-up takes it and
         // leaves Medium, and "everything" takes Medium.
         SingletonProvider<TestSetup>.Instance.GetApp().Database.ExecuteSQL("update hm_messages set messagecreatetime = '2020-01-01 00:00:00' where messageid = " + small);
         SingletonProvider<TestSetup>.Instance.GetApp().Settings.Cache.Clear();
         (int status, string body) aged = Http("POST", "/api/v1/me/folders/" + trashId + "/empty?older_than_days=7", UserHeader(UserPassword));
         Assert.AreEqual(200, aged.status, "Body: " + aged.body);
         StringAssert.Contains("\"deleted\":1,\"remaining\":0,", aged.body);
         (int status, string body) left = Http("GET", "/api/v1/me/folders/" + trashId + "/messages", UserHeader(UserPassword));
         StringAssert.Contains("\"subject\":\"Medium\"", left.body);
         StringAssert.DoesNotContain("\"subject\":\"Small\"", left.body);
         (int status, string body) all = Http("POST", "/api/v1/me/folders/" + trashId + "/empty?older_than_days=0", UserHeader(UserPassword));
         Assert.AreEqual(200, all.status, "Body: " + all.body);
         StringAssert.Contains("\"deleted\":1,\"remaining\":0,", all.body);
      }

      private static int CountOf(string body, string needle)
      {
         int count = 0, at = 0;
         while ((at = body.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
         return count;
      }

      [Test]
      [Description("receipt: true on a send asks for a read receipt; the reader of such a message sends one with POST /receipt, an RFC 8098 notification queued from the account")]
      public void AReadReceiptIsAskedForAndSent()
      {
         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.IsTrue(imap.CreateFolder("Sent"));
         imap.Disconnect();

         (int status, string body) sent = Http("POST", "/api/v1/me/messages", UserHeader(UserPassword),
            "{\"to\":\"" + Address + "\",\"subject\":\"Please confirm\",\"text\":\"Did this arrive?\",\"receipt\":true}");
         Assert.AreEqual(201, sent.status, "Body: " + sent.body);
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         (int status, string body) page = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         long asked = IdBefore(page.body, "\"subject\":\"Please confirm\"");

         (int status, string body) message = Http("GET", "/api/v1/me/messages/" + asked, UserHeader(UserPassword));
         Assert.AreEqual(200, message.status, "Body: " + message.body);
         StringAssert.Contains("\"receipt_requested_by\":\"", message.body);
         StringAssert.Contains(Address, Between(message.body, "\"receipt_requested_by\":\"", "\""));

         (int status, string body) receipt = Http("POST", "/api/v1/me/messages/" + asked + "/receipt", UserHeader(UserPassword));
         Assert.AreEqual(201, receipt.status, "Body: " + receipt.body);
         StringAssert.Contains("\"queued\":true", receipt.body);
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 2);

         (int status, string body) again = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         long mdn = IdBefore(again.body, "\"subject\":\"Read: Please confirm\"");
         (int status, string body) source = Http("GET", "/api/v1/me/messages/" + mdn + "/source", UserHeader(UserPassword));
         Assert.AreEqual(200, source.status, "Body: " + source.body);
         StringAssert.Contains("report-type=disposition-notification", source.body);
         StringAssert.Contains("Disposition: manual-action/MDN-sent-manually; displayed", source.body);
         StringAssert.Contains("Final-Recipient: rfc822;" + Address, source.body);

         // A message that never asked gets no receipt.
         (int status, string body) none = Http("POST", "/api/v1/me/messages/" + mdn + "/receipt", UserHeader(UserPassword));
         Assert.AreEqual(400, none.status, "Body: " + none.body);
      }

      [Test]
      [Description("A one-click unsubscribe is refused when the list's https address is not a public one, and a mailto address carrying control characters is refused rather than written into a header")]
      public void AnUnsubscribeToAPrivateAddressOrWithControlCharactersIsRefused()
      {
         // The list's address is inside somebody's network: the POST is not made.
         SmtpClientSimulator.StaticSendRaw("news@example.com", Address,
            "From: news@example.com\r\nTo: " + Address + "\r\nSubject: Inside\r\nList-Unsubscribe: <https://10.0.0.1/leave?u=1>\r\nList-Unsubscribe-Post: List-Unsubscribe=One-Click\r\n\r\nA list on a private address.\r\n");
         // Percent-decoded, the mailto address would carry a line break into To:.
         SmtpClientSimulator.StaticSendRaw("news@example.com", Address,
            "From: news@example.com\r\nTo: " + Address + "\r\nSubject: Crafted\r\nList-Unsubscribe: <mailto:leave%0D%0ABcc:%20victim@" + _domain.Name + ">\r\n\r\nA crafted address.\r\n");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 2);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         (int status, string body) page = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         long inside = IdBefore(page.body, "\"subject\":\"Inside\"");
         long crafted = IdBefore(page.body, "\"subject\":\"Crafted\"");

         (int status, string body) refused = Http("POST", "/api/v1/me/messages/" + inside + "/unsubscribe", UserHeader(UserPassword));
         Assert.AreEqual(502, refused.status, "Body: " + refused.body);
         StringAssert.Contains("\"method\":\"one-click\",\"ok\":false,\"status\":0,", refused.body);
         StringAssert.Contains("is not a public address", refused.body);

         (int status, string body) notWritten = Http("POST", "/api/v1/me/messages/" + crafted + "/unsubscribe", UserHeader(UserPassword));
         Assert.AreEqual(400, notWritten.status, "Body: " + notWritten.body);
         StringAssert.Contains("control characters", notWritten.body);
      }

      [Test]
      [Description("POST /unsubscribe writes to the list's mailto with its subject; a message with no List-Unsubscribe is refused; the JSON names the headers")]
      public void AnUnsubscribeByMailIsQueued()
      {
         string leave = "leave@" + _domain.Name;
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, leave, UserPassword);
         SmtpClientSimulator.StaticSendRaw("news@example.com", Address,
            "From: news@example.com\r\nTo: " + Address + "\r\nSubject: Weekly\r\nList-Unsubscribe: <mailto:" + leave + "?subject=Unsubscribe%20me>\r\n\r\nThe weekly letter.\r\n");
         Deliver(Address, "Plain", "No list here.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 2);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         (int status, string body) page = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         long weekly = IdBefore(page.body, "\"subject\":\"Weekly\"");
         long plain = IdBefore(page.body, "\"subject\":\"Plain\"");

         (int status, string body) message = Http("GET", "/api/v1/me/messages/" + weekly, UserHeader(UserPassword));
         Assert.AreEqual(200, message.status, "Body: " + message.body);
         StringAssert.Contains("\"list_unsubscribe\":\"<mailto:" + leave, message.body);
         StringAssert.Contains("\"list_unsubscribe_post\":false", message.body);

         (int status, string body) refused = Http("POST", "/api/v1/me/messages/" + plain + "/unsubscribe", UserHeader(UserPassword));
         Assert.AreEqual(400, refused.status, "Body: " + refused.body);

         (int status, string body) done = Http("POST", "/api/v1/me/messages/" + weekly + "/unsubscribe", UserHeader(UserPassword));
         Assert.AreEqual(201, done.status, "Body: " + done.body);
         StringAssert.Contains("\"method\":\"mail\"", done.body);
         StringAssert.Contains("\"to\":\"" + leave + "\"", done.body);

         string received = Pop3ClientSimulator.AssertGetFirstMessageText(leave, UserPassword);
         StringAssert.Contains("Subject: Unsubscribe me", received);
         StringAssert.Contains("Auto-Submitted: auto-generated", received);
      }

      [Test]
      [Description("A draft scheduled with send_at is sent when its minute comes - the task run by the administrator's route - and the draft goes")]
      public void ADraftIsSentLaterOnTheMinute()
      {
         string other = "other@" + _domain.Name;
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, other, UserPassword);
         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.IsTrue(imap.CreateFolder("Sent"));
         imap.Disconnect();

         (int status, string body) draft = Http("POST", "/api/v1/me/drafts", UserHeader(UserPassword),
            "{\"to\":\"" + other + "\",\"subject\":\"Later\",\"text\":\"Sent when the minute comes.\"}");
         Assert.AreEqual(201, draft.status, "Body: " + draft.body);
         long draftId = long.Parse(Between(draft.body, "\"id\":", ","));

         (int status, string body) past = Http("POST", "/api/v1/me/drafts/" + draftId + "/schedule", UserHeader(UserPassword), "{\"send_at\":\"2000-01-01 09:00\"}");
         Assert.AreEqual(400, past.status, "Body: " + past.body);
         (int status, string body) junk = Http("POST", "/api/v1/me/drafts/" + draftId + "/schedule", UserHeader(UserPassword), "{\"send_at\":\"soon\"}");
         Assert.AreEqual(400, junk.status, "Body: " + junk.body);

         string at = DateTime.Now.AddSeconds(2).ToString("yyyy-MM-dd HH:mm:ss");
         (int status, string body) scheduled = Http("POST", "/api/v1/me/drafts/" + draftId + "/schedule", UserHeader(UserPassword), "{\"send_at\":\"" + at + "\"}");
         Assert.AreEqual(201, scheduled.status, "Body: " + scheduled.body);

         (int status, string body) listed = Http("GET", "/api/v1/me/scheduled", UserHeader(UserPassword));
         Assert.AreEqual(200, listed.status, "Body: " + listed.body);
         StringAssert.Contains("\"action\":\"send\"", listed.body);
         StringAssert.Contains("\"subject\":\"Later\"", listed.body);

         Thread.Sleep(3000);
         (int status, string body) ran = Http("POST", "/api/v1/scheduled/run", AdminHeader());
         Assert.AreEqual(200, ran.status, "Body: " + ran.body);
         StringAssert.Contains("\"ran\":1", ran.body);

         Pop3ClientSimulator.AssertMessageCount(other, UserPassword, 1);
         string received = Pop3ClientSimulator.AssertGetFirstMessageText(other, UserPassword);
         StringAssert.Contains("Subject: Later", received);
         StringAssert.DoesNotContain("Bcc:", received);

         (int status, string body) empty = Http("GET", "/api/v1/me/scheduled", UserHeader(UserPassword));
         Assert.AreEqual("{\"scheduled\":[]}", empty.body);
         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         StringAssert.Contains("\"messages\":0,", FolderEntry(tree.body, "Drafts"));
         StringAssert.Contains("\"messages\":1,", FolderEntry(tree.body, "Sent"));
      }

      [Test]
      [Description("A snoozed message waits in Snoozed and comes back to its folder unread when its minute comes; a cancelled snooze brings it back now")]
      public void ASnoozedMessageComesBackUnread()
      {
         Deliver(Address, "Later please", "Not now.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         (int status, string body) page = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         long messageId = IdBefore(page.body, "\"subject\":\"Later please\"");
         Http("PUT", "/api/v1/me/messages/" + messageId + "/flags", UserHeader(UserPassword), "{\"seen\":true}");

         string until = DateTime.Now.AddSeconds(2).ToString("yyyy-MM-dd HH:mm:ss");
         (int status, string body) snoozed = Http("POST", "/api/v1/me/messages/" + messageId + "/snooze", UserHeader(UserPassword), "{\"until\":\"" + until + "\"}");
         Assert.AreEqual(200, snoozed.status, "Body: " + snoozed.body);
         long moved = long.Parse(Between(snoozed.body, "\"message_id\":", ","));

         (int status, string body) after = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         StringAssert.Contains("\"messages\":0,", FolderEntry(after.body, "INBOX"));
         StringAssert.Contains("\"messages\":1,", FolderEntry(after.body, "Snoozed"));

         Thread.Sleep(3000);
         (int status, string body) ran = Http("POST", "/api/v1/scheduled/run", AdminHeader());
         Assert.AreEqual(200, ran.status, "Body: " + ran.body);

         (int status, string body) back = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         string entry = EntryFor(back.body, "Later please");
         StringAssert.Contains("\"seen\":false", entry);
         (int status, string body) again = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         StringAssert.Contains("\"messages\":0,", FolderEntry(again.body, "Snoozed"));

         // Cancelled: back now.
         long returned = IdBefore(back.body, "\"subject\":\"Later please\"");
         (int status, string body) far = Http("POST", "/api/v1/me/messages/" + returned + "/snooze", UserHeader(UserPassword),
            "{\"until\":\"" + DateTime.Now.AddHours(2).ToString("yyyy-MM-dd HH:mm") + "\"}");
         Assert.AreEqual(200, far.status, "Body: " + far.body);
         long schedId = long.Parse(Between(far.body, "\"id\":", ","));
         (int status, string body) cancelled = Http("DELETE", "/api/v1/me/scheduled/" + schedId, UserHeader(UserPassword));
         Assert.AreEqual(200, cancelled.status, "Body: " + cancelled.body);
         StringAssert.Contains("\"returned\":true", cancelled.body);
         (int status, string body) home = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         StringAssert.Contains("\"messages\":1,", FolderEntry(home.body, "INBOX"));
         (int status, string body) gone = Http("DELETE", "/api/v1/me/scheduled/" + schedId, UserHeader(UserPassword));
         Assert.AreEqual(404, gone.status, "Body: " + gone.body);
      }

      [Test]
      [Description("A folder downloads as mbox with a From_ line before each message, and a .eml posted to the folder is stored in it unread")]
      public void AFolderExportsAsMboxAndImportsAMessage()
      {
         Deliver(Address, "First of two", "One.");
         Deliver(Address, "Second of two", "From the start of a line, quoted.\r\nFrom here too.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 2);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");

         (int status, string body) mbox = Http("GET", "/api/v1/me/folders/" + inboxId + "/export", UserHeader(UserPassword));
         Assert.AreEqual(200, mbox.status, "Body: " + mbox.body);
         Assert.AreEqual(2, CountOf(mbox.body, "\r\nFrom sender@example.com ") + (mbox.body.StartsWith("From sender@example.com ") ? 1 : 0), mbox.body);
         StringAssert.Contains("Subject: First of two", mbox.body);
         StringAssert.Contains(">From here too.", mbox.body);

         (int status, string body) imported = Http("POST", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword),
            "From: keeper@example.org\r\nTo: " + Address + "\r\nSubject: Kept from elsewhere\r\n\r\nA message that lived in another mailbox.\r\n");
         Assert.AreEqual(201, imported.status, "Body: " + imported.body);
         long id = long.Parse(Between(imported.body, "\"id\":", ","));

         (int status, string body) page = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         string entry = EntryFor(page.body, "Kept from elsewhere");
         StringAssert.Contains("\"seen\":false", entry);
         (int status, string body) message = Http("GET", "/api/v1/me/messages/" + id, UserHeader(UserPassword));
         Assert.AreEqual(200, message.status, "Body: " + message.body);
         StringAssert.Contains("A message that lived in another mailbox.", message.body);

         (int status, string body) notOne = Http("POST", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword), "no header here");
         Assert.AreEqual(400, notOne.status, "Body: " + notOne.body);
      }

      [Test]
      [Description("html beside text on a send goes as multipart/alternative, and the message JSON carries both parts")]
      public void AMessageSentWithHtmlCarriesBothParts()
      {
         string other = "other@" + _domain.Name;
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, other, UserPassword);
         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         Assert.IsTrue(imap.CreateFolder("Sent"));
         imap.Disconnect();

         (int status, string body) sent = Http("POST", "/api/v1/me/messages", UserHeader(UserPassword),
            "{\"to\":\"" + other + "\",\"subject\":\"Formatted\",\"text\":\"Hello there\",\"html\":\"<p>Hello <b>there</b></p>\"}");
         Assert.AreEqual(201, sent.status, "Body: " + sent.body);
         // Counted over POP3, read over REST: the POP3 helper that returns the text
         // deletes the message it read, and the listing below would find nothing.
         Pop3ClientSimulator.AssertMessageCount(other, UserPassword, 1);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", BasicHeader(other, UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         (int status, string body) page = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", BasicHeader(other, UserPassword));
         long id = IdBefore(page.body, "\"subject\":\"Formatted\"");
         (int status, string body) source = Http("GET", "/api/v1/me/messages/" + id + "/source", BasicHeader(other, UserPassword));
         Assert.AreEqual(200, source.status, "Body: " + source.body);
         StringAssert.Contains("multipart/alternative", source.body);
         StringAssert.Contains("text/html", source.body);
         StringAssert.Contains("<b>there</b>", source.body);
         StringAssert.Contains("Hello there", source.body);
         (int status, string body) message = Http("GET", "/api/v1/me/messages/" + id, BasicHeader(other, UserPassword));
         Assert.AreEqual(200, message.status, "Body: " + message.body);
         StringAssert.Contains("\"text\":\"Hello there", message.body);
         StringAssert.Contains("<b>there</b>", message.body);
      }

      [Test]
      [Description("The web app manifest and the service worker are served beside the page, under its policy")]
      public void TheManifestAndTheServiceWorkerAreServed()
      {
         Response manifest = Raw("GET", "/portal.webmanifest", null, null);
         Assert.AreEqual(200, manifest.Status, manifest.Body);
         StringAssert.StartsWith("application/manifest+json", manifest.Header("Content-Type"));
         StringAssert.Contains("\"start_url\":\"/portal\"", manifest.Body);
         StringAssert.Contains("\"display\":\"standalone\"", manifest.Body);

         Response worker = Raw("GET", "/portal-sw.js", null, null);
         Assert.AreEqual(200, worker.Status, worker.Body);
         StringAssert.StartsWith("text/javascript", worker.Header("Content-Type"));
         Assert.AreEqual("/", worker.Header("Service-Worker-Allowed"));
         StringAssert.Contains("addEventListener('fetch'", worker.Body);
         StringAssert.Contains("hm-portal-shell", worker.Body);

         Response page = Raw("GET", "/portal", null, null);
         StringAssert.Contains("<link rel=\"manifest\" href=\"/portal.webmanifest\">", page.Body);
         StringAssert.Contains("manifest-src 'self'", page.Header("Content-Security-Policy"));
         StringAssert.Contains("worker-src 'self'", page.Header("Content-Security-Policy"));
      }

      [Test]
      [Description("The branding an administrator sets is read by anyone, a domain's own overrides the server's, and the page carries the server's for its first paint")]
      public void BrandingIsServedPubliclyAndPerDomain()
      {
         try
         {
            (int status, string body) set = Http("PUT", "/api/v1/portal/branding", AdminHeader(),
               "{\"name\":\"Acme Mail\",\"announcement\":\"Maintenance at nine.\",\"logo\":\"data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg'/%3E\"}");
            Assert.AreEqual(200, set.status, "Body: " + set.body);

            Response open = Raw("GET", "/api/v1/portal/branding", null, null);
            Assert.AreEqual(200, open.Status, open.Body);
            StringAssert.Contains("\"name\":\"Acme Mail\"", open.Body);
            StringAssert.Contains("\"announcement\":\"Maintenance at nine.\"", open.Body);

            Response page = Raw("GET", "/portal", null, null);
            StringAssert.Contains("id=\"branding-data\">{\"name\":\"Acme Mail\"", page.Body);

            (int status, string body) own = Http("PUT", "/api/v1/portal/branding", AdminHeader(),
               "{\"domain\":\"" + _domain.Name + "\",\"name\":\"Our Mail\"}");
            Assert.AreEqual(200, own.status, "Body: " + own.body);
            Response theirs = Raw("GET", "/api/v1/portal/branding?domain=" + _domain.Name, null, null);
            StringAssert.Contains("\"name\":\"Our Mail\"", theirs.Body);
            StringAssert.Contains("\"announcement\":\"Maintenance at nine.\"", theirs.Body);
            Response others = Raw("GET", "/api/v1/portal/branding?domain=elsewhere.example", null, null);
            StringAssert.Contains("\"name\":\"Acme Mail\"", others.Body);

            (int status, string body) bad = Http("PUT", "/api/v1/portal/branding", AdminHeader(), "{\"logo\":\"https://example.com/logo.png\"}");
            Assert.AreEqual(400, bad.status, "Body: " + bad.body);
            (int status, string body) user = Http("PUT", "/api/v1/portal/branding", UserHeader(UserPassword), "{\"name\":\"Mine\"}");
            Assert.AreNotEqual(200, user.status, "An account may not set the branding. Body: " + user.body);
         }
         finally
         {
            Http("PUT", "/api/v1/portal/branding", AdminHeader(), "{\"name\":\"\",\"announcement\":\"\",\"logo\":\"\"}");
            Http("PUT", "/api/v1/portal/branding", AdminHeader(), "{\"domain\":\"" + _domain.Name + "\",\"name\":\"\"}");
         }
      }

      [Test]
      [Description("An administrator's support session needs the user's consent, acts as the user, is recorded for the user, is marked in the session list and can be ended by the user")]
      public void SupportAccessNeedsConsentAndIsRecorded()
      {
         (int status, string body) refused = Http("POST", "/api/v1/accounts/" + Address + "/support-session", AdminHeader());
         Assert.AreEqual(403, refused.status, "Body: " + refused.body);
         (int status, string body) nobody = Http("POST", "/api/v1/accounts/nobody@" + _domain.Name + "/support-session", AdminHeader());
         Assert.AreEqual(404, nobody.status, "Body: " + nobody.body);

         (int status, string body) allowed = Http("PUT", "/api/v1/me/preferences", UserHeader(UserPassword), "{\"support_allowed\":\"1\"}");
         Assert.AreEqual(200, allowed.status, "Body: " + allowed.body);

         Response opened = Raw("POST", "/api/v1/accounts/" + Address + "/support-session", AdminHeader(), null);
         Assert.AreEqual(201, opened.Status, opened.Body);
         StringAssert.Contains("\"support\":true", opened.Body);
         string cookie = CookieOf(opened.Header("Set-Cookie"));
         Assert.AreEqual(64, cookie.Length, opened.Header("Set-Cookie"));

         Response asUser = Raw("GET", "/api/v1/me", null, null, "Cookie: hmailsession=" + cookie + "\r\n");
         Assert.AreEqual(200, asUser.Status, asUser.Body);
         StringAssert.Contains("\"address\":\"" + Address + "\"", asUser.Body);

         (int status, string body) prefs = Http("GET", "/api/v1/me/preferences", UserHeader(UserPassword));
         StringAssert.Contains("\"support_last\":\"", prefs.body);
         StringAssert.Contains(" by administrator", prefs.body);

         (int status, string body) sessions = Http("GET", "/api/v1/me/sessions", UserHeader(UserPassword));
         Assert.AreEqual(200, sessions.status, "Body: " + sessions.body);
         StringAssert.Contains("\"support\":true", sessions.body);

         (int status, string body) ended = Http("DELETE", "/api/v1/me/sessions", UserHeader(UserPassword));
         Assert.AreEqual(200, ended.status, "Body: " + ended.body);
         Response gone = Raw("GET", "/api/v1/me", null, null, "Cookie: hmailsession=" + cookie + "\r\n");
         Assert.AreEqual(401, gone.Status, gone.Body);

         Http("PUT", "/api/v1/me/preferences", UserHeader(UserPassword), "{\"support_allowed\":null}");
         (int status, string body) again = Http("POST", "/api/v1/accounts/" + Address + "/support-session", AdminHeader());
         Assert.AreEqual(403, again.status, "Body: " + again.body);
      }

      [Test]
      [Description("A file is recorded, sent in chunks at the offset the record has reached, listed with its downloads, fetched by anyone with the link once they give the password, and removed")]
      public void AFileIsSentAsALinkInChunksAndFetchedByAnyone()
      {
         (int status, string body) made = Http("POST", "/api/v1/me/files", UserHeader(UserPassword),
            "{\"name\":\"report.bin\",\"type\":\"application/octet-stream\",\"size\":300000,\"password\":\"open-sesame\"}");
         Assert.AreEqual(201, made.status, "Body: " + made.body);
         long id = long.Parse(Between(made.body, "\"id\":", ","));
         string token = Between(made.body, "\"token\":\"", "\"");
         Assert.AreEqual(64, token.Length, made.body);
         StringAssert.Contains("\"protected\":true", made.body);
         StringAssert.Contains("\"complete\":false", made.body);
         StringAssert.Contains("\"link\":\"/files/" + token + "\"", made.body);

         // Bytes, not text: a run of NULs in the middle, under the content type
         // a browser's upload declares - the server refuses a NUL in a text body.
         string first = new string('a', 100000) + new string('\0', 16) + new string('a', 99984);
         string second = new string('b', 100000);
         const string Binary = "Content-Type: application/octet-stream\r\n";
         Response part = Raw("PUT", "/api/v1/me/files/" + id + "/content?offset=0", UserHeader(UserPassword), first, Binary);
         Assert.AreEqual(200, part.Status, part.Body);
         StringAssert.Contains("\"stored\":200000,\"size\":300000,\"complete\":false", part.Body);

         Response repeated = Raw("PUT", "/api/v1/me/files/" + id + "/content?offset=0", UserHeader(UserPassword), second, Binary);
         Assert.AreEqual(409, repeated.Status, repeated.Body);
         StringAssert.Contains("\"stored\":200000", repeated.Body);

         Response early = Raw("GET", "/files/" + token, null, null);
         Assert.AreEqual(404, early.Status, "An unfinished file is not served. " + early.Body);

         Response rest = Raw("PUT", "/api/v1/me/files/" + id + "/content?offset=200000", UserHeader(UserPassword), second, Binary);
         Assert.AreEqual(200, rest.Status, rest.Body);
         StringAssert.Contains("\"stored\":300000,\"size\":300000,\"complete\":true", rest.Body);
         Response over = Raw("PUT", "/api/v1/me/files/" + id + "/content?offset=300000", UserHeader(UserPassword), "x", Binary);
         Assert.AreEqual(409, over.Status, over.Body);

         (int status, string body) list = Http("GET", "/api/v1/me/files", UserHeader(UserPassword));
         Assert.AreEqual(200, list.status, "Body: " + list.body);
         StringAssert.Contains("\"name\":\"report.bin\"", list.body);
         StringAssert.Contains("\"used_bytes\":300000", list.body);
         StringAssert.Contains("\"downloads\":0", list.body);
         StringAssert.Contains("\"link_above_kb\":", list.body);

         Response form = Raw("GET", "/files/" + token, null, null);
         Assert.AreEqual(200, form.Status, form.Body);
         StringAssert.Contains("text/html", form.Header("Content-Type"));
         StringAssert.Contains("name=\"password\"", form.Body);
         StringAssert.Contains("report.bin", form.Body);

         Response wrong = Raw("POST", "/files/" + token, null, "password=nope");
         Assert.AreEqual(403, wrong.Status, wrong.Body);
         StringAssert.Contains("not right", wrong.Body);

         Response fetched = Raw("POST", "/files/" + token, null, "password=open-sesame");
         Assert.AreEqual(200, fetched.Status, fetched.Body.Length > 200 ? fetched.Body.Substring(0, 200) : fetched.Body);
         Assert.AreEqual(first + second, fetched.Body);
         StringAssert.Contains("attachment; filename=\"report.bin\"", fetched.Header("Content-Disposition"));
         StringAssert.Contains("application/octet-stream", fetched.Header("Content-Type"));
         StringAssert.Contains("nosniff", fetched.Header("X-Content-Type-Options"));

         list = Http("GET", "/api/v1/me/files", UserHeader(UserPassword));
         StringAssert.Contains("\"downloads\":1", list.body);

         (int status, string body) removed = Http("DELETE", "/api/v1/me/files/" + id, UserHeader(UserPassword));
         Assert.AreEqual(200, removed.status, "Body: " + removed.body);
         Response gone = Raw("GET", "/files/" + token, null, null);
         Assert.AreEqual(404, gone.Status, gone.Body);
         (int status, string body) twice = Http("DELETE", "/api/v1/me/files/" + id, UserHeader(UserPassword));
         Assert.AreEqual(404, twice.status, "Body: " + twice.body);
      }

      [Test]
      [Description("A link lives the days it was given and is swept when they are up, an open file needs no password and can be given one later, and the policy is the domain's own over the server's")]
      public void ALinkExpiresIsSweptAndThePolicyIsTheDomains()
      {
         (int status, string body) dead = Http("POST", "/api/v1/me/files", UserHeader(UserPassword), "{\"name\":\"gone.txt\",\"type\":\"text/plain\",\"size\":5,\"days\":0}");
         Assert.AreEqual(201, dead.status, "Body: " + dead.body);
         long deadId = long.Parse(Between(dead.body, "\"id\":", ","));
         string deadToken = Between(dead.body, "\"token\":\"", "\"");
         StringAssert.Contains("\"expired\":true", dead.body);
         Response deadBytes = Raw("PUT", "/api/v1/me/files/" + deadId + "/content?offset=0", UserHeader(UserPassword), "hello");
         Assert.AreEqual(200, deadBytes.Status, deadBytes.Body);
         Response expired = Raw("GET", "/files/" + deadToken, null, null);
         Assert.AreEqual(410, expired.Status, expired.Body);

         (int status, string body) swept = Http("POST", "/api/v1/scheduled/run", AdminHeader());
         Assert.AreEqual(200, swept.status, "Body: " + swept.body);
         StringAssert.Contains("\"files_removed\":", swept.body);
         (int status, string body) list = Http("GET", "/api/v1/me/files", UserHeader(UserPassword));
         Assert.IsFalse(list.body.Contains(deadToken), "The expired file is gone from the list. Body: " + list.body);

         (int status, string body) open = Http("POST", "/api/v1/me/files", UserHeader(UserPassword), "{\"name\":\"notes.txt\",\"type\":\"text/plain\",\"size\":5}");
         Assert.AreEqual(201, open.status, "Body: " + open.body);
         long openId = long.Parse(Between(open.body, "\"id\":", ","));
         string openToken = Between(open.body, "\"token\":\"", "\"");
         StringAssert.Contains("\"expired\":false", open.body);
         StringAssert.Contains("\"protected\":false", open.body);
         try
         {
            Response bytes = Raw("PUT", "/api/v1/me/files/" + openId + "/content?offset=0", UserHeader(UserPassword), "hello");
            Assert.AreEqual(200, bytes.Status, bytes.Body);
            StringAssert.Contains("\"complete\":true", bytes.Body);

            Response fetched = Raw("GET", "/files/" + openToken, null, null);
            Assert.AreEqual(200, fetched.Status, fetched.Body);
            Assert.AreEqual("hello", fetched.Body);
            StringAssert.Contains("notes.txt", fetched.Header("Content-Disposition"));
            StringAssert.Contains("text/plain", fetched.Header("Content-Type"));
            Response posted = Raw("POST", "/files/" + openToken, null, "password=x");
            Assert.AreEqual(405, posted.Status, posted.Body);

            (int status, string body) locked = Http("PUT", "/api/v1/me/files/" + openId, UserHeader(UserPassword), "{\"password\":\"later\"}");
            Assert.AreEqual(200, locked.status, "Body: " + locked.body);
            StringAssert.Contains("\"protected\":true", locked.body);
            Response form = Raw("GET", "/files/" + openToken, null, null);
            Assert.AreEqual(200, form.Status, form.Body);
            StringAssert.Contains("name=\"password\"", form.Body);
            (int status, string body) unlocked = Http("PUT", "/api/v1/me/files/" + openId, UserHeader(UserPassword), "{\"password\":\"\"}");
            StringAssert.Contains("\"protected\":false", unlocked.body);
            Response again = Raw("GET", "/files/" + openToken, null, null);
            Assert.AreEqual("hello", again.Body);

            (int status, string body) tooBig = Http("POST", "/api/v1/me/files", UserHeader(UserPassword), "{\"name\":\"huge.bin\",\"size\":629145600}");
            Assert.AreEqual(413, tooBig.status, "Body: " + tooBig.body);
            (int status, string body) noSize = Http("POST", "/api/v1/me/files", UserHeader(UserPassword), "{\"name\":\"none.bin\"}");
            Assert.AreEqual(400, noSize.status, "Body: " + noSize.body);

            (int status, string body) set = Http("PUT", "/api/v1/portal/files", AdminHeader(), "{\"domain\":\"" + _domain.Name + "\",\"link_above_kb\":1,\"days\":3}");
            Assert.AreEqual(200, set.status, "Body: " + set.body);
            StringAssert.Contains("\"link_above_kb\":1,\"days\":3", set.body);
            list = Http("GET", "/api/v1/me/files", UserHeader(UserPassword));
            StringAssert.Contains("\"link_above_kb\":1,\"days\":3", list.body);
            (int status, string body) servers = Http("GET", "/api/v1/portal/files", AdminHeader());
            Assert.AreEqual(200, servers.status, "Body: " + servers.body);
            StringAssert.Contains("\"link_above_kb\":8192,\"days\":14", servers.body);
            (int status, string body) refused = Http("PUT", "/api/v1/portal/files", UserHeader(UserPassword), "{\"days\":1}");
            Assert.AreNotEqual(200, refused.status, "An account may not set the policy. Body: " + refused.body);
            (int status, string body) bad = Http("PUT", "/api/v1/portal/files", AdminHeader(), "{\"days\":365}");
            Assert.AreEqual(400, bad.status, "Body: " + bad.body);
         }
         finally
         {
            Http("PUT", "/api/v1/portal/files", AdminHeader(), "{\"domain\":\"" + _domain.Name + "\",\"link_above_kb\":null,\"days\":null}");
            Http("DELETE", "/api/v1/me/files/" + openId, UserHeader(UserPassword));
         }
      }

      [Test]
      [Description("IMAP keywords are stored, shown by FETCH and the STORE reply, listed by SELECT, advertised with \\*, searched, carried by COPY, taken by APPEND, and seen and set over REST")]
      public void KeywordsAreStoredFetchedSearchedCopiedAndSeenOverRest()
      {
         Deliver(Address, "Tagged", "One.");
         Deliver(Address, "Plain", "Two.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 2);

         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
         string selected;
         Assert.IsTrue(imap.SelectFolder("INBOX", out selected));
         StringAssert.Contains("PERMANENTFLAGS (\\Deleted \\Seen \\Draft \\Answered \\Flagged \\*)", selected);

         string stored = imap.SendSingleCommand("A1 STORE 1 +FLAGS ($Label1 Work)");
         StringAssert.Contains("FLAGS ($Label1 Work)", stored);
         StringAssert.Contains("A1 OK", stored);
         Assert.AreEqual("1", imap.Search("KEYWORD Work"));
         Assert.AreEqual("1", imap.Search("KEYWORD work"));
         Assert.AreEqual("2", imap.Search("UNKEYWORD Work"));
         Assert.AreEqual("2", imap.Search("NOT KEYWORD $Label1"));
         string fetched = imap.Fetch("1 (FLAGS)");
         StringAssert.Contains("$Label1 Work", fetched);

         string removed = imap.SendSingleCommand("A2 STORE 1 -FLAGS (Work)");
         StringAssert.Contains("FLAGS ($Label1)", removed);
         string replaced = imap.SendSingleCommand("A3 STORE 1 FLAGS (\\Seen Home)");
         StringAssert.Contains("FLAGS (\\Seen Home)", replaced);
         string refused = imap.SendSingleCommand("A4 STORE 1 +FLAGS (Not(Valid))");
         StringAssert.Contains("A4 BAD", refused);
         string reselected;
         Assert.IsTrue(imap.SelectFolder("INBOX", out reselected));
         StringAssert.Contains("* FLAGS (\\Deleted \\Seen \\Draft \\Answered \\Flagged Home)", reselected);

         Assert.IsTrue(imap.CreateFolder("Kept"));
         Assert.IsTrue(imap.Copy(1, "Kept"));
         string appended = imap.SendSingleCommandWithLiteral("A5 APPEND Kept (\\Seen Appended) {30}", "Subject: Added\r\n\r\nBy APPEND.\r\n");
         StringAssert.Contains("A5 OK", appended);
         Assert.IsTrue(imap.SelectFolder("Kept"));
         string copied = imap.Fetch("1 (FLAGS)");
         StringAssert.Contains("Home", copied);
         string added = imap.Fetch("2 (FLAGS)");
         StringAssert.Contains("Appended", added);
         imap.Disconnect();

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         (int status, string body) list = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
         Assert.AreEqual(200, list.status, "Body: " + list.body);
         StringAssert.Contains("\"keywords\":[\"Home\"]", list.body);
         long tagged = IdBefore(list.body, "\"subject\":\"Tagged\"");

         (int status, string body) changed = Http("PUT", "/api/v1/me/messages/" + tagged + "/flags", UserHeader(UserPassword), "{\"keywords_add\":[\"Urgent\"],\"keywords_remove\":[\"Home\"]}");
         Assert.AreEqual(200, changed.status, "Body: " + changed.body);
         StringAssert.Contains("\"keywords\":[\"Urgent\"]", changed.body);
         (int status, string body) found = Http("GET", "/api/v1/me/search?q=label:urgent", UserHeader(UserPassword));
         Assert.AreEqual(200, found.status, "Body: " + found.body);
         StringAssert.Contains("\"subject\":\"Tagged\"", found.body);
         // In the INBOX, where the label came off; the copy in Kept keeps its Home.
         (int status, string body) none = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages?q=label:home", UserHeader(UserPassword));
         Assert.AreEqual(200, none.status, "Body: " + none.body);
         Assert.IsFalse(none.body.Contains("\"subject\":\"Tagged\""), "The label was taken off. Body: " + none.body);
         (int status, string body) bad = Http("PUT", "/api/v1/me/messages/" + tagged + "/flags", UserHeader(UserPassword), "{\"keywords_add\":[\"two words\"]}");
         Assert.AreEqual(400, bad.status, "Body: " + bad.body);
      }

      [Test]
      [Description("A Sieve addflag at delivery stores a keyword on the message, which IMAP and REST both show")]
      public void ASieveAddflagStoresAKeywordAtDelivery()
      {
         try
         {
            (int status, string body) set = Http("PUT", "/api/v1/me/filters", UserHeader(UserPassword),
               "{\"script\":\"require [\\\"imap4flags\\\"];\\nif header :contains \\\"subject\\\" \\\"invoice\\\" { addflag \\\"Receipts\\\"; }\\n\"}");
            Assert.AreEqual(200, set.status, "Body: " + set.body);

            Deliver(Address, "Invoice 42", "Pay me.");
            Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);

            var imap = new ImapClientSimulator();
            Assert.IsTrue(imap.ConnectAndLogon(Address, UserPassword));
            Assert.IsTrue(imap.SelectFolder("INBOX"));
            string flags = imap.Fetch("1 (FLAGS)");
            StringAssert.Contains("Receipts", flags);
            imap.Disconnect();

            (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
            long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
            (int status, string body) list = Http("GET", "/api/v1/me/folders/" + inboxId + "/messages", UserHeader(UserPassword));
            StringAssert.Contains("\"keywords\":[\"Receipts\"]", list.body);
         }
         finally
         {
            Http("PUT", "/api/v1/me/filters", UserHeader(UserPassword), "{\"script\":\"\"}");
         }
      }

      [Test]
      [Description("A catalogue is served to anyone and cached, an unknown language is not, and the page carries the catalogue for the request's Accept-Language - none for English")]
      public void TheCatalogueIsServedAndThePageCarriesTheNegotiatedOne()
      {
         Response german = Raw("GET", "/portal-lang/de.json", null, null);
         Assert.AreEqual(200, german.Status, german.Body.Length > 200 ? german.Body.Substring(0, 200) : german.Body);
         StringAssert.Contains("application/json", german.Header("Content-Type"));
         StringAssert.Contains("public, max-age=86400", german.Header("Cache-Control"));
         StringAssert.StartsWith("{\"", german.Body);
         StringAssert.Contains("\"Sign in\":\"", german.Body);

         Response anyCase = Raw("GET", "/portal-lang/DE.json", null, null);
         Assert.AreEqual(200, anyCase.Status, anyCase.Body.Length > 200 ? anyCase.Body.Substring(0, 200) : anyCase.Body);
         Response unknown = Raw("GET", "/portal-lang/xx.json", null, null);
         Assert.AreEqual(404, unknown.Status, unknown.Body);
         Response english = Raw("GET", "/portal-lang/en.json", null, null);
         Assert.AreEqual(404, english.Status, "English is the page itself. " + english.Body);

         Response page = Raw("GET", "/portal", null, null, "Accept-Language: de-DE,de;q=0.9,en;q=0.5\r\n");
         Assert.AreEqual(200, page.Status, page.Body.Length > 200 ? page.Body.Substring(0, 200) : page.Body);
         StringAssert.Contains("id=\"lang-data\" data-lang=\"de\"", page.Body);
         // A catalogue of its own since 12 September 2026: pt-PT is pt-PT. A
         // Portuguese the page lacks still falls back to Brazil, and zh-TW to
         // zh-Hans.
         Response regional = Raw("GET", "/portal", null, null, "Accept-Language: pt-PT, zh-TW;q=0.8\r\n");
         StringAssert.Contains("data-lang=\"pt-PT\"", regional.Body);
         Response fallback = Raw("GET", "/portal", null, null, "Accept-Language: pt-AO, zh-TW;q=0.8\r\n");
         StringAssert.Contains("data-lang=\"pt-BR\"", fallback.Body);
         Response chinese = Raw("GET", "/portal", null, null, "Accept-Language: zh-TW\r\n");
         StringAssert.Contains("data-lang=\"zh-Hans\"", chinese.Body);
         Response plain = Raw("GET", "/portal", null, null);
         Assert.IsFalse(plain.Body.Contains("id=\"lang-data\""), "No catalogue for English.");
         Response englishFirst = Raw("GET", "/portal", null, null, "Accept-Language: en-GB,de;q=0.7\r\n");
         Assert.IsFalse(englishFirst.Body.Contains("id=\"lang-data\""), "English before German is the page as it is.");
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
      [Description("DELETE /api/v1/me/messages/{id} moves to the Trash folder, making one when the account has none; a delete of what is already in Trash, or on request, is final")]
      public void DeleteGoesToTheTrashWhenThereIsOne()
      {
         Deliver(Address, "First", "One.");
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 1);

         (int status, string body) tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         long inboxId = IdBefore(tree.body, "\"path\":\"INBOX\"");
         Assert.IsFalse(tree.body.Contains("\"path\":\"Trash\""), "No Trash to begin with. Body: " + tree.body);
         string listPath = "/api/v1/me/folders/" + inboxId + "/messages";
         long first = IdBefore(Http("GET", listPath, UserHeader(UserPassword)).body, "\"subject\":\"First\"");

         // No Trash yet: the delete makes one and moves the message there.
         (int status, string body) gone = Http("DELETE", "/api/v1/me/messages/" + first, UserHeader(UserPassword));
         Assert.AreEqual(200, gone.status, "Body: " + gone.body);
         StringAssert.Contains("\"deleted\":false,\"moved_to\":", gone.body);
         long firstInTrash = long.Parse(Between(gone.body, "\"id\":", "}"));
         Pop3ClientSimulator.AssertMessageCount(Address, UserPassword, 0);
         tree = Http("GET", "/api/v1/me/folders", UserHeader(UserPassword));
         StringAssert.Contains("\"special_use\":\"\\\\Trash\"", FolderEntry(tree.body, "Trash"));

         // In Trash already: the delete is final, and a second one finds nothing.
         (int status, string body) finalDelete = Http("DELETE", "/api/v1/me/messages/" + firstInTrash, UserHeader(UserPassword));
         Assert.AreEqual(200, finalDelete.status, "Body: " + finalDelete.body);
         StringAssert.Contains("\"deleted\":true", finalDelete.body);
         (int status, string body) again = Http("DELETE", "/api/v1/me/messages/" + firstInTrash, UserHeader(UserPassword));
         Assert.AreEqual(404, again.status, "Body: " + again.body);
         ImapClientSimulator imap;

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
                  // JSON unless the caller named the type itself (a raw chunk).
                  if (extraHeaders == null || extraHeaders.IndexOf("Content-Type:", StringComparison.OrdinalIgnoreCase) < 0)
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
